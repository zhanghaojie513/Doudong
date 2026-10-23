using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 工具调用的唯一执行入口。所有宿主、Skills、MCP 与框架适配器都必须经由这里，
/// 不允许任何旁路直接调用 <see cref="IAgentTool.ExecuteAsync"/>。
/// </summary>
/// <remarks>
/// 本切片实现：schema 校验、参数规范化、调用 ID 去重、权限描述采集、终态记录。
/// 尚未实现：审批流程、资源锁与工作区独占、执行前对文件版本的复核。
/// 这些缺口在 docs/m1-candidate-comparison.md 中如实记录，不宣称已具备。
/// </remarks>
public interface IToolDispatcher
{
    /// <summary>注册工具；返回的句柄释放后撤销注册。</summary>
    IDisposable Register(IAgentTool tool);

    /// <summary>已注册工具的冻结描述符，供构造模型工具 schema。</summary>
    IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>
    /// 派发一次调用。无论成功、失败、取消还是被拒绝，都必须返回一个明确的终态，
    /// 不能抛出未归类的异常让调用方失去这条调用的结果。
    /// </summary>
    ValueTask<ToolDispatchResult> DispatchAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>一次工具调用的终态。每个模型请求的调用都必须有且只有一个终态。</summary>
public enum ToolCallOutcome
{
    /// <summary>已执行并成功。</summary>
    Succeeded,

    /// <summary>已执行但工具报告失败。</summary>
    Failed,

    /// <summary>参数不是合法 JSON 或不满足 schema，未执行。</summary>
    RejectedInvalidArguments,

    /// <summary>调用 ID 重复或缺失，未执行。</summary>
    RejectedDuplicateInvocation,

    /// <summary>工具未注册，未执行。</summary>
    RejectedUnknownTool,

    /// <summary>执行前被取消，未执行。</summary>
    CancelledBeforeExecution,

    /// <summary>执行过程中被取消。</summary>
    CancelledDuringExecution,
}

/// <summary>
/// 派发结果。<see cref="Result"/> 仅在有内容时非空（成功或工具返回了错误内容）。
/// <see cref="Diagnostic"/> 用于宿主记录失败原因，不进入模型历史。
/// </summary>
public sealed record ToolDispatchResult(
    string InvocationId,
    string ToolId,
    ToolCallOutcome Outcome,
    AgentToolResult? Result = null,
    string? Diagnostic = null,
    ToolPermission? Permission = null)
{
    public bool Executed => Outcome is ToolCallOutcome.Succeeded
        or ToolCallOutcome.Failed
        or ToolCallOutcome.CancelledDuringExecution;
}
