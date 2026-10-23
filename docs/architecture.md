# Dougong 架构

> 本文回答"系统长什么样、怎么运转"。为什么这样设计见 [philosophy.md](philosophy.md)；里程碑、命名与待定决策见 [design.md](design.md)。
> 对应 design v1（2026-10-22）。全部包名 `Dougong.*`，目标框架 `net10.0`。

---

## 1. 总览

```text
┌────────────────────────────────────────────────────────┐
│ 宿主层（不在本仓库）                                     │
│  Dougong Desktop(Avalonia) / Zed / 任意 ACP 客户端     │
└──────────────────────┬─────────────────────────────────┘
                       │ ACP stdio / 桌面内部可重连 JSON-RPC（§6）
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

分层图表示职责，不表示编译依赖。编译依赖以 §2 的无环图为准；协议适配器依赖 Agent 契约，不相互引用，也不能让 Agent 反向依赖具体适配器。

## 2. 包结构与依赖规则

| 包 | 职责 | 依赖 |
|---|---|---|
| `Dougong.Kernel` | 生命周期、注册表、事件、动态加载 | BCL + Microsoft.Extensions 的 DI/Logging/Configuration.Abstractions；无模型、协议或 OTel 依赖 |
| `Dougong.Agent` | 运行契约、工具 dispatcher、会话/提交/审批与循环实现候选 | Kernel + Microsoft.Extensions.AI.Abstractions |
| `Dougong.Ai` | 客户端工厂、连接器配置、路由与用量 | Agent + Microsoft.Extensions.AI；具体连接器由宿主选择 |
| `Dougong.Mcp` | MCP 连接及 Agent 工具桥接 | Agent（传递依赖 Kernel）+ 官方 MCP SDK |
| `Dougong.Acp` | 标准 ACP Agent 端适配 | Agent |
| `Dougong.Telemetry` | 内核/运行时事件到 Span/Metric | Kernel + Agent + OTel API；规划包，当前未建工程 |
| `Dougong.Cli` | headless 参考宿主与组合根 | 上述已实现包；不引用桌面内部 IPC |

```text
Kernel ← Agent（+ M.E.AI.Abstractions）
Agent ← { Ai, Mcp, Acp }
{ Kernel, Agent } ← Telemetry（规划）
{ Ai, Mcp, Acp, Telemetry } ← Cli
```

这是**目标依赖图**，不代表工程已同步：当前 6 个 src 工程未引用外部包，Mcp 仍只引用 Kernel，Telemetry 尚缺。版本需在可用 SDK 下核实兼容、还原并编译后固定；文档版本号或中央版本注释不是实际接入证据。本轮仅修订设计，不预先添加未验证的包引用。若 MAF 候选通过 §4.1 的门槛，再在 Agent 实现层或专用适配层引入所需包，不能反向污染 Kernel。

### 2.1 SDK 与桌面端互不耦合（可执行边界）

桌面端 `Dougong Desktop`（Avalonia + TKCode.Arc）是独立仓库，**不在本仓库的编译图中**。两边只通过两条契约相接：`Dougong.*` NuGet 包，以及 ACP 会话语义（外部 ACP Agent 与桌面内部管道共用同一套消息投影）。除此之外不存在编译期或程序集级耦合。

四条硬规则：

1. **依赖单向**。引用只允许指向更底层：`Kernel ← Agent ← {Ai, Mcp, Acp} ← Cli`。同层不得互引，不允许成环。桌面端只能位于 `Cli` 之上（宿主位），永远不能出现在被引一侧。
2. **UI 技术栈不进入 SDK**。SDK 任何工程（`src/**`、`samples/**`）不得引用 `Avalonia`、`AtomUI`、`DougongDesktop`、`ReactiveUI`、`CommunityToolkit.Mvvm`、`System.Windows`、`Microsoft.AspNetCore`；`Dougong.Kernel` 的程序集引用中也不得出现这些名字。
3. **内核不感知 UI**。`Dougong.Kernel` 的公开类型只允许位于 `Dougong.Kernel` 命名空间，不得出现 View、ViewModel、Window、Rendering 一类宿主投影概念；内核只暴露生命周期、服务、事件、状态增量与装卸能力，UI 如何渲染由宿主决定。
4. **内核依赖面固定**。仅 BCL + `Microsoft.Extensions.*` 抽象；模型、协议、OTel、UI 一律不进内核。

这些规则由 `tests/Dougong.Kernel.Tests/ArchitectureBoundaryTests.cs` 执行，不是文档约定：违规会让测试失败并直接打印越界的那条引用。新增工程或新增依赖时，若不符合上述规则，先改设计而不是改测试。

> 边界强度说明：这保证的是**编译期与程序集级解耦**——桌面端可以换 UI 框架、换进程模型、甚至换语言重写，SDK 无需改动。它不提供进程内安全隔离（见 §3.5 末），也不代表桌面端已经可运行（桌面仓库尚未建立）。

## 3. 插件内核（Dougong.Kernel）

内核是全系统唯一不是插件的部件，只提供四种能力：**承托（生命周期）、连接（服务注册表）、通联（事件总线）、装卸（动态加载）**。

### 3.1 核心契约

```csharp
public interface IPlugin
{
    PluginManifest Manifest { get; }   // Id / Version / Dependencies / 提供与消费的服务声明
    ValueTask ActivateAsync(PluginContext context, CancellationToken ct);
}
// 停用不是接口方法——停用 = 释放 PluginContext 作用域，
// 插件经 RegisterForCleanup 登记的收尾回调被动执行

public sealed class PluginContext : IAsyncDisposable
{
    IServiceRegistry Services { get; }   // 本作用域注册/解析服务；Resolve 为未跟踪过渡入口
    IResolver Resolver { get; }          // 代次绑定调用引用；InvokeAsync 持有租约，停用排空
    IEventBus Events { get; }            // 类型化事件总线，IObservable 表面
    IConfiguration Config { get; }       // 本插件配置节
    ILogger Logger { get; }
    void RegisterForCleanup(Func<CancellationToken, ValueTask> cleanup);
    PluginHandle Handle { get; }         // 状态机 + linked CancellationToken
}

public sealed class DougongKernel : IAsyncDisposable
{
    public ValueTask<PluginHandle> RegisterAsync(Func<IPlugin> factory, CancellationToken ct);
    public ValueTask<PluginHandle> LoadAsync(string manifestPath, CancellationToken ct);
    public ValueTask DeactivateAsync(PluginId id, CancellationToken ct);
    public ValueTask<PluginHandle> ActivateAsync(PluginId id, CancellationToken ct);
    public ValueTask<UnloadResult> UnloadAsync(PluginId id, CancellationToken ct);
    public ValueTask<IReadOnlyList<PluginSummary>> GetPluginsAsync(CancellationToken ct);
    public ValueTask<PluginManifest?> GetPluginManifestAsync(PluginId id, CancellationToken ct);
    public IResolver Resolver { get; }              // 提供作用域租约，不泄漏永久裸引用
    public IObservable<PluginStateChange> States { get; } // 已订阅连接上的增量通知
    public StateSubscription SubscribeStates(); // 原子取得快照、修订号和后续增量订阅
}
```

### 3.2 插件生命周期状态机

```text
Registered/Stopped → Blocked（必需需求未满足）→ Activating
Registered/Stopped → Activating → Active → Deactivating → Stopped
Activating/Deactivating → Failed（回滚/清理结果及需重启诊断）
Stopped → Unloading → Unloaded（已脱离宿主，GC 回收另行验证）
```

- `desiredState`（用户/宿主是否要求启用）与运行状态分开保存；`Blocked` 表示仍想启用但当前不能安全运行，不能当作 `Failed` 或“已停用”。
- `Active` 只在服务注册事务提交、解析快照可见后发布；`Deactivating` 在拒绝新调用和清理完成前不会转为 `Stopped`。
- 必需依赖恢复后才重新评估 `Blocked` 插件；可选依赖变化可以触发重新解析，但不能把可选缺失伪装成成功提供。
- 激活超时默认 30s，与 `CancellationTimeout`、`DrainTimeout`、`EventDrainTimeout`、`CleanupTimeout` 分别配置。超时只结束宿主等待，不强制终止仍在运行的插件任务；超时后的代次进入隔离，不得复用、卸载或重叠激活。激活失败必须回滚暂存注册并执行已登记清理。
- 所有状态迁移发布到 `States`（IObservable）；OTel Span 由 `Dougong.Telemetry` 桥接包订阅产生（内核零 OTel 依赖）。
- `Failed` 只表示该操作在受控边界内失败；进程内插件仍可触发不可捕获异常、原生崩溃或资源耗尽，不能据此承诺进程隔离。失败后不能干净停止的代次会保留隔离标记，要求重启宿主。

### 3.3 服务注册表与依赖重启

服务标识 = `(ServiceType, Name?)`；服务需求记录提供方声明之外的**未满足需求**，包括类型、名称、最小/最大数量、是否必需和选择优先级。提供方归属服务条目；消费方只拿到带生命周期租约的解析快照，不能在提供方排空后继续调用。

已实现的调用契约：`Resolver.Resolve<T>` 返回绑定提供方激活代次的 `IServiceReference<T>`，只有 `InvokeAsync` 能接触实例。引用只保存服务键、提供方 ID 与代次，不长期捕获实现对象；每次调用在同一临界区内重新校验双方代次、取得实例并计入在途。停用先封闭整个受影响批次的新调用、取消寿命令牌，再等待实际调用返回后清理。批次按当前代次的实际依赖关系（声明 `DependsOn` 与解析过的提供方边）排序，消费者先于提供方；超过 `DrainTimeout`（默认 30s）仍未排空则标记隔离失败、跳过清理并要求重启宿主，此后拒绝再次激活和卸载。`ActivationTimeout`、`CancellationTimeout`、`EventDrainTimeout`、`CleanupTimeout` 分别约束各阶段，超时只结束宿主等待，不强制终止插件任务；清理超时会停止同批次后续清理并保留依赖资源，cleanup 收到在预算耗尽时被取消的清理令牌。`Services.Resolve/ResolveAll` 只对本次激活回调的异步流开放，凭插件状态从无关任务借用会被拒绝；`Resolve` 返回唯一最优提供方，`ResolveAll` 返回全部匹配提供方而不被优先级收缩。裸实例仍是未跟踪引用，依赖可信插件不缓存到运行期的约定，不宣称运行时全部调用均受租约约束。观察者、事件处理器与状态订阅的诊断以共享 `PluginDiagnostic` 保存异常类型名与消息，不长期持有插件异常对象。

插件清单依赖与运行时服务依赖分开处理：前者决定程序集能否加载，后者决定插件能否激活。M0 拒绝依赖环并报告完整路径；同键多提供方按明确的名称/优先级选择，无法唯一选择时进入 `Blocked`，不静默覆盖。

已实现：`ServiceRequirement.Optional` 标记可选服务需求（缺失不阻塞激活）；`PluginManifest.DependsOn` 表达插件级依赖，被依赖插件须为 `Active`；`RegisterAsync` 在加入前检测依赖环并报告完整路径；`ServiceRequirement.ProviderId` 显式指定唯一可接受提供方；未指定时按 `Register(priority:)` 选出唯一最高优先级提供方，最高优先级并列仍判为歧义进入 `Blocked`。需求判断与 `Resolver`/`Services` 解析使用同一选择逻辑。

```text
提供方停用/失败 →
  1. 生命周期协调器串行化本次变更，冻结相关需求图
  2. 计算依赖闭包（声明依赖 + 已记录的服务需求，含未满足需求）
  3. 原子冻结消费者与提供方整个闭包的新解析/新调用，将对应代次标为不可调用并从可解析视图撤下；已发出租约仅允许在排空预算内完成
  4. 逆拓扑排空消费者及提供方 P，再逐个执行幂等 cleanup；失败/超时阻止代次重叠并保留诊断
  5. 原子提交最终注册表版本，按 desiredState 重评估；必需需求未满足者进入 Blocked，满足且期望启用者才按正拓扑激活
  6. 激活成功后原子提交服务快照，再发布状态事件；失败保留诊断并回滚暂存注册
```

注册表使用版本化解析视图和单一生命周期协调器；并发字典只负责底层存储，不负责启停事务。插件回调不在注册表锁内执行。服务调用通过 lease/作用域计数排空，过期租约拒绝新调用。新增、替换、移除提供方都触发需求重评估，后加载的观察者可通过当前快照和版本游标补齐既有状态。

### 3.4 事件总线

类型化 pub/sub 提供通知接口（`Publish<T>` / `Observe<T>`），不提供事务、持久化或 exactly-once 保证。会话提交、审批决定和工具派发通过显式可等待接口完成，提交后再发状态通知；文本块是独立的暂存投影，见 §6.3。

- 同一会话由协调器分配 `sessionId/turnId/eventSequence`；生命周期状态使用内核修订号与激活代次。不同会话不承诺全局顺序。订阅顺序不决定业务执行顺序。
- 每个异步订阅使用有界队列。状态投影溢出时断开订阅并要求快照重同步；文本/终端投影可合并片段或标记缺口后重同步；遥测可按明确策略丢弃并计数。不得无限积压、静默丢失关键状态或阻塞持久化提交。

已实现：每个订阅独占容量 256 的有界队列，`Publish` 只入队、在独立泵上执行处理器；队列满时断开该订阅并记录诊断，订阅方需重新获取快照；处理器异常被隔离并转为共享诊断数据；停用与内核销毁先封口、再等待已入队事件处理完毕，然后才执行插件清理。普通退订会立即从总线索引移除订阅与空频道，退订后仍在执行的处理器由排空等待覆盖。可空事件类型的 `null` 负载正常投递，不再被误判为溢出。Kernel 在插件作用域内为事件处理器提供生命周期回调保护，处理器内等待自身停用会立即报错而不是死锁。
- 普通观察者异常被隔离并诊断，不中断其他观察者；需要决定流程成败的处理器不得作为普通订阅者实现。同步回调只做有界入队，不运行用户代码或网络 IO。
- 订阅归 `PluginContext` 管理，关闭时停止入队、退订并等待在途回调；超时按清理失败处理，不能声称已排空。释放空频道和队列中的 Type/委托/插件对象引用，避免阻止 ALC 回收。
- `SubscribeStates()` 原子返回 `StateSubscription`：不可变状态快照、修订号 R、已从 R 之后衔接的有界增量流及可释放订阅句柄。返回前先建立增量边界，客户端先应用快照再消费增量；不能通过独立读快照再订阅 `States` 实现。普通 `States` 仅提供增量，不保证首次订阅前历史。版本缺口要求重同步；已关闭代次的迟到事件不得进入新代次或下一轮会话。

已实现：`DougongKernel.SubscribeStates(capacity)` 在同一临界区内取得快照、修订号并建立增量订阅；状态修改、修订号分配、历史写入和 live 入队在同一短事务内完成，同步观察者只在锁外通知，因此并发提交不会倒序。增量带修订号、`Partition`（所属插件）与 `Kind`（状态迁移、登记、移除），同一分区内修订号严格递增，跨分区只能依赖修订号而非业务顺序。队列满时序列以一条 `ResyncRequired` 项结束后终止。`ResumeStates(sinceRevision)` 在保留窗口内回放错过的增量，窗口外返回 `RequiresResync` 要求重取快照。状态历史和诊断只保留共享字符串数据，不持有插件异常对象、Type 或委托。`SubscribeStateChanges` 保留为同步观察者入口。
- M0 用慢订阅、回调抛错、重入内核、订阅期间启停和队列溢出验证上述规则；桌面同步另按 §6.7 验收。

### 3.5 动态加载与隔离

```text
~/.dougong/plugins/<id>/
  dougong.plugin.json          # 清单
  <assembly>.dll + 依赖
```

```json
{
  "id": "dougong.tools.git",
  "version": "0.1.0",
  "entryAssembly": "Dougong.Tools.Git.dll",
  "entryType": "Dougong.Tools.Git.GitPlugin",
  "contractVersions": { "Dougong.Kernel": "0.1.*", "Dougong.Agent": "0.1.*" },
  "pluginDependencies": [],
  "loadMode": "inproc-collectible"
}
```

- 加载：`PluginLoadContext : AssemblyLoadContext(isCollectible: true)` + `AssemblyDependencyResolver`。共享程序集白名单默认为 `Dougong.Kernel`，宿主可显式登记额外的共享契约程序集；显式登记的契约始终解析到宿主的同一程序集实例，其余依赖保持插件私有。M3 可按实际 API 边界评估抽取独立契约包。

已实现：`DougongKernel.LoadAsync(assemblyPath, typeName?)` 在独立 collectible 上下文中加载插件。共享白名单默认为 `Dougong.Kernel`；宿主可用 `PluginLoadContext.RegisterSharedContract(assembly)` 显式声明额外的共享契约程序集（例如示例中的 `Dougong.Sample.Shared`），这些程序集始终解析到宿主已加载的同一实例，其余依赖走 `AssemblyDependencyResolver`。`UnloadAsync` 先停用排空，再重新核验代次、失败标志和最终状态，只有干净停止才移除条目与服务、释放入口类型引用并调用 `Unload()`；清理失败、排空超时或本次停用中新产生的失败都会拒绝卸载并保留条目。`LoadAsync` 在登记后若评估被取消或失败，会撤销本次条目以免留下稍后自动激活的残留插件。契约版本范围校验已实现。
- 第三方跨插件服务使用独立、稳定的共享契约程序集；宿主预先验证兼容范围。实现依赖留在插件私有上下文；不支持的契约版本明确拒载，不能用同名类型字符串绕过类型同一性检查。
- **停止与卸载分离**：`DeactivateAsync` 停止当前激活代次并释放其作用域，保留已加载代码；再激活创建新作用域。`UnloadAsync` 先停止和排空，再释放入口实例、工厂、服务 Type 键、事件频道、队列、任务及诊断中的外部引用，调用 `ALC.Unload()`。清理失败/超时不进入成功卸载状态。卸载后只保留字符串/共享 DTO 描述，重新启用必须重新加载新实例。
- **卸载契约**：清理回调逆序、幂等执行；单个回调失败仍继续清理并聚合错误；清理超时则停止同批次后续清理、保留依赖资源并进入隔离。context 关闭后拒绝迟到注册。超时而不响应取消的代码不能强制中止，禁止重用该实例或重叠激活新代次，标记需重启宿主。独立线程、timer、委托和事件必须归作用域管理。
- ALC 内部静态引用环本身不阻止回收，关键是外部 GC 根。不得将插件对象、Type、反射缓存、原始异常或委托长期存入宿主；错误转为共享诊断 DTO，工具结果采用共享结构化数据。快照与句柄只含 `Id`、`Version`、`State` 等共享数据；完整 `PluginManifest` 含服务需求的 `Type`，只能经 `GetPluginManifestAsync` 显式取得，调用方须自行保证不在卸载后长期保留。
- 卸载验证在独立且禁止内联的辅助方法退出后检查 WeakReference，覆盖实际服务调用、事件、失败清理及多轮装卸。GC 循环仅用于测试，不放进正常生命周期；一次测试通过不代表任意插件可卸载。
- ALC 只隔离依赖解析，不隔离系统权限或进程故障。第一版仅加载可信进程内插件；`out-of-process` 是后续故障隔离候选，同一 OS 权限下仍不等于安全沙箱。

### 3.6 版本与兼容

插件声明所有共享契约的支持范围，加载前验证，不仅检查 Kernel 主版本。M3 起使用语义化版本与 `Microsoft.DotNet.ApiCompat`，同时保留生命周期行为兼容测试；API 形状兼容不等于运行行为兼容。

已实现：`PluginManifest.ContractVersions` 声明契约版本范围（M0 只支持 `major.minor`，语义为宿主主版本相同且次版本不低于声明值）；`DougongKernel.ContractVersions` 暴露宿主版本表；`LoadAsync` 在注册前校验，宿主未提供该契约或范围不符即拒载并给出具体原因。未声明契约的插件不做版本限制。完整 SemVer 区间与 ApiCompat 检查仍在 M3。

### 3.7 组合根与内核验收

宿主通过静态注册入口登记随产品发布的插件工厂，通过 `LoadAsync` 登记动态插件。两者进入同一生命周期协调器；公开方法形状仍是实验性草案。工厂及实例的 ALC 引用必须随卸载释放。Agent 以 `IAgentRuntime` 服务提供会话/运行入口，存储、审批器和模型客户端为必需能力；缺失或替换期间暂停接单及后续派发，不静默切换到无审批或无持久化模式。运行入口解析及工具调用均取得作用域租约，宿主不得永久缓存可替换实现。

M0 验收必须覆盖：必需依赖缺失/恢复、可选空集合后的服务新增、依赖环/多提供方冲突、跨 ALC 服务与事件类型同一性、并发激活/停用、在途调用排空、部分激活失败回滚、清理失败和忽略取消、多轮真实调用后的卸载。未通过这些反例前不冻结 Kernel 公共 API。

## 4. Agent 运行时（Dougong.Agent）

### 4.1 Agent Loop

运行实现通过 `IAgentRuntime` 对外提供会话/运行入口；具体使用 HarnessAgent 还是自研循环，按 [design.md §2.1](design.md#21-基础件与-maf-复用决策2026-10-23-修订) 同一反例集选择，当前未定。以下是两种候选必须满足的逻辑时序，不预设具体类实现：

```text
提交用户输入 → 构造已提交历史与工具 schema → 模型流
  → 文本片段作为暂存投影，不放行工具
  → 按 invocation ID 聚合完整工具参数，验证完整响应
  → 提交完整响应 → 同一 dispatcher 校验/审批/协调资源
  → 提交调用开始 → 工具执行 → 提交每个终态
  → 按请求顺序回填，满足预算与停止条件才进入下一轮
```

工具执行只有 dispatcher 一个所有者。候选 B 的 IChatClient 仅返回工具描述，不套用会自行执行的 FunctionInvokingChatClient；候选 A 的所有框架工具必须包装为同一 dispatcher 入口，并关闭或替换绕过该入口的自动执行/审批路径。不能同时启用两个循环。无法证明旁路已关闭的候选不能通过验证。

按 session/turn/call ID 聚合流式参数，重复/缺失 ID、畸形 JSON、schema 不符或响应中断都不执行；错误、取消和预算耗尽时为未执行调用记录明确终态。每会话首版只允许一个运行 turn，其他输入显式排队或拒绝；历史不能留下无法解释的调用/结果关系。

### 4.2 会话与持久化

- **append-only JSONL 是唯一真相源**：`~/.dougong/sessions/<workspace>/<ts>_<id>.jsonl`；分支 = 从分叉点复制新文件（会话即树）。
- **写盘屏障**：Agent 显式等待存储插件提交完整消息、审批决定和调用执行状态，再放行后续副作用；持久化状态的 UI/ACP 通知在提交后发布，事件订阅顺序不承担屏障职责。未提交文本块允许实时投影，但必须标为暂存，不构成执行依据或恢复承诺。存储不可用或提交失败时暂停执行。
- 恢复：重放已提交 JSONL 重建历史；支持 resume / branch / list。未提交流式文字不在崩溃恢复保证内；缺少终态的调用按结果未知处理，不自动重放副作用。持久化级别、单写者与桌面恢复边界见 §6.4。

### 4.3 工具体系

```csharp
public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    ToolPermission DescribePermission(JsonElement arguments);
    ToolExecutionPlan DescribeExecution(JsonElement arguments);
    Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct);
}

public sealed record ToolDescriptor(
    string Id, string Name, string Version, string Description, JsonElement InputSchema);

[Flags]
public enum ToolAccess
{
    None = 0, Read = 1, Write = 2, Execute = 4, Network = 8, External = 16
}

public sealed record ToolPermission(
    ToolAccess Access, bool IsKnown,
    string[] Paths, string[] Commands, string[] Domains);

public enum ToolConcurrency { Serial, WorkspaceExclusive }
public sealed record ToolExecutionPlan(ToolConcurrency Concurrency);

public sealed record AgentToolResult(JsonElement Content, JsonElement? Details = null);
```

以上是待编译验证的契约草案；`ToolExecutionPlan` 首版只有串行与工作区独占两种，资源级并发键在需要时再扩展。`Descriptor` 由运行时注册时冻结，模型只见 Name/Description/InputSchema；宿主同时记录插件激活代次、来源和版本。`InputSchema` 必须有宿主侧校验器，不以模型“应该遵守 schema”代替验证。运行时复制 JsonElement 为独立拥有的数据，限制参数/结果大小；不保留插件自定义对象或已释放 JsonDocument 的引用。Content 进入模型历史，Details 仅供 UI，仍遵守日志/隐私与大小限制。

`IsKnown=false` 表示无法可靠描述，不能把未知映射成 `None`；`None` 仅表示受信任实现声明无敏感访问。权限可组合，审批器必须覆盖所有要求；路径解析到当前工作区并处理大小写、链接/重解析点、越界路径及文件版本。校验仍存在文件系统竞态，必要时在实际打开文件时复核，不宣称绝对隔离。shell 按 Execute 处理，不能靠命令文本静态推断全部副作用。

自描述统一了声明入口，减少中央权限表遗漏风险，但不证明声明真实。宿主保留逐工具覆盖/拒绝配置；未知权限默认拒绝或经明示风险的人工确认，不能自动放行。可信进程内插件可直接调用 OS API，因此审批约束的是统一执行入口，不是安全沙箱。

所有宿主和适配器调用同一 `IToolDispatcher`：先依据已冻结的宿主 schema 校验并规范化参数；取得目标激活代次的短期租约后调用 `DescribePermission`/`DescribeExecution`，将权限与资源计划复制为共享数据，再释放预检租约并等待审批。执行前重新取得同一代次的调用租约与资源锁，重做预检并比对参数、权限、资源及文件版本，再提交调用开始、执行并提交结果；插件已停用或代次变化则拒绝或重新预检/审批，不调用过期实例。审批凭据绑定 session/turn/invocation ID、工具 ID/版本/激活代次、规范化参数摘要、工作区、授权范围、有效期以及依赖的文件版本；任意变化都重新审批或拒绝，不能“批准 A、执行 B”。等待审批期间不持插件租约或资源锁。CLI、ACP、MCP、Skills 与框架适配器都不得绕过 dispatcher。

### 4.4 执行管线与资源协调

- 未声明或无法验证并发能力的工具默认 `Serial`，在同一工作区跨会话互斥。shell、未知资源和会改变进程/环境状态的工具取工作区独占锁；不把结果按序回填当作副作用按序。
- 首版只实现这两级：工具串行 + 工作区独占。资源级读写键、跨工作区 host 全局协调属于后续扩展，在出现真实并发冲突并有测试用例后再引入，不预先冻结其键格式与冲突规则。
- 模型批次中的预检/审批串行；依赖先前调用结果的调用串行。同批包含独占工具时首版整批串行。
- 结果按请求顺序回填，工具状态区分等待、拒绝、取消、超时、执行失败和结果未知。取消是协作式：尚未执行者标记取消，已执行者不得伪装已撤销；忽略取消的调用仍占用租约/锁并阻止替换，需诊断而非提前释放资源后重叠执行。
- M1 验收包含同文件并发编辑、读写竞争、跨会话 shell、路径别名/越界、审批后工具替换/文件变动、未知权限、参数不符 schema、重复调用 ID 和取消不合作；验证调用仅有一个执行入口。

### 4.5 上下文压缩

- 预算包括系统指令、历史、工具 schema、输出余量与估算误差；`effective` 是扣除输出/安全余量后的输入预算，必须为正。旧公式 `max(effective − 13_000, floor(effective × 0.6))` 仅是候选触发阈值，不是通用保证；按模型上下文和真实 token 计数验证后配置。
- 压缩只处理已提交的一致历史切面，保留进行中工具调用及其结果配对；摘要版本和来源区间提交成功后再替换上下文，原日志保留。失败不得覆盖原历史，预算仍不足则暂停并提示处理。
- prompt cache 支持、前缀命中、有效期和计费依赖 provider，必须用实际 usage 验证；不能承诺“只为新增消息付费”。

## 5. 协议与集成层

### 5.1 Ai 层（Dougong.Ai）

- `ChatClientFactory`：按命名配置（provider / endpoint / model / key 来源）建 `IChatClient`；官方与社区 M.E.AI 连接器即插即用；DeepSeek 走 OpenAI 兼容端点。
- 用量/成本记账：per-session / per-day 聚合 → OTel 指标 + 事件总线。
- 逃生舱：`Dougong.Ai.Pi`（适配 pi.net 的 Pi.Ai，42 厂商 + OAuth 场景，M4 按需）。

### 5.2 Mcp 层（Dougong.Mcp）

每个 MCP server 连接由插件管理，工具桥接为 Agent 契约并通过同一 dispatcher。逐工具配置权限及资源计划；server 提供的 annotations 仅作提示，不是可信授权依据。未知工具默认不能自动批准，不以整个 server 的低风险默认值覆盖破坏性工具。工具列表/参数 schema/来源身份变化时更新注册版本，使旧审批失效；连接断开或取消后不假定远端操作已撤销，缺结果调用按未知状态处理。

### 5.3 Acp 层（Dougong.Acp）

协议面（C# 无官方 SDK，手写实现，协议版本锁定 + 兼容测试）：

| ACP 消息 | Dougong 侧映射 |
|---|---|
| `initialize` | Kernel 能力握手 |
| `session/new` · `session/prompt` | Agent Loop 启动 / 输入 |
| `session/load`（v1，需 `loadSession` 能力） | 加载已提交会话；不表示接管旧进程或恢复旧连接 |
| `session/resume` / `session/list` / `session/close`（v1 稳定） | 恢复、列举、关闭会话；按兼容客户端支持逐步实现 |
| `elicitation/create`（客户端能力） | 结构化补充输入；可承载审批类交互 |
| `session/update`（通知） | 事件总线消息翻译（流式文本块、工具调用、计划） |
| permission 请求（Agent→Client） | 审批流（客户端弹窗 = 审批器插件的一种） |
| `session/cancel` | linked CancellationToken 取消 |

### 5.4 遥测

OTel 三类 Span（LLM 调用、工具执行、插件生命周期）+ 用量 Metrics；交互宿主可用 console exporter，ACP stdio 模式必须改写 stderr 或独立出口，禁止污染协议 stdout。

## 6. 宿主与进程模型

```text
DougongDesktop.Desktop（Avalonia，可 NativeAOT）
    │ 桌面内部 JSON-RPC：可重连的本地命名管道
    ▼
DougongDesktop.AgentHost（JIT，直接内嵌 Dougong SDK）
    │ 独立、受认证的控制管道
    ▼
DougongDesktop.PtyHost（持有 PTY 与 shell 子进程）
```

### 6.1 进程职责与启动

- `Desktop` 只承载 UI、连接管理和状态投影，不引用 `Dougong.*`、不加载动态插件。NativeAOT 是发布目标，仍需以实际依赖的发布测试验证。
- `AgentHost` 是独立引擎宿主，直接引用 SDK、创建内核并加载插件；不再拉起另一个 `dougong` 引擎进程。桌面仓库交付该可执行程序；SDK 提供可复用的组合入口，CLI 和 AgentHost 各自调用。
- 第一版每个 OS 用户、每个应用配置目录只有一个 AgentHost，可承载多个工作区和会话。Desktop 先发现并认证已有实例；不存在才通过固定安装路径启动。通过用户级单实例锁和就绪握手处理并发启动，不能仅根据残留 PID 判断实例存活，也不从端点文件执行任意程序。
- `AgentHost` 创建并管理 `PtyHost`；PTY 与 shell 句柄归后者持有，Desktop 不拥有终端子进程。三者是三种进程角色，PtyHost 按需启动。
- 关闭窗口或 Desktop 崩溃只会断开 UI，不关闭 AgentHost/PtyHost；显式“停止引擎”才启动有序关闭。启动与 Windows Job Object 配置不得把引擎或 PTY 绑定到 UI 的退出生命周期。
- `dougong` CLI 是另一个直接承载 SDK 的 headless 宿主，不连接桌面常驻引擎；CLI 与桌面不得同时写同一会话文件，存储层必须获取单写者租约，冲突时拒绝写入并给出诊断。

### 6.2 两条协议入口与本地连接

| 入口 | 传输与边界 |
|---|---|
| 外部 ACP 客户端 ↔ `dougong` CLI | 标准 ACP stdio，UTF-8、每行一条 JSON-RPC；stdout 仅允许协议消息，日志/遥测写 stderr 或独立出口 |
| Desktop ↔ AgentHost | 版本化的桌面内部 JSON-RPC，第一版 Windows 本地命名管道；复用 ACP 会话消息语义，另有连接、附着和同步扩展；不宣称标准 ACP 客户端可直接连接 |
| AgentHost ↔ PtyHost | 独立的本地控制管道，用于创建、输入、输出、resize、取消、释放及状态查询；Desktop 通过 AgentHost 使用终端 |

本地管道必须拒绝远程连接，限定当前 OS 用户访问；端点元数据和启动凭据也使用当前用户 ACL。连接握手校验随机凭据、协议版本和本次进程的 `hostInstanceId`，不得在命令行、日志或模型上下文暴露凭据。该机制防止意外跨用户连接，不是对同一用户下恶意进程的安全沙箱。

私有层只承担标准协议不覆盖的部分：引擎进程的启动/发现/守护、本地认证、单实例与单写者控制、PTY 所有权。消息分帧、快照、事件游标和审批重建等会话语义**不在此处冻结**；具体编码随实现决定，并优先采用协议标准形态，不引入需要长期兼容的自定义格式。第一版同一会话只允许一个可写控制连接；会改变状态的请求带稳定 `commandId` 供去重，连接超时不能直接推导成执行失败或自动重试。

### 6.3 存活重连与状态同步

**存活重连**指 Desktop 重启后附着仍运行的 AgentHost，保留已有运行状态；**崩溃恢复**指 AgentHost 已退出，由新实例从持久化记录恢复。两者分别实现、分别验收，不能用一次 JSONL 重放测试代替。

会话语义**优先采用 ACP**：ACP v1 已稳定的 `session/resume`、`session/list`、`session/close`、取消与 elicitation，以及 v2 draft 中的 `replayFrom` 重放和恢复时重建待处理审批，应作为目标形态。ACP v2 定稿后，桌面内部协议优先收敛到这些方法，只对标准缺口保留私有扩展。当前阶段不把私有 DTO、帧格式或游标细节写成对外承诺。

在上述收敛完成前，第一版所需的最小私有语义是：附着时取得会话当前状态（已提交历史位置、当前 turn、工具终态/未知、待审批项、终端状态），并只应用之后的增量；客户端按业务 ID 合并，重复事件不产生重复操作。增量缓存必须有界，游标过旧或缺口时明确要求重新同步，不假装完整回放；慢客户端不得阻塞存储提交。ACK 仅表示 UI 已应用，不能充当提交确认或执行授权。

UI 断线后引擎停止接收新输入；当前模型请求可完成当前响应，已经开始的工具可按原授权完成并提交结果，但不派发新的工具调用，也不进入下一轮模型调用。未开始的调用保持暂停并在重新附着时重新校验授权。超过明确配置的模型/工具超时按取消策略处理，不能因 UI 断线自动扩大权限。无 UI 且没有运行任务时，AgentHost 可按配置空闲退出；已有终端未释放时不得按“空闲”直接退出。

### 6.4 AgentHost 崩溃恢复与提交边界

恢复所需的用户输入、完整模型工具请求、审批决定、调用开始与工具终态必须经存储接口显式 `await CommitAsync` 确认；持久化状态通知只在提交后发布，不依赖订阅顺序建立屏障。实时文本块作为独立的暂存投影事件，不参加提交游标或放行执行。存储缺失或提交失败时，停止后续执行。存储实现需要分别声明进程崩溃与断电的持久化级别；杀进程测试不能代替断电测试。

- 会话日志采用单写者、记录版本与顺序号；尾部半条记录在恢复时隔离并诊断，非尾部损坏不得静默跳过。新进程先取得会话写租约并验证已提交记录，再对客户端宣告恢复完成。
- 有调用开始记录但缺少终态的工具标记为 `OutcomeUnknown`，暂停该 turn 并要求对账或确认。即使只有执行意图，也不能仅凭“没有结果”假定尚未执行；恢复不得自动重放外部副作用。支持幂等键的工具可按自身契约核实结果。
- 运行中的模型流不从字节断点继续，已存历史用于后续新请求；未提交输出显示为中断，不补造成功终态。
- UI 发现 host 失联后展示“引擎已中断”，用户重新打开或明确恢复时才启动新实例；不在崩溃循环中无限自动重启或自动续跑任务。UI 重发请求前先查询 `commandId` 状态；无法确认时提示核实，不能重复执行。
- 新实例不得复用旧审批或接管旧进程中的未核实调用；`hostInstanceId` 的改变必须使客户端重新同步和重新取得控制权。

### 6.5 审批绑定与断线规则

审批记录至少绑定 `sessionId`、`turnId`、`invocationId`、工具身份及版本、规范化参数摘要、工作区、授权范围与到期时间；待展示请求另绑定当前 `hostInstanceId`。运行时在实际派发前重新校验，决定与派发由会话协调器串行处理。

| 情况 | 行为 |
|---|---|
| 等待审批时 UI 断线 | 保持暂停，不隐式批准；废止旧连接的审批请求，重连后为同一调用生成新请求 |
| 已批准但尚未开始执行时断线 | 收回尚未消费的一次性执行许可；重连并重新校验/审批后才派发 |
| 工具已开始后断线 | 允许原调用在预算内完成并提交结果；断线本身不等于取消，也不授权下一次调用 |
| 重复、迟到、过期或参数已变化的答复 | 返回明确失效/已处理状态，不再次执行，不改变已完成决定 |
| 用户取消、审批超时、会话关闭 | 持久化相应终态并停止后续派发；已运行工具按工具取消契约处理，不能宣称外部副作用已撤销 |
| AgentHost 重启 | 旧执行许可失效，待审批项重新生成；执行结果未知项先核实，不通过重新审批掩盖未知状态 |

审批等待超时是独立、显式的配置项；超时只会结束等待，不会自动同意。桌面“待定/允许/拒绝/取消”等交互状态与 `ToolAccess` 六类权限维度分别建模，不能共用枚举。这些机制约束统一执行管线，防的是模型误操作与并发冲突，**不是对可信进程内插件直接访问系统资源的隔离或安全保证**。

### 6.6 PTY 资源与退出策略

- AgentHost 负责启动 PtyHost、登记 terminal ID 和会话归属；PtyHost 持有 PTY、shell 与命令子进程句柄，并按终端串行处理输入和 resize。关闭 UI 不关闭这些句柄，重连时终端 ID 保持稳定。
- 首版只保证终端 ID 稳定、输出按 terminal ID 有界缓冲、重连可取回缓冲与后续增量；缓冲截断必须明确标记。**终端屏幕状态（VT 快照）恢复不纳入首版保证**，也暂不作为验收门槛；需要时再作为独立特性验证，未验证前不宣称“终端无损恢复”，而是明确显示中断或已截断。
- shell 交互输入使用序号/确认去重；重连先查询最后已接收输入，不盲目补发。Agent 工具命令还要绑定 `invocationId`，命令完成由 PtyHost 明确报告退出状态，不能从提示符或输出文本猜测。
- AgentHost 与 PtyHost 控制连接断开时，PtyHost 停止接收新输入，清理所属 shell/命令进程树并退出；第一版不支持跨 AgentHost 崩溃接管旧 PTY。新引擎把原终端标为中断，原工具结果按未知状态处理，用户可创建新终端。清理有界且结果可诊断；Windows 用由 PtyHost 持有的 Job Object 约束其子进程，但不承诺阻止相同权限下刻意脱离的程序。
- 显式释放终端或停止引擎时先停止新工作，再取消/排空调用、提交可确认结果、释放终端并退出；遇到不能确认的副作用记录未知状态。释放请求幂等，PtyHost 的强制结束也不能伪装成命令成功。

### 6.7 验收门槛与实施边界

本节是 2026-10-23 的设计修订，尚未实现或验证。SDK 仓库负责会话提交/恢复与执行状态契约；Desktop 仓库负责 AgentHost、PtyHost、内部 IPC 和 UI 投影；外部 ACP stdio 兼容测试独立进行。桌面 P1 验证正常连接，P2 必须通过下列故障用例后才能宣称支持恢复和重连，不以周数代替验收：

| 用例 | 必须观察到的结果 |
|---|---|
| 两个 UI 同时启动、残留端点、错误凭据或协议不兼容 | 只启动一个合法引擎，拒绝未授权连接与静默控制权抢占 |
| UI 退出后重新打开 | 同一 host/会话可重新附着，历史、当前 turn 和工具状态一致 |
| 快照与事件并发、慢消费者、缓存溢出 | 无状态缺口或重复执行；必要时明确要求重新同步，队列保持有界 |
| 等待审批/批准未开始/执行中分别断线 | 暂停或完成行为符合 §6.5；旧答复、重复请求不触发第二次执行 |
| 模型流、工具副作用与终态提交之间分别结束 AgentHost | 只恢复已提交记录，未知结果可见，不自动重放；存储失败不放行后续调用 |
| UI 重启和 AgentHost 崩溃分别发生在终端运行时 | 前者保持终端 ID 与有界输出缓冲并可同步；后者清理旧终端并显示中断，不伪装接管或成功；不要求 VT 屏幕状态恢复 |
| 重连时重复发送终端输入或工具命令 | 依据稳定 ID/序号去重，不重复输入或执行 |
| 显式停止引擎、取消、审批超时、重复释放 | 终态明确，进程/句柄按策略释放；不能清理的状态有诊断 |
| 外部 ACP + 日志/遥测同时启用 | stdout 保持逐行纯协议；桌面内部入口不影响标准 ACP 出口 |

双轨协同计划见 [design.md §12](design.md#12-桌面端协同开发计划dougong-desktop-双轨)，执行状态见 [status.md](status.md)。

## 7. 数据与配置布局

```text
~/.dougong/
├── sessions/<workspace>/<ts>_<id>.jsonl    # append-only 会话真相源
├── plugins/<id>/dougong.plugin.json + dll  # 动态插件
└── config/                                 # 宿主 appsettings + 每插件配置节
```

## 8. 仓库结构

```text
dougong/
├── src/
│   ├── Dougong.Kernel/
│   ├── Dougong.Agent/
│   ├── Dougong.Ai/
│   ├── Dougong.Mcp/
│   ├── Dougong.Acp/
│   └── Dougong.Cli/
├── samples/          # 每里程碑一个可运行样例，CI 编译保活
├── tests/
└── docs/
```

## 9. 关键时序

### 9.1 一次用户请求的生命周期

```text
用户输入 ──▶ Acp: session/prompt
              │
              ▼
        Agent Loop ──▶ IChatClient 流式返回
              │              │ 文本块 → 事件总线 → session/update → 客户端渲染
              │ MessageEnd ──┤
              │              ▼
              │      await 存储提交完整响应 ──放行──▶ 工具管线
              │                                          │ 预检(串行)→审批
              │                                          │ await 提交决定/调用状态
              │                                          │ 执行(并行/串行)
              │                                          ▼
              │                              await 提交结果，按序回填历史
              └───────────── 循环直到无 tool_call ◀────────┘
```

### 9.2 插件动态加载

```text
LoadAsync(manifest) → 校验全部共享契约范围 → new PluginLoadContext(isCollectible)
  → 解析共享契约/私有依赖 → 注册需求，验证无环 → 新激活代次
  → 暂存服务/订阅 → ActivateAsync → 原子提交注册表版本与 Active
  → 发布已提交状态通知；失败回滚并逆序清理，缺必需依赖进入 Blocked
```

### 9.3 提供方停用 → 依赖重启

```text
DeactivateAsync(P) → 串行协调变更 → 计算需求闭包
  → 原子冻结/撤下整个闭包的可解析代次，拒绝新解析与新调用
  → 逆拓扑排空消费者与 P，再清理 → 原子提交最终注册表版本
  → 按 desiredState 重评估：必需需求缺失 → Blocked；满足且期望启用 → 正拓扑激活
  → P 再启用后重评估 Blocked 消费者；清理超时阻止代次重叠并给诊断
```

## 10. 扩展点总表

| 想扩展什么 | 实现方式 |
|---|---|
| 新工具（Git、浏览器、数据库…） | `IAgentTool` 插件 |
| 新模型接入 | M.E.AI 连接器 + `ChatClientFactory` 配置（不改代码） |
| 新 MCP server | 配置声明连接，自动成为插件 |
| 新技能（SKILL.md 知识包） | 技能加载器插件：技能目录本身作为工具、按需加载（见 [skills-evolution.md](skills-evolution.md)） |
| 审批交互（控制台/桌面/自动白名单） | 审批器插件 |
| 会话存储后端（SQLite…） | 存储插件（默认 JSONL 插件） |
| 宿主 UI | 实现 ACP Client（Dougong Desktop 是第一例） |
| 遥测出口 | OTel exporter 配置 |
