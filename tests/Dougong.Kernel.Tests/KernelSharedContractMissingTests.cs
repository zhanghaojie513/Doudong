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
        // 前提：默认 ALC 中不存在该契约，且它没有被复制到插件输出目录。
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            a => string.Equals(a.GetName().Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static System.Reflection.Assembly LoadContractAssembly() =>
        System.Reflection.Assembly.LoadFrom(Path.Combine(
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "samples", "Dougong.Sample.Isolated", "bin", Configuration, "net10.0")),
            $"{ContractAssemblyName}.dll"));
}
