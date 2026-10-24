using System.Diagnostics.Metrics;

namespace Dougong.Ai;

/// <summary>
/// Dougong 的指标出口。基于 <see cref="Meter"/>（BCL 自带），不直接依赖 OpenTelemetry 包。
/// </summary>
/// <remarks>
/// 为什么用 <see cref="Meter"/> 而不是直接引 <c>OpenTelemetry</c>：
/// <c>System.Diagnostics.Metrics</c> 是 .NET 内置的标准计量 API，
/// 而 <c>OpenTelemetry</c> 是它的一个消费者。SDK 只需要把指标发布到标准出口，
/// 谁来采集由宿主决定——这样 SDK 不必把某个可观测后端变成自己的依赖。
/// <para>
/// 名字与单位一旦发布就是**对外契约**：后端仪表盘、告警规则都依赖它们。
/// 因此集中在这里定义，不散落在调用点。
/// </para>
/// <para>
/// 标签（tags）刻意保持低基数：只有 provider、model、protocol、result。
/// 会话 ID、调用 ID 这类高基数维度**不放指标**——那会让后端的时间序列数量爆炸。
/// 会话级归属属于日志或事件，不属于指标。
/// </para>
/// </remarks>
public static class DougongTelemetry
{
    /// <summary>meter 名称。宿主按此名订阅。</summary>
    public const string MeterName = "Dougong.Ai";

    /// <summary>meter 版本，便于后端区分格式变化。</summary>
    public const string MeterVersion = "0.1.0";

    public const string InputTokensInstrument = "dougong.llm.input_tokens";
    public const string OutputTokensInstrument = "dougong.llm.output_tokens";
    public const string CallsInstrument = "dougong.llm.calls";
    public const string UnreportedCallsInstrument = "dougong.llm.unreported_calls";
    public const string CallDurationInstrument = "dougong.llm.duration";

    public const string ProviderTag = "provider";
    public const string ModelTag = "model";
    public const string ProtocolTag = "protocol";
    public const string ResultTag = "result";

    private static readonly Meter Meter = new(MeterName, MeterVersion);

    /// <summary>输入 token 数。仅在 provider 报告了用量时记录。</summary>
    internal static readonly Histogram<long> InputTokens = Meter.CreateHistogram<long>(
        InputTokensInstrument, unit: "token",
        description: "Prompt tokens reported by the provider.");

    /// <summary>输出 token 数。仅在 provider 报告了用量时记录。</summary>
    internal static readonly Histogram<long> OutputTokens = Meter.CreateHistogram<long>(
        OutputTokensInstrument, unit: "token",
        description: "Completion tokens reported by the provider.");

    /// <summary>调用次数；按结果区分成功与失败。</summary>
    internal static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        CallsInstrument, unit: "call",
        description: "Model call count, tagged by result.");

    /// <summary>
    /// 未报告用量的调用次数。
    /// </summary>
    /// <remarks>
    /// 单独一个计数器而不是把 token 记成 0：后端看到"0 tokens"会读成
    /// "这次调用免费"，而真相是"不知道花了多少"。两者必须能区分。
    /// </remarks>
    internal static readonly Counter<long> UnreportedCalls = Meter.CreateCounter<long>(
        UnreportedCallsInstrument, unit: "call",
        description: "Model calls where the provider did not report usage.");

    /// <summary>调用耗时。</summary>
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        CallDurationInstrument, unit: "ms",
        description: "Model call duration in milliseconds.");
}
