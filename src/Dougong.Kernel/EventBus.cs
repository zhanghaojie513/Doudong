namespace Dougong.Kernel;

/// <summary>
/// 异步类型化事件总线。发布者只入队，不执行插件代码；每个订阅有独立有界队列，
/// 队列满时断开该订阅并上报溢出。处理器异常被隔离，不影响其他订阅者。
/// </summary>
internal sealed class EventBus : IEventBus, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<ISubscription>> _handlers = [];
    private readonly Action<Exception> _onHandlerError;
    private readonly int _capacity;

    public EventBus(Action<Exception> onHandlerError, int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _onHandlerError = onHandlerError;
        _capacity = capacity;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new EventSubscription<TEvent>(handler, _capacity, _onHandlerError);
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    public void Publish<TEvent>(TEvent evt)
    {
        ISubscription[] snapshot;
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0) return;
            snapshot = [.. list];
        }

        List<ISubscription>? overflowed = null;
        foreach (var subscription in snapshot)
        {
            if (!subscription.TryPublish(evt)) (overflowed ??= []).Add(subscription);
        }

        if (overflowed is null) return;
        foreach (var subscription in overflowed)
        {
            _onHandlerError(new InvalidOperationException(
                $"事件订阅队列已满（容量 {_capacity}），订阅被断开；订阅方需要重新获取快照。"));
            Remove(subscription);
        }
    }

    private void Remove(ISubscription subscription)
    {
        lock (_sync)
        {
            if (!_handlers.TryGetValue(subscription.EventType, out var list)) return;
            list.Remove(subscription);
            if (list.Count == 0) _handlers.Remove(subscription.EventType);
        }

        subscription.Dispose();
    }

    /// <summary>停止所有订阅并等待已入队事件处理完毕。</summary>
    public async Task DrainAsync()
    {
        ISubscription[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _handlers.Values.SelectMany(list => list)];
            _handlers.Clear();
        }

        foreach (var subscription in snapshot) subscription.Dispose();
        await Task.WhenAll(snapshot.Select(s => s.CloseAsync())).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(DrainAsync());

    internal interface ISubscription : IDisposable, IAsyncDisposable
    {
        Type EventType { get; }
        bool TryPublish(object? evt);
        Task CloseAsync();
    }
}
