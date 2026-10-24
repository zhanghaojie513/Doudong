using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 面向 Anthropic <c>/v1/messages</c> 协议的最小 <see cref="IChatClient"/> 适配器，
/// 用于 M1 真实 provider 对比（Routin 的 gpt-6.1-sol 走这条协议）。
/// </summary>
/// <remarks>
/// 与 <see cref="OpenAiCompatibleChatClient"/> 同一定位：只实现对比所需的最小子集，
/// 不是 <c>Dougong.Ai</c> 的正式连接器。
/// 关键职责差异（这正是必须分两个适配器的原因）：
/// <list type="bullet">
/// <item>工具声明是 <c>tools[].input_schema</c>，不是 <c>function.parameters</c></item>
/// <item>工具调用是 content block（<c>tool_use</c>），不是 <c>tool_calls</c> 数组</item>
/// <item>流式参数是 <c>input_json_delta.partial_json</c>，按 index 拼接</item>
/// <item>终结信号是 <c>message_delta.stop_reason</c>，不是每个 chunk 的 <c>finish_reason</c></item>
/// </list>
/// </remarks>
public sealed class AnthropicMessagesChatClient : IChatClient
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicMessagesChatClient(
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
        if (json["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                        contents.Add(new TextContent(text));
                        break;
                    case "tool_use":
                        contents.Add(new FunctionCallContent(
                            block["id"]!.GetValue<string>(),
                            block["name"]!.GetValue<string>(),
                            ToArgumentDictionary(block["input"])));
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

        // index -> (id, name, 参数片段)
        var blocks = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        // 已收到 stop_reason 的调用在此刻统一解析下发；Anthropic 的终结信号只在 message_delta。
        var finished = false;

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
                case "content_block_start":
                {
                    var index = evt["index"]!.GetValue<int>();
                    var block = evt["content_block"]!;
                    if (block["type"]?.GetValue<string>() == "tool_use")
                    {
                        blocks[index] = (
                            block["id"]?.GetValue<string>() ?? string.Empty,
                            block["name"]?.GetValue<string>() ?? string.Empty,
                            new StringBuilder());
                    }

                    break;
                }

                case "content_block_delta":
                {
                    var index = evt["index"]!.GetValue<int>();
                    var delta = evt["delta"]!;
                    switch (delta["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            if (delta["text"]?.GetValue<string>() is { Length: > 0 } text)
                                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                            break;

                        case "input_json_delta":
                            // 参数是原始 JSON 片段，按 index 拼接，不到终结不解析。
                            if (blocks.TryGetValue(index, out var pending)
                                && delta["partial_json"]?.GetValue<string>() is { } fragment)
                            {
                                pending.Args.Append(fragment);
                                blocks[index] = pending;
                            }

                            break;
                    }

                    break;
                }

                case "message_delta":
                {
                    // stop_reason 到达，本轮的调用才完整。
                    var reason = evt["delta"]?["stop_reason"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(reason)) break;
                    finished = true;
                    break;
                }
            }

            if (finished) break;
        }

        foreach (var pending in blocks.Values)
        {
            var args = ParseArguments(pending.Args.ToString());
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(pending.Id, pending.Name, args)]);
        }
    }

    private JsonObject BuildBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        // Anthropic 要求 system 独立字段，不能混在 messages 里。
        var systemParts = new List<string>();
        var array = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (message.Text is { Length: > 0 } systemText) systemParts.Add(systemText);
                continue;
            }

            array.Add(new JsonObject
            {
                ["role"] = message.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = message.Text,
            });
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 4096,
            ["messages"] = array,
            ["stream"] = stream,
        };

        if (systemParts.Count > 0)
            body["system"] = string.Join("\n", systemParts);

        // 工具声明来自运行时附加属性，形状是 Anthropic 的 input_schema。
        var declarations = options.ReadToolDeclarations();
        if (declarations.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in declarations)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = ToolDeclarations.ToSchemaNode(tool),
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
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

    private static IDictionary<string, object?>? ToArgumentDictionary(JsonNode? input)
    {
        if (input is not JsonObject obj) return null;
        return obj.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone());
    }

    private static IDictionary<string, object?>? ParseArguments(string json)
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
        };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
