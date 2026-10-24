using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话持久化契约。**唯一真相源**，也是放行副作用的屏障：
/// 只有 <see cref="CommitAsync"/> 返回成功，后续执行才可继续。
/// </summary>
/// <remarks>
/// 设计约束（docs/architecture.md §4.2、§6.4）：
/// <list type="bullet">
/// <item>事件订阅顺序<b>不</b>承担屏障职责；必须显式 await 提交结果</item>
/// <item>提交失败或存储不可用时停止后续执行，不静默降级</item>
/// <item>缺少终态的工具调用必须能恢复为 <see cref="ToolCallOutcome.OutcomeUnknown"/>，
/// 且不得自动重放副作用</item>
/// <item>实现需声明自身持久化级别；杀进程测试不能代替断电测试</item>
/// </list>
/// </remarks>
public interface IAgentStore : IAsyncDisposable
{
    /// <summary>本次打开的会话标识。</summary>
    string SessionId { get; }

    /// <summary>存储实现自述的持久化保证，供宿主如实展示而非假设。</summary>
    StoreDurability Durability { get; }

    /// <summary>
    /// 追加一条记录并等待其落盘。返回即代表已提交；抛异常代表<b>未</b>提交。
    /// 实现必须保证：返回成功之后，即使进程崩溃也能恢复出这条记录。
    /// </summary>
    ValueTask CommitAsync(StoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>按提交顺序读取已提交记录，用于恢复。不得返回未提交内容。</summary>
    IAsyncEnumerable<StoreRecord> ReadCommittedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 存储实现的持久化级别。宿主必须如实转述，不能把进程崩溃存活说成断电安全。
/// </summary>
public enum StoreDurability
{
    /// <summary>仅内存；进程退出即丢失。只用于测试。</summary>
    ProcessMemory,

    /// <summary>进程崩溃后仍可恢复，但不保证断电安全（例如未 fsync）。</summary>
    SurvivesProcessCrash,

    /// <summary>声明断电安全，需实现侧提供独立的持久化证明。</summary>
    SurvivesPowerLoss,
}

/// <summary>一条已提交记录的封装，带顺序号以支持单写者校验与缺口检测。</summary>
public sealed record StoreRecord(long Sequence, StoreRecordKind Kind, JsonElement Payload)
{
    public override string ToString() => $"#{Sequence} {Kind}";
}

/// <summary>记录类型。恢复时需要据此判断“有开始无终态”的调用。</summary>
public enum StoreRecordKind
{
    /// <summary>用户输入。执行前必须先提交。</summary>
    UserInput,

    /// <summary>完整的模型响应（含工具调用请求）。执行工具前必须先提交。</summary>
    ModelResponse,

    /// <summary>工具调用开始。提交后才允许产生副作用。</summary>
    ToolCallStarted,

    /// <summary>工具调用终态。与 Started 配对。</summary>
    ToolCallCompleted,

    /// <summary>审批决定。</summary>
    ApprovalDecision,

    /// <summary>运行终态。</summary>
    RunFinished,
}

/// <summary>提交失败时抛出的异常。携带失败阶段便于诊断，不包装底层异常对象。</summary>
public sealed class StoreCommitException(string stage, string message, Exception? inner = null)
    : Exception($"提交失败（阶段：{stage}）：{message}", inner)
{
    public string Stage { get; } = stage;
}

/// <summary>
/// 恢复会话时对账出的未知结果。有开始记录却无终态的工具调用会落到这里，
/// 宿主必须暂停该 turn，不得自动重放。
/// </summary>
public sealed record UnknownOutcome(string InvocationId, string ToolId, string Reason);
