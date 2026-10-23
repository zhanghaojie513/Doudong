# Dougong 前沿调查与产品演进方向

> **范围**：本文讨论 **M3 完成、产品可用之后** Dougong 往哪里走。它不是当前设计修订的验收报告，也不把已有实现当作前提。
> **时点**：2026-10-23。以下"现状"来自当日可访问的官方文档、规范与仓库；标为"待观察"的部分尚未定论。公开资料会变化，引用前应重新核对版本。
> **方法限制**：部分官方页面未标注发布日期；NuGet 版本号取自 nuget.org API；仓库活跃度取自 GitHub API。未能访问的来源已在正文标注，未以推测填补。

---

## 1. 一句话结论

**Harness 本身正在商品化，"能跑 agent 循环 + 有工具 + 有 Skills"不再是差异化。Dougong 的未来不在"再做一个 harness"，而在三件事：.NET 生态里可嵌入且可组合的运行时、跨宿主的稳定引擎、以及可审计的知识与执行治理。**

如果 M3 之后仍继续加 agent 功能，会和 MAF、Claude Agent SDK、Codex、dsh、Hermes 正面重叠，而 Dougong 在人力与生态上都不占优。相反，"被别的 .NET 系统当作组件嵌入"、"同一引擎服务 CLI/桌面/编辑器"、"技能与记忆有来源、审批、回滚、审计"这三点，目前没有强势占位者。

---

## 2. 当前格局：哪些已经变成基础设施

| 能力 | 现状（2026-10-23） | 对 Dougong 的含义 |
|---|---|---|
| Agent 循环与工具调用 | MAF `HarnessAgent` 已发布，含函数调用、逐次调用历史、压缩、todo、Plan/Execute、file memory、standing/auto 审批、OTel、Web 搜索、Skills、后台子 agent、shell、有界循环 | 这些不能再作为卖点；自研仅在有明确语义差异时保留 |
| 模型接入 | M.E.AI `IChatClient` 与大量连接器；MAF 覆盖多家 provider | 不做 provider 矩阵，这条已确认 |
| 编辑器 ↔ Agent 协议 | ACP v1 已稳定，v2 为 draft；官方 SDK 覆盖 Kotlin/Java/Python/Rust/TS（Rust/TS 已达 1.0）；已有 ACP Registry 用于发现和安装 agent | "能被编辑器驱动"会变成门槛而非优势；要尽早进入 Registry |
| 会话恢复 | ACP v2 已定义 `session/resume`，默认不重放历史、可 `replayFrom` 全量重放；明确不保证持久化每个消息、消息 ID 跨重启可能不稳定 | 桌面私有 IPC 中存在被标准协议替代的空间；自研部分应聚焦标准未覆盖处 |
| 长任务与人在环 | MCP 2026-07-28 规范外的 Tasks 扩展：持久 handle、轮询、`input_required`、协作式取消 | 可复用于长工具与审批等待；不必全部自研 |
| 技能格式 | Agent Skills 已成为开放格式（Anthropic 发起，agentskills.io），`SKILL.md` + 渐进披露（元数据约 100 token → 正文 <5000 → 资源按需）；`allowed-tools` 仍为 experimental | 格式不必自创；差异应在治理与执行约束 |
| 技能与记忆的自动演进 | Hermes Agent 已内置经验沉淀、技能自改进、会话检索、用户建模；其离线进化器用 DSPy+GEPA，Phase 1（技能）已实现，Phase 2–5 计划中，门禁含测试、体积、缓存兼容、语义保持、人工 PR 审查 | "自动进化"概念不再新颖；可竞争的是**可度量、可审计、可回滚** |
| 执行隔离 | Claude Code 使用 macOS Seatbelt / Linux bubblewrap，明确写"降低风险但不是完整隔离边界"；**原生 Windows 无沙箱**；网络需经代理白名单 | 自建通用沙箱不现实；Windows 是真实空白，也是风险点 |
| 评测 | SWE-bench（含 Verified 500、Lite 300、多语言 300 等）与 Terminal-Bench 4.0（榜单含成本与 token）已成为公共标尺；Harbor 提供评测框架 | 优化必须有公共或自建基准，否则"变好"不可证 |

