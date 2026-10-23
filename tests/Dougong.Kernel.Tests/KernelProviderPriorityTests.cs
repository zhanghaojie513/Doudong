using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelProviderPriorityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IStorage
    {
        string Marker { get; }
    }

    private sealed record Storage(string Marker) : IStorage;

    private sealed class PriorityProvider(string id, string marker, int priority) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IStorage>(new Storage(marker), priority: priority);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PriorityConsumer(ServiceRequirement requirement) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.consumer", "1.0.0", requirement);
        public string? Observed { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Observed = context.Services.Resolve<IStorage>()?.Marker;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Higher_priority_provider_wins_without_explicit_selection()
    {
        await using var kernel = new DougongKernel();
        var consumer = new PriorityConsumer(new ServiceRequirement(typeof(IStorage)));

        await kernel.RegisterAsync(() => new PriorityProvider("test.low", "low", 0));
        await kernel.RegisterAsync(() => new PriorityProvider("test.high", "high", 10));
        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal("high", consumer.Observed);
    }

    [Fact]
    public async Task Equal_top_priority_remains_ambiguous()
    {
        await using var kernel = new DougongKernel();
        var consumer = new PriorityConsumer(new ServiceRequirement(typeof(IStorage)));

        await kernel.RegisterAsync(() => new PriorityProvider("test.a", "A", 5));
        await kernel.RegisterAsync(() => new PriorityProvider("test.b", "B", 5));
        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Null(consumer.Observed);
    }

    [Fact]
    public async Task Resolver_follows_priority_choice()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new PriorityProvider("test.low", "low", 0));
        await kernel.RegisterAsync(() => new PriorityProvider("test.high", "high", 10));

        var reference = kernel.Resolver.Resolve<IStorage>();
        Assert.NotNull(reference);
        await reference.InvokeAsync((instance, _) =>
        {
            Assert.Equal("high", instance.Marker);
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task Removing_winner_promotes_the_next_provider()
    {
        await using var kernel = new DougongKernel();
        var consumer = new PriorityConsumer(new ServiceRequirement(typeof(IStorage)));
        await kernel.RegisterAsync(() => new PriorityProvider("test.low", "low", 0));
        await kernel.RegisterAsync(() => new PriorityProvider("test.high", "high", 10));
        await kernel.RegisterAsync(() => consumer);
        Assert.Equal("high", consumer.Observed);

        await kernel.DeactivateAsync(PluginId.Parse("test.high")).AsTask().WaitAsync(Timeout);

        var reference = kernel.Resolver.Resolve<IStorage>();
        Assert.NotNull(reference);
        await reference.InvokeAsync((instance, _) =>
        {
            Assert.Equal("low", instance.Marker);
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task Explicit_provider_overrides_priority()
    {
        await using var kernel = new DougongKernel();
        var consumer = new PriorityConsumer(new ServiceRequirement(
            typeof(IStorage), ProviderId: PluginId.Parse("test.low")));

        await kernel.RegisterAsync(() => new PriorityProvider("test.high", "high", 100));
        await kernel.RegisterAsync(() => new PriorityProvider("test.low", "low", 0));
        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal("low", consumer.Observed);
    }
}
