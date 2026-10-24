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
    /// <summary>
    /// 纯 ASCII 文本的字符/token 比，来自实测（DeepSeek deepseek-flash，2026-10-24）。
    /// </summary>
    /// <remarks>
    /// 实测线性拟合：<c>tokens = 30 + chars / 8</c>（50～12 800 字符区间）。
    /// 长文本约 8 字符/token，但短文本被固定开销主导（31 字符用 37 token）。
    /// <para>
    /// 这里取 4 而不是 8：这是**压缩触发**用的估算，宁可高估 token 数（更早压缩）
    /// 也不要低估（撞上 provider 上限才发现）。高估的代价是多压一点历史，
    /// 低估的代价是整个会话失败。
    /// </para>
    /// </remarks>
    public const int AsciiCharsPerToken = 4;

    /// <summary>
    /// CJK 字符/token 比，来自实测。中文实测约 1.2 字符/token。
    /// </summary>
    /// <remarks>
    /// 取 1 是保守值：把每个 CJK 字符都当成至少一个 token。
    /// </remarks>
    public const int CjkCharsPerToken = 1;

    /// <summary>每条消息的固定开销（角色标记、分隔符等），来自实测的截距。</summary>
    public const int PerMessageOverheadTokens = 4;

    /// <summary>
    /// 估算一段文本的 token 数，区分 CJK 与其它字符。
    /// </summary>
    /// <remarks>
    /// 之前所有文本一律按 2 字符/token 估算。实测表明这个值在两种方向上都会错：
    /// ASCII 长文本真实约 8 字符/token（高估 4 倍，导致过早压缩），
    /// CJK 约 1.2 字符/token（略低估）。按字符类别分开估算是唯一能把两者都覆盖的做法。
    /// </remarks>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        foreach (var character in text)
            if (IsCjk(character)) cjk++;

        var other = text.Length - cjk;
        var tokens = CjkDiv(cjk, CjkCharsPerToken) + AsciiDiv(other, AsciiCharsPerToken);
        return Math.Max(1, tokens);
    }

    /// <summary>估算整段历史的 token 数，计入每条消息的固定开销。</summary>
    public static int Estimate(IEnumerable<AgentMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += PerMessageOverheadTokens + Estimate(message.Content);
            // 工具调用的请求本身也占上下文（id、工具名、参数）。
            foreach (var call in message.ToolCalls)
                total += Estimate(call.ToolId) + Estimate(call.Arguments.GetRawText()) + 8;
        }

        return total;
    }

    private static int CjkDiv(int count, int perToken) =>
        count == 0 ? 0 : (count + perToken - 1) / perToken;

    private static int AsciiDiv(int count, int perToken) =>
        count == 0 ? 0 : (count + perToken - 1) / perToken;

    /// <summary>
    /// 是否属于 CJK 及其相邻文字系统（中日韩、全角符号）。
    /// </summary>
    /// <remarks>
    /// 只按码位范围判断，不做语言识别：目的是把"分词粒度粗"的字符
    /// 与"分词粒度细"的拉丁字符分开估算，粗分类已足够。
    /// </remarks>
    private static bool IsCjk(char character) => character switch
    {
        >= '一' and <= '鿿' => true,   // CJK 统一表意文字
        >= '㐀' and <= '䶿' => true,   // 扩展 A
        >= '぀' and <= 'ヿ' => true,   // 日文假名
        >= '가' and <= '힯' => true,   // 韩文音节
        >= '豈' and <= '﫿' => true,   // 兼容表意文字
        >= '＀' and <= '￯' => true,   // 全角符号
        >= '　' and <= '〿' => true,   // CJK 标点
        _ => false,
    };

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
