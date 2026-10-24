using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Cli;

/// <summary>
/// 最小 OpenAI 兼容连接器。CLI 的自带实现，用于在没有 <c>Dougong.Ai</c> 连接器工厂时
/// 也能跑通一次真实调用；正式连接器属于 Ai 层后续工作。
/// </summary>
/// <remarks>
/// 只支持 chat-completions 协议的最小子集：文本、流式文本、工具调用。
/// 关键职责是拼接流式参数片段（按 index），这是连接器的责任，不是循环的。
/// </remarks>
public sealed class OpenAiChatClient(
    string baseUrl, string apiKey, string model, HttpClient? http = null) : IChatClient
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("CLI 只使用流式接口。");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildBody(messages, options);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(
                body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"provider 返回 {(int)response.StatusCode}：{error[..Math.Min(300, error.Length)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        var pending = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        var reasoningBuffer = new System.Text.StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload is "[DONE]") break;

            JsonDocument document;
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind is not JsonValueKind.Array
                    || choices.GetArrayLength() == 0) continue;

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind is JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text);

                // DeepSeek 思考模式要求下一轮回传 reasoning_content，否则带工具的请求会被拒。
                // 这里先暂存，随本轮的 assistant 更新一起带出。
                if (delta.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind is JsonValueKind.String
                    && reasoning.GetString() is { Length: > 0 } thinking)
                    reasoningBuffer.Append(thinking);

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind is JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var index = toolCall.TryGetProperty("index", out var indexElement)
                            ? indexElement.GetInt32() : 0;
                        if (!pending.TryGetValue(index, out var entry))
                            entry = (string.Empty, string.Empty, new System.Text.StringBuilder());

                        // 只在非空时覆盖：多数端点首帧给 id/name，后续帧给空字符串。
                        if (toolCall.TryGetProperty("id", out var idElement)
                            && idElement.GetString() is { Length: > 0 } id)
                            entry.Id = id;

                        if (toolCall.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var nameElement)
                                && nameElement.GetString() is { Length: > 0 } name)
                                entry.Name = name;

                            if (function.TryGetProperty("arguments", out var argsElement)
                                && argsElement.GetString() is { } fragment)
                                entry.Args.Append(fragment);
                        }

                        pending[index] = entry;
                    }
                }

                if (delta.TryGetProperty("finish_reason", out var finishElement)
                    && finishElement.ValueKind is JsonValueKind.String
                    && finishElement.GetString() is { Length: > 0 } finish
                    && finish is not "null")
                {
                    var contents = new List<AIContent>();
                    foreach (var entry in pending.Values)
                        contents.Add(new FunctionCallContent(
                            entry.Id, entry.Name, Parse(entry.Args.ToString())));

                    var update = new ChatResponseUpdate(ChatRole.Assistant, contents);
                    if (reasoningBuffer.Length > 0)
                    {
                        update.AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["reasoning_content"] = reasoningBuffer.ToString(),
                        };
                        reasoningBuffer.Clear();
                    }

                    yield return update;
                    pending.Clear();
                }
            }
        }

        // 兜底：某些端点不发 finish_reason，或在最后一帧才发。
        // reasoning 必须在这里同样透传，否则带工具的回填会缺 reasoning_content。
        foreach (var entry in pending.Values)
        {
            var update = new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(entry.Id, entry.Name, Parse(entry.Args.ToString()))]);
            if (reasoningBuffer.Length > 0)
            {
                update.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_content"] = reasoningBuffer.ToString(),
                };
                reasoningBuffer.Clear();
            }

            yield return update;
        }
    }

    private System.Text.Json.Nodes.JsonObject BuildBody(
        IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var message in messages)
        {
            var item = new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = message.Role.Value,
            };

            if (message.Role == ChatRole.Tool)
            {
                // 协议要求工具结果必须带 tool_call_id，且 content 为字符串。
                item["tool_call_id"] = message.AdditionalProperties is { } props
                    && props.TryGetValue("tool_call_id", out var id) ? id?.ToString() : string.Empty;
                item["content"] = message.Text ?? string.Empty;
                array.Add(item);
                continue;
            }

            item["content"] = message.Text ?? string.Empty;

            // DeepSeek 思考模式：带工具调用的 assistant 消息必须回传 reasoning_content。
            if (message.AdditionalProperties is { } assistantProps
                && assistantProps.TryGetValue("reasoning_content", out var reasoningValue)
                && reasoningValue?.ToString() is { Length: > 0 } reasoningText)
                item["reasoning_content"] = reasoningText;

            // assistant 消息若携带函数调用，必须序列化为 tool_calls 数组，
            // 否则下一轮的 tool 消息找不到归属（provider 会 422）。
            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count > 0)
            {
                var toolCalls = new System.Text.Json.Nodes.JsonArray();
                foreach (var call in calls)
                {
                    toolCalls.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments is null
                                ? "{}"
                                : JsonSerializer.Serialize(call.Arguments),
                        },
                    });
                }

                item["tool_calls"] = toolCalls;
            }

            array.Add(item);
        }

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["messages"] = array,
            ["stream"] = true,
        };

        var declarations = options.ReadToolDeclarations();
        if (declarations.Count > 0)
            body["tools"] = ToolDeclarations.ToOpenAiToolsArray(declarations);

        return body;
    }

    private static IDictionary<string, object?>? Parse(string json)
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

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
