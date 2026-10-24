using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>OpenAI <c>/responses</c> 线格式。</summary>
/// <remarks>
/// 与 chat-completions 的结构差异：
/// <list type="bullet">
/// <item>输入是 <c>input</c> 条目数组，不是 <c>messages</c>；system 走 <c>instructions</c></item>
/// <item>工具是扁平结构 <c>{type:function, name, parameters}</c>，不套 <c>function</c> 层</item>
/// <item>工具调用在 <c>output[]</c> 里是 <c>type:function_call</c> 条目，结果是 <c>function_call_output</c> 条目</item>
/// <item>流式参数是 <c>response.function_call_arguments.delta</c>，按 <c>output_index</c> 聚合</item>
/// <item>终结是 <c>response.completed</c> / <c>response.incomplete</c>；<c>response.failed</c> 是错误</item>
/// </list>
/// </remarks>
public sealed class OpenAiResponsesFormat : IChatWireFormat
{
    public ChatProtocol Protocol => ChatProtocol.OpenAiResponses;

    public string RelativePath => "/responses";

    public void ApplyHeaders(HttpRequestMessage request, string apiKey) =>
        WireFormatHelpers.ApplyBearer(request, apiKey);

    public JsonObject BuildBody(
        string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var instructions = new List<string>();
        var input = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (message.Text is { Length: > 0 } systemText) instructions.Add(systemText);
                continue;
            }

            if (message.Role == ChatRole.Tool) { AppendToolOutput(input, message); continue; }

            // assistant 消息若带工具调用，要展开成 output 条目里的 function_call：
            // /responses 不接受 chat-completions 的 tool_calls 形状。
            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count > 0)
            {
                if (message.Text is { Length: > 0 } assistantText)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = assistantText,
                        }),
                    });
                }

                foreach (var call in calls)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = WireFormatHelpers.SerializeArguments(call.Arguments),
                    });
                }

                continue;
            }

            input.Add(new JsonObject
            {
                ["role"] = message.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = message.Text ?? string.Empty,
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
            ["stream"] = stream,
        };

        if (instructions.Count > 0)
            body["instructions"] = string.Join("\n", instructions);

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

    /// <summary>工具结果是 input 里的 <c>function_call_output</c> 条目，按 <c>call_id</c> 关联。</summary>
    private static void AppendToolOutput(JsonArray input, ChatMessage message)
    {
        var callId = message.AdditionalProperties is { } props
            && props.TryGetValue("tool_call_id", out var id) ? id?.ToString() ?? string.Empty
            : string.Empty;

        input.Add(new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = message.Text ?? string.Empty,
        });
    }

    public ChatStreamDecoder CreateDecoder() => new Decoder();

    private sealed class Decoder : ChatStreamDecoder
    {
        // output_index -> (call_id, name, 参数片段)
        private readonly Dictionary<int, (string CallId, string Name, System.Text.StringBuilder Args)> _calls = [];

        public override void Decode(string payload, List<ChatResponseUpdate> updates)
        {
            JsonNode? evt;
            try { evt = JsonNode.Parse(payload); }
            catch (JsonException) { return; }
            if (evt is null) return;

            switch (evt["type"]?.GetValue<string>())
            {
                case "response.output_item.added":
                {
                    if (evt["item"]?["type"]?.GetValue<string>() != "function_call") break;
                    var index = evt["output_index"]?.GetValue<int>() ?? 0;
                    _calls[index] = (
                        evt["item"]!["call_id"]?.GetValue<string>() ?? string.Empty,
                        evt["item"]!["name"]?.GetValue<string>() ?? string.Empty,
                        new System.Text.StringBuilder());
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    var index = evt["output_index"]?.GetValue<int>() ?? 0;
                    if (_calls.TryGetValue(index, out var pending)
                        && evt["delta"]?.GetValue<string>() is { } fragment)
                    {
                        pending.Args.Append(fragment);
                        _calls[index] = pending;
                    }

                    break;
                }

                case "response.output_text.delta":
                    if (evt["delta"]?.GetValue<string>() is { Length: > 0 } text)
                        updates.Add(new ChatResponseUpdate(ChatRole.Assistant, text));
                    break;

                case "response.completed":
                case "response.incomplete":
                    Usage = ReadUsage(evt["response"]?["usage"]);
                    IsTerminal = true;
                    break;

                case "response.failed":
                {
                    // 协议层的失败必须让调用方看见，不能当成正常结束。
                    var error = evt["response"]?["error"]?["message"]?.GetValue<string>();
                    throw new InvalidOperationException($"provider 报告失败：{error}");
                }
            }
        }

        public override IReadOnlyList<ChatResponseUpdate> Flush()
        {
            if (_calls.Count == 0) return [];

            var contents = new List<AIContent>();
            foreach (var call in _calls.Values)
            {
                contents.Add(new FunctionCallContent(
                    call.CallId, call.Name,
                    WireFormatHelpers.ParseArguments(call.Args.ToString())));
            }

            _calls.Clear();
            return [new ChatResponseUpdate(ChatRole.Assistant, contents)];
        }

        private static UsageDetails? ReadUsage(JsonNode? usage) => usage is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = usage["input_tokens"]?.GetValue<long>() ?? 0,
                OutputTokenCount = usage["output_tokens"]?.GetValue<long>() ?? 0,
                TotalTokenCount = usage["total_tokens"]?.GetValue<long>() ?? 0,
            };
    }
}
