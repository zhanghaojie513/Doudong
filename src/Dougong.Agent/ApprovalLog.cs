using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 审批决定的持久化记录。
/// </summary>
/// <remarks>
/// 为什么要单独持久化：恢复会话时必须能回答"这个调用当初经过审批了吗"。
/// 只靠工具终态不够——<see cref="ToolCallOutcome.OutcomeUnknown"/> 的对账需要知道
/// 授权情况，否则只能得出"可能执行了，但不知道是否经过批准"。
/// <para>
/// 记录必须绑定到具体调用（<see cref="InvocationId"/>）与参数摘要
/// （<see cref="ArgumentsDigest"/>），否则无法证明"批准的是这一次调用"而
/// 不是同名的另一次。
/// </para>
/// </remarks>
public sealed record ApprovalRecord(
    string InvocationId,
    string ToolId,
    string ToolVersion,
    bool Approved,
    string? Reason,
    string ArgumentsDigest,
    string Workspace,
    DateTimeOffset DecidedAt)
{
    /// <summary>序列化为可存储的 JSON。</summary>
    public JsonElement ToJson()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            invocationId = InvocationId,
            toolId = ToolId,
            toolVersion = ToolVersion,
            approved = Approved,
            reason = Reason,
            argumentsDigest = ArgumentsDigest,
            workspace = Workspace,
            decidedAt = DecidedAt,
        }));
        return document.RootElement.Clone();
    }

    /// <summary>从日志负载还原；字段缺失或类型不符时返回 null。</summary>
    public static ApprovalRecord? FromJson(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object) return null;

        var invocationId = ReadString(payload, "invocationId");
        var toolId = ReadString(payload, "toolId");
        if (invocationId is null || toolId is null) return null;

        var approved = payload.TryGetProperty("approved", out var approvedElement)
            && approvedElement.ValueKind is JsonValueKind.True;

        var decidedAt = payload.TryGetProperty("decidedAt", out var atElement)
            && atElement.ValueKind is JsonValueKind.String
            && atElement.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new ApprovalRecord(
            invocationId,
            toolId,
            ReadString(payload, "toolVersion") ?? string.Empty,
            approved,
            ReadString(payload, "reason"),
            ReadString(payload, "argumentsDigest") ?? string.Empty,
            ReadString(payload, "workspace") ?? string.Empty,
            decidedAt);
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.String
            ? element.GetString()
            : null;
}

/// <summary>
/// 从会话日志读取审批决定，供恢复与对账使用。
/// </summary>
public static class ApprovalLog
{
    /// <summary>按提交顺序返回全部审批记录；无法解析的条目被跳过。</summary>
    /// <remarks>
    /// 跳过而不是抛错：日志里可能存在更早版本写的、字段不完整的记录。
    /// 一条坏记录不应让整个会话的审批历史变得不可读。
    /// </remarks>
    public static async Task<IReadOnlyList<ApprovalRecord>> ReadAsync(
        IAgentStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var results = new List<ApprovalRecord>();
        await foreach (var record in store.ReadCommittedAsync(cancellationToken))
        {
            if (record.Kind != StoreRecordKind.ApprovalDecision) continue;
            if (ApprovalRecord.FromJson(record.Payload) is { } approval) results.Add(approval);
        }

        return results;
    }

    /// <summary>
    /// 恢复时的授权视图：哪些调用被批准、哪些被拒绝。
    /// 未知状态必须与"拒绝"区分——没有记录不等于没批准过。
    /// </summary>
    public static ApprovalSnapshot Summarize(IReadOnlyList<ApprovalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var approved = new HashSet<string>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Approved) approved.Add(record.InvocationId);
            else denied.Add(record.InvocationId);
        }

        return new ApprovalSnapshot(approved, denied);
    }
}

/// <summary>
/// 一次恢复得到的授权视图。
/// </summary>
/// <remarks>
/// 用两个集合而不是布尔查询，是为了让"既没批准也没拒绝"这件事可表达：
/// 那才是 <see cref="ToolCallOutcome.OutcomeUnknown"/> 对账时最需要的信息。
/// </remarks>
public sealed record ApprovalSnapshot(
    IReadOnlySet<string> Approved,
    IReadOnlySet<string> Denied)
{
    public bool HasAnyRecord(string invocationId) =>
        Approved.Contains(invocationId) || Denied.Contains(invocationId);

    /// <summary>该调用是否有明确的批准记录。</summary>
    public bool WasApproved(string invocationId) => Approved.Contains(invocationId);

    /// <summary>该调用是否有明确的拒绝记录。</summary>
    public bool WasDenied(string invocationId) => Denied.Contains(invocationId);

    /// <summary>有调用记录但没有任何审批记录——恢复时必须提示人工核实。</summary>
    public bool IsUnaccounted(string invocationId) => !HasAnyRecord(invocationId);
}
