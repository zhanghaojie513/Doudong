# 实施状态板

> 执行入口：技术契约见 [architecture.md](architecture.md)，总体决策与验收见 [design.md](design.md)。完成设计、完成实现和通过验收分别记录；新会话从本文件进入。

## 当前状态（2026-10-24）

- M0 已关闭：三轮审核问题全部修复，13 项独立探测通过，验收项逐条勾选并附证据。公共 API 仍为 experimental。
- 实际工程为 6 个产品项目 + 1 个测试项目 + 5 个样例工程 + 1 个对比探针工程。Agent 已有契约与最小 dispatcher；Ai/Mcp/Acp 仍为空工程；CLI 仍为占位。Telemetry 未建。
- 已修复本轮审核问题：排空超时后拒绝再激活与卸载；`UnloadAsync` 在停用后重新核验失败标志与最终状态；`LoadAsync` 取消/失败回滚残留条目；激活、取消、服务排空、事件排空与清理各自有独立预算；`Context.DisposeAsync` 走内核协调器，不能绕过隔离；`ResolveAll` 返回全部提供方；清理顺序按实际依赖边计算；普通退订立即从事件总线移除并可回收；事件先排空再清理；状态提交在锁内原子完成，新增登记/移除增量与共享诊断 DTO。
- .NET SDK 10.0.402 可用；完整解决方案 Release 构建通过（0 警告、0 错误），156 项内核行为测试全部通过（0 跳过）。
- 首个提交 `5c1bbe5` 及后续切片均已推送至 [GitHub 仓库](https://github.com/zhanghaojie513/Doudong)。
- [前沿调查](frontier-roadmap.md)与[未来迭代路线](iteration-roadmap.md)已建立；后者面向 M3 之后，不作为当前 M0 已完成的证据。
- M0 期间的实现缺陷、测试缺陷与判断错误已整理到[开发历程素材](dev-journey.md)，供后续梳理开发记录使用；该文件不是验收依据。

## 评审修订清单

| 顺序 | 设计修订 | 依据 | 实现/验收 |
|---|---|---|---|
| 1 | 已补必需/可选需求、Blocked、生命周期事务、租约排空、清理与跨 ALC 契约 | architecture.md §3 | 已实现静态生命周期、需求图、服务激活事务、调用租约、事件队列、状态快照与 ALC 装卸 |
| 2 | 已补 schema、复合/未知权限、审批绑定、资源锁与目标包依赖图 | architecture.md §2、§4.3–4.4 | 未开始；csproj 待同步 |
| 3 | 已撤回必须自研循环的预设，定义 MAF/自研同用例比较门槛 | design.md §2.1 | 未运行，最终实现未选定 |
| 4 | 已补事件顺序/背压/错误/排空及 Skill 来源/外发/撤销/最小评测 | architecture.md §3.4、skills-evolution.md | 未开始 |
| 5 | 已同步 README、理念、总体方案和本状态板 | 本轮六份文档 | 文档一致性检查，不代表运行验收 |

## 开工前置

- [ ] SDK 10.0.402 已用于构建与测试；仍需固定仓库 SDK 选择以保证可复现，不把 SDK 11 当唯一选择。
- [ ] 核实 NuGet 版本兼容并同步目标引用图：Kernel 抽象包、Agent M.E.AI.Abstractions、Mcp→Agent；Telemetry 在其里程碑创建。
- [ ] restore/build/test 已通过，恒真测试已移除；pack 已产出 6 个 nupkg，包自述文件与 XML 文档待补；完整内核反例集待补齐。
- [ ] 仓库已首次提交并推送；工程元数据仍有 placeholder，发布前需同步实际仓库 URL。提交仅在用户明确要求时执行。

## M0 内核任务与验收

> 2026-10-24：以下勾选依据是**实测**而非文档承诺。每项附证据位置；独立探测工程位于
> 仓库外 `%TEMP%/dougong-m0-final`，13 项全部 PASS。M0 据此关闭。

- [x] 契约与组合根：静态/动态注册、期望启用状态、激活代次、必需能力和作用域租约。
  - 证据：`KernelM0AcceptanceTests`、`KernelServiceTransactionTests`、`KernelServiceLeaseTests`；
    探测 `lifecycle-concurrent`、`resolveall-all`。
- [x] 生命周期协调器：静态/动态注册的激活暂存/提交/回滚及一致切面；插件回调不持内部锁。
  - 证据：`KernelDependencyTests.Cleanup_can_query_kernel_during_stop_rollback_and_disposal`、
    `KernelIsolationTests`、`KernelRecoveryRegressionTests`；探测 `isolation-rejects-reuse`。
- [x] 需求图：必需缺失/恢复、可选空集合后新增、多提供方选择/冲突和依赖环诊断。
  - 证据：`KernelDependencyGraphTests`、`KernelProviderSelectionTests`、`KernelProviderPriorityTests`、
    `KernelManifestImmutabilityTests`（清单为只读快照，外部改动不能改变依赖判定）。
- [x] 停用排空：拒绝新调用、等待在途、逆序幂等清理、错误聚合与忽略取消诊断；禁止重叠代次。
  - 证据：`KernelServiceLeaseTests`、`KernelReview2RegressionTests`（清理超时停止后续清理、
    取消回调预算）、`KernelR3DependencyRegressionTests`（按实际依赖边排序）；
    探测 `cleanup-consumer-before-used-provider`、`stale-reference-dead`。
- [x] 事件通知：内核版本顺序、有界队列、异常隔离、快照重同步、退订后在途排空。
  - 证据：`KernelEventBusTests`、`EventBusLifecycleRegressionTests`、`KernelStateCommitTests`；
    探测 `event-unsubscribe-releases`、`event-self-stop-rejected`。
- [x] 动态加载：共享契约白名单、完整支持范围验证、私有依赖解析、清理外部引用和重新加载。
  - 证据：`KernelDynamicLoadingTests`、`KernelContractVersionTests`、`KernelCrossAlcAcceptanceTests`、
    `KernelSharedContractTests`、`KernelSharedContractMissingTests`、`KernelUnloadReferenceTests`；
    探测 `unload-idempotent-rejects`、`missing-shared-contract-rollback`。
- [x] 示例与测试：真实服务/事件、失败激活、并发启停、清理和服务事务回滚、慢观察者、
  动态加载及多轮调用后 WeakReference 卸载。
  - 证据：`KernelSnapshotIsolationTests`（快照不钉住 ALC）、`KernelCrossAlcAcceptanceTests`
    （真实共享契约的服务与事件双向）、`KernelUnloadReferenceTests`。
- [x] architecture.md §3.7 与 §3.4 全部用例通过。
  - 证据：上述测试与 13 项独立探测；探测 `state-revision-monotonic`、
    `state-stream-ends-on-dispose`、`state-registered-delta`。

### 关闭前最后一轮修复（2026-10-24）

- `PluginManifest` 曾持有调用方传入集合的引用：外部 `Clear()` 可绕过必需依赖使消费者直接
  `Active`；激活后注入需求又会让已激活插件变 `Blocked`。已改为构造时复制为只读快照。
  测试 `KernelManifestImmutabilityTests`；探测 `manifest-mutation-before-register`（修复前
  `Active`，修复后 `Blocked`）与 `manifest-mutation-after-active`。
- 移除只读死字段 `PluginEntry.DrainTimedOut`（隔离实际由 `IsolationReason` 承载）与未使用的
  `UnloadTask`；行为不变，156 项测试全绿。

## M1 入口与执行契约

- [x] 用同一假模型/工具测试 design.md §2.1 两个候选；记录版本、复现命令和选择理由。
  - 证据：[m1-candidate-comparison.md](m1-candidate-comparison.md)。两候选在同一 10 项反例集上
    均 10/10；最终选**候选 B（自研循环）**，依据是候选 A 存在可复现的事件顺序错误、
    终止原因错误、每场景多一轮模型调用，且关闭旁路依赖带 `ExperimentalAttribute` 的
    `EnableInvocableFunctionBypassing`。
  - **真实 provider 已验证**：六个模型各 5/5 通过——`mimo-v2.6-flash`、`MiniMax-M3.1-Flash`、
    `deepseek-v4.1-flash`（Waygate）、`GLM-5.3`、`GLM-5.3-Flash`（BigModel）走 OpenAI 协议，
    `gpt-6.1-sol`（Routin）走 Anthropic 协议。两个适配器实现同一 `IChatClient`，
    被比较的始终是循环。Routin **Plan** 端点因订阅未激活 403，非代码问题。
  - 真实调用暴露三个假模型测不出的缺陷：循环未把工具声明传给模型（模型输出 XML 伪调用）、
    空 `choices` 帧越界、参数分片语义（应按 index 拼接原文而非按键合并字典）。
  - Waygate 另外暴露两种"空字符串当缺失"的协议变体：后续帧用空字符串覆盖 `id`/`name`
    （原 `??` 只挡 null），以及用空字符串表示未完成的 `finish_reason`（导致每个 delta 误下发）。
    已修复为"只接受非空值"。
- [ ] 唯一 dispatcher：schema/参数规范化、审批身份绑定、执行前校验、调用去重、资源冲突和取消终态。
  - 已实现（`src/Dougong.Agent/ToolDispatcher.cs`）：最小 JSON schema 校验（顶层 type 与 required）、
    调用 ID 去重、权限描述采集、七种明确终态、旁路检测用的调用记录、冻结工具声明的对外投影。
  - **工具声明通道已落地**：`Dougong.Agent.ToolDeclarations` 通过 `ChatOptions` 附加属性
    （键 `dougong.toolDeclarations`）传递冻结描述符，运行时与连接器解耦；
    附 6 项契约自检（`-- contract`）。
  - **未实现**：审批流程、资源锁与工作区独占、执行前文件版本复核、完整 schema 校验器。
    因此当前不得宣称具备“批准 A 执行 B”的防护。
- [ ] 显式存储提交、单写者租约、半条尾记录处理、结果未知恢复、分支一致切面及预算/压缩失败协议。
- [ ] 存储失败、工具副作用后未提交、同文件写冲突、跨会话 shell、工具替换、流式参数不全等故障测试。
- [ ] 真实编码任务与 CLI 构建/打包通过；模型/工具代码实际接入后再记录已验证能力。

## 桌面恢复与重连（设计已修订，未实现）

契约依据：[architecture.md §6](architecture.md#6-宿主与进程模型)；总体方案：[design.md §7](design.md#7-宿主与进程模型与-dougong-desktop-的协同)。Desktop 源码由其独立仓库交付，本仓库维护 SDK 依赖契约，不把桌面设计当作当前 SDK 功能。

- [x] 固定 AgentHost 直接内嵌 SDK，CLI 为另一宿主；内部命名管道与标准 ACP stdio 分开。
- [x] 定义存活重连/崩溃恢复、审批连接失效、PTY 所有权与退出策略，以及显式提交/暂存流式边界。
- [ ] SDK：提交/恢复、命令去重、未知结果和租约实现通过 M1 验证。
- [ ] Desktop：启动/认证、附着与会话状态同步、有界增量队列；会话语义优先采用 ACP 标准方法。
- [ ] Desktop：审批断线/重发/过期和重复答复不得重复执行。
- [ ] Desktop：终端 ID、有界输出缓冲与输入去重；VT 屏幕状态恢复不纳入首版验收。
- [ ] architecture.md §6.7 全部故障用例、标准 ACP stdout、干净 Windows 打包与 NativeAOT 兼容验证通过后才宣称完成。

## M2/M3 后续验收入口

- [ ] M2 ACP/MCP 按版本/能力联调，未知权限、schema 变化、审批取消和日志出口均覆盖。
- [ ] M2 L1 默认候选待审；受限自动启用需用户选择，来源/作用域/撤销/隐私双数据流和最小留出评测齐备。
- [ ] M3 API/行为兼容、作者模板和卸载测试稳定化；完整 Skill 进化和评测优化按验证结果后续建设。

## 约定

- 当前 API 全部 experimental，编译契约详细定义以 architecture.md 为准，design.md 保持摘要和决策，避免复制多份接口后漂移。
- 实现与其行为测试一起交付；未运行、失败、未覆盖的检查必须如实记录，文档勾选不替代测试结果。
- 原工期和桌面周数是未验证估算，按 PoC 和单人产能重估，不当作并行人力或交付承诺。
- M1 验收后再用 CLI 辅助后续开发；自用不能替代故障与独立任务验证。

## 进度日志

- 2026-10-22：建立设计文档与仓库骨架、定名 Dougong；当时“设计定稿”的结论已被 2026-10-23 技术评审修正。
- 2026-10-23：完成桌面内嵌宿主、IPC、恢复/重连、审批断线与 PTY 设计修订。
- 2026-10-23：按顺序修订其余内核/工具/依赖/MAF/事件/Skill 问题，并同步 README、理念和状态。
- 2026-10-23：完成 Kernel 生命周期首批实现与回归测试，修复清理重入、失败重试和激活/停用竞态；首个提交 `5c1bbe5` 创建并推送。
- 2026-10-23：完成服务激活事务首个切片：激活代次暂存、成功原子发布、失败/停用回滚、旧上下文隔离；Release 构建与 25 项内核测试通过。
- 2026-10-23：完成服务调用租约切片：代次绑定引用、原子在途计数、停用批次封口/取消/排空、旧引用失效与销毁排空；Release 构建与 32 项内核测试通过。
- 2026-10-23：补逆拓扑清理顺序与排空超时诊断：批次按激活序逆序清理，超时标记 Failed 并跳过清理；Release 构建与 34 项内核测试通过。
- 2026-10-23：补需求图能力：可选服务需求、插件级依赖阻塞与激活顺序、缺失依赖诊断、注册期依赖环拒绝（含完整路径）；Release 构建与 43 项内核测试通过。
- 2026-10-23：补事件总线有界异步投递：每订阅独立队列、发布不执行插件代码、异常隔离、溢出断开并诊断、停用/销毁排空；Release 构建与 50 项内核测试通过。
- 2026-10-23：补原子状态快照与修订号增量订阅：快照/订阅同临界区建立、修订号单调递增、溢出发出重同步信号并结束序列；Release 构建与 55 项内核测试通过。
- 2026-10-23：补同键多提供方显式选择：`ServiceRequirement.ProviderId` 在需求判断与运行时解析两侧生效，未指定时仍按歧义阻塞；Release 构建与 62 项内核测试通过。
- 2026-10-23：补动态加载与卸载：collectible ALC、宿主共享契约、`LoadAsync`/`UnloadAsync`、样例外挂插件、多轮装卸后的程序集回收验证；Release 构建与 69 项内核测试通过。
- 2026-10-23：补契约版本范围校验：`ContractVersion` 声明、宿主版本表、加载期兼容检查与拒载，样例拆为独立程序集；Release 构建与 79 项内核测试通过。
- 2026-10-23：收敛裸 `Services.Resolve`：仅激活回调窗口可用，窗口外抛错并指向 `Resolver`；Release 构建与 84 项内核测试通过。
- 2026-10-23：M0 验收收尾：`dotnet pack` 产出 6 个 nupkg；补可选服务后新增、跨 ALC 类型同一性、多轮真实调用后卸载的验收测试；Release 构建与 90 项内核测试通过。
- 2026-10-23：补齐 M0 最后两项：提供方注册优先级自动选择（并列仍歧义）与状态增量分区标记/游标续订；Release 构建与 100 项内核测试通过。
- 2026-10-23：M0 独立审核发现阻断问题（排空超时后可重激活、失败卸载、预算缺失、ResolveAll 收缩、清理顺序、事件引用残留、状态乱序）。按 R1–R6 修复：隔离代次与卸载事务、分阶段预算、集合解析与实际依赖清理、事件封口/排空/引用释放、原子状态提交与登记/移除增量、真实跨 ALC 共享契约验收；Release 构建与 128 项内核测试通过。
- 2026-10-23：第二轮独立复审发现 6 项实现与契约不符：激活窗口可从无关异步流借用、清理超时后仍清理提供方、`DeactivateAsync` 取消回调无预算、cleanup 令牌不可取消、观察者诊断仍持有插件异常、内核销毁后状态流不结束，另加历史上限缺少校验。已全部修复并补回归测试；复审探测 6 项均返回期望值，Release 构建与 140 项内核测试通过。
- 2026-10-23：处理外部引用治理：`PluginHandle`/`PluginSummary` 不再携带 `PluginManifest`，只保留 Id/Version/State；完整清单改由 `GetPluginManifestAsync` 显式取得。实证保留快照不再阻止动态插件 ALC 回收，显式持有清单仍会（属调用方主动选择）。Release 构建与 143 项内核测试通过。
- 2026-10-23：最终复审发现共享契约登记是进程级静态状态且不可撤销，漏配时插件会先登记、激活失败、留下不可卸载的隔离代次。改为 `DougongKernel.RegisterSharedContract`（按内核实例隔离、返回句柄可撤销）；装配解析失败时 `LoadAsync` 回滚条目并抛出提示登记契约的可操作诊断。Release 构建与 146 项内核测试通过。
- 2026-10-24：M0 关闭复审：`PluginManifest` 改为只读快照（外部改动不再能绕过依赖判定），移除死字段；13 项独立探测全部通过，M0 验收项据此勾选。
- 2026-10-24：M1 真实 provider 验证：用 MiMo 与 MiniMax 各跑 5 项真实调用（含真实编码任务），全部通过；期间修复三个仅真实协议暴露的缺陷（工具声明未传给模型、空 choices 越界、参数分片语义）。
- 2026-10-24：M1 首批：落地 `IAgentRuntime`/`IToolDispatcher`/`IAgentTool` 契约与最小 dispatcher；用 10 项确定性反例对比 MAF 包装与自研循环，均 10/10，选择自研循环（详见 m1-candidate-comparison.md）。真实 provider 对比待验证。
- 2026-10-23：补漏配路径的进程内回归：新增 `Dougong.Sample.Isolated`（测试项目刻意不引用）与对应插件，未登记时装配解析必然失败，因此能在 CI 中真实覆盖拒绝与回滚；测试先断言该前提，误引用会显式失败而非静默假通过。同时修正 `LoadAsync` 在类型发现阶段抛 `ReflectionTypeLoadException` 时未给出可操作诊断的问题。Release 构建与 148 项内核测试通过。
