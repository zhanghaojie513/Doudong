# 开发计划（防跑偏用）

> **这是什么**：唯一一份排期性文档。它回答"下一步做什么、为什么是这个顺序"。
> **这不是什么**：不是目标（见 [goal.md](goal.md)）、不是完成定义（见 [quality-gates.md](quality-gates.md)）、
> 不是设计（见 [architecture.md](architecture.md) / [design.md](design.md)）、
> 不是台账（见 [status.md](status.md)）。
>
> **文档闭环**：
>
> ```text
> goal.md          为什么做、什么算达成   ← 判定标准
>   ↓ 每项工作都要对应一条性质
> plan.md          按什么顺序做、还差什么 ← 本文
>   ↓ 每片交付都要过门
> quality-gates.md 六道门与红线
>   ↓ 结果如实记录
> status.md        实际做到哪一步（附证据）
>   ↓ 过程中的判断错误
> journey/         复盘素材，反哺门的加严
> ```
>
> **维护规则**：每完成一项更新本文标记；发现新缺口先登记 §5，并标注它影响
> [goal.md](goal.md) 的哪条性质。本文与 status.md 冲突时，以实测证据为准并回来修正本文。

## 零、本文每项的书写要求

每一项必须写清三件事，缺一不可：

1. **对应哪条性质**（P1 可组合 / P2 生命周期 / P3 故障可解释 / P4 真实可用）
2. **失败证据是什么**（门 1 要求；写不出说明还没想清问题）
3. **从哪个入口触达**（门 3 要求；写不出说明会重演 G1）

答不上第 1 条的，先问"它推进了什么"；答不上第 3 条的，不要开始。

---

## 一、当前真实位置（2026-10-24 核准）

| 里程碑 | 状态 | 证据 |
|---|---|---|
| M0 内核 | **已完成** | 157 项内核测试、13 项独立探测、M0 验收项全部勾选 |
| M1 入口与执行契约 | **主体完成，有 2 个真缺口** | 10 套自检 85 项、真实 provider 6 端点验证、见 §2 |
| M2 协议与集成 | **未开始** | `Dougong.Mcp`、`Dougong.Acp` 为空工程 |
| M3 生态与稳定 | **未开始** | — |

**核准方式**：不采信文档描述，逐个 grep 代码确认。M1 清单里勾选的项都验证过对应实现存在；
未勾选的项验证过确实缺失。下面 §2 的两个缺口是这次核准发现的，其中第 1 项
status.md 的记录不够准确（写了"未实现真实模型摘要"，但没写"未接入循环"）。

---

## 二、M1 剩余缺口（优先于 M2）

### G1. 上下文压缩从未接入生产路径（阻断级）— **已修复**

**事实**：`IContextCompactor`、`SlidingWindowCompactor`、`SummarizingCompactionTask`、
`CompactionApply` 全部实现并有 9 项自检通过。但：

- `LoopAgentRuntime` 的构造函数只有 `(client, dispatcher, store?, ...)`，**没有压缩器参数**
- CLI 的 `messages` 列表只增不减（`Program.cs:167` 起）
- 全仓库除测试外，没有任何代码调用 `Plan()` / `AdvanceAsync()` / `TryApply()`

**后果**：长会话把完整历史一路送进 `history`（`LoopAgentRuntime.cs:41-43`），
直到撞上 provider 的上下文上限才失败。压缩能力只存在于测试里。

**为什么是阻断级**：M1 的验收目标是"能真实跑编码任务"。短任务能过，长任务必挂，
而失败形式是 provider 报上下文超限——使用者会以为是模型限制，不是产品缺陷。

**修法方向**：
1. `LoopAgentRuntime` 接受可选 `IContextCompactor` + 预算参数，在每轮构造 `history` 前
   检查并压缩
2. 用真实 `UsageDetails.InputTokenCount`（已由 `UsageRecordingChatClient` 采集）校准
   那个"2 字符/token"的粗略估算——这是唯一能把估算变成实测的通道
