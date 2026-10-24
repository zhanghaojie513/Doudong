# M1 开发历程素材

> 用途：记录 M1（入口与执行契约）期间真实发生的问题与转折，供后续梳理成开发历程文章。
> 不是验收文档，也不是对外承诺。验收状态见 [status.md](../status.md)。
> 记录原则：只写实际发生的；区分「实现缺陷 / 测试缺陷 / 判断错误」三类。
> 上一篇：[m0-dev-journey.md](m0-dev-journey.md)。

---

## 一、数字概览（2026-10-24 收尾时）

| 项 | 值 |
|---|---|
| 生产代码 | 8 006 行（Kernel 2 934 / Agent 3 216 / Ai 1 349 / Cli 507） |
| 探测与自检工程 | 4 423 行（`spike/`，不进 NuGet 包） |
| 测试代码 | 5 016 行 |
| C# 文件总数 | 92 |
| 内核行为测试 | 157 项（M0 关闭时 156，本轮增加 1 项真实缺陷回归） |
| 对抗性探针 | 20 项（6 个维度） |
| 确定性自检套件 | 10 套、85 项 |
| 真实 provider 验证 | 6 个 provider 端点、7 个模型；3 种协议各跑一遍 |
| 本轮修复的真实缺陷 | 15 个（内核 2、连接器 6、循环 3、工具与安全 2、会话 2） |
| 反证（先破坏修复确认测试失败） | 23 处 |

M1 与 M0 最大的不同：**M0 的问题几乎全是"我自己写错了"，M1 的多数问题只有接上真实
provider 才会暴露**。假模型能验证控制流，验证不了协议形状。

---

## 二、实现缺陷

### 1. 工具声明从来没送给模型（真实 provider 首次调用）

本地假模型全绿，第一次接真实 provider，模型没有调用工具，而是在正文里输出了一段
XML 形状的伪调用 `<tool_call>...</tool_call>`。

- **发现方式**：真实调用返回的文本里出现了工具调用的"手写版"
- **根因**：循环把工具声明拼进系统提示文本，从未通过协议字段下发
- **修法**：改为 `ChatOptions.AdditionalProperties` 的约定键（`dougong.toolDeclarations`），
  由连接器按各协议序列化
- **代价**：这条弯路直接说明了"契约层必须用真实 provider 验证"——假模型根本不知道
  工具声明应该放在哪
- **可写成**：模型没调用工具，不一定是不听话

### 2. 空 `choices` 帧导致越界

某些端点会单独发一帧只含 `usage`、`choices` 为空的响应。原实现直接取 `choices[0]`，
抛 `IndexOutOfRangeException`。

- **发现方式**：真实调用随机失败，日志里是数组越界
- **修法**：先判 `GetArrayLength() == 0`
- **可写成**：假模型永远按你的假设发数据

### 3. 参数分片的语义搞错了：应该拼接原文，不是合并字典

`Microsoft.Extensions.AI` 把流式参数暴露成"已解析的字典"。我按字典合并处理，
结果带嵌套对象和数组的参数被拍平，结构丢失。

- **发现方式**：真实调用里 `{"lines":["a","b"]}` 变成了解析失败的参数
- **根因**：协议的参数是**字节片段**（`input_json_delta.partial_json`、
  `function.arguments` 字符串），只有拼完才是合法 JSON
- **修法**：连接器层按 index 拼接原文，到终结才解析
- **反证**：把循环的聚合逻辑临时改回"按键合并"，**11 项假模型反例仍然全绿**
  ——因为片段信息在抽象层就被抹平了。这条是 M1 最重要的方法论发现
- **可写成**：抽象层会吃掉你需要的信息

### 4. 两类"空字符串当缺失"的协议变体

Waygate 端点在后续帧里用**空字符串**覆盖 `id` / `name`（原代码用 `??` 只挡 null），
以及用空字符串表示"本帧没有 `finish_reason`"（原代码把 `""` 当终结，导致每个
delta 都误下发一次调用）。

- **修法**：改为"只接受非空值"
- **可写成**：协议规范没写，但所有实现都在做的隐含约定

### 5. 工具结果回填缺 `tool_call_id`（provider 422）

工具执行完，结果回填成普通 `Tool` 文本，缺 `tool_call_id`，provider 直接 422。

