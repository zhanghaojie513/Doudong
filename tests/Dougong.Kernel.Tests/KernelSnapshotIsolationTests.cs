using System.Runtime.CompilerServices;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 快照类型不得持有插件清单：旧实现里 <c>PluginHandle.Manifest</c> 会携带
/// 服务需求的 <see cref="Type"/>，宿主保留快照就会阻止动态插件 ALC 回收。
/// </summary>
public sealed class KernelSnapshotIsolationTests
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
    public void Plugin_summary_and_handle_carry_no_manifest_member()
    {
        // 结构约束：快照类型只暴露共享数据，不能有 Manifest 属性。
        Assert.Null(typeof(PluginSummary).GetProperty("Manifest"));
        Assert.Null(typeof(PluginHandle).GetProperty("Manifest"));
        Assert.NotNull(typeof(PluginSummary).GetProperty(nameof(PluginSummary.Version)));
        Assert.NotNull(typeof(PluginHandle).GetProperty(nameof(PluginHandle.Version)));
    }

    [Fact]
    public async Task Retained_handle_and_snapshot_do_not_block_alc_reclamation()
    {
        var kernel = new DougongKernel();
        var (weakAssembly, handle, snapshot) = await LoadRetainAndUnloadAsync(kernel);

        Collect();

        Assert.False(weakAssembly.IsAlive,
            "保留卸载前取得的句柄或快照不应阻止插件程序集回收。");
        Assert.NotEmpty(snapshot);
        GC.KeepAlive(handle);
        GC.KeepAlive(snapshot);
        await kernel.DisposeAsync();
    }

    [Fact]
    public async Task Manifest_is_available_only_through_the_explicit_lookup()
    {
        var kernel = new DougongKernel();
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);

        var manifest = await kernel.GetPluginManifestAsync(handle.Id).AsTask().WaitAsync(Timeout);
        Assert.NotNull(manifest);
        Assert.Equal(handle.Id, manifest.Id);
        Assert.Equal(handle.Version, manifest.Version);

        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        Assert.Null(await kernel.GetPluginManifestAsync(handle.Id).AsTask().WaitAsync(Timeout));
        await kernel.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, PluginHandle, IReadOnlyList<PluginSummary>)>
        LoadRetainAndUnloadAsync(DougongKernel kernel)
    {
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);
        var snapshot = (await kernel.GetPluginsAsync().AsTask().WaitAsync(Timeout)).ToList();
        var assembly = kernel.GetLoadedAssembly(handle.Id)!;
        var weak = new WeakReference(assembly);
        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        return (weak, handle, snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
