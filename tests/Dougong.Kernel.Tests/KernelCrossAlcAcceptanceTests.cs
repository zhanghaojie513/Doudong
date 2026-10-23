using Dougong.Kernel;
using Dougong.Sample.Shared;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 真实的跨 ALC 验收：宿主与插件引用同一共享契约程序集，
/// 并通过公开 Resolver 调用服务、通过事件总线双向传递事件。
/// </summary>
public sealed class KernelCrossAlcAcceptanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string ContractsAssemblyPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.Contracts", "bin", Configuration,
        "net10.0", "Dougong.Sample.Contracts.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task Shared_contract_types_resolve_to_the_same_assembly_instance()
    {
        PluginLoadContext.RegisterSharedContract(typeof(IGreetingService).Assembly);

        await using var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.SharedContractPlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
        var reference = kernel.Resolver.Resolve<IGreetingService>();
        Assert.NotNull(reference);

        var greeting = await reference.InvokeAsync(
            (service, _) => ValueTask.FromResult(service.Greet("dougong")));
        Assert.Equal("hello, dougong", greeting);
    }

    [Fact]
    public async Task Shared_contract_event_flows_into_and_out_of_the_plugin_alc()
    {
        PluginLoadContext.RegisterSharedContract(typeof(IGreetingService).Assembly);
        GreetingProbe.Reset();

        await using var kernel = new DougongKernel();
        await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.SharedContractPlugin")
            .AsTask().WaitAsync(Timeout);

        // 插件在自己的 ALC 中发布共享 DTO；宿主订阅到的必须是同一类型实例。
        var received = await GreetingProbe.Received.WaitAsync(Timeout);
        Assert.IsType<GreetingEvent>(received);
        Assert.Equal("hello-from-plugin", received.Message);
        Assert.Same(typeof(GreetingEvent).Assembly, received.GetType().Assembly);
    }
}
