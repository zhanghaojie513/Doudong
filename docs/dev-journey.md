# M0 开发历程素材

> 用途：记录 M0 期间真实发生的问题与转折，供后续梳理成开发历程文章。
> 不是验收文档，也不是对外承诺。验收状态见 [status.md](status.md)。
> 记录原则：只写实际发生的；区分“实现缺陷 / 测试缺陷 / 文档缺陷 / 判断错误”四类。

---

## 一、数字概览（2026-10-23 收尾时）

| 项 | 值 |
|---|---|
| 内核产品代码 | 21 个文件，约 2800 行 |
| 内核行为测试 | 148 项，0 跳过 |
| 测试数量变化 | 10 → 25 → 32 → 43 → 50 → 55 → 62 → 69 → 79 → 84 → 90 → 100 → 128 → 140 → 143 → 146 → 148 |
| 独立审核轮次 | 2 轮专项审核 + 1 轮最终复审 |
| 审核发现并修复的问题 | 13 项（含 6 项阻断级） |
| 其他产品工程 | Agent / Ai / Mcp / Acp 均为 0 行空工程；Cli 3 行占位 |

测试数量这条曲线本身就是素材：它上涨了 15 次，但**其中至少两次“全绿”状态对应的是有缺陷的实现**。这是文章可以立住的核心矛盾。

---

## 二、实现缺陷

### 1. 生命周期回调在锁内执行，导致重入死锁

最早的内核在持有生命周期锁时调用插件的 `ActivateAsync`，插件在回调里查 `GetPlugins()` 就会自锁。

- **发现方式**：清理回调里查询内核状态的测试挂起
- **根因**：把“保护状态”和“执行用户代码”放进了同一个锁
- **修法**：短临界区只保护状态，用户回调移到锁外；生命周期变更由独立信号量串行化
- **可写成**：为什么插件框架的锁必须短

### 2. 激活失败后无限重试

`Failed` 状态不排除重新评估，评估循环会立刻再次尝试激活一个必然失败的插件，形成忙循环。

- **发现方式**：失败激活测试的插件被调用次数远超预期
- **修法**：`Failed` 跳过自动重评估，只有宿主显式 `ActivateAsync` 才重置
- **可写成**：状态机里“失败”不是“可以再试”

### 3. 停用提供方时，正在激活的消费者不会收到取消信号

停用 P 时计算依赖闭包，但闭包只看“已激活”的消费者；一个正在 `ActivateAsync` 里等寿命令牌取消的消费者不在闭包里，于是协调器等它，它等取消，互相卡死。

- **发现方式**：取消相关的理论测试超时
- **修法**：停用前把“需求已失效”的消费者也纳入取消批次
- **可写成**：依赖传播要覆盖“正在启动中”的节点

### 4. 清理顺序假设了“激活序倒序 = 逆拓扑”

最初每个插件激活时执行 `entry.ActivationSequence++`，各插件都是 1，排序完全失效。改成全局递增后基本路径通过了——但真正的缺陷还在后面，见下一条。

- **发现方式**：消费者先于提供方清理的测试失败
- **修法**：先改全局序号，后改为按实际依赖边拓扑排序
- **可写成**：一个“看起来对了”的修复，掩盖了两轮之后才暴露的问题

### 5. 晚到的高优先级提供方，让清理顺序与运行时选择脱节

低优先级提供方先激活 → 消费者激活并解析它 → 高优先级提供方后到 → 运行时 Resolver 改选高优先级 → 但清理仍按激活序。

结果：**消费者实际在用的那个提供方，先于消费者被清理**。

- **发现方式**：独立审核用确定性场景构造，输出 `cleanup topology: high,consumer,low`
- **修法**：记录消费者代次实际解析过的提供方，按真实依赖边做拓扑排序
- **可写成**：静态声明和运行时行为不一致时，谁说了算

### 6. `ResolveAll` 只返回一个提供方

`FindServices` 把“单值选择”的优先级规则用在了集合解析上，两个提供方只返回最高优先级那个。

