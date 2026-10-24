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

这个设计决定本身来自两条实测发现（2026-10-24 补充 Harness 复查后仍成立）：

1. `ChatClientAgentOptions.EnableInvocableFunctionBypassing` 带有 `ExperimentalAttribute`，
   编译时产生 `MAAI001` 诊断（"仅用于评估，将来可能更改或删除"）。
2. **Harness 复查**：`HarnessAgent` 位于独立包 `Microsoft.Agents.AI.Harness 1.26.0`
   （初次对比时探测的是 `Microsoft.Agents.AI`，遗漏了该包）。`HarnessAgentOptions`
   提供**非实验性**开关，但实测两个开关都无法关闭自动执行——
   `DisableToolAutoApproval = true` 的语义是"跳过审批直接执行"，设置后工具仍被框架执行
   （`toolExecuted=1`，流中出现 `FunctionResultContent`）；
   `DisableApprovalNotRequiredFunctionBypassing = true` 后行为完全相同。
   MAF 自带的审批体系（`ToolApprovalAgent`）执行点仍在框架内部，不经过宿主 dispatcher，
   按 design.md §2.1 "无法证明旁路已关闭的候选不能通过验证"判定不通过。

Harness 内置的压缩、Todo、技能等能力全部依赖框架内部工具执行，与"唯一 dispatcher"
的硬约束冲突；不传工具使用 Harness 则只剩一个模型调用包装，而其压缩策略本身
（`CompactionStrategy`）仍带 `ExperimentalAttribute`。

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

> 注：这张表是**假模型**结果。真实 provider 下的候选 B 另见第 6 节，两家模型各 5/5。
> 候选 A 未跑真实 provider——它在假模型阶段已因旁路与事件顺序缺陷淘汰。

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

### 3.4 以上均为假模型结果

本节 3.1–3.3 的轨迹全部来自 `ScriptedChatClient`，用于比较两个**循环**的控制流差异。
它不能证明候选 B 在真实协议下正确——真实 provider 的验证另见第 6 节，那里发现了
三个假模型完全测不出的缺陷。两节结论需合起来读：**3.x 决定选哪个循环，第 6 节决定循环之外还缺什么。**

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

## 6. 真实 provider 验证（2026-10-24 完成）

design.md §2.1 要求的"再用一家真实 provider 完成相同编码任务"已完成，实际跑了**两家**。

| 模型 | 端点 | 结果 |
|---|---|---|
| `mimo-v2.6-flash`（Xiaomi MiMo） | `https://token-plan-cn.xiaomimimo.com/v1` | **5/5 PASS** |
| `MiniMax-M3.1-Flash`（MiniMax） | `https://api.minimax.chat/v1` | **5/5 PASS** |
| `deepseek-v4.1-flash`（途关 Waygate） | `https://www.haihao.xyz:8443/v1` | **5/5 PASS** |
| `GLM-5.3`（智谱 BigModel） | `https://open.bigmodel.cn/api/paas/v4` | **5/5 PASS** |
| `GLM-5.3-Flash`（智谱 BigModel） | `https://open.bigmodel.cn/api/paas/v4` | **5/5 PASS** |

未通过（账户状态问题，非代码缺陷）：

| 模型 | 端点 | 状态 |
|---|---|---|
| `gpt-6.1-sol` 等（Routin Plan） | `https://api.routin.ai/plan/v1` | 403 `plan_required`（"No active plan subscription"） |
| `GLM-5.3`（BigModel Coding Plan / Start Plan） | 同 bigmodel / `zcode.z.ai/.../zcode-plan` | 429 余额不足 / 401 未授权 |

五个模型全部走的同一个 `OpenAiCompatibleChatClient`，没有为某家写特例分支。

验证项（每项都是可观察行为，不是"跑通不报错"）：

1. `simple-tool-call` —— 工具调用经 dispatcher 执行，且执行次数与 dispatcher 报告一致（旁路检测）
2. `fragmented-args-real` —— 真实流式分片后参数仍完整，通过 required 校验
3. `unknown-tool-real` —— 模型被要求调用不存在的工具名时，凡**实际被执行**的调用，
   工具名必须是已注册的；同时校验 dispatcher 执行计数与工具自身计数一致
   （模型可能忽略指令改调已注册工具，因此不要求它产生特定行为）
4. `text-only-real` —— 纯文本请求不产生任何工具调用
5. `coding-task` —— 真实编码任务：模型写下 `answer.txt`，宿主校验文件内容含 "42"，越界路径被拒

稳定性：两家模型各连续运行 3 轮，均 `ALL PASS`。

> **协议变体的价值**：五个端点里，Waygate 是唯一暴露"空字符串当缺失"这一族的。
> 如果只测一两家，这类兼容问题会一直潜伏到用户现场。

> **一条测试设计教训**：`unknown-tool-real` 最初写成"要求模型去调用不存在的工具名"，
> 结果间歇性失败——模型有时不听话，直接改调了已注册的 `read_file`。
> 这是**模型行为不确定**，不是产品缺陷。改为断言"实际执行者的工具名必须已注册"后稳定。
> 教训：真实模型测试只能断言不变量，不能断言模型的具体选择。

### 6.1 真实调用暴露的三个缺陷

**这是本节最重要的部分：三个缺陷全部只有真实 provider 能发现。**

**（1）循环没有把工具声明传给模型（契约缺口）**

首轮 MiMo 运行，模型输出了 XML 文本而不是工具调用：

```text
Text(<tool_call><) Text(function=rea…) Text(<) Text(parameter=pa…) …
```

