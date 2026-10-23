using System.Reflection;
using System.Runtime.Loader;

namespace Dougong.Kernel;

/// <summary>
/// 可回收插件加载上下文。共享契约始终解析到宿主程序集，保证类型同一性；
/// 其余依赖由 <see cref="AssemblyDependencyResolver"/> 从插件目录解析。
/// ALC 只隔离依赖版本，不隔离权限或进程故障。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedContracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dougong.Kernel",
    };

    /// <summary>宿主显式声明共享的额外契约程序集；不会默认共享插件私有依赖。</summary>
    private static readonly HashSet<string> HostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>登记宿主与插件共享的契约程序集名，须在加载插件前调用。</summary>
    public static void RegisterSharedContract(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.GetName().Name is { } name) HostSharedAssemblies.Add(name);
    }

    public PluginLoadContext(string assemblyPath)
        : base($"Dougong.Plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name
            && (SharedContracts.Contains(name) || HostSharedAssemblies.Contains(name)))
        {
            // 先返回宿主已加载的实例，避免同一契约在不同 ALC 中各有一份。
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
