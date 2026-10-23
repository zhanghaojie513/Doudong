namespace Dougong.Kernel;

/// <summary>类型化事件总线。它只承载通知，不提供事务、持久化或 exactly-once 保证。</summary>
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);

    void Publish<TEvent>(TEvent evt);
}

/// <summary>
/// 宿主暴露给插件的上下文。清理回调按登记顺序的逆序执行，单个失败不阻止其余清理。
/// 关闭后拒绝新的服务注册与清理登记。
/// </summary>
public sealed class PluginContext : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<Func<CancellationToken, ValueTask>> _cleanup = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly IAsyncDisposable? _scopedLifetime;
    private readonly Action<PluginId, PluginContext> _releaseServices;
    private bool _closed;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _cancellationRunning;

    internal bool IsCancellationRunning => Volatile.Read(ref _cancellationRunning) != 0;

    private readonly Func<PluginContext, ValueTask> _requestDispose;
    internal ServiceInvocationScope Invocations { get; } = new();

    internal PluginContext(
        PluginId pluginId,
        IServiceRegistry services,
        IResolver resolver,
        IEventBus events,
        IAsyncDisposable? scopedLifetime,
        Action<PluginId, PluginContext> releaseServices,
        Func<PluginContext, ValueTask> requestDispose)
    {
        PluginId = pluginId;
        Services = services;
        Resolver = resolver;
        _requestDispose = requestDispose;
        Events = events;
        _scopedLifetime = scopedLifetime;
        _releaseServices = releaseServices;
        _lifetimeToken = _lifetime.Token;
    }

    public PluginId PluginId { get; }

    public IServiceRegistry Services { get; }

    public IResolver Resolver { get; }

    public IEventBus Events { get; }

    /// <summary>插件寿命令牌；停用开始时取消。</summary>
    public CancellationToken Lifetime => _lifetimeToken;

    internal bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    public void RegisterForCleanup(Func<CancellationToken, ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _cleanup.Add(cleanup);
        }
    }

    internal Task RequestStopAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _closed = true;
            Invocations.Close();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        _ = CancelLifetimeAsync(completion);
        return completion.Task;
    }

    private async Task CancelLifetimeAsync(TaskCompletionSource completion)
    {
        Interlocked.Exchange(ref _cancellationRunning, 1);
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            Volatile.Write(ref _cancellationRunning, 0);
        }
    }

    /// <summary>请求内核停用本代次，取消并排空调用后逆序清理。</summary>
    public ValueTask DisposeAsync() => _requestDispose(this);

    internal ValueTask CompleteDisposalAsync()
    {
        TaskCompletionSource completion;
        Func<CancellationToken, ValueTask>[] cleanup;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _closed = true;
            cleanup = [.. _cleanup];
            _cleanup.Clear();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeCoreAsync(cleanup, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(
        Func<CancellationToken, ValueTask>[] cleanup,
        TaskCompletionSource completion)
    {
        List<Exception> errors = [];
        try
        {
            await RequestStopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        await Invocations.WhenDrained.ConfigureAwait(false);
        for (var i = cleanup.Length - 1; i >= 0; i--)
        {
            try
            {
                await cleanup[i](CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        try { if (_scopedLifetime is not null) await _scopedLifetime.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try { _releaseServices(PluginId, this); }
        catch (Exception ex) { errors.Add(ex); }
        _lifetime.Dispose();

        if (errors.Count > 0)
        {
            completion.SetException(new AggregateException($"Plugin '{PluginId}' cleanup failed.", errors));
        }
        else
        {
            completion.SetResult();
        }
    }
}
