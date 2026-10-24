using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>OpenAI <c>/chat/completions</c> 线格式。</summary>
/// <remarks>
/// 结构要点：
/// <list type="bullet">
/// <item>工具声明是 <c>tools[].function.{name,description,parameters}</c></item>
/// <item>工具调用是 assistant 消息的 <c>tool_calls</c> 数组，结果回填为 <c>role:"tool"</c> 加 <c>tool_call_id</c></item>
/// <item>流式参数是 <c>tool_calls[].function.arguments</c> 的字符串片段，按 <c>index</c> 拼接</item>
/// <item>终结信号是每个 chunk 的 <c>finish_reason</c>，且"未完成"有 null、空串、缺失三种表示</item>
/// </list>
/// </remarks>
public sealed class OpenAiChatCompletionsFormat : IChatWireFormat
{
    public ChatProtocol Protocol => ChatProtocol.OpenAiChatCompletions;

    public string RelativePath => "/chat/completions";

    public void ApplyHeaders(HttpRequestMessage request, string apiKey) =>
        WireFormatHelpers.ApplyBearer(request, apiKey);

    public JsonObject BuildBody(
        string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(ToWireMessage(message));
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = array,
            ["stream"] = stream,
        };

        var declarations = options.ReadToolDeclarations();
        if (declarations.Count > 0)
        {
            body["tools"] = ToolDeclarations.ToOpenAiToolsArray(declarations);
        }

        return body;
    }

    private static JsonObject ToWireMessage(ChatMessage message)
    {
        var item = new JsonObject { ["role"] = message.Role.Value };

        if (message.Role == ChatRole.Tool)
        {
            // 协议要求工具结果必须带 tool_call_id 且 content 为字符串；
            // 缺了它下一轮请求会被拒（实测 422）。
            item["tool_call_id"] = message.AdditionalProperties is { } toolProps
                && toolProps.TryGetValue("tool_call_id", out var id) ? id?.ToString() : string.Empty;
            item["content"] = message.Text ?? string.Empty;
            return item;
        }

        item["content"] = message.Text ?? string.Empty;

        // 思考模式下带工具调用的 assistant 消息必须回传 reasoning_content，否则被拒（实测 400）。
        if (message.AdditionalProperties is { } props
            && props.TryGetValue("reasoning_content", out var reasoning)
            && reasoning?.ToString() is { Length: > 0 } reasoningText)
        {
            item["reasoning_content"] = reasoningText;
        }

        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (calls.Count > 0)
        {
            var toolCalls = new JsonArray();
            foreach (var call in calls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = WireFormatHelpers.SerializeArguments(call.Arguments),
                    },
                });
            }

            item["tool_calls"] = toolCalls;
        }

        return item;
    }

    public ChatStreamDecoder CreateDecoder() => new Decoder();

    private sealed class Decoder : ChatStreamDecoder
    {
        // index -> (id, name, 参数片段)
        private readonly Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)> _pending = [];
        private readonly System.Text.StringBuilder _reasoning = new();

        public override void Decode(string payload, List<ChatResponseUpdate> updates)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException) { return; }

            using (document)
            {
                var root = document.RootElement;

                if (root.TryGetProperty("usage", out var usage)
                    && usage.ValueKind is JsonValueKind.Object)
                {
                    Usage = new UsageDetails
                    {
                        InputTokenCount = ReadLong(usage, "prompt_tokens"),
                        OutputTokenCount = ReadLong(usage, "completion_tokens"),
                        TotalTokenCount = ReadLong(usage, "total_tokens"),
                    };
                }

                // 只发用量的帧没有 choices，必须先判长度再取 [0]（真实端点会发）。
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind is not JsonValueKind.Array
                    || choices.GetArrayLength() == 0) return;

                var choice = choices[0];

                // finish_reason 是 choices[0] 的兄弟字段，不是 delta 的字段。
                // 早期实现从 delta 里读，主路径从未命中，全靠流结束时的兜底掩盖。
                if (choice.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind is JsonValueKind.String
                    && WireFormatHelpers.IsTerminalReason(finish.GetString()))
                {
                    IsTerminal = true;
                }

                // 终结帧的 delta 可能是 {}、null 或整个缺失，不能因此丢掉终结信号。
                if (!choice.TryGetProperty("delta", out var delta)
                    || delta.ValueKind is not JsonValueKind.Object) return;

                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind is JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                {
                    updates.Add(new ChatResponseUpdate(ChatRole.Assistant, text));
                }

                if (delta.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind is JsonValueKind.String
                    && reasoning.GetString() is { Length: > 0 } thinking)
                {
                    _reasoning.Append(thinking);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind is JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var index = toolCall.TryGetProperty("index", out var indexElement)
                            && indexElement.ValueKind is JsonValueKind.Number
                            ? indexElement.GetInt32() : 0;

                        if (!_pending.TryGetValue(index, out var entry))
                            entry = (string.Empty, string.Empty, new System.Text.StringBuilder());

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

                        _pending[index] = entry;
                    }
                }
            }
        }

        public override IReadOnlyList<ChatResponseUpdate> Flush()
        {
            if (_pending.Count == 0)
            {
                if (_reasoning.Length == 0) return [];
                // 只有思考内容时也要带出，否则下一轮缺 reasoning_content。
                return [WithReasoning(new ChatResponseUpdate(ChatRole.Assistant, string.Empty))];
            }

            var contents = new List<AIContent>();
            foreach (var entry in _pending.Values)
            {
                contents.Add(new FunctionCallContent(
                    entry.Id, entry.Name,
                    WireFormatHelpers.ParseArguments(entry.Args.ToString())));
            }

            _pending.Clear();
            var update = new ChatResponseUpdate(ChatRole.Assistant, contents);
            return [WithReasoning(update)];
        }

        private ChatResponseUpdate WithReasoning(ChatResponseUpdate update)
        {
            if (_reasoning.Length == 0) return update;
            update.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_content"] = _reasoning.ToString(),
            };
            _reasoning.Clear();
            return update;
        }

        private static long? ReadLong(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number
                ? value.GetInt64() : null;
    }
}
