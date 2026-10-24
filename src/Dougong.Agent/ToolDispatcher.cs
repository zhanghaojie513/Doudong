using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// <see cref="IToolDispatcher"/> 的首版实现。它是唯一执行工具的地方：
/// 校验参数、去重调用 ID、采集权限描述、记录终态。
/// </summary>
/// <remarks>
/// 能力边界（与 docs/m1-candidate-comparison.md 一致）：
/// 尚未实现审批、资源锁/工作区独占、执行前文件版本复核。
/// 因此当前实现不能声称具备“批准 A 执行 B”的防护。
/// </remarks>
public sealed class ToolDispatcher : IToolDispatcher
{
    private readonly object _sync = new();
    private readonly IToolApprover? _approver;
    private readonly ToolResourceGate? _gate;
    private readonly string _sessionId;
    private readonly string _turnId;
    private readonly string _workspace;
    private readonly TimeSpan _approvalValidity;

    /// <summary>
    /// 建立 dispatcher。审批器、资源闸门与工作区都可省略：
    /// 省略即表示"无审批要求"，仅用于测试与对比；生产宿主必须显式提供审批器。
    /// </summary>
    public ToolDispatcher(
        IToolApprover? approver = null,
        ToolResourceGate? gate = null,
        string sessionId = "session",
        string turnId = "turn",
        string workspace = ".",
        TimeSpan? approvalValidity = null)
    {
        _approver = approver;
        _gate = gate;
        _sessionId = sessionId;
        _turnId = turnId;
        _workspace = workspace;
        _approvalValidity = approvalValidity ?? TimeSpan.FromMinutes(5);
    }

    private readonly Dictionary<string, Registration> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenInvocations = new(StringComparer.Ordinal);

    /// <summary>
    /// 记录所有实际进入 dispatcher 的调用 ID，供旁路检测使用。
    /// 绕过本类直接执行工具的调用不会出现在这里。
    /// </summary>
    public IReadOnlyCollection<string> DispatchedInvocations
    {
        get { lock (_sync) { return [.. _seenInvocations]; } }
    }

    public IReadOnlyList<ToolDescriptor> Tools
    {
        get { lock (_sync) { return [.. _tools.Values.Select(r => r.Descriptor)]; } }
    }

    /// <summary>
    /// 把已注册工具的冻结描述符投影为模型可见的工具声明。
    /// 这是唯一合法的"告知模型有哪些工具"的通道：schema 由宿主冻结，
    /// 执行仍必须回到 <see cref="DispatchAsync"/>，不存在模型侧直接执行路径。
    /// </summary>
    public IReadOnlyList<ToolDescriptor> ModelVisibleTools => Tools;

    public IDisposable Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var descriptor = tool.Descriptor
            ?? throw new InvalidOperationException("工具描述符为 null。");
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new InvalidOperationException("工具 Id 不能为空。");
        if (descriptor.InputSchema.ValueKind is not JsonValueKind.Object)
            throw new InvalidOperationException(
                $"工具 '{descriptor.Id}' 的 InputSchema 必须是 JSON 对象。");

        // 冻结：复制 schema，避免注册后外部改动影响已发布给模型的描述。
        var frozen = descriptor with { InputSchema = descriptor.InputSchema.Clone() };
        lock (_sync)
        {
            if (!_tools.TryAdd(descriptor.Id, new Registration(tool, frozen)))
                throw new InvalidOperationException($"工具 '{descriptor.Id}' 已注册。");
        }

