# Dougong（斗拱）设计方案 v1 — 给 .NET 生态一个插件化 Agent Harness（路线 B）

> **状态**：设计稿 v1，2026-10-22（同日定名 **Dougong／斗拱**，见 §11）
> **文档体系**：本文是总体方案（生态位 / 里程碑 / 决策 / 双轨计划）；"为什么这样设计"见 [philosophy.md](philosophy.md)，"系统长什么样、怎么运转"见 [architecture.md](architecture.md)，旗舰特性 Skill 自动进化见 [skills-evolution.md](skills-evolution.md)，外部前沿事实见 [frontier-roadmap.md](frontier-roadmap.md)，M3 之后的内部迭代排序见 [iteration-roadmap.md](iteration-roadmap.md)。
> **定位**：以 [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)（dsh）的 "Everything is a Plugin" 为架构参照，在 .NET 上**原生实现**一个插件化 Agent Harness SDK——借思想，不移植代码。
> **名字**：斗拱 = 标准化构件逐层装配、承托整体屋檐的千年木构系统——"一切皆插件"的建筑版。内核是承托层，插件是斗拱件。
> **上游决策**：底座决定（M.E.AI + MCP 官方 SDK、不自研 provider、不移植 Cordis）见 Obsidian《AI编程助手桌面端-方案C-Avalonia-DotNet.md》§3.2（2026-10-22 修订）。
> **配套桌面端**：Dougong Desktop（方案 C，Avalonia，独立仓库；程序集前缀 `DougongDesktop.*`，与 SDK 的 `Dougong.*` NuGet 前缀区分）是本 SDK 的第一个目标宿主，但本仓库不包含 UI。双轨协同开发计划见 §12。2026-10-22 统一命名，弃用旧名 DriftCode。

---

## 1. 定位与三条原则

```text
Dougong = 插件内核 + Agent 运行时 + 协议壳（ACP）
          全部站在微软官方基础件上，交付为 NuGet 包族
```

1. **借架构，不搬实现**。从 dsh 借三样东西：一切皆插件、运行时启停、作用域生命周期。**不移植 Cordis**——.NET 无对应物，且"时空可组合"的完整响应式语义超出 harness 所需，只保留其中最有价值的一条（依赖方随提供方重启，见 §4.3）。
2. **站在官方件上**。LLM 抽象 = `Microsoft.Extensions.AI`；MCP = 官方 `ModelContextProtocol` C# SDK；日志/配置/DI = `Microsoft.Extensions.*`；可观测 = OpenTelemetry。自研范围压缩到"内核 + 运行时 + ACP 壳"。
3. **SDK 优先，宿主示范**。交付物是 NuGet 包族 + 一个 headless CLI 参考宿主（`dougong` dotnet tool）。桌面 UI、IDE 集成由宿主项目（如 Dougong Desktop）承担。

## 2. 生态位对照

| 项目 | 是什么 | 与 Dougong 的边界 |
|---|---|---|
| dsh（TS） | 架构参照物，developer preview、高速演进 | 只借思想，**不追版本**；参照点冻结为其当前架构文档 |
| pi.net | Pi 核心库的 .NET 移植（Pi.Ai/Pi.Agent/Pi.Mcp） | Pi.Ai 可作 Ai 层备选底座（42 厂商/OAuth 场景）；无插件内核 |
| Microsoft.Extensions.AI | LLM 统一抽象（`IChatClient`） | 本项目的 **Ai 层底座**，不重复造 |
| ModelContextProtocol C# SDK | 微软官方 MCP 实现 | 本项目的 **Mcp 层底座** |
| Microsoft Agent Framework（含 HarnessAgent） | 提供运行循环、上下文、审批和中间件等公开能力；版本在 PoC 中固定 | 公开能力存在重叠，是否复用按 §2.1 验证；Dougong 聚焦运行时插件组合、统一工具管线与宿主集成 |
| Semantic Kernel / AutoGen 独立版 | 同上的旧形态 | 同上 |

一句话（2026-10-22 修正）：MAF HarnessAgent 已补上"**拥有一个 harness**"这层；Dougong 要补的是"**拥有一个可插件化的 harness 平台**"——运行时装卸的插件生态在 .NET 仍是空白（重叠分析与复用决策见 §2.1）。

