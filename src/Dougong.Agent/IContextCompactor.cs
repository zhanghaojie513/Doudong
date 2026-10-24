using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 上下文压缩契约。压缩只处理**已提交的一致历史切面**，且自身是可恢复的长任务。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.5）：
/// <list type="bullet">
/// <item>预算包含系统指令、历史、工具 schema、输出余量与估算误差；
/// <c>effective</c> 扣除输出/安全余量后必须为正</item>
/// <item>压缩只处理已提交的一致切面，保留进行中工具调用与其结果的配对</item>
/// <item>摘要版本与来源区间**提交成功后**才替换上下文；原日志保留</item>
/// <item>失败不得覆盖原历史；预算仍不足则暂停并提示，不强行截断</item>
/// </list>
/// <para>
/// 之所以把压缩建模成带检查点的任务而非同步管道钩子：真实 provider 上，
/// 摘要本身是一次模型调用，可能失败、超时或被取消。把它做成同步钩子会让
/// 一次失败的摘要污染整个 turn。
/// </para>
/// </remarks>
public interface IContextCompactor
{
    /// <summary>
    /// 判断是否需要压缩，并给出计划。返回 <c>null</c> 表示当前预算足够。
    /// 本方法不应产生任何副作用。
    /// </summary>
    CompactionPlan? Plan(CompactionInput input);
}

/// <summary>压缩输入：已提交的历史切面与预算参数。</summary>
public sealed record CompactionInput(
    IReadOnlyList<AgentMessage> CommittedHistory,
    int ContextWindowTokens,
    int MaxOutputTokens,
    int EstimateErrorTokens = 512);

/// <summary>
/// 上下文预算。宿主按所用模型的上下文窗口构造，循环据此判断是否需要压缩。
/// </summary>
/// <remarks>
/// 与 <see cref="CompactionInput"/> 分开的原因：预算是"这个模型能装多少"，
/// 而 <see cref="CompactionInput"/> 是"这一次请求要压什么"。把两者混在一起，
/// 会让每次压缩都重新声明模型能力，容易漂移。
/// <para>
/// <see cref="OverheadTokens"/> 预留给工具声明与系统指令——它们不进
/// <c>CommittedHistory</c>，但确实占用上下文。不预留会系统性低估。
/// </para>
/// </remarks>
public sealed record ContextLimits
{
    public required int ContextWindowTokens { get; init; }

    public required int MaxOutputTokens { get; init; }

    /// <summary>估算误差与工具声明/系统指令的预留量。</summary>
    public int OverheadTokens { get; init; } = 512;

    /// <summary>构造压缩输入；历史在调用时给出。</summary>
    public CompactionInput ToInput(IReadOnlyList<AgentMessage> history) =>
        new(history, ContextWindowTokens, MaxOutputTokens, OverheadTokens);

    /// <summary>有效输入预算，必须为正；否则配置本身不可行。</summary>
    public int EffectiveInputTokens =>
        TokenBudget.EffectiveInput(ContextWindowTokens, MaxOutputTokens, OverheadTokens);
}

/// <summary>
/// 压缩计划。第一阶段（Select）的产物：要压缩哪个区间、保留哪些内容。
/// </summary>
public sealed record CompactionPlan(
    int FirstIndex,
    int LastIndex,
    int PreservedHeadCount,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter,
    string Reason);

/// <summary>
/// 可恢复的压缩任务。每次调用推进一个阶段，直到 <see cref="CompactionPhase.Completed"/>
/// 或 <see cref="CompactionPhase.Failed"/>；中间状态可序列化以便崩溃后续跑。
/// </summary>
public interface ICompactionTask
{
    /// <summary>当前阶段与检查点，可序列化。</summary>
    CompactionCheckpoint Checkpoint { get; }

    /// <summary>推进一个阶段。返回新的检查点。</summary>
    ValueTask<CompactionCheckpoint> AdvanceAsync(CancellationToken cancellationToken = default);
}

/// <summary>压缩阶段。对应 pi.net 的 select → summarize → retry → done。</summary>
public enum CompactionPhase
{
    /// <summary>选择要压缩的区间。</summary>
    Select,

    /// <summary>调用模型生成摘要。</summary>
    Summarize,

    /// <summary>摘要失败后的重试。</summary>
    Retry,

    /// <summary>已完成，可以提交替换。</summary>
    Completed,

    /// <summary>无法恢复的失败；原历史必须保留。</summary>
    Failed,
}

/// <summary>
/// 压缩检查点。可序列化为 JSON，崩溃后续跑时不丢进度。
/// <see cref="TailIndex"/> 标记"截至哪条记录"被压缩，避免压缩期间新提交的内容被误覆盖。
/// </summary>
public sealed record CompactionCheckpoint(
    CompactionPhase Phase,
    int Attempt,
    int FirstIndex,
    int LastIndex,
    int PreservedHeadCount,
    int TailIndex,
    string? Summary,
    string? FailureReason)
{
    public bool IsTerminal => Phase is CompactionPhase.Completed or CompactionPhase.Failed;

    /// <summary>序列化为可存储的 JSON，供崩溃恢复。</summary>
    public JsonElement ToJson()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            phase = Phase.ToString(),
            attempt = Attempt,
            firstIndex = FirstIndex,
            lastIndex = LastIndex,
            preservedHead = PreservedHeadCount,
            tailIndex = TailIndex,
            summary = Summary,
            failure = FailureReason,
        }));
        return document.RootElement.Clone();
    }
}

/// <summary>
/// token 预算计算。刻意保持保守：无法精确计数时宁可少算可用空间。
/// </summary>
/// <remarks>
/// 这里用字符数近似 token（约 4 字符/token），是**估算**而非真实计数。
/// 设计文档要求"按真实 token 计数验证后配置"，当前实现没有接入 provider 的
/// 计数器，因此阈值必须保守，且不能在文档里宣称精确。
/// </remarks>
public static class TokenBudget
{
    /// <summary>粗略估算：英文约 4 字符/token，CJK 约 1 字符/token，取保守值 2。</summary>
    public const int CharsPerToken = 2;

    /// <summary>估算一段文本的 token 数。</summary>
    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <summary>估算整段历史的 token 数。</summary>
    public static int Estimate(IEnumerable<AgentMessage> messages) =>
        messages.Sum(m => Estimate(m.Content));

    /// <summary>
    /// 计算可用于输入的有效预算：上下文窗口扣除输出余量与估算误差。
    /// 结果必须为正；否则说明配置本身不可行，应暂停而不是硬压。
    /// </summary>
    public static int EffectiveInput(int contextWindowTokens, int maxOutputTokens, int estimateErrorTokens)
    {
        var effective = contextWindowTokens - maxOutputTokens - estimateErrorTokens;
        return effective > 0
            ? effective
            : throw new ArgumentOutOfRangeException(
                nameof(contextWindowTokens),
                $"有效输入预算为 {effective}：上下文窗口 {contextWindowTokens} 不足以容纳输出 {maxOutputTokens} 与误差 {estimateErrorTokens}。");
    }
}
