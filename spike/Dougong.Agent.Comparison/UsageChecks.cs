using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 用量记账验收（docs/architecture.md §5.1）。
/// </summary>
/// <remarks>
/// 这里最要紧的一条不是"能算出总数"，而是**未知与零能区分开**：
/// provider 不报 usage 时，一个"共 0 tokens"的汇总会让人以为不花钱。
/// 因此 <see cref="UsageSummary.UnreportedCalls"/> 与可空的 TotalTokens 是主要验收对象。
/// </remarks>
public static class UsageChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("records-streaming-usage", StreamingUsageAsync);
        failures += await CheckAsync("unreported-is-not-zero", UnreportedAsync);
        failures += await CheckAsync("failed-call-is-counted", FailedCallAsync);
        failures += await CheckAsync("cancelled-call-keeps-elapsed", CancelledAsync);
        failures += await CheckAsync("summarizes-per-session", PerSessionAsync);
        failures += await CheckAsync("summarizes-per-day", PerDayAsync);
        failures += await CheckAsync("concurrent-adds-are-safe", ConcurrentAsync);
        failures += await CheckAsync("usage-survives-non-streaming", NonStreamingAsync);
        failures += await CheckAsync("text-is-not-polluted-by-usage", NoTextPollutionAsync);
        failures += await CheckAsync("real-client-exposes-usage-as-content", RealClientShapeAsync);
        failures += await CheckAsync("client-emits-usage-content", ClientEmitsUsageContentAsync);

        Console.WriteLine(failures == 0
            ? "USAGE: ALL PASS"
            : $"USAGE: {failures} FAILURE(S)");
        return failures;
    }

    // 流式响应的 usage 必须被记录，且取自标准的 UsageContent。
    private static async Task<string?> StreamingUsageAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(Text: "hi"),
                ScriptedUpdate.Usage(10, 5, 15))), ledger, "s1");

        var text = await CollectTextAsync(client);
        if (text != "hi") return $"文本应为 hi，实际 '{text}'";

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"调用数应为 1，实际 {summary.Calls}";
        if (summary.InputTokens != 10 || summary.OutputTokens != 5)
            return $"用量不符：in={summary.InputTokens} out={summary.OutputTokens}";
        if (summary.TotalTokens != 15) return $"总量应为 15，实际 {summary.TotalTokens}";
        if (summary.UnreportedCalls != 0) return "有报告却记成未报告";
        return null;
    }

    // provider 不报 usage 时，必须记为"未知"而不是 0。
    private static async Task<string?> UnreportedAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "hi"))), ledger, "s1");

        await CollectTextAsync(client);
        var summary = ledger.Summarize("s1");

        if (summary.UnreportedCalls != 1)
            return $"未报告调用数应为 1，实际 {summary.UnreportedCalls}";
        if (summary.TotalTokens is not null)
            return $"未报告时总量应为未知，实际 {summary.TotalTokens}";
        if (summary.ReportedRatio != 0) return $"覆盖率应为 0，实际 {summary.ReportedRatio}";
        return null;
    }

    // 失败调用必须计入调用数与失败数，不能只统计成功。
    private static async Task<string?> FailedCallAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "x", AbortAfter: true))), ledger, "s1");

        try { await CollectTextAsync(client); return "应抛出"; }
        catch (InvalidOperationException) { }

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"失败调用也应计入，实际 {summary.Calls}";
        if (summary.FailedCalls != 1) return $"失败数应为 1，实际 {summary.FailedCalls}";

        var record = ledger.Records.Single();
        return record.Succeeded ? "记录被标记为成功" : null;
    }

    // 取消也要留痕：取消前可能已经消耗了输入 token。
    private static async Task<string?> CancelledAsync()
    {
        var ledger = new UsageLedger();
        var inner = new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "x", Delay: TimeSpan.FromSeconds(5))));
        var client = Wrap(inner, ledger, "s1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await CollectTextAsync(client, cts.Token);
            return "取消后未抛出";
        }
        catch (OperationCanceledException) { }

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"取消也要留痕，实际 {summary.Calls} 条";
        if (summary.FailedCalls != 1) return $"取消应记为失败，实际 {summary.FailedCalls}";
        return null;
    }

    // 会话隔离：不同会话不能混在一起。
    private static async Task<string?> PerSessionAsync()
    {
        var ledger = new UsageLedger();
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "a"), ScriptedUpdate.Usage(10, 1))),
            ledger, "s1"));
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "b"), ScriptedUpdate.Usage(20, 2))),
            ledger, "s2"));

        var s1 = ledger.Summarize("s1");
        var s2 = ledger.Summarize("s2");
        var all = ledger.SummarizeAll();

        if (s1.InputTokens != 10) return $"s1 输入应为 10，实际 {s1.InputTokens}";
        if (s2.InputTokens != 20) return $"s2 输入应为 20，实际 {s2.InputTokens}";
        if (all.InputTokens != 30) return $"总计应为 30，实际 {all.InputTokens}";
        if (all.Calls != 2) return $"总调用数应为 2，实际 {all.Calls}";
        return null;
    }

    // 按自然日汇总，用可控时钟避免跨日抖动。
    private static async Task<string?> PerDayAsync()
    {
        var day1 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var now = day1;
        var ledger = new UsageLedger(() => now);

        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "a"), ScriptedUpdate.Usage(10, 1))),
            ledger, "s1"));

        now = day2;
        await CollectTextAsync(Wrap(new ScriptedChatClient(
            new ScriptedTurn(new ScriptedUpdate(Text: "b"), ScriptedUpdate.Usage(20, 2))),
            ledger, "s1"));

        var first = ledger.SummarizeDay(DateOnly.FromDateTime(day1.LocalDateTime));
        var second = ledger.SummarizeDay(DateOnly.FromDateTime(day2.LocalDateTime));

        if (first.Calls != 1 || first.InputTokens != 10)
            return $"第一日汇总不符：calls={first.Calls} in={first.InputTokens}";
        if (second.Calls != 1 || second.InputTokens != 20)
            return $"第二日汇总不符：calls={second.Calls} in={second.InputTokens}";
        return null;
    }

    // 并发追加不能丢记录或损坏聚合。
    private static async Task<string?> ConcurrentAsync()
    {
        var ledger = new UsageLedger();
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var client = Wrap(new ScriptedChatClient(
                new ScriptedTurn(new ScriptedUpdate(Text: "x"), ScriptedUpdate.Usage(1, 1))),
                ledger, $"s{i % 4}");
            await CollectTextAsync(client);
        }));

        await Task.WhenAll(tasks);

        var all = ledger.SummarizeAll();
        if (all.Calls != 40) return $"调用数应为 40，实际 {all.Calls}";
        if (all.InputTokens != 40) return $"输入应为 40，实际 {all.InputTokens}";

        var perSession = Enumerable.Range(0, 4).Sum(i => ledger.Summarize($"s{i}").Calls);
        return perSession == 40 ? null : $"按会话求和应为 40，实际 {perSession}";
    }

    // 非流式路径同样要记账，不能只有流式被统计。
    private static async Task<string?> NonStreamingAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new FixedResponseClient(new UsageDetails
        {
            InputTokenCount = 7,
            OutputTokenCount = 3,
            TotalTokenCount = 10,
        }), ledger, "s1");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        if (response.Usage?.InputTokenCount != 7)
            return $"响应应带上 usage，实际 {response.Usage?.InputTokenCount}";

        var summary = ledger.Summarize("s1");
        if (summary.Calls != 1) return $"非流式调用未记账，总计 {summary.Calls}";
        return summary.TotalTokens == 10 ? null : $"总量应为 10，实际 {summary.TotalTokens}";
    }

    // 用量内容不能混进模型可见文本，否则模型会读到一串 token 数字。
    private static async Task<string?> NoTextPollutionAsync()
    {
        var ledger = new UsageLedger();
        var client = Wrap(new ScriptedChatClient(
            new ScriptedTurn(
                new ScriptedUpdate(Text: "answer"),
                ScriptedUpdate.Usage(100, 50))), ledger, "s1");

        var text = await CollectTextAsync(client);
        return text == "answer" ? null : $"用量污染了文本：'{text}'";
    }

    // 真实连接器必须把 usage 作为 UsageContent 交给上层，而不是藏在附加属性里。
    private static Task<string?> RealClientShapeAsync()
    {
        var format = new OpenAiChatCompletionsFormat();
        var decoder = format.CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        decoder.Decode("""{"choices":[{"delta":{"content":"x"}}]}""", updates);
        decoder.Decode("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13}}""", updates);

        if (decoder.Usage is null) return Task.FromResult<string?>("连接器未解析出 usage");
        if (decoder.Usage.InputTokenCount != 9)
            return Task.FromResult<string?>($"输入应为 9，实际 {decoder.Usage.InputTokenCount}");

        // 连接器必须用标准 UsageContent 承载，M.E.AI 的聚合与中间件才看得见。
        var usageContent = new UsageContent(decoder.Usage);
        if (usageContent.Details.TotalTokenCount != 13)
            return Task.FromResult<string?>("UsageContent 未携带总量");

        return Task.FromResult<string?>(null);
    }

    // 端到端：走真实 HttpClientChatClient（假传输），确认 usage 真的以 UsageContent 发出。
    // 上面那条只验证"解码器能解析"，不验证"客户端怎么发"——两者必须分开测，
    // 否则把发送端改回自定义附加属性时，检查仍然会通过。
    private static async Task<string?> ClientEmitsUsageContentAsync()
    {
        var sse = string.Join("\n", [
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":2,\"total_tokens\":8}}",
            string.Empty,
        ]);

        using var http = new HttpClient(new StubHandler(sse));
        IChatClient client = new HttpClientChatClient(
            "https://example.invalid", "key", "m",
            new OpenAiChatCompletionsFormat(), http);

        UsageDetails? seen = null;
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")]))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent) seen = usageContent.Details;
                else if (content is TextContent textContent) text.Append(textContent.Text);
            }
        }

        if (text.ToString() != "hi") return $"文本应为 hi，实际 '{text}'";
        if (seen is null) return "客户端没有以 UsageContent 发出用量（被藏进附加属性了？）";
        return seen.TotalTokenCount == 8 ? null : $"总量应为 8，实际 {seen.TotalTokenCount}";
    }

    private sealed class StubHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
            });
    }

    private static UsageRecordingChatClient Wrap(
        IChatClient inner, UsageLedger ledger, string sessionId) =>
        new(inner, ledger, sessionId, "test", "test-model",
            ChatProtocol.OpenAiChatCompletions);

    private static async Task<string> CollectTextAsync(
        IChatClient client, CancellationToken cancellationToken = default)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], null, cancellationToken))
        {
            if (update.Text is { Length: > 0 } chunk) text.Append(chunk);
        }

        return text.ToString();
    }

    private sealed class FixedResponseClient(UsageDetails usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixed"))
            {
                Usage = usage,
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "fixed");
            await Task.CompletedTask;
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
