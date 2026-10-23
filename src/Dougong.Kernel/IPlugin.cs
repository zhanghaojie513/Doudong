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

/// <summary>
/// 宿主可用的插件快照。只包含可跨 ALC 边界的共享数据：不再携带
/// <see cref="PluginManifest"/>，以免长期保留的动态插件类型阻止程序集回收。
/// 需要完整清单时显式调用 <c>DougongKernel.GetPluginManifestAsync</c>。
/// </summary>
public sealed record PluginSummary(PluginId Id, string Version, PluginState State);

/// <summary>宿主可用的插件句柄；状态是快照，后续变化通过内核的状态订阅观察。</summary>
public sealed record PluginHandle(PluginId Id, string Version, PluginState State);