### 2.1 基础件与 MAF 复用决策（2026-10-23 修订）

以下是官方文档层面的能力依据，不代表当前项目已引用、还原或验证任何版本。实施时记录 SDK、NuGet 版本、源码/文档参照及还原结果，再建立可复现基线；删除未经编译验证的精确版本、下载量和“覆盖一半”等比例判断。

- `Microsoft.Extensions.AI.Abstractions` 提供 `IChatClient` 等核心抽象；`Microsoft.Extensions.AI` 提供函数调用、缓存、遥测等组合。Agent 可依赖抽象，具体连接器由工厂和宿主选择。
- MAF `HarnessAgent` 可组合上下文、历史、压缩、Skills、工具审批等能力，可本地运行并接自定义 UI；不是只能用于云服务。Agent、Function 与 IChatClient 层的中间件提供候选扩展点。
- 默认 `InMemoryChatHistoryProvider` 不等于物理写盘。逐模型调用更新历史不能直接满足 Dougong 的提交屏障、断电恢复、未知副作用处理或 exactly-once。
- 官方资料没有直接证明 MAF 可以满足或不能满足本项目全部生命周期与调度要求；不再以“会和管线打架”为由预先排除，也不直接选为最终运行时。

**决定**：M0 内核验证保持独立；M1 正式实现前，用同一 `IAgentRuntime`/`IToolDispatcher` 契约比较两个最小候选：A = HarnessAgent + 自定义历史/中间件/工具包装；B = M.E.AI 原始模型客户端 + Dougong 调用循环。优先选择通过正确性门槛、维护成本较低的实现；两者都未通过时保持决策未定，不以赶进度降低门槛。需要 fork/依赖私有 API 时必须记录维护代价，不隐藏在适配层里。

| 共用验证用例 | 必须取得的证据 |
|---|---|
| 参数流式分片、同批多个工具 | 按调用 ID 聚合完整参数并校验后才派发，不能执行残缺参数 |
| 提交失败与崩溃窗口 | 输入/调用意图先提交；存储失败阻止执行，缺终态恢复为未知，不重复副作用 |
| 审批暂停、参数/工具变化与拒绝 | 同一 dispatcher 绑定并校验授权；无内置自动调用旁路 |
| 同文件并发、独占工具与结果顺序 | 资源锁有效，副作用冲突被协调，历史结果与请求对应 |
| 取消、预算耗尽、模型/工具错误 | 所有未执行调用有明确终态，不留下无法解释的 tool-call/result 关系 |
| 插件停用/替换与会话存活 | 调用租约排空、必需能力缺失暂停，框架缓存不阻止卸载 |
| 流式输出、Details/Content 与遥测 | 不因只写非流式中间件而退化；Details 不入模型历史；日志不污染 ACP stdout |

先使用可控制的假模型/假工具做确定性反例，再用一家真实 provider 完成相同编码任务；比较实现复杂度、依赖耦合、延迟/token 与故障诊断。记录通过/失败、版本、复现命令与选择理由；未运行前状态均为“待验证”。

MCP 官方 SDK 保持拟采用底座；Tasks 等可选扩展按实际版本能力评估，不预设能替代审批协议。`Dougong.Maf`、AgentHooks、A2A 等保持远期候选，不进入已确认依赖。

