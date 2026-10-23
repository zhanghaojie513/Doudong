using Dougong.Kernel;
using Dougong.Sample.Shared;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 共享契约登记属于宿主配置，按内核实例隔离且可撤销。
/// 漏配时的行为由仓库外独立进程验证（激活期装配加载失败 → 回滚并抛出可操作诊断）；
/// 这里覆盖登记语义本身，以及登记后加载/卸载的完整路径。
/// </summary>
public sealed class KernelSharedContractTests
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
    public async Task Registered_shared_contract_loads_and_unloads_cleanly()
    {
        await using var kernel = new DougongKernel();
        using var registration = kernel.RegisterSharedContract(typeof(IGreetingService).Assembly);

        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.SharedContractPlugin")
            .AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginState.Active, handle.State);

        var reference = kernel.Resolver.Resolve<IGreetingService>();
        Assert.NotNull(reference);
        var greeting = await reference.InvokeAsync(
            (service, _) => ValueTask.FromResult(service.Greet("dougong")));
        Assert.Equal("hello, dougong", greeting);

        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Registration_is_scoped_to_the_kernel_and_revocable()
    {
        await using var first = new DougongKernel();
        await using var second = new DougongKernel();

        var registration = first.RegisterSharedContract(typeof(IGreetingService).Assembly);
        registration.Dispose();
        registration.Dispose();  // 幂等

        // 两个内核互不影响：撤销只作用于登记它的那个实例。
        using var again = first.RegisterSharedContract(typeof(IGreetingService).Assembly);
        using var other = second.RegisterSharedContract(typeof(IGreetingService).Assembly);
    }

    [Fact]
    public async Task Plugin_without_shared_contracts_loads_without_registration()
    {
        await using var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
    }
}
