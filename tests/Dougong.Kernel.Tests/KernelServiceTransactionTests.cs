using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelServiceTransactionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Pending_services_are_private_until_activation_commits()
    {
        await using var kernel = new DougongKernel();
        var reader = new RegistryPlugin("test.reader");
        await kernel.RegisterAsync(() => reader);
        var staged = Signal();
        var release = Signal();
        var first = new Marker("first");
        var second = new Marker("second");
        var provider = new RegistryPlugin("test.provider", async (context, _) =>
        {
            context.Services.Register<IMarker>(first);
            context.Services.Register<IMarker>(second, "named");
            Assert.Same(first, context.Services.Resolve<IMarker>());
            Assert.Same(second, context.Services.Resolve<IMarker>("named"));
            Assert.Equal([first], context.Services.ResolveAll<IMarker>());
            staged.TrySetResult();
            await release.Task;
        });
        var observedCommit = false;
        using var observation = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId != provider.Manifest.Id || change.To != PluginState.Active) return;
            observedCommit = ReferenceEquals(first, reader.Services.Resolve<IMarker>())
                && ReferenceEquals(second, reader.Services.Resolve<IMarker>("named"));
        });

        var registering = kernel.RegisterAsync(() => provider).AsTask();
        try
        {
            await staged.Task.WaitAsync(Timeout);
            Assert.Null(reader.Services.Resolve<IMarker>());
            Assert.Null(reader.Services.Resolve<IMarker>("named"));
            Assert.Empty(reader.Services.ResolveAll<IMarker>());
            Assert.Empty(reader.Services.ResolveAll<IMarker>("named"));
            Assert.Equal(PluginState.Activating,
                (await kernel.GetPluginsAsync()).Single(p => p.Id == provider.Manifest.Id).State);
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(Timeout);
        }

        Assert.True(observedCommit);
        Assert.Same(first, reader.Services.Resolve<IMarker>());
        Assert.Same(second, reader.Services.Resolve<IMarker>("named"));
        Assert.Equal([first], reader.Services.ResolveAll<IMarker>());
    }

    [Fact]
    public async Task Activation_can_resolve_committed_dependencies_and_its_own_pending_services()
    {
        await using var kernel = new DougongKernel();
        var committed = new Marker("committed");
        await kernel.RegisterAsync(() => new RegistryPlugin("test.first", (context, _) =>
        {
            context.Services.Register<IMarker>(committed);
            return ValueTask.CompletedTask;
        }));
        var pending = new Marker("pending");
        var second = new RegistryPlugin("test.second", (context, _) =>
        {
            Assert.Same(committed, context.Services.Resolve<IMarker>());
            context.Services.Register<IMarker>(pending, "own");
            Assert.Same(committed, context.Services.Resolve<IMarker>());
            Assert.Same(pending, context.Services.Resolve<IMarker>("own"));
            Assert.Equal([pending], context.Services.ResolveAll<IMarker>("own"));
            return ValueTask.CompletedTask;
        }, new ServiceRequirement(typeof(IMarker)));

        var handle = await kernel.RegisterAsync(() => second);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.Same(committed, second.Services.Resolve<IMarker>());
        Assert.Same(pending, second.Services.Resolve<IMarker>("own"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_duplicate_registration_never_publishes_services(bool duplicate)
    {
        await using var kernel = new DougongKernel();
        var reader = new RegistryPlugin("test.reader");
        await kernel.RegisterAsync(() => reader);
        var staged = Signal();
        var release = Signal();
        var cleanupSawNoServices = false;
        var provider = new RegistryPlugin("test.broken", async (context, _) =>
        {
            context.Services.Register<IMarker>(new Marker("pending"));
            context.RegisterForCleanup(_ =>
            {
                cleanupSawNoServices = reader.Services.Resolve<IMarker>() is null
                    && reader.Services.ResolveAll<IMarker>().Count == 0;
                return ValueTask.CompletedTask;
            });
            staged.TrySetResult();
            await release.Task;
            if (duplicate)
                context.Services.Register<IMarker>(new Marker("duplicate"));
            else
                throw new InvalidOperationException("activation failed");
        });
        var registering = kernel.RegisterAsync(() => provider).AsTask();
        try
        {
            await staged.Task.WaitAsync(Timeout);
            Assert.Null(reader.Services.Resolve<IMarker>());
            Assert.Empty(reader.Services.ResolveAll<IMarker>());
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(Timeout);
        }

        Assert.Equal(PluginState.Failed, (await registering).State);
        Assert.True(cleanupSawNoServices);
        Assert.Null(reader.Services.Resolve<IMarker>());
        Assert.Empty(reader.Services.ResolveAll<IMarker>());
        var consumer = await kernel.RegisterAsync(() =>
            new RegistryPlugin("test.consumer", requirements: [new ServiceRequirement(typeof(IMarker))]));
        Assert.Equal(PluginState.Blocked, consumer.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_or_disposal_discards_the_uncommitted_batch(bool disposeKernel)
    {
        var kernel = new DougongKernel();
        var reader = new RegistryPlugin("test.reader");
        await kernel.RegisterAsync(() => reader);
        var staged = Signal();
        var cancelled = Signal();
        var release = Signal();
        var activeNotifications = 0;
        var provider = new RegistryPlugin("test.pending", async (context, token) =>
        {
            context.Services.Register<IMarker>(new Marker("pending"));
            using var subscription = token.Register(() => cancelled.TrySetResult());
            staged.TrySetResult();
            await release.Task;
        });
        using var observation = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId == provider.Manifest.Id && change.To == PluginState.Active)
                activeNotifications++;
        });
        var registering = kernel.RegisterAsync(() => provider).AsTask();
        Task? stopping = null;
        try
        {
            await staged.Task.WaitAsync(Timeout);
            stopping = disposeKernel ? kernel.DisposeAsync().AsTask()
                : kernel.DeactivateAsync(provider.Manifest.Id).AsTask();
            await cancelled.Task.WaitAsync(Timeout);
            Assert.Null(reader.Services.Resolve<IMarker>());
            Assert.Empty(reader.Services.ResolveAll<IMarker>());
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(Timeout);
            if (stopping is not null) await stopping.WaitAsync(Timeout);
            await kernel.DisposeAsync();
        }

        Assert.Equal(0, activeNotifications);
        Assert.Null(reader.Services.Resolve<IMarker>());
        Assert.Empty(reader.Services.ResolveAll<IMarker>());
    }

    [Fact]
    public async Task Losing_dependency_discards_the_consumers_pending_services()
    {
        await using var kernel = new DougongKernel();
        var reader = new RegistryPlugin("test.reader");
        await kernel.RegisterAsync(() => reader);
        var dependency = new RegistryPlugin("test.dependency", (context, _) =>
        {
            context.Services.Register<IMarker>(new Marker("dependency"), "required");
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => dependency);
        var staged = Signal();
        var cancelled = Signal();
        var release = Signal();
        var consumer = new RegistryPlugin("test.consumer", async (context, token) =>
        {
            context.Services.Register<IMarker>(new Marker("pending"));
            using var subscription = token.Register(() => cancelled.TrySetResult());
            staged.TrySetResult();
            await release.Task;
        }, new ServiceRequirement(typeof(IMarker), "required"));
        var becameActive = false;
        using var observation = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId == consumer.Manifest.Id && change.To == PluginState.Active)
                becameActive = true;
        });
        var registering = kernel.RegisterAsync(() => consumer).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await staged.Task.WaitAsync(Timeout);
            stopping = kernel.DeactivateAsync(dependency.Manifest.Id).AsTask();
            await cancelled.Task.WaitAsync(Timeout);
            Assert.Null(reader.Services.Resolve<IMarker>());
            Assert.Empty(reader.Services.ResolveAll<IMarker>());
            Assert.Null(consumer.Services.Resolve<IMarker>());
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(Timeout);
            if (stopping is not null) await stopping.WaitAsync(Timeout);
        }

        Assert.False(becameActive);
        Assert.Equal(PluginState.Blocked, (await registering).State);
        Assert.Empty(reader.Services.ResolveAll<IMarker>());
    }

    [Fact]
    public async Task Explicit_retry_uses_a_fresh_batch_and_old_registry_cannot_register()
    {
        await using var kernel = new DougongKernel();
        var reader = new RegistryPlugin("test.reader");
        await kernel.RegisterAsync(() => reader);
        var contexts = new List<PluginContext>();
        var provider = new RegistryPlugin("test.retry", (context, _) =>
        {
            contexts.Add(context);
            context.Services.Register<IMarker>(new Marker(contexts.Count == 1 ? "failed" : "current"));
            if (contexts.Count == 1) throw new InvalidOperationException("first activation fails");
            return ValueTask.CompletedTask;
        });
        Assert.Equal(PluginState.Failed, (await kernel.RegisterAsync(() => provider)).State);
        Assert.Null(reader.Services.Resolve<IMarker>());

        Assert.Equal(PluginState.Active, (await kernel.ActivateAsync(provider.Manifest.Id)).State);
        Assert.Equal("current", reader.Services.Resolve<IMarker>()?.Value);
        Assert.Single(reader.Services.ResolveAll<IMarker>());
        Assert.Throws<ObjectDisposedException>(() =>
            contexts[0].Services.Register<IMarker>(new Marker("stale"), "late"));
        Assert.Null(reader.Services.Resolve<IMarker>("late"));
    }

    [Fact]
    public async Task Active_registration_is_rejected_and_new_generation_can_register_again()
    {
        await using var kernel = new DougongKernel();
        var generation = 0;
        var provider = new RegistryPlugin("test.provider", (context, _) =>
        {
            context.Services.Register<IMarker>(new Marker((++generation).ToString()));
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => provider);
        Assert.Throws<InvalidOperationException>(() =>
            provider.Services.Register<IMarker>(new Marker("late"), "late"));
        Assert.Null(provider.Services.Resolve<IMarker>("late"));

        await kernel.DeactivateAsync(provider.Manifest.Id);
        Assert.Null(provider.Services.Resolve<IMarker>());
        await kernel.ActivateAsync(provider.Manifest.Id);
        Assert.Equal("2", provider.Services.Resolve<IMarker>()?.Value);
        Assert.Single(provider.Services.ResolveAll<IMarker>());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private interface IMarker { string Value { get; } }
    private sealed record Marker(string Value) : IMarker;

    private sealed class RegistryPlugin(
        string id,
        Func<PluginContext, CancellationToken, ValueTask>? activate = null,
        params ServiceRequirement[] requirements) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0", requirements);
        public IServiceRegistry Services { get; private set; } = null!;
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Services = context.Services;
            return activate?.Invoke(context, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }
}
