using System.Threading.Channels;

namespace Dougong.Kernel;

/// <summary>
/// 单个订阅独占的有界投递队列。发布只入队，用户代码在独立泵上执行；
/// 队列满时本次投递失败，由总线断开该订阅并报告溢出。
/// </summary>
internal sealed class EventSubscription<TEvent> : EventBus.ISubscription, IAsyncDisposable
{
    private readonly Channel<TEvent> _channel;
    private readonly Action<TEvent> _handler;
    private readonly Action<Exception> _onError;
    private readonly Task _pump;
    private int _disposed;

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

    /// <summary>非阻塞入队；已关闭或队列已满时返回 false。</summary>
    public bool TryPublish(TEvent evt) => !IsClosed && _channel.Writer.TryWrite(evt);

    bool EventBus.ISubscription.TryPublish(object? evt) => evt is TEvent typed && TryPublish(typed);

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _pump.ConfigureAwait(false);
            return;
        }

        _channel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    public void Dispose() => _ = CloseAsync();

    public ValueTask DisposeAsync() => new(CloseAsync());
}
