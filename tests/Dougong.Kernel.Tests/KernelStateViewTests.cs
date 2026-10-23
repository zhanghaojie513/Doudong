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
        Assert.Equal(PluginState.Active, Assert.Single(view.Snapshot).State);

        var deltas = new List<StateDelta>();
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                deltas.Add(delta);
                if (deltas.Count >= 2) break;
            }
        });

        await kernel.RegisterAsync(() => new SimplePlugin("test.b"));
        await reader.WaitAsync(Timeout);

        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.False(d.ResyncRequired));
        Assert.Equal(view.Revision + 1, deltas[0].Revision);
        Assert.Equal(view.Revision + 2, deltas[1].Revision);
        Assert.All(deltas, d => Assert.NotNull(d.Change));
    }

    [Fact]
    public async Task Changes_do_not_replay_states_already_in_the_snapshot()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new SimplePlugin("test.a"));

        using var view = kernel.SubscribeStates();
        Assert.Single(view.Snapshot);

        var first = new TaskCompletionSource<StateDelta>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
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
        Assert.NotNull(kernel.LastObserverError);
        Assert.Contains("队列已满", kernel.LastObserverError!.Message);
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