- **发现方式**：独立审核对比契约注释与实现
- **修法**：拆成 `FindAllServices`（全部）与 `SelectService`（唯一）
- **可写成**：一个函数服务两种语义，迟早出错

### 7. 排空超时后仍能“重新激活”

超时代次被标 `Failed`，但 `ActivateAsync` 只检查 `CleanupFailed`，允许重试；重试后 `EvaluateAsync` 又跳过带 `DrainTimedOut` 的条目。最终 `Activate` 返回 `Stopped`、激活次数不增、服务不存在、旧调用仍在跑。

- **发现方式**：独立审核复现，输出 `activations=1, invocationStillRunning=True, serviceMissing=True`
- **修法**：引入隔离标记，拒绝激活/卸载/绕过内核的 Context 清理
- **可写成**：返回值类型合法，语义全错

### 8. `UnloadAsync` 吞掉本次停用失败

只在停用**前**检查 `CleanupFailed`/`DrainTimedOut`，停用**后**直接移除条目并 Detach。清理失败和排空超时恰恰产生在停用过程中。

- **发现方式**：审核验证清理失败插件的卸载路径
- **修法**：停用后重新核验代次、失败标志、Context 与最终状态
- **可写成**：前后置条件检查的位置，决定它有没有用

### 9. 取消回调、清理回调没有预算

`DrainTimeout` 只管服务调用排空。阻塞的 `Lifetime.Register` 回调能让停用永久停在 `Deactivating`；cleanup 拿的是 `CancellationToken.None`，既无预算也不可取消。

- **发现方式**：探测输出 `boundedByCancellationTimeout=False`、`canBeCanceled=False`
- **修法**：拆出 `ActivationTimeout` / `CancellationTimeout` / `EventDrainTimeout` / `CleanupTimeout`
- **可写成**：一个超时参数覆盖不了四个阶段

### 10. 事件退订不释放总线引用

`EventSubscription.Dispose()` 只关闭 channel，不从 `EventBus._handlers` 移除。插件卸载后总线仍持有 handler、事件类型和插件 ALC，WeakReference 检查显示程序集存活。

- **发现方式**：审核用 WeakReference 实证
- **修法**：订阅持有总线移除回调，退订立即移除
- **可写成**：Dispose 不等于注销

### 11. 并发 `Publish` 重复报告溢出

多个线程同时看到同一订阅队列已满，各自报告一次溢出。测试断言“只报告一次”在单跑时通过，重复运行 5 轮时失败。

- **发现方式**：**只因为重复跑了并发测试才发现**
- **修法**：订阅内 `Interlocked.Exchange` 抢占，每个订阅只允许一次溢出报告
- **可写成**：一次通过不代表并发正确

### 12. 状态增量乱序

修订号和历史在 `_sync` 内分配，live 入队却在观察者之后、锁外。并发发布可产生“修订 2 先到、修订 1 后到”。

- **发现方式**：审核静态推导 + 阻塞观察者探测
- **修法**：把修订分配、历史写入、live 入队放进同一临界区，观察者移到锁外
- **可写成**：文档写了“严格递增”，代码没做到

### 13. 快照携带插件类型，钉住 ALC

`PluginHandle` 含 `PluginManifest`，清单里的 `ServiceRequirement.Type` 可能属于插件 ALC。宿主保留句柄就永久钉住程序集。

- **发现方式**：仓库外最小插件探测，`retained-handle: assemblyAlive=True` / `dropped-handle: False`
- **修法**：快照只留 Id/Version/State，完整清单改显式 `GetPluginManifestAsync`
- **可写成**：一个 record 字段的代价

### 14. 共享契约漏配导致插件不可卸载

共享契约登记是 `PluginLoadContext` 上的进程级静态集合，而该类是 `internal`——**外部宿主根本调不到**。漏配时插件先登记、激活失败、留下需要重启宿主的隔离代次。