- **修法**：按协议补 assistant 的 `tool_calls` 与 tool 的 `tool_call_id` 配对

### 6. DeepSeek 思考模式要求回传 `reasoning_content`（provider 400）

同上，带工具的 assistant 消息必须回传思考内容，否则 400。

- **修法**：累积 `reasoning_content` 并随该轮 assistant 消息带出

### 7. `finish_reason` 一直读错字段，被兜底掩盖

重写连接器时才发现：`finish_reason` 是 `choices[0]` 的**兄弟字段**，不是 `delta` 的字段。
原实现从 `delta` 里读，主路径**从未命中过**——一直靠"流结束时兜底下发"把调用发出去，
所以功能看起来是好的。

- **发现方式**：新写的确定性帧回放测试 `fragmented-arguments-are-concatenated` 失败
- **反证**：改回从 `delta` 读，该测试立刻失败
- **可写成**：兜底逻辑会掩盖主路径的死亡。这是最难自己发现的一类缺陷

### 8. 工具结果消息的 `Text` 是空的

工具结果只放了 `FunctionResultContent`，而 `ChatMessage.Text` 是由 `Contents` 派生的。
于是三种协议拿到的工具结果都是**空字符串**，模型回答"它返回了空输出"。

- **发现方式**：CLI 端到端跑读文件，模型困惑地说"文件可能存在但是空文件"
- **修法**：`Contents` 里同时放 `TextContent`
- **反证**：去掉 `TextContent`，`runtime-history-carries-tool-result-text` 立刻失败
- **可写成**：消息对象的"便捷属性"是派生的，不是独立字段

### 9. 文本与 `tool_calls` 被拆成两条 assistant 消息（400）

思考模式 provider 拒掉整轮：带 `tool_calls` 的那条消息失去了 `reasoning_content`。

- **发现方式**：审批被拒绝后（拒绝也要回填结果）真实调用报 400
- **修法**：本轮的文本与 `tool_calls` 合进同一条 assistant 消息
- **可写成**：拒绝也是一条历史，也要符合协议

### 10. 两档并发用了两把不相干的锁

`Serial` 与 `WorkspaceExclusive` 各有一把信号量，于是串行工具能与同工作区的独占工具
**重叠执行**——串行承诺和取消语义同时失效。

- **发现方式**：故障验收里"取消不合作仍应持锁"这一项失败
- **修法**：两档共用同一把工作区执行权（`Serial` 额外先取全局串行权）
- **反证**：改回独立双锁，该检查立刻失败
- **可写成**：两个"互斥级别"如果互不互斥，就不是级别

### 11. 宿主密钥会随环境变量继承给子进程

`ProcessStartInfo.Environment` 初始就装着父进程的全部变量。只往里添加白名单项
**并不等于**只传这些——`DOUGONG_PROVIDER_KEY` 会一起被继承，模型跑一条 `printenv`
就能读走密钥，审批与权限描述同时失效。

- **发现方式**：shell 工具验收里 `host-secrets-are-not-inherited` 失败
- **修法**：先 `Environment.Clear()` 再填白名单
- **反证**：去掉 `Clear()`，检查立刻失败
- **可写成**：白名单的前提是先清空。这是安全语义里最容易写错的一行

### 12. 日志 Payload 被双重编码

`CommitOrNullAsync` 把已经是 JSON 的文本又 `JsonSerializer.Serialize` 了一次，
存进去的是字符串字面量。恢复时必须先解一层才能读。

- **发现方式**：恢复逻辑改为正式实现后，所有旧会话都读不出来
- **修法**：按结构存（`JsonElement`），同时保留读旧格式的兼容
- **可写成**：序列化一个"已经是 JSON 的字符串"是个安静的陷阱

### 13. 在工具交互中间分叉，留下悬空的 `tool_calls`

真实 provider 实测 400：`An assistant message with 'tool_calls' must be followed by
tool messages responding to each 'tool_call_id'`。

- **发现方式**：`--branch <会话> --at 2` 在第 2 条（助手声明调用、结果在第 3 条）分叉
- **修法**：向后**补齐**到配对完成处，而不是回退到上一个完整点——用户选的分叉点
  代表"保留到这里为止的上下文"，回退会悄悄少给一段
