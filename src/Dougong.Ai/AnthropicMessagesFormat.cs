using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>Anthropic <c>/v1/messages</c> 线格式。</summary>
/// <remarks>
/// 与 chat-completions 的结构差异（因此不能共用实现）：
/// <list type="bullet">
/// <item>system 提示是顶层 <c>system</c> 字段，不能混进 messages</item>
/// <item>工具声明是 <c>tools[].input_schema</c>，不套 <c>function</c> 层</item>
/// <item>工具调用是 content block（<c>tool_use</c>），不是并行数组</item>
/// <item>工具结果回填为 <c>user</c> 消息里的 <c>tool_result</c> block，<b>不是</b>独立的 tool 角色</item>
/// <item>流式参数是 <c>input_json_delta.partial_json</c>，按 <c>index</c> 拼接</item>
/// <item>终结信号只在 <c>message_delta.stop_reason</c>，不是每个 chunk</item>
/// </list>
/// </remarks>
public sealed class AnthropicMessagesFormat : IChatWireFormat
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly string _messagesPath;

    /// <param name="messagesPath">
    /// messages 端点的相对路径。多数实现是 <c>/v1/messages</c>；
    /// DeepSeek 把 Anthropic 兼容层挂在 <c>/anthropic/v1/messages</c>，需显式传入。
    /// </param>
    public AnthropicMessagesFormat(string messagesPath = "/v1/messages")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messagesPath);
        _messagesPath = messagesPath;
    }

    public ChatProtocol Protocol => ChatProtocol.AnthropicMessages;

    public string RelativePath => _messagesPath;

    public void ApplyHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
    }

    public JsonObject BuildBody(
        string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var systemParts = new List<string>();
        var array = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (message.Text is { Length: > 0 } systemText) systemParts.Add(systemText);
                continue;
            }

            if (message.Role == ChatRole.Tool) { AppendToolResult(array, message); continue; }

            var item = new JsonObject
            {
                ["role"] = message.Role == ChatRole.Assistant ? "assistant" : "user",
            };

            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count > 0)
            {
                var blocks = new JsonArray();
                if (message.Text is { Length: > 0 } assistantText)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = assistantText });

                foreach (var call in calls)
                {
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["input"] = ToInputNode(call.Arguments),
                    });
                }

                item["content"] = blocks;
            }
            else
            {
                item["content"] = message.Text ?? string.Empty;
            }

            array.Add(item);
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["messages"] = array,
            ["stream"] = stream,
        };

        if (systemParts.Count > 0)
            body["system"] = string.Join("\n", systemParts);

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

    /// <summary>
    /// 工具结果必须作为 <c>user</c> 消息的 <c>tool_result</c> block 回填。
    /// 早期实现直接套用 OpenAI 的独立 tool 角色，Anthropic 端会拒绝整轮请求。
    /// </summary>
    private static void AppendToolResult(JsonArray array, ChatMessage message)
    {
        var callId = message.AdditionalProperties is { } props
            && props.TryGetValue("tool_call_id", out var id) ? id?.ToString() ?? string.Empty
            : string.Empty;

        var block = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = callId,
            ["content"] = message.Text ?? string.Empty,
        };

        // 相邻的工具结果合并进同一条 user 消息，这是 Anthropic 期望的形状。
        if (array.Count > 0
            && array[^1] is JsonObject last
            && last["role"]?.GetValue<string>() == "user"
            && last["content"] is JsonArray existing
            && existing.Count > 0
            && existing[0]?["type"]?.GetValue<string>() == "tool_result")
        {
            existing.Add(block);
            return;
        }

        array.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(block),
        });
    }

    private static JsonNode ToInputNode(IDictionary<string, object?>? arguments) =>
        arguments is null
            ? new JsonObject()
            : JsonNode.Parse(WireFormatHelpers.SerializeArguments(arguments)) ?? new JsonObject();

    public ChatStreamDecoder CreateDecoder() => new Decoder();

    private sealed class Decoder : ChatStreamDecoder
    {
        // index -> (id, name, 参数片段)；tool_use 块用 index 标识，不是 tool_calls[].index。
        private readonly Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)> _blocks = [];

        public override void Decode(string payload, List<ChatResponseUpdate> updates)
        {
            JsonNode? evt;
            try { evt = JsonNode.Parse(payload); }
            catch (JsonException) { return; }
            if (evt is null) return;

            switch (evt["type"]?.GetValue<string>())
            {
                case "message_start":
                    Usage = ReadUsage(evt["message"]?["usage"]);
                    break;

                case "content_block_start":
                {
                    if (evt["index"] is not { } indexNode) break;
                    var index = indexNode.GetValue<int>();
                    var block = evt["content_block"];
                    if (block?["type"]?.GetValue<string>() == "tool_use")
                    {
                        _blocks[index] = (
                            block["id"]?.GetValue<string>() ?? string.Empty,
                            block["name"]?.GetValue<string>() ?? string.Empty,
                            new System.Text.StringBuilder());
                    }

                    break;
                }

                case "content_block_delta":
                {
                    if (evt["index"] is not { } indexNode) break;
                    var index = indexNode.GetValue<int>();
                    var delta = evt["delta"];
                    switch (delta?["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            if (delta!["text"]?.GetValue<string>() is { Length: > 0 } text)
                                updates.Add(new ChatResponseUpdate(ChatRole.Assistant, text));
                            break;

                        case "input_json_delta":
                            // 参数是原始 JSON 片段，按 index 拼接，到终结才解析。
                            if (_blocks.TryGetValue(index, out var pending)
                                && delta!["partial_json"]?.GetValue<string>() is { } fragment)
                            {
                                pending.Args.Append(fragment);
                                _blocks[index] = pending;
                            }

                            break;
                    }

                    break;
                }

                case "message_delta":
                {
                    if (evt["delta"]?["stop_reason"]?.GetValue<string>() is { } reason
                        && WireFormatHelpers.IsTerminalReason(reason))
                        IsTerminal = true;
                    if (ReadUsage(evt["usage"]) is { } usage) Usage = usage;
                    break;
                }
            }
        }

        public override IReadOnlyList<ChatResponseUpdate> Flush()
        {
            if (_blocks.Count == 0) return [];

            var contents = new List<AIContent>();
            foreach (var block in _blocks.Values)
            {
                contents.Add(new FunctionCallContent(
                    block.Id, block.Name,
                    WireFormatHelpers.ParseArguments(block.Args.ToString())));
            }

            _blocks.Clear();
            return [new ChatResponseUpdate(ChatRole.Assistant, contents)];
        }

        private static UsageDetails? ReadUsage(JsonNode? usage) => usage is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = usage["input_tokens"]?.GetValue<long>() ?? 0,
                OutputTokenCount = usage["output_tokens"]?.GetValue<long>() ?? 0,
            };
    }
}
