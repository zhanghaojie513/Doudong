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
    private readonly List<string> _unregisteredHostFallbacks = [];

    public PluginLoadContext(string assemblyPath, SharedContractRegistry shared)
        : base($"Dougong.Plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _shared = shared;
    }

    /// <summary>
    /// 本次装配里"不在插件目录、也未登记、却已存在于宿主进程"的非框架程序集。
    /// </summary>
    /// <remarks>
    /// 这是要拒绝的情形：返回 null 会让运行时回落到默认 ALC 并静默绑定成功，
    /// 于是"某个无关代码恰好先把该程序集装进进程"就能让未登记的插件加载通过，
    /// 「登记是加载前置条件」被绕过；同一份插件在干净进程里失败、在别处成功。
    /// <para>
    /// 为什么排除框架程序集：<c>System.Runtime</c>、<c>netstandard</c> 之类
    /// 本来就不会出现在插件目录里，它们回落到共享框架是唯一正确行为。
    /// 只有插件自己声明依赖的第三方/契约程序集才适用"必须登记"的规则。
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> UnregisteredHostFallbacks
    {
        get { lock (_unregisteredHostFallbacks) return [.. _unregisteredHostFallbacks]; }
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

        // 插件目录能解析就用插件自带的副本——正常依赖，与登记无关。
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);

        if (assemblyName.Name is { Length: > 0 } name
            && !IsFrameworkAssembly(assemblyName)
            && AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            lock (_unregisteredHostFallbacks)
            {
                if (!_unregisteredHostFallbacks.Contains(name, StringComparer.OrdinalIgnoreCase))
                    _unregisteredHostFallbacks.Add(name);
            }
        }

        return null;
    }

    /// <summary>
    /// 框架程序集：位于共享框架目录，不属于插件自己的依赖。
    /// 它们回落到共享框架是正常行为，不该被"必须登记"的规则约束。
    /// </summary>
    private static bool IsFrameworkAssembly(AssemblyName assemblyName)
    {
        // 共享框架里的程序集没有 CodeBase 路径，或位于 dotnet 安装目录下。
        if (assemblyName.Name is { Length: > 0 } name)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (loaded is not null)
            {
                var location = loaded.IsDynamic ? null : loaded.Location;
                if (string.IsNullOrEmpty(location) || IsUnderDotnetRoot(location)) return true;
            }
        }

        // 尚未加载的同名程序集：按名字前缀判断常见框架程序集。
        // 这只是保守兜底，主判据是上面的路径检查。
        var simple = assemblyName.Name ?? string.Empty;
        return simple.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || simple.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || simple is "System" or "mscorlib" or "netstandard"
            || simple.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDotnetRoot(string location)
    {
        var root = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(root)) return false;

        // typeof(object) 在 System.Private.CoreLib 里，其目录即共享框架目录。
        return location.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
