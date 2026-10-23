using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelDependencyGraphTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IMarker;

    private sealed class Marker : IMarker;

    [Fact]
    public async Task Plugin_dependency_blocks_activation_until_provider_is_active()
    {
        await using var kernel = new DougongKernel();
        var activated = 0;
        var dependent = new GraphPlugin(
            "test.dependent",
            dependsOn: [PluginId.Parse("test.base")],
            onActivate: () => activated++);

        var handle = await kernel.RegisterAsync(() => dependent);
        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Equal(0, activated);

        await kernel.RegisterAsync(() => new GraphPlugin("test.base"));
        Assert.Equal(PluginState.Active, (await kernel.GetPluginsAsync())
            .Single(p => p.Id == dependent.Manifest.Id).State);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task Deactivating_dependency_blocks_dependent_plugin()
    {
        await using var kernel = new DougongKernel();
        var baseId = PluginId.Parse("test.base");
        await kernel.RegisterAsync(() => new GraphPlugin("test.base"));
        await kernel.RegisterAsync(() => new GraphPlugin("test.dependent", dependsOn: [baseId]));

        await kernel.DeactivateAsync(baseId);

        Assert.Equal(
            PluginState.Blocked,
            (await kernel.GetPluginsAsync()).Single(p => p.Id.Value == "test.dependent").State);
    }

    [Fact]
    public async Task Dependency_cycle_is_rejected_at_registration()
    {
        await using var kernel = new DougongKernel();
        var first = PluginId.Parse("test.first");
        var second = PluginId.Parse("test.second");
        await kernel.RegisterAsync(() => new GraphPlugin("test.first", dependsOn: [second]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.RegisterAsync(() => new GraphPlugin("test.second", dependsOn: [first])));

        Assert.Contains("成环", error.Message);
        Assert.Single(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Self_dependency_is_rejected()
    {
        await using var kernel = new DougongKernel();
        var id = PluginId.Parse("test.self");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.RegisterAsync(() => new GraphPlugin("test.self", dependsOn: [id])));
    }

    [Fact]
    public async Task Optional_service_requirement_does_not_block_activation()
    {
        await using var kernel = new DougongKernel();
        var activated = 0;
        var plugin = new GraphPlugin(
            "test.optional",
            requirements: [new ServiceRequirement(typeof(IMarker), Optional: true)],
            onActivate: () => activated++);

        var handle = await kernel.RegisterAsync(() => plugin);

        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task Required_service_appearing_later_activates_blocked_consumer()
    {
        await using var kernel = new DougongKernel();
        var activated = 0;
        var consumer = new GraphPlugin(
            "test.consumer",
            requirements: [new ServiceRequirement(typeof(IMarker))],
            onActivate: () => activated++);
        var blocked = await kernel.RegisterAsync(() => consumer);
        Assert.Equal(PluginState.Blocked, blocked.State);

        await kernel.RegisterAsync(() => new GraphPlugin("test.provider", registerMarker: true));

        var handle = (await kernel.GetPluginsAsync()).Single(p => p.Id == consumer.Manifest.Id);
        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task Dependency_activation_order_follows_declared_dependencies()
    {
        await using var kernel = new DougongKernel();
        var order = new List<string>();
        var baseId = PluginId.Parse("test.base");
        var middleId = PluginId.Parse("test.middle");
        await kernel.RegisterAsync(() => new GraphPlugin("test.top", dependsOn: [middleId],
            onActivate: () => order.Add("top")));
        await kernel.RegisterAsync(() => new GraphPlugin("test.middle", dependsOn: [baseId],
            onActivate: () => order.Add("middle")));
        await kernel.RegisterAsync(() => new GraphPlugin("test.base",
            onActivate: () => order.Add("base")));

        Assert.Equal(["base", "middle", "top"], order);
    }

    [Fact]
    public async Task Missing_plugin_dependency_is_reported_as_blocked_reason()
    {
        await using var kernel = new DougongKernel();
        var seen = new List<PluginStateChange>();
        using var subscription = kernel.SubscribeStateChanges(change =>
        {
            if (change.PluginId.Value == "test.orphan") seen.Add(change);
        });

        var handle = await kernel.RegisterAsync(() =>
            new GraphPlugin("test.orphan", dependsOn: [PluginId.Parse("test.never.registered")]));

        Assert.Equal(PluginState.Blocked, handle.State);
        Assert.Contains(seen, change => change.To == PluginState.Blocked
            && change.Reason is not null && change.Reason.Contains("依赖"));
    }

    [Fact]
    public async Task Cyclic_dependency_chain_is_reported_with_full_path()
    {
        await using var kernel = new DougongKernel();
        var mid = PluginId.Parse("test.mid");
        await kernel.RegisterAsync(() => new GraphPlugin("test.top", dependsOn: [mid]));

        // test.mid -> test.top -> test.mid，注册时即拒绝并给出完整路径。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.RegisterAsync(
                () => new GraphPlugin("test.mid", dependsOn: [PluginId.Parse("test.top")])));

        Assert.Contains("test.mid", error.Message);
        Assert.Contains("test.top", error.Message);
        Assert.Single(await kernel.GetPluginsAsync());
    }

    private sealed class GraphPlugin(
        string id,
        IReadOnlyList<PluginId>? dependsOn = null,
        IReadOnlyList<ServiceRequirement>? requirements = null,
        Action? onActivate = null,
        bool registerMarker = false) : IPlugin
    {
        public PluginManifest Manifest { get; } = new(
            PluginId.Parse(id),
            "1.0.0",
            requirements ?? [],
            dependsOn ?? []);

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            if (registerMarker) context.Services.Register<IMarker>(new Marker());
            onActivate?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
