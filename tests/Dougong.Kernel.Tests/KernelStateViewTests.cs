using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelStateViewTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class SimplePlugin(string id) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Snapshot_and_changes_are_consistent_without_gaps()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));

        using var view = kernel.SubscribeStates();
        Assert.Equal(PluginState.Active, Assert.Single(view.Snapshot!).State);

        var deltas = new List<StateDelta>();
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                if (delta.Kind != StateDeltaKind.StateChange) continue;
                deltas.Add(delta);
                if (deltas.Count >= 2) break;
            }
        });

        await kernel.RegisterAsync(() => new SimplePlugin("test.b"));
        await reader.WaitAsync(Timeout);

        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.False(d.ResyncRequired));
        Assert.Equal(view.Revision + 2, deltas[0].Revision);
        Assert.Equal(view.Revision + 3, deltas[1].Revision);
        Assert.All(deltas, d => Assert.NotNull(d.Change));
    }

    [Fact]
    public async Task Changes_do_not_replay_states_already_in_the_snapshot()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));

        using var view = kernel.SubscribeStates();
        Assert.Single(view.Snapshot!);

        var first = new TaskCompletionSource<StateDelta>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                if (delta.Kind != StateDeltaKind.StateChange) continue;
                if (delta.Change?.PluginId.Value == "test.b") first.TrySetResult(delta);
                if (first.Task.IsCompleted) break;
            }
        });

        await kernel.RegisterAsync(() => new SimplePlugin("test.b"));
        var delta = await first.Task.WaitAsync(Timeout);

        Assert.Equal("test.b", delta.Change!.PluginId.Value);
        // 快照中没有 test.b，增量必须从它的首个迁移开始，不能跳过。
        Assert.Equal(PluginState.Registered, delta.Change.From);
    }

    [Fact]
    public async Task Revision_increases_monotonically_across_subscriptions()
    {
        await using var kernel = new DougongKernel();
        using var first = kernel.SubscribeStates();
        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));
        using var second = kernel.SubscribeStates();
        Assert.True(second.Revision > first.Revision);
    }

    [Fact]
    public async Task Overflow_signals_resync_and_ends_the_stream()
    {
        await using var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates(capacity: 1);

        var sawResync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                if (delta.ResyncRequired) sawResync.TrySetResult();
            }

            completed.TrySetResult();
        });

        for (var i = 0; i < 20; i++)
            await kernel.RegisterAsync(() => new SimplePlugin($"test.p{i}"));

        await sawResync.Task.WaitAsync(Timeout);
        await completed.Task.WaitAsync(Timeout);
        Assert.NotNull(kernel.LastObserverDiagnostic);
        Assert.Contains("队列已满", kernel.LastObserverDiagnostic!.Message);
    }

    [Fact]
    public async Task Deltas_carry_their_partition_and_order_within_a_partition()
    {
        await using var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates();
        var deltas = new List<StateDelta>();
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                deltas.Add(delta);
                if (deltas.Count >= 2) break;
            }
        });

        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));
        await reader.WaitAsync(Timeout);

        Assert.All(deltas, d => Assert.Equal("test.a", d.Partition!.Value.Value));
        Assert.Equal(
            new[] { StateDeltaKind.Registered, StateDeltaKind.StateChange },
            deltas.Select(d => d.Kind).ToArray());

        // 同一分区内修订号严格递增。
        foreach (var group in deltas.GroupBy(d => d.Partition))
        {
            var revisions = group.Select(d => d.Revision).ToList();
            Assert.Equal(revisions.OrderBy(r => r), revisions);
            Assert.Equal(revisions.Distinct().Count(), revisions.Count);
        }
    }

    [Fact]
    public async Task Resuming_from_a_cursor_replays_missed_deltas_without_a_snapshot()
    {
        await using var kernel = new DougongKernel();
        using var first = kernel.SubscribeStates();
        var start = first.Revision;

        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));
        await kernel.RegisterAsync(() => new SimplePlugin("test.b"));

        using var resumed = kernel.ResumeStates(start);
        Assert.False(resumed.RequiresResync);
        Assert.Null(resumed.Snapshot);

        var replay = new List<StateDelta>();
        await foreach (var delta in resumed.Changes)
        {
            replay.Add(delta);
            if (replay.Count >= 6) break;
        }

        Assert.Equal(6, replay.Count);
        Assert.Equal(start + 1, replay[0].Revision);
        Assert.Equal(start + 6, replay[^1].Revision);
        Assert.Equal(replay.Select(d => d.Revision), replay.Select(d => d.Revision).Order());
        Assert.Contains(replay, d => d.Partition?.Value == "test.a");
        Assert.Contains(replay, d => d.Partition?.Value == "test.b");
        Assert.Equal(StateDeltaKind.Registered, replay[0].Kind);
        Assert.Equal(PluginState.Active, replay[^1].Change!.To);
    }

    [Fact]
    public async Task Cursor_older_than_retention_requires_resync()
    {
        await using var kernel = new DougongKernel();
        kernel.RevisionHistoryLimit = 2;
        using var first = kernel.SubscribeStates();
        var start = first.Revision;

        for (var i = 0; i < 3; i++)
            await kernel.RegisterAsync(() => new SimplePlugin($"test.p{i}"));

        using var stale = kernel.ResumeStates(start);
        Assert.True(stale.RequiresResync);
        Assert.Null(stale.Snapshot);
        Assert.Empty(await CollectAsync(stale.Changes, 1));
    }

    [Fact]
    public async Task Cursor_at_current_revision_yields_only_new_deltas()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));
        using var current = kernel.SubscribeStates();
        var cursor = current.Revision;

        using var resumed = kernel.ResumeStates(cursor);
        Assert.False(resumed.RequiresResync);

        var deltas = new List<StateDelta>();
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in resumed.Changes)
            {
                deltas.Add(delta);
                break;
            }
        });
        await kernel.RegisterAsync(() => new SimplePlugin("test.b"));
        await reader.WaitAsync(Timeout);

        Assert.Equal(cursor + 1, deltas[0].Revision);
    }

    [Fact]
    public async Task Future_cursor_is_rejected()
    {
        await using var kernel = new DougongKernel();
        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.ResumeStates(9999));
    }

    private static async Task<List<StateDelta>> CollectAsync(IAsyncEnumerable<StateDelta> source, int max)
    {
        var list = new List<StateDelta>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var delta in source.WithCancellation(cts.Token))
            {
                list.Add(delta);
                if (list.Count >= max) break;
            }
        }
        catch (OperationCanceledException)
        {
            // 预期：没有更多增量时序列保持等待。
        }

        return list;
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving_changes()
    {
        await using var kernel = new DougongKernel();
        var view = kernel.SubscribeStates();
        var revision = view.Revision;
        view.Dispose();

        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));

        using var fresh = kernel.SubscribeStates();
        Assert.True(fresh.Revision > revision);
    }
}
