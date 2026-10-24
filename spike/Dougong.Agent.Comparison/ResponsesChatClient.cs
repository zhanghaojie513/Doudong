using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 面向 OpenAI <c>/responses</c> 协议的最小 <see cref="IChatClient"/> 适配器，
/// 用于 M1 真实 provider 对比（DeepSeek 支持三种协议）。
/// </summary>
/// <remarks>
/// 与另外两个适配器同一定位：只实现对比所需子集，不是正式连接器。
/// 与 chat-completions 的结构差异：
/// <list type="bullet">
/// <item>输入是 <c>input</c>（字符串或消息数组），不是 <c>messages</c></item>
/// <item>工具是扁平结构 <c>{type:function, name, parameters}</c>，不套 <c>function</c></item>
/// <item>工具调用在 <c>output[]</c> 里是 <c>type:function_call</c> 条目</item>
/// <item>流式参数是 <c>response.function_call_arguments.delta</c>，按 <c>output_index</c> 聚合</item>
/// <item>终结是 <c>response.completed</c></item>
/// </list>
/// </remarks>
public sealed class ResponsesChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public ResponsesChatClient(
        string baseUrl, string apiKey, string model, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = BuildBody(messages, options, stream: false);
        using var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        var json = JsonNode.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;

        var contents = new List<AIContent>();
        if (json["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                switch (item?["type"]?.GetValue<string>())
                {
                    case "message":
                        if (item["content"] is JsonArray parts)
                            foreach (var part in parts)
                                if (part?["type"]?.GetValue<string>() == "output_text"
                                    && part["text"]?.GetValue<string>() is { Length: > 0 } text)
                                    contents.Add(new TextContent(text));
                        break;

                    case "function_call":
                        contents.Add(new FunctionCallContent(
                            item["call_id"]?.GetValue<string>() ?? string.Empty,
                            item["name"]?.GetValue<string>() ?? string.Empty,
                            ParseArguments(item["arguments"]?.GetValue<string>())));
                        break;
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            Usage = ReadUsage(json["usage"]),
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildBody(messages, options, stream: true);
        using var request = CreateRequest(body);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // output_index -> (call_id, name, 参数片段)；事件类型里的索引是这个，不是 tool_calls[].index。
        var calls = new Dictionary<int, (string CallId, string Name, StringBuilder Args)>();
        var completed = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload is "[DONE]") break;

            JsonNode? evt;
            try { evt = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }
            if (evt is null) continue;

            switch (evt["type"]?.GetValue<string>())
            {
                case "response.output_item.added":
                {
                    if (evt["item"]?["type"]?.GetValue<string>() != "function_call") break;
                    var index = evt["output_index"]?.GetValue<int>() ?? 0;
                    calls[index] = (
                        evt["item"]!["call_id"]?.GetValue<string>() ?? string.Empty,
                        evt["item"]!["name"]?.GetValue<string>() ?? string.Empty,
                        new StringBuilder());
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    var index = evt["output_index"]?.GetValue<int>() ?? 0;
                    if (calls.TryGetValue(index, out var pending)
                        && evt["delta"]?.GetValue<string>() is { } fragment)
                    {
                        pending.Args.Append(fragment);
                        calls[index] = pending;
                    }

                    break;
                }

                case "response.output_text.delta":
                    if (evt["delta"]?.GetValue<string>() is { Length: > 0 } text)
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                    break;

                case "response.completed":
                case "response.incomplete":
                    completed = true;
                    break;

                case "response.failed":
                {
                    var error = evt["response"]?["error"]?["message"]?.GetValue<string>();
                    throw new InvalidOperationException($"provider 报告失败：{error}");
                }
            }

            if (completed) break;
        }

        foreach (var pending in calls.Values)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(pending.CallId, pending.Name,
                    ParseArguments(pending.Args.ToString()))]);
        }
    }

    private JsonObject BuildBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        // /responses 的 input 用 role/content 数组，system 用 instructions。
        var instructions = new List<string>();
        var input = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (message.Text is { Length: > 0 } systemText) instructions.Add(systemText);
                continue;
            }

            input.Add(new JsonObject
            {
                ["role"] = message.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = message.Text,
            });
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["input"] = input,
            ["stream"] = stream,
        };

        if (instructions.Count > 0)
            body["instructions"] = string.Join("\n", instructions);

        // /responses 的工具是扁平结构，不套 function 层。
        var declarations = options.ReadToolDeclarations();
        if (declarations.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in declarations)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ToolDeclarations.ToSchemaNode(tool),
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private async Task<HttpResponseMessage> PostAsync(
        JsonObject body, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(CreateRequest(body), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException(
                $"provider 返回 {(int)response.StatusCode}：{error[..Math.Min(400, error.Length)]}");
        }

        return response;
    }

    private static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var element = JsonDocument.Parse(json).RootElement;
            if (element.ValueKind is not JsonValueKind.Object) return null;
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException) { return null; }
    }

    private static UsageDetails? ReadUsage(JsonNode? usage) => usage is null
        ? null
        : new UsageDetails
        {
            InputTokenCount = usage["input_tokens"]?.GetValue<long>() ?? 0,
            OutputTokenCount = usage["output_tokens"]?.GetValue<long>() ?? 0,
            TotalTokenCount = usage["total_tokens"]?.GetValue<long>() ?? 0,
        };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
