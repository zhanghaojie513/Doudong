namespace Dougong.Kernel;

/// <summary>
/// 同步类型化事件总线。发布者在调用线程上执行处理器；处理器异常被隔离并上报，
/// 不中断其他观察者或主流程。慢处理器会阻塞发布者，因此处理器只应做有界工作。
/// </summary>
internal sealed class EventBus : IEventBus
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<object>> _handlers = [];
    private readonly Action<Exception> _onHandlerError;

    public EventBus(Action<Exception> onHandlerError) => _onHandlerError = onHandlerError;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(this, typeof(TEvent), handler);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        object[] snapshot;
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<TEvent>)handler)(evt);
            }
            catch (Exception ex)
            {
                _onHandlerError(ex);
            }
        }
    }

    private void Remove(Type eventType, object handler)
    {
        lock (_sync)
        {
            if (_handlers.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _handlers.Remove(eventType);
                }
            }
        }
    }

    private sealed class Subscription(EventBus owner, Type eventType, object handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(eventType, handler);
            }
        }
    }
}

/// <summary>插件作用域内的事件视图：作用域关闭时自动退订，避免插件句柄泄漏。</summary>
internal sealed class ScopedEventBus(IEventBus inner) : IEventBus, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private int _disposed;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        var subscription = inner.Subscribe(handler);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish<TEvent>(TEvent evt) => inner.Publish(evt);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        IDisposable[] pending;
        lock (_subscriptions)
        {
            pending = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in pending)
        {
            subscription.Dispose();
        }
    }
}
