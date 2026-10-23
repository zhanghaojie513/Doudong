using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelDependencyTests
{
    /// <summary>模拟存储服务：用记号值确认消费者拿到的是当前代次的实现。</summary>
    private interface IStorage
    {
        string Marker { get; }
    }

    private sealed class Storage(string marker) : IStorage
    {
        public string Marker { get; } = marker;
    }

    /// <summary>提供存储的插件；可在测试里切换实现内容以区分代次。</summary>
    private sealed class StoragePlugin(string marker, string id = "test.storage") : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IStorage>(new Storage(marker));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>消费者：激活时记录是否拿到依赖，供断言检查“没有带缺失依赖运行”。</summary>
    private sealed class ConsumerPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } =
            PluginManifest.Create("test.consumer", "1.0.0", new ServiceRequirement(typeof(IStorage)));

        public int ActivationCount { get; private set; }

        public string? ObservedMarker { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            ActivationCount++;
            // 依赖缺失时 Resolve 返回 null；测试用它证明消费者不会在缺失状态下被激活。
            ObservedMarker = context.Services.Resolve<IStorage>()?.Marker;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Consumer_waits_in_blocked_when_provider_is_not_registered()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();

        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Equal(0, consumer.ActivationCount);
    }

    [Fact]
    public async Task Registering_provider_activates_blocked_consumer()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();

        await kernel.RegisterAsync(() => consumer);
        await kernel.RegisterAsync(() => new StoragePlugin("A"));

        Assert.Equal(PluginState.Active, (await kernel.GetPluginsAsync())
            .Single(p => p.Id.Value == "test.consumer").State);
        Assert.Equal(1, consumer.ActivationCount);
        Assert.Equal("A", consumer.ObservedMarker);
    }

    [Fact]
    public async Task Deactivating_provider_blocks_consumer_instead_of_restarting_it()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();
        await kernel.RegisterAsync(() => consumer);
        await kernel.RegisterAsync(() => new StoragePlugin("A"));
        Assert.Equal(1, consumer.ActivationCount);

        var provider = await kernel.DeactivateAsync(PluginId.Parse("test.storage"));

        Assert.Equal(PluginState.Stopped, provider.State);
        Assert.Equal(
            PluginState.Blocked,
            (await kernel.GetPluginsAsync()).Single(p => p.Id.Value == "test.consumer").State);
        // 关键回归：消费者没有被带着缺失依赖重新激活。
        Assert.Equal(1, consumer.ActivationCount);
    }

    [Fact]
    public async Task Reactivating_provider_resumes_blocked_consumer()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();
        await kernel.RegisterAsync(() => consumer);
        await kernel.RegisterAsync(() => new StoragePlugin("A"));
        await kernel.DeactivateAsync(PluginId.Parse("test.storage"));

        await kernel.ActivateAsync(PluginId.Parse("test.storage"));

        Assert.Equal(
            PluginState.Active,
            (await kernel.GetPluginsAsync()).Single(p => p.Id.Value == "test.consumer").State);
        Assert.Equal(2, consumer.ActivationCount);
    }

    [Fact]
    public async Task Failing_activation_rolls_back_registered_services()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();
        await kernel.RegisterAsync(() => consumer);

        await kernel.RegisterAsync(() => new FailingProvider());

        var state = (await kernel.GetPluginsAsync()).Single(p => p.Id.Value == "test.broken").State;
        Assert.Equal(PluginState.Failed, state);
        // 失败插件的服务不得残留，否则消费者会拿到半初始化的实例。
        Assert.Equal(PluginState.Blocked, (await kernel.GetPluginsAsync())
            .Single(p => p.Id.Value == "test.consumer").State);
    }

    private sealed class FailingProvider : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.broken", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IStorage>(new Storage("half-initialized"));
            throw new InvalidOperationException("激活失败，用于验证回滚。");
        }
    }

    [Fact]
    public async Task Cleanup_runs_in_reverse_order_and_continues_after_failure()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();

        await kernel.RegisterAsync(() => new CleanupPlugin(order));

        await kernel.DeactivateAsync(PluginId.Parse("test.cleanup"));

        // 逆序执行；中间一个抛错不阻止其余清理。
        Assert.Equal(["third", "second", "first"], order);
        Assert.Equal(PluginState.Failed, (await kernel.GetPluginsAsync()).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.ActivateAsync(PluginId.Parse("test.cleanup")));
        Assert.Equal(["third", "second", "first"], order);
    }

    private sealed class CleanupPlugin(List<string> order) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.cleanup", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.RegisterForCleanup(_ =>
            {
                order.Add("first");
                return ValueTask.CompletedTask;
            });
            context.RegisterForCleanup(_ =>
            {
                order.Add("second");
                throw new InvalidOperationException("清理失败，用于验证继续执行。");
            });
            context.RegisterForCleanup(_ =>
            {
                order.Add("third");
                return ValueTask.CompletedTask;
            });

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Event_subscription_is_removed_when_plugin_scope_is_disposed()
    {
        await using var kernel = new DougongKernel();
        var received = 0;

        var plugin = new PublishingPlugin();
        await kernel.RegisterAsync(() => plugin);

        // 通过消费者拿到同一总线不方便，这里直接验证作用域退订：停用后不再收到事件。
        await kernel.DeactivateAsync(plugin.Manifest.Id);

        Assert.Equal(0, received);

        // 事件总线在插件作用域外不可达，这里用插件内部计数确认其订阅已随作用域释放。
        plugin.PublishAfterDispose();

        Assert.Equal(0, plugin.ReceivedAfterPublish);
    }

    private sealed class PublishingPlugin : IPlugin
    {
        private IEventBus? _events;
        private int _received;

        public PluginManifest Manifest { get; } = PluginManifest.Create("test.publisher", "1.0.0");

        public int ReceivedAfterPublish => _received;

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            _events = context.Events;
            context.Events.Subscribe<int>(_ => _received++);
            return ValueTask.CompletedTask;
        }

        /// <summary>停用后发布；若作用域退订正确，计数不应增加。</summary>
        public void PublishAfterDispose() => _events?.Publish(1);
    }

    [Fact]
    public async Task Duplicate_plugin_registration_is_rejected()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new StoragePlugin("A"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.RegisterAsync(() => new StoragePlugin("B")));
    }

    [Fact]
    public async Task Ambiguous_providers_leave_consumer_blocked()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin();

        await kernel.RegisterAsync(() => new StoragePlugin("B", "test.storage.b"));
        await kernel.RegisterAsync(() => new StoragePlugin("A", "test.storage.a"));
        await kernel.RegisterAsync(() => consumer);

        // 两个提供方同键且需求未命名 -> 歧义，不猜。
        Assert.Equal(PluginState.Blocked, (await kernel.GetPluginsAsync())
            .Single(p => p.Id.Value == "test.consumer").State);
    }

    [Fact]
    public async Task Cleanup_can_query_kernel_during_stop_rollback_and_disposal()
    {
        var kernel = new DougongKernel();
        var snapshots = new List<PluginState>();
        var id = PluginId.Parse("test.reentrant");
        var plugin = new CallbackPlugin(id, (context, _) =>
        {
            context.RegisterForCleanup(async _ =>
            {
                var plugins = await kernel.GetPluginsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                snapshots.Add(plugins.Single(p => p.Id == id).State);
            });
            return ValueTask.CompletedTask;
        });

        try
        {
            await kernel.RegisterAsync(() => plugin);
            await kernel.DeactivateAsync(id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await kernel.ActivateAsync(id);
            await kernel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await kernel.DisposeAsync();

            await using var rollbackKernel = new DougongKernel();
            var broken = new CallbackPlugin(PluginId.Parse("test.reentrant.broken"), (context, _) =>
            {
                context.RegisterForCleanup(async _ =>
                {
                    var plugins = await rollbackKernel.GetPluginsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    snapshots.Add(plugins.Single().State);
                });
                throw new InvalidOperationException("rollback");
            });
            var failed = await rollbackKernel.RegisterAsync(() => broken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PluginState.Failed, failed.State);
            Assert.Equal([PluginState.Deactivating, PluginState.Deactivating, PluginState.Deactivating], snapshots);
        }
        finally
        {
            await kernel.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_activation_retries_only_after_explicit_activate()
    {
        await using var kernel = new DougongKernel();
        var attempts = 0;
        var id = PluginId.Parse("test.retry");
        var broken = new CallbackPlugin(id, (_, _) =>
        {
            attempts++;
            throw new InvalidOperationException("activation");
        });

        var failed = await kernel.RegisterAsync(() => broken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PluginState.Failed, failed.State);
        Assert.Equal(1, attempts);
        await kernel.RegisterAsync(() => new ConsumerPlugin());
        Assert.Equal(1, attempts);

        var retried = await kernel.ActivateAsync(id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PluginState.Failed, retried.State);
        Assert.Equal(2, attempts);
        await kernel.RegisterAsync(() => new StoragePlugin("A"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Deactivating_during_activation_rejects_late_registration_and_active_commit()
    {
        await using var kernel = new DougongKernel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = PluginId.Parse("test.slow");
        var seen = new List<PluginState>();
        Exception? lateRegistrationError = null;
        var cleanupCount = 0;
        var plugin = new CallbackPlugin(id, async (context, token) =>
        {
            context.Services.Register<IStorage>(new Storage("early"));
            context.RegisterForCleanup(_ =>
            {
                cleanupCount++;
                return ValueTask.CompletedTask;
            });
            using var subscription = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await release.Task;
            lateRegistrationError = Record.Exception(() =>
                context.Services.Register<IStorage>(new Storage("late"), "late"));
        });
        using var observer = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId == id) seen.Add(change.To);
        });

        var registering = kernel.RegisterAsync(() => plugin).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = kernel.DeactivateAsync(id).AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PluginState.Deactivating, (await kernel.GetPluginsAsync()).Single().State);
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(TimeSpan.FromSeconds(5));
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsType<ObjectDisposedException>(lateRegistrationError);
        Assert.DoesNotContain(PluginState.Active, seen);
        Assert.Equal(1, cleanupCount);
        Assert.Equal(PluginState.Stopped, (await kernel.GetPluginsAsync()).Single().State);
        var consumer = await kernel.RegisterAsync(() => new ConsumerPlugin());
        Assert.Equal(PluginState.Blocked, consumer.State);
    }

    private sealed class CallbackPlugin(
        PluginId id, Func<PluginContext, CancellationToken, ValueTask> activate) : IPlugin
    {
        public PluginManifest Manifest { get; } = new(id, "1.0.0");
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            activate(context, cancellationToken);
    }

    [Fact]
    public async Task Observer_exception_does_not_stop_state_propagation()
    {
        await using var kernel = new DougongKernel();
        var seen = new List<PluginState>();

        using var bad = kernel.SubscribeStateChanges(_ => throw new InvalidOperationException("观察者失败"));
        using var good = kernel.SubscribeStateChanges(c => seen.Add(c.To));

        await kernel.RegisterAsync(() => new StoragePlugin("A"));

        Assert.Contains(PluginState.Active, seen);
        Assert.NotNull(kernel.LastObserverError);
    }
}