        return new RegistrationHandle(this, descriptor.Id);
    }

    public async ValueTask<ToolDispatchResult> DispatchAsync(
        ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrWhiteSpace(invocation.InvocationId))
            return Reject(invocation, ToolCallOutcome.RejectedDuplicateInvocation,
                "调用 ID 为空，无法与模型响应中的调用对应。");

        Registration? registration;
        lock (_sync)
        {
            if (!_seenInvocations.Add(invocation.InvocationId))
                return Reject(invocation, ToolCallOutcome.RejectedDuplicateInvocation,
                    $"调用 ID '{invocation.InvocationId}' 重复。");
            _tools.TryGetValue(invocation.ToolId, out registration);
        }

        if (registration is null)
            return Reject(invocation, ToolCallOutcome.RejectedUnknownTool,
                $"工具 '{invocation.ToolId}' 未注册。");

        if (cancellationToken.IsCancellationRequested)
            return Reject(invocation, ToolCallOutcome.CancelledBeforeExecution, "执行前已取消。");

        if (invocation.Arguments.ValueKind is JsonValueKind.Undefined)
            return Reject(invocation, ToolCallOutcome.RejectedInvalidArguments, "调用参数缺失。");

        var validation = ValidateArguments(registration.Descriptor.InputSchema, invocation.Arguments);
        if (validation is not null)
            return Reject(invocation, ToolCallOutcome.RejectedInvalidArguments, validation);

        ToolPermission permission;
        ToolExecutionPlan execution;
        try
        {
            permission = registration.Tool.DescribePermission(invocation.Arguments);
            execution = registration.Tool.DescribeExecution(invocation.Arguments);
        }
        catch (Exception ex)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed, Diagnostic: $"权限/执行计划描述失败：{ex.Message}");
        }

        // 阶段一：预检（不持锁）。审批绑定必须基于规范化参数与本次权限描述。
        var binding = ArgumentCanonicalizer.Bind(
            _sessionId, _turnId, invocation.InvocationId,
            registration.Descriptor, invocation.Arguments, permission,
            _workspace, _approvalValidity);

        if (_approver is not null)
        {
            // 等待审批期间不持任何锁：慢审批不应阻塞整个工作区。
            var decision = await _approver.RequestApprovalAsync(
                new ToolApprovalRequest(
                    _sessionId, _turnId, invocation.InvocationId,
                    registration.Descriptor, invocation.Arguments, permission,
                    execution, _workspace, binding),
                cancellationToken).ConfigureAwait(false);

            if (!decision.Approved)
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: decision.Reason ?? "审批未通过。",
                    Permission: permission);
        }

        // 阶段二：进锁后复核（防"批准 A、执行 B"）。
        using var lease = _gate is null
            ? null
            : await _gate.EnterAsync(execution, _workspace, cancellationToken).ConfigureAwait(false);

        if (!approvalIsStillValid(registration, invocation, permission, execution, binding, out var staleReason))
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.RejectedNotApproved,
                Diagnostic: staleReason, Permission: permission);

        try
        {
            var result = await registration.Tool.ExecuteAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Succeeded, result, Permission: permission);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.CancelledDuringExecution, Diagnostic: "执行中被取消。",
                Permission: permission);
        }
        catch (Exception ex)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed, Diagnostic: $"{ex.GetType().Name}: {ex.Message}",
                Permission: permission);
        }
    }

    /// <summary>
    /// 执行前复核：重新描述权限与执行计划，重算绑定，比对全部维度。
    /// 任一项变化即拒绝，要求重新审批。
    /// </summary>
    private static bool approvalIsStillValid(
        Registration registration, ToolInvocation invocation, ToolPermission approvedPermission,
        ToolExecutionPlan approvedExecution, ApprovalBinding approvedBinding, out string reason)
    {
        reason = string.Empty;

        if (!approvedBinding.IsUnexpired(DateTimeOffset.UtcNow))
        {
            reason = "审批已过期。";
            return false;
        }

        ToolPermission currentPermission;
        ToolExecutionPlan currentExecution;
        try
        {
            currentPermission = registration.Tool.DescribePermission(invocation.Arguments);
            currentExecution = registration.Tool.DescribeExecution(invocation.Arguments);
        }
        catch (Exception ex)
        {
            reason = $"复核时无法重新描述权限：{ex.Message}";
            return false;
        }

        // 只比较"批准了什么"，时刻由 IsUnexpired 单独判断。
        var current = approvedBinding with
        {
            ArgumentsDigest = ArgumentCanonicalizer.Digest(invocation.Arguments),
            PermissionScope = ArgumentCanonicalizer.DescribeScope(currentPermission),
        };

        if (!approvedBinding.Matches(current))
        {
            reason = "执行前复核不通过：参数、权限或资源计划与批准时不一致。";
            return false;
        }

        if (currentExecution.Concurrency != approvedExecution.Concurrency)
        {
            reason = "执行前复核不通过：并发要求与批准时不一致。";
            return false;
        }

        // 未知权限不能被当作已知放行。
        if (!currentPermission.IsKnown && approvedPermission.IsKnown)
        {
            reason = "执行前复核不通过：权限从已知变为未知。";
            return false;
        }

        return true;
    }

    private static ToolDispatchResult Reject(
        ToolInvocation invocation, ToolCallOutcome outcome, string diagnostic) =>
        new(invocation.InvocationId, invocation.ToolId, outcome, Diagnostic: diagnostic);

    /// <summary>
    /// 最小 JSON Schema 校验：只检查顶层 type 与 required。
    /// 完整校验器属于后续切片；这里刻意保持小，避免宣称尚未实现的能力。
    /// </summary>
    private static string? ValidateArguments(JsonElement schema, JsonElement arguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
            return "调用参数必须是 JSON 对象。";

        if (schema.TryGetProperty("type", out var type)
            && type.ValueKind is JsonValueKind.String
            && !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
            return $"工具参数 schema 的顶层类型必须是 object，实际为 '{type.GetString()}'。";

        if (!schema.TryGetProperty("required", out var required)
            || required.ValueKind is not JsonValueKind.Array)
            return null;

        foreach (var item in required.EnumerateArray())
        {
            var name = item.GetString();
            if (name is null) continue;
            if (!arguments.TryGetProperty(name, out _))
                return $"缺少必需参数 '{name}'。";
        }

        return null;
    }

    private sealed record Registration(IAgentTool Tool, ToolDescriptor Descriptor);

    private sealed class RegistrationHandle(ToolDispatcher owner, string toolId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) { owner._tools.Remove(toolId); }
        }
    }
}
