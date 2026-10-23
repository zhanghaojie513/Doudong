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
    private static readonly HashSet<string> KernelContracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dougong.Kernel",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly SharedContractRegistry _shared;

    public PluginLoadContext(string assemblyPath, SharedContractRegistry shared)
        : base($"Dougong.Plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _shared = shared;
    }

    /// <summary>该程序集是否应解析到宿主实例：内核契约或宿主显式登记的共享契约。</summary>
    internal static bool IsHostShared(string? name, SharedContractRegistry shared) =>
        name is not null && (KernelContracts.Contains(name) || shared.Contains(name));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostShared(assemblyName.Name, _shared))
        {
            // 先返回宿主已加载的实例，避免同一契约在不同 ALC 中各有一份。
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
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
