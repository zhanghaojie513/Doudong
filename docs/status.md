# 实施状态板

> 执行入口：技术契约见 [architecture.md](architecture.md)，总体决策与验收见 [design.md](design.md)。完成设计、完成实现和通过验收分别记录；新会话从本文件进入。

## 当前状态（2026-10-24）

- M0 已关闭：三轮审核问题全部修复，13 项独立探测通过，验收项逐条勾选并附证据。公共 API 仍为 experimental。
- 实际工程为 6 个产品项目 + 1 个测试项目 + 5 个样例工程 + 1 个对比探针工程。Agent 已有契约与最小 dispatcher；Ai/Mcp/Acp 仍为空工程；CLI 仍为占位。Telemetry 未建。
- 已修复本轮审核问题：排空超时后拒绝再激活与卸载；`UnloadAsync` 在停用后重新核验失败标志与最终状态；`LoadAsync` 取消/失败回滚残留条目；激活、取消、服务排空、事件排空与清理各自有独立预算；`Context.DisposeAsync` 走内核协调器，不能绕过隔离；`ResolveAll` 返回全部提供方；清理顺序按实际依赖边计算；普通退订立即从事件总线移除并可回收；事件先排空再清理；状态提交在锁内原子完成，新增登记/移除增量与共享诊断 DTO。
- .NET SDK 10.0.402 可用；完整解决方案 Release 构建通过（0 警告、0 错误），156 项内核行为测试全部通过（0 跳过）。
- 首个提交 `5c1bbe5` 及后续切片均已推送至 [GitHub 仓库](https://github.com/zhanghaojie513/Doudong)。
- [前沿调查](vision/frontier-roadmap.md)与[未来迭代路线](vision/iteration-roadmap.md)已建立；后者面向 M3 之后，不作为当前 M0 已完成的证据。
- M0 期间的实现缺陷、测试缺陷与判断错误已整理到[开发历程素材](journey/m0-dev-journey.md)，M1 的同类素材见 [m1-dev-journey.md](journey/m1-dev-journey.md)；这两份文件不是验收依据。
- 本文只回答"做到了哪一步"。**要达成什么**见 [goal.md](goal.md)，**什么算做完**见
  [quality-gates.md](quality-gates.md)，**下一步做什么**见 [plan.md](plan.md)。
  勾选任一项前，先确认它过了 [quality-gates.md](quality-gates.md) 的六道门。

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
    `gpt-6.1-sol`（Routin）走 Anthropic 协议；`deepseek-flash` 的三种协议（chat-completions、
    Anthropic 兼容层、Responses）各 5/5。三个适配器实现同一 `IChatClient`，
    被比较的始终是循环。Routin **Plan** 端点因订阅未激活 403，非代码问题。
  - 真实调用暴露三个假模型测不出的缺陷：循环未把工具声明传给模型（模型输出 XML 伪调用）、
    空 `choices` 帧越界、参数分片语义（应按 index 拼接原文而非按键合并字典）。
  - Waygate 另外暴露两种"空字符串当缺失"的协议变体：后续帧用空字符串覆盖 `id`/`name`
    （原 `??` 只挡 null），以及用空字符串表示未完成的 `finish_reason`（导致每个 delta 误下发）。
    已修复为"只接受非空值"。
- [x] 唯一 dispatcher：schema/参数规范化、审批身份绑定、执行前校验、调用去重、资源冲突和取消终态。
  - 已实现（`src/Dougong.Agent/ToolDispatcher.cs`）：最小 JSON schema 校验（顶层 type 与 required）、
    调用 ID 去重、权限描述采集、七种明确终态、旁路检测用的调用记录、冻结工具声明的对外投影。
  - **工具声明通道已落地**：`Dougong.Agent.ToolDeclarations` 通过 `ChatOptions` 附加属性
    （键 `dougong.toolDeclarations`）传递冻结描述符，运行时与连接器解耦；
    附 6 项契约自检（`-- contract`）。
  - 已实现审批与资源锁（`IToolApprover.cs`、`ToolResourceGate.cs`）：
    审批绑定 session/turn/invocation/工具版本/规范化参数摘要/工作区/权限范围/有效期；
    执行前进锁复核，参数、权限或并发计划任一变化即拒绝；等待审批期间不持锁；
    两档并发（Serial / WorkspaceExclusive）。
  - 两档并发**共用同一把工作区执行权**（Serial 额外先取全局串行权）。这是故障验收抓出来的
    真实缺陷：原先两级各用一把独立信号量，串行工具能与同工作区的独占工具重叠执行，
    等于取消没有真正生效。反证已确认——改回独立双锁后，`uncooperative-cancel-still-holds-lock`
    立刻失败。
  - 9 项自检（`-- approval`）全部通过，并用禁用复核的反证确认测试有效
    （权限升级、并发变化、过期三项立刻失败）。
  - 已实现文件版本复核（`WorkspacePathGuard.cs`）：工具通过
    `DescribeFileDependencies` 声明依赖文件；审批时捕获长度+时间+内容哈希，
    执行前进锁后重新捕获并比对，文件被改动/新建即拒绝。
  - 已实现工作区路径守卫：绝对路径与 `../` 越界、大小写不敏感前缀、
    符号链接/重解析点逐段解析（限深度 8）。
  - 8 项自检（`-- fileversion`）全部通过，并用禁用比对的反证确认测试有效。
  - **完整 JSON Schema 校验已实现**（`ToolSchemaValidator`）：类型（含 integer 与 number
    的区分）、嵌套 required、enum/const、数值边界、字符串长度与 pattern、数组 items 与
    uniqueItems、additionalProperties:false。8 项验收（`-- schema`）。
    关键立场：**遇到不支持的 schema 关键字必须报错**，不能静默跳过——
    静默跳过等于向使用者宣称"已校验"而实际没有。
  - **未实现**：资源级读写键。
  - **明确保留的竞态**：指纹计算与真正打开文件之间仍可能被替换，
    当前只能降低概率，不宣称消除。
- [x] 显式存储提交、单写者租约、半条尾记录处理、结果未知恢复、分支一致切面及预算/压缩失败协议。
  - 已实现（`src/Dougong.Agent/IAgentStore.cs`、`JsonlAgentStore.cs`）：提交屏障四个落点
    （用户输入→模型响应→工具开始→工具终态）、单写者 `FileShare.None` 租约、
    尾部半条记录隔离、序号缺口检测、`OutcomeUnknown` 对账。
  - 6 项屏障自检（`-- store`）全部通过；已反证测试有效（移除工具开始屏障立刻报
    "提交失败后仍执行了工具 1 次"）。
  - **审批决定与运行终态已落盘**（原缺口：`StoreRecordKind.ApprovalDecision` 与
    `RunFinished` 的使用点均为 0）：`ToolDispatcher` 新增可选 `IAgentStore`，
    审批后立即提交记录，绑定 invocationId / 工具身份与版本 / 参数摘要 / 工作区 /
    决定时刻；提交失败阻止执行。
  - 新增 `ApprovalLog` + `ApprovalSnapshot`：`IsUnaccounted` 让"有调用但无审批记录"
    可表达，不与"被拒绝"混淆——这正是 `OutcomeUnknown` 对账最需要的信息。
  - `RunFinished` 收口到 `FinishAsync` 单一出口：终止点有七处，逐处插入容易漏；
    提交失败不改变已发生的事实，只并入诊断。
  - 7 项自检（`-- approval-log`，含两个反证）。真实端点日志确认六段记录齐全。
  - **会话 resume / branch / list 已实现**（`SessionRecovery.cs`、`SessionBranch.cs`）：
    重放已提交 JSONL 重建历史并还原工具交互配对；分支 = 从分叉点复制新文件，源会话不变。
  - 9 项会话自检（`-- session`）覆盖：恢复出的历史能**真的续跑一轮**、未知结果阻塞续跑、
    分叉截断、源会话不被改动、分叉点不存在时报错、序号范围与缺口、旧双重编码格式仍可读、
    分支点悬空时向后补齐。
  - 三个反证确认检查有效：历史退化成扁平文本 → 续跑检查失败；分支忽略分叉点 → 截断检查失败；
    模型响应不记参数 → 恢复检查失败。
  - **修掉两个真实缺陷**：
    1. 日志 Payload 被双重编码（`JsonSerializer.Serialize(string)` 把 JSON 包成字符串），
       恢复必须先解一层才能读。已改为按结构存，同时保留读旧会话的兼容。
    2. 在工具交互中间分叉会留下悬空的 `tool_calls`，provider 直接拒掉续跑
       （实测 400 "must be followed by tool messages"）。已改为向后补齐到配对完成处。
  - 真实端点验证：读文件 → 退出 → `--resume` 后模型准确复述文件内容；
    在原本失败的 #2 处分叉，自动补齐到 #4 并续跑成功。
  - **未实现**：压缩失败协议、断电级持久化（当前只声明 `SurvivesProcessCrash`，
    不声明 `SurvivesPowerLoss`）。
- [x] 存储失败、工具副作用后未提交、同文件写冲突、跨会话 shell、工具替换、流式参数不全等故障测试。
  - 6 项故障验收（`-- failure`，`spike/Dougong.Agent.Comparison/FailureAcceptanceChecks.cs`）：
    同文件并发编辑被串行化、读写竞争在复核阶段被拦下、取消不合作仍持锁且如实报"已执行"、
    并发派发的执行次数等于报告成功次数（单一执行入口）、提交失败发生在副作用之前、
    崩溃窗口报 `OutcomeUnknown` 而非重放。重复 5 轮全绿。
  - 这一套立刻抓到一个真实缺陷（两档并发不互斥），见上一节。
  - **跨会话 shell 已实现**（`src/Dougong.Agent/ShellTool.cs`）：11 项验收（`-- shell`）覆盖
    退出码由进程报告、stdout/stderr 分离、args 不经 shell 解释、工作目录固定为工作区、
    宿主密钥不进入子进程、声明 `WorkspaceExclusive` 与 `Execute`、取消与超时都结束进程树、
    输出有界且标记截断、stdin 关闭不挂起、被工作区锁阻塞。
  - shell 的两个反证确认检查有效：去掉 `Environment.Clear()` 立刻报密钥泄漏；
    去掉结束进程树的调用，取消与超时两项同时失败。
  - **shell 抓到一个真实安全缺陷**：`ProcessStartInfo.Environment` 初始就包含父进程的
    全部变量，只往里加白名单项并不等于"只传这些"——`DOUGONG_PROVIDER_KEY` 会一起被继承，
    模型可以执行一条命令把它读走。已改为先 `Clear()` 再填白名单。
  - **不宣称隔离**：命令可以触达进程权限内的一切；本类只保证经过唯一 dispatcher、
    取到工作区独占锁、结果如实上报。CLI 默认不启用 shell，需 `DOUGONG_SHELL=1` 显式开启。
  - **未覆盖**：工具替换（`Register` 句柄够用但未做替换语义测试）、
    流式参数不全（已在 `-- contract` 的畸形 JSON/分片中覆盖，未单列）。
- [x] 上下文压缩：预算计算、保留工具配对、摘要失败不覆盖原历史、检查点可续跑。
  - 已实现（`src/Dougong.Agent/IContextCompactor.cs`、`ContextCompaction.cs`）：
    三阶段检查点（Select → Summarize → Retry → Completed/Failed）、滑动窗口选区间、
    工具调用与其结果的配对保护、失败保留原历史、取消不算失败。
  - 9 项自检（`-- compaction`）全部通过，并用"失败也覆盖历史"的反证确认测试有效。
  - **已接入生产路径**（原缺口：压缩能力只存在于测试里，循环从未调用它）：
    `LoopAgentRuntime` 新增可选 `ContextLimits` + `IContextCompactor` + `summarize`，
    每轮调用模型前检查一次。压缩只处理进入循环前的基线历史——循环内追加的
    assistant/tool 交互不参与，它们必须成对保留。
  - 四种降级路径都保留原历史并继续运行，各自发出带诊断的 `ContextCompacted` 事件：
    摘要失败、未配摘要器、预算不可行、应用失败。中断一个能跑的任务比让它被
    provider 拒绝更糟。新增 `AgentEvent.ContextCompacted` 让宿主如实展示有损压缩。
  - 9 项接入验收（`-- wiring`）：无预算时保持旧行为、压缩后请求变短、事件可观察、
    **工具配对不被拆散**、摘要失败保留历史、无摘要器不假装压缩。
    反证：移除接入点后 4 项立刻失败。
  - 真实端点验证（DeepSeek）：12 轮长会话，估算 9 418 → 3 445 tokens，19 次调用 0 失败。
  - **未实现**：分支式压缩、按真实 token 计数校准（`UsageRecord.InputTokens` 已可对照，
    但换算因子尚未回填为默认值，仍按 2 字符/token 保守估算）。
  - **token 估算已按实测校准**（原为固定 2 字符/token）：2026-10-24 对
    DeepSeek `deepseek-flash` 实测，线性拟合 `tokens = 30 + chars/8`。
    纯 ASCII 长文本约 8 字符/token，中文约 1.2 字符/token——固定 2 会让
    ASCII 高估 4 倍（过早压缩）、中文略低估。改为分段模型：ASCII 取保守值 4、
    CJK 取 1，并计入每条消息与工具调用的固定开销。7 项验收（`-- token`）。
  - **仍然保留的近似**：系数是有意保守的（低估 token 会让会话失败在高估之前），
    不同 provider 的实际分词器不同，不宣称精确。
- [x] 真实编码任务与 CLI 构建/打包通过；模型/工具代码实际接入后再记录已验证能力。
  - 真实编码任务已通过（六个 provider，含写文件与内容校验）。
  - **Ai 层落地**（`src/Dougong.Ai`）：三种线协议各自独立实现（`OpenAiChatCompletionsFormat`、
    `OpenAiResponsesFormat`、`AnthropicMessagesFormat`），共用 `HttpClientChatClient` 传输；
    `ChatClientFactory` 按具名配置建连接器，密钥只从环境变量读。
    CLI 与真实 provider 对比都已切到这一套，spike 里的一次性适配器与 CLI 自带连接器已删除。
  - 10 项线格式自检（`-- wire`）覆盖三种协议的请求形状、流解码与**多轮回填**：
    Anthropic 的 `tool_result` block、/responses 的 `function_call_output` 条目此前从未被构造过。
  - 三协议真实端点复验（DeepSeek `deepseek-flash`，各 5/5）：chat-completions、Anthropic 兼容层、/responses。
  - CLI 已接入（`src/Dougong.Cli`）：REPL + 控制台审批 + 工作区内 read_file/write_file
    + JSONL 会话 + 完整提交屏障 + 三种协议可选。已用 DeepSeek 端到端验证：模型请求调用 →
    控制台审批 → 执行读取 → 真实内容回填 → 模型给出正确结论。
  - **本轮修掉的四个真实缺陷**：
    1. `finish_reason` 被从 `delta` 里读，实际是 `choices[0]` 的兄弟字段——主路径从未命中，
       一直靠流结束兜底掩盖（反证：改回后 `-- wire` 立刻失败）。
    2. 工具结果消息只放 `FunctionResultContent`，而 `ChatMessage.Text` 由 `Contents` 派生，
       于是三种协议拿到的工具结果都是空字符串，模型回答"没有返回任何东西"（反证已确认）。
    3. 文本与 `tool_calls` 被拆成两条相邻 assistant 消息，思考模式 provider 因此拒掉整轮（实测 400）。
    4. 审批被拒绝后同样触发第 3 条：拒绝也必须留下可解释的历史。
  - 会话命令：`--list` 列出会话（记录数、序号范围、缺口）、`--resume <会话>` 恢复、
    `--branch <会话> [--at N]` 分叉；会话可用完整路径、标识或序号（1 = 最新）指定。
  - shell 工具已接入（`DOUGONG_SHELL=1` 启用）。已用 DeepSeek 端到端验证：
    模型请求执行命令 → 控制台审批显示具体命令与 `Execute` 权限 → 执行 → 退出码与真实输出回填。
  - **用量记账已实现**（`src/Dougong.Ai/UsageLedger.cs`）：`UsageRecordingChatClient` 作为装饰器
    包住任意 `IChatClient`，记录每次调用的输入/输出/总量、耗时与成败；`UsageLedger` 按会话与
    自然日聚合。11 项自检（`-- usage`）覆盖流式/非流式、失败与取消留痕、会话隔离、按日汇总、
    并发追加、文本不被用量污染、未报告与零可区分。
  - **修掉一个真实缺陷**：usage 原先以自定义附加属性（`["usage"]`）传出，
    而 M.E.AI 的聚合与中间件只认标准 `UsageContent`，账目会静默丢失。已改为标准类型；
    反证确认——把发送端改回附加属性，`client-emits-usage-content` 立刻失败。
  - 真实端点验证：DeepSeek `deepseek-flash` 报回输入 352 / 输出 3 tokens，`/usage` 正确显示。
  - **不猜单价**：本层只汇总 token 与调用次数，金额依赖 provider 价目表，由宿主换算；
    `UnreportedCalls` 与可空的 `TotalTokens` 用于区分"零消耗"与"不知道"。
  - **未实现**：OTel 指标导出（属 M2，届时把 `UsageLedger` 的汇总接到
    `OpenTelemetry` 1.20.1 的 meter 上）、同目录多会话并行（当前一次只开一个会话）。

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
  - **MCP 工具桥接已实现**（`src/Dougong.Mcp/McpAgentTool.cs`，ModelContextProtocol 2.3.0）：
    远端工具包装为 `IAgentTool`，经过同一个 dispatcher。8 项验收（`-- mcp`）用**真实的
    MCP client + server 内存配对**，不是 mock。
  - 三条硬约束各有验收项：远端 annotations 仅作提示、**不得**翻译成已知权限；
    schema 变化必须改变工具版本（否则旧审批继续有效）；远端 `IsError` 必须是失败终态。
  - 连不上 server 不阻止启动，但必须报错——静默跳过会让用户以为工具已就绪。
  - **修掉一个契约不一致的真实缺陷**：`ToolDeclarations` 把 `Name` 发给模型，
    而 dispatcher 只按 `Id` 查找。M1 里所有工具 `Id == Name` 所以从未暴露；
    MCP 工具 ID 带 server 前缀（`demo/echo`）立刻暴露为"工具已注册却被判未注册"。
    已让 dispatcher 同时按 Id 与 Name 路由，并有反证。
  - CLI 接入：`DOUGONG_MCP_SERVERS="name=command args;..."`，默认不连。
    子进程不继承宿主环境（与 shell 工具同理）。真实 stdio server 端到端验证通过。
  - **MCP 变化检测已实现**（`src/Dougong.Mcp/McpToolRegistry.cs`）：
    订阅 `notifications/tools/list_changed`，远端工具集合或 schema 变化后全量刷新注册。
    4 项验收（`-- mcp-reload`）用真实 server 主动发通知触发：工具刷新、schema 变更
    使版本变（旧审批失效）、移除后不再被派发、多 server 刷新互不影响。
    反证：去掉订阅后 4 项全部失败。
  - **刷新是「先全部注销、再全部注册」**，存在极短的工具不可用窗口。接受这个窗口的
    理由：此时正确行为是「工具暂时不在列表里」，而不是「工具在列表里但指向旧 schema」
    ——后者会让模型按旧 schema 生成参数、远端正按新 schema 解释，即「批准了 A、执行了 B」。
  - **修掉一个真实缺陷**：模型可见的工具名最初用 `server/tool` 形式，真实 provider
    直接 400 拒绝（只接受 `^[a-zA-Z0-9_-]+$`）。改用 `server__tool`。
    这条只有真实端点能发现——假模型不校验工具名字符集。
  - **工具名必须跨 server 唯一**：两个 server 各自暴露 `alpha` 时，若模型可见名称相同，
    模型无法区分，dispatcher 的 Name 别名也只能指向一个。因此 Id 与 Name 都带 server 前缀。
  - **未实现**：MCP resources/prompts、HTTP transport、断线重连、ACP 全部。
- [ ] M2 L1 默认候选待审；受限自动启用需用户选择，来源/作用域/撤销/隐私双数据流和最小留出评测齐备。
- [ ] M3 API/行为兼容、作者模板和卸载测试稳定化；完整 Skill 进化和评测优化按验证结果后续建设。

## 依赖版本

2026-10-24 对照 NuGet 最新稳定版核对。原则：不锁定过时版本，也不追预览版。

| 包 | 版本 | 位置 | 说明 |
|---|---|---|---|
| `Microsoft.Extensions.AI.Abstractions` | 10.11.0 | Agent、Ai、spike | 已是当前最新稳定版 |
| `Microsoft.Agents.AI` | 1.26.0 | 仅 spike（候选 A 对比基线） | 最新稳定版；结论是不能依赖，见 m1-candidate-comparison.md |
| `xunit.v3` | 4.0.2 | 内核测试 | v2 主线已由 v3 接替；M0 测试全部迁移通过 |
| `ModelContextProtocol` | 2.3.0 | M2 计划 | 尚未引入 |
| `OpenTelemetry` | 1.20.1 | M2 计划 | 尚未引入 |

- 测试栈从 xUnit v2 + VSTest adapter 迁到 **xUnit v3 + Microsoft.Testing.Platform**：
  `global.json` 固定 SDK 并启用 MTP 运行器，`Microsoft.NET.Test.Sdk` 与
  `xunit.runner.visualstudio` 已不再需要，删除后 156 项测试仍全绿。
- 迁移暴露一个**测试自身的缺陷**：`Publish_does_not_run_handler_on_publisher_thread`
  在线程池线程上发布再比较线程 ID。事件泵也跑在线程池上，线程池可能复用发布线程，
  于是这个断言在 xUnit v3 的并行调度下稳定失败（VSTest 的时序恰好掩盖了它）。
  已改为在专用非线程池线程上发布；反证确认原写法 5 轮中 2 轮失败。
- xUnit v3 的 `xUnit1051`（建议统一传 `TestContext.Current.CancellationToken`）在
  `.editorconfig` 中降级为建议：内核测试大量 await 的是引擎内部的取消路径，
  统一塞入测试级令牌会把"取消是否被正确传播"与"测试是否超时"混在一起。

## 与 goal.md 的对应（2026-10-24）

四条性质当前状态（详细判定见 [goal.md](goal.md) §4、§6）：

| 性质 | 状态 | 依据 |
|---|---|---|
| P1 可组合 | 部分 | 依赖方向有守卫、内核零第三方依赖；插件生态与可替换性验收未做（M3） |
| P2 完整生命周期 | 基本达成 | 157 项测试 + 20 项对抗探针；断电级持久化未声明 |
| P3 故障可解释 | 基本达成 | 四个提交屏障 + 审批记录 + 未知结果对账；缺工具幂等键 |
| P4 真实可用 | 部分 | 3 协议 + 真实编码任务 + 压缩 + 恢复；MCP/ACP 未接入 |

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
- 2026-10-24：M1 CLI：REPL 宿主接入循环/屏障/审批/JSONL；端到端真实调用通过，
  期间修掉工具结果回填缺 tool_call_id 与 DeepSeek reasoning_content 回传两个协议缺陷。
- 2026-10-24：M1 上下文压缩：三阶段检查点 + 滑动窗口，保护工具配对，
  摘要失败不覆盖原历史；9 项自检通过（含反证）。
- 2026-10-24：M1 文件版本复核：工具声明文件依赖，审批时捕获、执行前比对；
  路径守卫拦下绝对/相对越界与重解析点逃逸。8 项自检通过（含反证）。
- 2026-10-24：M1 审批与资源锁：审批凭据绑定八项维度并做执行前复核，
  9 项自检通过（含反证）；等待审批期间不持锁，避免慢审批阻塞工作区。
- 2026-10-24：M1 提交屏障：`IAgentStore` 契约与 JSONL 实现，四个提交落点串起
  "先提交意图、再产生副作用"；6 项自检通过，并用移除屏障的反证确认测试有效。
- 2026-10-24：清掉 G4/G6/G8 三项遗留（触发条件均由 M2 满足）：
  G4 完整 schema 校验（不支持关键字必须报错）、G6 工具替换后旧审批失效（4 项）、
  G8 token 估算按实测校准（ASCII 8 字符/token、CJK 1.2，改分段模型）。
  另新增 scripts/all-checks.sh —— 固化「反证后必须单独重建 spike」这条纪律，
  该坑已重复四次。

- 2026-10-24：M2.2 MCP 变化检测：McpToolRegistry 订阅 list_changed 并全量刷新，
  4 项验收用真实 server 主动通知触发（含反证 4/4）。
  修掉一个只有真实端点能发现的缺陷：工具名用 server/tool 形式被 provider 400 拒绝
  （只接受 ^[a-zA-Z0-9_-]+$），改用 server__tool。

- 2026-10-24：M2 起步：MCP 工具桥接（ModelContextProtocol 2.3.0）。
  远端工具包装为 IAgentTool 走同一 dispatcher；annotations 不作授权、schema 变化改版本、
  IsError 记失败。8 项验收用真实 client/server 内存配对（含三个反证）。
  修掉一个契约不一致缺陷：模型只看得见工具 Name，而 dispatcher 只按 Id 查找
  ——M1 里 Id==Name 从未暴露，MCP 的 server 前缀立刻触发了它。

- 2026-10-24：M1 G2/G3 修复：审批决定与运行终态从未落盘（两处枚举值使用点均为 0）。
  dispatcher 新增可选 store，审批后提交绑定参数摘要的记录，提交失败阻止执行；
  新增 ApprovalLog/ApprovalSnapshot 让「有调用但无审批记录」可表达；
  RunFinished 收口到单一出口。7 项自检（含两个反证），真实日志六段记录齐全。

- 2026-10-24：M1 G1 修复：上下文压缩从未接入循环（能力只在测试里）。探针先确认缺口
  （10 200 tokens 完整历史全量送进模型），再接入 `ContextLimits` + 压缩器 + 摘要器；
  循环内交互不参与压缩以保证工具配对完整；四种降级路径都保留原历史并发出诊断事件。
  9 项接入验收（含反证），真实端点 12 轮会话 9 418 → 3 445 tokens。
  同时新增 [plan.md](plan.md) 作为排期文档，并登记 10 项缺口台账。

- 2026-10-24：内核对抗性审查：6 个维度共 20 项攻击探针（`KernelAdversarialTests`、
  `KernelDrainProbeTests`）。发现并修复 1 个真实缺陷：同步阻塞的激活回调会独占
  评估循环，使 `ActivationTimeout` 完全无效，其它插件的生命周期变更无限排队。
  修法是回调改为 `Task.Run` 启动；修复前先实测验证了 `Task.Run + WaitAsync` 的
  超时有效性与 `AsyncLocal` 的跨线程传递语义，并有反证。其余 15 类攻击未击穿
  不变量。详见 [kernel-adversarial-review.md](kernel-adversarial-review.md)。

- 2026-10-24：修掉一个 M0 遗留的真实内核缺陷：插件装配回落到宿主进程中
  未登记的共享契约时，加载会静默成功。根因是 `PluginLoadContext.Load` 在插件目录
  解析不到时返回 null，运行时于是回落到默认 ALC——只要同名程序集碰巧已在进程里，
  「登记是加载前置条件」就被绕过。表现为同一份插件在干净进程里失败、在跑过相关代码的
  进程里成功，CI 与本地结果不一致。修法是记录这类回落并在 `LoadAsync` 中拒绝，
  同时排除共享框架程序集（它们回落是唯一正确行为）。附回归测试 + 反证。

- 2026-10-24：CI 修复：测试命令从 VSTest 的 `--logger` 改为 MTP 的 `--output Detailed`
  （前者在 MTP 下表现为 Zero tests ran + 握手失败）；共享契约测试改为用独立可回收 ALC
  制造污染条件，不再把契约永久留在进程里。

- 2026-10-24：M1 用量记账：`UsageRecordingChatClient` 装饰器 + `UsageLedger` 会话/按日聚合，
  11 项自检（含两个反证）。修掉一个真实缺陷：usage 走自定义附加属性时 M.E.AI 的聚合
  与中间件看不见，账目会静默丢失；改用标准 `UsageContent`。真实端点验证 352/3 tokens。

- 2026-10-24：M1 shell 工具：`ShellTool` + `ShellEnvironment` 落地，11 项验收（含两个反证）。
  修掉一个真实安全缺陷：`ProcessStartInfo.Environment` 初始含父进程全部变量，
  只加白名单不等于只传白名单，宿主密钥会被子进程继承。CLI 侧默认不启用，需显式开启。

- 2026-10-24：依赖版本与测试栈核对：对照 NuGet 最新稳定版，AI 抽象 10.11.0 与 MAF 1.26.0
  已是最新；测试栈从 xUnit v2 + VSTest 迁到 xUnit v3 4.0.2 + Microsoft.Testing.Platform
  （删除 `Microsoft.NET.Test.Sdk` 与 `xunit.runner.visualstudio`）。迁移暴露并修复一个
  测试自身的并发缺陷（线程池复用导致线程 ID 断言不稳定，反证 5 轮中 2 轮失败）。

- 2026-10-24：M1 会话恢复与分支：`SessionRecovery`/`SessionBranch` 落地，
  CLI 支持 `--list`/`--resume`/`--branch --at N`；9 项会话自检（含三个反证）。
  期间修掉两个真实缺陷：日志 Payload 双重编码、在工具交互中间分叉留下悬空 tool_calls
  （实测 400）。真实端点验证过恢复后续跑能准确复述上一轮工具读到内容。

- 2026-10-24：M1 Ai 层：三种线协议拆成可独立校验的实现，共用 HTTP 传输与 `ChatClientFactory`；
  一次性适配器与 CLI 自带连接器删除，真实 provider 证据落在交付代码上。
  10 项线格式自检（`-- wire`）首次覆盖多轮回填；三协议真实端点各 5/5。
  期间修掉四个真实缺陷：`finish_reason` 读错字段（一直被兜底掩盖）、工具结果 Text 为空、
  文本与 tool_calls 拆成两条消息导致 400、审批拒绝后同样触发。四项均有反证或真实复现。

- 2026-10-24：M1 故障验收：补 6 项故障用例（同文件并发编辑、读写竞争、取消不合作、单一执行入口、
  提交失败先于副作用、崩溃窗口报未知结果），重复 5 轮全绿。该套件当场抓到一个真实缺陷——
  `Serial` 与 `WorkspaceExclusive` 原本各用一把独立信号量，串行工具能与同工作区独占工具重叠执行；
  已改为共用同一把工作区执行权，并用改回双锁的反证确认检查有效。156 项内核测试与全部自检套件通过。
- 2026-10-24：M1 真实 provider 验证：用 MiMo 与 MiniMax 各跑 5 项真实调用（含真实编码任务），全部通过；期间修复三个仅真实协议暴露的缺陷（工具声明未传给模型、空 choices 越界、参数分片语义）。
- 2026-10-24：M1 首批：落地 `IAgentRuntime`/`IToolDispatcher`/`IAgentTool` 契约与最小 dispatcher；用 10 项确定性反例对比 MAF 包装与自研循环，均 10/10，选择自研循环（详见 m1-candidate-comparison.md）。真实 provider 对比待验证。
- 2026-10-23：补漏配路径的进程内回归：新增 `Dougong.Sample.Isolated`（测试项目刻意不引用）与对应插件，未登记时装配解析必然失败，因此能在 CI 中真实覆盖拒绝与回滚；测试先断言该前提，误引用会显式失败而非静默假通过。同时修正 `LoadAsync` 在类型发现阶段抛 `ReflectionTypeLoadException` 时未给出可操作诊断的问题。Release 构建与 148 项内核测试通过。
