using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// <see cref="IToolDispatcher"/> 的首版实现。它是唯一执行工具的地方：
/// 校验参数、去重调用 ID、采集权限描述、请求审批、协调执行权、复核后执行并记录终态。
/// </summary>
/// <remarks>
/// 能力边界（与 docs/m1-candidate-comparison.md 一致）：
/// 资源协调只有两级——全局串行 + 按工作区独占，尚无资源级读写键与跨工作区仲裁。
/// schema 校验只覆盖顶层 type 与 required，不是完整校验器。
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
    private readonly WorkspacePathGuard? _pathGuard;

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
        TimeSpan? approvalValidity = null,
        WorkspacePathGuard? pathGuard = null,
        IAgentStore? store = null)
    {
        _approver = approver;
        _gate = gate;
        _sessionId = sessionId;
        _turnId = turnId;
        _workspace = workspace;
        _approvalValidity = approvalValidity ?? TimeSpan.FromMinutes(5);
        _pathGuard = pathGuard;
        _store = store;
    }

    private readonly IAgentStore? _store;

    private readonly Dictionary<string, Registration> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration> _aliases = new(StringComparer.Ordinal);
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
        var registration = new Registration(tool, frozen);
        lock (_sync)
        {
            if (!_tools.TryAdd(descriptor.Id, registration))
                throw new InvalidOperationException($"工具 '{descriptor.Id}' 已注册。");

            // 模型只看得见 Name（见 ToolDeclarations），回传的调用里自然也是 Name。
            // 因此 Name 必须也能路由到同一个注册项，否则"工具已注册却被判未注册"。
            // Id 与 Name 相同时不额外占用键；不同名时保留 Id 优先。
            if (!string.Equals(descriptor.Name, descriptor.Id, StringComparison.Ordinal)
                && !_aliases.TryAdd(descriptor.Name, registration))
            {
                _tools.Remove(descriptor.Id);
                throw new InvalidOperationException(
                    $"工具名 '{descriptor.Name}' 与其他工具冲突（Id 为 '{descriptor.Id}'）。");
            }
        }

        return new RegistrationHandle(this, descriptor.Id, descriptor.Name);
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
            // 先按 Id 找（宿主内部派发），再按 Name 找（模型回传只看得到 Name）。
            if (!_tools.TryGetValue(invocation.ToolId, out registration))
                _aliases.TryGetValue(invocation.ToolId, out registration);
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
        FileVersionSet files;
        try
        {
            permission = registration.Tool.DescribePermission(invocation.Arguments);
            execution = registration.Tool.DescribeExecution(invocation.Arguments);
            files = CaptureFileVersions(registration, invocation);
        }
        catch (Exception ex)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed, Diagnostic: $"权限/执行计划/文件依赖描述失败：{ex.Message}");
        }

        // 阶段一：预检（不持锁）。审批绑定必须基于规范化参数与本次权限描述。
        var binding = ArgumentCanonicalizer.Bind(
            _sessionId, _turnId, invocation.InvocationId,
            registration.Descriptor, invocation.Arguments, permission,
            _workspace, _approvalValidity) with
        {
            FileVersions = files.ToBindingValue(),
        };

        if (_approver is not null)
        {
            // 等待审批期间不持任何锁：慢审批不应阻塞整个工作区。
            var decision = await _approver.RequestApprovalAsync(
                new ToolApprovalRequest(
                    _sessionId, _turnId, invocation.InvocationId,
                    registration.Descriptor, invocation.Arguments, permission,
                    execution, _workspace, binding),
                cancellationToken).ConfigureAwait(false);

            // 审批决定必须落盘后再继续：否则"已批准"这件事没有被持久化，
            // 而副作用却发生了，恢复时无法对账。
            if (await CommitApprovalAsync(
                    registration, invocation, decision, binding, cancellationToken)
                    .ConfigureAwait(false) is { } approvalFailure)
            {
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.Failed,
                    Diagnostic: $"审批记录提交失败，未执行：{approvalFailure}",
                    Permission: permission);
            }

            if (!decision.Approved)
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: decision.Reason ?? "审批未通过。",
                    Permission: permission);
        }

        // 阶段二：进锁后复核（防"批准 A、执行 B"）。
        // 等待执行权时被取消属于"尚未执行"，必须返回终态而不是把取消异常抛给调用方。
        IDisposable? lease;
        try
        {
            lease = _gate is null
                ? null
                : await _gate.EnterAsync(execution, _workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.CancelledBeforeExecution,
                Diagnostic: "等待执行权时被取消，未执行。", Permission: permission);
        }
        catch (ObjectDisposedException ex)
        {
            // 闸门已被宿主释放：不执行，但不能把异常抛给调用方而丢掉这条调用的终态。
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed,
                Diagnostic: $"资源闸门已释放，未执行：{ex.ObjectName}", Permission: permission);
        }

        using (lease)
        {
            // 复核的第一件事：这个注册是否仍是当前生效的那一个。
            // 审批窗口内工具可能被注销并替换（MCP 的 list_changed 随时会到），
            // 此时手中的 registration 是过期对象——继续用它会"批准了 A、执行了 B"。
            if (!IsCurrentRegistration(registration, invocation.ToolId))
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: "执行前复核不通过：该工具在审批期间已被注销或替换，请重新审批。",
                    Permission: permission);

            if (!approvalIsStillValid(registration, invocation, permission, execution, binding, out var staleReason))
                return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                    ToolCallOutcome.RejectedNotApproved,
                    Diagnostic: staleReason, Permission: permission);

            try
            {
                var result = await registration.Tool.ExecuteAsync(invocation, cancellationToken)
                    .ConfigureAwait(false);

                // 取消是协作式的：工具若忽略取消并完成，就必须如实报成功。
                // 伪装成"已撤销"会让上层误判副作用没发生。
                return cancellationToken.IsCancellationRequested
                    ? new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                        ToolCallOutcome.Succeeded, result,
                        Diagnostic: "取消已请求，但工具未响应取消并完成了执行；副作用可能已发生。",
                        Permission: permission)
                    : new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
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
    }

    /// <summary>
    /// 执行前复核：重新描述权限与执行计划，重算绑定，比对全部维度。
    /// 任一项变化即拒绝，要求重新审批。
    /// </summary>
    /// <summary>
    /// 捕获工具声明的文件依赖版本。路径必须先经工作区守卫解析，
    /// 越界直接抛错——宁可拒绝调用，也不对越界路径做指纹。
    /// </summary>
    private FileVersionSet CaptureFileVersions(Registration registration, ToolInvocation invocation)
    {
        var declared = registration.Tool.DescribeFileDependencies(invocation.Arguments);
        if (declared.Count == 0) return FileVersionSet.Empty;
        if (_pathGuard is null)
            throw new InvalidOperationException(
                "工具声明了文件依赖，但 dispatcher 未配置工作区路径守卫。");

        var resolved = new List<string>(declared.Count);
        foreach (var path in declared)
        {
            if (!_pathGuard.TryResolve(path, out var absolute, out var why))
                throw new UnauthorizedAccessException($"文件依赖路径被拒绝：{path}（{why}）");
            resolved.Add(absolute);
        }

        return FileVersionSet.Capture(resolved);
    }

    private bool approvalIsStillValid(
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
        FileVersionSet currentFiles;
        try
        {
            currentFiles = CaptureFileVersions(registration, invocation);
        }
        catch (Exception ex)
        {
            reason = $"复核时无法重新捕获文件版本：{ex.Message}";
            return false;
        }

        var current = approvedBinding with
        {
            ArgumentsDigest = ArgumentCanonicalizer.Digest(invocation.Arguments),
            PermissionScope = ArgumentCanonicalizer.DescribeScope(currentPermission),
            FileVersions = currentFiles.ToBindingValue(),
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

    /// <summary>
    /// 提交一条审批决定。返回 null 表示已提交（或无需提交），非空为失败原因。
    /// </summary>
    /// <remarks>
    /// 记录里带上参数摘要：只记"调用 c1 被批准"不足以证明批准的是**这一次**参数，
    /// 同名工具换参数再跑一次就会共享同一条授权。
    /// </remarks>
    private async ValueTask<string?> CommitApprovalAsync(
        Registration registration,
        ToolInvocation invocation,
        ToolApprovalDecision decision,
        ApprovalBinding binding,
        CancellationToken cancellationToken)
    {
        if (_store is null) return null;

        var record = new ApprovalRecord(
            invocation.InvocationId,
            registration.Descriptor.Id,
            registration.Descriptor.Version,
            decision.Approved,
            decision.Reason,
            binding.ArgumentsDigest,
            _workspace,
            DateTimeOffset.UtcNow);

        try
        {
            await _store.CommitAsync(
                new StoreRecord(0, StoreRecordKind.ApprovalDecision, record.ToJson()),
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 该注册是否仍是 dispatcher 中"当前生效"的那一个。
    /// </summary>
    /// <remarks>
    /// 只比较引用是否相同：同名工具被重新注册后是**另一个** Registration 对象，
    /// 即使 schema 未变也必须重新审批——否则无法证明用户批准的是当前这一份实现。
    /// </remarks>
    private bool IsCurrentRegistration(Registration registration, string toolId)
    {
        lock (_sync)
        {
            if (_tools.TryGetValue(toolId, out var current) && ReferenceEquals(current, registration))
                return true;
            return _aliases.TryGetValue(toolId, out var alias) && ReferenceEquals(alias, registration);
        }
    }

    private static ToolDispatchResult Reject(
        ToolInvocation invocation, ToolCallOutcome outcome, string diagnostic) =>
        new(invocation.InvocationId, invocation.ToolId, outcome, Diagnostic: diagnostic);

    /// <summary>
    /// 按 schema 校验参数。委托给 <see cref="ToolSchemaValidator"/>：
    /// 校验范围与"不支持的关键字必须报错"的立场都定义在那里。
    /// </summary>
    /// <remarks>
    /// 曾经只检查顶层 type 与 required。M2 接入 MCP 之后不再够用——
    /// schema 来自不可信远端，只查 required 意味着它声明的类型约束形同虚设。
    /// </remarks>
    private static string? ValidateArguments(JsonElement schema, JsonElement arguments) =>
        ToolSchemaValidator.Validate(schema, arguments);

    private sealed record Registration(IAgentTool Tool, ToolDescriptor Descriptor);

    private sealed class RegistrationHandle(ToolDispatcher owner, string toolId, string toolName) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync)
            {
                owner._tools.Remove(toolId);
                if (!string.Equals(toolName, toolId, StringComparison.Ordinal))
                    owner._aliases.Remove(toolName);
            }
        }
    }
}
