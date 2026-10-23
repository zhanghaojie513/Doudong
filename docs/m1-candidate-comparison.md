# M1 候选对比：MAF 包装 vs 自研循环

> 依据 [design.md §2.1](design.md#21-基础件与-maf-复用决策2026-10-23-修订) 的决策门槛执行。
> 状态：**两个候选在同一反例集上行为等价，选择依据是代码量、调用次数与未验证 API 依赖。**
> 复现命令与实测输出见文末。

## 1. 候选定义

| | 候选 A | 候选 B |
|---|---|---|
| 名称 | MAF 包装 | 自研循环 |
| 模型调用 | `Microsoft.Agents.AI.ChatClientAgent` | `IChatClient` 直接驱动 |
| 版本 | Microsoft.Agents.AI 1.26.0 | Microsoft.Extensions.AI.Abstractions 10.11.0 |
| 工具执行 | 同一 `IToolDispatcher` | 同一 `IToolDispatcher` |
| 代码量 | 195 行（`MafAgentRuntime.cs`） | 213 行（`LoopAgentRuntime.cs`） |
| 依赖 | 额外 1 个包（MAF），传递引入 M.E.AI 10.10.x 后被提升到 10.11.0 | 仅 M.E.AI 抽象 |

**共同前提**：两个候选都**不向模型层传工具**。工具 schema 以文本注入系统提示，函数调用由运行时解析后交给 dispatcher。这样比较的是循环本身，而不是"框架自动执行工具"的能力。

这个设计决定本身来自一条实测发现：MAF 1.26.0 中关闭函数旁路所需的
`ChatClientAgentOptions.EnableInvocableFunctionBypassing` 带有 `ExperimentalAttribute`，
编译时会产生 `MAAI001` 诊断（"仅用于评估，将来可能更改或删除"）。
因此本条路径不适合作为生产依赖。

## 2. 反例集与结果

两者都是 **10/10 通过**：

| # | 场景 | 候选 A | 候选 B | 说明 |
|---|---|---|---|---|
| 1 | 参数分片聚合 | PASS | PASS | 同一 ID 多次更新，按键合并后才派发 |
| 2 | 同批多工具 | PASS | PASS | 两个调用都有终态 |
| 3 | 重复调用 ID（键冲突） | PASS | PASS | 不执行，`InvalidModelResponse` |
| 4 | 缺失调用 ID | PASS | PASS | 不执行 |
| 5 | 畸形 JSON | PASS | PASS | 拒绝，有终态 |
| 6 | 参数不符 schema | PASS | PASS | 拒绝，有终态 |
| 7 | 响应中途中断 | PASS | PASS | 已收到的调用不执行 |
| 8 | 工具抛异常 | PASS | PASS | 记为失败终态 |
| 9 | 流式文本不退化 | PASS | PASS | 两次 `TextDelta` |
| 10 | Details 不进历史 | PASS | PASS | 正常结束 |

**结论：行为的最终结果等价。** 单看通过率无法区分。

## 3. 真正的差异：过程与代价

通过率相同，但轨迹不同。以下为实测输出。

**候选 A**

```text
[fragmented-arguments] modelCalls=3 dispatching=1
  RunStarted → Text(reading ) → Text(done) → Call(c1,read) → Done(c1,Succeeded) → Finished(Failed)
[multiple-tools] modelCalls=3 dispatching=2
  RunStarted → Text(done) → Call(c1,read) → Done(c1,Succeeded) → Call(c2,read) → Done(c2,Succeeded) → Finished(Failed)
[tool-throws] modelCalls=3 dispatching=1
  RunStarted → Text(done) → Call(c1,boom) → Done(c1,Failed) → Finished(Failed)
```

**候选 B**

```text
[fragmented-arguments] modelCalls=2 dispatching=1
  RunStarted → Text(reading ) → Call(c1,read) → Done(c1,Succeeded) → Text(done) → Finished(Completed)
[multiple-tools] modelCalls=2 dispatching=2
  RunStarted → Call(c1,read) → Done(c1,Succeeded) → Call(c2,read) → Done(c2,Succeeded) → Text(done) → Finished(Completed)
[tool-throws] modelCalls=2 dispatching=1
  RunStarted → Call(c1,boom) → Done(c1,Failed) → Text(done) → Finished(Completed)
```

### 3.1 候选 A 每场景多一次模型调用

`modelCalls=3` 对 `modelCalls=2`。原因是 MAF 在收到工具结果后会自行发起下一轮，
而本包装层又在自己的循环里发起了一次，两次重叠。真实 provider 下这意味着**双倍 token 成本**。

### 3.2 候选 A 的事件顺序错误

`Text(done)` 出现在 `Call(c1,...)` **之前**。`done` 是第二轮模型响应，逻辑上必须在工具执行之后。
候选 A 把它排到了工具调用前面，说明 MAF 内部的历史处理与包装层的循环边界不一致。

这不是显示问题：如果宿主用事件流做 UI 投影或写盘屏障，会得到错误的时间线。

### 3.3 候选 A 的终止原因不准确

工具成功、调用全部完成，候选 A 却报 `Finished(Failed)`；候选 B 报 `Finished(Completed)`。
包装层与 MAF 的异常/状态语义没有完全对齐，需要额外的映射代码，而这类映射正是"适配层隐藏维护代价"的典型。

## 4. 依赖与维护代价

| 维度 | 候选 A | 候选 B |
|---|---|---|
| 直接包依赖 | MAF 1.26.0 | 无（仅抽象） |
| 版本约束冲突 | MAF 钉 M.E.AI 10.10.0/10.10.1，需显式声明 10.11.0 覆盖 | 无冲突 |
| 未验证 API | `EnableInvocableFunctionBypassing` 带 `ExperimentalAttribute` | 无 |
| 隐式行为 | 自动续跑、历史注入、内部状态机 | 全部显式 |
| 调试入口 | 需理解 MAF 内部循环 | 本仓库自有代码 |

## 5. 决策

**选择候选 B（自研循环）。**

理由按权重排序：

1. **候选 A 有可复现的语义缺陷**：事件顺序错误、终止原因错误、每次多一轮模型调用。这些不是配置问题，而是包装层与框架循环边界不一致的必然结果。
2. **关闭旁路依赖实验性 API**：`EnableInvocableFunctionBypassing` 标注为仅够评估用，将来可能删除。项目不能把"无旁路"这个安全属性建立在这类 API 上。
3. **候选 B 的代码量与候选 A 相当**（213 行对 195 行），没有为了可控性付出显著成本。
4. **候选 B 的每一处行为都在本仓库可读代码里**，符合本项目"可观察行为由契约定义"的原则。

**不选择的理由不包含"MAF 会和管线打架"**——这是设计文档明确禁止的预设排除。候选 A 是在实测之后因具体缺陷被淘汰的。

## 6. 尚未验证的部分（必须如实记录）

- **真实 provider 未运行**。`DEEPSEEK_API_KEY` 未在本次执行环境中提供，因此两个候选都只有假模型结果。design.md §2.1 要求"再用一家真实 provider 完成相同编码任务"，这一项状态为 **待验证**。
- **未实现审批与资源锁**。当前 dispatcher 只做 schema 最小校验、调用 ID 去重、权限描述采集和终态记录。审批绑定、工作区独占、执行前文件版本复核均未实现，因此**不能宣称具备"批准 A 执行 B"的防护**。
- **JSON schema 校验是最小子集**（顶层 `type` 与 `required`），不是完整校验器。
- **未测持久化与提交屏障**，属于后续切片。

## 7. 复现方式

```bash
dotnet build spike/Dougong.Agent.Comparison -c Release

# 候选 A
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- a

# 候选 B
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- b
```

两候选共享同一 `ScriptedChatClient`、同一 `FakeTool`、同一 `ContractHarness`，
因此差异只可能来自运行时实现，不会来自测试基础设施。

## 8. 结论对后续的含义

- `Dougong.Agent` 的契约（`IAgentRuntime`/`IToolDispatcher`/`IAgentTool`）保持不变，两种实现都能满足。
- 正式实现走候选 B；`spike/` 目录下的一次性对比工程保留作为回归基线，M1 完成稳定后可删除。
- 下一步应在自研循环上补齐：审批、资源锁、JSONL 提交屏障、上下文压缩。
