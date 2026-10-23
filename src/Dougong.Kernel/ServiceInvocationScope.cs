namespace Dougong.Kernel;

/// <summary>单一激活代次的调用门禁与排空信号，不持有插件服务对象。</summary>
internal sealed class ServiceInvocationScope
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _inFlight;

    public bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_closed) return false;
            _inFlight++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            _inFlight--;
            if (_closed && _inFlight == 0) _drained.TrySetResult();
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            if (_inFlight == 0) _drained.TrySetResult();
        }
    }

    public Task WhenDrained => _drained.Task;
}
