using System.Runtime.CompilerServices;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// M0 验收反例集（architecture.md §3.7）中尚未被其他测试覆盖的部分：
/// 可选空集合后新增、跨 ALC 类型同一性、多轮真实调用后的卸载。
/// </summary>
public sealed class KernelM0AcceptanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private interface IOptional;

    private sealed class OptionalService : IOptional;

    private sealed class OptionalProvider(string id, bool registers) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            if (registers) context.Services.Register<IOptional>(new OptionalService());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OptionalConsumer : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(
            "test.consumer", "1.0.0",
            new ServiceRequirement(typeof(IOptional), Optional: true));

        public PluginContext? Context { get; private set; }
        public int Activations { get; private set; }

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            Activations++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyProvider(string id) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(id, "1.0.0");

        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Optional_service_arriving_after_activation_becomes_resolvable()
    {
        await using var kernel = new DougongKernel();
        var consumer = new OptionalConsumer();
        await kernel.RegisterAsync(() => consumer).AsTask().WaitAsync(Timeout);
        Assert.Equal(1, consumer.Activations);
        Assert.Null(kernel.Resolver.Resolve<IOptional>());

        await kernel.RegisterAsync(() => new OptionalProvider("test.optional", registers: true))
            .AsTask().WaitAsync(Timeout);

        // 可选需求不重启消费者，但新提供方必须对后续解析可见。
        Assert.Equal(1, consumer.Activations);
        var reference = kernel.Resolver.Resolve<IOptional>();
        Assert.NotNull(reference);
        var invoked = false;
        await reference.InvokeAsync((_, _) =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });
        Assert.True(invoked);
    }

    [Fact]
    public async Task Optional_service_action_after_empty_set_adds_new_provider()
    {
        await using var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new EmptyProvider("test.empty")).AsTask().WaitAsync(Timeout);
        await kernel.RegisterAsync(() => new OptionalProvider("test.optional", registers: true))
            .AsTask().WaitAsync(Timeout);

        Assert.NotNull(kernel.Resolver.Resolve<IOptional>());
    }

    [Fact]
    public async Task Optional_provider_removal_does_not_block_the_consumer()
    {
        await using var kernel = new DougongKernel();
        var consumer = new OptionalConsumer();
        await kernel.RegisterAsync(() => new OptionalProvider("test.optional", registers: true))
            .AsTask().WaitAsync(Timeout);
        await kernel.RegisterAsync(() => consumer).AsTask().WaitAsync(Timeout);
        Assert.NotNull(kernel.Resolver.Resolve<IOptional>());

        await kernel.DeactivateAsync(PluginId.Parse("test.optional")).AsTask().WaitAsync(Timeout);

        Assert.Null(kernel.Resolver.Resolve<IOptional>());
        Assert.Equal(
            PluginState.Active,
            (await kernel.GetPluginsAsync()).Single(p => p.Id == consumer.Manifest.Id).State);
        Assert.Equal(1, consumer.Activations);
    }

    [Fact]
    public async Task Cross_alc_manifest_uses_host_plugin_id_type()
    {
        await using var kernel = new DougongKernel();
        var sample = SampleAssemblyPath();
        var handle = await kernel.LoadAsync(sample, "Dougong.Sample.Plugin.SamplePlugin")
            .AsTask().WaitAsync(Timeout);

        // 插件在自己的 ALC 中引用 Dougong.Kernel；清单中的 PluginId 必须与宿主是同一类型。
        var loadedAssembly = kernel.GetLoadedAssembly(handle.Id)!;
        Assert.NotSame(typeof(PluginId).Assembly, loadedAssembly);
        var pluginType = loadedAssembly.GetType("Dougong.Sample.Plugin.SamplePlugin")!;
        var manifest = (PluginManifest)pluginType.GetProperty("Manifest")!.GetValue(
            Activator.CreateInstance(pluginType))!;

        Assert.IsType<PluginId>(manifest.Id);
        Assert.Equal(handle.Id, manifest.Id);
        Assert.Same(typeof(PluginId).Assembly, handle.Id.GetType().Assembly);
    }

    [Fact]
    public async Task Plugin_owned_service_contract_stays_private_to_its_alc()
    {
        await using var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(
            SampleAssemblyPath(), "Dougong.Sample.Plugin.SamplePlugin")
            .AsTask().WaitAsync(Timeout);

        // 服务接口定义在插件程序集内：宿主只能通过插件自己的 Type 句柄访问。
        // 这不是共享契约验收；宿主/插件共享契约见 KernelCrossAlcAcceptanceTests。
        var serviceType = kernel.GetLoadedAssembly(handle.Id)!
            .GetType("Dougong.Sample.Plugin.ISampleService")!;
        Assert.NotSame(typeof(PluginId).Assembly, serviceType.Assembly);
        Assert.NotNull(kernel.ResolveDiagnosticByType(serviceType));
    }

    [Fact]
    public async Task Multiple_real_calls_then_unload_reclaims_the_assembly()
    {
        await using var kernel = new DougongKernel();
        var sample = SampleAssemblyPath();
        WeakReference? weak = null;

        await InvokeAndUnloadAsync(kernel, sample, reference =>
        {
            weak = reference;
        });

        for (var attempt = 0; attempt < 20 && weak!.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50);
        }

        Assert.False(weak!.IsAlive, "多轮调用并卸载后插件程序集仍被强引用。");
        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task InvokeAndUnloadAsync(
        DougongKernel kernel, string sample, Action<WeakReference> capture)
    {
        var handle = await kernel.LoadAsync(sample, "Dougong.Sample.Plugin.SamplePlugin")
            .AsTask().WaitAsync(Timeout);
        var assembly = kernel.GetLoadedAssembly(handle.Id)!;
        var serviceType = assembly.GetType("Dougong.Sample.Plugin.ISampleService")!;
        capture(new WeakReference(assembly));

        for (var i = 0; i < 5; i++)
        {
            var instance = kernel.ResolveDiagnosticByType(serviceType);
            Assert.NotNull(instance);
            var describe = serviceType.GetMethod("Describe")!;
            Assert.Equal("sample", describe.Invoke(instance, null));
        }

        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
    }

    private static string SampleAssemblyPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.Plugin", "bin", Configuration,
        "net10.0", "Dougong.Sample.Plugin.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