- **边界**：若补齐会越过下一个用户输入，说明这条调用确实没结果，此时报错而不产出
  不可用的分支
- **可写成**：一个"合理"的修法（回退）为什么是错的

### 14. 未登记共享契约可以经宿主进程静默通过（内核，M0 遗留）

`PluginLoadContext.Load` 在插件目录解析不到时返回 null，运行时于是回落到默认 ALC。
只要同名程序集碰巧已在宿主进程中——哪怕由完全无关的代码装入——未登记的插件就能
加载成功，「登记是加载前置条件」被绕过。

- **发现方式**：CI 持续失败，本地持续通过。追查到底才发现是内核缺陷
- **修法**：记录这类回落并在 `LoadAsync` 中拒绝；排除共享框架程序集
  （`System.*`、`netstandard` 回落到共享框架是唯一正确行为）
- **两次错误尝试**：①直接 `Load` 里抛异常 → 坏 24 个用例（异常被包成
  `ReflectionTypeLoadException`，真正原因被埋掉）②不排除框架程序集 → 坏 20 个用例
  （`System.Runtime` 必然不在插件目录里）
- **可写成**：一个"看起来是环境问题"的 CI 失败，根因在内核

### 15. 同步阻塞的激活回调绕过整个超时预算（内核，对抗性审查发现）

`RunActivationCallbackAsync` 虽返回 `Task`，但在遇到**第一个真正异步的 `await` 之前
是同步执行的**。于是 `activationTask = RunActivationCallbackAsync(...)` 这行本身
就卡在评估循环线程上，后面的 `WaitAsync(ActivationTimeout)` 根本没机会开始计时。

后果不只是"这个插件超时无效"——整个 `EvaluateAsync` 连同 `_reconcile` 信号量被独占，
**其它所有插件的生命周期变更无限排队**。

- **发现方式**：对抗性探针里"同步阻塞的激活回调"这一项，状态停在 `Active` 而不是 `Failed`
- **修法**：回调改为 `Task.Run` 启动，卡住的是线程池线程
- **修复前先实测验证了两个前提**（不靠推测）：`Task.Run + WaitAsync(300ms)` 对同步
  阻塞在 349ms 触发超时；`AsyncLocal` 能传入 `Task.Run`、内部设置的值在内部持续
  有效且不污染调用方——这正是 `_activationScope` / `_callback` 需要的语义
- **反证**：移除 `Task.Run`，回归探针报"激活预算未生效：注册耗时 5032ms"
- **可写成**：`async` 方法在第一个 `await` 之前是同步的——这条语言细节能让超时机制
  整体失效

---

## 三、测试缺陷（测试本身在说谎）

### 16. 协议检查只验证解码器，不验证客户端怎么发

用量记账决定改用标准 `UsageContent`（自定义附加属性对 M.E.AI 的聚合与中间件不可见，
账目会静默丢失）。我写了检查项验证"解码器能解析 usage"，反证时把发送端改回附加属性
——**检查仍然全绿**。

- **根因**：这条检查只测了解码器与 `UsageContent` 的构造，没测客户端实际发出什么
- **修法**：补一条走真实 `HttpClientChatClient` + 假传输的端到端检查
- **可写成**：反证失败，往往说明测试有盲区，而不是代码没问题

### 17. 用进程级全局状态做前提断言

`Unregistered_contract_fails_load_and_leaves_no_entry` 断言"该契约不在当前进程中"，
而同族的另一条用例用 `Assembly.LoadFile` 把它装进去了。谁先跑取决于运行器调度
——CI 稳定失败、本地稳定通过。

- **修法**：改为断言真正要保证的事（插件 ALC 解析不到该契约）；新测试用**独立可回收
  ALC** 制造"进程里已有该程序集"的条件，不再永久污染进程
- **可写成**：测试不该依赖全局可变状态，尤其不该依赖"谁先跑"

### 18. `Publish` 的线程断言在线程池上不成立

`Publish_does_not_run_handler_on_publisher_thread` 在线程池线程上发布再比较线程 ID。
事件泵也跑在线程池上，线程池可能复用发布线程——"两线程不同"根本不是稳定性质。
VSTest 的调度时序恰好掩盖了它，换成 xUnit v3 并行后 5 轮里失败 2 轮。