3. 压缩失败必须保留原历史并继续（不覆盖、不中断），预算仍不足时明确暂停

**验收**：探针先证明"长会话确实会撞上限"，再验证压缩后不撞；压缩失败的降级路径有反证。

**完成情况（2026-10-24）**：
- 探针先确认缺口：10 200 tokens 的完整历史全部送进模型，构造参数里既无预算也无压缩器
- 接入：`LoopAgentRuntime` 新增可选 `ContextLimits` + `IContextCompactor` + `summarize`；
  每轮调用模型前检查一次；压缩只处理进入循环前的基线历史，循环内追加的交互不参与
  （它们必须成对保留）
- 降级：摘要失败、无摘要器、预算不可行、应用失败——四种情形都保留原历史并继续运行，
  并各自发出带诊断的 `ContextCompacted` 事件
- 真实端点验证（DeepSeek）：12 轮长会话，估算从 9 418 压缩到 3 445 tokens，19 次调用 0 失败
- 反证：移除接入点后 4 项检查立刻失败
- **G8（token 估算非真实计数）同时缓解**：`UsageRecord.InputTokens` 已可对照校准，
  但换算因子尚未回填为默认值，仍按 2 字符/token 保守估算

### G2. 审批决定没有持久化（中）— **已修复**

**事实**：`StoreRecordKind.ApprovalDecision` 枚举值存在，但全仓库使用点 **0 处**。
`RunFinished` 同样 0 处。

**后果**：恢复会话时无法回答"这个调用当初经过审批了吗"。`OutcomeUnknown` 对账
只知调用了、不知授权情况，对账信息缺一半。

**修法方向**：`ToolDispatcher` 取得审批决定后提交一条 `ApprovalDecision` 记录；
`RunFinished` 在每次运行终止时提交。两者都要在既有四个提交屏障的框架内。

**验收**：崩溃恢复后能列出"哪些调用有审批记录、哪些没有"。

**完成情况（2026-10-24）**：
- `ToolDispatcher` 新增可选 `IAgentStore`；审批后立即提交 `ApprovalDecision`，
  记录绑定 invocationId / 工具身份与版本 / 参数摘要 / 工作区 / 决定时刻
- 提交失败阻止执行并返回 `Failed`：否则"已批准"未被持久化而副作用已发生
- 新增 `ApprovalLog` + `ApprovalSnapshot`：`IsUnaccounted` 让"有调用但无审批记录"
  可表达，不与"被拒绝"混淆
- **G3 一并修复**：`RunFinished` 收口到 `FinishAsync` 单一出口（原先七处 `yield break`
  逐处插入容易漏），提交失败不改变已发生的事实，只并入诊断
- 7 项自检（`-- approval-log`，含两个反证）；真实端点日志确认六段记录齐全：
  UserInput → ModelResponse → ToolCallStarted → ApprovalDecision → ToolCallCompleted → RunFinished

---

## 三、后续里程碑

### M2 协议与集成（G1/G2 之后）

按价值排序，不按文档章节顺序：

| 顺序 | 事项 | 为什么是这个位置 |
|---|---|---|
| 2.1 | **MCP 工具桥接**（P4，兼 P1）— **已完成** | 见 `status.md` M2 段；`-- mcp` 8 项 |
| 2.2 | MCP 变化检测（P3）— **已完成** | `-- mcp-reload` 4 项；含反证 4/4 |
| 2.3 | 指标导出（P1/P4）— **已完成** | `-- telemetry` 5 项；BCL Meter，不直接依赖 OTel 包 |
| 2.4 | ACP Agent 端 stdio（P1/P4）— **已完成（含审批）** | `-- acp` 13 项 + 真实进程验证；load/resume/list/close 未实现（显式报错） |

**MCP 优先于 ACP 的理由**：MCP 扩展"能做什么"，ACP 扩展"怎么接入"。后者依赖前者有内容可暴露。

