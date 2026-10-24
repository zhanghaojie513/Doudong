using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G1 缺口探针：证明长会话会把完整历史送进模型请求，压缩能力从未生效。
/// </summary>
/// <remarks>
/// 这不是在测"压缩能不能压缩"（`-- compaction` 已覆盖），而是测
/// **压缩有没有接在生产路径上**。两者是不同的事：
/// 前者是算法的性质，后者是这个能力是否真的被调用。
/// <para>
/// 判定依据不看实现细节，只看可观察事实：发给模型的请求里历史有多长。
/// </para>
/// </remarks>
public static class CompactionWiringChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("full-history-reaches-model", FullHistoryReachesModelAsync);
        failures += await CheckAsync("no-budget-guard-in-loop", NoBudgetGuardAsync);
        failures += await CheckAsync("compaction-api-unused-by-loop", CompactionUnusedAsync);
        failures += await CheckAsync("no-limits-keeps-old-behaviour", NoLimitsAsync);

        if (failures > 0)
        {
            Console.WriteLine($"COMPACTION-WIRING: {failures} GAP(S) CONFIRMED");
            return failures;
        }

        // 缺口已填：验证接入后的行为。
        failures += await CheckAsync("compaction-shrinks-request", CompactionShrinksAsync);
        failures += await CheckAsync("compaction-reports-event", CompactionEventAsync);
        failures += await CheckAsync("compaction-keeps-tool-pairs", CompactionKeepsPairsAsync);
        failures += await CheckAsync("summary-failure-keeps-history", SummaryFailureAsync);
        failures += await CheckAsync("no-summarizer-does-not-fake", NoSummarizerAsync);

        Console.WriteLine(failures == 0
            ? "COMPACTION-WIRING: ALL PASS"
            : $"COMPACTION-WIRING: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 缺口证据：100 条历史、每条 200 字符（约 1 万 token 估算），
    /// 而预算设成 2 000 token —— 全文仍然被完整送进模型。
    /// </summary>
    private static async Task<string?> FullHistoryReachesModelAsync()
    {
        var history = new List<AgentMessage>();
        for (var i = 0; i < 100; i++)
            history.Add(new AgentMessage(AgentRole.User, new string('x', 200) + $" #{i}"));

        var client = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "ok")));
        var dispatcher = new ToolDispatcher();
        var runtime = new LoopAgentRuntime(client, dispatcher);

        await foreach (var _ in runtime.RunAsync(
            new AgentRunRequest("s", "t", history, "continue")))
        {
        }

        if (client.ObservedHistory.Count == 0) return "模型没有被调用";

        // 观察实际发送的历史条数。
        var sent = client.ObservedHistory[0].Count;

        // 未配置预算时必须保持旧行为：完整历史送入模型（向后兼容契约）。
        return sent >= 100 ? null : $"未配置 limits 却裁剪了历史：只发 {sent} 条";
    }

    /// <summary>
    /// 缺口证据：`LoopAgentRuntime` 没有预算参数，无法表达"上下文上限"。
    /// </summary>
    private static Task<string?> NoBudgetGuardAsync()
    {
        var ctors = typeof(LoopAgentRuntime).GetConstructors();
        var parameters = ctors.SelectMany(c => c.GetParameters()).ToArray();

        var hasBudget = parameters.Any(p =>
            p.ParameterType == typeof(ContextLimits)
            || p.ParameterType.Name.Contains("Budget", StringComparison.Ordinal)
            || p.ParameterType.Name.Contains("ContextWindow", StringComparison.Ordinal));

        var hasCompactor = parameters.Any(p =>
            typeof(IContextCompactor).IsAssignableFrom(p.ParameterType));

        return Task.FromResult<string?>(
            hasCompactor && hasBudget
                ? null
                : "构造参数缺少预算或压缩器：" + string.Join(", ", parameters.Select(p => p.Name)));
    }

    /// <summary>
    /// 缺口证据：压缩 API 只在测试与自检里被调用，生产路径一处都没有。
    /// </summary>
    private static Task<string?> CompactionUnusedAsync()
    {
        // 用反射确认压缩器的公开入口存在（能力在），
        // 而循环的公开面里没有任何成员接受它（没接进去）。
        var forOneCompactor = typeof(IContextCompactor);
        if (!forOneCompactor.IsInterface) return Task.FromResult<string?>("IContextCompactor 不是接口");

        var runtimeMembers = typeof(LoopAgentRuntime).GetMembers()
            .Select(m => m.ToString() ?? string.Empty)
            .ToArray();
        var referenced = runtimeMembers.Any(m =>
            m.Contains("Compaction", StringComparison.Ordinal)
            || m.Contains("Compactor", StringComparison.Ordinal));

        return Task.FromResult<string?>(
            referenced ? null : "循环公开面仍未引用压缩器");
    }

    // 配置预算与摘要器后，真实发出去的历史必须变短，且带摘要。
    private static async Task<string?> CompactionShrinksAsync()
    {
        var history = BuildLongHistory(60);
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var runtime = new LoopAgentRuntime(
            client, new ToolDispatcher(),
            limits: new ContextLimits { ContextWindowTokens = 4_000, MaxOutputTokens = 500 },
            compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 4),
            summarize: (_, _) => ValueTask.FromResult("SUMMARY"));

        await foreach (var _ in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go"))) { }

        var sent = client.ObservedHistory.Single();
        if (sent.Count >= history.Count)
            return $"历史未被压缩：发送 {sent.Count} 条，原始 {history.Count} 条";

        var hasSummary = sent.Any(m =>
            m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains("SUMMARY", StringComparison.Ordinal));
        return hasSummary ? null : "压缩后未见摘要内容";
    }

    // 压缩必须产生可观察事件，宿主与用户才知道历史被改写了。
    private static async Task<string?> CompactionEventAsync()
    {
        var history = BuildLongHistory(60);
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var runtime = new LoopAgentRuntime(
            client, new ToolDispatcher(),
            limits: new ContextLimits { ContextWindowTokens = 4_000, MaxOutputTokens = 500 },
            compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 4),
            summarize: (_, _) => ValueTask.FromResult("SUMMARY"));

        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go")))
            events.Add(e);

        var compacted = events.OfType<AgentEvent.ContextCompacted>().FirstOrDefault();
        if (compacted is null) return "发生了压缩但没有发出 ContextCompacted 事件";
        if (!compacted.Applied) return $"事件标记为未应用：{compacted.Diagnostic}";
        if (compacted.RemovedMessages <= 0) return "事件未报告被压缩的条数";
        return compacted.EstimatedTokensAfter < compacted.EstimatedTokensBefore
            ? null : "事件里的压缩后估算没有下降";
    }

    // 压缩不得拆散工具调用与它的结果：否则 provider 会拒绝整轮。
    private static async Task<string?> CompactionKeepsPairsAsync()
    {
        var history = new List<AgentMessage> { new(AgentRole.System, "sys") };
        for (var i = 0; i < 20; i++)
        {
            history.Add(new AgentMessage(AgentRole.User, new string('u', 180) + $" #{i}"));
            history.Add(new AgentMessage(AgentRole.Assistant, string.Empty)
            {
                ToolCalls = [new AgentToolCallRef($"c{i}", "read", JsonOne("path", "a.txt"))],
            });
            history.Add(new AgentMessage(AgentRole.Tool, new string('t', 180))
            {
                ToolCallId = $"c{i}",
                ToolId = "read",
            });
        }

        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));
        var runtime = new LoopAgentRuntime(
            client, new ToolDispatcher(),
            limits: new ContextLimits { ContextWindowTokens = 5_000, MaxOutputTokens = 500 },
            compactor: new SlidingWindowCompactor(preservedHead: 1, preservedTail: 3),
            summarize: (_, _) => ValueTask.FromResult("SUMMARY"));

        await foreach (var _ in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go"))) { }

        var sent = client.ObservedHistory.Single();
        var declared = sent
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);

        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in sent.Where(m => m.Role == ChatRole.Tool))
        {
            if (message.AdditionalProperties is { } properties
                && properties.TryGetValue("tool_call_id", out var id)
                && id?.ToString() is { Length: > 0 } value)
                answered.Add(value);
        }

        var dangling = declared.Except(answered).ToList();
        if (dangling.Count > 0)
            return $"压缩拆散了工具配对：{string.Join(",", dangling)} 有调用无结果";

        var orphan = answered.Except(declared).ToList();
        return orphan.Count == 0 ? null : $"有结果无调用：{string.Join(",", orphan)}";
    }

    // 摘要失败必须保留原历史并继续运行，不能中断任务。
    private static async Task<string?> SummaryFailureAsync()
    {
        var history = BuildLongHistory(60);
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var runtime = new LoopAgentRuntime(
            client, new ToolDispatcher(),
            limits: new ContextLimits { ContextWindowTokens = 4_000, MaxOutputTokens = 500 },
            compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 4),
            summarize: (_, _) => throw new InvalidOperationException("summary boom"));

        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go")))
            events.Add(e);

        var finished = events.OfType<AgentEvent.RunFinished>().Single();
        if (finished.Outcome != AgentRunOutcome.Completed)
            return $"摘要失败中断了运行：{finished.Outcome} {finished.Diagnostic}";

        var compacted = events.OfType<AgentEvent.ContextCompacted>().FirstOrDefault();
        if (compacted is null || compacted.Applied) return "摘要失败却报告压缩成功";

        return client.ObservedHistory.Single().Count >= history.Count
            ? null : "摘要失败后历史被裁剪";
    }

    // 没有摘要器时不得假装压缩：如实报告未压缩。
    private static async Task<string?> NoSummarizerAsync()
    {
        var history = BuildLongHistory(60);
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var runtime = new LoopAgentRuntime(
            client, new ToolDispatcher(),
            limits: new ContextLimits { ContextWindowTokens = 4_000, MaxOutputTokens = 500 },
            compactor: new SlidingWindowCompactor(preservedHead: 2, preservedTail: 4));

        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go")))
            events.Add(e);

        var compacted = events.OfType<AgentEvent.ContextCompacted>().FirstOrDefault();
        if (compacted is null) return "超预算但没有报告任何压缩状态";
        if (compacted.Applied) return "没有摘要器却报告压缩成功";
        return client.ObservedHistory.Single().Count >= history.Count
            ? null : "没有摘要器却裁剪了历史";
    }

    // 不传 limits 时完全保持旧行为（向后兼容）。
    private static async Task<string?> NoLimitsAsync()
    {
        var history = BuildLongHistory(20);
        var client = new ScriptedChatClient(new ScriptedTurn(new ScriptedUpdate(Text: "ok")));

        var runtime = new LoopAgentRuntime(client, new ToolDispatcher());
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", history, "go")))
            events.Add(e);

        if (events.OfType<AgentEvent.ContextCompacted>().Any())
            return "未配置 limits 却产生了压缩事件";

        return client.ObservedHistory.Single().Count == history.Count + 1
            ? null : "未配置 limits 时历史条数不符合预期";
    }

    private static List<AgentMessage> BuildLongHistory(int count)
    {
        var history = new List<AgentMessage> { new(AgentRole.System, "sys") };
        for (var i = 0; i < count; i++)
            history.Add(new AgentMessage(AgentRole.User, new string('x', 200) + $" #{i}"));
        return history;
    }

    private static System.Text.Json.JsonElement JsonOne(string key, string value)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { [key] = value }));
        return document.RootElement.Clone();
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var note = await action();
            if (note is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            // 缺口探针与普通断言相反：note 非空表示缺口成立，作为证据输出。
            Console.WriteLine($"GAP  {name}: {note}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
