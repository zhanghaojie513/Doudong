using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelServiceLeaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IWorker;

    private sealed class Worker : IWorker;

    [Fact]
    public async Task In_flight_invocation_blocks_cleanup_until_action_returns()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var invocation = reference.InvokeAsync(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            // 故意忽略取消：租约必须持续到方法真正返回。
            await release.Task;
        }).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            stopping = kernel.DeactivateAsync(provider.Manifest.Id).AsTask();
            await cancelled.Task.WaitAsync(Timeout);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, provider.CleanupCount);
            Assert.Equal(PluginState.Deactivating, (await kernel.GetPluginsAsync()).Single().State);
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Timeout);
            if (stopping is not null) await stopping.WaitAsync(Timeout);
        }

        Assert.Equal(1, provider.CleanupCount);
        Assert.Equal(PluginState.Stopped, (await kernel.GetPluginsAsync()).Single().State);
    }

    [Fact]
    public async Task Closed_gate_rejects_new_resolution_and_old_reference_calls()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var invocation = reference.InvokeAsync(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await release.Task;
        }).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            stopping = kernel.DeactivateAsync(provider.Manifest.Id).AsTask();
            await cancelled.Task.WaitAsync(Timeout);

            Assert.Null(kernel.Resolver.Resolve<IWorker>());
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await reference.InvokeAsync((_, _) => ValueTask.CompletedTask));
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Timeout);
            if (stopping is not null) await stopping.WaitAsync(Timeout);
        }

        Assert.Equal(PluginState.Stopped, (await kernel.GetPluginsAsync()).Single().State);
    }

    [Fact]
    public async Task Failed_or_cancelled_invocation_releases_the_lease()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reference.InvokeAsync((_, _) => throw new InvalidOperationException("call failed")));
        using var cancelledToken = new CancellationTokenSource();
        cancelledToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reference.InvokeAsync((_, _) => ValueTask.CompletedTask, cancelledToken.Token));

        var stopped = await kernel.DeactivateAsync(provider.Manifest.Id).AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Stopped, stopped.State);
        Assert.Equal(1, provider.CleanupCount);
    }

    [Fact]
    public async Task Deactivating_consumer_drains_its_calls_without_stopping_provider()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var consumer = new WorkerPlugin(
            "test.consumer", requirements: [new ServiceRequirement(typeof(IWorker))]);
        await kernel.RegisterAsync(() => consumer);
        var reference = consumer.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var invocation = reference.InvokeAsync(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await release.Task;
        }).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            stopping = kernel.DeactivateAsync(consumer.Manifest.Id).AsTask();
            await cancelled.Task.WaitAsync(Timeout);

            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, consumer.CleanupCount);
            Assert.Equal(PluginState.Active,
                (await kernel.GetPluginsAsync()).Single(p => p.Id == provider.Manifest.Id).State);
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Timeout);
            if (stopping is not null) await stopping.WaitAsync(Timeout);
        }

        Assert.Equal(1, consumer.CleanupCount);
        Assert.Equal(PluginState.Active,
            (await kernel.GetPluginsAsync()).Single(p => p.Id == provider.Manifest.Id).State);
    }

    [Fact]
    public async Task Old_reference_and_old_context_resolver_stay_dead_after_reactivation()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var oldContext = provider.Context!;
        var oldReference = oldContext.Resolver.Resolve<IWorker>();
        Assert.NotNull(oldReference);

        await kernel.DeactivateAsync(provider.Manifest.Id);
        await kernel.ActivateAsync(provider.Manifest.Id);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await oldReference.InvokeAsync((_, _) => ValueTask.CompletedTask));
        Assert.Null(oldContext.Resolver.Resolve<IWorker>());

        var newReference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(newReference);
        await newReference.InvokeAsync((_, _) => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Kernel_disposal_waits_for_in_flight_invocations()
    {
        var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        var started = Signal();
        var release = Signal();
        var invocation = reference.InvokeAsync(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        }).AsTask();
        Task? disposing = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            disposing = kernel.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, provider.CleanupCount);
        }
        finally
        {
            release.TrySetResult();
            await invocation.WaitAsync(Timeout);
            if (disposing is not null) await disposing.WaitAsync(Timeout);
            await kernel.DisposeAsync();
        }

        Assert.Equal(1, provider.CleanupCount);
    }

    [Fact]
    public async Task Nested_lifecycle_change_inside_invocation_is_rejected()
    {
        await using var kernel = new DougongKernel();
        var provider = new WorkerPlugin("test.provider", registerWorker: true);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IWorker>();
        Assert.NotNull(reference);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await reference.InvokeAsync(async (_, _) =>
            {
                await kernel.DeactivateAsync(provider.Manifest.Id);
            });
        });

        var stopped = await kernel.DeactivateAsync(provider.Manifest.Id).AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginState.Stopped, stopped.State);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WorkerPlugin(
        string id,
        bool registerWorker = false,
        params ServiceRequirement[] requirements) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0", requirements);
        public PluginContext? Context { get; private set; }
        public IResolver Resolver => Context!.Resolver;
        public int CleanupCount { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            context.RegisterForCleanup(_ =>
            {
                CleanupCount++;
                return ValueTask.CompletedTask;
            });
            if (registerWorker) context.Services.Register<IWorker>(new Worker());
            return ValueTask.CompletedTask;
        }
    }
}
