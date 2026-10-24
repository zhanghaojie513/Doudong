using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// Agent 工具的宿主契约。描述符在注册时冻结，模型只看到 Name/Description/InputSchema；
/// 实际执行必须经过唯一的 <see cref="IToolDispatcher"/>，不得由框架直接调用。
/// </summary>
public interface IAgentTool
{
    /// <summary>注册时冻结的工具身份与 schema。</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>根据参数描述本次调用需要的权限。无法可靠描述时必须返回 <c>IsKnown=false</c>。</summary>
    ToolPermission DescribePermission(JsonElement arguments);

    /// <summary>描述本次调用的资源并发要求。</summary>
    ToolExecutionPlan DescribeExecution(JsonElement arguments);

    /// <summary>
    /// 声明本次调用依赖哪些文件的版本。审批时捕获、执行前比对；
    /// 返回空表示不依赖文件内容（例如纯内存工具）。
    /// </summary>
    IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    /// <summary>实际执行。只能由 dispatcher 调用。</summary>
    ValueTask<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>冻结后的工具描述符；<see cref="InputSchema"/> 必须是宿主侧可校验的 JSON Schema。</summary>
public sealed record ToolDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    JsonElement InputSchema);

/// <summary>工具可能触达的访问类别，可组合。</summary>
[Flags]
public enum ToolAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
    Network = 8,
    External = 16,
}

/// <summary>
/// 工具自描述的权限声明。<see cref="IsKnown"/> 为 false 表示无法可靠描述，
/// 调用方不得把它当作 <see cref="ToolAccess.None"/> 放行。
/// </summary>
public sealed record ToolPermission(
    ToolAccess Access,
    bool IsKnown,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Domains)
{
    public static ToolPermission Unknown { get; } = new(
        ToolAccess.None, false, [], [], []);
}

/// <summary>首版只有两级并发：串行与工作区独占。</summary>
public enum ToolConcurrency
{
    Serial,
    WorkspaceExclusive,
}

/// <summary>本次调用的资源计划。</summary>
public sealed record ToolExecutionPlan(ToolConcurrency Concurrency)
{
    public static ToolExecutionPlan Serial { get; } = new(ToolConcurrency.Serial);
}

/// <summary>
/// 一次已通过 schema 校验并规范化参数的工具调用。
/// <see cref="InvocationId"/> 来自模型响应，用于聚合流式参数与去重。
/// </summary>
public sealed record ToolInvocation(
    string InvocationId,
    string ToolId,
    JsonElement Arguments);

/// <summary>
/// 工具执行结果。<see cref="Content"/> 进入模型历史，<see cref="Details"/> 仅供宿主 UI，
/// 两者都必须是复制过的独立 JSON 数据，不得引用已释放的 JsonDocument。
/// </summary>
public sealed record AgentToolResult(JsonElement Content, JsonElement? Details = null);