- **修法**：在专用非线程池线程上发布
- **可写成**：线程池会复用线程，"不是同一个线程"不能靠 ID 比较证明

### 19. 拿"进入过回调的次数"和"清理次数"比较

并发启停探针断言两者相等。但激活可能在提交前被撤销（调用方取消、期望状态改为停用），
那时根本没登记清理，两者本来就不该相等。

- **修法**：改成"每个登记了清理的代次恰好清理一次"——既不漏（资源泄漏）也不重复（双释放）
- **可写成**：断言写错了不变量，会把自己的实现误判成有泄漏

### 20. 用会被取消的令牌测排空

停用会取消代次寿命令牌。探针里 `await release.Task.WaitAsync(ct)` 于是在停用瞬间
就被取消，"排空"退化成"等一个已完成的调用"，测不到真正的排空。

- **修法**：要测排空，被调用方必须**忽略**取消
- **可写成**：测"等待"之前，先确认被等的东西不会自己跑掉

### 21. 探针自己把被测行为搞错了三次

- 在 `RegisterAsync` 返回前读状态（它内部会等激活预算走完）→ 读到中间态
- 把 `DeactivateAsync` 的返回时刻当成排空完成 → 它的语义是"完成状态迁移"，不承诺等
  在途调用；排空的保证要看清理回调的相对顺序
- 先怀疑内核、后怀疑探针：有三次是探针错了，内核是对的

---

## 四、判断错误（AI 协作视角）

### 22. 用"假模型 11 项全绿"宣布循环正确

真实 provider 一接就暴露出 4 个缺陷（工具声明、空帧、参数分片、空串哨兵）。
其中参数分片那条尤其典型：把聚合逻辑改回错误版本，**假模型反例仍然全绿**。

- **结论**：契约层与连接器层必须用真实 provider 验证；假模型只能验证控制流
- **可写成**：绿灯的适用范围比绿灯本身重要

### 23. 反复用 `--no-build` 跑旧 DLL

同一个错误犯了至少四次，最严重的一次是"连接器修好了但 reasoning 仍然是 0"，
排查了很久才发现跑的是旧产物。`spike` 不在 solution 里，全量构建不会重建它。

- **教训**：改完必须重建再验证。这条写进流程后，才有后面"每次反证都重建"的纪律

### 24. 把"看起来是 CI 环境问题"当成结论

CI 连续三次失败，前两次修的是 workflow 参数与一条测试，第三次才发现根因在内核
（缺陷 14）。如果停在"CI 环境不一样"这个解释上，内核缺陷会一直存在。

- **可写成**：环境差异是症状，不是原因

### 25. 一次就把内核改坏了两回

修缺陷 14 时，第一次直接在 `Load` 里抛异常（坏 24 个用例），第二次不排除框架程序集
（坏 20 个用例）。两次都是"只想到目标场景，没想到合法场景"。

- **教训**：改拒绝逻辑前先问"哪些合法调用会命中这条规则"

---

## 五、可用的文章角度

按"有没有戏剧性"排序：

1. **兜底逻辑掩盖了主路径的死亡**
   —— `finish_reason` 读错字段，功能却一直是好的（缺陷 7）
2. **13 个缺陷里 11 个是真实 provider 测出来的，假模型一个也测不到**
   —— 尤其"把聚合改回错误版本，假模型反例仍全绿"（缺陷 3、22）
3. **`async` 方法在第一个 `await` 之前是同步的**
   —— 一条语言细节让整个超时预算失效，还能卡死所有其它插件（缺陷 15）
4. **白名单的前提是先清空**
   —— 一行 `Environment.Clear()`，没有它密钥就交给了模型（缺陷 11）
5. **反证失败，说明测试有盲区**
   —— 用量检查只测了解码器不测发送端（缺陷 16）
6. **CI 失败不是 CI 的问题**
   —— 表面是 workflow 参数与测试顺序，根因是内核绕过契约登记（缺陷 14、24）
7. **一个"合理"的修法为什么是错的**
   —— 分叉点悬空时回退而不是补齐，会悄悄少给一段上下文（缺陷 13）
8. **测试不该依赖谁先跑**
   —— 进程级全局状态让 CI 与本地结论相反（缺陷 17）

