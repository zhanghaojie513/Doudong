using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dougong.Agent;

/// <summary>
/// 审批器。由宿主实现（控制台、桌面弹窗、自动白名单）。
/// 返回拒绝即不执行；返回批准也不能跳过执行前的复核。
/// </summary>
public interface IToolApprover
{
    ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>审批结论。批准携带凭据，执行前必须用凭据复核。</summary>
public sealed record ToolApprovalDecision(bool Approved, string? Reason = null)
{
    public static ToolApprovalDecision Allow(string? reason = null) => new(true, reason);
    public static ToolApprovalDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// 一次审批请求。<see cref="Binding"/> 是完整绑定摘要，批准只对这一个摘要有效。
/// </summary>
public sealed record ToolApprovalRequest(
    string SessionId,
    string TurnId,
    string InvocationId,
    ToolDescriptor Tool,
    JsonElement NormalizedArguments,
    ToolPermission Permission,
    ToolExecutionPlan Execution,
    string Workspace,
    ApprovalBinding Binding);

/// <summary>
/// 审批绑定：把"批准的是什么"固定下来。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.3）：凭据绑定 session/turn/invocation ID、
/// 工具 ID/版本、规范化参数摘要、工作区、授权范围与有效期。
/// 任意一项变化都必须重新审批——不能"批准 A、执行 B"。
/// <para>
/// 插件激活代次在本层以 <see cref="ToolDescriptor.Version"/> 与工具来源标识表达；
/// 接入内核时由宿主把代次并入版本字符串，避免 Agent 层反向依赖 Kernel。
/// </para>
/// </remarks>
public sealed record ApprovalBinding(
    string SessionId,
    string TurnId,
    string InvocationId,
    string ToolId,
    string ToolVersion,
    string ArgumentsDigest,
    string Workspace,
    string PermissionScope,
    DateTimeOffset ExpiresAt,
    string FileVersions = "")
{
    /// <summary>把绑定折叠成一个稳定字符串（不含时刻），用于比较与审计。</summary>
    public string ToKey() => string.Join('|',
        SessionId, TurnId, InvocationId, ToolId, ToolVersion,
        ArgumentsDigest, Workspace, PermissionScope, FileVersions);

    /// <summary>
    /// 凭据是否仍适用于当前这次调用。比较的是"批准了什么"，
    /// 不含时刻——过期由调用方单独判断，否则每次重算时间戳都会导致永不匹配。
    /// </summary>
    public bool Matches(ApprovalBinding current) =>
        string.Equals(ToKey(), current.ToKey(), StringComparison.Ordinal);

    /// <summary>是否仍在有效期内。</summary>
    public bool IsUnexpired(DateTimeOffset now) => ExpiresAt > now;
}

/// <summary>规范化参数与摘要。同一逻辑参数必须得到同一摘要，不受字段顺序影响。</summary>
public static class ArgumentCanonicalizer
{
    /// <summary>
    /// 递归按字段名排序后输出紧凑 JSON，再取 SHA-256。
    /// 用途是让"参数变了就必须重新审批"成为可比较的事实。
    /// </summary>
    public static string Digest(JsonElement arguments)
    {
        var canonical = Canonicalize(arguments);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    /// <summary>输出按字段名排序的紧凑 JSON。</summary>
    public static string Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",",
            element.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{JsonSerializer.Serialize(p.Name)}:{Canonicalize(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",",
            element.EnumerateArray().Select(Canonicalize)) + "]",
        JsonValueKind.Undefined => "null",
        _ => element.GetRawText(),
    };

    /// <summary>构造绑定。有效期与工作区由调用方给出，本方法不做策略判断。</summary>
    public static ApprovalBinding Bind(
        string sessionId, string turnId, string invocationId,
        ToolDescriptor tool, JsonElement arguments, ToolPermission permission,
        string workspace, TimeSpan validity)
    {
        return new ApprovalBinding(
            sessionId, turnId, invocationId,
            tool.Id, tool.Version,
            Digest(arguments),
            workspace,
            DescribeScope(permission),
            DateTimeOffset.UtcNow.Add(validity));
    }

    /// <summary>把权限折叠为稳定字符串。未知权限必须显式标注，不能当成 None。</summary>
    public static string DescribeScope(ToolPermission permission)
    {
        if (!permission.IsKnown) return "unknown";
        var parts = new List<string> { permission.Access.ToString() };
        if (permission.Paths.Count > 0) parts.Add("paths=" + string.Join(",", permission.Paths.Order()));
        if (permission.Commands.Count > 0) parts.Add("cmd=" + string.Join(",", permission.Commands.Order()));
        if (permission.Domains.Count > 0) parts.Add("domains=" + string.Join(",", permission.Domains.Order()));
        return string.Join(";", parts);
    }
}