根因：`Microsoft.Extensions.AI 10.11.0` 的 `AITool` 是抽象类且只有非公开构造函数，**无法子类化**，因此早期实现把工具 schema 拼进系统提示文本让模型"知道"有哪些工具。模型看到文字描述后自行发明了 XML 格式的伪调用，而不是走标准 `tool_calls` 字段。

直接验证：用 curl 在请求体带 `tools`，MiMo 立即返回标准 `tool_calls`（`finish_reason: tool_calls`，`function.arguments` 为 JSON 字符串）。**问题不在模型，在契约。**

修法：连接器从 `IToolDispatcher` 的冻结描述符读取工具声明并放入请求体。这同时修正了职责归属——schema 权威在宿主（dispatcher 冻结过一次），不在循环临时拼接的文本里。

**（2）空 `choices` 数组导致越界**

MiMo 会发送 `choices: []` 的 usage-only 帧，`chunk["choices"]?[0]` 直接抛：

```text
模型流中断：Index was out of range. Must be non-negative and less than the size of the collection.
```

假模型永远返回恰好一个 choice，所以这条路径从未被覆盖。修法：先判 `choices.Count`。

**（3）Waygate 暴露的两种"空值覆盖"**

`deepseek-v4.1-flash` 的流式帧有两种形态：首帧给 `id`/`name`，后续帧**只给 `arguments` 片段**，
且 `id`/`name` 以**空字符串**而非 `null` 出现。原实现用 `?? pending.Id` 只挡住 null，
结果被空字符串覆盖，最终产生大量空 ID、空名称的伪调用（一次响应里出现 17 条）。

同一个端点还把未完成的 `finish_reason` 表示为**空字符串**而不是 `null` 或缺失。
原判断 `finish != "null"` 把 `""` 当成有效终结，导致**每个 delta 都触发一次下发**。

两处修法：只接受非空字符串；`id`/`name` 也只在非空时覆盖。
这两个缺陷在 MiMo 与 MiniMax 上都不出现——**它们是协议变体，不是协议错误**。

**（4）参数分片的真实语义与假模型假设不同**

GLM 与 MiniMax 的流式协议证实：工具参数是**原始 JSON 字符串片段**，需要按 index 拼接到结束才可解析：

```text
content_block_start  index=1  tool_use  input={}
input_json_delta  partial_json: "{\""
input_json_delta  partial_json: "path"
input_json_delta  partial_json: "\":\""
input_json_delta  partial_json: "/tmp"
…
message_delta  stop_reason: tool_use
```

早期循环实现用"同一调用 ID，参数按 key 合并字典"是错误的：那既无法表达字节片段，也会把嵌套结构拍平。正确做法在**连接器层**——累积片段字符串，`finish_reason` 到达时统一解析一次。循环只消费完整参数。

### 6.2 关于验证方式的教训

同一个缺陷，假模型测不出来：把循环的聚合逻辑临时改回"按 key 合并"，11 项假模型反例**仍然全绿**。原因是 M.E.AI 的 `FunctionCallContent.Arguments` 已经是解析后的字典，片段信息在抽象层就被抹平了。

结论：**契约层与连接器层的行为必须用真实 provider 验证**，假模型只能验证循环自身的控制流（顺序、终态、去重、旁路）。两者不可互相替代。

## 7. 尚未验证的部分（必须如实记录）

- **审批与资源锁未实现**。当前 dispatcher 只做 schema 最小校验、调用 ID 去重、权限描述采集和终态记录。审批绑定、工作区独占、执行前文件版本复核均未实现，因此**不能宣称具备"批准 A 执行 B"的防护**。
- **JSON schema 校验是最小子集**（顶层 `type` 与 `required`），不是完整校验器。
- **未测持久化与提交屏障**，属于后续切片。
- **只测了两家 OpenAI 兼容端点**，未覆盖 Anthropic 原生协议、多模态输入、并行工具调用。

## 8. 复现方式

```bash
dotnet build spike/Dougong.Agent.Comparison -c Release

# 假模型对比
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- a
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- b

# 真实 provider（密钥只从环境变量读取，缺失时明确输出「未运行」）
export DOUGONG_PROVIDER_BASE=https://token-plan-cn.xiaomimimo.com/v1
export DOUGONG_PROVIDER_KEY=<key>
export DOUGONG_PROVIDER_MODEL=mimo-v2.6-flash
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- live
```

假模型的三个候选共享同一 `ScriptedChatClient`、`FakeTool`、`ContractHarness`，差异只可能来自运行时实现。
真实 provider 使用 `OpenAiCompatibleChatClient`（最小适配器，非 `Dougong.Ai` 正式连接器），
它在连接器层完成片段拼接，与循环边界清晰分离。

## 9. 结论对后续的含义

- `Dougong.Agent` 的契约（`IAgentRuntime`/`IToolDispatcher`/`IAgentTool`）保持不变，两种实现都能满足。
- 正式实现走候选 B；`spike/` 目录保留作为回归基线，M1 稳定后可删除。
- **工具声明通道已正式化**：`Dougong.Agent.ToolDeclarations` 用 `ChatOptions.AdditionalProperties`
  的约定键（`dougong.toolDeclarations`）让运行时写、连接器读，双方只共享冻结的
  `ToolDescriptor`，互不依赖具体实现。同时提供 `ToOpenAiToolsArray` 避免每个连接器各写一遍。
  契约自检（`-- contract`，6 项）覆盖往返、缺省值、schema 独立解析与 OpenAI 形状。
- 下一步应在自研循环上补齐：审批、资源锁、JSONL 提交屏障、上下文压缩。