关键判断：**MAF 已经把"harness 该有什么"定义完并且免费提供**；dsh 用插件化 + Web UI 占据"可插拔体验"；Hermes 用自改进 + 多平台网关占据"个人助理"；Codex/Claude Code 占据"编码 CLI 与 IDE"。Dougong 不能在这四条正面主线上硬碰。

---

## 3. 可防守的四个位置

### 3.1 .NET 应用内嵌（最优先）

.NET 企业系统需要把 agent 嵌进现有服务：已有 DI、配置、日志、鉴权、后台任务、gRPC/ASP.NET 管道。目前没有"专为被嵌入设计"的 .NET agent 运行时：MAF 更偏构建独立 agent 应用，Claude Agent SDK 仅 Python/TS 且需宿主自行托管进程。

Dougong 的机会是成为 **agent 运行时里的 Serilog/ASP.NET 角色**：一个 NuGet 包，宿主决定模型、存储、审批、工具与生命周期，内核不抢控制权。

**产品形态**：`Dougong.Hosting`（通用宿主适配）、`Dougong.Hosting.AspNetCore`（后台任务 + SSE/WebSocket 推送）、`Dougong.Hosting.Worker`（定时/队列触发）。这些比再做一个 CLI 更稀缺。

**验证方式**：一个真实 .NET 服务在**不改动其部署模型**的前提下嵌入 Dougong，完成一次带审批的工具任务，并能在其现有日志/指标中观测。

### 3.2 跨宿主的一致性（第二优先）

同一引擎被 CLI、桌面、编辑器同时驱动，且行为一致：同一会话在不同宿主看到相同历史、审批语义、工具结果与错误。ACP 已把"连接"标准化，但没有标准化"引擎质量"。

差异点应放在 **标准未覆盖处**：多会话并发与配额、工作区隔离、审批在断线与重启后的确定性、工具执行的可观测性、失败诊断。

**触发条件**：ACP v2 稳定（结束 draft）后，桌面内部协议优先收敛到 `session/resume` + `replayFrom`；只对标准缺口保留私有扩展（进程启动、认证、PTY）。**不要在 v2 定稿前把私有协议做成公开 API。**

### 3.3 知识与执行的治理（第三优先，也是与 MAF 的真正分界）

MAF 能加载 Skills，但不提供：来源追溯、版本与内容哈希、审批绑定、作用域、撤销、审计、跨项目污染控制。Hermes 有自动沉淀，但其公开材料强调的是"自我改进"，不是组织级治理。

Dougong 应把 Skills/Memory 做成**可治理资产**：

- 每条记忆与技能有来源（哪次任务、哪些证据）、作用域（用户/项目/组织）、版本与内容哈希；
- 启用与执行分离，脚本永远走统一工具管线；
- 撤销影响新 turn，明确不影响已发生副作用；
- 对外可导出审计记录，对内可回答"这条规则从哪来、谁批准、影响过什么"。

这在团队/企业场景比"自动变聪明"更有购买理由，也更可验证。

### 3.4 Windows 与 .NET 工具链的执行边界（机会与风险并存）

Claude Code 明确不支持原生 Windows 沙箱；Codex 的隔离以 macOS/Linux 为主。Windows 上大量 .NET 开发者与 CI 环境缺少"受限执行"选项。

可选路径：Job Object（进程树约束）、AppContainer / Windows Sandbox、WSL2 转发、容器后端。**不要承诺安全沙箱**——官方文档自己都说"降低风险而非完整边界"。定位应是"可配置的执行约束与审计"，并按后端分别声明保证级别。

**风险**：Windows 隔离实现复杂、易被绕过、维护成本高。建议先做**执行后端抽象 + 容器/Docker 后端**，Windows 原生后端作为后续实验，不阻塞主线。

---

## 4. 分阶段演进路线

各阶段以"上一阶段有真实用户/真实嵌入"为触发，不以日期触发。每阶段给出退出条件，避免无限扩张。

### 阶段 A：产品化收口（M3 之后立即）

目标：让 M3 的能力**能被别人用起来**，而不是只在自己机器上跑。

- 安装与升级：`dotnet tool`、单文件发布、版本检查、配置迁移；
- 诊断：启动自检、日志分级、失败可复现信息包；
- 性能与规模：大会话、长会话、并发会话的实测数据；
- 文档：插件作者指南、嵌入指南、故障排查；
- ACP Registry 上架，声明支持的能力与版本。

