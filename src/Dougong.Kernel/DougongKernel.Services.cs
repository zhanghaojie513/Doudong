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
                var matches = kernel._services.Find(typeof(TService), name);
                if (matches.Count != 1) return null;
                var match = matches[0];
                if (!kernel.CanProvide(match.Owner, match.Generation)) return null;
                return new ServiceReference<TService>(kernel, match, consumer, generation);
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
        DougongKernel kernel, ServiceTable.ServiceEntry service, PluginId? consumer, long consumerGeneration)
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
            lock (kernel._sync)
            {
                ObjectDisposedException.ThrowIf(kernel._disposed
                    || !kernel.CanProvide(service.Owner, service.Generation), this);
                providerContext = kernel.RequireEntry(service.Owner).Context!;
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
                return await action((TService)service.Instance, linked.Token).ConfigureAwait(false);
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
        await DeactivateGenerationAsync(context.PluginId, context, CancellationToken.None).ConfigureAwait(false);
        await context.CompleteDisposalAsync().ConfigureAwait(false);
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
                    if (entry.Context is not { } current || entry.DrainTimedOut) continue;
                    var missing = MissingRequirements(entry);
                    if (entry.DesiredActive && !current.IsClosed && !current.Invocations.IsClosed
                        && entry.State != PluginState.Deactivating && missing.Count == 0) continue;
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

            // 逆拓扑：后激活的消费者先清理，提供方最后清理。
            batch.Sort((left, right) => right.Entry.ActivationSequence.CompareTo(left.Entry.ActivationSequence));
        }

        PublishChanges(changes);
        await Task.WhenAll(batch.Select(b => RequestStopQuietlyAsync(b.Context))).ConfigureAwait(false);

        var draining = Task.WhenAll(batch.Select(b => b.Context.Invocations.WhenDrained));
        if (DrainTimeout != Timeout.InfiniteTimeSpan)
        {
            try
            {
                await draining.WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var timedOut = new List<PluginStateChange>();
                lock (_sync)
                {
                    foreach (var (entry, context) in batch)
                    {
                        if (!ReferenceEquals(entry.Context, context)) continue;
                        entry.DrainTimedOut = true;
                        SetState(entry, timedOut, PluginState.Failed,
                            $"服务调用未在排空预算 {DrainTimeout} 内结束；不执行清理，需重启宿主。",
                            new TimeoutException("服务调用排空超时。"));
                    }
                }
                PublishChanges(timedOut);
                return;
            }
        }
        else
        {
            await draining.ConfigureAwait(false);
        }

        foreach (var (entry, context) in batch)
        {
            await StopAsync(entry, context).ConfigureAwait(false);
        }
    }
}
