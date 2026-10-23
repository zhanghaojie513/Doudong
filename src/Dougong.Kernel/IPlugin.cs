namespace Dougong.Kernel;

/// <summary>
/// 内核承载的插件。实现者不提供停用方法：停用即释放 <see cref="PluginContext"/> 作用域，
/// 由 <see cref="PluginContext.RegisterForCleanup"/> 登记的收尾回调被执行。
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken);
}

/// <summary>宿主可用的插件句柄；状态是快照，后续变化通过内核的 <c>StateChanged</c> 观察。</summary>
public sealed record PluginHandle(PluginId Id, PluginManifest Manifest, PluginState State);
