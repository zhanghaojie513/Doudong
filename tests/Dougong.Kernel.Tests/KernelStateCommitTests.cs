using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelStateCommitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class SimplePlugin(string id) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Blocking_observer_does_not_reorder_or_drop_revisions()
    {
        await using var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates(capacity: 256);
        var deltas = new List<StateDelta>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            ready.SetResult();
            await foreach (var delta in view.Changes)
            {
                deltas.Add(delta);
                if (deltas.Count >= 6) break;
            }
        });
        await ready.Task.WaitAsync(Timeout);

        // 同步观察者阻塞：live 增量在观察者运行前已提交，不能被它拖在后面。
        using var gate = new ManualResetEventSlim();
        using var observer = kernel.SubscribeStateChanges(
            _ => gate.Wait(TimeSpan.FromMilliseconds(200)));

        await kernel.RegisterAsync(() => new SimplePlugin("commit.a"));
        await kernel.RegisterAsync(() => new SimplePlugin("commit.b"));
        gate.Set();

        await reader.WaitAsync(Timeout);
        var revisions = deltas.Select(d => d.Revision).ToList();
        Assert.Equal(revisions.OrderBy(r => r), revisions);
        Assert.Equal(revisions.Distinct().Count(), revisions.Count);
    }

    [Fact]
    public async Task Snapshot_plus_deltas_rebuilds_the_current_plugin_set_after_removal()
    {
        var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates();
        var added = new List<PluginId>();
        var removed = new List<PluginId>();
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in view.Changes)
            {
                if (delta.Partition is not { } id) continue;
                if (delta.Kind == StateDeltaKind.Registered) added.Add(id);
                if (delta.Kind == StateDeltaKind.Removed) removed.Add(id);
            }
        });

        var handle = await kernel.LoadAsync(
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "samples", "Dougong.Sample.Contracts", "bin",
#if DEBUG
                    "Debug",
#else
                    "Release",
#endif
                    "net10.0", "Dougong.Sample.Contracts.dll")),
                "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);
        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);

        await Task.Delay(100);
        Assert.Contains(handle.Id, added);
        Assert.Contains(handle.Id, removed);

        var snapshot = await kernel.GetPluginsAsync();
        var projected = view.Snapshot!.Select(p => p.Id).ToHashSet();
        foreach (var id in added) projected.Add(id);
        foreach (var id in removed) projected.Remove(id);

        Assert.Equal(snapshot.Select(p => p.Id).OrderBy(id => id.Value), projected.OrderBy(id => id.Value));
        view.Dispose();
        await kernel.DisposeAsync();
        GC.KeepAlive(reader);
    }
}
