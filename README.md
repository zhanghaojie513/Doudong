# Dougong（斗拱）

<p align="center">
  <strong>Everything is a Plugin · .NET Agent Harness SDK</strong>
</p>

<p align="center">
  用可组合的插件内核，承托 Agent 运行时、工具、协议与宿主。
</p>

<p align="center">
  <a href="https://github.com/zhanghaojie513/Doudong"><img src="https://img.shields.io/badge/repository-Doudong-2f6f5e?style=flat-square" alt="Repository"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square" alt=".NET 10"></a>
  <a href="https://github.com/zhanghaojie513/Doudong/releases"><img src="https://img.shields.io/badge/version-0.1.0--alpha.1-orange?style=flat-square" alt="Version 0.1.0-alpha.1"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1f883d?style=flat-square" alt="MIT License"></a>
</p>

> **Dougong** 借鉴 DeepSeek Harness（dsh）的 “Everything is a Plugin” 思想，为 .NET 生态构建独立的 Agent Harness SDK。项目借鉴架构思想，不移植代码，也不是 DeepSeek 官方项目。

斗拱是中国木构建筑中由标准化构件逐层装配、共同承托屋檐的结构。Dougong 采用同样的隐喻：内核负责承托和协调，插件负责提供能力；组件可以组合、替换、停用和重新加载。

## 当前状态

Dougong 目前处于 **实验性 Alpha 阶段**，目标框架为 `net10.0`。

`Dougong.Kernel` 已经完成一轮生命周期、服务、事件和动态加载实现，并拥有 148 项内核行为测试。两轮 M0 审核确认的阻断问题已修复：失败或排空超时的代次不能被再次激活或卸载，激活、取消、排空、事件与清理各有独立预算且清理超时会停止后续清理，裸解析只对本次激活回调的异步流开放，`ResolveAll` 返回全部提供方，清理顺序按实际依赖边计算，普通退订立即释放事件引用，状态增量在锁内原子提交并在内核销毁时结束，观察者诊断只保留共享字符串，快照不再携带插件清单因而不会阻止动态插件回收，并补充了真实共享契约的跨 ALC 验收。M0 仍需最后一次确认后才关闭，暂不进入 M1 正式实现。

当前仓库包含：

- 6 个产品工程：`Kernel`、`Agent`、`Ai`、`Mcp`、`Acp`、`Cli`
- 1 个内核测试工程
- 3 个样例工程：基础插件、契约插件、共享契约
- `Dougong.Telemetry` 尚未创建
- `Dougong.Agent`、`Dougong.Ai`、`Dougong.Mcp`、`Dougong.Acp` 仍处于骨架阶段
- `Dougong.Cli` 仍是占位宿主，尚不能直接运行 AI 编程任务

## 为什么是 Dougong

Dougong 关注的是 Agent 的**承托层**，而不是某一个模型或某一种 UI：

- 插件拥有清晰的生命周期和作用域
- 服务通过代次绑定和调用租约进行协作
- 依赖缺失、提供方替换和停用都进入统一协调器
- 动态插件使用可回收的 AssemblyLoadContext
- Agent、MCP、ACP、Skill 和宿主都可以作为插件组合
- 模型供应商通过 `Microsoft.Extensions.AI` 接入，不把 provider 矩阵写进内核

## 核心架构

```text
┌──────────────────────────────────────────────────────┐
│                    Host / Desktop                    │
│              CLI · Avalonia · ACP Client             │
└──────────────────────────┬───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│ Dougong.Agent                                         │
│ Agent loop · 会话 · 工具管线 · 审批 · 上下文压缩      │
├──────────────────────────────────────────────────────┤
│ Dougong.Ai · Dougong.Mcp · Dougong.Acp                │
│ 模型路由 · MCP 桥接 · ACP 适配                         │
├──────────────────────────────────────────────────────┤
│ Dougong.Kernel                                        │
│ 生命周期 · 服务注册 · 依赖图 · 事件 · 动态加载         │
└──────────────────────────────────────────────────────┘
```

### `Dougong.Kernel` 的设计重点

| 能力 | 说明 |
|---|---|
| 生命周期 | `Registered`、`Activating`、`Active`、`Deactivating`、`Stopped`、`Blocked`、`Failed` 状态与统一协调器 |
| 服务协作 | `(Type, Name?)` 服务键、可选需求、显式 `ProviderId`、优先级选择和激活事务 |
| 调用租约 | `IResolver` 返回代次绑定的服务引用，调用结束后释放在途计数 |
| 停用排空 | 关闭新调用、取消插件寿命、等待在途调用与已接受事件，再执行清理 |
| 事件总线 | 类型化订阅、独立有界队列、异步处理、退订即释放引用和溢出诊断 |
| 状态视图 | 锁内原子提交的修订号增量、快照、游标续订、登记/移除投影和重同步信号 |
| 动态加载 | collectible `AssemblyLoadContext`、显式共享契约、版本检查和协作式卸载 |
| 外部引用治理 | 快照与句柄只含共享数据，完整清单显式获取 |

