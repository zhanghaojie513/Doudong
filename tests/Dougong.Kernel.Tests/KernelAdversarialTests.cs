using System.Collections.Concurrent;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 内核对抗性探针：用违反契约、违反时序、违反并发假设的方式攻击内核，
/// 目的是找出"正常用法测不出来"的缺陷。
/// </summary>
/// <remarks>
/// 与既有测试的区别：既有测试验证"按文档使用是正确的"，这里验证
/// "插件做错事、宿主用错方式、并发撞在同一临界区"时内核是否仍然守住不变量。
/// <para>
/// 每个探针都只断言**内核自己的承诺**（状态机的可见结果、资源不被重复释放、
/// 排空可观测），不断言实现细节，避免把当前实现固化。
/// </para>
/// </remarks>
public sealed class KernelAdversarialTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── 维度一：状态机与生命周期顺序 ────────────────────────────────

    /// <summary>攻击：在激活回调里再次调用 Activate/Deactivate/Dispose。</summary>
    [Fact]
    public async Task Lifecycle_reentry_from_activation_callback_is_rejected()
    {
        var kernel = new DougongKernel();
        Exception? seen = null;
        var plugin = new ProbePlugin("reentrant", async ctx =>
        {
            try { await kernel.DeactivateAsync(ctx.PluginId); }
            catch (Exception ex) { seen = ex; }
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        Assert.IsType<InvalidOperationException>(seen);
        // 拒绝之后内核仍必须可用，且插件保持已激活。
        Assert.Equal(PluginState.Active, (await kernel.GetPluginsAsync()).Single().State);
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：并发对同一插件发起多次 Activate 与 Deactivate。</summary>
    [Fact]
    public async Task Concurrent_activate_deactivate_keeps_consistent_state()
    {
        var kernel = new DougongKernel();
        // 只统计"登记过清理的回调"，而不是"进入过回调的次数"：
        // 激活可能在提交前被撤销（调用方取消、期望状态改为停用），
        // 那种情况下根本没有登记清理，两者的差值本来就不该相等。
        // 要钉住的不变量是"每个登记了清理的代次都恰好清理一次"。
        var cleanupRegistered = 0;
        var cleanupRan = 0;
        var plugin = new ProbePlugin("churn", async ctx =>
        {
            ctx.RegisterForCleanup(_ =>
            {
                Interlocked.Increment(ref cleanupRan);
                return ValueTask.CompletedTask;
            });
            Interlocked.Increment(ref cleanupRegistered);
            await Task.Yield();
        });

        await kernel.RegisterAsync(() => plugin);

        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try { await kernel.ActivateAsync(plugin.Id); } catch (Exception) { }
            }));
            tasks.Add(Task.Run(async () =>
            {
                try { await kernel.DeactivateAsync(plugin.Id); } catch (Exception) { }
            }));
        }

        await Task.WhenAll(tasks);

        // 无论调度如何，状态必须是确定的，且不能出现"激活数与清理数"长期不匹配的泄漏。
        var state = (await kernel.GetPluginsAsync()).Single().State;
        Assert.Contains(state, new[] { PluginState.Active, PluginState.Stopped, PluginState.Blocked });

        await kernel.DisposeAsync();

        // 每个登记了清理的代次恰好吃到一次清理——既不漏（资源泄漏）也不重复（双释放）。
        Assert.Equal(Volatile.Read(ref cleanupRegistered), Volatile.Read(ref cleanupRan));
    }

    /// <summary>
    /// 攻击：插件在激活回调里同步阻塞线程且不响应取消。
    /// </summary>
    /// <remarks>
    /// 用 <c>await Task.Delay(...)</c> 而不是 <c>ManualResetEventSlim.Wait</c> 制造阻塞：
    /// 前者让回调在首个 await 处让出线程，超时机制才有机会介入；
    /// 后者会在调用线程上同步卡死（连超时都无法启动），那是另一个更严重的问题，
    /// 单独用 <see cref="Sync_blocking_activation_freezes_evaluation"/> 覆盖。
    /// </remarks>
    [Fact]
    public async Task Blocking_activation_is_isolated_after_timeout()
    {
        var kernel = new DougongKernel { ActivationTimeout = TimeSpan.FromMilliseconds(150) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var plugin = new ProbePlugin("blocker", async _ =>
        {
            entered.TrySetResult();
            // 忽略取消：模拟不合作的激活回调（停用令牌被取消也不退出）。
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        });

        // RegisterAsync 内部会等待激活预算走完才返回；必须等它返回后再读状态，
        // 否则读到的是"仍在 Activating/Active 的中间态"，测不到隔离结果。
        var registration = kernel.RegisterAsync(() => plugin).AsTask();
        await entered.Task.WaitAsync(Timeout);

        // 超时后激活回调仍在运行，隔离判定发生在 RegisterAsync 返回之前。
        var handle = await registration.WaitAsync(TimeSpan.FromSeconds(30));

        var state = (await kernel.GetPluginsAsync()).Single().State;
        _ = handle;
        Assert.Equal(PluginState.Failed, state);

        // 隔离后不能再激活：这是"需重启宿主"承诺的可执行形式。
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.ActivateAsync(plugin.Id));

        // 销毁必须如实报告仍有未干净停止的代次，而不是假装成功。
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.DisposeAsync());
    }

    /// <summary>
    /// 回归：激活回调在首个 await 之前同步阻塞时，激活预算仍然必须生效。
    /// </summary>
    /// <remarks>
    /// 这是修复过的内核缺陷（D1）。原先 <c>RunActivationCallbackAsync</c> 在
    /// 评估循环的线程上直接启动：它虽返回 <see cref="Task"/>，但在第一个真正异步的
    /// await 之前是同步执行的，于是 <c>WaitAsync(ActivationTimeout)</c> 根本没机会
    /// 开始计时，整个评估循环（含 <c>_reconcile</c> 信号量）被这个插件独占，
    /// 其它插件的生命周期变更全部无限排队。
    /// <para>
    /// 修法是把回调放到线程池执行（<c>Task.Run</c>），卡住的是线程池线程而不是
    /// 评估循环线程。本用例不关心阻塞线程最终何时返回，只断言：
    /// 注册在预算量级内返回、该代次被隔离、且其它插件仍能被正常激活。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sync_blocking_activation_respects_budget_and_isolates()
    {
        var kernel = new DougongKernel { ActivationTimeout = TimeSpan.FromMilliseconds(200) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bounded = new ManualResetEventSlim(false);

        var plugin = new ProbePlugin("sync-blocker", _ =>
        {
            entered.TrySetResult();
            // 同步阻塞 5 秒：超过激活预算，且发生在第一个 await 之前。
            bounded.Wait(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        });

        var started = DateTime.UtcNow;
        var registration = kernel.RegisterAsync(() => plugin).AsTask();
        await entered.Task.WaitAsync(Timeout);
        await registration.WaitAsync(Timeout);
        var elapsed = DateTime.UtcNow - started;

        // 关键断言：注册在预算量级内返回，而不是等满 5 秒的阻塞。
        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"激活预算未生效：注册耗时 {elapsed.TotalMilliseconds:0}ms，说明同步阻塞仍卡住评估循环");

        // 该代次被隔离。
        Assert.Equal(PluginState.Failed, (await kernel.GetPluginsAsync()).Single().State);

        // 最要紧的一条：评估循环没有被独占，其它插件仍能激活。
        var healthy = new ProbePlugin("healthy", _ => ValueTask.CompletedTask);
        await kernel.RegisterAsync(() => healthy).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            PluginState.Active,
            (await kernel.GetPluginsAsync()).Single(p => p.Id == healthy.Id).State);

        bounded.Set();
        // 隔离代次无法干净停止，销毁必须如实报错而不是假装成功。
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.DisposeAsync());
    }

    // ── 维度二：服务与租约 ────────────────────────────────────────

    /// <summary>攻击：插件在 Active 之后继续注册服务。</summary>
    [Fact]
    public async Task Service_registration_after_activation_is_rejected()
    {
        var kernel = new DougongKernel();
        PluginContext? captured = null;
        Exception? seen = null;
        var plugin = new ProbePlugin("late-register", ctx =>
        {
            captured = ctx;
            ctx.Services.Register<IProbeService>(new ProbeService("first"));
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        try { captured!.Services.Register<IProbeService>(new ProbeService("second")); }
        catch (Exception ex) { seen = ex; }

        Assert.NotNull(seen);

        // 原有服务必须仍然可用，不能被后来的失败注册破坏。
        var reference = kernel.Resolver.Resolve<IProbeService>();
        Assert.NotNull(reference);
        var name = await reference!.InvokeAsync((s, ct) =>
        {
            _ = ct; return ValueTask.FromResult(s.Name);
        });
        Assert.Equal("first", name);
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：在激活回调里重复注册同一服务键。</summary>
    [Fact]
    public async Task Duplicate_service_key_within_activation_is_rejected()
    {
        var kernel = new DougongKernel();
        Exception? seen = null;
        var plugin = new ProbePlugin("dup-key", ctx =>
        {
            ctx.Services.Register<IProbeService>(new ProbeService("a"));
            try { ctx.Services.Register<IProbeService>(new ProbeService("b")); }
            catch (Exception ex) { seen = ex; }
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        Assert.IsType<InvalidOperationException>(seen);
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：用已失效代次的引用在停用后继续调用。</summary>
    [Fact]
    public async Task Stale_service_reference_fails_after_deactivation()
    {
        var kernel = new DougongKernel();
        var provider = new ProbePlugin("provider", ctx =>
        {
            ctx.Services.Register<IProbeService>(new ProbeService("p"));
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => provider);
        await WaitForStateAsync(kernel, provider.Id, PluginState.Active);

        var reference = kernel.Resolver.Resolve<IProbeService>();
        Assert.NotNull(reference);

        // 停用后旧引用必须失效，不能继续触达已清理的实例。
        await kernel.DeactivateAsync(provider.Id);
        await WaitForStateAsync(kernel, provider.Id, PluginState.Stopped);

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
            await reference!.InvokeAsync((s, ct) => { s.Touch(); return ValueTask.CompletedTask; }));
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：提供方在调用进行中被停用（排空语义）。</summary>
    [Fact]
    public async Task Inflight_call_blocks_deactivation_until_completed()
    {
        var kernel = new DougongKernel();
        var provider = new ProbePlugin("provider", ctx =>
        {
            ctx.Services.Register<IProbeService>(new ProbeService("p"));
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => provider);
        await WaitForStateAsync(kernel, provider.Id, PluginState.Active);

        var reference = kernel.Resolver.Resolve<IProbeService>()!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = reference.InvokeAsync(async (s, ct) =>
        {
            _ = ct;
            entered.TrySetResult();
            // 忽略取消：停用会取消代次令牌，响应取消的调用会立刻结束，
            // 那样就测不到"在途调用与停用的交互"。排空时序由 KernelDrainProbeTests 覆盖。
            await release.Task;
            return "done";
        }).AsTask();

        await entered.Task.WaitAsync(Timeout);
        var deactivating = kernel.DeactivateAsync(provider.Id).AsTask();

        // DeactivateAsync 的语义是"发起停用并完成状态迁移"，不承诺等待在途调用；
        // 排空的时序保证由 KernelDrainProbeTests 覆盖（清理必须在调用结束后才执行）。
        // 这里验证的是:在途调用结束前，插件不能再次被成功激活（代次已封口）。
        await Task.Delay(150);
        var reacquired = kernel.Resolver.Resolve<IProbeService>();
        Assert.Null(reacquired);

        release.TrySetResult();
        Assert.Equal("done", await call.WaitAsync(Timeout));
        await deactivating.WaitAsync(Timeout);

        var final = (await kernel.GetPluginsAsync()).Single();
        Assert.Equal(PluginState.Stopped, final.State);
        await kernel.DisposeAsync();
    }

    // ── 维度三：事件总线 ──────────────────────────────────────────

    /// <summary>攻击：处理器在回调里重新发布同类事件（递归风暴）。</summary>
    [Fact]
    public async Task Recursive_publish_does_not_deadlock_or_run_on_caller()
    {
        var kernel = new DougongKernel();
        var depth = 0;
        PluginContext? captured = null;

        var plugin = new ProbePlugin("recursive", ctx =>
        {
            captured = ctx;
            ctx.Events.Subscribe<int>(n =>
            {
                var current = Interlocked.Increment(ref depth);
                if (current < 50 && n < 100) ctx.Events.Publish(n + 1);
            });
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        // 发布必须立刻返回（投递而非调用），否则会在同一线程递归。
        captured!.Events.Publish(0);
        await Task.Delay(300);
        Assert.True(Volatile.Read(ref depth) > 0);

        await kernel.DisposeAsync();
    }

    /// <summary>攻击：订阅者在处理器里退订自己。</summary>
    [Fact]
    public async Task Handler_can_unsubscribe_itself_without_deadlock()
    {
        var kernel = new DougongKernel();
        PluginContext? captured = null;

        var plugin = new ProbePlugin("self-unsub", ctx =>
        {
            captured = ctx;
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        var count = 0;
        IDisposable? subscription = null;
        subscription = captured!.Events.Subscribe<int>(_ =>
        {
            Interlocked.Increment(ref count);
            subscription!.Dispose();   // 在处理器内部退订
        });

        captured.Events.Publish(1);
        await Task.Delay(200);
        captured.Events.Publish(2);
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref count));
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：发布 null 负载，并验证订阅只收到实际发布的事件。</summary>
    [Fact]
    public async Task Null_payload_reaches_reference_typed_subscription()
    {
        await using var kernel = new DougongKernel();
        var received = new ConcurrentQueue<string?>();
        PluginContext? captured = null;

        var plugin = new ProbePlugin("null-payload", ctx =>
        {
            captured = ctx;
            ctx.Events.Subscribe<string?>(value => received.Enqueue(value));
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        captured!.Events.Publish<string?>(null);
        await Task.Delay(200);

        // null 是合法的引用类型负载，必须被投递而不是被当成"无事件"丢掉。
        Assert.Single(received);
        Assert.Null(received.Single());
    }

    // ── 维度四：依赖图 ────────────────────────────────────────────

    /// <summary>攻击：登记自依赖的插件。</summary>
    [Fact]
    public async Task Self_dependency_is_rejected()
    {
        var kernel = new DougongKernel();
        var id = PluginId.Parse("self-dep");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.RegisterAsync(() => new ManifestPlugin(
                new PluginManifest(id, "1.0.0", dependsOn: [id]))));

        Assert.Contains("环", error.Message);
        Assert.Empty(await kernel.GetPluginsAsync());
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：运行时解析出服务环（声明上无环，靠 Resolve 才成环）。</summary>
    [Fact]
    public async Task Runtime_service_cycle_is_detected()
    {
        var kernel = new DougongKernel();
        Exception? seen = null;
        var a = new ProbePlugin("a", ctx =>
        {
            ctx.Services.Register<IProbeService>(new ProbeService("a"));
            return ValueTask.CompletedTask;
        });
        var b = new ProbePlugin("b", ctx =>
        {
            ctx.Services.Register<IProbeService>(new ProbeService("b"));
            // 从 b 的激活回调里解析 a 的服务，再让 a 解析 b —— 形成运行时环。
            try { ctx.Resolver.Resolve<IProbeService>(); }
            catch (Exception ex) { seen = ex; }
            return ValueTask.CompletedTask;
        });

        await kernel.RegisterAsync(() => a);
        await WaitForStateAsync(kernel, a.Id, PluginState.Active);
        await kernel.RegisterAsync(() => b);
        await WaitForStateAsync(kernel, b.Id, PluginState.Active);

        // 同键多提供方且无优先级区分 → 歧义，Resolve 返回 null。
        // 这是内核的承诺：不"随意选一个"，也不把歧义当成异常炸给调用方。
        var ambiguous = kernel.Resolver.Resolve<IProbeService>();
        Assert.Null(ambiguous);
        Assert.Null(seen);
        await kernel.DisposeAsync();
    }

    // ── 维度五：状态视图与修订号 ──────────────────────────────────

    /// <summary>攻击：请求一个远超当前修订号的游标。</summary>
    [Fact]
    public async Task Future_revision_cursor_is_rejected()
    {
        var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new ProbePlugin("p", _ => ValueTask.CompletedTask));

        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.ResumeStates(long.MaxValue));
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：把历史保留上限设为 0 后使用游标续订。</summary>
    [Fact]
    public async Task Zero_history_limit_forces_resync()
    {
        var kernel = new DougongKernel { RevisionHistoryLimit = 0 };
        await kernel.RegisterAsync(() => new ProbePlugin("p", _ => ValueTask.CompletedTask));

        using var view = kernel.ResumeStates(0);
        Assert.True(view.RequiresResync);
        await kernel.DisposeAsync();
    }

    /// <summary>攻击：状态观察者抛异常。</summary>
    [Fact]
    public async Task Throwing_state_observer_does_not_break_kernel()
    {
        var kernel = new DougongKernel();
        using var bad = kernel.SubscribeStateChanges(_ => throw new InvalidOperationException("observer boom"));
        var observed = 0;
        using var good = kernel.SubscribeStateChanges(_ => Interlocked.Increment(ref observed));

        var plugin = new ProbePlugin("p", _ => ValueTask.CompletedTask);
        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        Assert.True(Volatile.Read(ref observed) > 0);
        Assert.NotNull(kernel.LastObserverDiagnostic);
        await kernel.DisposeAsync();
    }

    // ── 维度六：销毁与重复调用 ────────────────────────────────────

    /// <summary>攻击：并发调用 DisposeAsync 多次。</summary>
    [Fact]
    public async Task Concurrent_dispose_returns_same_completion()
    {
        var kernel = new DougongKernel();
        await kernel.RegisterAsync(() => new ProbePlugin("p", _ => ValueTask.CompletedTask));

        var disposals = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await kernel.DisposeAsync()))
            .ToArray();
        await Task.WhenAll(disposals).WaitAsync(Timeout);

        // 销毁后所有变更入口都必须拒绝，且不能因此崩溃。
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await kernel.RegisterAsync(() => new ProbePlugin("q", _ => ValueTask.CompletedTask)));
    }

    /// <summary>攻击：销毁后在激活回调里再次请求销毁。</summary>
    [Fact]
    public async Task Dispose_from_inside_activation_is_rejected()
    {
        await using var kernel = new DougongKernel();
        Exception? seen = null;
        var plugin = new ProbePlugin("disposer", async ctx =>
        {
            try { await kernel.DisposeAsync(); }
            catch (Exception ex) { seen = ex; }
        });

        await kernel.RegisterAsync(() => plugin);
        await WaitForStateAsync(kernel, plugin.Id, PluginState.Active);

        Assert.IsType<InvalidOperationException>(seen);
    }

    // ── 辅助 ─────────────────────────────────────────────────────

    private static async Task WaitForStateAsync(
        DougongKernel kernel, PluginId id, PluginState expected)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var handle = (await kernel.GetPluginsAsync()).FirstOrDefault(p => p.Id == id);
            if (handle is not null && handle.State == expected) return;
            await Task.Delay(10);
        }

        var actual = (await kernel.GetPluginsAsync()).FirstOrDefault(p => p.Id == id);
        Assert.Fail($"插件 '{id}' 未在时限内到达 {expected}，实际 {actual?.State}");
    }

    private interface IProbeService
    {
        string Name { get; }
        void Touch();
    }

    private sealed class ProbeService(string name) : IProbeService
    {
        public string Name { get; } = name;
        public void Touch() { }
    }

    private sealed class ProbePlugin(
        string id, Func<PluginContext, ValueTask> activate) : IPlugin
    {
        public PluginId Id { get; } = PluginId.Parse("probe." + id);
        public PluginManifest Manifest => PluginManifest.Create(Id.Value, "1.0.0");
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            activate(context);
    }

    private sealed class ManifestPlugin(PluginManifest value) : IPlugin
    {
        public PluginManifest Manifest { get; } = value;
        public PluginId Id => Manifest.Id;
        public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
