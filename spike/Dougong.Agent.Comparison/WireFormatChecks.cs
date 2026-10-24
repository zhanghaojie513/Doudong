using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 三种线协议的确定性校验：请求体形状、流解码、多轮回填。
/// </summary>
/// <remarks>
/// 为什么需要这一套：真实 provider 只验证过单轮调用。多轮次回填
/// （assistant 携带调用 → 工具结果回填 → 下一轮请求）是循环正确性的核心，
/// 而三种协议的形状完全不同：Anthropic 要求 <c>tool_result</c> block，
/// /responses 要求 <c>function_call_output</c> 条目。这些结构一旦写错，
/// 只有在第二次模型调用时才会被 provider 拒绝。
/// <para>
/// 这里用录制的帧与假传输做断言，不依赖网络，也不依赖 provider 当天行为。
/// </para>
/// </remarks>
public static class WireFormatChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("chat-completions-declares-tools", ChatDeclaresToolsAsync);
        failures += await CheckAsync("chat-completions-roundtrip-tool-result", ChatRoundtripAsync);
        failures += await CheckAsync("anthropic-tool-result-is-user-block", AnthropicToolResultAsync);
        failures += await CheckAsync("anthropic-terminal-only-on-message-delta", AnthropicTerminalAsync);
        failures += await CheckAsync("responses-function-call-output-entry", ResponsesToolOutputAsync);
        failures += Check("responses-failed-raises", ResponsesFailed);
        failures += Check("fragmented-arguments-are-concatenated", Fragmented);
        failures += Check("empty-choices-frame-is-ignored", EmptyChoices);
        failures += Check("reasoning-content-is-echoed", Reasoning);
        failures += await CheckAsync("runtime-history-carries-tool-result-text", RuntimeToolResultTextAsync);

        Console.WriteLine(failures == 0
            ? "WIRE: ALL PASS"
            : $"WIRE: {failures} FAILURE(S)");
        return failures;
    }

    private static ToolDescriptor Descriptor(string id, string schema = """{"type":"object","required":["path"]}""")
        => new(id, id, "1.0.0", "tool " + id,
            JsonDocument.Parse(schema).RootElement.Clone());

    private static ChatOptions OptionsWith(params ToolDescriptor[] tools)
        => new ChatOptions().Attach(tools);

    // chat-completions：工具必须出现在 tools[].function.parameters。
    private static async Task<string?> ChatDeclaresToolsAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var body = format.BuildBody("m", [new ChatMessage(ChatRole.User, "hi")],
            OptionsWith(Descriptor("read")), stream: true);

        var tools = body["tools"] as JsonArray;
        if (tools is null || tools.Count != 1) return "工具声明缺失";
        var fn = tools[0]!["function"];
        if (fn?["name"]?.GetValue<string>() != "read") return "工具名不符";
        if (fn["parameters"]?["required"] is not JsonArray required
            || required[0]?.GetValue<string>() != "path") return "parameters schema 未透传";
        await Task.CompletedTask;
        return null;
    }

    // chat-completions：工具结果必须是 role=tool 且带 tool_call_id。
    private static async Task<string?> ChatRoundtripAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })])
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_content"] = "thinking",
                },
            },
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c1" },
            },
        };

        var array = format.BuildBody("m", messages, null, true)["messages"] as JsonArray;
        if (array is null || array.Count != 3) return "消息数不符";

        var assistant = array[1]!;
        if (assistant["tool_calls"] is not JsonArray calls || calls.Count != 1)
            return "assistant 未序列化 tool_calls";
        if (calls[0]!["id"]?.GetValue<string>() != "c1") return "tool_calls.id 不符";
        if (assistant["reasoning_content"]?.GetValue<string>() != "thinking")
            return "reasoning_content 未回传";

        var tool = array[2]!;
        if (tool["role"]?.GetValue<string>() != "tool") return "工具结果角色不是 tool";
        if (tool["tool_call_id"]?.GetValue<string>() != "c1") return "工具结果缺 tool_call_id";
        await Task.CompletedTask;
        return null;
    }

    // Anthropic：工具结果必须是 user 消息里的 tool_result block，不能是独立 tool 角色。
    private static async Task<string?> AnthropicToolResultAsync()
    {
        var format = new AnthropicMessagesFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c1" },
            },
        };

        var body = format.BuildBody("m", messages, OptionsWith(Descriptor("read")), true);

        if (body["system"]?.GetValue<string>() != "be brief") return "system 未独立成字段";

        var array = body["messages"] as JsonArray;
        if (array is null) return "messages 缺失";
        // user + assistant + 合并后的 tool_result user 消息
        if (array.Count != 3) return $"消息数不符：{array.Count}";

        foreach (var item in array)
        {
            if (item?["role"]?.GetValue<string>() == "tool")
                return "不得出现独立 tool 角色";
        }

        var assistantBlocks = array[1]!["content"] as JsonArray;
        if (assistantBlocks is null) return "assistant content 不是 block 数组";
        if (assistantBlocks[0]?["type"]?.GetValue<string>() != "tool_use") return "assistant 未用 tool_use block";

        var toolResult = array[2]!;
        if (toolResult["role"]?.GetValue<string>() != "user") return "tool_result 外层角色必须是 user";
        var resultBlocks = toolResult["content"] as JsonArray;
        if (resultBlocks is null || resultBlocks.Count != 1) return "tool_result block 数不符";
        if (resultBlocks[0]?["type"]?.GetValue<string>() != "tool_result") return "不是 tool_result block";
        if (resultBlocks[0]!["tool_use_id"]?.GetValue<string>() != "c1") return "tool_use_id 不符";

        // 相邻的第二个工具结果必须并入同一条 user 消息，而不是新开一条。
        var twoResults = new List<ChatMessage>(messages)
        {
            new(ChatRole.Tool, "second")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "c2" },
            },
        };

        var mergedBody = format.BuildBody("m", twoResults, null, true);
        var merged = mergedBody["messages"] as JsonArray;
        if (merged is null || merged.Count != 3) return "相邻工具结果未合并且同一条 user 消息";
        if ((merged[2]!["content"] as JsonArray)?.Count != 2) return "合并后 block 数应为 2";

        // 工具声明必须是 input_schema，不是 function.parameters。
        var tools = body["tools"] as JsonArray;
        if (tools is null || tools.Count != 1) return "工具声明缺失";
        if (tools[0]!["input_schema"] is null) return "工具声明缺 input_schema";
        if (tools[0]!["function"] is not null) return "工具声明不得套 function 层";

        await Task.CompletedTask;
        return null;
    }

    // Anthropic：只有 message_delta.stop_reason 才是终结；content_block_delta 不是。
    private static async Task<string?> AnthropicTerminalAsync()
    {
        var decoder = new AnthropicMessagesFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"type":"message_start","message":{"usage":{"input_tokens":7}}}""", updates);
        if (decoder.IsTerminal) return "message_start 不应终结";

        decoder.Decode("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""", updates);
        if (decoder.IsTerminal) return "content_block_delta 不应终结";
        if (updates.Count != 1 || updates[0].Text != "hi") return "文本增量未透传";

        decoder.Decode("""{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", updates);
        if (!decoder.IsTerminal) return "message_delta.stop_reason 未标记终结";

        if (decoder.Usage?.InputTokenCount != 7) return "usage 未读取";
        await Task.CompletedTask;
        return null;
    }

    // /responses：工具结果必须是 function_call_output 条目，assistant 调用是 function_call 条目。
    private static async Task<string?> ResponsesToolOutputAsync()
    {
        var format = new OpenAiResponsesFormat();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call_1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, """{"ok":true}""")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["tool_call_id"] = "call_1" },
            },
        };

        var body = format.BuildBody("m", messages, OptionsWith(Descriptor("read")), true);

        if (body["instructions"]?.GetValue<string>() != "be brief") return "system 未走 instructions";
        if (body["messages"] is not null) return "/responses 不得出现 messages 字段";

        var input = body["input"] as JsonArray;
        if (input is null) return "input 缺失";

        var call = input.FirstOrDefault(i => i?["type"]?.GetValue<string>() == "function_call");
        if (call is null) return "assistant 调用未序列化为 function_call 条目";
        if (call["call_id"]?.GetValue<string>() != "call_1") return "function_call.call_id 不符";
        if (call["arguments"] is not JsonValue argsValue
            || !argsValue.ToJsonString().Contains("a.txt", StringComparison.Ordinal))
            return "function_call.arguments 未包含参数";

        var output = input.FirstOrDefault(i => i?["type"]?.GetValue<string>() == "function_call_output");
        if (output is null) return "工具结果未序列化为 function_call_output";
        if (output["call_id"]?.GetValue<string>() != "call_1") return "function_call_output.call_id 不符";

        var tools = body["tools"] as JsonArray;
        if (tools is null || tools[0]?["parameters"] is null) return "工具声明缺 parameters";
        if (tools[0]!["function"] is not null) return "/responses 工具声明不得套 function 层";

        await Task.CompletedTask;
        return null;
    }

    // /responses：response.failed 必须抛错，不能当正常结束。
    private static string? ResponsesFailed()
    {
        var decoder = new OpenAiResponsesFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();
        try
        {
            decoder.Decode(
                """{"type":"response.failed","response":{"error":{"message":"boom"}}}""", updates);
            return "response.failed 未抛错";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("boom", StringComparison.Ordinal) ? null : "错误信息未透传";
        }
    }

    // 参数分片必须按 index 拼接原文，不能按键合并字典。
    private static string? Fragmented()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"write","arguments":"{\"path\":\"a.txt\","}}]}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"lines\":[\"one\",\"two\"]}"}}]}}]}""", updates);
        if (updates.Count != 0) return "未终结就下发了调用";

        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);
        if (!decoder.IsTerminal) return "finish_reason 未标记终结";

        var flushed = decoder.Flush();
        // 终结时未自动下发，由 Flush 负责；两种路径都必须给出完整参数。
        if (flushed.Count != 1) return $"应恰好下发 1 个调用，实际 {flushed.Count}";
        var call = flushed[0].Contents.OfType<FunctionCallContent>().Single();
        if (call.CallId != "c1" || call.Name != "write") return "调用身份不符";
        if (call.Arguments is null) return "参数未解析";
        if (!call.Arguments.TryGetValue("lines", out var lines) || lines is null)
            return "嵌套数组参数丢失（按键合并会拍平结构）";

        return null;
    }

    // 只发用量的帧没有 choices：必须跳过而不是越界。
    private static string? EmptyChoices()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();
        try
        {
            decoder.Decode("""{"usage":{"prompt_tokens":3,"completion_tokens":1}}""", updates);
            decoder.Decode("""{"choices":[]}""", updates);
        }
        catch (Exception ex)
        {
            return $"空 choices 帧抛错：{ex.GetType().Name}";
        }

        if (updates.Count != 0) return "空帧不应产生更新";
        if (decoder.Usage?.InputTokenCount != 3) return "usage 未读取";
        return null;
    }

    // 思考模式：reasoning_content 必须随 assistant 更新带出，否则下一轮被拒。
    private static string? Reasoning()
    {
        var decoder = new OpenAiChatCompletionsFormat().CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"reasoning_content":"think "}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"reasoning_content":"more"}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{\"path\":\"a\"}"}}]}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", updates);

        var flushed = decoder.Flush();
        if (flushed.Count != 1) return $"应下发 1 个更新，实际 {flushed.Count}";
        var props = flushed[0].AdditionalProperties;
        if (props is null || !props.TryGetValue("reasoning_content", out var value))
            return "reasoning_content 未随调用带出";
        return value?.ToString() == "think more" ? null : $"思考内容不符：{value}";
    }

    // 工具结果回填后，协议层必须能读到内容。
    // 这条针对一个真实缺陷：ChatMessage.Text 由 Contents 派生，
    // 只放 FunctionResultContent 会让 Text 变空，三种协议都拿到空字符串，
    // 模型因此说"工具没有返回任何东西"。
    private static async Task<string?> RuntimeToolResultTextAsync()
    {
        var tool = new EchoTool();
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var client = new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "echo",
                    ArgumentsJson: """{"text":"payload-42"}""")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")));

        var runtime = new LoopAgentRuntime(client, dispatcher);
        await foreach (var _ in runtime.RunAsync(new AgentRunRequest("s", "t", [], "go"))) { }

        // 第二次模型调用看到的历史里，tool 消息必须带内容。
        if (client.ObservedHistory.Count < 2) return "模型只被调用了一次";
        var toolMessage = client.ObservedHistory[1].LastOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is null) return "历史里没有 tool 消息";
        if (toolMessage.Text is not { Length: > 0 } text) return "tool 消息 Text 为空";
        return text.Contains("payload-42", StringComparison.Ordinal)
            ? null
            : $"tool 消息内容不符：{text}";
    }

    private sealed class EchoTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "echo", "echo", "1.0.0", "echo",
            JsonDocument.Parse("""{"type":"object","required":["text"]}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var text = invocation.Arguments.TryGetProperty("text", out var value)
                ? value.GetString() ?? string.Empty : string.Empty;
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(new { echoed = text }));
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
