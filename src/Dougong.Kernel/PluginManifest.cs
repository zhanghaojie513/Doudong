namespace Dougong.Kernel;

/// <summary>
/// 一条服务需求。它属于消费方，可以指向当前尚不存在的提供方：
/// 内核保存未满足的需求，用于后续重新评估。<see cref="Optional"/> 为 true 时，
/// 缺失不会阻止激活，但提供方后续出现时仍会触发重新评估。
/// <see cref="ProviderId"/> 非空时表示同键多提供方中只接受该插件，
/// 用于显式消解歧义；此时不再按唯一性判断。
/// </summary>
public sealed record ServiceRequirement(
    Type ServiceType,
    string? Name = null,
    bool Optional = false,
    PluginId? ProviderId = null)
{
    /// <summary>该需求是否排斥除 <paramref name="owner"/> 之外的提供方。</summary>
    public bool Accepts(PluginId owner) => ProviderId is not { } required || required == owner;
}

/// <summary>
/// 插件清单。版本约束与程序集兼容检查属于加载层，这里只表达运行时所需的最小信息。
/// <see cref="DependsOn"/> 是插件级依赖：被依赖插件必须已激活，本插件才能激活。
/// </summary>
public sealed record PluginManifest
{
    public PluginManifest(
        PluginId id,
        string version,
        IReadOnlyList<ServiceRequirement>? requiredServices = null,
        IReadOnlyList<PluginId>? dependsOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;
        RequiredServices = requiredServices ?? [];
        DependsOn = dependsOn ?? [];
    }

    public PluginId Id { get; }

    public string Version { get; }

    /// <summary>包含必需与可选服务需求；用 <see cref="ServiceRequirement.Optional"/> 区分。</summary>
    public IReadOnlyList<ServiceRequirement> RequiredServices { get; }

    /// <summary>必须已处于 <see cref="PluginState.Active"/> 的插件。</summary>
    public IReadOnlyList<PluginId> DependsOn { get; }

    public static PluginManifest Create(string id, string version, params ServiceRequirement[] requiredServices) =>
        new(PluginId.Parse(id), version, requiredServices);

    public static PluginManifest CreateWithDependencies(
        string id,
        string version,
        IReadOnlyList<ServiceRequirement> requiredServices,
        IReadOnlyList<PluginId> dependsOn) =>
        new(PluginId.Parse(id), version, requiredServices, dependsOn);
}