**MCP 的硬约束**（来自架构 §5.2，不可简化）：
- server 的 `annotations` 仅作提示，**不是**可信授权依据
- 未知工具默认不能自动批准，不以整个 server 的低风险默认值覆盖破坏性工具
- 连接断开或取消后不假定远端操作已撤销，缺结果调用按 `OutcomeUnknown` 处理

### M3 生态与稳定

按 `status.md` 的 M2/M3 验收入口执行，届时再细化。M3 之前不启动
`vision/iteration-roadmap.md` 里的内容。

---

## 四、贯穿全程的纪律

这些不是"某阶段的任务"，是每一片都要遵守的：

1. **先证伪再动手**。改之前先写一个能失败的探针。没有失败证据的改动，写完也不知道
   是否解决了问题。G1 尤其如此——"压缩接进去了"和"长会话不再失败"是两件事。
2. **反证必须重建后再跑**。本项目已因此浪费过至少四次排查：`spike` 不在 solution 里，
   全量构建不重建它，`--no-build` 跑的是旧 DLL。
3. **测试全绿不等于验收**。M0/M1 各有实例：假模型全绿但 4 个协议缺陷；
   兜底逻辑掩盖主路径死亡。
4. **真实 provider 是协议层的唯一裁判**。假模型只能验证控制流。
5. **文档与实测不符时改文档**。G1 的记录不准确就是教训。
6. **不宣称未验证的能力**。`status.md` 每一项必须附证据位置。
7. **破坏性 API 变更先给方案**。评审不等于修改授权。

---

## 五、缺口登记簿

发现即登记，不要在对话里丢失。修完标记并注明提交哈希。

| ID | 缺口 | 性质 | 严重度 | 状态 | 证据 / 触发条件 |
|---|---|---|---|---|---|
| G1 | 上下文压缩未接入循环 | P4 | 阻断 | **已修** | `-- wiring` 9 项；真实端点 9418→3445 tokens |
| G2 | 审批决定未持久化 | P3 | 中 | **已修** | `-- approval-log` 7 项；真实日志含 ApprovalDecision |
| G3 | `RunFinished` 未持久化 | P3 | 低 | **已修** | 统一收口 `FinishAsync` |
| G4 | 完整 JSON Schema 校验 | P2 | 中 | **已修** | `-- schema` 8 项；触发条件已在 M2.1 满足（schema 来自不可信远端） |
| G5 | 资源级读写键 | P2 | 低 | 待定 | **触发条件**：出现真实并发读冲突，且有可复现用例 |
| G6 | 工具替换语义未测 | P3 | 低 | **已修** | `-- tool-replacement` 4 项；反证确认版本不随 schema 变化时检查失败 |
| G7 | 断电级持久化 | P3 | 低 | 设计限制 | 只声明 `SurvivesProcessCrash`；需要独立持久化证明，不在正常路径解决 |
| G8 | token 估算非真实计数 | P4 | 中 | **已修** | `-- token` 7 项；实测校准（ASCII 8 字符/token，CJK 1.2），改用分段模型 |
| G9 | 同目录多会话并行 | P1 | 低 | 待定 | **触发条件**：桌面宿主需要多会话 |
| G10 | 工具幂等键 | P3 | 低 | 设计缺口 | `OutcomeUnknown` 后无法按契约核实。**触发条件**：接入有幂等语义的远端工具 |

**"触发条件"取代"什么时候修"**：这些缺口都不阻断当前目标，等真实需求出现再引入，
不预先冻结格式——与 architecture.md §4.4 的立场一致。写下触发条件是为了将来
能判定"现在该做了"，而不是靠感觉。

**已修的 G4/G6/G8 说明**（2026-10-24）：三者的触发条件都已由 M2 满足——
M2.1 接入 MCP 让 schema 成为不可信输入（G4），
M2.2 的热更新让"批准了 A、执行了 B"成为现实风险（G6），
真实 usage 已采集到足够样本可校准估算（G8）。
**这验证了"触发条件"这种写法的价值**：不是拖延，而是有判据地决定何时动手。