> 当前实现仍是可信进程内插件模型。ALC 提供依赖隔离和协作式卸载，不提供操作系统级权限沙箱或故障隔离。

## 包结构

| 包 | 当前定位 | 状态 |
|---|---|---|
| `Dougong.Kernel` | 生命周期、服务、事件、依赖图、动态加载 | 核心实现，M0 审核中 |
| `Dougong.Agent` | Agent 运行契约、循环、会话、工具和审批 | 骨架 |
| `Dougong.Ai` | `IChatClient` 工厂、模型路由和用量记账 | 骨架 |
| `Dougong.Mcp` | MCP Server 与 Agent 工具桥接 | 骨架 |
| `Dougong.Acp` | ACP Agent 端适配 | 骨架 |
| `Dougong.Cli` | headless 参考宿主 | 占位实现 |
| `Dougong.Telemetry` | OpenTelemetry 桥接 | 规划中 |

## 快速开始

### 环境要求

- .NET SDK `10.0.402`
- Windows、Linux 或 macOS
- Git

### 构建和测试

```bash
git clone https://github.com/zhanghaojie513/Doudong.git
cd Doudong

dotnet restore Dougong.slnx
dotnet build Dougong.slnx -c Release
dotnet test Dougong.slnx -c Release --no-build
```

### 打包

```bash
dotnet pack Dougong.slnx -c Release --output ./artifacts/packages
```

当前 API 仍处于实验阶段。使用 `Dougong.Kernel` 前，请先阅读状态板和架构文档，并以实际 API 与测试结果为准。

## 文档

| 文档 | 内容 |
|---|---|
| [架构](docs/architecture.md) | 分层结构、内核契约、状态机、动态加载和宿主边界 |
| [总体设计](docs/design.md) | 生态位、里程碑、MAF 与自研循环的比较门槛 |
| [实施状态](docs/status.md) | 已实现内容、测试基线、未完成验收和恢复入口 |
| [设计理念](docs/philosophy.md) | 斗拱隐喻、设计原则和明确不做的事情 |
| [前沿调查](docs/frontier-roadmap.md) | Agent Harness、协议、隔离和记忆评测的公开进展 |
| [未来路线](docs/iteration-roadmap.md) | M3 之后的演进方向和退出条件 |
| [Skill 演进](docs/skills-evolution.md) | 复盘、沉淀、审批和加载的 Skill 进化设想 |

## 路线图

```text
M0  插件内核与生命周期反例验证   ← 当前阶段
 │
 ▼
M1  Agent 运行实现比较与最小可用 Agent
 │
 ▼
M2  ACP · MCP · 遥测 · 受限记忆
 │
 ▼
M3  插件 API 稳定化与生态工具
```

M1 不会以“先写一个能跑的循环”为验收标准。计划使用同一组确定性假模型、假工具和故障用例，对比 `MAF HarnessAgent` 与基于 `IChatClient` 的自研循环，再决定最终实现路径。

## 设计边界

Dougong 当前**不是**：

- dsh 的移植或兼容运行时
- 进程内安全沙箱
- LLM provider 矩阵
- 桌面 UI 或 IDE
- 多 Agent 编排框架
- 已完成的 AI 编程助手

配套桌面端将作为独立的 Dougong Desktop 宿主项目开发；本仓库负责 SDK、内核和参考 CLI。

## 贡献

项目尚处于 API 快速演进期。提交 Issue 或 Pull Request 前，建议先阅读 [实施状态](docs/status.md) 和 [架构文档](docs/architecture.md)，并为行为变化补充确定性的失败路径测试。

## 致谢与参考

Dougong 是独立开源项目，以下项目提供了公开思想或技术参考：

- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) — “Everything is a Plugin” 架构参照
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) — Agent 运行能力参考
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) — 模型抽象与连接器基础
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — MCP 协议基础
- [Hermes Agent](https://github.com/NousResearch/hermes-agent) — Skill 自动进化设想的参考
- [TKCode](https://github.com/AtomUICommunity/TKCode) — 权限自描述、并发分级和写盘屏障的设计参考

## License

[MIT](LICENSE) © 2026 Haojie Zhang（张浩洁）
