using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 对 G4/G6/G8 修复的复检：主动攻击上轮实现，找它没覆盖的情形。
/// </summary>
/// <remarks>
/// 与既有套件的分工：`-- tool-replacement`、`-- schema` 验证"按预期使用时正确"；
/// 这里验证"在竞态与边界形态下是否仍然正确"。
/// <para>
/// 复检的立场：上轮检查全绿不代表修好了，只代表**已想到的场景**都过了。
/// 因此这里专门挑上轮没测的时序与 schema 形态。
/// </para>
/// </remarks>
public static class FixRecheckChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("tool-replaced-between-approval-and-execute", ReplaceDuringApprovalAsync);
        failures += await CheckAsync("schema-type-array-is-not-silently-skipped", TypeArrayAsync);
        failures += await CheckAsync("schema-boolean-exclusive-minimum-is-not-skipped", BooleanExclusiveMinAsync);
        failures += await CheckAsync("estimate-is-monotonic", MonotonicAsync);

        Console.WriteLine(failures == 0
            ? "FIX-RECHECK: ALL PASS"
            : $"FIX-RECHECK: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 攻击：批准过程中工具被替换（MCP 的 list_changed 完全可能在审批对话框打开时到达）。
    /// </summary>
    /// <remarks>
    /// 期望：复核必须发现"批准的版本"与"当前注册的版本"不一致并拒绝，且绝不执行任一版本。
    /// <para>
    /// 时序按 <c>McpToolRegistry.RefreshAsync</c> 的真实行为模拟：先注销旧注册、再注册新的。
    /// 因此审批窗口内存在一个极短的空档——工具暂时不在 dispatcher 里。
    /// 这正是要验证的：空档期间的调用必须失败，而不是回落到旧实例或新实例。
    /// </para>
    /// </remarks>
    private static async Task<string?> ReplaceDuringApprovalAsync()
    {
        var original = new SwapProbeTool("1.0.0-old-schema", requiredCount: 1);
        var replacement = new SwapProbeTool("2.0.0-new-schema", requiredCount: 2);

        ToolDispatcher? dispatcher = null;
        var swapper = new SwappingApprover(() =>
        {
            // 复刻 McpToolRegistry.RefreshAsync 的时序：先注销、再注册。
            originalRegistration!.Dispose();
            dispatcher!.Register(replacement);
        });

        dispatcher = new ToolDispatcher(
            swapper, sessionId: "s1", turnId: "t1", workspace: Path.GetTempPath());
        originalRegistration = dispatcher.Register(original);

        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");
        var result = await dispatcher.DispatchAsync(
            new ToolInvocation("c1", original.Descriptor.Id, arguments.RootElement.Clone()));

        if (result.Outcome == ToolCallOutcome.Succeeded)
            return "审批期间工具被替换，旧审批仍放行并执行——批准了 A、执行了 B";

        // 新旧工具都没执行才算真正拒绝；执行了新工具同样是错（用户没批准过它）。
        if (original.ExecutionCount > 0) return "旧的已替换工具仍被执行";
        if (replacement.ExecutionCount > 0) return "未获批准的新工具被执行";

        return null;
    }

    [ThreadStatic]
    private static IDisposable? originalRegistration;

    /// <summary>
    /// 攻击：<c>"type": ["string","null"]</c>（JSON Schema 合法的联合类型）。
    /// </summary>
    /// <remarks>
    /// 期望：要么正确按联合类型校验，要么明确报"不支持"。
    /// 不允许静默跳过——那正是 G4 里写下的不变量。
    /// </remarks>
    private static async Task<string?> TypeArrayAsync()
    {
        const string schema = """
            {"type":"object","properties":{"name":{"type":["string","null"]}},"required":["name"]}
            """;

        // 数字既不是 string 也不是 null，必须被拒。
        var result = await DispatchAsync(schema, """{"name":42}""");
        if (result.Outcome == ToolCallOutcome.Succeeded)
            return "联合类型 schema 被静默跳过：数字冒充 string|null 被放行";

        return result.Outcome == ToolCallOutcome.RejectedInvalidArguments
            ? null : $"预期 RejectedInvalidArguments，实际 {result.Outcome}";
    }

    /// <summary>
    /// 攻击：<c>"exclusiveMinimum": true</c>（draft-4 布尔形态，与 minimum 搭配）。
    /// </summary>
    /// <remarks>
    /// 期望：不支持的形态必须报错，不能因为读不成数字就悄悄跳过。
    /// </remarks>
    private static async Task<string?> BooleanExclusiveMinAsync()
    {
        const string schema = """
            {"type":"object","properties":{"n":{"type":"number","minimum":5,"exclusiveMinimum":true}},"required":["n"]}
            """;

        var result = await DispatchAsync(schema, """{"n":5}""");
        if (result.Outcome == ToolCallOutcome.Succeeded)
            return "布尔形态的 exclusiveMinimum 被静默跳过：n=5 应被拒（exclusive）却放行";

        return result.Outcome == ToolCallOutcome.RejectedInvalidArguments
            ? null : $"预期 RejectedInvalidArguments，实际 {result.Outcome}";
    }

    /// <summary>估算必须单调：更长的同类文本不能估出更少的 token。</summary>
    private static Task<string?> MonotonicAsync()
    {
        var previous = 0;
        foreach (var length in new[] { 1, 10, 100, 1000, 10000 })
        {
            var estimate = TokenBudget.Estimate(new string('a', length));
            if (estimate < previous)
                return Task.FromResult<string?>($"长度 {length} 的估算 {estimate} 小于更短文本的 {previous}");
            previous = estimate;
        }

        return Task.FromResult<string?>(null);
    }

    private static async Task<ToolDispatchResult> DispatchAsync(string schema, string arguments)
    {
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new SchemaProbeTool(schema));

        using var document = JsonDocument.Parse(arguments);
        return await dispatcher.DispatchAsync(
            new ToolInvocation("c1", "probe", document.RootElement.Clone()));
    }

    private sealed class SwapProbeTool(string version, int requiredCount) : IAgentTool
    {
        private int _executions;

        public int ExecutionCount => Volatile.Read(ref _executions);

        public ToolDescriptor Descriptor { get; } = new(
            "swap", "swap", version, "swap probe",
            requiredCount == 1
                ? JsonDocument.Parse("""{"type":"object","required":["path"]}""").RootElement.Clone()
                : JsonDocument.Parse("""{"type":"object","required":["path","content"]}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, ["a.txt"], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class SchemaProbeTool(string schemaJson) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "probe", "probe", "1.0.0", "schema probe",
            JsonDocument.Parse(schemaJson).RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    /// <summary>批准的同时执行一次替换动作，模拟变更通知落在审批窗口内。</summary>
    private sealed class SwappingApprover(Action swap) : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            swap();
            return ValueTask.FromResult(ToolApprovalDecision.Allow("approved before swap"));
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