**剩余 G5/G7/G9/G10**：仍不阻断当前目标。G7 是设计限制（需要独立持久化证明），
其余三项等真实需求。

---

## 六、下一个具体动作

**M2 的 2.1/2.2/2.3 与 2.4 的最小闭环均已完成。**

**前置调查已完成**：[acp-reference-survey.md](acp-reference-survey.md)。三个关键结论：

1. **方向相反**：TKCode 做 ACP **Client**（驱动子进程），我们要做 ACP **Agent**（被驱动）。
   可借鉴协议形状与真实怪癖，不可借鉴进程管理与审批 UI。
2. **pi.net 没有 ACP**：它的 `Pi.Protocol` 是自研 CBOR 协议，`Pi.Mcp` 是 MCP。
   M2.4 无法从它借鉴，这一点必须如实记录以免误传。
3. **首版范围**：只做能跑通一轮对话的最小闭环（initialize / session/new /
   session/prompt / session/cancel / 5 种 session/update / request_permission），
   其余声明不支持而不是"实现了但走不通"。

这一项的必答：

1. **性质**：P1（可组合）+ P4（真实可用）——外部 ACP 客户端能驱动一个会话
2. **失败证据**：先确认当前没有任何 ACP 实现（`Dougong.Acp` 是空工程），
   且 `architecture.md §5.3` 的消息映射表一条都没实现
3. **入口**：需要一个 ACP stdio 宿主模式（`dougong acp` 或环境变量开关）；
   检查项必须验证"从该入口进入后，外部客户端发 `session/new` + `session/prompt`
   能拿到 `session/update` 流"

**硬约束**：
- stdio 模式的 **stdout 只能有协议帧**——日志必须走 stderr 或独立出口，
  混进 stdout 会直接破坏协议（architecture.md §5.4）
- ACP 无官方 C# SDK，需手写并锁定协议版本 + 独立兼容测试
- `session/load` 不表示接管旧进程或恢复旧连接，两者语义必须分开

这一项动手前的三项必答（按 §零 的要求）：

1. **性质**：P4（真实可用，扩展工具箱）兼 P1（可组合，新能力不改内核）
2. **失败证据**：先写探针验证 `ModelContextProtocol` 2.3.0 的实际 API 形态——
   尤其是工具 schema 与调用结果的表达能力能否无损映射到 `IAgentTool`。
   不能按文档假设，M1 的四个协议缺陷全来自"我以为它是这样"
3. **入口**：CLI 通过环境变量启用 MCP server（沿用 `DOUGONG_SHELL` 的模式，默认关闭）；
   检查项必须验证"从该入口进入后工具真的出现在 `dispatcher.Tools` 里"

另需注意：MCP 工具的 schema 来自远端，属于**不可信输入**，G4（完整 schema 校验）
的触发条件在这里成立——桥接时必须把 schema 当作需要校验的输入，而不是直接信任。

## 已完成动作（留档）

**G1**，分三步，每步都要有失败证据：

1. **写探针证明缺口**：构造一段足够长的历史，验证 `LoopAgentRuntime` 把全部历史送进
   `history`、没有任何裁剪。这一步可能发现比"没接压缩"更具体的问题
   （例如历史里有失效的 `tool_call_id` 配对）。
2. **接入压缩**：`LoopAgentRuntime` 增加可选的 `IContextCompactor` 与预算参数；
   在每轮构造 `history` 前 `Plan()`，需要时推进 `SummarizingCompactionTask`，
   `CompactionApply.TryApply()` 成功才替换。
3. **用真实 usage 校准**：把 `UsageRecord.InputTokens` 与估算值对照，
   得出实际比例；换算因子作为可配置参数，默认值保守。

**不做**：不重构 `LoopAgentRuntime` 的既有提交屏障顺序；不改 `IAgentRuntime` 的公开签名
（压缩器走可选构造参数）；不为压缩引入新的存储记录类型（摘要替换是内存态，
原日志保留）。
