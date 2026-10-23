using System.Threading.Channels;

namespace Dougong.Kernel;

/// <summary>
/// 单个订阅独占的有界投递队列。发布只入队，用户代码在独立泵上执行；
/// 队列满时本次投递失败，由总线断开该订阅并报告溢出。
/// </summary>
internal sealed class EventSubscription<TEvent>
    : EventBus.ISubscription, EventBus.EventSubscriptionMarker, IAsyncDisposable
{
    private readonly Channel<TEvent> _channel;
    private readonly Action<TEvent> _handler;
    private readonly Action<Exception> _onError;
    private readonly Task _pump;
    private Action? _disposer;
    private int _disposed;
    private int _overflowing;

    public EventSubscription(
        Action<TEvent> handler,
        int capacity,
        Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _handler = handler;
        _onError = onError;
        _channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    public Type EventType => typeof(TEvent);

    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    /// <summary>由总线设置；让退订能立即从总线索引移除，而不只是关闭队列。</summary>
    internal void SetDisposer(Action disposer) => _disposer = disposer;

    bool EventBus.EventSubscriptionMarker.TryBeginOverflow() => TryBeginOverflow();

    /// <summary>
    /// 抢占一次溢出报告。并发发布可能同时看到同一订阅队列已满，
    /// 但只允许一个调用者报告并断开该订阅。
    /// </summary>
    internal bool TryBeginOverflow() => Interlocked.Exchange(ref _overflowing, 1) == 0;

    /// <summary>非阻塞入队；已关闭或队列已满时返回 false。</summary>
    public bool TryPublish(TEvent evt) => !IsClosed && _channel.Writer.TryWrite(evt);

    bool EventBus.ISubscription.TryPublish(object? evt)
    {
        if (evt is null)
            return default(TEvent) is null && TryPublish(default!);
        return evt is TEvent typed && TryPublish(typed);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var evt))
                {
                    try { _handler(evt); }
                    catch (Exception ex) { _onError(ex); }
                }
            }
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    /// <summary>停止入队并等待已入队事件处理完毕。</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _channel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    public void Dispose()
    {
        var disposer = _disposer;
        if (disposer is not null) disposer();
        else _ = CloseAsync();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
