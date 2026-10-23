using System.Threading.Channels;

namespace Dougong.Kernel;

/// <summary>带修订号的状态增量订阅。队列满时结束序列并让读取方看到一次重同步信号。</summary>
internal sealed class RevisionSubscription : IDisposable
{
    private readonly DougongKernel _kernel;
    private readonly Channel<StateDelta> _channel;
    private readonly int _capacity;
    private TaskCompletionSource<long>? _overflow;
    private int _disposed;
    private int _overflowing;

    public RevisionSubscription(DougongKernel kernel, Channel<StateDelta> channel, int capacity)
    {
        _kernel = kernel;
        _channel = channel;
        _capacity = capacity;
    }

    public bool TryPublish(StateDelta delta)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (_channel.Writer.TryWrite(delta)) return true;
        if (Interlocked.Exchange(ref _overflowing, 1) != 0) return false;

        var revision = _kernel.MarkRevisionOverflow(this,
            $"状态增量队列已满（容量 {_capacity}），订阅已断开；调用方需重新取快照。");
        // 队列已满时无法再写入哨兵项，改为结束序列并由读取端补发重同步信号。
        _overflow = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _overflow.TrySetResult(revision);
        _channel.Writer.TryComplete();
        Remove();
        return false;
    }

    public async IAsyncEnumerable<StateDelta> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return delta;

        if (_overflow is { } overflow)
            yield return new StateDelta(await overflow.Task.ConfigureAwait(false), null, ResyncRequired: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        Remove();
    }

    private void Remove() => _kernel.RemoveRevisionSubscription(this);
}