**退出条件**：一个外部开发者仅凭文档完成"安装 → 配置模型 → 跑通一次任务 → 写一个工具插件"，全程无需提问。

### 阶段 B：嵌入与宿主生态

目标：从"一个 CLI"变成"一个可被嵌入的运行时"。

- 通用宿主抽象 + ASP.NET Core / Worker 适配；
- 多会话并发、配额、取消、优雅停机；
- 宿主级观测（OpenTelemetry 语义约定、成本与延迟）；
- 明确"库模式"与"进程模式"两种集成路径及各自保证。

**退出条件**：至少一个真实 .NET 服务在生产形态下嵌入，任务失败可定位到具体组件。

### 阶段 C：执行边界（受控扩展）

目标：让"能改文件、能跑命令"在真实项目里可控。

- 执行后端抽象：本地 → 容器 → SSH/远程；
- 按工作区配置策略，越界需显式批准；
- 每个后端声明其保证级别（进程约束、文件系统、网络），不混用"沙箱"一词；
- 失败与逃逸路径的检测与告警。

**退出条件**：同一工具在本地与容器后端行为一致（除明确声明的差异），且网络受限场景有可验证结果。

### 阶段 D：知识资产化

目标：让 Skills/Memory 成为可管理资产。

- 兼容 agentskills.io 格式，可导入导出；
- 版本、来源、作用域、审批、撤销、审计查询；
- 组织级策略：哪些技能允许自动启用、哪些必须人工批准；
- 关注 MCP "Skills over MCP" 工作组，具备互操作能力后再决定是否作为分发通道。

**退出条件**：能回答"某条规则从哪来、谁批准、影响过哪些会话、如何撤销"，并可在留出任务上证明无退化。

### 阶段 E：可度量进化

目标：把"自我提升"从叙事变成可复现实验。

- 基准适配层：让同一引擎可跑 SWE-bench / Terminal-Bench / 自建任务集；
- 从会话日志构建评测集，但**与生成材料分离**，避免自我强化；
- 离线优化（GEPA 类方法）先作用于技能文本，再考虑提示与工具描述；
- 门禁：测试、体积、语义保持、成本、安全、人工审查；
- 结果公开：版本、样本、指标、失败案例。

**退出条件**：一次优化在独立留出集上可复现地不劣于基线，且失败案例可解释。

### 阶段 F：互操作而非编排

目标：不造多 agent 平台，而是成为可被编排的节点。

- MAF 适配（把 Dougong 会话暴露为 `AIAgent`，进入其 workflow）；
- A2A / MCP Tasks 等按实际采用度接入；
- 明确边界：编排、托管、企业 agent 服务交给 MAF/Azure，Dougong 提供引擎与治理。

**退出条件**：在 MAF workflow 中作为子 agent 正常运行，且取消、错误、审批语义不丢失。

### 阶段 G：产品面扩张（按需）

- Dougong Desktop 成为主产品，CLI 为自动化入口；
- 远程/无头托管（服务器上常驻引擎，桌面/移动连接）；
- 团队特性（共享技能库、审批策略、审计报表）——这是最可能形成商业价值的方向，但也是与开源定位需要权衡的方向。

**触发**：只有在 A–D 有稳定外部用户后才考虑；否则容易做成半成品。

---

## 5. 明确不做（M3 之后仍然成立）

1. **不做通用 OS 沙箱**。平台差异大、维护成本高，官方实现自己也声明不是完整边界。做执行后端与策略。
2. **不做 provider 矩阵**。M.E.AI 与连接器生态已足够。
3. **不做 agent 编排平台**。MAF 已占据，且与 Dougong 定位冲突。
4. **不自建技能格式**。agentskills.io 已是事实标准。
5. **不做插件市场基建**。分发用 NuGet/Registry，发现与信任另做但不建交易平台。
6. **不与 dsh 插件生态比数量**。目标应是 .NET 特有系统（MSBuild、Azure、SQL Server、企业内网工具），而非通用 JS 插件平替。
7. **不把"自动进化"作为默认开启**。默认候选、显式启用、可撤销。

---

## 6. 需要持续观察的触发点

