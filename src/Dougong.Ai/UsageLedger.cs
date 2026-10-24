using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 一次模型调用的用量记录。字段与 <see cref="UsageDetails"/> 对齐，
/// 但不引用 provider 类型，便于序列化与长期保存。
/// </summary>
/// <remarks>
/// <see cref="Reported"/> 为 false 表示 provider 没有给出 usage。
/// 这种情况必须如实记录为"未知"，不能用 0 冒充——0 会被读成"这次调用没花钱"。
/// </remarks>
public sealed record UsageRecord(
    string Provider,
    string Model,
    ChatProtocol Protocol,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    TimeSpan Elapsed,
    bool Succeeded,
    string? Failure)
{
    /// <summary>provider 是否真的报告了用量。</summary>
    public bool Reported => InputTokens is not null || OutputTokens is not null;

    /// <summary>未报告时返回 null，让调用方区分"零消耗"与"不知道"。</summary>
    public long? EffectiveTotal => TotalTokens
        ?? (InputTokens is { } input && OutputTokens is { } output ? input + output : null);
}

/// <summary>
/// 按会话与自然日聚合用量。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §5.1）：
/// <list type="bullet">
/// <item>per-session / per-day 聚合</item>
/// <item>本层只汇总 token 与调用次数，不猜单价——成本依赖 provider 计价，
/// 金额由宿主用已知价目表换算</item>
/// <item>失败调用单独计数：失败也可能计费，不能只统计成功</item>
/// </list>
/// <para>
/// 同批多工具、多会话都可能并发追加，因此所有读写都在同一把锁内完成。
/// </para>
/// </remarks>
public sealed class UsageLedger
{
    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly Func<DateTimeOffset> _clock;

    public UsageLedger(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>追加一条记录。</summary>
    public void Add(string sessionId, UsageRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync) _entries.Add(new Entry(sessionId, record, _clock()));
    }

    /// <summary>全部记录的快照，按加入顺序。</summary>
    public IReadOnlyList<UsageRecord> Records
    {
        get { lock (_sync) return [.. _entries.Select(e => e.Record)]; }
    }

    /// <summary>按会话汇总。</summary>
    public UsageSummary Summarize(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Snapshot(sessionId, null);
    }

    /// <summary>按自然日（本地时区）汇总。</summary>
    public UsageSummary SummarizeDay(DateOnly day) => Snapshot(null, day);

    /// <summary>全部会话的汇总。</summary>
    public UsageSummary SummarizeAll() => Snapshot(null, null);

    private UsageSummary Snapshot(string? sessionId, DateOnly? day)
    {
        List<UsageRecord> records;
        lock (_sync)
        {
            records = [.. _entries
                .Where(e => sessionId is null || e.SessionId == sessionId)
                .Where(e => day is null || DateOnly.FromDateTime(e.At.LocalDateTime) == day)
                .Select(e => e.Record)];
        }

        return UsageSummary.From(records);
    }

    private sealed record Entry(string SessionId, UsageRecord Record, DateTimeOffset At);
}

/// <summary>
/// 聚合结果。
/// </summary>
/// <remarks>
/// <see cref="UnreportedCalls"/> 存在的意义：如果 provider 从不报 usage，
/// 一个"共 0 tokens"的汇总会严重误导。调用方必须能看到有多少次调用没有数据。
/// </remarks>
public sealed record UsageSummary(
    int Calls,
    int FailedCalls,
    int UnreportedCalls,
    long InputTokens,
    long OutputTokens,
    long? TotalTokens,
    TimeSpan TotalElapsed)
{
    /// <summary>provider 报告的用量覆盖率，用于判断汇总是否可信。</summary>
    public double ReportedRatio => Calls == 0 ? 1 : (double)(Calls - UnreportedCalls) / Calls;

    public static UsageSummary Empty { get; } =
        new(0, 0, 0, 0, 0, 0, TimeSpan.Zero);

    internal static UsageSummary From(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0) return Empty;

        long input = 0, output = 0, total = 0;
        var anyTotal = false;
        var unreported = 0;
        var failed = 0;
        var elapsed = TimeSpan.Zero;

        foreach (var record in records)
        {
            elapsed += record.Elapsed;
            if (!record.Succeeded) failed++;

            if (!record.Reported)
            {
                unreported++;
                continue;
            }

            input += record.InputTokens ?? 0;
            output += record.OutputTokens ?? 0;
            if (record.TotalTokens is { } explicitTotal)
            {
                total += explicitTotal;
                anyTotal = true;
            }
            else if (record.InputTokens is { } i && record.OutputTokens is { } o)
            {
                total += i + o;
                anyTotal = true;
            }
        }

        return new UsageSummary(
            records.Count, failed, unreported, input, output,
            // 只要有一次调用给出了总量，汇总总量就是已知的；全都没给才是未知。
            anyTotal ? total : null, elapsed);
    }
}

