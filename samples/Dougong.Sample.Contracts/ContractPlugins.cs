using Dougong.Kernel;

namespace Dougong.Sample.Contracts;

/// <summary>声明了不兼容主/次版本的契约，用于验证宿主拒载。</summary>
public sealed class IncompatibleContractPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.CreateWithContracts(
        "dougong.sample.incompatible",
        "1.0.0",
        [new ContractVersion("Dougong.Kernel", "999.0")]);

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>声明了宿主不提供的契约，用于验证拒载。</summary>
public sealed class UnknownContractPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.CreateWithContracts(
        "dougong.sample.unknown",
        "1.0.0",
        [new ContractVersion("Dougong.NotProvided", "0.1")]);

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>声明了兼容契约版本，用于验证正常加载。</summary>
public sealed class CompatibleContractPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.CreateWithContracts(
        "dougong.sample.compatible",
        "1.0.0",
        [new ContractVersion("Dougong.Kernel", "0.1")]);

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