官方依据：[M.E.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)、[Harness](https://learn.microsoft.com/en-us/agent-framework/concepts/harness)、[会话](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session)、[审批](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval)、[中间件](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware/)。

## 3. 总体架构

```text
┌────────────────────────────────────────────────────────┐
│ 宿主层（不在本仓库）                                     │
│  Dougong Desktop(Avalonia) / Zed / 任意 ACP 客户端       │
└──────────────────────┬─────────────────────────────────┘
                       │ ACP stdio / 桌面内部可重连 JSON-RPC（§7）
┌──────────────────────▼─────────────────────────────────┐
│ Agent 运行时                 ← Dougong.Agent             │
│  agent loop · 会话(JSONL) · 工具管线 · 审批 · 压缩        │
├────────────────────────────────────────────────────────┤
│ Ai 层                       ← Dougong.Ai                 │
│  IChatClient 工厂/路由/用量记账（底座 M.E.AI）            │
├────────────────────────────────────────────────────────┤
│ Mcp 层                      ← Dougong.Mcp                │
│  MCP server 连接（底座官方 SDK；每个连接是一个插件）       │
├────────────────────────────────────────────────────────┤
│ 插件内核                     ← Dougong.Kernel             │
│  IPlugin/PluginContext · 服务注册表 · 依赖重启 · 事件总线  │
│  ALC 动态加载/隔离/可卸载                                 │
├────────────────────────────────────────────────────────┤
│ 横切：Dougong.Telemetry（OTel）· 配置 · 日志 · CLI 宿主   │
└────────────────────────────────────────────────────────┘
```

**目标包清单**（`net10.0`，MIT，`Dougong.*` 前缀；当前未实现，实际引用状态见 architecture.md §2）：

| 包 | 内容 | 目标依赖 |
|---|---|---|
| `Dougong.Kernel` | 生命周期、注册表、事件与加载 | BCL + Microsoft.Extensions 的 DI/Logging/Configuration.Abstractions |
| `Dougong.Agent` | 运行契约、工具 dispatcher、存储/审批与循环候选 | Kernel + Microsoft.Extensions.AI.Abstractions |
| `Dougong.Ai` | 客户端工厂、路由、用量与连接器配置 | Agent + Microsoft.Extensions.AI |
| `Dougong.Mcp` | MCP 连接与工具桥接 | Agent + 官方 MCP SDK |
| `Dougong.Acp` | 标准 ACP Agent 端适配 | Agent |
| `Dougong.Telemetry`（规划） | 事件到 Span/Metric；不进入 Kernel | Kernel + Agent + OTel API |
| `Dougong.Cli` | headless 参考宿主 | 组合上述已实现包 |

包图已修订，工程引用留待可编译的实现步骤同步：当前为六个产品工程，无产品外部包引用，Telemetry 尚未创建；不能把表中选型当作已还原或验证。

## 4. 插件内核（核心章节）

这是与 pi.net 拉开差异、也是整个项目最有原创工作量的部分。

### 4.1 概念模型

内核公开 API 是实验性草案，签名与行为依据统一维护在 [architecture.md §3](architecture.md#3-插件内核dougongkernel)。`IPlugin.ActivateAsync` 在新 `PluginContext` 中执行；context 提供受作用域管理的服务、事件、配置、日志和逆序清理登记。

静态 `RegisterAsync(factory)` 与动态 `LoadAsync(manifest)` 进入同一生命周期协调器。`ActivateAsync`、`DeactivateAsync` 与 `UnloadAsync` 分别表达激活、停止与卸载，不能混用。解析器向宿主提供调用租约；宿主不能永久缓存可替换插件对象。

**一切皆插件在代码里的含义**：CLI REPL、JSONL 存储、审批器、内置工具、MCP 连接与 Agent 运行实现均由内核承载。组合根解析 `IAgentRuntime`，存储/审批/模型客户端是必需能力；缺失或替换期间暂停工作，不自动降级到无审批或无存储。

### 4.2 生命周期状态机

分开保存用户期望启用状态、当前运行状态和激活代次。运行状态包含 Registered/Activating/Active/Deactivating/Stopped、必需依赖缺失的 `Blocked`、受控失败的 `Failed`，以及卸载请求/脱离状态；具体规则见 architecture.md §3.2、§3.5。

激活注册先暂存，成功后原子提交服务快照与状态；失败回滚并清理。停用先拒绝新调用并排空，再逆序幂等清理。操作取消、生命周期和清理预算分别控制；忽略取消或清理超时需诊断并要求宿主重启，不能重叠激活。受控异常不等于任意插件故障隔离。

### 4.3 服务注册表与依赖变化

服务键是 `(Type, Name?)`，保存服务数量、必需/可选、选择优先级等需求；包括当前没有提供方的需求。程序集兼容依赖与运行时服务依赖分开。M0 拒绝循环依赖，多提供方冲突不得静默覆盖。

提供方新增、替换或移除时，协调器串行处理相关闭包：先原子冻结并撤下整个受影响闭包的新解析/新调用，再逆拓扑排空和清理消费者及提供方，提交最终注册表版本并按期望启用状态重新评估需求；未满足必需需求者进入 `Blocked`，仅满足且期望启用者按正拓扑激活。提供方恢复后唤醒仍期望启用的消费者。并发字典不是生命周期事务；不得持内部锁调用插件代码。

### 4.4 事件总线

事件总线用于状态通知与投影，不承担存储提交、审批决定或执行授权。订阅归作用域管理，关闭时排空在途回调；顺序、背压、错误隔离和状态快照规则统一见 architecture.md §3.4。

### 4.5 动态加载与卸载

使用 collectible ALC 与 `AssemblyDependencyResolver`。共享白名单覆盖 Kernel、Agent 中跨边界契约以及签名依赖的抽象程序集；第三方服务使用稳定共享契约。宿主统一解析契约程序集实例，私有实现依赖仍可隔离版本，不能只共享 `IPlugin`。

停止保留加载代码，再激活使用新作用域；卸载还须释放所有外部引用、工厂、服务类型键、事件/任务/诊断缓存。工具结果使用共享数据，禁止长期持有插件自定义对象。ALC 卸载是协作式，不能强制中止代码或当作安全边界；第一版只加载可信插件。

标准清单、兼容范围和卸载失败协议见 architecture.md §3.5。M0 用真实服务/事件、失败路径和多轮装卸验证 WeakReference，不把测试中的强制 GC 用于正常运行。

### 4.6 版本与兼容

加载前检查所有共享契约的支持范围，不只检查 Kernel 主版本；M3 使用 ApiCompat 和行为兼容测试。M0 验收覆盖 architecture.md §3.7 的全部反例后，才进入 Agent 实现；公共 API 在 M3 前保持 experimental。

## 5. Agent 运行时

### 5.1 agent loop

执行实现待 §2.1 的对比验证后决定，统一对外提供 `IAgentRuntime`；具体调用时序见 architecture.md §4.1。两种候选均必须先提交输入，聚合完整模型工具参数并校验，再通过唯一 dispatcher 完成审批、提交、执行和终态记录。

自研候选禁止在模型客户端内自动执行工具；MAF 候选必须把框架工具包装到同一 dispatcher 并验证无旁路，不能叠加两个循环。流式参数未完成、schema 无效或响应中断时不执行；每会话首版只运行一个 turn。

### 5.2 会话与持久化

- **append-only JSONL 是真相源**：`~/.dougong/sessions/<workspace>/<ts>_<id>.jsonl`；分支 = 从分叉点复制出新文件（树的另一条路径，TKCode 模型）。
- **写盘屏障**：Agent 显式等待存储插件提交完整消息、审批决定和调用执行状态，再放行后续副作用；持久化状态通知在提交后发布，不依赖监听器订阅顺序。未提交文本块可实时渲染但须标为暂存，不能作为执行依据或恢复承诺。提交失败时停止后续执行。
- 恢复：重放已提交记录重建历史，支持 resume/branch/list；未提交流式文字不在恢复保证内，缺终态调用标为结果未知，不自动重放。存储需声明进程崩溃/断电持久化级别，并处理单写者和残缺尾记录；桌面恢复细则见 architecture.md §6.4。

### 5.3 工具体系

工具契约以 [architecture.md §4.3](architecture.md#43-工具体系) 为单一详细定义：描述符含稳定 ID、版本、说明和参数 JSON Schema；权限支持组合与未知状态；执行计划声明资源读写键；结果使用共享 JSON 数据，区分模型 Content 与 UI Details。

运行时冻结注册信息，校验/规范化参数并复制 JSON 数据。所有宿主、Skills、MCP 和框架适配器通过 `IToolDispatcher`，审批绑定调用、工具/插件代次、参数、工作区、资源版本、授权范围与有效期。自描述减少遗漏但不能证明工具诚实；未知权限不得自动放行，宿主保留逐工具覆盖配置。进程内可信插件仍有宿主权限。

### 5.4 执行管线与资源协调

未声明并发能力的工具默认工作区串行；shell、未知资源和会改变进程/环境状态的工具取工作区独占锁。首版只实现这两级，资源级读写键与跨工作区协调在出现真实冲突后再扩展。等待审批不长期占锁，执行前取得锁/租约并重校验；变化后重新审批。同批包含独占工具时首版整批串行，结果按请求顺序回填不等于副作用按序。

取消、超时、失败与结果未知分别记录；不合作调用不得提前释放锁/租约后重叠执行。M1 反例验收详见 architecture.md §4.4，包括同文件写冲突、跨会话、工具替换、路径别名、schema 校验和重复调用。

### 5.5 上下文压缩

上下文预算显式预留输出和估算余量，并计入工具 schema；旧触发公式仅作候选，不直接视为跨模型默认值。摘要从已提交历史生成，保留工具调用/结果配对，提交成功后才替换上下文；失败保留原历史，不能超预算继续请求。缓存命中和费用按 provider 实测，不能保证只为新增内容付费。详见 architecture.md §4.5。

## 6. 协议与集成层

### 6.1 Ai 层

- `ChatClientFactory`：按命名配置（provider、endpoint、model、key 来源）建 `IChatClient`；官方/社区 M.E.AI 连接器即插即用，DeepSeek 走 OpenAI 兼容端点。
- 用量/成本记账：per-session/per-day 聚合，走 OTel 指标 + 事件总线。
- 逃生舱：`Dougong.Ai.Pi`（适配 pi.net 的 Pi.Ai，42 厂商 + OAuth 场景），M4 按需再做。

### 6.2 Mcp 层

MCP 连接由插件管理并桥接到 Agent 工具契约。权限和资源计划按工具配置，annotations 仅作提示，未知权限不得自动放行；工具列表/schema 或来源变化使旧审批失效。断开不等于远端撤销，结果未知时停止自动重试；详见 architecture.md §5.2。

### 6.3 Acp 层

- 手写 ACP Agent 端，锁定协议版本并进行兼容测试。v1 基线：initialize、session/new、session/prompt、session/update、session/request_permission、session/cancel；`session/load` 需声明 `loadSession` 能力。v1 已稳定的 `session/list`、`session/close`、`session/resume`、取消与 elicitation 按兼容客户端支持情况逐步实现。
- 映射：审批流 ↔ permission request / elicitation；会话事件 ↔ session/update。标准入口为 UTF-8 逐行 JSON-RPC over stdio，stdout 独占协议，日志和遥测走 stderr 或独立出口。
- 外部 ACP 客户端可通过标准入口驱动 Dougong。ACP v2 目前为 draft，含 `replayFrom` 重放与恢复时重建待处理审批；定稿后桌面内部会话语义优先收敛到标准，私有层只保留进程启动/守护、认证、单实例和 PTY 所有权（§7）。

### 6.4 遥测

OTel：LLM 调用、工具执行、插件生命周期三类 Span + 用量 Metrics。交互宿主可用 console exporter；ACP stdio 模式日志与遥测改走 stderr 或独立出口，stdout 仅承载协议。

## 7. 宿主与进程模型（与 Dougong Desktop 的协同）

确定三种进程角色，桌面恢复与重连的完整契约以 [architecture.md §6](architecture.md#6-宿主与进程模型) 为准（2026-10-23 修订，设计已明确，尚未实现验证）：

```text
DougongDesktop.Desktop（Avalonia，可 NativeAOT）
    │ 本地命名管道：桌面内部 JSON-RPC + 附着/同步扩展
    ▼
DougongDesktop.AgentHost（JIT，直接内嵌 Dougong SDK）
    │ 独立控制管道
    ▼
DougongDesktop.PtyHost（按需启动，持有 PTY/shell 句柄）
```

- AgentHost 不再启动另一个 `dougong` 进程。Desktop 仓库交付 AgentHost/PtyHost，SDK 提供可复用的组合入口；UI 不引用 SDK，也不加载动态插件。
- 外部客户端连接 `dougong` CLI 时继续使用标准 ACP stdio；桌面内部使用可重连命名管道，复用 ACP 会话语义并增加附着/状态同步扩展，不把该入口当作标准 ACP 传输。
- Desktop 关闭只断开连接，AgentHost 与终端可继续存活；重开 UI 通过实例 ID、会话 ID、连接代次、状态快照和事件序号重新附着。单用户/配置目录只启动一个引擎，同一会话只允许一个可写控制连接。
- **存活重连**与**崩溃恢复**分别验收：前者重连仍运行的引擎；后者启动新引擎，只恢复已提交状态，不自动重放结果未知的工具调用，也不接管旧 PTY。
- 断线不批准工具；待审批与已批准但未派发的调用暂停并在重连后重新校验/审批，已开始的调用可按原授权在预算内完成。旧连接审批失效，不派发新调用或进入下一轮模型调用。
- PtyHost 拥有终端资源并提供有界输出、屏幕状态同步及输入去重；UI 退出不释放终端，AgentHost 崩溃则清理旧终端并标记中断。NativeAOT 发布、VT 状态恢复和进程树清理仍需专项验证。
- `Dougong.Cli` 是另一个内嵌 SDK 的 headless 宿主；CLI 与桌面通过存储写租约防止同时写入同一会话。

## 8. 里程碑与验收标准

| 阶段 | 内容 | 验收 | 估时 |
|---|---|---|---|
| **M0 内核 spike** | 生命周期协调器、需求图/租约、激活事务、跨 ALC 契约与协作式卸载 | architecture.md §3.7 全部反例及 §3.4 背压/错误/排空用例通过，不能只验收两个插件正常路径 | 原 1–2 周仅作历史估算，待 PoC 重估 |
| **M1 最小可用 agent** | 先完成 §2.1 MAF/自研对比，再实现统一 dispatcher、JSONL 提交、内置工具、控制台审批与 CLI | 真实编码任务；存储失败停止执行，崩溃恢复不重复副作用；权限/资源冲突/取消反例及 build/test/pack 验证通过；开始自用 | 原 3–5 周待重估，含框架选择成本 |
| **M2 协议与受限记忆** | ACP/MCP、OTel、Skill 最简 L1 候选/启用/撤销与最小评测 | 协议版本/能力联调、审批/取消、stdout 纯协议、MCP 工具变更与未知权限；记忆在留出任务对照评测，无未经解释的安全/隐私违规 | 原 2–3 周待重估 |
| **M3 插件 API 公开** | ApiCompat、行为兼容、作者模板/文档、依赖准备与加载诊断 | 第三方按文档完成可信插件并通过多轮卸载测试；API/行为/打包门槛全过才稳定化 | 原 4–6 周待重估 |
| **M4+（持续）** | out-of-process、其他模型适配、发现约定与完整评测优化器 | 按真实需求及独立验收推进；不是 M2 最小退化检查的前置 | — |

原总计 **3.5–4.5 个月**缺少产能和原型依据，不再作为交付承诺。按上述验证结果和单人可投入时间重新估算；桌面与 SDK 的两条工作线不等于人力并行。

> **2026-10-23 修订状态**：内核依赖/生命周期/ALC、工具权限/并发/包图、MAF 复用门槛、事件与 Skill 信任/评测，以及桌面恢复契约均已写入候选设计。这里只关闭“缺少设计方向”的问题；实现、版本兼容、故障注入和运行验收均未完成，不能宣称技术风险已消除。

## 9. 风险与规避

| 风险 | 规避 |
|---|---|
| ALC 卸载泄漏（事件句柄/静态/线程） | 卸载契约 + 测试模板 + CI WeakReference 门禁；兜底：宿主重启或 out-of-process 模式 |
| M.E.AI 连接器覆盖不齐 | 工厂只依赖 `IChatClient` 抽象；Pi.Ai 逃生舱（§6.1） |
| ACP 协议演进 | 锁协议版本 + 针对 TS SDK 的兼容测试集 |
| dsh 上游高速演进的诱惑 | 参照冻结在其当前架构文档；本项目的"对齐对象"是思想，不是 commit |
| 内核 API 过早公开被生态锁死 | M0–M2 API 全部标记 experimental；M3 才稳定化 |
| 单人项目巴士因子 | 设计文档即本文件；每里程碑附带可运行验收（CI 保活，学 pi.net） |
| **MAF Harness 能力扩张侵蚀生态位** | 分水岭守在"运行时插件内核 + ACP 壳 + 权限自描述"；每季度复核 §2.1 基线，可吸收则吸收 |

## 10. 明确不做

```text
1. UI 框架 / 桌面应用（Dougong Desktop 仓库的事）
2. LLM provider 矩阵自研（M.E.AI 的事）
3. Cordis 完整响应式语义（超出 harness 所需，§4.3）
4. OS 级沙箱（审批 + 工作区边界，不宣传"沙箱"）
5. 插件市场基建（NuGet 用于分发，扫描只负责发现；激活仍需依赖准备、兼容及信任检查）
6. 多 Agent 编排（Microsoft Agent Framework 的事）
7. dsh 的 Web UI / 桌面端复刻
```

## 11. 命名与待定决策

### 11.1 名称：Dougong（斗拱）——2026-10-22 选定

- **隐喻**：斗拱是标准化木构件逐层装配、承托整体屋檐的承重系统——不用钉胶、靠标准接口咬合、可拆可换。"一切皆插件"的建筑版；内核是承托层，插件是斗拱件。
- **查证记录**（2026-10-22）：NuGet 搜 "dougong" 零结果；GitHub 仅有拼写不同的 Dugong（儒艮）等无关项目。落选者：Totem（NuGet 已有 [Totem 事件溯源框架](https://www.nuget.org/packages?q=totem) 全家桶）、Tenon（NuGet 已有 Tenon.* DDD 框架系列）、Sunmao（"榫卯=组件拼装"故事已被 [sunmao-ui](https://github.com/smartxworks/sunmao-ui) 使用）。
- 本机目录已迁至 `C:\work\myProject\dougong`（2026-10-22 自历史名 dsh.net 迁出）；仓库名与包前缀（`Dougong.*`）即刻生效。

### 11.2 路线 C（全量移植 dsh）的处置——2026-10-22

**触发条件**：dsh 发布正式版（脱离 developer preview、API 稳定）。触发后**先评估，不直接开工**：

1. 对照 dsh 稳定架构与 Dougong 现状，把值得吸收的设计**并入 Dougong**（更新架构参照，成本周级）——这是大概率结局；
2. 仅当出现"必须与 dsh 行为/插件 100% 兼容"的真实需求（如原样运行 dsh 的 JS 插件生态）时，才立项独立移植仓库（人年级、团队规模），立项前再决策一次。

结论：**不预建目录、不预留代码位置**。若立项，是 `C:\work\myProject` 下的兄弟仓库，与本项目互不嵌套。

### 11.3 其余待定决策与当前默认值

| 项 | 默认 | 备注 |
|---|---|---|
| TFM | `net10.0` | SDK 需支持该目标和 `.slnx`，实施时检测并固定可复现版本；不把 SDK 11 当唯一要求。当前无 11 独有 API 依赖，升目标或多目标在 M3 按需求评估 |
| 序列化 | System.Text.Json | AgentHost 是 JIT，AOT 压力只在宿主侧 |
| 日志/配置/DI | Microsoft.Extensions.* | 不引第三方 |
| 许可 | MIT | 与 dsh/pi.net 一致 |
| 仓库结构 | `src/`（六包）+ `samples/`（每里程碑一个可运行样例）+ `docs/` | 学 pi.net：样例由 CI 编译保活 |

## 12. 桌面端协同开发计划（Dougong Desktop 双轨）

桌面端是独立仓库（**Dougong Desktop**，方案 C：Avalonia + TKCode.Arc + ACP Client；程序集前缀 `DougongDesktop.*`——2026-10-22 统一命名，弃用旧名 DriftCode），组件选型、MVP 边界、风险表以 Obsidian《AI编程助手桌面端-方案C-Avalonia-DotNet.md》为准。本节只定**双轨协同路线**：两轨各自做什么、在哪个节点会合、互相之间如何做到不阻塞。2026-10-22 编制。

> 与总案的关系：本节按"Dougong 的配套桌面 = 方案 C（Avalonia 轨）"编制；Obsidian 总案中"方案 A（Electron+React）为主线"的定位是否随之调整，由用户择期裁定，本节不替它做决定。

### 12.1 双轨总览

| 阶段 | Dougong 轨（SDK） | 桌面端轨（Dougong Desktop，并行） | 会合点 |
|---|---|---|---|
| **P0**（原估算第 1–6 周） | M0 内核 spike → M1 最小可用 agent | Avalonia/TKCode.Arc 验证 + 三进程角色骨架 + 命名管道启动/认证 PoC | 正常会话可先用外部 ACP Agent 联调；内部附着与同步用专用测试宿主验证 |
| **P1**（原估算第 7–9 周） | M2：Dougong.Acp / Mcp / 遥测 | 桌面正常会话 MVP：消息/流式渲染、工具时间线、文件树、审批、CodeBlock、Windows 打包 | 分别通过外部 ACP stdio 和自有 AgentHost 管道驱动会话；重连能力尚不算验收完成 |
| **P2**（原估算第 10–13 周，需重估） | M3：插件 API 稳定化 | 存活重连、引擎崩溃恢复、审批连接失效、PTY 重连/清理、有界队列、AOT/打包验证 | architecture.md §6.7 故障用例全部通过后，才宣称支持恢复与重连 |
| **P3**（持续） | M4+ | 插件管理 UI（浏览/启停/配置，对应 M3 插件 API）、多会话管理 | 生态闭环：桌面端成为插件生态的第一现场 |

### 12.2 协同规则

1. **会话交互复用 ACP 语义，桌面生命周期显式扩展**。UI 不引用 `Dougong.*`；`DougongDesktop.AgentHost` 直接引用 SDK。外部 Agent 使用标准 ACP stdio，自有引擎使用内部命名管道及附着/同步扩展；客户端共用消息投影，按能力启用重连功能，不假设外部 Agent 支持内部扩展。
2. **桌面正常会话不等待 Dougong 完成**：ACP Client 可先与外部 Agent 联调；后台存活、审批重发、终端重连等能力必须使用自有 AgentHost 或专用测试宿主验证，不能凭外部 Agent 会话通过就认定完成。
3. **SDK 正常运行不等待桌面完成**：headless CLI 自证引擎可用；桌面内部 IPC 不进入 Kernel，SDK 提供会话、提交、取消与工具执行状态契约。
4. **宿主职责固定**：SDK 仓库交付 NuGet 包和 CLI 参考宿主；Desktop 仓库交付直接内嵌 SDK 的 AgentHost 与 PtyHost，不用包装 CLI 的方式叠加进程。两种宿主复用 SDK 的组合入口。
5. **审批维度分离**：`ToolAccess` 是 None/Read/Write/Execute/Network/External 六类权限；桌面审批交互状态另建类型。P1 对齐正常审批，P2 按 architecture.md §6.5 验证断线、过期、取消和重启语义。
6. **完成状态以故障验收为准**：§7 和 architecture.md §6 是本轮桌面设计基线；实现/测试未完成，不再以“已有三进程”推导“恢复重连已解决”。§12.1 表中的周数仅保留为原估算，需随实际验证和单人产能重估。

### 12.3 关键依赖与验收（桌面侧摘要，详表见方案 C）

- 组件风险前置：TKCode.Arc 为 `0.3.0-preview`，P0 的组件验证必须覆盖方案 C §2.3 列出的 Agent 刚需组件（含流式 Markdown 的 `Append` 压测），不合格项在 P0 内决定替代方案。
- P1 验收：正常消息、流式、工具与审批分别在外部 ACP Agent 和自有 AgentHost 上通过；自有入口还需通过启动认证与版本检查。
- P2 验收：architecture.md §6.7 全部故障用例通过；分别证明 UI 存活重连、AgentHost 崩溃恢复和 PTY 退出策略。断电持久性按存储声明独立验证，不能用杀进程结果代替；打包产物在干净 Windows 机器验证。
