# Dougong 文档索引

> 新会话从这里进入。先读 [status.md](status.md) 了解当前进度，再按需深入。

## 归类规则

```text
根目录   = 描述当前系统：契约、决策、状态。改动会约束实现，代码注释直接引用这些路径。
vision/  = 方向与理念：前瞻调查、远期路线、设计哲学。是思考材料，不是已实现能力的承诺。
journey/ = 开发记录：各里程碑真实发生的问题与判断。复盘素材，不是验收依据；按里程碑命名（m0、m1…）。
```

判断标准：**如果一份文档的内容变了，代码要不要跟着改？** 要，就放根目录；不要，按性质进 `vision/`（面向未来）或 `journey/`（记录过去）。

## 当前系统（根目录）

| 文档 | 内容 | 谁在读 |
|---|---|---|
| [architecture.md](architecture.md) | 系统长什么样、怎么运转：分层、内核契约、状态机、动态加载、宿主边界 | 实现者；多处代码注释引用其章节号 |
| [design.md](design.md) | 为什么这样设计：生态位、里程碑、MAF 复用决策、桌面端双轨计划 | 决策与评审 |
| [status.md](status.md) | 现在到哪了：已实现、测试基线、未完成验收、恢复入口 | **新会话第一入口** |
| [m1-candidate-comparison.md](m1-candidate-comparison.md) | M1 候选对比：MAF 包装 vs 自研循环的同用例对比与选择理由 | M1 实现者；Agent 代码注释引用。M1 关闭后移入 `journey/` |

## 方向与理念（vision/）

| 文档 | 内容 |
|---|---|
| [philosophy.md](vision/philosophy.md) | 斗拱隐喻、设计原则、明确不做的事情 |
| [frontier-roadmap.md](vision/frontier-roadmap.md) | Agent Harness、协议、隔离、记忆评测的外部公开进展调查 |
| [iteration-roadmap.md](vision/iteration-roadmap.md) | M3 之后的内部迭代排序与退出条件 |
| [skills-evolution.md](vision/skills-evolution.md) | Skill 复盘、沉淀、审批与加载的进化设想 |

## 开发记录（journey/）

| 文档 | 内容 |
|---|---|
| [m0-dev-journey.md](journey/m0-dev-journey.md) | M0 期间真实发生的问题与判断错误，供开发记录梳理 |
| `m1-dev-journey.md` | M1 关闭后按同一格式整理 |

## 阅读路径

```text
第一次接触   status.md → architecture.md §1–3 → design.md §1–2
实现某特性   architecture.md 对应章节 → design.md 相关联动 → 测试基线（status.md）
评估方向     vision/frontier-roadmap.md → vision/iteration-roadmap.md
复盘教训     journey/m0-dev-journey.md（实现同类能力前先看）
```

> `vision/` 与 `journey/` 的内容不随实现状态自动同步；引用其中结论时先对照 [status.md](status.md) 核实是否仍然成立。
