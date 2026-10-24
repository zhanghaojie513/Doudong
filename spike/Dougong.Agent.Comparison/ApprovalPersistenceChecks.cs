using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G2 缺口探针：审批决定从未被持久化，恢复后无法回答"这个调用当初经过审批了吗"。
/// </summary>
/// <remarks>
/// 判定不看实现细节，只看可观察事实：一次需要审批的调用跑完后，
/// 会话日志里有没有任何一条记录能说明授权情况。
/// </remarks>
public static class ApprovalPersistenceChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("approval-decision-is-persisted", PersistedAsync);
        failures += await CheckAsync("denied-call-is-recorded", DenialRecordedAsync);
        failures += await CheckAsync("not-approved-call-has-no-start", NoStartOnDenialAsync);
        failures += await CheckAsync("approval-records-are-recoverable", RecoverableAsync);
        failures += await CheckAsync("approval-failure-stops-execution", CommitFailureStopsAsync);
        failures += await CheckAsync("run-finished-is-persisted", RunFinishedAsync);
        failures += await CheckAsync("unaccounted-approval-is-visible", UnaccountedAsync);

        Console.WriteLine(failures == 0
            ? "APPROVAL-PERSISTENCE: ALL PASS"
            : $"APPROVAL-PERSISTENCE: {failures} FAILURE(S)");
        return failures;
    }

    // 审批通过后，日志里必须有 ApprovalDecision 记录，且能看出批准了什么。
    private static async Task<string?> PersistedAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var tool = new FileTool();
            var store = await JsonlAgentStore.OpenAsync(path, "s1");
            await using (store)
            {
                var dispatcher = new ToolDispatcher(
                    new FixedApprover(ToolApprovalDecision.Allow("approved in test")),
                    sessionId: "s1", turnId: "turn-1", workspace: root,
                    pathGuard: new WorkspacePathGuard(root),
                    store: store);

                dispatcher.Register(tool);
                var result = await dispatcher.DispatchAsync(Invocation("c1"));
                if (result.Outcome != ToolCallOutcome.Succeeded)
                    return $"调用未成功：{result.Outcome}（{result.Diagnostic}）";
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var records = await ReadAllAsync(reopened);
            var decisions = records.Where(r => r.Kind == StoreRecordKind.ApprovalDecision).ToList();

            if (decisions.Count == 0) return "日志里没有 ApprovalDecision 记录";

            var payload = decisions[0].Payload;
            if (payload.ValueKind is not JsonValueKind.Object)
                return $"审批记录不是结构化对象：{payload.ValueKind}";

            var approved = payload.TryGetProperty("approved", out var a) && a.ValueKind is JsonValueKind.True;
            if (!approved) return "审批记录没有标记为已批准";

            if (!payload.TryGetProperty("invocationId", out var id) || id.GetString() != "c1")
                return "审批记录缺少 invocationId 或值不符";

            if (!payload.TryGetProperty("toolId", out var toolId) || toolId.GetString() != "write_file")
                return "审批记录缺少 toolId";

            if (!payload.TryGetProperty("argumentsDigest", out var digest)
                || digest.GetString() is not { Length: > 0 })
                return "审批记录缺少参数摘要，无法证明批准的是哪一次调用";

            return null;
        }
        finally { TryDelete(root); }
    }

    // 被拒绝的调用同样要有记录——否则恢复时看不出"用户拒绝了"还是"根本没请求过"。
    private static async Task<string?> DenialRecordedAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var store = await JsonlAgentStore.OpenAsync(path, "s1");
            await using (store)
            {
                var dispatcher = new ToolDispatcher(
                    new FixedApprover(ToolApprovalDecision.Deny("user said no")),
                    sessionId: "s1", turnId: "turn-1", workspace: root,
                    pathGuard: new WorkspacePathGuard(root),
                    store: store);

                dispatcher.Register(new FileTool());
                var result = await dispatcher.DispatchAsync(Invocation("c1"));
                if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
                    return $"预期被拒绝，实际 {result.Outcome}";
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var records = await ReadAllAsync(reopened);
            var decisions = records.Where(r => r.Kind == StoreRecordKind.ApprovalDecision).ToList();

            if (decisions.Count == 0) return "拒绝没有被记录";

            var payload = decisions[0].Payload;
            var approved = payload.TryGetProperty("approved", out var a) && a.ValueKind is JsonValueKind.True;
            if (approved) return "拒绝被记成了批准";

            return payload.TryGetProperty("reason", out var reason)
                && reason.GetString() is { Length: > 0 }
                ? null : "拒绝记录缺少原因";
        }
        finally { TryDelete(root); }
    }

    // 被拒绝的调用不得留下"调用开始"记录：那会让恢复逻辑误判副作用可能已发生。
    private static async Task<string?> NoStartOnDenialAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var store = await JsonlAgentStore.OpenAsync(path, "s1");
            await using (store)
            {
                var dispatcher = new ToolDispatcher(
                    new FixedApprover(ToolApprovalDecision.Deny("no")),
                    sessionId: "s1", turnId: "turn-1", workspace: root,
                    pathGuard: new WorkspacePathGuard(root),
                    store: store);

                dispatcher.Register(new FileTool());
                await dispatcher.DispatchAsync(Invocation("c1"));
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var records = await ReadAllAsync(reopened);

            // dispatcher 只记审批，不记 Started/Completed——那两个屏障属于循环。
            // 因此这里必须为空，否则说明职责被混淆了。
            var started = records.Count(r => r.Kind == StoreRecordKind.ToolCallStarted);
            return started == 0 ? null : $"被拒绝的调用留下了 {started} 条 Started 记录";
        }
        finally { TryDelete(root); }
    }

    // 恢复时必须能从日志重建"哪些调用被批准过"。
    private static async Task<string?> RecoverableAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var store = await JsonlAgentStore.OpenAsync(path, "s1");
            await using (store)
            {
                var dispatcher = new ToolDispatcher(
                    new AlwaysAllow(),
                    sessionId: "s1", turnId: "turn-1", workspace: root,
                    pathGuard: new WorkspacePathGuard(root),
                    store: store);

                dispatcher.Register(new FileTool());
                await dispatcher.DispatchAsync(Invocation("c1"));
                await dispatcher.DispatchAsync(Invocation("c2"));
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var approvals = await ApprovalLog.ReadAsync(reopened);

            if (approvals.Count != 2)
                return $"应恢复 2 条审批记录，实际 {approvals.Count}";

            var ids = approvals.Select(a => a.InvocationId).OrderBy(x => x).ToArray();
            return ids.SequenceEqual(["c1", "c2"])
                ? null : $"恢复出的调用 ID 不符：{string.Join(",", ids)}";
        }
        finally { TryDelete(root); }
    }

    // 审批记录提交失败必须阻止执行——否则"已审批"这件事没有被持久化，副作用却发生了。
    private static async Task<string?> CommitFailureStopsAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new FileTool();
            var store = new FailingStore(StoreRecordKind.ApprovalDecision);
            var dispatcher = new ToolDispatcher(
                new AlwaysAllow(),
                sessionId: "s1", turnId: "turn-1", workspace: root,
                pathGuard: new WorkspacePathGuard(root),
                store: store);

            dispatcher.Register(tool);
            var result = await dispatcher.DispatchAsync(Invocation("c1"));

            if (tool.ExecutionCount > 0)
                return "审批记录提交失败后仍执行了工具";

            return result.Outcome is ToolCallOutcome.Failed ? null
                : $"审批记录提交失败应报失败，实际 {result.Outcome}";
        }
        finally { TryDelete(root); }
    }

    // G3：运行终态也要落盘，否则恢复时看不出这个 turn 是否正常结束。
    private static async Task<string?> RunFinishedAsync()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "s.jsonl");
            var store = await JsonlAgentStore.OpenAsync(path, "s1");
            await using (store)
            {
                var client = new OneTurnClient();
                var runtime = new LoopAgentRuntime(
                    client, new ToolDispatcher(), store);

                await foreach (var _ in runtime.RunAsync(
                    new AgentRunRequest("s1", "turn-1", [], "hello")))
                {
                }
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var records = await ReadAllAsync(reopened);
            var finished = records.Where(r => r.Kind == StoreRecordKind.RunFinished).ToList();

            if (finished.Count == 0) return "运行结束后没有 RunFinished 记录";

            var payload = finished[0].Payload;
            if (payload.ValueKind is not JsonValueKind.Object)
                return $"RunFinished 不是结构化对象：{payload.ValueKind}";

            return payload.TryGetProperty("outcome", out var outcome)
                && outcome.GetString() == "Completed"
                ? null : "RunFinished 记录的 outcome 不符";
        }
        finally { TryDelete(root); }
    }

    // 恢复时"有调用但无审批记录"必须可表达——不能与"被拒绝"混为一谈。
    private static Task<string?> UnaccountedAsync()
    {
        var records = new List<ApprovalRecord>
        {
            new("c1", "write_file", "1.0.0", true, "ok", "digest1", "/ws", DateTimeOffset.UtcNow),
            new("c2", "write_file", "1.0.0", false, "no", "digest2", "/ws", DateTimeOffset.UtcNow),
        };

        var snapshot = ApprovalLog.Summarize(records);

        if (!snapshot.WasApproved("c1")) return Task.FromResult<string?>("c1 应被识别为已批准");
        if (!snapshot.WasDenied("c2")) return Task.FromResult<string?>("c2 应被识别为已拒绝");
        if (!snapshot.IsUnaccounted("c3")) return Task.FromResult<string?>("c3 无记录却被当成已授权");
        if (snapshot.HasAnyRecord("c3")) return Task.FromResult<string?>("c3 不应有任何记录");

        return Task.FromResult<string?>(null);
    }

    // ── 辅助 ─────────────────────────────────────────────────────

    private static ToolInvocation Invocation(string id)
    {
        using var document = JsonDocument.Parse("""{"path":"a.txt","content":"x"}""");
        return new ToolInvocation(id, "write_file", document.RootElement.Clone());
    }

    private static async Task<List<StoreRecord>> ReadAllAsync(IAgentStore store)
    {
        var records = new List<StoreRecord>();
        await foreach (var record in store.ReadCommittedAsync()) records.Add(record);
        return records;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-appr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
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

    private sealed class FixedApprover(ToolApprovalDecision decision) : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(decision);
    }

    private sealed class AlwaysAllow : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolApprovalDecision.Allow("test"));
    }

    private sealed class FileTool : IAgentTool
    {
        private int _executions;

        public int ExecutionCount => Volatile.Read(ref _executions);

        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "write",
            JsonDocument.Parse("""{"type":"object","required":["path"]}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, ["a.txt"], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;
        public IReadOnlyList<string> DescribeFileDependencies(JsonElement a) => ["a.txt"];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class OneTurnClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _calls;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            _calls++;
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(
                Microsoft.Extensions.AI.ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailingStore(StoreRecordKind failOn) : IAgentStore
    {
        public string SessionId => "failing";
        public StoreDurability Durability => StoreDurability.ProcessMemory;

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (record.Kind == failOn)
                throw new StoreCommitException(record.Kind.ToString(), "注入失败");
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
