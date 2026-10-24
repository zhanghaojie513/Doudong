using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 上下文压缩的确定性验证。重点不是"能压"，而是：
/// 失败不覆盖原历史、工具配对不被拆散、检查点可续跑。
/// </summary>
public static class CompactionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("no-plan-under-budget", NoPlanUnderBudget);
        failures += await CheckAsync("plan-under-pressure", PlanUnderPressureAsync);
        failures += await CheckAsync("plan-keeps-tool-pairs", PlanKeepsToolPairsAsync);
        failures += await CheckAsync("summary-failure-does-not-cover-history", FailureKeepsHistoryAsync);
        failures += await CheckAsync("retry-then-succeed", RetryThenSucceedAsync);
        failures += await CheckAsync("completed-checkpoint-applies", ApplyWhenCompletedAsync);
        failures += await CheckAsync("checkpoint-survives-serialisation", CheckpointRoundTrip);
        failures += await CheckAsync("cancellation-preserves-progress", CancellationPreservesAsync);
        failures += await CheckAsync("insufficient-budget-is-reported", InsufficientBudgetAsync);

        Console.WriteLine(failures == 0
            ? "COMPACTION: ALL PASS"
            : $"COMPACTION: {failures} FAILURE(S)");
        return failures;
    }

    private static Task<string?> NoPlanUnderBudget()
    {
        var compactor = new SlidingWindowCompactor();
        var history = new List<AgentMessage>
        {
            new(AgentRole.System, "sys"),
            new(AgentRole.User, "short"),
        };

        var plan = compactor.Plan(new CompactionInput(history, 100_000, 1_000));
        return Task.FromResult<string?>(plan is null ? null : "预算充足时仍给出压缩计划");
    }

    private static Task<string?> PlanUnderPressureAsync()
    {
        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2);
        var history = BuildHistory(40, "some long content to burn tokens ");
        var plan = compactor.Plan(new CompactionInput(history, 200, 20, 8));

        if (plan is null) return Task.FromResult<string?>("超出预算却没有给出计划");
        if (plan.EstimatedTokensAfter >= plan.EstimatedTokensBefore)
            return Task.FromResult<string?>("压缩后估算没有下降");
        return Task.FromResult<string?>(null);
    }

    // 压缩区间边界不能把 Tool 消息与其前的 Assistant 拆开。
    private static Task<string?> PlanKeepsToolPairsAsync()
    {
        var history = new List<AgentMessage>
        {
            new(AgentRole.System, "sys"),
            new(AgentRole.User, "question"),
            new(AgentRole.Assistant, "calling tool"),
            new(AgentRole.Tool, "result-1"),
            new(AgentRole.Assistant, "calling again"),
            new(AgentRole.Tool, "result-2"),
            new(AgentRole.Assistant, "answer"),
            new(AgentRole.User, "next"),
        };

        var compactor = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 1);
        // 窗口调小到必然触发：estimate=32 tokens，需 trigger < 32。
        var plan = compactor.Plan(new CompactionInput(history, 30, 4, 2));
        if (plan is null) return Task.FromResult<string?>("未给出计划");

        // 区间内如果出现 Tool，其前一条必须是同类调用的 Assistant；
        // 更实际的不变量：区间起点不能是 Tool。
        if (history[plan.FirstIndex].Role == AgentRole.Tool)
            return Task.FromResult<string?>("压缩区间从 Tool 消息开始，配对被拆散");

        // 区间终点后若紧跟 Tool，说明结果被留在了区间外。
        if (plan.LastIndex + 1 < history.Count && history[plan.LastIndex + 1].Role == AgentRole.Tool)
            return Task.FromResult<string?>("压缩区间结束后紧跟 Tool，配对被拆散");

        return Task.FromResult<string?>(null);
    }

    // 摘要失败：原历史必须原样保留。
    private static async Task<string?> FailureKeepsHistoryAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) => throw new InvalidOperationException("摘要服务不可用"),
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        if (checkpoint.Phase != CompactionPhase.Failed)
            return $"期望 Failed，实际 {checkpoint.Phase}";

        var applied = CompactionApply.TryApply(history, checkpoint);
        return applied is null ? null : "失败后仍然替换了历史";
    }

    private static async Task<string?> RetryThenSucceedAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var attempts = 0;
        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? throw new InvalidOperationException("第一次失败")
                    : ValueTask.FromResult("summary");
            },
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        if (checkpoint.Phase != CompactionPhase.Completed)
            return $"期望 Completed，实际 {checkpoint.Phase}（{checkpoint.FailureReason}）";
        if (checkpoint.Attempt != 2) return $"重试次数 {checkpoint.Attempt}，期望 2";
        return null;
    }

    private static async Task<string?> ApplyWhenCompletedAsync()
    {
        var history = BuildHistory(30, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 120, 10, 4))!;

        var task = new SummarizingCompactionTask(
            plan, history,
            (_, _) => ValueTask.FromResult("condensed"),
            Render);

        var checkpoint = await AdvanceToTerminalAsync(task);
        var applied = CompactionApply.TryApply(history, checkpoint);
        if (applied is null) return "完成态却未应用";

        if (!applied.Any(m => m.Content.Contains("condensed")))
            return "应用后找不到摘要内容";
        if (applied.Count >= history.Count)
            return $"应用后条数未减少：{history.Count} → {applied.Count}";

        // 头尾必须保留。
        if (!applied.Any(m => m.Content == history[0].Content))
            return "头部记录丢失";
        if (!applied.Any(m => m.Content == history[^1].Content))
            return "尾部记录丢失";
        return null;
    }

    private static Task<string?> CheckpointRoundTrip()
    {
        var checkpoint = new CompactionCheckpoint(
            CompactionPhase.Retry, 2, 3, 9, 2, 12, "summary text", "last error");
        var json = checkpoint.ToJson();

        var phase = json.GetProperty("phase").GetString();
        var attempt = json.GetProperty("attempt").GetInt32();
        var tail = json.GetProperty("tailIndex").GetInt32();

        if (phase != "Retry") return Task.FromResult<string?>($"phase 丢失：{phase}");
        if (attempt != 2) return Task.FromResult<string?>("attempt 丢失");
        if (tail != 12) return Task.FromResult<string?>("tailIndex 丢失");
        return Task.FromResult<string?>(null);
    }

    // 取消不是失败：检查点保留，可以继续。
    private static async Task<string?> CancellationPreservesAsync()
    {
        var history = BuildHistory(20, "content ");
        var plan = new SlidingWindowCompactor(preservedHead: 1, preservedTail: 2)
            .Plan(new CompactionInput(history, 100, 8, 4))!;

        using var cts = new CancellationTokenSource();
        var task = new SummarizingCompactionTask(
            plan, history,
            async (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return "unreachable";
            },
            Render);

        // Select → Summarize
        await task.AdvanceAsync();
        try { await task.AdvanceAsync(cts.Token); }
        catch (OperationCanceledException) { }

        if (task.Checkpoint.Phase == CompactionPhase.Failed)
            return "取消被当成失败";
        if (task.Checkpoint.Phase == CompactionPhase.Completed)
            return "取消却标记为完成";
        return null;
    }

    // 预算不足时必须报错，而不是静默给出负数预算。
    private static Task<string?> InsufficientBudgetAsync()
    {
        try
        {
            TokenBudget.EffectiveInput(contextWindowTokens: 1_000, maxOutputTokens: 900, estimateErrorTokens: 200);
            return Task.FromResult<string?>("预算不足却未报错");
        }
        catch (ArgumentOutOfRangeException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static async Task<CompactionCheckpoint> AdvanceToTerminalAsync(
        SummarizingCompactionTask task, int maxSteps = 6)
    {
        for (var i = 0; i < maxSteps && !task.Checkpoint.IsTerminal; i++)
            await task.AdvanceAsync();
        return task.Checkpoint;
    }

    private static List<AgentMessage> BuildHistory(int count, string body) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new AgentMessage(i % 2 == 0 ? AgentRole.User : AgentRole.Assistant,
                $"{body}{i}"))];

    private static string Render(IReadOnlyList<AgentMessage> messages) =>
        string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));

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
