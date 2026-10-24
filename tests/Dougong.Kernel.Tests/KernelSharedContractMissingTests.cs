using System.Runtime.CompilerServices;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 漏配共享契约的进程内回归。
/// 这里刻意使用一个测试项目从不引用的契约程序集（<c>Dougong.Sample.Isolated</c>）：
/// 默认 ALC 中没有它，插件也没有把契约复制到输出目录，因此未登记时装配解析必然失败。
/// 首个用例会先断言这个前提，防止将来有人引用该程序集后测试静默变成假通过。
/// </summary>
public sealed class KernelSharedContractMissingTests
{
    private const string ContractAssemblyName = "Dougong.Sample.Isolated";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string PluginAssemblyPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Dougong.Sample.IsolatedPlugin", "bin", Configuration,
        "net10.0", "Dougong.Sample.IsolatedPlugin.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task Unregistered_contract_fails_load_and_leaves_no_entry()
    {
        // 前提：该契约没有被复制到插件输出目录——否则漏配路径不会失败，
        // 后面的断言就变成假通过。
        //
        // 为什么不再断言「程序集不在当前进程中」：那是进程级全局状态，
        // 同族的 Registering_the_contract_makes_the_same_plugin_load_and_unload
        // 会用 Assembly.LoadFile 把它装进进程（LoadFile 不走默认 ALC 但仍出现在
        // AppDomain.CurrentDomain.GetAssemblies() 里）。两条测试谁先跑取决于
        // 测试运行器的调度，于是这条前提时真时假——CI 上稳定失败、本地稳定通过。
        //
        // 已确定性验证：Assembly.LoadFile 之后，GetAssemblies() 立刻包含该程序集，
        // 因此原断言在 Registering 先跑时必然失败（不是偶发，是顺序依赖）。
        // 真正要保证的不是"进程里没有它"，而是"插件 ALC 解析不到它"。
        // 下面直接测这一点：未登记时加载必然失败，且诊断指出缺失程序集。
        Assert.False(
            File.Exists(Path.Combine(Path.GetDirectoryName(PluginAssemblyPath())!, $"{ContractAssemblyName}.dll")),
            "契约程序集被复制到插件目录，漏配路径将不再失败。");

        await using var kernel = new DougongKernel();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.LoadAsync(PluginAssemblyPath())
                .AsTask().WaitAsync(Timeout));

        // 诊断必须可操作：指出缺失程序集并给出登记入口。
        Assert.Contains(ContractAssemblyName, error.Message);
        Assert.Contains(nameof(DougongKernel.RegisterSharedContract), error.Message);

        // 配置错误不得留下需要重启宿主才能清理的条目。
        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Registering_the_contract_makes_the_same_plugin_load_and_unload()
    {
        var kernel = new DougongKernel();
        using var registration = kernel.RegisterSharedContract(LoadContractAssembly());

        var handle = await kernel.LoadAsync(PluginAssemblyPath()).AsTask().WaitAsync(Timeout);
        Assert.Equal(PluginState.Active, handle.State);

        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        Assert.Empty(await kernel.GetPluginsAsync());
        await kernel.DisposeAsync();
    }

    /// <summary>
    /// 回归：该契约即使已在宿主进程中、但未登记，插件也必须加载失败。
    /// </summary>
    /// <remarks>
    /// 这条钉住"登记是加载前置条件"这一约束。真正要防的情形是：
    /// 程序集既不在插件目录、又没登记，但宿主进程里恰好已有同名程序集，
    /// 装配解析于是回落到默认 ALC 并静默绑定成功。
    /// <para>
    /// 这里的构造方式很关键：契约装进一个**独立可回收 ALC**，而不是
    /// <c>Assembly.LoadFile</c>。后者会把它永久留在进程里，污染同族的其它用例
    /// ——那正是 CI 上出现「本地全绿、CI 稳定失败」的原因。
    /// 独立 ALC 既能把"进程里存在该程序集"这个前提造出来，又能在用例结束时卸载。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Contract_present_in_process_but_unregistered_still_fails()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext(
            $"contract-probe:{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            var loaded = context.LoadFromAssemblyPath(ContractAssemblyPath());
            Assert.Equal(ContractAssemblyName, loaded.GetName().Name);

            // 前提成立：该程序集此刻确实在这一进程内。
            Assert.Contains(
                AppDomain.CurrentDomain.GetAssemblies(),
                a => string.Equals(
                    a.GetName().Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase));

            await using var kernel = new DougongKernel();

            // 没有登记，必须失败——不能因为进程里恰好有同名程序集就放行。
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await kernel.LoadAsync(PluginAssemblyPath()).AsTask().WaitAsync(Timeout));

            Assert.Contains(ContractAssemblyName, error.Message);
            Assert.Contains(nameof(DougongKernel.RegisterSharedContract), error.Message);
            Assert.Empty(await kernel.GetPluginsAsync());
        }
        finally
        {
            context.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static System.Reflection.Assembly LoadContractAssembly() =>
        System.Reflection.Assembly.LoadFrom(ContractAssemblyPath());

    private static string ContractAssemblyPath() => Path.Combine(
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Isolated", "bin", Configuration, "net10.0")),
        $"{ContractAssemblyName}.dll");
}