/// <summary>
/// 给 <see cref="IChatClient"/> 加一层用量记录，不改变任何调用语义。
/// </summary>
/// <remarks>
/// 用装饰器而不是在循环里记账：usage 是连接器观察到的协议事实，
/// 让循环去解析会把协议知识漏进运行时层。装饰器也能覆盖将来接入的
/// 任何 <see cref="IChatClient"/>（官方、社区、MCP 桥接）。
/// <para>
/// 失败与取消都记一条并保留已耗时：取消前可能已经消耗了输入 token，
/// 只记录成功会让账目系统性地偏低。
/// </para>
/// </remarks>
public sealed class UsageRecordingChatClient(
    IChatClient inner,
    UsageLedger ledger,
    string sessionId,
    string provider,
    string model,
    ChatProtocol protocol,
    Func<DateTimeOffset>? clock = null) : IChatClient
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var started = _clock();
        try
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            Record(started, response.Usage, succeeded: true, failure: null);
            return response;
        }
        catch (Exception ex)
        {
            Record(started, null, succeeded: false, failure: Describe(ex));
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = _clock();
        UsageDetails? usage = null;

        await using var enumerator = inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                // 流中断：已收到的 usage 仍算数（有些 provider 在终结帧前就给出）。
                Record(started, usage, succeeded: false, failure: Describe(ex));
                throw;
            }

            foreach (var content in current.Contents)
            {
                if (content is UsageContent usageContent) usage = usageContent.Details;
            }

            yield return current;
        }

        Record(started, usage, succeeded: true, failure: null);
    }

    private void Record(
        DateTimeOffset started, UsageDetails? usage, bool succeeded, string? failure)
    {
        var elapsed = _clock() - started;

        ledger.Add(sessionId, new UsageRecord(
            provider, model, protocol,
            usage?.InputTokenCount,
            usage?.OutputTokenCount,
            usage?.TotalTokenCount,
            elapsed,
            succeeded,
            failure));

        // 同一份事实同时投递到标准计量出口。标签只放低基数维度：
        // provider / model / protocol / result。会话 ID 属于日志，不属于指标。
        var tags = new TagList
        {
            { DougongTelemetry.ProviderTag, provider },
            { DougongTelemetry.ModelTag, model },
            { DougongTelemetry.ProtocolTag, protocol.ToString() },
            { DougongTelemetry.ResultTag, succeeded ? "success" : "failure" },
        };

        DougongTelemetry.Calls.Add(1, tags);
        DougongTelemetry.CallDuration.Record(elapsed.TotalMilliseconds, tags);

        // 未报告与零消耗必须可区分：前者计到单独的计数器，不记 token。
        if (usage?.InputTokenCount is null && usage?.OutputTokenCount is null)
        {
            DougongTelemetry.UnreportedCalls.Add(1, tags);
            return;
        }

        if (usage.InputTokenCount is { } input)
            DougongTelemetry.InputTokens.Record(input, tags);
        if (usage.OutputTokenCount is { } output)
            DougongTelemetry.OutputTokens.Record(output, tags);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
