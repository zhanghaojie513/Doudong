using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelEventBusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class BusPlugin(Action<PluginContext> activate) : IPlugin
    {
        public string Id { get; } = $"test.bus.{Guid.NewGuid():N}";
        public PluginManifest Manifest => PluginManifest.Create(Id, "1.0.0");
        public PluginContext? Context { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            activate(context);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Publish_does_not_run_handler_on_publisher_thread()
    {
        await using var kernel = new DougongKernel();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerThread = 0;
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(_ =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            gate.TrySetResult();
        }));
        await kernel.RegisterAsync(() => plugin);

        // 断言的意图是"处理器不在发布者的线程上运行"，即 Publish 不同步调用处理器。
        // 不能只比较线程 ID 后就在线程池线程上发布：事件泵跑在线程池上，
        // 线程池可能把发布线程复用于泵回调，"两线程不同"因此不是稳定性质
        // （xunit v3 并行执行时会稳定撞上）。
        // 也不能断言"Publish 返回时处理器尚未运行"：Publish 是投递，
        // 泵与发布并发推进，处理器在 Publish 返回前开始运行是合法的。
        // 用一条专用非线程池线程发布，处理器则仍在线程池的泵上执行，
        // 这样"不同线程"才是可依赖的性质。
        var publisherThread = 0;
        var publishReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var publisher = new Thread(() =>
        {
            publisherThread = Environment.CurrentManagedThreadId;
            plugin.Context!.Events.Publish(1);
            publishReturned.TrySetResult();
        })
        {
            IsBackground = true,
        };

        publisher.Start();
        await publishReturned.Task.WaitAsync(Timeout);
        await gate.Task.WaitAsync(Timeout);

        Assert.NotEqual(publisherThread, handlerThread);
    }

    [Fact]
    public async Task Slow_handler_does_not_block_publisher()
    {
        await using var kernel = new DougongKernel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(_ =>
        {
            release.Task.GetAwaiter().GetResult();
        }));
        await kernel.RegisterAsync(() => plugin);

        var publishing = Task.Run(() => plugin.Context!.Events.Publish(1));
        await publishing.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
    }

    [Fact]
    public async Task Handler_exception_is_isolated_and_reported()
    {
        await using var kernel = new DougongKernel();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new BusPlugin(context =>
        {
            context.Events.Subscribe<int>(_ => throw new InvalidOperationException("handler failed"));
            context.Events.Subscribe<int>(_ => second.TrySetResult());
        });
        await kernel.RegisterAsync(() => plugin);

        plugin.Context!.Events.Publish(1);
        await second.Task.WaitAsync(Timeout);
        // 两个订阅各有独立泵，成功处理器先完成不代表失败处理器已上报。
        await WaitUntilAsync(() => kernel.LastObserverDiagnostic is not null);

        Assert.NotNull(kernel.LastObserverDiagnostic);
    }

    [Fact]
    public async Task Subscription_is_removed_when_plugin_is_deactivated()
    {
        await using var kernel = new DougongKernel();
        var received = 0;
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(_ => Interlocked.Increment(ref received)));
        await kernel.RegisterAsync(() => plugin);
        plugin.Context!.Events.Publish(1);
        await WaitUntilAsync(() => Volatile.Read(ref received) == 1);

        await kernel.DeactivateAsync(plugin.Manifest.Id);
        // 停用会退订并排空；旧代次不得再把事件送进总线。
        Assert.Throws<ObjectDisposedException>(() => plugin.Context!.Events.Publish(2));
        await Task.Delay(150);

        Assert.Equal(1, Volatile.Read(ref received));
    }

    [Fact]
    public async Task Overflow_disconnects_subscriber_and_bus_keeps_working()
    {
        await using var kernel = new DougongKernel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(_ =>
        {
            slowStarted.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        await kernel.RegisterAsync(() => plugin);

        plugin.Context!.Events.Publish(0);
        await slowStarted.Task.WaitAsync(Timeout);
        for (var i = 0; i < 600; i++) plugin.Context!.Events.Publish(i);

        await WaitUntilAsync(() => kernel.LastObserverDiagnostic is not null);
        Assert.Contains("队列已满", kernel.LastObserverDiagnostic!.Message);

        var afterOverflow = 0;
        using var fresh = plugin.Context!.Events.Subscribe<int>(_ => Interlocked.Increment(ref afterOverflow));
        plugin.Context!.Events.Publish(1);
        await WaitUntilAsync(() => Volatile.Read(ref afterOverflow) == 1);

        release.TrySetResult();
    }

    [Fact]
    public async Task Kernel_disposal_waits_for_queued_events()
    {
        var kernel = new DougongKernel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(_ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref completed);
        }));
        await kernel.RegisterAsync(() => plugin);

        plugin.Context!.Events.Publish(1);
        await started.Task.WaitAsync(Timeout);

        var disposing = kernel.DisposeAsync().AsTask();
        var notCompleted = await Task.WhenAny(disposing, Task.Delay(150)) != disposing;
        Assert.True(notCompleted, "内核销毁不应在处理完已入队事件前完成。");

        release.TrySetResult();
        await disposing.WaitAsync(Timeout);
        Assert.Equal(1, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Reentrant_publish_from_handler_is_delivered()
    {
        await using var kernel = new DougongKernel();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new BusPlugin(context => context.Events.Subscribe<int>(evt =>
        {
            if (evt == 1) context.Events.Publish(2);
            if (evt == 2) second.TrySetResult();
        }));
        await kernel.RegisterAsync(() => plugin);

        plugin.Context!.Events.Publish(1);
        await second.Task.WaitAsync(Timeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时。");
    }
}
