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

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string assemblyPath)
        : base($"Dougong.Plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedContracts.Contains(name))
        {
            // 返回 null 让默认上下文提供宿主已加载的实例。
            return null;
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
