namespace Dougong.Kernel;

/// <summary>
/// 运行状态。用户是否希望启用（desired state）与此分开保存：
/// 插件可以在被期望启用的情况下处于 <see cref="Blocked"/>。
/// </summary>
public enum PluginState
{
    /// <summary>已登记，尚未尝试激活。</summary>
    Registered,

    /// <summary>正在执行激活，服务尚未提交到解析视图。</summary>
    Activating,

    /// <summary>已激活，服务已提交，可被解析。</summary>
    Active,

    /// <summary>正在排空并清理。</summary>
    Deactivating,

    /// <summary>已停止，但代码可能仍处于已加载状态，可再次激活。</summary>
    Stopped,

    /// <summary>仍期望启用，但必需依赖不满足；提供方恢复后重新评估。</summary>
    Blocked,

    /// <summary>本次操作在受控边界内失败，带诊断信息。</summary>
    Failed
}

/// <summary>一次状态迁移的记录。诊断只保留共享字符串数据，不持有插件异常对象。</summary>
public sealed record PluginStateChange(
    PluginId PluginId,
    PluginState From,
    PluginState To,
    string? Reason = null,
    PluginDiagnostic? Diagnostic = null);
