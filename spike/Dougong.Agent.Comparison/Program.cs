using System.Diagnostics;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M1 候选对比入口。用同一套确定性场景驱动两个实现，输出结构化轨迹与通过/失败。
/// 只比较可观察行为，不比较代码风格。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "live")
            return await LiveRun.RunAsync();

        if (args.FirstOrDefault() == "contract")
            return ContractChecks.Run();

        if (args.FirstOrDefault() == "store")
            return await StoreBarrierChecks.RunAsync();

        if (args.FirstOrDefault() == "approval")
            return await ApprovalChecks.RunAsync();

        if (args.FirstOrDefault() == "fileversion")
            return await FileVersionChecks.RunAsync();

        if (args.FirstOrDefault() == "compaction")
            return await CompactionChecks.RunAsync();

        var candidate = args.FirstOrDefault() ?? "b";
        Console.WriteLine($"candidate = {candidate}");

        var results = new List<ScenarioResult>();
        foreach (var scenario in Scenarios.All())
        {
            results.Add(await RunScenarioAsync(candidate, scenario));
        }

        Console.WriteLine();
        Console.WriteLine("name".PadRight(28) + "result".PadRight(8) + "detail");
        foreach (var r in results)
        {
            Console.WriteLine(
                r.Scenario.PadRight(28) +
                (r.Passed ? "PASS" : "FAIL").PadRight(8) +
                r.Detail);
        }

        Console.WriteLine();
        Console.WriteLine($"passed {results.Count(r => r.Passed)}/{results.Count}");
        return results.All(r => r.Passed) ? 0 : 1;
    }

    private static async Task<ScenarioResult> RunScenarioAsync(string candidate, Scenario scenario)
    {
        var dispatcher = new ToolDispatcher();
        var detector = new BypassDetector();
        var tools = scenario.Tools
            .Select(t => new FakeTool(
                t.Descriptor.Id,
                RequiredOf(t),
                detector.Track(t.Descriptor.Id, t.ExecuteAsync),
                ToolAccess.Read))
            .ToArray();

        foreach (var tool in tools) dispatcher.Register(tool);

        var client = new ScriptedChatClient(scenario.Turns);
        IAgentRuntime runtime = candidate == "a"
            ? new MafAgentRuntime(client, dispatcher)
            : new LoopAgentRuntime(client, dispatcher);

        var result = await ContractHarness.RunAsync(
            candidate, runtime, scenario, dispatcher, detector);

        Console.WriteLine($"[{scenario.Name}] modelCalls={client.CallCount} " +
                          $"dispatching={dispatcher.DispatchedInvocations.Count} " +
                          $"{ContractHarness.FormatTrace(result.Events)}");
        var diag = result.Events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Diagnostic;
        if (diag is not null) Console.WriteLine($"    diagnostic: {diag}");
        return result;
    }

    private static string[] RequiredOf(IAgentTool tool)
    {
        var schema = tool.Descriptor.InputSchema;
        if (!schema.TryGetProperty("required", out var required)) return [];
        return [.. required.EnumerateArray().Select(e => e.GetString()!).Where(s => s is not null)];
    }

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>反例集。每个场景对应 docs/design.md §2.1 的一类验收要求。</summary>
public static class Scenarios
{
    public static IEnumerable<Scenario> All()
    {
        yield return FragmentedArguments();
        yield return NestedArguments();
        yield return MultipleTools();
        yield return DuplicateInvocationId();
        yield return MissingInvocationId();
        yield return MalformedJson();
        yield return SchemaMismatch();
        yield return ResponseAborted();
        yield return ToolThrows();
        yield return TextStreaming();
        yield return DetailsNotInHistory();
    }

