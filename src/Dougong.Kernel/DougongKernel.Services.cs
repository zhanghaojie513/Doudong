namespace Dougong.Kernel;

public sealed partial class DougongKernel
{
    private sealed class ServiceResolver(
        DougongKernel kernel, PluginId? consumer = null, long generation = 0) : IResolver
    {
        public IServiceReference<TService>? Resolve<TService>(string? name = null) where TService : class
        {
            lock (kernel._sync)
            {
                if (kernel._disposed) return null;
                if (consumer is { } id && !kernel.CanConsume(id, generation)) return null;
                var matches = kernel.FindAllServices(
                    consumer ?? default, consumer is null ? 0 : generation, typeof(TService), name);
                if (matches.Count == 0) return null;
                var match = ServiceTable.SelectByPriority(matches);
                if (match is null) return null;
                if (!kernel.CanProvide(match.Owner, match.Generation)) return null;
                if (consumer is { } consumerId)
                    kernel.RecordDependency(consumerId, generation, match.Owner, match.Generation);
                return new ServiceReference<TService>(
                    kernel, typeof(TService), name, match.Owner, match.Generation, consumer, generation);
            }
        }
    }

    private bool CanConsume(PluginId id, long generation) =>
        _entries.TryGetValue(id, out var entry) && entry.Generation == generation
        && entry.DesiredActive && entry.State is PluginState.Activating or PluginState.Active
        && entry.Context is { IsClosed: false } context && !context.Invocations.IsClosed;

    private bool CanProvide(PluginId id, long generation) =>
        _entries.TryGetValue(id, out var entry) && entry.Generation == generation
        && entry.DesiredActive && entry.State == PluginState.Active
        && entry.Context is { IsClosed: false } context && !context.Invocations.IsClosed;

    private sealed class ServiceReference<TService>(
        DougongKernel kernel,
        Type serviceType,
        string? serviceName,
        PluginId provider,
        long providerGeneration,
        PluginId? consumer,
        long consumerGeneration)
        : IServiceReference<TService> where TService : class
    {
        public async ValueTask InvokeAsync(
            Func<TService, CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            await InvokeAsync(async (instance, token) =>
            {
                await action(instance, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<TResult> InvokeAsync<TResult>(
            Func<TService, CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            cancellationToken.ThrowIfCancellationRequested();
            PluginContext providerContext;
            PluginContext? consumerContext = null;
            object instance;
            lock (kernel._sync)
            {
                ObjectDisposedException.ThrowIf(kernel._disposed
                    || !kernel.CanProvide(provider, providerGeneration), this);
                // 调用时重新取得实例，引用本身不成为实现对象的长期 GC 根。
                var service = kernel._services.Find(provider, providerGeneration, serviceType, serviceName)
                    ?? throw new ObjectDisposedException(nameof(IServiceReference<TService>));
                instance = service.Instance;
                providerContext = kernel.RequireEntry(provider).Context!;
                if (consumer is { } id)
                {
                    ObjectDisposedException.ThrowIf(!kernel.CanConsume(id, consumerGeneration), this);
                    consumerContext = kernel.RequireEntry(id).Context!;
                }
                ObjectDisposedException.ThrowIf(!providerContext.Invocations.TryEnter(), this);
                if (consumerContext is not null && !ReferenceEquals(providerContext, consumerContext)
                    && !consumerContext.Invocations.TryEnter())
                {
                    providerContext.Invocations.Exit();
                    throw new ObjectDisposedException(nameof(IServiceReference<TService>));
                }
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, providerContext.Lifetime, consumerContext?.Lifetime ?? default);
                using var providerCallback = kernel.EnterCallback(providerContext);
                using var consumerCallback = kernel.EnterCallback(consumerContext);
                linked.Token.ThrowIfCancellationRequested();
                return await action((TService)instance, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                if (consumerContext is not null && !ReferenceEquals(providerContext, consumerContext))
                    consumerContext.Invocations.Exit();
                providerContext.Invocations.Exit();
            }
        }
    }

    private async ValueTask DisposePluginContextAsync(PluginContext context)
    {
        EnsureLifecycleCallAllowed();
        List<PluginStateChange> changes = [];
        lock (_sync)
        {
            if (!_entries.TryGetValue(context.PluginId, out var entry)
                || !ReferenceEquals(entry.Context, context))
                return;
            // 排空超时或已隔离的代次不允许经 Context 绕过内核继续清理。
            if (entry.IsolationReason is not null)
                throw new InvalidOperationException(
                    $"插件 '{context.PluginId}' 未能干净停止；必须重启宿主，不能单独清理该上下文。");
            entry.DesiredActive = false;
            WithdrawServices(entry);
            SetState(entry, changes, PluginState.Deactivating, "上下文请求停用。");
        }
        PublishChanges(changes);
        await DeactivateGenerationAsync(context.PluginId, context, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task StopBatchAsync()
    {
        List<PluginStateChange> changes = [];
        List<(PluginEntry Entry, PluginContext Context)> batch = [];
        lock (_sync)
        {
            bool invalidated;
            do
            {
                invalidated = false;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Context is not { } current 
                        || entry.IsolationReason is not null) continue;
                    var missing = MissingRequirements(entry);
                    if (entry.DesiredActive && !current.IsClosed && !current.Invocations.IsClosed
                        && entry.State != PluginState.Deactivating && missing.Count == 0
                        && !HasUnavailableUsedProvider(entry)) continue;
                    if (!current.Invocations.IsClosed)
                    {
                        WithdrawServices(entry);
                        invalidated = true;
                    }
                    if (entry.State != PluginState.Deactivating)
                        SetState(entry, changes, PluginState.Deactivating,
                            entry.DesiredActive ? string.Join(" ", missing) : "用户停用。");
                    if (!batch.Exists(b => ReferenceEquals(b.Context, current)))
                        batch.Add((entry, current));
                }
            } while (invalidated);

            // 消费者先于它实际使用的提供方；无依赖时以逆激活序稳定排列。
            SortStopBatch(batch);
        }

        PublishChanges(changes);
        var cancellation = Task.WhenAll(batch.Select(b => RequestStopQuietlyAsync(b.Context)));
        var draining = Task.WhenAll(batch.Select(b => b.Context.Invocations.WhenDrained));
        var events = Task.WhenAll(batch.Select(b => b.Context.DrainEventsAsync()));
        try
        {
            await WithinBudgetAsync(cancellation, CancellationTimeout, "cancellation").ConfigureAwait(false);
            await WithinBudgetAsync(draining, DrainTimeout, "drain").ConfigureAwait(false);
            await WithinBudgetAsync(events, EventDrainTimeout, "events").ConfigureAwait(false);
        }
        catch (LifecycleTimeoutException ex)
        {
            foreach (var (entry, context) in batch) Isolate(entry, context, ex.Message, ex);
            return;
        }

        for (var index = 0; index < batch.Count; index++)
        {
            var (entry, context) = batch[index];
            var timeout = await StopAsync(entry, context).ConfigureAwait(false);
            if (timeout is null) continue;

            // 前一消费者仍在清理时，不能继续清理它依赖的提供方；其余条目一并隔离保留。
            for (var remaining = index; remaining < batch.Count; remaining++)
            {
                var (pendingEntry, pendingContext) = batch[remaining];
                Isolate(pendingEntry, pendingContext,
                    "同批次前序清理超时；本代次保留资源并隔离，需重启宿主。", timeout);
            }

            return;
        }
    }
}
