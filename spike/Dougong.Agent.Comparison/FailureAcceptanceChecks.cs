using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M1 故障验收（docs/architecture.md §4.4）：并发编辑、读写竞争、取消不合作、
/// 单一执行入口。这些是"M1 验收包含"清单里尚未被其他套件覆盖的部分。
/// </summary>
public static class FailureAcceptanceChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("same-file-concurrent-edit-is-serialised", ConcurrentEditAsync);
        failures += await CheckAsync("read-write-race-sees-consistent-violation", ReadWriteRaceAsync);
        failures += await CheckAsync("uncooperative-cancel-still-holds-lock", UncooperativeCancelAsync);
        failures += await CheckAsync("single-execution-entry-under-concurrency", SingleEntryAsync);
        failures += await CheckAsync("commit-failure-stops-before-side-effect", CommitBeforeSideEffectAsync);
        failures += await CheckAsync("crash-window-reports-unknown-not-replay", CrashWindowAsync);

        Console.WriteLine(failures == 0
            ? "FAILURE-ACCEPTANCE: ALL PASS"
            : $"FAILURE-ACCEPTANCE: {failures} FAILURE(S)");
        return failures;
    }

    // 同文件并发编辑：工作区独占必须把两次写串行化，不能交织。
    private static async Task<string?> ConcurrentEditAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "shared.txt");
            await File.WriteAllTextAsync(target, "start");

            using var gate = new ToolResourceGate();
            var order = new List<string>();
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tool = new SlowWriteTool(target, order, firstEntered, releaseFirst, blockFirst: true);
            var dispatcher = new ToolDispatcher(
                gate: gate, workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var first = dispatcher.DispatchAsync(Invocation("e1", tool)).AsTask();
            await firstEntered.Task;

            var second = dispatcher.DispatchAsync(Invocation("e2", tool)).AsTask();
            var secondBlocked = await Task.WhenAny(second, Task.Delay(300)) != second;

            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);

            if (!secondBlocked) return "同文件并发编辑未串行化";
            lock (order)
            {
                // 必须是一段完整结束后另一段才开始，不能出现 a-start,b-start 交错。
                if (order.Count < 4) return $"执行记录不完整：{string.Join(",", order)}";
                if (order[1].Contains("start") && order[1].StartsWith("b"))
                    return $"执行交织：{string.Join(",", order)}";
            }

            return null;
        }
        finally { TryDelete(root); }
    }

    // 读写竞争：读工具看到的是写入前的版本，审批后文件变化必须被拦。
    private static async Task<string?> ReadWriteRaceAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "data.txt");
            await File.WriteAllTextAsync(target, "v1");

            var readTool = new FileTool("read_file", target, ToolAccess.Read);
            var writeTool = new FileTool("write_file", target, ToolAccess.Write);

            // 读工具在审批期间文件被改写：复核必须拒绝。
            var dispatcher = new ToolDispatcher(
                new Approver(async () =>
                {
                    await File.WriteAllTextAsync(target, "v2-changed-during-approval");
                    return ToolApprovalDecision.Allow();
                }),
                workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(readTool);
            dispatcher.Register(writeTool);

            var result = await dispatcher.DispatchAsync(Invocation("r1", readTool));
            if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
                return $"读写竞争未被拦下，结果 {result.Outcome}";
            return readTool.ExecutionCount == 0 ? null : "竞争下仍然执行了读取";
        }
        finally { TryDelete(root); }
    }

    // 取消不合作：工具忽略取消并继续持有工作区锁，此时第二个写必须仍被阻塞。
    private static async Task<string?> UncooperativeCancelAsync()
    {
        var root = NewRoot();
        try
        {
            using var gate = new ToolResourceGate();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tool = new UncooperativeTool(entered, release);
            var other = new FileTool("other_write", Path.Combine(root, "x.txt"), ToolAccess.Write);

            var dispatcher = new ToolDispatcher(
                gate: gate, workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);
            dispatcher.Register(other);

            using var cts = new CancellationTokenSource();
            var running = dispatcher.DispatchAsync(Invocation("u1", tool), cts.Token).AsTask();
            await entered.Task;

            cts.Cancel();
            await Task.Delay(100);   // 给取消传播一点时间

            var second = dispatcher.DispatchAsync(Invocation("u2", other)).AsTask();
            var secondBlocked = await Task.WhenAny(second, Task.Delay(300)) != second;

            release.TrySetResult();
            var firstResult = await running.WaitAsync(TimeSpan.FromSeconds(3));
            await second.WaitAsync(TimeSpan.FromSeconds(3));

            if (!secondBlocked)
                return "不合作工具取消后提前释放了锁，可能出现重叠执行";

            // 文档 §4.4：已执行者不得伪装已撤销。工具忽略取消并跑完，终态必须是"已执行"，
            // 并把"取消被忽略、副作用可能已发生"作为诊断暴露给宿主。
            if (firstResult.Outcome != ToolCallOutcome.Succeeded)
                return $"不合作工具跑完后应报已执行，实际 {firstResult.Outcome}";
            return firstResult.Diagnostic?.Contains("未响应取消", StringComparison.Ordinal) == true
                ? null
                : $"缺少取消被忽略的诊断，实际 {firstResult.Diagnostic}";
        }
        finally { TryDelete(root); }
    }

    // 单一执行入口：并发派发下，工具执行次数必须等于 dispatcher 记录的派发次数。
    private static async Task<string?> SingleEntryAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new CountingTool();
            var dispatcher = new ToolDispatcher(
                workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var tasks = Enumerable.Range(0, 24)
                .Select(i => dispatcher.DispatchAsync(Invocation($"c{i}", tool)).AsTask())
                .ToArray();
            await Task.WhenAll(tasks);

            var succeeded = tasks.Count(t => t.Result.Outcome == ToolCallOutcome.Succeeded);
            if (tool.ExecutionCount != succeeded)
                return $"执行 {tool.ExecutionCount} 次但报告成功 {succeeded} 次";
            return succeeded == 24 ? null : $"应全部成功，实际 {succeeded}/24";
        }
        finally { TryDelete(root); }
    }

    // 提交失败必须发生在副作用之前。
    private static async Task<string?> CommitBeforeSideEffectAsync()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "must-not-exist.txt");
            var tool = new WriteOnceTool(target);
            var store = new FailingStore(StoreRecordKind.ToolCallStarted);
            var dispatcher = new ToolDispatcher(
                workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);
            var runtime = new LoopAgentRuntime(
                new OneToolCallClient("c1", "write_file",
                    """{"path":"must-not-exist.txt","content":"x"}"""),
                dispatcher, store);

            var events = new List<AgentEvent>();
            await foreach (var e in runtime.RunAsync(new AgentRunRequest("s", "t", [], "write it")))
                events.Add(e);

            if (File.Exists(target)) return "提交失败后仍产生了副作用";
            return null;
        }
        finally { TryDelete(root); }
    }

    // 崩溃窗口：有开始无终态 → 恢复必须报 unknown，不重放。
    private static async Task<string?> CrashWindowAsync()
    {
        var root = NewRoot();
        var sessionPath = Path.Combine(root, "session.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(sessionPath, "crash"))
            {
                await store.CommitAsync(Record(StoreRecordKind.UserInput, "do it"));
                await store.CommitAsync(Record(StoreRecordKind.ToolCallStarted,
                    JsonSerializer.Serialize(new { id = "c1", tool = "write_file" })));
                // 模拟崩溃：不写终态。
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(sessionPath, "crash");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation) return "崩溃窗口未报出对账需求";
            if (recovered.Unknown.Single().InvocationId != "c1") return "未知结果 ID 不符";
            return null;
        }
        finally { TryDelete(root); }
    }

    private static StoreRecord Record(StoreRecordKind kind, string payload)
    {
        // 已经是 JSON 的按结构存，纯文本按字符串存——与运行时写入一致。
        JsonElement element;
        try { element = JsonDocument.Parse(payload).RootElement.Clone(); }
        catch (JsonException) { element = JsonSerializer.SerializeToElement(payload); }
        return new StoreRecord(0, kind, element);
    }

    private static ToolInvocation Invocation(string id, IAgentTool tool)
    {
        using var document = JsonDocument.Parse("""{"path":"shared.txt","content":"v"}""");
        return new ToolInvocation(id, tool.Descriptor.Id, document.RootElement.Clone());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-fa-" + Guid.NewGuid().ToString("N")[..8]);
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

    private sealed class Approver(Func<Task<ToolApprovalDecision>> decide) : IToolApprover
    {
        public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            await decide().ConfigureAwait(false);
    }

    private sealed class SlowWriteTool(
        string target, List<string> order,
        TaskCompletionSource entered, TaskCompletionSource release, bool blockFirst) : IAgentTool
    {
        private int _calls;

        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "slow write",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, [target], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement a) =>
            new(ToolConcurrency.WorkspaceExclusive);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement a) => [target];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var n = ++_calls;
            var label = n == 1 ? "a" : "b";
            lock (order) order.Add($"{label}-start");

            if (n == 1 && blockFirst)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            lock (order) order.Add($"{label}-end");
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    private sealed class UncooperativeTool(
        TaskCompletionSource entered, TaskCompletionSource release) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "stubborn", "stubborn", "1.0.0", "ignores cancellation",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Execute, true, [], [], []);

        // 故意声明工作区独占：取消后仍持锁，第二个调用必须被挡。
        public ToolExecutionPlan DescribeExecution(JsonElement a) =>
            new(ToolConcurrency.WorkspaceExclusive);

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            // 忽略 cancellationToken：模拟不合作工具。
            await release.Task;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    private sealed class CountingTool : IAgentTool
    {
        private int _count;
        public int ExecutionCount => Volatile.Read(ref _count);

        public ToolDescriptor Descriptor { get; } = new(
            "count", "count", "1.0.0", "count",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class WriteOnceTool(string target) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "write",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.Write, true, [target], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            File.WriteAllText(target, "side-effect");
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class FileTool(string id, string path, ToolAccess access) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            id, id, "1.0.0", "file",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(access, true, [path], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;
        public IReadOnlyList<string> DescribeFileDependencies(JsonElement a) => [path];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private sealed class FailingStore(StoreRecordKind failOn) : IAgentStore
    {
        public string SessionId => "failing";
        public StoreDurability Durability => StoreDurability.ProcessMemory;

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (record.Kind == failOn) throw new StoreCommitException(record.Kind.ToString(), "注入失败");
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

    private sealed class OneToolCallClient(string id, string tool, string args) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _calls++;
            if (_calls == 1)
            {
                using var document = JsonDocument.Parse(args);
                var dict = document.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(id, tool, dict)]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
