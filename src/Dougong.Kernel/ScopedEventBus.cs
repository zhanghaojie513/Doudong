namespace Dougong.Kernel;

/// <summary>
/// 插件作用域内的事件视图：关闭时停止新订阅与发布，并等待已接受的事件处理完毕。
/// 已手动退订但仍在执行的处理器同样由 <see cref="DrainAsync"/> 覆盖。
/// </summary>
internal sealed class ScopedEventBus : IEventBus, IAsyncDisposable
{
    private readonly IEventBus _inner;
    private readonly Func<IDisposable>? _enterCallback;
    private readonly object _sync = new();
    private readonly List<ScopedSubscription> _subscriptions = [];
    private readonly List<Task> _pendingDrains = [];
    private bool _closed;
    private Task? _drainTask;

    public ScopedEventBus(IEventBus inner, Func<IDisposable>? enterCallback = null)
    {
        _inner = inner;
        _enterCallback = enterCallback;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); }

        IDisposable inner;
        try
        {
            inner = _inner.Subscribe<TEvent>(evt =>
            {
                using var scope = _enterCallback?.Invoke();
                handler(evt);
            });
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(ScopedEventBus));
        }

        var scoped = new ScopedSubscription(inner, OnRemoved);
        lock (_sync)
        {
            if (!_closed)
            {
                _subscriptions.Add(scoped);
                return scoped;
            }
        }

        scoped.Dispose();
        throw new ObjectDisposedException(nameof(ScopedEventBus));
    }

    public void Publish<TEvent>(TEvent evt)
    {
        // 关闭后发布来自过期代次；明确拒绝，避免旧代次事件流入新订阅者。
        lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); }
        _inner.Publish(evt);
    }

    /// <summary>同步封口：关闭后拒绝新的订阅与发布，并立即从总线索引移除本作用域订阅。</summary>
    public void Close()
    {
        ScopedSubscription[] pending;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            pending = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in pending) subscription.Dispose();
    }

    /// <summary>封口并等待已接受事件处理完毕；重复调用返回同一任务。</summary>
    public Task DrainAsync()
    {
        lock (_sync)
        {
            return _drainTask ??= DrainCoreAsync();
        }
    }

    private async Task DrainCoreAsync()
    {
        Close();
        Task[] pending;
        lock (_sync)
        {
            pending = [.. _pendingDrains];
            _pendingDrains.Clear();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(DrainAsync());

    private void OnRemoved(ScopedSubscription subscription, Task completion)
    {
        lock (_sync)
        {
            _subscriptions.Remove(subscription);
            _pendingDrains.Add(completion);
        }
    }

    /// <summary>作用域持有的订阅句柄；退订后仍保留处理器泵的完成信号供排空。</summary>
    private sealed class ScopedSubscription(
        IDisposable inner, Action<ScopedSubscription, Task> onRemoved) : IDisposable, IAsyncDisposable
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Task Completion => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            inner.Dispose();
            onRemoved(this, Completion);
            _ = ObserveAsync();
        }

        private async Task ObserveAsync()
        {
            try
            {
                if (inner is EventBus.ISubscription subscription)
                    await subscription.CloseAsync().ConfigureAwait(false);
                _completion.TrySetResult();
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return new ValueTask(Completion);
        }
    }
}