    // 1. 参数分片：同一调用 ID 的多次更新携带互补参数键，必须合并后才派发。
    //    说明：IChatClient 抽象已把协议的 index 分片合并为字典，这里模拟的是
    //    "同一 ID 出现多次、每次只有部分键"的情形，属于循环必须自己聚合的部分。
    private static Scenario FragmentedArguments() => new(
        "fragmented-arguments",
        "同一调用 ID 的参数分两次到达，必须合并成完整对象后才派发",
        [
            new ScriptedTurn(
                new ScriptedUpdate(Text: "reading "),
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{}"),
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{\"path\":\"a.txt\"}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "应成功执行一次");

    // 1b. 嵌套/数组参数：只有按原文解析才能保持结构，按键合并会把嵌套拍平。
    private static Scenario NestedArguments() => new(
        "nested-arguments",
        "参数含嵌套对象与数组，聚合后必须保持原结构",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "write",
                    ArgumentsJson: "{}"),
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "write",
                    ArgumentsJson: "{\"path\":\"a.txt\",\"lines\":[\"one\",\"two\"],\"meta\":{\"eol\":\"lf\"}}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("write", ["path"])],
        "应成功执行且参数保持嵌套结构");

    // 2. 同批多个工具。
    private static Scenario MultipleTools() => new(
        "multiple-tools",
        "同一响应包含两个调用，都必须有终态",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{\"path\":\"a\"}"),
                new ScriptedUpdate(FunctionCallId: "c2", FunctionName: "read", ArgumentsJson: "{\"path\":\"b\"}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "两个调用都应完成");

    // 3. 重复调用 ID：同一 ID 携带同名参数的不同值，属于冲突而非分片，不得执行。
    private static Scenario DuplicateInvocationId() => new(
        "duplicate-invocation-id",
        "同一调用 ID 对同一参数给出冲突取值，不得执行",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "dup", FunctionName: "read", ArgumentsJson: "{\"path\":\"a\"}"),
                new ScriptedUpdate(FunctionCallId: "dup", FunctionName: "read", ArgumentsJson: "{\"path\":\"b\"}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "不得执行任何调用");

    // 4. 缺失调用 ID。
    private static Scenario MissingInvocationId() => new(
        "missing-invocation-id",
        "调用没有 ID，无法与请求对应，不得执行",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "", FunctionName: "read", ArgumentsJson: "{\"path\":\"a\"}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "不得执行任何调用");

    // 5. 畸形 JSON。
    private static Scenario MalformedJson() => new(
        "malformed-json",
        "参数不是合法 JSON，不得执行",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{\"path\":")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "不得执行");

    // 6. 不满足 schema（缺必需参数）。
    private static Scenario SchemaMismatch() => new(
        "schema-mismatch",
        "缺少 required 参数，不得执行成功",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("read", ["path"])],
        "不得成功执行");

    // 7. 响应中途中断。
    private static Scenario ResponseAborted() => new(
        "response-aborted",
        "模型流中途报错，已收到的调用不得执行",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "read", ArgumentsJson: "{\"path\":\"a\"}", AbortAfter: true)),
        ],
        [new FakeTool("read", ["path"])],
        "不得执行");

    // 8. 工具抛异常。
    private static Scenario ToolThrows() => new(
        "tool-throws",
        "工具执行抛异常，必须记为失败终态而不是成功",
        [
            new ScriptedTurn(
                new ScriptedUpdate(FunctionCallId: "c1", FunctionName: "boom", ArgumentsJson: "{\"path\":\"a\"}")),
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [new FakeTool("boom", ["path"],
            (_, _) => throw new InvalidOperationException("tool exploded"))],
        "终态应为失败");

    // 9. 流式文本不退化。
    private static Scenario TextStreaming() => new(
        "text-streaming",
        "纯文本响应必须产生 TextDelta",
        [
            new ScriptedTurn(
                new ScriptedUpdate(Text: "hel"),
                new ScriptedUpdate(Text: "lo")),
        ],
        [],
        "应有文本增量");

    // 10. Details 不进模型历史（结构约束，由运行时保证）。
    private static Scenario DetailsNotInHistory() => new(
        "details-not-in-history",
        "工具 Details 不应进入模型历史",
        [
            new ScriptedTurn(new ScriptedUpdate(Text: "done")),
        ],
        [],
        "应正常结束");
}
