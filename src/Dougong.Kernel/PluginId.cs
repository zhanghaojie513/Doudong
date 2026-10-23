namespace Dougong.Kernel;

/// <summary>插件的稳定标识，来自清单，在进程内唯一。</summary>
public readonly record struct PluginId(string Value)
{
    public override string ToString() => Value;

    public static PluginId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new PluginId(value);
    }
}
