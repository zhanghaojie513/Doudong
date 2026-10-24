using System.Diagnostics.Metrics;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2.3 验收：用量与调用指标必须能被外部可观测系统消费（docs/architecture.md §5.4）。
/// </summary>
/// <remarks>
/// 缺口形态：`UsageLedger` 的汇总只存在于内存，进程结束即丢失，
/// 没有任何外部系统能看到"这次会话花了多少 token、失败了几次"。
/// <para>
/// 判定方式不看实现细节，只看标准出口：用 <see cref="MeterListener"/> 监听
/// Dougong 的 meter，验证真实调用发生后确实产生了记录。
/// 这是"能被任何 OTel 后端消费"的可执行形式。
/// </para>
/// </remarks>
public static class TelemetryChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("meter-is-emitted", MeterEmittedAsync);
        failures += await CheckAsync("unreported-is-not-counted-as-zero", UnreportedAsync);
        failures += await CheckAsync("failures-are-tagged", FailureTaggedAsync);
        failures += await CheckAsync("instrument-names-are-stable", InstrumentNamesAsync);
        failures += await CheckAsync("no-high-cardinality-tags", CardinalityAsync);

        Console.WriteLine(failures == 0
            ? "TELEMETRY: ALL PASS"
            : $"TELEMETRY: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>真实调用后必须观察到 token 计量。</summary>
    private static async Task<string?> MeterEmittedAsync()
    {
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DougongTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();

        var ledger = new UsageLedger();
        var client = new UsageRecordingChatClient(
            new UsageClient(input: 120, output: 30), ledger, "s1",
            "test-provider", "test-model", ChatProtocol.OpenAiChatCompletions);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")])) { }

        lock (measurements)
        {
            if (measurements.Count == 0)
                return "真实调用后 meter 上没有任何记录";

            var input = measurements.FirstOrDefault(m => m.Name == DougongTelemetry.InputTokensInstrument);
            if (input.Name is null) return "缺少输入 token 计量";
            if (input.Value != 120) return $"输入 token 应为 120，实际 {input.Value}";

            var output = measurements.FirstOrDefault(m => m.Name == DougongTelemetry.OutputTokensInstrument);
            if (output.Name is null) return "缺少输出 token 计量";
            if (output.Value != 30) return $"输出 token 应为 30，实际 {output.Value}";
        }

        return null;
    }

    /// <summary>
    /// provider 未报告用量时不能记成 0 token —— 那会让人以为这次调用免费。
    /// </summary>
    private static async Task<string?> UnreportedAsync()
    {
        var measurements = new List<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DougongTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, value));
        });
        listener.Start();

        var ledger = new UsageLedger();
        // 不报 usage 的客户端。
        var client = new UsageRecordingChatClient(
            new UsageClient(input: null, output: null), ledger, "s1",
            "test-provider", "test-model", ChatProtocol.OpenAiChatCompletions);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")])) { }

        lock (measurements)
        {
            if (measurements.Any(m => m.Name == DougongTelemetry.InputTokensInstrument))
                return "未报告用量仍产生了 token 计量：会被读成 0 消耗";

            if (!measurements.Any(m => m.Name == DougongTelemetry.UnreportedCallsInstrument))
                return "未报告用量没有单独计数，无法与零消耗区分";
        }

        return null;
    }

    /// <summary>失败调用必须被单独计数并可区分。</summary>
    private static async Task<string?> FailureTaggedAsync()
    {
        var measurements = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DougongTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements) measurements.Add((instrument.Name, ToDictionary(tags)));
        });
        listener.Start();

        var ledger = new UsageLedger();
        var client = new UsageRecordingChatClient(
            new ThrowingClient(), ledger, "s1",
            "test-provider", "test-model", ChatProtocol.OpenAiChatCompletions);

        try
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")])) { }
        }
        catch (InvalidOperationException) { }

        lock (measurements)
        {
            var calls = measurements
                .Where(m => m.Name == DougongTelemetry.CallsInstrument)
                .ToList();

            if (calls.Count == 0) return "失败调用没有产生计量";

            var hasSuccessTag = calls.Any(c =>
                c.Tags.TryGetValue(DougongTelemetry.ResultTag, out var v)
                && v?.ToString() == "success");
            var hasFailureTag = calls.Any(c =>
                c.Tags.TryGetValue(DougongTelemetry.ResultTag, out var v)
                && v?.ToString() == "failure");

            if (hasSuccessTag && !hasFailureTag) return "失败调用被记成了成功";
            return hasFailureTag ? null : "失败调用缺少结果标签";
        }
    }

    /// <summary>计量名称与单位必须稳定——后端仪表盘依赖它们。</summary>
    private static async Task<string?> InstrumentNamesAsync()
    {
        await Task.CompletedTask;
        var names = new[]
        {
            DougongTelemetry.MeterName,
            DougongTelemetry.InputTokensInstrument,
            DougongTelemetry.OutputTokensInstrument,
            DougongTelemetry.CallsInstrument,
            DougongTelemetry.UnreportedCallsInstrument,
        };

        if (names.Any(string.IsNullOrWhiteSpace))
            return "计量名称有空值";

        return names.Distinct(StringComparer.Ordinal).Count() == names.Length
            ? null : "计量名称有重复";
    }

    /// <summary>
    /// 标签不能含高基数维度（会话 ID、调用 ID）。
    /// </summary>
    /// <remarks>
    /// 把 sessionId 之类放进指标标签会让后端的时间序列数量爆炸，
    /// 这是 OTel 里最常见的错误之一。会话级归属应该走日志或事件，不放指标。
    /// </remarks>
    private static async Task<string?> CardinalityAsync()
    {
        var tagKeys = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DougongTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags) lock (tagKeys) tagKeys.Add(tag.Key);
        });
        listener.Start();

        var ledger = new UsageLedger();
        var client = new UsageRecordingChatClient(
            new UsageClient(input: 10, output: 5), ledger, "session-should-not-be-a-tag",
            "test-provider", "test-model", ChatProtocol.OpenAiChatCompletions);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")])) { }

        lock (tagKeys)
        {
            foreach (var forbidden in new[] { "session_id", "sessionId", "invocation_id", "invocationId" })
                if (tagKeys.Contains(forbidden))
                    return $"指标标签含高基数维度 '{forbidden}'";
        }

        return null;
    }

    private static Dictionary<string, object?> ToDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags) result[tag.Key] = tag.Value;
        return result;
    }

    private sealed class UsageClient(long? input, long? output) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");

            if (input is not null || output is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = input,
                        OutputTokenCount = output,
                    })]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            throw new InvalidOperationException("模拟流中断");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        try
        {
            var failure = await action();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name}");
                return 0;
            }

            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
