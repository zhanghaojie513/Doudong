using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelIsolationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private interface IWorker;
    private sealed class Worker : IWorker;

    [Fact]
    public async Task Drain_timeout_cannot_be_reactivated_or_disposed_into_cleanup()
    {
        var kernel = new DougongKernel { DrainTimeout = TimeSpan.FromMilliseconds(50) };
        var release = Signal();
        var entered = Signal();
        PluginContext? context = null;
        var cleanup = 0;
        var plugin = new CallbackPlugin("isolation.drain", (scope, _) =>
        {
            context = scope;
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
            Assert.Equal(PluginState.Failed, (await kernel.DeactivateAsync(plugin.Manifest.Id).AsTask().WaitAsync(Limit)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ActivateAsync(plugin.Manifest.Id).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => context!.DisposeAsync().AsTask().WaitAsync(Limit));
            Assert.Equal(0, cleanup);
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Limit);
            await Record.ExceptionAsync(() => kernel.DisposeAsync().AsTask().WaitAsync(Limit));
        }
        Assert.Equal(0, cleanup);
    }

    internal sealed class CallbackPlugin(string id, Func<PluginContext, CancellationToken, ValueTask> activate) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) => activate(context, cancellationToken);
    }
}