避免的角度：把它写成"AI 高效产出了 8000 行"。事实是 13 个真实缺陷、5 个测试缺陷、
4 类判断错误，其中 3 次是探针错了要怪自己。过程比结论有说服力。

---

## 六、素材对应关系

| 主题 | 证据位置 |
|---|---|
| 工具声明通道 | `Dougong.Agent.ToolDeclarations`、提交 `63bd277`、`-- contract`（6 项） |
| 空串哨兵 | 提交 `139cead`、`-- wire`（10 项） |
| 三协议连接器 | `Dougong.Ai/*Format.cs`、提交 `f1fb095`、`-- wire` |
| 真实 provider 验证 | `spike/.../LiveRun.cs`、提交 `35f7b85`/`f5e7504`/`4890556` |
| 提交屏障 | `JsonlAgentStore`、`-- store`（6 项）、提交 `edc578a` |
| 审批与资源 | `ToolDispatcher`、`-- approval`（9 项）、`-- failure`（6 项） |
| 文件版本 | `WorkspacePathGuard`、`-- fileversion`（8 项） |
| 上下文压缩 | `ContextCompaction`、`-- compaction`（9 项） |
| 会话恢复与分支 | `SessionRecovery`/`SessionBranch`、`-- session`（9 项）、提交 `36c7e4b` |
| shell 与密钥 | `ShellTool`/`ShellEnvironment`、`-- shell`（11 项）、提交 `1a26a3a` |
| 用量记账 | `UsageLedger`、`-- usage`（11 项）、提交 `c6db572` |
| 内核对抗审查 | `KernelAdversarialTests`/`KernelDrainProbeTests`、[kernel-adversarial-review.md](../kernel-adversarial-review.md) |
| 未登记契约绕过 | 提交 `706fd43`、`KernelSharedContractMissingTests` |
| 同步阻塞绕过超时 | 提交 `ec59191`、`Sync_blocking_activation_respects_budget_and_isolates` |

复现命令（确定性部分，不联网）：

```bash
dotnet build Dougong.slnx -c Release
dotnet test  Dougong.slnx -c Release --no-build

dotnet build spike/Dougong.Agent.Comparison -c Release
for m in wire session usage shell failure store approval fileversion compaction contract; do
  dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- $m
done
```

---

## 七、实测输出留档（2026-10-24，HEAD `ec59191`）

### 7.1 三协议真实端点（DeepSeek `deepseek-flash`，各 5/5）

```text
protocol=openai     simple-tool-call / fragmented-args-real / unknown-tool-real /
                    text-only-real / coding-task         PASS 5/5
protocol=anthropic  （同上五项）                          PASS 5/5
protocol=responses  （同上五项）                          PASS 5/5
```

### 7.2 会话恢复与分支（真实端点）

```text
读文件 → 退出 → --resume
  模型复述："The file `hello.txt` ... contains exactly: hello from dougong"

在工具交互中间分叉（原本 400 的那个点）
  已从 #4 分支：20261024-055847-d36e（4 条）    ← 自动补齐到配对完成处
  续跑成功：OK
```

### 7.3 用量记账（真实端点）

```text
调用      : 1 次（失败 0）
输入      : 352 tokens
输出      : 3 tokens
合计      : 355 tokens
成本      : 未计算（依赖 provider 价目表，本层不猜单价）
```

### 7.4 内核对抗性审查

```text
修复前（无 Task.Run）： 激活预算未生效：注册耗时 5032ms
修复后：                注册在预算量级内返回，隔离为 Failed，其它插件仍可激活

排空时序（忽略取消的在途调用）：call-start -> call-end -> cleanup
并发启停 20 轮：清理登记数 == 清理执行数（无泄漏、无重复释放）
```

### 7.5 基线

```text
构建       : 0 警告 0 错误
内核测试   : 157 项通过（重复 5 轮）
自检套件   : 10 套、85 项全部通过
打包       : 6 个 nupkg
CI         : success
```

---

## 八、一句话结论

M0 的教训是"测试全绿不等于正确"；M1 的教训是**"假模型全绿不等于协议正确"**
——而 M1 最有价值的一条，是发现兜底逻辑可以把一个从未生效的主路径伪装成正常。
