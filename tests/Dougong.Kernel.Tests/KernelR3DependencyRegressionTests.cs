using System.Runtime.CompilerServices;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelR3DependencyRegressionTests
{
    private interface IMarker
    {
        string Name { get; }
    }

    private sealed record Marker(string Name) : IMarker;

    [Fact]
    public async Task Late_high_priority_resolution_orders_consumer_before_both_used_providers()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var low = Provider("r3.low", order);
        var consumer = Consumer("r3.consumer", order);
        await kernel.RegisterAsync(() => low);
        await kernel.RegisterAsync(() => consumer);
        Assert.NotNull(consumer.Context!.Resolver.Resolve<IMarker>());

        var high = Provider("r3.high", order, priority: 10);
        await kernel.RegisterAsync(() => high);
        var reference = consumer.Context.Resolver.Resolve<IMarker>();
        Assert.NotNull(reference);
        Assert.Equal("r3.high", await reference.InvokeAsync(
            (service, _) => ValueTask.FromResult(service.Name)));
        Assert.Equal(1, consumer.Activations);

        await kernel.DisposeAsync();

        AssertBefore(order, "r3.consumer", "r3.high");
        AssertBefore(order, "r3.consumer", "r3.low");
    }

    [Fact]
    public async Task Optional_provider_arriving_later_is_selected_without_restarting_consumer()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var consumer = Consumer("r3.optional", order,
            requirements: [new ServiceRequirement(typeof(IMarker), Optional: true)]);
        await kernel.RegisterAsync(() => consumer);
        Assert.Null(consumer.Context!.Resolver.Resolve<IMarker>());
        var provider = Provider("r3.late", order);
        await kernel.RegisterAsync(() => provider);
        Assert.NotNull(consumer.Context.Resolver.Resolve<IMarker>());
        Assert.Equal(1, consumer.Activations);

        await kernel.DisposeAsync();

        Assert.Equal(["r3.optional", "r3.late"], order);
    }

    [Fact]
    public async Task Uninvoked_reference_records_dependency_at_resolution()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var consumer = Consumer("r3.consumer", order);
        await kernel.RegisterAsync(() => consumer);
        var provider = Provider("r3.late", order);
        await kernel.RegisterAsync(() => provider);
        Assert.NotNull(consumer.Context!.Resolver.Resolve<IMarker>());

        await kernel.DisposeAsync();

        Assert.Equal(["r3.consumer", "r3.late"], order);
    }

    [Fact]
    public async Task Raw_resolution_keeps_original_provider_edge_after_late_higher_priority_arrival()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var low = Provider("r3.low", order);
        IMarker? resolved = null;
        var consumer = Consumer("r3.consumer", order, context =>
            resolved = context.Services.Resolve<IMarker>());
        await kernel.RegisterAsync(() => low);
        await kernel.RegisterAsync(() => consumer);
        var high = Provider("r3.high", order, priority: 10);
        await kernel.RegisterAsync(() => high);
        Assert.Equal("r3.low", resolved?.Name);
        Assert.Equal(1, consumer.Activations);

        await kernel.DeactivateAsync(low.Manifest.Id);

        AssertBefore(order, "r3.consumer", "r3.low");
        Assert.Equal(2, consumer.Activations);
        Assert.Equal("r3.high", resolved?.Name);
    }

    [Fact]
    public async Task Resolve_all_records_every_returned_provider_including_lower_priority()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var low = Provider("r3.low", order);
        var high = Provider("r3.high", order, priority: 10);
        string[] observed = [];
        var consumer = Consumer("r3.all", order, context =>
            observed = [.. context.Services.ResolveAll<IMarker>().Select(service => service.Name).Order()]);
        await kernel.RegisterAsync(() => low);
        await kernel.RegisterAsync(() => high);
        await kernel.RegisterAsync(() => consumer);
        Assert.Equal(["r3.high", "r3.low"], observed);

        await kernel.DeactivateAsync(low.Manifest.Id);

        AssertBefore(order, "r3.all", "r3.low");
        Assert.Equal(2, consumer.Activations);
        Assert.Equal(["r3.high"], observed);
    }

    [Fact]
    public async Task Stopping_previously_used_provider_restarts_consumer_even_after_switching_to_another()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var low = Provider("r3.low", order);
        var consumer = Consumer("r3.consumer", order);
        await kernel.RegisterAsync(() => low);
        await kernel.RegisterAsync(() => consumer);
        var lowReference = consumer.Context!.Resolver.Resolve<IMarker>();
        Assert.NotNull(lowReference);
        var high = Provider("r3.high", order, priority: 10);
        await kernel.RegisterAsync(() => high);
        Assert.NotNull(consumer.Context.Resolver.Resolve<IMarker>());

        await kernel.DeactivateAsync(low.Manifest.Id);

        AssertBefore(order, "r3.consumer", "r3.low");
        Assert.Equal(2, consumer.Activations);
        Assert.NotNull(consumer.Context!.Resolver.Resolve<IMarker>());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await lowReference.InvokeAsync((_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task Runtime_dependency_cycle_is_rejected_without_adding_the_edge()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var first = Provider("r3.first", order, name: "first");
        var second = Provider("r3.second", order, name: "second");
        await kernel.RegisterAsync(() => first);
        await kernel.RegisterAsync(() => second);
        Assert.NotNull(first.Context!.Resolver.Resolve<IMarker>("second"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            second.Context!.Resolver.Resolve<IMarker>("first"));
        Assert.Contains("r3.first", error.Message);
        Assert.Contains("r3.second", error.Message);

        await kernel.DisposeAsync();

        Assert.Equal(["r3.first", "r3.second"], order);
    }

    [Fact]
    public async Task Runtime_dependency_cycle_including_depends_on_is_rejected()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var provider = Provider("r3.base", order, name: "base");
        var dependent = new TestPlugin("r3.dependent", context =>
            context.Services.Register<IMarker>(new Marker("dependent"), "dependent"), order,
            dependsOn: [provider.Manifest.Id]);
        await kernel.RegisterAsync(() => provider);
        await kernel.RegisterAsync(() => dependent);

        Assert.Throws<InvalidOperationException>(() =>
            provider.Context!.Resolver.Resolve<IMarker>("dependent"));

        await kernel.DisposeAsync();

        Assert.Equal(["r3.dependent", "r3.base"], order);
    }

    [Fact]
    public async Task Resolving_own_service_ignores_self_edge()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var provider = Provider("r3.self", order);
        await kernel.RegisterAsync(() => provider);
        var reference = provider.Context!.Resolver.Resolve<IMarker>();
        Assert.NotNull(reference);
        await reference.InvokeAsync((_, _) => ValueTask.CompletedTask);

        await kernel.DisposeAsync();

        Assert.Equal(["r3.self"], order);
    }

    [Fact]
    public async Task Old_reference_does_not_retain_service_instance_after_provider_stops()
    {
        await using var kernel = new DougongKernel();
        var (reference, weakInstance, providerId) = await RegisterEphemeralProviderAsync(kernel);

        await kernel.DeactivateAsync(providerId);
        Collect();

        Assert.False(weakInstance.IsAlive);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await reference.InvokeAsync((_, _) => ValueTask.CompletedTask));
        GC.KeepAlive(reference);
    }

    private static TestPlugin Provider(string id, List<string> order, int priority = 0, string? name = null) =>
        new(id, context => context.Services.Register<IMarker>(new Marker(id), name, priority), order);

    private static TestPlugin Consumer(string id, List<string> order, Action<PluginContext>? activate = null,
        ServiceRequirement[]? requirements = null) => new(id, activate, order, requirements);

    private static void AssertBefore(List<string> order, string consumer, string provider)
    {
        Assert.Contains(consumer, order);
        Assert.Contains(provider, order);
        Assert.True(order.IndexOf(consumer) < order.IndexOf(provider),
            $"Expected {consumer} before {provider}; actual order: {string.Join(", ", order)}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IServiceReference<IMarker>, WeakReference, PluginId)> RegisterEphemeralProviderAsync(
        DougongKernel kernel)
    {
        var holder = new EphemeralHolder();
        var provider = new TestPlugin("r3.ephemeral", holder.Register);
        await kernel.RegisterAsync(() => provider);
        var reference = kernel.Resolver.Resolve<IMarker>();
        Assert.NotNull(reference);
        return (reference, holder.Instance!, provider.Manifest.Id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class EphemeralHolder
    {
        public WeakReference? Instance { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Register(PluginContext context)
        {
            var instance = new Marker("ephemeral");
            Instance = new WeakReference(instance);
            context.Services.Register<IMarker>(instance);
        }
    }

    private sealed class TestPlugin(
        string id, Action<PluginContext>? activate = null, List<string>? cleanupOrder = null,
        ServiceRequirement[]? requirements = null, PluginId[]? dependsOn = null) : IPlugin
    {
        public PluginManifest Manifest { get; } = new(PluginId.Parse(id), "1.0.0", requirements ?? [], dependsOn ?? []);
        public PluginContext? Context { get; private set; }
        public int Activations { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            Activations++;
            if (cleanupOrder is not null)
                context.RegisterForCleanup(_ =>
                {
                    cleanupOrder.Add(id);
                    return ValueTask.CompletedTask;
                });
            activate?.Invoke(context);
            return ValueTask.CompletedTask;
        }
    }
}
