namespace Dougong.Kernel;

/// <summary>
/// 本插件作用域内的服务注册表视图。注册归属调用它的插件，清理时随作用域一并移除。
/// </summary>
public interface IServiceRegistry
{
    /// <summary>注册服务；同一插件重复注册同一键视为编程错误。</summary>
    void Register<TService>(TService service, string? name = null)
        where TService : class;

    /// <summary>解析当前快照中唯一的提供方；缺失或存在多个同键提供方时返回 null。</summary>
    TService? Resolve<TService>(string? name = null)
        where TService : class;

    /// <summary>解析当前快照中的全部提供方。</summary>
    IReadOnlyList<TService> ResolveAll<TService>(string? name = null)
        where TService : class;
}
