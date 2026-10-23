using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>第二轮独立复审确认的缺陷回归：激活作用域、清理超时、取消预算、诊断引用、状态流收尾。</summary>
public sealed class KernelReview2RegressionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private interface IMarker;
    private sealed class Marker : IMarker;
    private sealed class CustomPluginException(string message) : Exception(message);

    [Fact]
    public async Task Bare_resolve_from_unrelated_async_flow_is_rejected()
    {
        var kernel = new DougongKernel();
        var activationStarted = Signal();
        var releaseActivation = Signal();
        var plugin = new P("r2.scope", async (ctx, _) =>
        {
            ctx.Services.Register<IMarker>(new Marker());
            activationStarted.TrySetResult();
            await releaseActivation.Task;
        });

        var registering = kernel.RegisterAsync(() => plugin).AsTask();
        await activationStarted.Task.WaitAsync(Limit);
        try
        {
            // 独立异步流不在激活回调作用域内，必须拒绝裸解析。
            var error = await Task.Run(() => Record.Exception(
                () => plugin.Context!.Services.Resolve<IMarker>()));
            Assert.IsType<InvalidOperationException>(error);
        }
        finally
        {
            releaseActivation.TrySetResult();
        }

        await registering.WaitAsync(Limit);
        await kernel.DisposeAsync();
    }

    [Fact]
    public async Task Cleanup_timeout_stops_later_provider_cleanup()
    {
        var kernel = new DougongKernel { CleanupTimeout = TimeSpan.FromMilliseconds(100) };
        var consumerStarted = Signal();
        var releaseConsumer = Signal();
        var providerCleaned = false;

        var provider = new P("r2.provider", (ctx, _) =>
        {
            ctx.Services.Register<IMarker>(new Marker());
            ctx.RegisterForCleanup(_ => { providerCleaned = true; return ValueTask.CompletedTask; });
            return ValueTask.CompletedTask;
        });
        var consumer = new P("r2.consumer", (ctx, _) =>
        {
            ctx.RegisterForCleanup(async _ =>
            {
                consumerStarted.TrySetResult();
                await releaseConsumer.Task;
            });
            return ValueTask.CompletedTask;
        }, new ServiceRequirement(typeof(IMarker)));

        await kernel.RegisterAsync(() => provider);
        await kernel.RegisterAsync(() => consumer);

        var disposing = kernel.DisposeAsync().AsTask();
        try
        {
            await consumerStarted.Task.WaitAsync(Limit);
            await Task.Delay(400);
            Assert.False(providerCleaned, "消费者清理超时后不得继续清理其提供方。");
        }
        finally
        {
            releaseConsumer.TrySetResult();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await disposing.WaitAsync(Limit));
    }

    [Fact]
    public async Task Deactivate_is_bounded_when_lifetime_callback_blocks()
    {
        var kernel = new DougongKernel { CancellationTimeout = TimeSpan.FromMilliseconds(100) };
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var plugin = new P("r2.cancel", (ctx, _) =>
        {
            ctx.Lifetime.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => plugin);

        var deactivating = kernel.DeactivateAsync(plugin.Manifest.Id).AsTask();
        try
        {
            await callbackEntered.Task.WaitAsync(Limit);
            var handle = await deactivating.WaitAsync(Limit);
            Assert.Equal(PluginState.Failed, handle.State);
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        try { await kernel.DisposeAsync().AsTask().WaitAsync(Limit); }
        catch (InvalidOperationException) { /* 隔离代次如实报告未干净销毁。 */ }
    }

    [Fact]
    public async Task Cleanup_receives_a_cancelable_token()
    {
        var kernel = new DougongKernel();
        var canBeCanceled = false;
        var plugin = new P("r2.token", (ctx, _) =>
        {
            ctx.RegisterForCleanup(token => { canBeCanceled = token.CanBeCanceled; return ValueTask.CompletedTask; });
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => plugin);
        await kernel.DeactivateAsync(plugin.Manifest.Id);
        await kernel.DisposeAsync();

        Assert.True(canBeCanceled, "清理回调必须收到可取消令牌。");
    }

    [Fact]
    public async Task Observer_failure_is_stored_as_shared_diagnostic_not_plugin_exception()
    {
        var kernel = new DougongKernel();
        var plugin = new P("r2.diagnostic", (ctx, _) =>
        {
            ctx.Events.Subscribe<int>(_ => throw new CustomPluginException("handler failed"));
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => plugin);
        plugin.Context!.Events.Publish(1);

        await WaitUntilAsync(() => kernel.LastObserverDiagnostic is not null);
        var diagnostic = kernel.LastObserverDiagnostic!;
        Assert.Equal(nameof(CustomPluginException), diagnostic.Code);
        Assert.Contains("handler failed", diagnostic.Message);
        await kernel.DisposeAsync();
    }

    [Fact]
    public async Task State_stream_completes_when_kernel_is_disposed()
    {
        var kernel = new DougongKernel();
        using var view = kernel.SubscribeStates();
        var ended = Signal();
        _ = Task.Run(async () =>
        {
            await foreach (var _ in view.Changes) { }
            ended.TrySetResult();
        });

        await kernel.DisposeAsync().AsTask().WaitAsync(Limit);
        await ended.Task.WaitAsync(Limit);
    }

    [Fact]
    public void Negative_revision_history_limit_is_rejected_at_assignment()
    {
        var kernel = new DougongKernel();
        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.RevisionHistoryLimit = -1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "条件在限定时间内未满足。");
    }

    private sealed class P(
        string id, Func<PluginContext, CancellationToken, ValueTask> activate,
        params ServiceRequirement[] requirements) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0", requirements);
        public PluginContext? Context { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return activate(context, cancellationToken);
        }
    }
}
