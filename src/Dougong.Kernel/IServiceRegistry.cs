namespace Dougong.Kernel;

/// <summary>
/// 本插件作用域内的服务注册表视图。注册归属调用它的插件，清理时随作用域一并移除。
/// <see cref="Resolve{TService}"/> 与 <see cref="ResolveAll{TService}"/> 返回未跟踪的裸引用，
/// 且只在激活回调期间可用；窗口外调用会抛出 <see cref="InvalidOperationException"/>。
/// 运行时跨插件访问必须使用 <see cref="PluginContext.Resolver"/> 取得受跟踪调用引用。
/// </summary>
public interface IServiceRegistry
{
    /// <summary>
    /// 仅在激活回调期间注册服务；同一插件同代次重复注册同一键视为编程错误。
    /// 注册在激活成功后整批发布，失败或关闭时丢弃；Active 后新增注册会抛出异常。
    /// </summary>
    void Register<TService>(TService service, string? name = null)
        where TService : class;

    /// <summary>
    /// 解析唯一提供方；缺失或同键歧义时返回 null。
    /// 激活期间包含本代次暂存服务，其余提供方只读取已提交服务。
    /// </summary>
    TService? Resolve<TService>(string? name = null)
        where TService : class;

    /// <summary>解析当前快照中的全部提供方。</summary>
    IReadOnlyList<TService> ResolveAll<TService>(string? name = null)
        where TService : class;
}
