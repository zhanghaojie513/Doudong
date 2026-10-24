# 内核对抗性审查（2026-10-24）

对内核做了 6 个维度的攻击性探测，新增 20 项探针（`KernelAdversarialTests`、
`KernelDrainProbeTests`）。目标是找"按文档正常使用测不出来"的缺陷。

## 已确认并修复的内核缺陷

### D1. 同步阻塞的激活回调会独占评估循环，绕过激活预算（已修复）

**攻击方式**：在 `ActivateAsync` 里、第一个 `await` 之前同步阻塞
（例如调用 `ManualResetEventSlim.Wait` 或同步 IO）。

**现象**：`RunActivationCallbackAsync` 返回 `Task`，但在遇到第一个真正异步的
`await` 之前是**同步执行**的。因此 `RunActivationAsync` 里
`activationTask = RunActivationCallbackAsync(...)` 这一行本身就卡在调用线程上，
后面的 `WaitAsync(ActivationTimeout, ...)` 根本没有机会开始计时。
结果是：`ActivationTimeout` 对这类回调完全无效，整个 `EvaluateAsync`
（连同 `_reconcile` 信号量）被一直占用，其它插件的生命周期变更全部排队。

**影响**：宿主的"所有生命周期变更有界"承诺对不可信插件不成立。
一个写错的插件（同步 IO、锁等待、死循环前的阻塞调用）可以让内核永久失去响应，
且宿主的超时预算无从生效。

**修复**：把激活回调改为 `Task.Run(() => RunActivationCallbackAsync(...))` 启动。
这样卡住的是线程池线程而不是评估循环线程，`WaitAsync(ActivationTimeout)` 得以
按时触发，原有的隔离路径（标记 `Failed`、禁止再激活、销毁时如实报"未干净停止"）
也才真正可用。

**修复前先验证了前置条件**（不靠推测）：
- `Task.Run` + `WaitAsync(300ms)` 对同步阻塞能在 ~349ms 触发超时；
- `AsyncLocal` 能传入 `Task.Run`（内部读到 `outer`），内部设置的值在内部持续
  有效（`inner`），且不污染调用方（外部仍是 `outer`）——这正是
  `_activationScope` / `_callback` 需要的语义。

**反证**：移除 `Task.Run` 后，回归探针报"激活预算未生效：注册耗时 5032ms"；
加回后在预算量级内返回，且其它插件仍能正常激活。

## 已验证正确（攻击未击穿）

以下攻击均未发现缺陷，说明这些不变量确实被执行：

| 攻击 | 结果 |
|---|---|
| 激活回调里再调用 Activate/Deactivate/Dispose | 抛 `InvalidOperationException`，内核保持可用 |
| 并发 20 次 Activate/Deactivate 交叉 | 最终状态确定；激活数 == 清理数，无泄漏 |
| 激活回调忽略取消并持续运行 | 超时后被隔离为 `Failed`，不可再激活；销毁如实报"未干净停止" |
| Active 之后继续注册服务 | 拒绝；已有服务不受影响 |
| 同代次内重复注册同一服务键 | 拒绝 |
| 停用后继续用旧服务引用调用 | `ObjectDisposedException` |
| 忽略取消的在途调用 + 停用 | 清理回调严格在调用结束之后执行（`call-start -> call-end -> cleanup`） |
| 处理器内递归发布事件 | 不阻塞调用方、不死锁（投递而非调用） |
| 处理器内退订自己 | 不死锁，后续事件不再到达 |
| 发布 `null` 负载 | 正确投递到引用类型订阅（不被当成"无事件"丢弃） |
| 登记自依赖 | 拒绝并给出环路径 |
| 同键多提供方无优先级 | `Resolve` 返回 `null`（歧义），不随意选一个 |
| 游标超前于当前修订号 | 拒绝 |
| `RevisionHistoryLimit = 0` 后游标续订 | 要求重同步 |
| 状态观察者抛异常 | 被隔离，其它观察者照常收到，内核不受影响 |
| 并发多次 `DisposeAsync` | 返回同一完成，销毁后入口全拒绝 |

## 探针自身踩过的坑（记录以免重复）

1. **用会被取消的令牌测排空**：停用会取消代次寿命令牌，响应取消的调用立刻结束，
   "排空"退化成"等一个已完成的调用"。要测真正的排空，调用必须**忽略**取消。
2. **`RegisterAsync` 会等到激活预算走完才返回**：在它返回前读状态，读到的是中间态。
3. **`DeactivateAsync` 的语义是"完成状态迁移"，不承诺等待在途调用**；
   排空的时序保证要看清理回调的相对顺序，不能看这个方法的返回时刻。
