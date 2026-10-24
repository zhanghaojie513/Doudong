using System.Reflection;

namespace Dougong.Kernel;

public sealed partial class DougongKernel
{
    /// <summary>
    /// 从程序集动态加载插件。程序集进入独立可回收上下文，Kernel 契约由宿主共享。
    /// 只加载可信进程内插件；ALC 不提供权限或故障隔离。
    /// </summary>
    public async ValueTask<PluginHandle> LoadAsync(
        string assemblyPath,
        string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLifecycleCallAllowed();
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"插件程序集不存在：{fullPath}", fullPath);

        var context = new PluginLoadContext(fullPath, _sharedContracts);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            var pluginType = FindPluginType(assembly, typeName);
            var manifest = ReadManifest(pluginType);
            EnsureContractsCompatible(manifest);

            // 装配时曾回落到宿主进程里的同名程序集，而它既不在插件目录、
            // 也没登记为共享契约。此时插件"看起来加载成功"，实际绑定的是
            // 宿主里恰好存在的那个程序集——换到干净进程就会失败。
            if (context.UnregisteredHostFallbacks is { Count: > 0 } fallbacks)
                throw new InvalidOperationException(DescribeUnregisteredFallback(fallbacks));

            var loaded = new LoadedPlugin(context, pluginType, manifest.Id);
            var handle = await RegisterCoreAsync(
                () => CreatePlugin(loaded), manifest, loaded, cancellationToken).ConfigureAwait(false);
            return handle;
        }
        catch (Exception ex) when (FindMissingAssembly(ex) is { } missing)
        {
            context.Unload();
            throw new InvalidOperationException(
                DescribeMissingAssembly(missing), ex);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary>沿内部异常查找缺失程序集名；反射类型加载错误会包一层。</summary>
    private static string? FindMissingAssembly(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException { FileName: { } name }) return name;
            if (current is System.Reflection.ReflectionTypeLoadException { LoaderExceptions: { } inner })
                foreach (var loaderException in inner)
                    if (FindMissingAssembly(loaderException) is { } nested) return nested;
        }

        return null;
    }

    private static string DescribeMissingAssembly(string missing) =>
        $"无法加载程序集 '{missing}'。若它是宿主与插件共享的契约，"
        + $"请在加载前调用 {nameof(DougongKernel)}.{nameof(RegisterSharedContract)} 登记；"
        + "否则请确认该依赖随插件一同部署。";

    /// <summary>
    /// 插件装配回落到宿主进程中未登记同名程序集时的诊断。
    /// </summary>
    /// <remarks>
    /// 这种失败最隐蔽：在"跑过相关代码"的进程里能加载，在干净进程里失败。
    /// 诊断必须同时说明发生了什么与怎么改，否则使用者只会看到"有时能跑"。
    /// </remarks>
    private static string DescribeUnregisteredFallback(IReadOnlyList<string> names) =>
        $"程序集 {string.Join("、", names.Select(n => $"'{n}'"))} 不在插件目录中，"
        + "也未登记为共享契约，但宿主进程中已存在同名程序集，装配被静默回落到宿主。"
        + $"请显式登记：{nameof(DougongKernel)}.{nameof(RegisterSharedContract)}，"
        + "或把这些程序集随插件一同部署。";

    /// <summary>
    /// 停止并卸载动态插件：先停用排空，再释放工厂、实例与服务引用，最后调用 ALC.Unload()。
    /// 卸载是协作式的，回收需要 GC 实际执行；此方法不承诺立即释放内存。
    /// </summary>
    public async ValueTask UnloadAsync(PluginId id, CancellationToken cancellationToken = default)
    {
        EnsureLifecycleCallAllowed();
        cancellationToken.ThrowIfCancellationRequested();
        LoadedPlugin loaded;
        long generation;
        lock (_sync)
        {
            var entry = RequireEntry(id);
            loaded = entry.Loaded
                ?? throw new InvalidOperationException($"插件 '{id}' 不是动态加载的，不能卸载。");
            EnsureReusable(entry);
            if (entry.Unloading)
                throw new InvalidOperationException($"插件 '{id}' 正在卸载。");
            entry.Unloading = true;
            entry.DesiredActive = false;
            generation = entry.Generation;
        }

        try
        {
            await DeactivateGenerationAsync(id, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(id, out var entry)) entry.Unloading = false;
            }
            throw;
        }

        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            // 停用过程新产生的失败或超时同样阻止卸载。
            if (entry.Generation != generation || !ReferenceEquals(entry.Loaded, loaded)
                || entry.State != PluginState.Stopped || entry.Context is not null
                || entry.CleanupFailed || entry.IsolationReason is not null)
            {
                entry.Unloading = false;
                throw new InvalidOperationException(
                    $"插件 '{id}' 未能干净停止（{entry.State}），拒绝卸载以免留下未释放资源。");
            }

            entry.PendingServices.Clear();
            _services.RemoveOwner(id, entry.Generation);
            ForgetDependencies(id, entry.Generation);
            _entries.Remove(id);
            CommitRemoved(entry);
        }

        loaded.Detach();
    }

    private async ValueTask<PluginHandle> RegisterCoreAsync(
        Func<IPlugin> factory,
        PluginManifest manifest,
        LoadedPlugin? loaded,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.ContainsKey(manifest.Id))
                throw new InvalidOperationException($"插件 '{manifest.Id}' 已登记。");
            if (FindDependencyCycle(manifest.Id, manifest) is { } cycle)
                throw new InvalidOperationException(
                    $"插件依赖成环：{string.Join(" -> ", cycle)}。");
            var entry = new PluginEntry
            {
                Id = manifest.Id, Manifest = manifest, Factory = factory,
                DesiredActive = true, Loaded = loaded,
            };
            _entries.Add(manifest.Id, entry);
            CommitRegistered(entry);
        }

        try
        {
            await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            var handle = GetHandle(manifest.Id);

            // 动态加载的插件若因缺少程序集而激活失败，几乎必然是宿主漏配共享契约。
            // 这种配置错误不应留下不可卸载的隔离条目，而是回滚并抛出可操作异常。
            if (loaded is not null && handle.State == PluginState.Failed
                && FindActivationLoadError(manifest.Id) is { } loadError)
            {
                RollBackRegistration(manifest.Id, loaded);
                throw loadError;
            }

            return handle;
        }
        catch (LifecycleConfigurationException)
        {
            RollBackRegistration(manifest.Id, loaded);
            throw;
        }
        catch
        {
            // 登记已提交但评估失败：撤销本次条目的启用意图，并按身份回滚。
            RollBackRegistration(manifest.Id, loaded);
            throw;
        }
    }

    private sealed class LifecycleConfigurationException(string message, Exception inner)
        : InvalidOperationException(message, inner);

    private LifecycleConfigurationException? FindActivationLoadError(PluginId id)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.CleanupFailed) return null;
            if (FindMissingAssembly(entry.LastActivationError) is not { } missing) return null;
            return new LifecycleConfigurationException(
                DescribeMissingAssembly(missing), entry.LastActivationError!);
        }
    }

    private void RollBackRegistration(PluginId id, LoadedPlugin? loaded)
    {
        bool remove = false;
        lock (_sync)
        {
            remove = _entries.TryGetValue(id, out var entry)
                && ReferenceEquals(entry.Loaded, loaded)
                && entry.Context is null
                && entry.State is PluginState.Registered or PluginState.Blocked or PluginState.Failed;
            if (remove)
            {
                _entries.Remove(id);
                ForgetDependencies(id, 0);
                CommitRemoved(entry!);
            }
            else if (_entries.TryGetValue(id, out var retained))
            {
                retained.DesiredActive = false;
            }
        }

        if (remove) loaded?.Detach();
    }

    private static Type FindPluginType(Assembly assembly, string? typeName)
    {
        if (typeName is not null)
        {
            var explicitType = assembly.GetType(typeName, throwOnError: false)
                ?? throw new InvalidOperationException($"程序集内找不到类型 '{typeName}'。");
            if (!typeof(IPlugin).IsAssignableFrom(explicitType))
                throw new InvalidOperationException($"类型 '{typeName}' 未实现 IPlugin。");
            return explicitType;
        }

        var candidates = assembly.GetTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("程序集内没有实现 IPlugin 的公开类型。"),
            _ => throw new InvalidOperationException(
                $"程序集内有 {candidates.Length} 个 IPlugin 类型，请显式指定 typeName。"),
        };
    }

    private static PluginManifest ReadManifest(Type pluginType)
    {
        var instance = (IPlugin)Activator.CreateInstance(pluginType)!;
        return instance.Manifest
            ?? throw new InvalidOperationException($"插件 '{pluginType.FullName}' 的清单为 null。");
    }

    private static IPlugin CreatePlugin(LoadedPlugin loaded) =>
        (IPlugin)Activator.CreateInstance(loaded.PluginType)!;

    /// <summary>
    /// 校验插件声明的共享契约版本范围。未声明的契约不做限制；
    /// 声明了但宿主未提供，或范围不匹配，都拒绝加载。
    /// </summary>
    private void EnsureContractsCompatible(PluginManifest manifest)
    {
        List<string>? problems = null;
        foreach (var contract in manifest.ContractVersions)
        {
            if (!ContractVersions.TryGetValue(contract.ContractName, out var hostVersion))
            {
                (problems ??= []).Add($"宿主未提供契约 '{contract.ContractName}'。");
                continue;
            }

            if (!contract.IsSatisfiedBy(hostVersion))
            {
                (problems ??= []).Add(
                    $"契约 '{contract.ContractName}' 需要 {contract.Range} 及以上的兼容版本，"
                    + $"宿主为 {hostVersion}。");
            }
        }

        if (problems is null) return;
        throw new InvalidOperationException(
            $"插件 '{manifest.Id}' 的契约版本不兼容：{string.Join(" ", problems)}");
    }

    /// <summary>诊断用：取得动态插件的入口程序集，供装卸测试检查加载上下文。</summary>
    internal Assembly? GetLoadedAssembly(PluginId id)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(id, out var entry) && entry.Loaded is { } loaded
                ? loaded.PluginType.Assembly
                : null;
        }
    }

    /// <summary>诊断用：按类型解析已提交服务，不参与插件运行时调用路径。</summary>
    internal TService? ResolveDiagnostic<TService>(string? name = null) where TService : class
    {
        lock (_sync)
        {
            var matches = _services.Find(typeof(TService), name);
            return matches.Count == 1 ? (TService)matches[0].Instance : null;
        }
    }

    internal IReadOnlyList<TService> ResolveAllDiagnostic<TService>(string? name = null) where TService : class =>
        [.. _services.Find(typeof(TService), name).Select(e => (TService)e.Instance)];

    /// <summary>诊断用：按运行时 Type 解析，供跨 ALC 契约测试使用。</summary>
    internal object? ResolveDiagnosticByType(Type serviceType, string? name = null) =>
        _services.Find(serviceType, name) is { Count: 1 } matches ? matches[0].Instance : null;

    /// <summary>
    /// 裸解析只允许发生在激活回调窗口内；窗口外必须使用带租约的 <see cref="Resolver"/>，
    /// 否则停用排空无法覆盖这些调用。
    /// </summary>
    internal void EnsureActivationResolveAllowed(PluginId owner, long generation)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(owner, out var entry)
                && entry.Generation == generation
                && entry.State == PluginState.Activating
                && entry.Context is { IsClosed: false }
                && ReferenceEquals(_activationScope.Value, entry.Context))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"插件 '{owner}' 只能在激活回调期间使用 Services.Resolve；"
            + "运行时跨插件访问请改用 Resolver 取得受跟踪调用引用。");
    }

    /// <summary>动态加载的插件及其可回收上下文；Detach 后不再持有程序集强引用。</summary>
    internal sealed class LoadedPlugin(PluginLoadContext context, Type pluginType, PluginId id)
    {
        private PluginLoadContext? _context = context;
        private Type? _pluginType = pluginType;

        public PluginId Id { get; } = id;

        public Type PluginType => _pluginType
            ?? throw new InvalidOperationException($"插件 '{Id}' 的程序集引用已释放。");
        public void Detach()
        {
            var loadedContext = Interlocked.Exchange(ref _context, null);
            Interlocked.Exchange(ref _pluginType, null);
            loadedContext?.Unload();
        }

        public bool TryUnload() => Volatile.Read(ref _context) is null;
    }
}
