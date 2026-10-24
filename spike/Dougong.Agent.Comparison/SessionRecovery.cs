using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>从已提交记录恢复会话状态，并对账出结果未知的工具调用。</summary>
public static class SessionRecovery
{
    /// <summary>
    /// 重放已提交记录。有 <see cref="StoreRecordKind.ToolCallStarted"/> 但无对应
    /// <see cref="StoreRecordKind.ToolCallCompleted"/> 的调用，按
    /// <see cref="ToolCallOutcome.OutcomeUnknown"/> 返回——绝不自动重放。
    /// </summary>
    public static async Task<RecoveredSession> ReplayAsync(
        IAgentStore store, CancellationToken cancellationToken = default)
    {
        var started = new List<(string Id, string Tool)>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var history = new List<string>();
        var truncated = false;
        long lastSequence = 0;

        await foreach (var record in store.ReadCommittedAsync(cancellationToken))
        {
            // 序号必须严格递增；出现缺口说明日志被破坏，不能静默跳过。
            if (record.Sequence != lastSequence + 1)
                truncated = true;
            lastSequence = record.Sequence;

            var payload = record.Payload.ValueKind is JsonValueKind.String
                ? record.Payload.GetString()
                : record.Payload.GetRawText();

            switch (record.Kind)
            {
                case StoreRecordKind.UserInput:
                    if (payload is { Length: > 0 }) history.Add($"user: {payload}");
                    break;

                case StoreRecordKind.ToolCallStarted:
                    if (Parse(payload) is { } s) started.Add((s.Id, s.Tool));
                    break;

                case StoreRecordKind.ToolCallCompleted:
                    if (Parse(payload) is { } c) completed.Add(c.Id);
                    break;
            }
        }

        var unknown = started
            .Where(s => !completed.Contains(s.Id))
            .Select(s => new UnknownOutcome(
                s.Id, s.Tool,
                "已提交调用开始但没有终态；无法确认副作用是否发生。"))
            .ToList();

        return new RecoveredSession(lastSequence, history, unknown, truncated);
    }

    private static (string Id, string Tool)? Parse(string? payload)
    {
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var tool = root.TryGetProperty("tool", out var toolElement) ? toolElement.GetString() : null;
            return id is null || tool is null ? null : (id, tool);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>恢复结果。<see cref="Unknown"/> 非空时宿主必须暂停该 turn。</summary>
public sealed record RecoveredSession(
    long LastSequence,
    IReadOnlyList<string> History,
    IReadOnlyList<UnknownOutcome> Unknown,
    bool HasSequenceGap)
{
    public bool RequiresReconciliation => Unknown.Count > 0;
}
