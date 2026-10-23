namespace Dougong.Kernel;

/// <summary>类型化事件总线。它只承载通知，不提供事务、持久化或 exactly-once 保证。</summary>
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);

    void Publish<TEvent>(TEvent evt);
}

/// <summary>
/// 宿主暴露给插件的上下文。清理回调按登记顺序的逆序执行，单个失败不阻止其余清理。
/// 关闭后拒绝新的服务注册、清理登记、事件订阅与发布。
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
    private Task? _cleanupTask;
    private TaskCompletionSource? _cleanupCompletion;
    private int _cancellationRunning;

    internal bool IsCancellationRunning => Volatile.Read(ref _cancellationRunning) != 0;

    private readonly CancellationTokenSource _cleanupCancellation = new();
    private readonly CancellationToken _cleanupToken;
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
        _cleanupToken = _cleanupCancellation.Token;
    }

    public PluginId PluginId { get; }

    public IServiceRegistry Services { get; }

    public IResolver Resolver { get; }

    public IEventBus Events { get; }

    /// <summary>插件寿命令牌；停用开始时取消。</summary>
    public CancellationToken Lifetime => _lifetimeToken;

    /// <summary>清理令牌：宿主清理预算耗尽时取消，协作式 cleanup 可据此提前退出。</summary>
    internal CancellationToken CleanupToken => _cleanupToken;

    /// <summary>收到清理超时通知时取消清理令牌；不等待回调自行退出。</summary>
    internal void CancelCleanup() => _cleanupCancellation.Cancel();

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

    /// <summary>
    /// 关闭服务与事件的入口并发出取消；返回的任务在取消回调全部返回后完成。
    /// 调用方负责给这次等待设置预算。
    /// </summary>
    internal Task RequestStopAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_stopTask is not null) return _stopTask;

            _closed = true;
            Invocations.Close();
            (Events as ScopedEventBus)?.Close();
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

    /// <summary>等待已接受的事件全部处理完毕。</summary>
    internal Task DrainEventsAsync() =>
        _scopedLifetime is ScopedEventBus scoped ? scoped.DrainAsync() : Task.CompletedTask;

    /// <summary>请求内核停用本代次；不绕过内核的排空、预算与隔离规则。</summary>
    public ValueTask DisposeAsync() => _requestDispose(this);

    /// <summary>
    /// 逆序执行已登记清理；同一上下文只执行一次。返回的任务在全部回调返回后完成，
    /// 失败以 <see cref="AggregateException"/> 结束。
    /// </summary>
    internal Task CompleteDisposalAsync()
    {
        lock (_sync)
        {
            if (_cleanupTask is not null) return _cleanupTask;

            var cleanup = _cleanup.ToArray();
            _cleanup.Clear();
            _cleanupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanupTask = _cleanupCompletion.Task;
            _ = DisposeCoreAsync(cleanup, _cleanupCompletion);
            return _cleanupTask;
        }
    }

    private async Task DisposeCoreAsync(
        Func<CancellationToken, ValueTask>[] cleanup,
        TaskCompletionSource completion)
    {
        List<Exception> errors = [];
        for (var i = cleanup.Length - 1; i >= 0; i--)
        {
            try
            {
                await cleanup[i](_cleanupToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        try
        {
            if (_scopedLifetime is not null) await _scopedLifetime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        try { _releaseServices(PluginId, this); }
        catch (Exception ex) { errors.Add(ex); }
        _lifetime.Dispose();
        _cleanupCancellation.Dispose();

        if (errors.Count > 0)
            completion.SetException(new AggregateException($"Plugin '{PluginId}' cleanup failed.", errors));
        else
            completion.SetResult();
    }
}
