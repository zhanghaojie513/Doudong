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
        try
        {
            permission = registration.Tool.DescribePermission(invocation.Arguments);
        }
        catch (Exception ex)
        {
            return new ToolDispatchResult(invocation.InvocationId, invocation.ToolId,
                ToolCallOutcome.Failed, Diagnostic: $"权限描述失败：{ex.Message}");
        }

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
