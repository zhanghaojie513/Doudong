# Dougong（斗拱）

> 给 .NET 生态一个 [dsh](https://github.com/deepseek-ai/deepseek-harness) 这样的东西：**一切皆插件**的 Agent Harness SDK。

**斗拱（Dougong）**：中国木构建筑中由标准化构件逐层装配、承托整体屋檐的千年结构——不用钉子与胶水，靠标准接口层层咬合，可拆可换可增减。这正是 "Everything is a Plugin" 的建筑版本，也是本项目的架构隐喻：内核是承托层，插件是斗拱构件。

**非 DeepSeek 官方项目**——以 dsh（DeepSeek Harness）的 "Everything is a Plugin" 架构为参照的独立 .NET 实现，借思想，不移植代码。

## 状态

候选设计已按 2026-10-23 技术评审修订，公共 API 仍处于实验阶段。当前仓库为 **6 个产品工程 + 1 个测试工程**；`Dougong.Telemetry` 仍在规划中，CLI 仍是占位宿主。M0 正在实现：内核已具备生命周期协调、依赖阻塞/恢复与激活顺序、可选服务需求、依赖环诊断、取消与清理回滚、按激活代次暂存并原子提交服务、代次绑定调用租约、逆拓扑清理与排空超时，以及有界异步事件投递和带修订号的状态快照/增量订阅。Release 构建和 55 项内核测试已通过，M0 总体验收仍未完成。详见 [实施状态板](docs/status.md)。

## 文档导航

| 文档 | 回答什么 |
|---|---|
| [docs/philosophy.md](docs/philosophy.md) | 设计理念——斗拱隐喻、七条理念、反目标、成功标准 |
| [docs/architecture.md](docs/architecture.md) | 架构——包结构、插件内核、Agent 运行时、协议层、进程模型、关键时序 |
| [docs/design.md](docs/design.md) | 总体方案——生态位、里程碑（M0–M3）、桌面端双轨计划（§12）、命名与决策 |
| [docs/skills-evolution.md](docs/skills-evolution.md) | 设计笔记——Skill 自动进化：复盘 → 沉淀 → 审批 → 加载的个人适配闭环 |
| [docs/status.md](docs/status.md) | 实施状态板——设计修订与实现进度，开发从这恢复上下文 |
| [docs/frontier-roadmap.md](docs/frontier-roadmap.md) | 前沿调查——外部事实：框架、协议、隔离、评测走到哪一步 |
| [docs/iteration-roadmap.md](docs/iteration-roadmap.md) | 未来迭代方向——M3 之后的内部排序、退出条件、触发式工作 |

## 是什么 / 不是什么

以下是设计范围，尚非现有功能清单：

| 是 | 不是 |
|---|---|
| 插件内核（作用域、依赖满足、ALC 协作式卸载） | dsh 的移植或进程内安全沙箱 |
| Agent 运行时（agent loop、JSONL 会话、权限自描述工具、审批、压缩） | LLM provider 矩阵（底座是 Microsoft.Extensions.AI） |
| ACP Agent 壳 + headless CLI 参考宿主 | 桌面 UI / IDE（那是宿主项目的事，如配套的 Dougong Desktop） |
| MCP 集成（官方 C# SDK，每个 server 连接即插件） | 多 Agent 编排（那是 Microsoft Agent Framework 的事） |

## 包清单（规划）

| 包 | 内容 |
|---|---|
| `Dougong.Kernel` | 插件内核：IPlugin / PluginContext / 依赖重启 / 事件总线 / ALC 动态加载 |
| `Dougong.Agent` | agent loop、append-only JSONL 会话、工具管线、审批流、上下文压缩 |
| `Dougong.Ai` | IChatClient 工厂与模型路由、用量记账（底座 Microsoft.Extensions.AI） |
| `Dougong.Mcp` | MCP server ↔ 插件桥接（底座官方 ModelContextProtocol C# SDK） |
| `Dougong.Acp` | 标准 ACP stdio 适配，按已实现协议版本和能力与兼容客户端联调 |
| `Dougong.Telemetry` | OTel 桥接：内核/运行时事件 → Span 与用量指标（内核零 OTel 依赖） |
| `Dougong.Cli` | `dougong` headless 参考宿主（dotnet tool） |

## 路线

M0 内核与生命周期反例验证 → M1 运行实现对比选择及最小可用 agent → M2 ACP + MCP + 遥测及受限 L1 记忆 → M3 插件 API 稳定化。桌面恢复/重连契约与待验收故障用例见 [architecture.md §6](docs/architecture.md#6-宿主与进程模型)，详细门槛见 [design.md §8](docs/design.md#8-里程碑与验收标准)。

## Credits

Dougong 站在许多优秀工作的肩膀上（**思想借鉴，非代码移植**）：

- **[deepseek-harness (dsh)](https://github.com/deepseek-ai/deepseek-harness)** — "Everything is a Plugin" 架构与技能承载方式的参照
- **[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) / [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) / [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)** — 框架参考与拟采用的模型/协议基础件；MAF 运行实现复用待 PoC，当前尚未接入产品包引用
- **[Hermes Agent](https://github.com/NousResearch/hermes-agent)** — Skill 自动进化闭环的灵感来源（复盘 → 沉淀 → 适配个人）
- **[TKCode](https://github.com/AtomUICommunity/TKCode)** — 权限自描述、工具并发分级、持久化写盘屏障三条核心机制的设计来源

> 本项目为独立开源项目，与上述任何项目均无隶属关系；与 DeepSeek/dsh 的关系见顶部声明。

## License

[MIT](LICENSE) © 2026 Haojie Zhang (张浩洁)