- **发现方式**：最终复审在干净进程里加载共享契约插件
- **修法**：公开 `RegisterSharedContract`，按内核实例隔离、句柄可撤销；装配失败时回滚条目并给出可操作诊断
- **可写成**：设计成 internal 的配置入口，等于没有入口

### 15. 类型发现阶段的异常绕过了诊断

修完上一条后，写进程内测试时才暴露：漏配时异常发生在 `FindPluginType` 的 `assembly.GetTypes()`，抛的是 `ReflectionTypeLoadException`，不是激活阶段的 `FileNotFoundException`。两条路径，一套诊断只覆盖了一条。

- **发现方式**：新测试断言异常类型，实际得到 `ReflectionTypeLoadException`
- **修法**：递归遍历 `InnerException` 与 `LoaderExceptions` 统一识别
- **可写成**：修完一个问题，测试立刻找出它的兄弟问题

---

## 三、测试缺陷（测试本身在说谎）

这一类最值得写，因为它直接对应“把编译通过当移植完成”。

### 16. 名为“跨 ALC 事件同一性”的测试没有事件

`Cross_alc_event_type_identity_is_preserved` 实际只反射读 `Manifest` 和 `PluginId`，没有 `Subscribe`、没有 `Publish`。名字和内容完全不符，且验收表把它勾成了“已覆盖”。

- **修法**：新建独立共享契约程序集，真实调用服务、真实发布订阅事件
- **可写成**：测试名可以骗过所有看报告的人

### 17. 大量断言走内部诊断通道，绕开了公开语义

激活窗口内的断言迁移到 `internal ResolveDiagnostic`，因此这些测试**不验证公开 `Resolver` 的选择行为**。公开路径错了，测试照样绿。

### 18. 阻塞式测试写错，导致 testhost 崩溃

`UnloadAsync` 用例用了静态插件（不可卸载）且用阻塞激活占住协调器，测试跑了 30 秒后 host 进程崩溃并生成 dump。

- **教训**：并发测试的阻塞点必须保证 finally 放行；测试自身也可能成为故障源

### 19. 依赖隐式进程状态，导致假通过/假失败

漏配共享契约的测试一度无法在测试进程内复现——因为测试项目自己引用了该契约，默认 ALC 永远能解析到它。

- **修法**：新增一个**测试项目刻意不引用**的契约程序集，并在测试开头断言这个前提
- **可写成**：测试的隐含前提，就是它最脆弱的地方

### 20. 文档写死的测试数量和实际不符

README 与状态板写“126 项”，实际是 128；再后来变成 140、143、146、148。每次改动都要手工同步，每次都忘。

- **可写成**：写进文档的指标，迟早和代码不一致

---

## 四、判断错误（AI 协作视角，这类素材最适合公众号）

### 21. 把“Monitor 可重入”误判为死锁

一度断言“在 `lock (_sync)` 内 Dispose 会重入死锁”。C# 的 `Monitor` 对同一线程可重入，这个推断本身不成立。如果没被纠正，会去修一个不存在的问题。

### 22. 用“100 项测试全绿”宣布 M0 封版

测试全绿、构建 0 警告之后，直接以“M0 已封版”为前提开始 M1 调研。但当时排水超时、失败卸载、预算缺失等阻断问题全都还在，状态板自己写着“总体验收未完成”。用户随后要求审核，审核发现 7 类问题。

- **这是整个 M0 最重要的一次教训**
- **可写成**：测试数量是工作量指标，不是正确性指标

### 23. 把“静态预检共享契约”当成可行方案，连错两次

修漏配问题时先做静态预检：
- 第一版按整个程序集判断 → 误伤同程序集内不相关的类型
- 第二版按插件类型签名判断 → 漏掉方法体内部的间接使用

两次都失败后放弃静态推断，改为“真实装配失败时报错并回滚”。**静态推断依赖图不可靠**这个结论是花两次错误换来的。

### 24. 把 `tail` 的退出码当成 `git push` 的退出码

