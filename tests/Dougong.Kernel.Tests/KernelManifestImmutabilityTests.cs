using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 清单是不可变快照：调用方持有并修改传入集合，不能改变内核的依赖判定。
/// 也覆盖 PluginId/ContractVersion 的输入校验边界。
/// </summary>
public sealed class KernelManifestImmutabilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private interface IMarker;
    private sealed class Marker : IMarker;

    private sealed class P(PluginManifest manifest, Action<PluginContext>? activate = null) : IPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            activate?.Invoke(context);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Clearing_the_source_list_cannot_bypass_a_required_dependency()
    {
        await using var kernel = new DougongKernel();
        var requirements = new List<ServiceRequirement> { new(typeof(IMarker)) };
        var manifest = new PluginManifest(new PluginId("immut.consumer"), "1.0.0", requirements);
        var plugin = new P(manifest);

        requirements.Clear();

        var handle = await kernel.RegisterAsync(() => plugin).AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginState.Blocked, handle.State);
    }

    [Fact]
    public async Task Injecting_a_requirement_after_activation_does_not_change_evaluation()
    {
        await using var kernel = new DougongKernel();
        var provider = new P(PluginManifest.Create("immut.provider", "1.0.0"),
            context => context.Services.Register<IMarker>(new Marker()));
        var requirements = new List<ServiceRequirement>();
        var consumer = new P(new PluginManifest(new PluginId("immut.consumer"), "1.0.0", requirements));

        await kernel.RegisterAsync(() => provider).AsTask().WaitAsync(Timeout);
        var handle = await kernel.RegisterAsync(() => consumer).AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginState.Active, handle.State);

        // 往同一集合注入一个无法满足的需求，不得影响已经提交的清单快照。
        requirements.Add(new ServiceRequirement(typeof(IDisposable)));
        await kernel.DeactivateAsync(new PluginId("immut.provider")).AsTask().WaitAsync(Timeout);

        var state = (await kernel.GetPluginsAsync())
            .Single(p => p.Id.Value == "immut.consumer").State;
        Assert.Equal(PluginState.Active, state);
    }

    [Fact]
    public void Manifest_exposes_read_only_copies()
    {
        var requirements = new List<ServiceRequirement> { new(typeof(IMarker), Optional: true) };
        var dependsOn = new List<PluginId> { new("other") };
        var contracts = new List<ContractVersion> { new("Dougong.Kernel", "0.1") };
        var manifest = new PluginManifest(new PluginId("immut.copy"), "1.0.0",
            requirements, dependsOn, contracts);

        requirements.Clear();
        dependsOn.Clear();
        contracts.Clear();

        Assert.Single(manifest.RequiredServices);
        Assert.Single(manifest.DependsOn);
        Assert.Single(manifest.ContractVersions);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2extra")]
    public void Invalid_contract_range_is_rejected_when_evaluated(string range)
    {
        // ContractVersion 构造时不解析，校验发生在清单构造或 IsSatisfiedBy 时。
        Assert.Throws<FormatException>(() => new PluginManifest(
            new PluginId("immut.range"), "1.0.0",
            contractVersions: [new ContractVersion("Dougong.Kernel", range)]));
        Assert.Throws<FormatException>(
            () => new ContractVersion("Dougong.Kernel", range).IsSatisfiedBy(new Version(1, 2)));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1..2")]
    [InlineData(" 1.2 ")]
    public void Extra_or_malformed_segments_are_currently_tolerated(string range)
    {
        // 已知宽松点：只要求至少两段且前两段可解析，多余段被忽略。
        // 这里显式锁定当前行为，避免将来无意改变；若要收紧应作为独立变更。
        var contract = new ContractVersion("Dougong.Kernel", range);
        Assert.True(contract.IsSatisfiedBy(new Version(1, 2)));
    }
}
