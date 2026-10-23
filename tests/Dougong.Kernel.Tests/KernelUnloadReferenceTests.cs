using System.Runtime.CompilerServices;
using Dougong.Kernel;
using Dougong.Sample.Shared;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 正常卸载后，失效的服务引用与状态历史都不应继续持有插件 ALC 的对象。
/// 需要不合作回调仍在运行时不做回收断言（那属于隔离代次）。
/// </summary>
public sealed class KernelUnloadReferenceTests
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
    public async Task Service_reference_does_not_keep_the_plugin_assembly_alive_after_unload()
    {
        var kernel = new DougongKernel();
        var (weakAssembly, reference) = await LoadReferenceAndUnloadAsync(kernel);

        // ALC 回收不是确定性的，做有界重试；多次 GC 后仍存活才说明存在真实引用根。
        var collected = await CollectUntilAsync(() => !weakAssembly.IsAlive);
        Assert.True(collected, "失效的服务引用或状态历史仍持有插件程序集。");
        Assert.NotNull(reference);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await reference.InvokeAsync((_, _) => ValueTask.CompletedTask));
        GC.KeepAlive(reference);
        await kernel.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Assembly, IServiceReference<IGreetingService>? Reference)>
        LoadReferenceAndUnloadAsync(DougongKernel kernel)
    {
        var handle = await kernel.LoadAsync(
                ContractsAssemblyPath(), "Dougong.Sample.Contracts.SharedContractPlugin")
            .AsTask().WaitAsync(Timeout);
        var assembly = kernel.GetLoadedAssembly(handle.Id)!;
        var weak = new WeakReference(assembly);
        var reference = kernel.Resolver.Resolve<IGreetingService>();
        Assert.NotNull(reference);
        await kernel.UnloadAsync(handle.Id).AsTask().WaitAsync(Timeout);
        return (weak, reference);
    }

    private static async Task<bool> CollectUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 10 && !condition(); attempt++)
        {
            Collect();
            if (condition()) return true;
            await Task.Delay(50);
        }

        Collect();
        return condition();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
