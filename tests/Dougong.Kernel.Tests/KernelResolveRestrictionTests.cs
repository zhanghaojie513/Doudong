using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelResolveRestrictionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IMarker;

    private sealed class Marker : IMarker;

    private sealed class ProviderPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.provider", "1.0.0");
        public PluginContext? Context { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            context.Services.Register<IMarker>(new Marker());
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Bare_resolve_inside_activation_window_is_allowed()
    {
        await using var kernel = new DougongKernel();
        var observed = false;
        var plugin = new ActivationResolvePlugin(() => observed = true);

        var handle = await kernel.RegisterAsync(() => plugin).AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.True(observed);
    }

    [Fact]
    public async Task Bare_resolve_after_activation_is_rejected_with_guidance()
    {
        await using var kernel = new DougongKernel();
        var provider = new ProviderPlugin();
        await kernel.RegisterAsync(() => provider);

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Context!.Services.Resolve<IMarker>());

        Assert.Contains("Resolver", error.Message);
        Assert.Contains("激活回调", error.Message);
    }

    [Fact]
    public async Task Bare_resolve_after_deactivation_is_rejected()
    {
        await using var kernel = new DougongKernel();
        var provider = new ProviderPlugin();
        await kernel.RegisterAsync(() => provider);
        await kernel.DeactivateAsync(provider.Manifest.Id);

        Assert.Throws<InvalidOperationException>(
            () => provider.Context!.Services.ResolveAll<IMarker>());
    }

    [Fact]
    public async Task Resolver_remains_the_supported_runtime_path()
    {
        await using var kernel = new DougongKernel();
        var provider = new ProviderPlugin();
        await kernel.RegisterAsync(() => provider);

        var reference = kernel.Resolver.Resolve<IMarker>();
        Assert.NotNull(reference);
        var called = false;
        await reference.InvokeAsync((_, _) =>
        {
            called = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(called);
    }

    [Fact]
    public async Task Registration_from_callback_still_works_during_activation()
    {
        await using var kernel = new DougongKernel();
        var plugin = new ActivationResolvePlugin(onResolve: null);

        var handle = await kernel.RegisterAsync(() => plugin).AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.NotNull(kernel.ResolveDiagnostic<IMarker>());
    }

    private sealed class ActivationResolvePlugin(Action? onResolve) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.activation", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IMarker>(new Marker());
            // 激活期间解析自己的暂存服务是合法用法。
            Assert.NotNull(context.Services.Resolve<IMarker>());
            onResolve?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
