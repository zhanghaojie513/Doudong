using System.Reflection;
using System.Runtime.CompilerServices;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelDynamicLoadingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string SampleAssemblyPath
    {
        get
        {
            var here = AppContext.BaseDirectory;
            var candidate = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "..",
                "samples", "Dougong.Sample.Plugin", "bin",
                Configuration, "net10.0", "Dougong.Sample.Plugin.dll"));
            Assert.True(File.Exists(candidate), $"样例插件未构建：{candidate}");
            return candidate;
        }
    }

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task Load_activates_plugin_in_collectible_context()
    {
        await using var kernel = new DougongKernel();

        var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);

        Assert.Equal("dougong.sample.plugin", handle.Id.Value);
        Assert.Equal(PluginState.Active, handle.State);
        var assembly = kernel.GetLoadedAssembly(handle.Id);
        Assert.NotNull(assembly);
        Assert.True(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly)!.IsCollectible);
        Assert.NotSame(typeof(IPlugin).Assembly, assembly);
    }

    [Fact]
    public async Task Unload_stops_plugin_and_removes_it_from_the_kernel()
    {
        await using var kernel = new DougongKernel();
        await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);

        await kernel.UnloadAsync(PluginId.Parse("dougong.sample.plugin")).AsTask().WaitAsync(Timeout);

        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Unload_rejects_statically_registered_plugins()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new StaticPlugin());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.UnloadAsync(PluginId.Parse("test.static")));
    }

    [Fact]
    public async Task Repeated_load_and_unload_does_not_leak_assemblies()
    {
        await using var kernel = new DougongKernel();
        var references = new List<WeakReference>();

        for (var i = 0; i < 3; i++)
        {
            var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
            Assert.Equal(PluginState.Active, handle.State);
            references.Add(new WeakReference(kernel.GetLoadedAssembly(handle.Id)));
            await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        }

        // 卸载是协作式的：只要求可回收上下文的程序集最终被回收，不承诺立即释放。
        for (var attempt = 0; attempt < 20 && references.Any(r => r.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50);
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive,
            "卸载后插件程序集仍被强引用持有。"));
    }

    [Fact]
    public async Task Missing_assembly_is_rejected()
    {
        await using var kernel = new DougongKernel();

        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await kernel.LoadAsync(Path.Combine(Path.GetTempPath(), "missing-plugin.dll")));
    }

    [Fact]
    public async Task Unknown_type_name_is_rejected()
    {
        await using var kernel = new DougongKernel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.LoadAsync(SampleAssemblyPath, "Dougong.Sample.Plugin.DoesNotExist"));
    }

    [Fact]
    public async Task Loaded_plugin_services_are_released_on_unload()
    {
        await using var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(SampleAssemblyPath).AsTask().WaitAsync(Timeout);
        var serviceType = kernel.GetLoadedAssembly(handle.Id)!
            .GetType("Dougong.Sample.Plugin.ISampleService")!;

        var before = ResolveByReflection(kernel, serviceType);
        Assert.NotNull(before);

        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);

        Assert.Null(ResolveByReflection(kernel, serviceType));
    }

    private static object? ResolveByReflection(DougongKernel kernel, Type serviceType)
    {
        var method = typeof(DougongKernel)
            .GetMethod(nameof(DougongKernel.ResolveDiagnostic), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(serviceType);
        return method.Invoke(kernel, [null]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly CaptureAssembly() => Assembly.LoadFrom(SampleAssemblyPath);

    private sealed class StaticPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("test.static", "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
