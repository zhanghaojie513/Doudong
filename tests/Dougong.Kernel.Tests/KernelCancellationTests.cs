using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifetime_callback_rejects_nested_stop_after_activation_has_returned(bool disposeKernel)
    {
        var kernel = new DougongKernel();
        var id = PluginId.Parse("test.cancellation.callback");
        Exception? callbackError = null;
        var cleanupCount = 0;
        var plugin = new CallbackPlugin(id, (context, _) =>
        {
            context.Lifetime.Register(() => callbackError = TryNestedStop(kernel, id));
            context.RegisterForCleanup(_ =>
            {
                cleanupCount++;
                return ValueTask.CompletedTask;
            });
            return ValueTask.CompletedTask;
        });
        await kernel.RegisterAsync(() => plugin);

        if (disposeKernel)
            await kernel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else
            await kernel.DeactivateAsync(id).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(callbackError);
        Assert.Equal(1, cleanupCount);
        Assert.Equal(PluginState.Stopped, (await kernel.GetPluginsAsync()).Single().State);
        await kernel.DisposeAsync();
    }

    private static Exception? TryNestedStop(DougongKernel kernel, PluginId id) =>
        Record.Exception(() => kernel.DeactivateAsync(id).AsTask().GetAwaiter().GetResult());

    [Fact]
    public async Task Stopping_provider_cancels_consumer_that_is_still_activating()
    {
        await using var kernel = new DougongKernel();
        var providerId = PluginId.Parse("test.provider");
        var consumerId = PluginId.Parse("test.waiting.consumer");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerCancelled = false;
        var cleanupCount = 0;
        var provider = new CallbackPlugin(providerId, (context, _) =>
        {
            context.Services.Register<IMarker>(new Marker());
            return ValueTask.CompletedTask;
        });
        var consumer = new CallbackPlugin(consumerId, async (context, token) =>
        {
            context.RegisterForCleanup(_ =>
            {
                cleanupCount++;
                return ValueTask.CompletedTask;
            });
            started.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                consumerCancelled = true;
                throw;
            }
        }, new ServiceRequirement(typeof(IMarker)));
        await kernel.RegisterAsync(() => provider);
        var registering = kernel.RegisterAsync(() => consumer).AsTask();
        Task<PluginHandle>? stopping = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = kernel.DeactivateAsync(providerId).AsTask();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await registering.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(consumerCancelled);
            Assert.Equal(1, cleanupCount);
            var plugins = await kernel.GetPluginsAsync();
            Assert.Equal(PluginState.Stopped, plugins.Single(p => p.Id == providerId).State);
            Assert.Equal(PluginState.Blocked, plugins.Single(p => p.Id == consumerId).State);
        }
        finally
        {
            release.TrySetResult();
            await registering.WaitAsync(TimeSpan.FromSeconds(5));
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private interface IMarker;
    private sealed class Marker : IMarker;

    private sealed class CallbackPlugin(
        PluginId id,
        Func<PluginContext, CancellationToken, ValueTask> activate,
        params ServiceRequirement[] requirements) : IPlugin
    {
        public PluginManifest Manifest { get; } = new(id, "1.0.0", requirements);
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            activate(context, cancellationToken);
    }
}
