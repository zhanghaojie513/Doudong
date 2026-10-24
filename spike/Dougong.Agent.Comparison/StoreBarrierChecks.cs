using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 提交屏障的确定性验证：用会失败的存储证明"提交失败即停止执行"，
/// 而不是靠观察日志猜测。
/// </summary>
public static class StoreBarrierChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("input-commit-failure-stops-run", InputFailureStopsRunAsync);
        failures += await CheckAsync("tool-start-failure-prevents-execution", ToolStartFailurePreventsExecutionAsync);
        failures += await CheckAsync("committed-session-recovers-history", CommittedSessionRecoversAsync);
        failures += await CheckAsync("unknown-outcome-on-missing-completion", UnknownOutcomeAsync);
        failures += await CheckAsync("single-writer-lease", SingleWriterLeaseAsync);
        failures += await CheckAsync("truncated-tail-is-isolated", TruncatedTailAsync);

        Console.WriteLine(failures == 0
            ? "STORE: ALL PASS"
            : $"STORE: {failures} FAILURE(S)");
        return failures;
    }

    // 1. 用户输入提交失败 → 不得调用模型、不得执行工具。
    private static async Task<string?> InputFailureStopsRunAsync()
    {
        var store = new FailingStore(failOn: StoreRecordKind.UserInput);
        var client = new CountingClient();
        var tool = new FakeTool("read_file", ["path"]);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var runtime = new LoopAgentRuntime(client, dispatcher, store);

        var events = await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");

        if (client.CallCount != 0) return $"提交失败后仍调用了模型 {client.CallCount} 次";
        if (tool.ExecutionCount != 0) return $"提交失败后仍执行了工具 {tool.ExecutionCount} 次";
        if (events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Outcome != AgentRunOutcome.Failed)
            return "未以 Failed 结束";
        return null;
    }

    // 2. 工具开始记录提交失败 → 工具不得执行（副作用屏障）。
    private static async Task<string?> ToolStartFailurePreventsExecutionAsync()
    {
        var store = new FailingStore(failOn: StoreRecordKind.ToolCallStarted);
        var client = new SingleCallClient();
        var tool = new FakeTool("read_file", ["path"]);
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);
        var runtime = new LoopAgentRuntime(client, dispatcher, store);

        var events = await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");

        if (tool.ExecutionCount != 0)
            return $"工具开始记录提交失败后仍执行了工具 {tool.ExecutionCount} 次";
        if (events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Outcome != AgentRunOutcome.Failed)
            return "未以 Failed 结束";
        return null;
    }

    // 3. 正常提交的会话可回放出一致历史。
    private static async Task<string?> CommittedSessionRecoversAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-store-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s1"))
            {
                var client = new SingleCallClient();
                var tool = new FakeTool("read_file", ["path"]);
                var dispatcher = new ToolDispatcher();
                dispatcher.Register(tool);
                var runtime = new LoopAgentRuntime(client, dispatcher, store);
                await CollectAsync(runtime, "Read /tmp/a.txt using the read_file tool.");
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s1");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.History.Count == 0) return "恢复后没有历史";
            if (recovered.Unknown.Count > 0) return $"正常会话出现未知结果 {recovered.Unknown.Count} 条";
            if (recovered.HasSequenceGap) return "正常会话出现序号缺口";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 4. 有开始无终态 → 必须报 OutcomeUnknown，且不自动重放。
    private static async Task<string?> UnknownOutcomeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-unknown-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s2"))
            {
                await CommitAsync(store, StoreRecordKind.UserInput, "do it");
                await CommitAsync(store, StoreRecordKind.ToolCallStarted,
                    JsonSerializer.Serialize(new { id = "c1", tool = "write_file" }));
                // 故意不提交终态，模拟崩溃窗口。
            }

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s2");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (!recovered.RequiresReconciliation) return "没有报出需要人工对账";
            var unknown = recovered.Unknown.SingleOrDefault();
            if (unknown is null) return "未知结果条目缺失";
            if (unknown.InvocationId != "c1") return $"调用 ID 不符：{unknown.InvocationId}";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 5. 单写者租约：第二个打开必须失败。
    private static async Task<string?> SingleWriterLeaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-lease-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var first = await JsonlAgentStore.OpenAsync(path, "s3");
            var error = await RecordAsync(async () =>
            {
                await using var second = await JsonlAgentStore.OpenAsync(path, "s3");
            });
            if (error is null) return "第二个写入者被允许打开，单写者租约失效";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // 6. 尾部半条记录必须被隔离，不能污染已提交部分。
    private static async Task<string?> TruncatedTailAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dougong-tail-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var store = await JsonlAgentStore.OpenAsync(path, "s4"))
            {
                await CommitAsync(store, StoreRecordKind.UserInput, "first");
                await CommitAsync(store, StoreRecordKind.UserInput, "second");
            }

            // 追加一条被截断的记录（模拟写入中途崩溃）。
            await File.AppendAllTextAsync(path, "{\"Sequence\":3,\"Kind\":\"UserInp");

            await using var reopened = await JsonlAgentStore.OpenAsync(path, "s4");
            var recovered = await SessionRecovery.ReplayAsync(reopened);

            if (recovered.History.Count != 2)
                return $"期望恢复 2 条已提交记录，实际 {recovered.History.Count}";
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task CommitAsync(IAgentStore store, StoreRecordKind kind, string payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await store.CommitAsync(new StoreRecord(0, kind, document.RootElement.Clone()));
    }

    private static async Task<IReadOnlyList<AgentEvent>> CollectAsync(
        IAgentRuntime runtime, string input)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunAsync(new AgentRunRequest("t", "turn", [], input)))
            events.Add(e);
        return events;
    }

    private static async Task<Exception?> RecordAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
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

    /// <summary>在指定记录类型上必然失败的存储，用于证明屏障真的拦截执行。</summary>
    private sealed class FailingStore(StoreRecordKind failOn) : IAgentStore
    {
        public string SessionId => "failing";
        public StoreDurability Durability => StoreDurability.ProcessMemory;

        public ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (record.Kind == failOn)
                throw new StoreCommitException(record.Kind.ToString(), "注入的提交失败");
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

    /// <summary>永远返回纯文本，用于观察"提交失败时模型是否被调用"。</summary>
    private sealed class CountingClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "text")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "text");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>第一次返回工具调用，之后返回文本。</summary>
    private sealed class SingleCallClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("c1", "read_file",
                        new Dictionary<string, object?> { ["path"] = "/tmp/a.txt" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
