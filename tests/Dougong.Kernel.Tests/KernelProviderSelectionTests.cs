using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelProviderSelectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IStorage
    {
        string Marker { get; }
    }

    private sealed record Storage(string Marker) : IStorage;

    private sealed class StoragePlugin(string id, string marker) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            context.Services.Register<IStorage>(new Storage(marker));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConsumerPlugin(string id, ServiceRequirement requirement) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0", requirement);
        public string? Observed { get; private set; }
        public int Activations { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Activations++;
            Observed = context.Services.Resolve<IStorage>()?.Marker;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Specified_provider_resolves_ambiguity()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin(
            "test.consumer",
            new ServiceRequirement(typeof(IStorage), ProviderId: PluginId.Parse("test.storage.b")));

        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.a", "A"));
        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.b", "B"));
        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(1, consumer.Activations);
        Assert.Equal("B", consumer.Observed);
    }

    [Fact]
    public async Task Unspecified_provider_stays_blocked_on_ambiguity()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin("test.consumer", new ServiceRequirement(typeof(IStorage)));

        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.a", "A"));
        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.b", "B"));
        var handle = await kernel.RegisterAsync(() => consumer);

        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Equal(0, consumer.Activations);
    }

    [Fact]
    public async Task Specified_provider_waits_when_that_provider_is_absent()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin(
            "test.consumer",
            new ServiceRequirement(typeof(IStorage), ProviderId: PluginId.Parse("test.storage.b")));

        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.a", "A"));
        var handle = await kernel.RegisterAsync(() => consumer);

        // 其他插件提供同键服务不能满足这条指定需求。
        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Equal(0, consumer.Activations);
    }

    [Fact]
    public async Task Specified_provider_activation_unblocks_consumer()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin(
            "test.consumer",
            new ServiceRequirement(typeof(IStorage), ProviderId: PluginId.Parse("test.storage.b")));
        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.a", "A"));
        await kernel.RegisterAsync(() => consumer);

        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.b", "B"));

        var state = (await kernel.GetPluginsAsync()).Single(p => p.Id == consumer.Manifest.Id).State;
        Assert.Equal(PluginState.Active, state);
        Assert.Equal("B", consumer.Observed);
    }

    [Fact]
    public async Task Deactivating_specified_provider_blocks_consumer_again()
    {
        await using var kernel = new DougongKernel();
        var consumer = new ConsumerPlugin(
            "test.consumer",
            new ServiceRequirement(typeof(IStorage), ProviderId: PluginId.Parse("test.storage.b")));
        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.a", "A"));
        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.b", "B"));
        await kernel.RegisterAsync(() => consumer);
        Assert.Equal(PluginState.Active,
            (await kernel.GetPluginsAsync()).Single(p => p.Id == consumer.Manifest.Id).State);

        await kernel.DeactivateAsync(PluginId.Parse("test.storage.b"));

        // 另一个提供方仍在，但需求指定了 b，因此消费者必须阻塞而不是悄悄切到 a。
        Assert.Equal(
            PluginState.Blocked,
            (await kernel.GetPluginsAsync()).Single(p => p.Id == consumer.Manifest.Id).State);
    }

    [Fact]
    public async Task Resolver_respects_named_provider_instead_of_ambiguity()
    {
        await using var kernel = new DougongKernel();
        var a = new StoragePlugin("test.storage.a", "A");
        await kernel.RegisterAsync(() => a);
        var reference = kernel.Resolver.Resolve<IStorage>();
        Assert.NotNull(reference);
        await reference.InvokeAsync((instance, _) =>
        {
            Assert.Equal("A", instance.Marker);
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => new StoragePlugin("test.storage.b", "B"));

        // 出现第二个提供方后，未命名解析变为歧义，必须返回 null 而不是任选一个。
        Assert.Null(kernel.Resolver.Resolve<IStorage>());
    }

    [Fact]
    public async Task Provider_selection_is_reported_in_blocked_reason()
    {
        await using var kernel = new DougongKernel();
        var changes = new List<PluginStateChange>();
        using var subscription = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId.Value == "test.consumer") changes.Add(change);
        });
        var consumer = new ConsumerPlugin(
            "test.consumer",
            new ServiceRequirement(typeof(IStorage), ProviderId: PluginId.Parse("test.missing")));

        await kernel.RegisterAsync(() => consumer).AsTask().WaitAsync(Timeout);

        Assert.Contains(changes, change => change.To == PluginState.Blocked
            && change.Reason is not null && change.Reason.Contains("test.missing"));
    }
}