| 观察项 | 触发后动作 | 当前状态 |
|---|---|---|
| ACP v2 结束 draft | 桌面内部协议收敛到 `session/resume` + `replayFrom`，删减私有扩展 | v2 为 draft；v1 稳定 |
| ACP Registry 采用度上升 | 上架并维护能力声明；把"可被编辑器发现"当作分发渠道 | Registry 已存在 |
| MCP Tasks 被主流客户端支持 | 长工具与审批等待改用 Tasks 语义，减少自研状态机 | 扩展存在，host 支持不一 |
| Skills over MCP 定稿 | 技能可从 MCP 发现与分发，本地目录变为一种来源 | 工作组阶段 |
| MAF Harness 继续吸收治理能力 | 重新评估 Dougong 的治理层是否仍有差异；必要时改为适配而非竞争 | 当前无来源/审批/撤销治理 |
| 公共基准饱和 | 自建贴近真实 .NET 项目的任务集，公开方法与失败案例 | SWE-bench/Terminal-Bench 仍为主流 |
| Windows 隔离方案成熟 | 评估原生后端，否则维持容器/WSL2 路径 | 原生 Windows 无成熟方案 |

---

## 7. 主要风险

1. **平台陷阱**：先做"平台"再做"产品"，结果是没人用。应始终以"能被嵌入/能被日常使用"为验收。
2. **标准追赶**：ACP/MCP/Skills 演进快，私有协议与格式投入可能被标准替代。原则是"标准优先，私有只补缺口"。
3. **治理过度**：审批、来源、审计如果让日常使用变慢，用户会关掉。默认体验必须顺滑，严格模式可选。
4. **单人产能**：阶段 A–G 不可能全部完成。建议 A、B、D 为必做，C、E 为差异化投资，F、G 视外部信号。
5. **评测自欺**：用自己生成的会话做评测会高估效果。必须保留独立任务集与负面样例。

---

## 8. 对当前设计的回看（仅作边界说明）

本文不评价具体修订条目，但调查结果对设计有两点直接影响，供后续修订参考：

- 桌面私有 IPC 的部分内容（附着、恢复、审批重建）与 ACP v2 方向重合，**应在 v2 稳定后重新评估**，避免长期维护两套语义；
- MCP Tasks 已覆盖"长任务 + 中途输入 + 断线恢复"，**审批等待与长工具执行可优先复用**，而非完全自研。

这两点不改变"当前没有实现、不能宣称已解决"的判断。

---

## 9. 来源

访问日期均为 2026-10-23。

- Microsoft Agent Framework — Agent Harness：https://learn.microsoft.com/en-us/agent-framework/concepts/harness
- Microsoft Agent Framework — Overview：https://learn.microsoft.com/en-us/agent-framework/overview/
- ACP — Overview（v1）：https://agentclientprotocol.com/protocol/overview
- ACP — v2 Session Setup（draft）：https://agentclientprotocol.com/protocol/v2/session-setup.md
- ACP — 文档索引（版本、稳定特性、SDK、Registry）：https://agentclientprotocol.com/llms.txt
- MCP — Specification（2026-07-28）：https://modelcontextprotocol.io/specification/latest
- MCP — Tasks 扩展：https://modelcontextprotocol.io/extensions/tasks/overview
- Agent Skills — Overview / Specification：https://agentskills.io/ 、https://agentskills.io/specification
- Claude Code — Sandboxing：https://code.claude.com/docs/en/sandboxing
- Claude Agent SDK — Overview：https://code.claude.com/docs/en/agent-sdk/overview
- DeepSeek Harness — README：https://github.com/deepseek-ai/deepseek-harness
- Hermes Agent — README：https://github.com/NousResearch/hermes-agent
- Hermes Agent Self-Evolution — README：https://github.com/NousResearch/hermes-agent-self-evolution
- SWE-bench：https://www.swebench.com/
- Terminal-Bench：https://www.tbench.ai/
- NuGet 版本：https://api.nuget.org/v3-flatcontainer/{package}/index.json（M.E.AI 10.11.0、MAF 1.25.0、Harness 1.25.0、MCP 2.3.0）

未能访问：Codex 安全文档（403）、dsh 架构目录（超时）、SWE-bench/Terminal-Bench GitHub 仓库（超时）。相关判断以上述可访问来源为限。
