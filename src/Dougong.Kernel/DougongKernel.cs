namespace Dougong.Kernel;

using System.Threading.Channels;

internal sealed record ServiceKey(Type ServiceType, string? Name);

internal sealed record PendingService(object Instance, int Priority);

internal sealed class ServiceTable
{
    private readonly object _sync = new();
    private readonly Dictionary<ServiceKey, List<ServiceEntry>> _entries = [];

    internal sealed record ServiceEntry(PluginId Owner, long Generation, object Instance, int Priority);

    public void Publish(PluginId owner, long generation, IReadOnlyDictionary<ServiceKey, PendingService> services)
    {
        lock (_sync)
        {
            foreach (var (key, pending) in services)
            {
                if (!_entries.TryGetValue(key, out var list))
                {
                    list = [];
                    _entries[key] = list;
                }

                list.Add(new ServiceEntry(owner, generation, pending.Instance, pending.Priority));
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

    /// <summary>
    /// 判断需求是否满足。指定 <see cref="ServiceRequirement.ProviderId"/> 时只匹配该提供方；
    /// 否则按优先级选最高者，最高优先级仍并列多个则视为歧义。
    /// </summary>
    public bool IsSatisfied(ServiceRequirement requirement, out string? reason)
    {
        var matches = Find(requirement.ServiceType, requirement.Name);
        if (requirement.ProviderId is { } required)
        {
            matches = [.. matches.Where(e => e.Owner == required)];
        }

        if (matches.Count == 0)
        {
            reason = requirement.ProviderId is { } id
                ? $"未找到由插件 {id} 提供的服务 {requirement.ServiceType.Name}({requirement.Name})。"
                : $"未找到服务 {requirement.ServiceType.Name}({requirement.Name})。";
            return false;
        }

        if (SelectByPriority(matches) is not null)
        {
            reason = null;
            return true;
        }

        var top = TopPriority(matches);
        reason = $"服务 {requirement.ServiceType.Name} 有 {matches.Count} 个提供方"
            + $"（最高优先级 {top} 并列），无法唯一选择；请指定 ProviderId 或调整优先级。";
        return false;
    }

    /// <summary>按优先级选出唯一提供方；最高优先级并列时返回 null。</summary>
    public static ServiceEntry? SelectByPriority(IReadOnlyList<ServiceEntry> matches)
    {
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        var top = TopPriority(matches);
        ServiceEntry? winner = null;
        foreach (var match in matches)
        {
            if (match.Priority != top) continue;
            if (winner is not null) return null;
            winner = match;
        }

        return winner;
    }

    private static int TopPriority(IReadOnlyList<ServiceEntry> matches)
    {
        var top = matches[0].Priority;
        for (var i = 1; i < matches.Count; i++)
            if (matches[i].Priority > top) top = matches[i].Priority;
        return top;
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
    public DougongKernel.LoadedPlugin? Loaded { get; set; }
    public Dictionary<ServiceKey, PendingService> PendingServices { get; } = [];
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
    private readonly List<RevisionSubscription> _revisionSubscriptions = [];
    private readonly List<StateDelta> _revisionHistory = [];
    private long _revision;

    /// <summary>保留的增量条数，供游标续订回放；超出后旧游标必须重取快照。</summary>
    public int RevisionHistoryLimit { get; set; } = 1024;
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

    /// <summary>宿主提供的共享契约版本；插件可在清单里声明所需的兼容范围。</summary>
    public static IReadOnlyDictionary<string, Version> ContractVersions { get; } =
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["Dougong.Kernel"] = typeof(DougongKernel).Assembly.GetName().Version ?? new Version(0, 1),
        };

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

    /// <summary>
    /// 原子取得状态快照与其后的增量流。快照与订阅在同一临界区内建立，
    /// 因此调用方先应用快照再消费增量不会丢状态。
    /// </summary>
    public KernelStateView SubscribeStates(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var revision = _revision;
            var snapshot = new List<PluginHandle>(_entries.Values.Select(ToHandle));
            return CreateView(revision, snapshot, capacity, [], requiresResync: false);
        }
    }

    /// <summary>
    /// 从已知修订号续订：回放该修订号之后的保留增量，随后继续接收新增量。
    /// 游标早于保留窗口时返回 <see cref="KernelStateView.RequiresResync"/>，调用方须重新取快照。
    /// </summary>
    public KernelStateView ResumeStates(long sinceRevision, int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sinceRevision > _revision)
                throw new ArgumentOutOfRangeException(nameof(sinceRevision),
                    $"修订号 {sinceRevision} 超前于当前内核修订号 {_revision}。");

            var oldest = _revisionHistory.Count > 0 ? _revisionHistory[0].Revision : _revision + 1;
            if (sinceRevision + 1 < oldest)
                return CreateView(_revision, null, capacity, [], requiresResync: true);

            var replay = _revisionHistory.Where(d => d.Revision > sinceRevision).ToArray();
            return CreateView(sinceRevision, null, capacity, replay, requiresResync: false);
        }
    }