多次 `git push | tail` 后 `echo $?` 得到 0，实际推送失败。后来统一加 `set -o pipefail`。

---

## 五、可用的文章角度

按“有没有戏剧性”排序：

1. **100 项测试全绿，我宣布封版，然后审核发现 7 个阻断问题**
   —— 起承转合最完整，适合做系列开篇
2. **移植的最高境界是行为等价，但只做框架会误导**
   —— 呼应“行为等价”那句话：我们没有 oracle，所以把参照物换成自己写的契约 + 真实故障
3. **测试名可以骗过所有看报告的人**
   —— 跨 ALC 事件测试没有事件，验收表却勾了“已覆盖”
4. **一个 record 字段钉住了整个程序集**
   —— 从 `PluginHandle.Manifest` 到 ALC 永驻，适合讲 .NET 的引用根
5. **超时参数拆成四个之前，停用逻辑有多脆**
   —— 取消、排空、事件、清理，四个阶段一套预算不够用
6. **AI 写代码，AI 审代码，错误从哪来**
   —— 第 21、22、23 条：误判可重入、过早宣布完成、静态推断连错两次
7. **一次通过不代表并发正确**
   —— 重复跑 5 轮才发现溢出重复报告

避免的角度：把它写成“AI 一次写对了”。事实是三轮审核、13 个问题、多次判断错误。真实的过程比结论有说服力。

---

## 六、素材对应关系

| 主题 | 证据位置 |
|---|---|
| 首轮审核 7 项 | 提交 `93cd6c8`、`docs/status.md` 进度日志 |
| 第二轮 6 项 | 提交 `93cd6c8` 后的复审探测工程（仓库外） |
| 快照治理 | 提交 `cc71ad0`、`KernelSnapshotIsolationTests` |
| 共享契约 | 提交 `ee6b809`、`dbde45d`、`KernelSharedContractTests` |
| 并发溢出 | `EventBusLifecycleRegressionTests` |
| 状态流顺序 | `KernelStateCommitTests` |
| 隔离语义 | `KernelIsolationTests`、`KernelRecoveryRegressionTests` |

仓库外探测工程（不在版本控制内）：

- `%TEMP%/dougong-lifecycle-audit-20261023-7fd94e66` — 生命周期与服务
- `%TEMP%/dougong-m0-review-qxnot82w` — 动态加载与卸载
- `%TEMP%/dougong-review2`、`dougong-review2b`、`dougong-review2c` — 第二轮复审
- `%TEMP%/dougong-alc-probe`、`dougong-alc-runner` — ALC 引用根
- `%TEMP%/dougong-final-review` — 最终复审
- `%TEMP%/dougong-evidence-r1` — 用当前 DLL 复跑第一轮场景（证据留档用）

这些工程的价值在于：**它们不共享测试宿主的隐式前提，只能走公开入口**，因此比单元测试更接近“外部可观察行为”。这一点本身也值得写。

### 临时目录不可依赖

上面这些工程全在 `%TEMP%` 下，重启或清理后可能消失，且不在版本控制内。因此把关键输出直接摘录在下面，保证文章素材自带证据。

---

## 七、实测输出留档（2026-10-23，当前 HEAD `0f4cd06`）

所有输出均使用 Release DLL，由独立进程执行。格式为 `场景: 实际输出`。

### 7.1 第一轮审核场景（修复后复跑）

```text
R1 r1-timedout-activate: state=Failed, retry=InvalidOperationException,
   serviceMissing=True, invocationRunning=True
R1 resolveall: count=2, names=low+high
R1 cleanup-order: high,consumer,low (consumer-before-low=True)
```

对应三个原缺陷：

