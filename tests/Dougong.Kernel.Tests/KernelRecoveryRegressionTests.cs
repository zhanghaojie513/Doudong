using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelRecoveryRegressionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ContractsAssemblyPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.Contracts", "bin", Configuration,
        "net10.0", "Dougong.Sample.Contracts.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task Unload_rejects_when_cleanup_fails_during_the_same_operation()
    {
        var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.FailingCleanupPlugin")
            .AsTask().WaitAsync(Limit);

        var error = await Record.ExceptionAsync(async () =>
            await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Limit));

        Assert.NotNull(error);
        Assert.IsAssignableFrom<InvalidOperationException>(error);

        // 失败必须保留条目，不能假装已卸载。
        var plugins = await kernel.GetPluginsAsync();
        Assert.Single(plugins);
        Assert.NotEqual(PluginState.Active, plugins.Single().State);
        await kernel.DisposeAsync();
    }

    [Fact]
    public async Task Activation_requested_while_unloading_is_rejected()
    {
        var kernel = new DougongKernel();
        var cleanupEntered = Signal();
        var releaseCleanup = Signal();
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Limit);

        Assert.Equal(PluginState.Active, (await kernel.GetPluginsAsync()).Single().State);
        var unloading = kernel.UnloadAsync(handle.Id).AsTask();
        await unloading.WaitAsync(Limit);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            async () => await kernel.ActivateAsync(handle.Id).AsTask().WaitAsync(Limit));
        GC.KeepAlive(cleanupEntered);
        GC.KeepAlive(releaseCleanup);
    }

    [Fact]
    public async Task Activate_after_drain_timeout_is_rejected()
    {
        var kernel = new DougongKernel { DrainTimeout = TimeSpan.FromMilliseconds(50) };
        var entered = Signal();
        var release = Signal();
        var cleanup = 0;
        var plugin = new CallbackPlugin("recovery.isolated", (scope, _) =>
        {
            scope.Services.Register<IWorker>(new Worker());
            scope.RegisterForCleanup(_ => { cleanup++; return ValueTask.CompletedTask; });
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => plugin);
        var invocation = kernel.Resolver.Resolve<IWorker>()!.InvokeAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            var stopped = await kernel.DeactivateAsync(plugin.Manifest.Id).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Failed, stopped.State);

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await kernel.ActivateAsync(plugin.Manifest.Id).AsTask().WaitAsync(Limit));
            Assert.Equal(0, cleanup);
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await kernel.DisposeAsync().AsTask().WaitAsync(Limit));
        }

        Assert.Equal(0, cleanup);
    }

    [Fact]
    public async Task Cancelled_activation_rolls_back_without_reactivating()
    {
        var kernel = new DougongKernel();
        var entered = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var plugin = new CallbackPlugin("recovery.cancel", async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        var registering = kernel.RegisterAsync(() => plugin, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await registering.WaitAsync(Limit));

        // 取消后条目不得在没有显式 Activate 的情况下自动变成 Active。
        release.TrySetResult();
        await Task.Delay(100);
        var state = (await kernel.GetPluginsAsync()).Single().State;
        Assert.NotEqual(PluginState.Active, state);
        await kernel.DisposeAsync();
    }

    private interface IWorker;
    private sealed class Worker : IWorker;

    private sealed class CallbackPlugin(
        string id, Func<PluginContext, CancellationToken, ValueTask> activate) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            activate(context, cancellationToken);
    }
}
