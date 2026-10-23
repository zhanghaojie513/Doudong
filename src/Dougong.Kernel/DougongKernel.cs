namespace Dougong.Kernel;

internal sealed record ServiceKey(Type ServiceType, string? Name);

internal sealed class ServiceTable
{
    private readonly object _sync = new();
    private readonly Dictionary<ServiceKey, List<ServiceEntry>> _entries = [];

    internal sealed record ServiceEntry(PluginId Owner, long Generation, object Instance);

    public void Publish(PluginId owner, long generation, IReadOnlyDictionary<ServiceKey, object> services)
    {
        lock (_sync)
        {
            foreach (var (key, instance) in services)
            {
                if (!_entries.TryGetValue(key, out var list))
                {
                    list = [];
                    _entries[key] = list;
                }

                list.Add(new ServiceEntry(owner, generation, instance));
            }
        }
    }

    public IReadOnlyList<ServiceEntry> Find(Type serviceType, string? name)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(new ServiceKey(serviceType, name), out var list) ? [.. list] : [];
        }
    }

    public void RemoveOwner(PluginId owner, long generation)
    {
        lock (_sync)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                var list = _entries[key];
                list.RemoveAll(e => e.Owner == owner && e.Generation == generation);
                if (list.Count == 0) _entries.Remove(key);
            }
        }
    }

    public bool IsSatisfied(ServiceRequirement requirement, out string? reason)
    {
        var matches = Find(requirement.ServiceType, requirement.Name);
        reason = matches.Count switch
        {
            1 => null,
            0 => $"未找到服务 {requirement.ServiceType.Name}({requirement.Name})。",
            _ => $"服务 {requirement.ServiceType.Name} 存在 {matches.Count} 个提供方，需求不唯一。",
        };
        return matches.Count == 1;
    }
}

internal sealed class PluginEntry
{
    public required PluginId Id { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required Func<IPlugin> Factory { get; init; }
    public PluginState State { get; set; } = PluginState.Registered;
    public bool DesiredActive { get; set; }
    public bool CleanupFailed { get; set; }
    public bool DrainTimedOut { get; set; }
    public long ActivationSequence { get; set; }
    public long Generation { get; set; }
    public PluginContext? Context { get; set; }
    public Dictionary<ServiceKey, object> PendingServices { get; } = [];
}

/// <summary>
/// 插件内核。状态由短临界区保护，生命周期回调由重评估器串行执行。
/// 插件回调可以查询状态；生命周期变更须由宿主在回调之外发起。
/// </summary>
public sealed partial class DougongKernel : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly Dictionary<PluginId, PluginEntry> _entries = [];
    private readonly ServiceTable _services = new();
    private readonly List<Action<PluginStateChange>> _stateObservers = [];
    private readonly EventBus _events;
    private readonly AsyncLocal<CallbackScope?> _callback = new();
    private bool _disposed;
    private Task? _disposeTask;
    private long _activationSequence;

    public DougongKernel()
    {
        _events = new EventBus(OnEventHandlerError);
        Resolver = new ServiceResolver(this);
    }

    public IResolver Resolver { get; }

    /// <summary>等待受影响批次在途调用结束的预算；超时后不执行清理并保留诊断。</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>最近一次被隔离的观察者/事件处理器异常，供宿主记录诊断。</summary>
    public Exception? LastObserverError { get; private set; }

