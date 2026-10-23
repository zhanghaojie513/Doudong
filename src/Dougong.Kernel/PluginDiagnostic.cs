namespace Dougong.Kernel;

/// <summary>
/// 共享诊断数据：只保留字符串与结构，不持有插件 Type、委托或原始异常对象，
/// 避免状态历史成为插件 ALC 的长期 GC 根。
/// </summary>
public sealed record PluginDiagnostic(string Code, string Message);

/// <summary>状态投影中的一条记录；用于从快照和增量重建插件集合。</summary>
public enum StateDeltaKind
{
    /// <summary>一次状态迁移。</summary>
    StateChange,

    /// <summary>插件已登记（此前不在投影中）。</summary>
    Registered,

    /// <summary>插件已从内核移除。</summary>
    Removed,
}
