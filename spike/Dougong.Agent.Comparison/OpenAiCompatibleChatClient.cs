using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 面向 OpenAI 兼容 <c>/chat/completions</c> 端点（Xiaomi MiMo、MiniMax 等）的最小
/// <see cref="IChatClient"/> 适配器，仅用于 M1 真实 provider 对比。
/// </summary>
/// <remarks>
/// 这不是 <c>Dougong.Ai</c> 的正式连接器：它只实现对比所需的最小子集
/// （文本、流式文本、工具调用、usage），刻意不做重试、连接池调优、多模态。
/// 关键职责（也是循环不该承担的部分）：把流式 <c>tool_calls[].function.arguments</c>
/// 的字符串片段按 index 拼接到 <c>content_block_stop</c> 等价点，再解析成完整参数。
/// </remarks>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    public OpenAiCompatibleChatClient(
        string baseUrl, string apiKey, string model, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = BuildBody(messages, options, stream: false);
        using var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
        var choice = json["choices"]![0]!;
        var message = choice["message"]!;

        var contents = new List<AIContent>();
        if (message["content"]?.GetValue<string>() is { Length: > 0 } text)
            contents.Add(new TextContent(text));
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                var id = call!["id"]!.GetValue<string>();
                var name = call["function"]!["name"]!.GetValue<string>();
                var args = call["function"]!["arguments"]!.GetValue<string>();
                contents.Add(new FunctionCallContent(id, name, ParseArguments(args)));
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

        // 按 index 累积工具参数片段：同一 index 的 arguments 是字符串拼接，
        // 只有到流结束（或该 index 的 finish）才能解析。
        var pendingCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload == "[DONE]") break;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }

            // 某些端点会发 choices 为空的 usage-only 帧，必须先判长度。
            var choices = chunk?["choices"] as JsonArray;
            if (choices is null || choices.Count == 0) continue;
            var choice = choices[0];
            if (choice is null) continue;
            var delta = choice["delta"];
            if (delta is null) continue;

            if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);

            if (delta["tool_calls"] is JsonArray toolDeltas)
            {
                foreach (var toolDelta in toolDeltas)
                {
                    var index = toolDelta!["index"]?.GetValue<int>() ?? 0;
                    if (!pendingCalls.TryGetValue(index, out var pending))
                        pending = (string.Empty, string.Empty, new StringBuilder());

                    // 只在"有非空值"时覆盖：多数端点首帧给 id/name，后续帧只给 arguments，
                    // 且可能给空字符串而不是 null，不能直接赋值。
                    if (toolDelta["id"]?.GetValue<string>() is { Length: > 0 } id)
                        pending.Id = id;
                    if (toolDelta["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
                        pending.Name = name;
                    if (toolDelta["function"]?["arguments"]?.GetValue<string>() is { } fragment)
                        pending.Args.Append(fragment);
                    pendingCalls[index] = pending;
                }
            }

            // finish_reason 到达后该轮的调用才算完整，可以解析并下发。
            // 注意：不同端点的"未完成"表示法不同——null、空字符串、缺失都出现过。
            // 只认非空字符串为终结，否则 Waygate 这类端点会在每个 delta 上误触发下发。
            var finishValue = choice["finish_reason"];
            var finished = finishValue is not null
                && finishValue.GetValueKind() == JsonValueKind.String
                && finishValue.GetValue<string>() is { Length: > 0 } reason
                && reason != "null";

            if (finished)
            {
                foreach (var pending in pendingCalls.Values)
                {
                    var args = ParseArguments(pending.Args.ToString());
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        [new FunctionCallContent(pending.Id, pending.Name, args)]);
                }

                if (pendingCalls.Count > 0) pendingCalls.Clear();
            }
        }

        // 某些端点不保证 finish_reason 一定出现，兜底再下发一次。
        foreach (var pending in pendingCalls.Values)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(pending.Id, pending.Name,
                    ParseArguments(pending.Args.ToString()))]);
        }
    }

    private JsonObject BuildBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var item = new JsonObject
            {
                ["role"] = message.Role.Value,
                ["content"] = message.Text,
            };
            array.Add(item);
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = array,
            ["stream"] = stream,
        };

        // 工具声明由运行时通过 ChatOptions 附加属性传入，连接器只负责序列化。
        var declarations = options.ReadToolDeclarations();
        if (declarations.Count > 0)
            body["tools"] = ToolDeclarations.ToOpenAiToolsArray(declarations);

        return body;
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(CreateRequest(body), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException(
                $"provider 返回 {(int)response.StatusCode}：{error[..Math.Min(400, error.Length)]}");
        }

        return response;
    }

    /// <summary>把协议给出的 JSON 字符串解析为参数；失败时返回 null 而不是抛错。</summary>
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
            InputTokenCount = usage["prompt_tokens"]?.GetValue<long>() ?? 0,
            OutputTokenCount = usage["completion_tokens"]?.GetValue<long>() ?? 0,
            TotalTokenCount = usage["total_tokens"]?.GetValue<long>() ?? 0,
        };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
