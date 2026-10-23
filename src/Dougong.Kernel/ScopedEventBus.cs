namespace Dougong.Kernel;

/// <summary>插件作用域内的事件视图：作用域关闭时自动退订并在必要时等待在途事件。</summary>
internal sealed class ScopedEventBus(IEventBus inner) : IEventBus, IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private int _disposed;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var subscription = inner.Subscribe(handler);
        lock (_subscriptions)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                subscription.Dispose();
                throw new ObjectDisposedException(nameof(ScopedEventBus));
            }

            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish<TEvent>(TEvent evt) => inner.Publish(evt);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        IDisposable[] pending;
        lock (_subscriptions)
        {
            pending = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in pending) subscription.Dispose();
        await Task.WhenAll(pending.OfType<IAsyncDisposable>()
            .Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