- **排空超时后再激活**——修复前 `Activate` 返回 `Stopped`、`activations=1`、服务缺失但无障碍返回；修复后返回 `Failed` 且重试抛 `InvalidOperationException`。注意 `invocationRunning=True` 保留在输出里，如实反映“超时只结束宿主等待，不终止插件”。
- **`ResolveAll` 只返回一个提供方**——修复前 `count=1, names=high`；修复后 `count=2`。
- **清理顺序**——`consumer-before-low=True`。这里 `high` 排在 `consumer` 之前是**正确**的：`high` 是晚到的第三方提供方，与 `consumer` 之间不存在依赖边，二者顺序无关。最初的判断以为这仍是缺陷，加断言核对后才确认算法没错。这条本身也是素材：**看输出顺序就下结论，容易误判**。

### 7.2 第二轮审核场景（修复后复跑）

```text
state-stream-after-dispose: completed=True
foreign-flow-bare-resolve: error=InvalidOperationException, value=null
negative-history-limit: ArgumentOutOfRangeException
last-observer-error: type=CustomPluginException, sameInstance=False
cleanup-timeout: providerCleanedWhileConsumerRunning=False, order=[consumer-started]
deactivate-blocking-cancel-callback: boundedByCancellationTimeout=True, state=Failed
cleanup-token: canBeCanceled=True
```

修复前对应的实测值：

```text
state-stream-after-dispose: completed=False
foreign-flow-bare-resolve: error=none, value=scoped
negative-history-limit: 注册时才抛 ArgumentException
last-observer-error: sameInstance=True（持有插件异常对象）
cleanup-timeout: providerCleanedWhileConsumerRunning=True, order=[consumer-started,provider-cleanup]
deactivate-blocking-cancel-callback: boundedByCancellationTimeout=False, state=Deactivating
cleanup-token: canBeCanceled=False
```

### 7.3 ALC 引用根

```text
retained-snapshot: assemblyAlive=False summaryCount=1
retained-manifest: assemblyAlive=True (expected True: explicit opt-in)
```

修复前同一场景：`retained-handle: assemblyAlive=True`。

含义：保留卸载前取得的句柄或快照不再阻止程序集回收；显式取回完整清单仍会钉住，这是设计上要求调用方主动承担的选择。

### 7.4 共享契约漏配

修复后：

```text
load threw: LifecycleConfigurationException:
  无法加载程序集 'Dougong.Sample.Shared, Version=0.1.0.0...'。
  若它是宿主与插件共享的契约，请在加载前调用 DougongKernel.RegisterSharedContract 登记；
  否则请确认该依赖随插件一同部署。
entries after load: 0
```

修复前：`Failed` 状态 + 底层 file-not-found + 条目留存且 `UnloadAsync` 拒绝（只能重启宿主）。

### 7.5 并发与稳定性

```text
concurrent-act-deact: completed=True, activations=1, finalState=Stopped
dispose-vs-deactivate: completed=True, cleanups=1
double-unload: second=KeyNotFoundException
```

以及溢出重复报告：修复前重复运行 5 轮时出现 `Assert.Single() Failure: The collection contained 3 items`；修复后连续 5 轮全绿。

### 7.6 测试与构建基线

```text
dotnet build Dougong.slnx -c Release    0 警告 0 错误
dotnet test  Dougong.slnx -c Release    148 通过 0 失败 0 跳过
dotnet pack                             6 个 nupkg
```

---

## 八、新增素材：审查输出本身也会误判

复跑第一轮场景时，`cleanup-order` 输出 `high,consumer,low`，第一反应是“清理顺序仍然错了”。但加一条断言 `consumer-before-low` 后确认：`consumer` 确实先于它实际使用的 `low`；`high` 与 `consumer` 之间没有依赖边，先后无关。

这条值得单独写，因为它和整个文档的主题同构：**看现象就下结论，和“编译通过就算完成”是同一种错误**。区别只是前者发生在人身上，后者发生在流程里。

---

## 九、一句话结论

M0 的产出不是一个能跑的 Agent，而是一个**能在失败时保持诚实的内核**：失败就隔离、超时就有界、卸载不伪报、快照不钉住程序集。这些性质没有一条能靠“编译通过”验证，全部靠故障注入和独立探测拿到实证。