    private KernelStateView CreateView(
        long revision,
        IReadOnlyList<PluginHandle>? snapshot,
        int capacity,
        IReadOnlyList<StateDelta> replay,
        bool requiresResync)
    {
        var channel = Channel.CreateBounded<StateDelta>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var subscription = new RevisionSubscription(this, channel, capacity);
        if (!requiresResync) _revisionSubscriptions.Add(subscription);
        else subscription.Dispose();

        return new KernelStateView(
            revision, snapshot, subscription.ReadAllAsync(replay), subscription, requiresResync);
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

    internal void AddService(
        PluginId owner, long generation, Type serviceType, string? name, object instance, int priority = 0)
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
            if (!entry.PendingServices.TryAdd(new ServiceKey(serviceType, name), new PendingService(instance, priority)))
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
                committed = [.. committed, new ServiceTable.ServiceEntry(
                    owner, generation, pending.Instance, pending.Priority)];
            }

            // 消费方声明了 ProviderId 时，按该提供方过滤，避免同键多提供方被判为歧义。
            if (_entries.TryGetValue(owner, out var consumer)
                && PreferredProvider(consumer, serviceType, name) is { } preferred)
            {
                committed = [.. committed.Where(e => e.Owner == preferred)];
            }

            // 未指定 ProviderId 时按优先级缩小到唯一最优提供方；并列则保持多值，由调用方判为歧义。
            if (committed.Count > 1 && ServiceTable.SelectByPriority(committed) is { } best)
            {
                committed = [best];
            }

            return committed;
        }
    }

    internal PluginId? PreferredProviderFor(PluginId consumerId, Type serviceType, string? name) =>
        _entries.TryGetValue(consumerId, out var consumer)
            ? PreferredProvider(consumer, serviceType, name)
            : null;

    private static PluginId? PreferredProvider(PluginEntry consumer, Type serviceType, string? name)    {
        foreach (var requirement in consumer.Manifest.RequiredServices)
        {
            if (requirement.ProviderId is { } provider
                && requirement.ServiceType == serviceType
                && string.Equals(requirement.Name, name, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    private PluginEntry RequireEntry(PluginId id) => _entries.TryGetValue(id, out var entry)
        ? entry : throw new KeyNotFoundException($"插件 '{id}' 未登记。");

    private PluginHandle GetHandle(PluginId id)
    {
        lock (_sync) { return ToHandle(RequireEntry(id)); }
    }

    private static PluginHandle ToHandle(PluginEntry entry) => new(entry.Id, entry.Manifest, entry.State);

    /// <summary>调用方须持有 <c>_sync</c>。超出保留上限时丢弃最旧的增量。</summary>
    private void AppendHistory(StateDelta delta)
    {
        _revisionHistory.Add(delta);
        var overflow = _revisionHistory.Count - RevisionHistoryLimit;
        if (overflow > 0) _revisionHistory.RemoveRange(0, overflow);
    }

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
        RevisionSubscription[] revisions;
        List<StateDelta>? deltas = null;
        lock (_stateObservers) { observers = [.. _stateObservers]; }
        lock (_sync)
        {
            revisions = [.. _revisionSubscriptions];
            deltas = new List<StateDelta>(changes.Count);
            foreach (var change in changes)
            {
                var delta = new StateDelta(++_revision, change, Partition: change.PluginId);
                deltas.Add(delta);
                AppendHistory(delta);
            }
        }

        using (EnterCallback())
        {
            foreach (var change in changes)
                foreach (var observer in observers)
                    try { observer(change); }
                    catch (Exception ex) { LastObserverError = ex; }
        }

        if (deltas is null || deltas.Count == 0) return;
        foreach (var subscription in revisions)
            foreach (var delta in deltas)
                subscription.TryPublish(delta);
    }

    private void OnEventHandlerError(Exception ex) => LastObserverError = ex;

    internal long MarkRevisionOverflow(RevisionSubscription subscription, string reason)
    {
        LastObserverError = new InvalidOperationException(reason);
        lock (_sync) { return _revision; }
    }

    internal void RemoveRevisionSubscription(RevisionSubscription subscription)
    {
        lock (_sync) { _revisionSubscriptions.Remove(subscription); }
    }

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
        public void Register<TService>(TService service, string? name = null, int priority = 0) where TService : class
        {
            ArgumentNullException.ThrowIfNull(service);
            kernel.AddService(owner, generation, typeof(TService), name, service, priority);
        }
        public TService? Resolve<TService>(string? name = null) where TService : class
        {
            kernel.EnsureActivationResolveAllowed(owner, generation);
            var matches = kernel.FindServices(owner, generation, typeof(TService), name);
            return matches.Count == 1 ? (TService)matches[0].Instance : null;
        }
        public IReadOnlyList<TService> ResolveAll<TService>(string? name = null) where TService : class
        {
            kernel.EnsureActivationResolveAllowed(owner, generation);
            return [.. kernel.FindServices(owner, generation, typeof(TService), name).Select(e => (TService)e.Instance)];
        }
    }
}
