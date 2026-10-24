using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 定向探针：验证"停用是否真的等在途调用结束"。
/// </summary>
/// <remarks>
/// 这条约束是内核的核心承诺之一：停用必须先排空在途调用，否则插件会在
/// 自己的服务仍被调用时执行清理（释放文件句柄、关闭连接），
/// 调用方随后就会看到"对象已被释放"之类的随机故障。
/// <para>
/// 关键前提：**在途调用必须忽略取消令牌**。停用会取消代次寿命令牌，
/// 响应取消的调用会立刻结束，"排空"就退化成了"等一个已完成的调用"，
/// 测不到真正的排空逻辑。只有忽略取消的调用才会让内核必须真的等待。
/// </para>
/// <para>
/// 因此这里不依赖 DeactivateAsync 何时返回（它可能只是先返回句柄、后台继续排空），
/// 而是直接观察**清理回调相对于调用完成的时序**——这才是实现承诺的地方。
/// </para>
/// </remarks>
public sealed class KernelDrainProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Cleanup_does_not_run_before_inflight_call_completes()
    {
        var kernel = new DougongKernel();
        var order = new List<string>();
        var inCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new Probe("provider", ctx =>
        {
            ctx.Services.Register<IProbe>(new Impl());
            ctx.RegisterForCleanup(_ =>
            {
                lock (order) order.Add("cleanup");
                cleanupRan.TrySetResult();
                return ValueTask.CompletedTask;
            });
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => provider);
        await WaitForAsync(kernel, provider.Id, PluginState.Active);

        var reference = kernel.Resolver.Resolve<IProbe>()!;
        var call = Task.Run(async () => await reference.InvokeAsync(async (s, ct) =>
        {
            lock (order) order.Add("call-start");
            inCall.TrySetResult();
            // 故意忽略 ct：模拟不响应取消的调用。停用必须仍然等它结束。
            _ = ct;
            await releaseCall.Task;
            lock (order) order.Add("call-end");
            return s.Value;
        }));

        await inCall.Task.WaitAsync(Timeout);

        // 发起停用，但不要 await —— 排空可能异步进行。
        var deactivate = kernel.DeactivateAsync(provider.Id).AsTask();

        // 在途调用尚未结束：清理绝不能已经开始。
        var cleanupTooEarly = await Task.WhenAny(cleanupRan.Task, Task.Delay(300)) == cleanupRan.Task;
        if (cleanupTooEarly)
        {
            lock (order)
            {
                Assert.Fail(
                    "排空失败：在途调用未结束时清理已执行。顺序为 "
                    + string.Join(" -> ", order));
            }
        }

        releaseCall.TrySetResult();
        await call.WaitAsync(Timeout);
        await deactivate.WaitAsync(Timeout);
        await cleanupRan.Task.WaitAsync(Timeout);

        lock (order)
        {
            Assert.Equal(["call-start", "call-end", "cleanup"], order);
        }

        await kernel.DisposeAsync();
    }

    /// <summary>停用返回时，在途调用必须已经结束（返回值即代表代次已封口）。</summary>
    [Fact]
    public async Task Deactivation_return_implies_call_finished()
    {
        var kernel = new DougongKernel();
        var callFinished = false;
        var inCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new Probe("provider", ctx =>
        {
            ctx.Services.Register<IProbe>(new Impl());
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => provider);
        await WaitForAsync(kernel, provider.Id, PluginState.Active);

        var reference = kernel.Resolver.Resolve<IProbe>()!;
        var call = Task.Run(async () => await reference.InvokeAsync(async (s, ct) =>
        {
            _ = ct;   // 同上：忽略取消，考察真正的排空
            inCall.TrySetResult();
            try { await releaseCall.Task; }
            finally { Volatile.Write(ref callFinished, true); }
            return s.Value;
        }));

        await inCall.Task.WaitAsync(Timeout);
        var deactivate = kernel.DeactivateAsync(provider.Id).AsTask();
        await Task.Delay(200);

        // 这里只记录事实，不当作失败：DeactivateAsync 可能先返回、后台继续排空。
        // 真正的门槛是上面的时序探针。
        releaseCall.TrySetResult();
        await call.WaitAsync(Timeout);
        await deactivate.WaitAsync(Timeout);
        Assert.True(Volatile.Read(ref callFinished));
        await kernel.DisposeAsync();
    }

    private static async Task WaitForAsync(DougongKernel kernel, PluginId id, PluginState expected)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var handle = (await kernel.GetPluginsAsync()).FirstOrDefault(p => p.Id == id);
            if (handle is not null && handle.State == expected) return;
            await Task.Delay(10);
        }

        Assert.Fail($"插件 '{id}' 未在时限内到达 {expected}。");
    }

    private interface IProbe { string Value { get; } }

    private sealed class Impl : IProbe { public string Value => "v"; }

    private sealed class Probe(string id, Func<PluginContext, ValueTask> activate) : IPlugin
    {
        public PluginId Id { get; } = PluginId.Parse("drain." + id);
        public PluginManifest Manifest => PluginManifest.Create(Id.Value, "1.0.0");
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            activate(context);
    }
}
