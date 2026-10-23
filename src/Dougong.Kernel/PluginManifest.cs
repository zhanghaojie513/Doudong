namespace Dougong.Kernel;

/// <summary>
/// 一条服务需求。它属于消费方，可以指向当前尚不存在的提供方：
/// 内核保存未满足的需求，用于后续重新评估。
/// </summary>
public sealed record ServiceRequirement(Type ServiceType, string? Name = null)
{
    public bool IsSatisfiedBy(Type providedType, string? providedName) =>
        ServiceType.IsAssignableFrom(providedType)
        && string.Equals(Name, providedName, StringComparison.Ordinal);
}

/// <summary>
/// 插件清单。版本约束与程序集兼容检查属于加载层，这里只表达运行时所需的最小信息。
/// </summary>
public sealed record PluginManifest
{
    public PluginManifest(PluginId id, string version, IReadOnlyList<ServiceRequirement>? requiredServices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;
        RequiredServices = requiredServices ?? [];
    }

    public PluginId Id { get; }

    public string Version { get; }

    public IReadOnlyList<ServiceRequirement> RequiredServices { get; }

    public static PluginManifest Create(string id, string version, params ServiceRequirement[] requiredServices) =>
        new(PluginId.Parse(id), version, requiredServices);
}
