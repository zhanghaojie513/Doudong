using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class EventBusLifecycleRegressionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int ChannelCount(EventBus bus) =>
        ((IDictionary)typeof(EventBus).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bus)!).Count;

    [Fact]
    public async Task Manual_unsubscribe_removes_empty_channel_immediately_without_overflow()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var bus = new EventBus(errors.Enqueue);
        var subscription = bus.Subscribe<int>(_ => { });

        subscription.Dispose();
        bus.Publish(1);

        Assert.Equal(0, ChannelCount(bus));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Removing_one_subscription_preserves_other_handlers_and_releases_last_channel()
    {
        await using var bus = new EventBus(_ => { });
        var delivered = Signal();
        var first = bus.Subscribe<int>(_ => { });
        var second = bus.Subscribe<int>(_ => delivered.TrySetResult());

        first.Dispose();
        Assert.Equal(1, ChannelCount(bus));
        bus.Publish(1);
        await delivered.Task.WaitAsync(Timeout);
        second.Dispose();

        Assert.Equal(0, ChannelCount(bus));
    }

    [Fact]
    public async Task Null_reference_and_nullable_value_events_are_delivered()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var bus = new EventBus(errors.Enqueue);
        var reference = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nullable = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe<string?>(value => reference.TrySetResult(value));
        bus.Subscribe<int?>(value => nullable.TrySetResult(value));

        bus.Publish<string?>(null);
        bus.Publish<int?>(null);
        await bus.DrainAsync().WaitAsync(Timeout);

        Assert.True(reference.Task.IsCompletedSuccessfully, "null 引用事件必须入队。");
        Assert.True(nullable.Task.IsCompletedSuccessfully, "null 可空值事件必须入队。");
        Assert.Null(await reference.Task);
        Assert.Null(await nullable.Task);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Overflow_is_reported_once_and_immediately_releases_channel()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var bus = new EventBus(errors.Enqueue, capacity: 1);
        var started = Signal();
        var release = Signal();
        bus.Subscribe<int>(_ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            bus.Publish(1);
            await started.Task.WaitAsync(Timeout);
            bus.Publish(2);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => bus.Publish(i))));

            Assert.Single(errors);
            Assert.Contains("队列已满", errors.Single().Message);
            Assert.Equal(0, ChannelCount(bus));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Bus_drain_waits_for_manually_unsubscribed_pump()
    {
        await using var bus = new EventBus(_ => { });
        var started = Signal();
        var release = Signal();
        var completed = 0;
        var subscription = bus.Subscribe<int>(_ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref completed);
        });
        Task drain;
        try
        {
            bus.Publish(1);
            await started.Task.WaitAsync(Timeout);
            subscription.Dispose();
            drain = bus.DrainAsync();
            Assert.False(drain.IsCompleted, "总线必须等待已退订但仍在执行的 pump。");
        }
        finally { release.TrySetResult(); }

        await drain.WaitAsync(Timeout);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Scope_repeated_dispose_shares_pending_drain_even_after_manual_unsubscribe()
    {
        await using var bus = new EventBus(_ => { });
        var scope = new ScopedEventBus(bus);
        var started = Signal();
        var release = Signal();
        var subscription = scope.Subscribe<int>(_ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        Task first;
        try
        {
            bus.Publish(1);
            await started.Task.WaitAsync(Timeout);
            subscription.Dispose();
            first = scope.DisposeAsync().AsTask();
            var second = scope.DisposeAsync().AsTask();

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted, "重复 Dispose 必须等待同一在途回调。");
            Assert.Same(first, second);
        }
        finally { release.TrySetResult(); }

        await first.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Disposed_scope_rejects_publish_and_subscribe()
    {
        await using var bus = new EventBus(_ => { });
        var scope = new ScopedEventBus(bus);
        await scope.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => scope.Publish(1));
        Assert.Throws<ObjectDisposedException>(() => scope.Subscribe<int>(_ => { }));
    }

    [Fact]
    public async Task Drained_bus_rejects_publish_and_subscribe()
    {
        await using var bus = new EventBus(_ => { });
        await bus.DrainAsync();

        Assert.Throws<ObjectDisposedException>(() => bus.Publish(1));
        Assert.Throws<ObjectDisposedException>(() => bus.Subscribe<int>(_ => { }));
    }
}
