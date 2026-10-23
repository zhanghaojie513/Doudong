using Dougong.Kernel;
using Dougong.Sample.Shared;

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

/// <summary>清理必然失败，用于验证失败不得被当作成功卸载。</summary>
public sealed class FailingCleanupPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create("dougong.sample.failing-cleanup", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.RegisterForCleanup(_ => throw new InvalidOperationException("cleanup failure"));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 使用共享契约程序集注册服务并订阅/发布事件，验证跨 ALC 类型同一性。
/// </summary>
public sealed class SharedContractPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        "dougong.sample.shared", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Services.Register<IGreetingService>(new GreetingService());
        context.Events.Subscribe<GreetingEvent>(GreetingProbe.Report);
        context.Events.Publish(new GreetingEvent("hello-from-plugin"));
        return ValueTask.CompletedTask;
    }

    private sealed class GreetingService : IGreetingService
    {
        public string Greet(string name) => $"hello, {name}";
    }
}