    public IDisposable SubscribeStateChanges(Action<PluginStateChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_stateObservers) { _stateObservers.Add(observer); }
        return new StateSubscription(this, observer);
    }

    public ValueTask<IReadOnlyList<PluginHandle>> GetPluginsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<PluginHandle>>([.. _entries.Values.Select(ToHandle)]);
        }
    }

    public async ValueTask<PluginHandle> RegisterAsync(Func<IPlugin> factory, CancellationToken cancellationToken = default)
    {
        EnsureLifecycleCallAllowed();
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = factory()?.Manifest ?? throw new InvalidOperationException("插件工厂返回 null。");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.ContainsKey(manifest.Id))
                throw new InvalidOperationException($"插件 '{manifest.Id}' 已登记。");
            if (FindDependencyCycle(manifest.Id, manifest) is { } cycle)
                throw new InvalidOperationException(
                    $"插件依赖成环：{string.Join(" -> ", cycle)}。");
            _entries.Add(manifest.Id, new PluginEntry
            {
                Id = manifest.Id, Manifest = manifest, Factory = factory, DesiredActive = true,
            });
        }

        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        return GetHandle(manifest.Id);
    }

    public async ValueTask<PluginHandle> ActivateAsync(PluginId id, CancellationToken cancellationToken = default)
    {
        EnsureLifecycleCallAllowed();
        cancellationToken.ThrowIfCancellationRequested();
        List<PluginStateChange> changes = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = RequireEntry(id);
            if (entry.CleanupFailed)
                throw new InvalidOperationException($"插件 '{id}' 清理失败，不能再次激活。");
            entry.DesiredActive = true;
            if (entry.State == PluginState.Failed)
                SetState(entry, changes, PluginState.Stopped, "宿主显式请求重试。");
        }
        PublishChanges(changes);
        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        return GetHandle(id);
    }

    public ValueTask<PluginHandle> DeactivateAsync(PluginId id, CancellationToken cancellationToken = default) =>
        DeactivateGenerationAsync(id, null, cancellationToken);

    private async ValueTask<PluginHandle> DeactivateGenerationAsync(
        PluginId id, PluginContext? expectedContext, CancellationToken cancellationToken)
    {
        EnsureLifecycleCallAllowed();
        cancellationToken.ThrowIfCancellationRequested();
        List<PluginContext> contexts = [];
        List<PluginStateChange> changes = [];
        lock (_sync)
        {
            var entry = RequireEntry(id);
            if (expectedContext is not null && !ReferenceEquals(entry.Context, expectedContext))
                return ToHandle(entry);
            ObjectDisposedException.ThrowIf(_disposed && expectedContext is null, this);
            entry.DesiredActive = false;
            WithdrawServices(entry);
            if (entry.Context is { } context)
            {
                contexts.Add(context);
                SetState(entry, changes, PluginState.Deactivating, "用户停用。");
            }

            // 在等待重评估前传播依赖失效，否则消费者激活可能一直等待自己的寿命令牌。
            bool invalidated;
            do
            {
                invalidated = false;
                foreach (var dependent in _entries.Values)
                {
                    if (dependent.Context is not { } dependentContext
                        || dependent.State == PluginState.Deactivating) continue;
                    var missing = MissingRequirements(dependent);
                    if (missing.Count == 0) continue;
                    WithdrawServices(dependent);
                    SetState(dependent, changes, PluginState.Deactivating, string.Join(" ", missing));
                    contexts.Add(dependentContext);
                    invalidated = true;
                }
            } while (invalidated);
        }
        PublishChanges(changes);

        // 取消不能等待重评估许可：正在激活的回调可能正等待寿命令牌取消。
        await Task.WhenAll(contexts.Select(RequestStopQuietlyAsync)).ConfigureAwait(false);
        await EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        return GetHandle(id);
    }

    private async ValueTask EvaluateAsync(CancellationToken cancellationToken)
    {
        await _reconcile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                PluginEntry? selected = null;
                PluginContext? context = null;
                var stopping = false;
                List<PluginStateChange> changes = [];
                lock (_sync)
                {
                    var stopNeeded = false;
                    foreach (var entry in _entries.Values)
                    {
                        if (entry.Context is null || entry.DrainTimedOut) continue;
                        var missing = MissingRequirements(entry);
                        if (entry.DesiredActive && !entry.Context.IsClosed
                            && !entry.Context.Invocations.IsClosed
                            && entry.State != PluginState.Deactivating && missing.Count == 0) continue;
                        stopNeeded = true;
                        break;
                    }

                    if (stopNeeded)
                    {
                        selected = null;
                    }
                    else
                    {
                        foreach (var entry in _entries.Values)
                        {
                            if (entry.Context is not null || entry.State == PluginState.Failed) continue;
                            if (!entry.DesiredActive)
                            {
                                if (entry.State != PluginState.Stopped)
                                    SetState(entry, changes, PluginState.Stopped, "已停止。");
                                continue;
                            }
                            var missing = MissingRequirements(entry);
                            if (missing.Count > 0)
                            {
                                if (entry.State != PluginState.Blocked)
                                    SetState(entry, changes, PluginState.Blocked, string.Join(" ", missing));
                                continue;
                            }
                            selected = entry;
                            entry.PendingServices.Clear();
                            entry.Generation++;
                            entry.ActivationSequence = ++_activationSequence;
                            var events = new ScopedEventBus(_events);
                            context = new PluginContext(entry.Id,
                                new ScopedRegistry(this, entry.Id, entry.Generation),
                                new ServiceResolver(this, entry.Id, entry.Generation), events, events,
                                static (_, _) => { }, DisposePluginContextAsync);
                            entry.Context = context;
                            SetState(entry, changes, PluginState.Activating, "开始激活。");
                            break;
                        }
                    }

                    stopping = stopNeeded;
                }

                PublishChanges(changes);
                if (stopping)
                {
                    await StopBatchAsync().ConfigureAwait(false);
                    continue;
                }

                if (selected is null) return;
                await RunActivationAsync(selected, context!).ConfigureAwait(false);
            }
        }
        finally { _reconcile.Release(); }
    }

    private async ValueTask RunActivationAsync(PluginEntry entry, PluginContext context)
    {
        var generation = entry.Generation;
        Exception? activationError = null;
        using (EnterCallback(context))
        {
            try
            {
                var plugin = entry.Factory();
                await plugin.ActivateAsync(context, context.Lifetime).ConfigureAwait(false);
            }
            catch (Exception ex) { activationError = ex; }
        }

        List<PluginStateChange> changes = [];
        bool accepted;
        bool stopped;
        lock (_sync)
        {
            stopped = _disposed || !entry.DesiredActive || context.IsClosed
                || entry.State == PluginState.Deactivating || MissingRequirements(entry).Count > 0;
            accepted = activationError is null && !stopped && entry.Generation == generation
                && ReferenceEquals(entry.Context, context) && entry.State == PluginState.Activating;
            if (accepted)
            {
                _services.Publish(entry.Id, generation, entry.PendingServices);
                entry.PendingServices.Clear();
                SetState(entry, changes, PluginState.Active, "激活完成，服务已提交。");
            }
            else
            {
                WithdrawServices(entry);
                SetState(entry, changes, PluginState.Deactivating, "激活未提交，开始清理。");
            }
        }
        PublishChanges(changes);
        if (accepted) return;

        await RequestStopQuietlyAsync(context).ConfigureAwait(false);
        try
        {
            await context.Invocations.WhenDrained.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(entry.Context, context))
                {
                    entry.DrainTimedOut = true;
                    SetState(entry, changes, PluginState.Failed,
                        $"激活回滚时服务调用未在 {DrainTimeout} 内结束；不执行清理，需重启宿主。",
                        new TimeoutException("激活回滚排空超时。"));
                }
            }
            PublishChanges(changes);
            return;
        }
        var cleanupError = await DisposeContextAsync(context).ConfigureAwait(false);
        changes = [];
        lock (_sync)
        {
            if (entry.Generation != generation || !ReferenceEquals(entry.Context, context)) return;
            entry.Context = null;
            entry.CleanupFailed = cleanupError is not null;
            Exception? error = activationError is not null && cleanupError is not null
                ? new AggregateException(activationError, cleanupError) : activationError ?? cleanupError;
            SetState(entry, changes, cleanupError is not null || (!stopped && activationError is not null)
                ? PluginState.Failed : PluginState.Stopped,
                error is null ? "已停止。" : "激活未提交；注册已撤销。", error);
        }
        PublishChanges(changes);
    }

    private async ValueTask StopAsync(PluginEntry entry, PluginContext context)
    {
        var generation = entry.Generation;
        var error = await DisposeContextAsync(context).ConfigureAwait(false);
        List<PluginStateChange> changes = [];
        lock (_sync)
        {
            if (entry.Generation != generation || !ReferenceEquals(entry.Context, context)) return;
            entry.Context = null;
            entry.CleanupFailed = error is not null;
            SetState(entry, changes, error is null ? PluginState.Stopped : PluginState.Failed,
                error is null ? "已停止。" : "清理失败，不能再次激活。", error);
        }
        PublishChanges(changes);
    }

    private async Task RequestStopQuietlyAsync(PluginContext context)
    {
        using (EnterCallback(context))
        {
            try { await context.RequestStopAsync().ConfigureAwait(false); }
            catch (Exception) { /* DisposeAsync 会再次观察并聚合取消回调异常。 */ }
        }
    }

    private async ValueTask<Exception?> DisposeContextAsync(PluginContext context)
    {
        using (EnterCallback(context))
        {
            try { await context.CompleteDisposalAsync().ConfigureAwait(false); return null; }
            catch (Exception ex) { return ex; }
        }
    }

    private List<string> MissingRequirements(PluginEntry entry)
    {
        List<string> missing = [];
        foreach (var requirement in entry.Manifest.RequiredServices)
        {
            if (requirement.Optional) continue;
            if (!_services.IsSatisfied(requirement, out var reason)) missing.Add(reason!);
        }

        foreach (var dependency in entry.Manifest.DependsOn)
        {
            if (!_entries.TryGetValue(dependency, out var provider))
                missing.Add($"未登记插件依赖 {dependency}。");
            else if (provider.State != PluginState.Active)
                missing.Add($"插件依赖 {dependency} 尚未激活（当前 {provider.State}）。");
        }

        return missing;
    }

    /// <summary>检测加入 <paramref name="pending"/> 后是否形成插件依赖环，返回完整路径。</summary>
    private List<PluginId>? FindDependencyCycle(PluginId start, PluginManifest pending)
    {
        HashSet<PluginId> customSet = [pending.Id];
        HashSet<PluginId> onPath = [];
        List<PluginId> path = [];
        return Walk(start) ? [.. path] : null;

        IEnumerable<PluginId> DependenciesOf(PluginId id) =>
            customSet.Contains(id) ? pending.DependsOn
            : _entries.TryGetValue(id, out var entry) ? entry.Manifest.DependsOn
            : [];

        bool Walk(PluginId current)
        {
            if (!onPath.Add(current))
            {
                path.Add(current);
                return true;
            }

            path.Add(current);
            foreach (var dependency in DependenciesOf(current))
            {
                if (Walk(dependency)) return true;
            }

            onPath.Remove(current);
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    internal void AddService(PluginId owner, long generation, Type serviceType, string? name, object instance)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = RequireEntry(owner);
            ObjectDisposedException.ThrowIf(entry.Generation != generation || !entry.DesiredActive
                || entry.Context is null || entry.Context.IsClosed
                || entry.State is not (PluginState.Activating or PluginState.Active), entry.Context);
            if (entry.State != PluginState.Activating)
                throw new InvalidOperationException("服务注册事务已提交；新增服务需要重新激活插件。");
            if (!entry.PendingServices.TryAdd(new ServiceKey(serviceType, name), instance))
                throw new InvalidOperationException($"插件 '{owner}' 在本次激活中重复注册服务 '{serviceType.Name}'。");
        }
    }

    private void WithdrawServices(PluginEntry entry)
    {
        entry.Context?.Invocations.Close();
        entry.PendingServices.Clear();
        _services.RemoveOwner(entry.Id, entry.Generation);
    }

    internal IReadOnlyList<ServiceTable.ServiceEntry> FindServices(
        PluginId owner, long generation, Type serviceType, string? name)
    {
        lock (_sync)
        {
            var committed = _services.Find(serviceType, name);
            if (_entries.TryGetValue(owner, out var entry) && entry.Generation == generation
                && entry.DesiredActive && entry.State == PluginState.Activating
                && entry.Context is { IsClosed: false }
                && entry.PendingServices.TryGetValue(new ServiceKey(serviceType, name), out var pending))
            {
                return [.. committed, new ServiceTable.ServiceEntry(owner, generation, pending)];
            }
            return committed;
        }
    }

    private PluginEntry RequireEntry(PluginId id) => _entries.TryGetValue(id, out var entry)
        ? entry : throw new KeyNotFoundException($"插件 '{id}' 未登记。");

    private PluginHandle GetHandle(PluginId id)
    {
        lock (_sync) { return ToHandle(RequireEntry(id)); }
    }

    private static PluginHandle ToHandle(PluginEntry entry) => new(entry.Id, entry.Manifest, entry.State);

    private static void SetState(PluginEntry entry, List<PluginStateChange> changes,
        PluginState to, string? reason, Exception? error = null)
    {
        var from = entry.State;
        entry.State = to;
        changes.Add(new PluginStateChange(entry.Id, from, to, reason, error));
    }

    private void PublishChanges(List<PluginStateChange> changes)
    {
        if (changes.Count == 0) return;
        Action<PluginStateChange>[] observers;
        lock (_stateObservers) { observers = [.. _stateObservers]; }
        using (EnterCallback())
        {
            foreach (var change in changes)
                foreach (var observer in observers)
                    try { observer(change); }
                    catch (Exception ex) { LastObserverError = ex; }
        }
    }

    private void OnEventHandlerError(Exception ex) => LastObserverError = ex;

    private void EnsureLifecycleCallAllowed()
    {
        if (_callback.Value is { Active: true })
            throw new InvalidOperationException("生命周期回调中不能发起嵌套生命周期变更；可以查询内核状态。");
    }

    private CallbackScope EnterCallback(PluginContext? context = null)
    {
        var scope = new CallbackScope(_callback, _callback.Value, context);
        _callback.Value = scope;
        return scope;
    }

    private sealed class CallbackScope(
        AsyncLocal<CallbackScope?> slot, CallbackScope? parent, PluginContext? context) : IDisposable
    {
        public bool Active => Volatile.Read(ref _active) != 0
            || context is { IsCancellationRunning: true } || parent is { Active: true };
        private int _active = 1;
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            slot.Value = parent;
        }
    }

    public ValueTask DisposeAsync()
    {
        EnsureLifecycleCallAllowed();
        TaskCompletionSource completion;
        PluginContext[] contexts;
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.DesiredActive = false;
                WithdrawServices(entry);
            }
            contexts = [.. _entries.Values.Select(e => e.Context).OfType<PluginContext>()];
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = DisposeCoreAsync(contexts, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(PluginContext[] contexts, TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(contexts.Select(RequestStopQuietlyAsync)).ConfigureAwait(false);
            await EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
            await _events.DrainAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception ex) { completion.SetException(ex); }
    }

    private sealed class StateSubscription(DougongKernel owner, Action<PluginStateChange> observer) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (owner._stateObservers) { owner._stateObservers.Remove(observer); }
        }
    }

    private sealed class ScopedRegistry(DougongKernel kernel, PluginId owner, long generation) : IServiceRegistry
    {
        public void Register<TService>(TService service, string? name = null) where TService : class
        {
            ArgumentNullException.ThrowIfNull(service);
            kernel.AddService(owner, generation, typeof(TService), name, service);
        }
        public TService? Resolve<TService>(string? name = null) where TService : class
        {
            var matches = kernel.FindServices(owner, generation, typeof(TService), name);
            return matches.Count == 1 ? (TService)matches[0].Instance : null;
        }
        public IReadOnlyList<TService> ResolveAll<TService>(string? name = null) where TService : class =>
            [.. kernel.FindServices(owner, generation, typeof(TService), name).Select(e => (TService)e.Instance)];
    }
}
