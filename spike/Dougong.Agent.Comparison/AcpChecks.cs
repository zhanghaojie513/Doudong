using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Acp;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2.4 验收：ACP Agent 端能被外部客户端驱动跑通一轮对话。
/// </summary>
/// <remarks>
/// 全部用真实的 <see cref="Stream"/> 配对驱动 <see cref="AcpAgentHost"/>，
/// 不 mock 协议层——mock 只能验证我的假设，测不到"帧格式对不对"。
/// <para>
/// 重点覆盖 docs/acp-reference-survey.md 里记的三个坑：
/// stdout 纯净、事件先于响应、权限请求无回复必须按拒绝。
/// </para>
/// </remarks>
public static class AcpChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("initialize-negotiates-v1", InitializeAsync);
        failures += await CheckAsync("rejects-other-protocol-version", VersionRejectAsync);
        failures += await CheckAsync("prompt-round-trips-with-updates", PromptAsync);
        failures += await CheckAsync("updates-precede-response", OrderingAsync);
        failures += await CheckAsync("cancel-marks-turn-cancelled", CancelAsync);
        failures += await CheckAsync("unknown-method-is-reported", UnknownMethodAsync);
        failures += await CheckAsync("unimplemented-session-methods-are-explicit", UnimplementedAsync);
        failures += await CheckAsync("request-id-type-is-preserved", IdTypeAsync);
        failures += await CheckAsync("stdout-carries-only-protocol-frames", StdoutPurityAsync);
        failures += await CheckAsync("string-id-request-gets-string-response", StringIdAsync);

        Console.WriteLine(failures == 0
            ? "ACP: ALL PASS"
            : $"ACP: {failures} FAILURE(S)");
        return failures;
    }

    // ── 驱动骨架 ─────────────────────────────────────────────────

    /// <summary>客户端侧：向 host 写请求并从 host 读出响应/通知。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toHost = new();
        private readonly System.IO.Pipelines.Pipe _fromHost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _stdout = new();
        private readonly List<string> _logs = [];
        private readonly Task _loop;
        private readonly AcpAgentHost _host;
        private readonly Stream _hostToClient;
        private readonly StreamReader _reader;

        public Harness(IAgentRuntime runtime, IToolDispatcher? dispatcher = null)
        {
            _host = new AcpAgentHost(
                _toHost.Reader.AsStream(leaveOpen: true),
                _fromHost.Writer.AsStream(leaveOpen: true),
                _ => runtime,
                _ => dispatcher ?? new ToolDispatcher(),
                new StringWriter());

            _loop = Task.Run(() => _host.RunAsync(_stop.Token));
            _hostToClient = _fromHost.Reader.AsStream(leaveOpen: true);
            // 必须复用同一个 reader：每次新建会丢掉已缓冲但未消费的字节，
            // 表现为"只收到第一条增量"。
            _reader = new StreamReader(_hostToClient, Encoding.UTF8, false, 8192, leaveOpen: true);
        }

        public List<JsonNode> Received { get; } = [];

        /// <summary>发送一条消息（紧凑 JSON + LF）。</summary>
        public async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(
                message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
            await _toHost.Writer.AsStream(leaveOpen: true).WriteAsync(bytes);
        }

        /// <summary>发请求并读到匹配 id 的响应，途中收到的通知进 <see cref="Received"/>。</summary>
        public async Task<JsonNode> RequestAsync(JsonObject message, TimeSpan? timeout = null)
        {
            await SendAsync(message);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            var id = message["id"]!.ToJsonString();

            while (true)
            {
                var node = await ReadLineAsync(cts.Token);
                if (node is null) throw new TimeoutException("host 没有在时限内响应");

                if (node["id"]?.ToJsonString() == id && node["method"] is null)
                    return node;

                Received.Add(node);
            }
        }

        private async Task<JsonNode?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            return line is null ? null : JsonNode.Parse(line);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static JsonObject Request(long id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
    };

    /// <summary>只回一段文本的运行时。</summary>
    private static IAgentRuntime TextRuntime(string text) =>
        new LoopAgentRuntime(new FixedClient(text), new ToolDispatcher());

    // ── 用例 ─────────────────────────────────────────────────────

    private static async Task<string?> InitializeAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "probe", ["version"] = "1.0.0" },
        }));

        var result = response["result"];
        if (result is null) return $"initialize 返回了错误：{response["error"]?.ToJsonString()}";

        if (result["protocolVersion"]?.GetValue<int>() != 1)
            return $"协商版本应为 1，实际 {result["protocolVersion"]}";

        // 没实现的能力不能声明。
        var caps = result["agentCapabilities"];
        if (caps?["loadSession"]?.GetValue<bool>() == true)
            return "声明了 loadSession 但未实现";

        return result["agentInfo"]?["name"]?.GetValue<string>() == "dougong"
            ? null : "agentInfo 缺少名称";
    }

    private static async Task<string?> VersionRejectAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 2,
        }));

        if (response["error"] is null)
            return "v2 请求被接受：不应尝试跨主版本兼容";

        var message = response["error"]!["message"]?.GetValue<string>() ?? "";
        return message.Contains("v1", StringComparison.Ordinal)
            ? null : $"拒绝原因未说明支持的版本：{message}";
    }

    private static async Task<string?> PromptAsync()
    {
        await using var harness = new Harness(TextRuntime("hello-acp"));
        var init = await harness.RequestAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
        }));
        if (init["error"] is not null) return $"initialize 失败：{init["error"]}";

        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]?["sessionId"]?.GetValue<string>();
        if (sessionId is null) return $"session/new 失败：{session["error"]?.ToJsonString()}";

        var prompt = await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "say hello",
            }),
        }));

        if (prompt["error"] is not null) return $"session/prompt 失败：{prompt["error"]}";

        var stopReason = prompt["result"]?["stopReason"]?.GetValue<string>();
        if (stopReason != "end_turn") return $"stopReason 应为 end_turn，实际 {stopReason}";

        // 必须收到文本增量。
        var chunks = harness.Received
            .Where(n => n["method"]?.GetValue<string>() == "session/update")
            .Select(n => n["params"]?["update"])
            .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
            .ToList();

        if (chunks.Count == 0) return "没有收到 agent_message_chunk";

        var text = string.Concat(chunks.Select(c =>
            c?["content"]?["text"]?.GetValue<string>() ?? ""));
        return text == "hello-acp" ? null : $"文本增量不符：'{text}'";
    }

    /// <summary>
    /// 硬约束：所有 <c>session/update</c> 必须在 <c>session/prompt</c> 响应之前到达。
    /// </summary>
    /// <remarks>
    /// 客户端按顺序处理：响应标志着轮次结束。若增量晚于响应到达，
    /// 客户端会丢掉最后几条输出。这是 zeron 修过两次的陷阱。
    /// </remarks>
    private static async Task<string?> OrderingAsync()
    {
        var client = new MultiChunkClient(["a", "b", "c"]);
        await using var harness = new Harness(new LoopAgentRuntime(client, new ToolDispatcher()));

        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        // RequestAsync 会把途中收到的通知收进 Received，然后才返回响应。
        // 因此这里要验证：三条增量全部在 Received 里。
        await harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }));

        var chunks = harness.Received
            .Select(n => n["params"]?["update"])
            .Where(u => u?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
            .Select(u => u?["content"]?["text"]?.GetValue<string>() ?? "")
            .ToList();

        return string.Concat(chunks) == "abc"
            ? null : $"增量未全部先于响应到达：'{string.Concat(chunks)}'";
    }

    private static async Task<string?> CancelAsync()
    {
        var client = new SlowClient(TimeSpan.FromSeconds(10));
        await using var harness = new Harness(new LoopAgentRuntime(client, new ToolDispatcher()));

        await harness.RequestAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = 1 }));
        var session = await harness.RequestAsync(Request(2, "session/new", new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["mcpServers"] = new JsonArray(),
        }));
        var sessionId = session["result"]!["sessionId"]!.GetValue<string>();

        // 发 prompt 后立刻取消（通知不需要响应）。
        var promptTask = harness.RequestAsync(Request(3, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }), TimeSpan.FromSeconds(8));

        await Task.Delay(200);
        await harness.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/cancel",
            ["params"] = new JsonObject { ["sessionId"] = sessionId },
        });

        var response = await promptTask;
        var stopReason = response["result"]?["stopReason"]?.GetValue<string>();
        return stopReason == "cancelled" ? null : $"取消后 stopReason 应为 cancelled，实际 {stopReason}";
    }

    private static async Task<string?> UnknownMethodAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(1, "totally/unknown"));

        if (response["error"] is null) return "未知方法没有返回错误";
        return response["error"]!["code"]?.GetValue<int>() == -32601
            ? null : $"错误码应为 -32601，实际 {response["error"]!["code"]}";
    }

    private static async Task<string?> UnimplementedAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));

        foreach (var method in new[] { "session/load", "session/resume", "session/list", "session/close" })
        {
            var response = await harness.RequestAsync(Request(1, method, new JsonObject
            {
                ["sessionId"] = "x",
                ["cwd"] = Path.GetTempPath(),
            }));

            if (response["error"] is null)
                return $"'{method}' 未实现却返回成功——不能静默假装支持";

            var message = response["error"]!["message"]?.GetValue<string>() ?? "";
            if (!message.Contains("session/new", StringComparison.Ordinal))
                return $"'{method}' 的错误未给出替代做法：{message}";
        }

        return null;
    }

    private static async Task<string?> IdTypeAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var response = await harness.RequestAsync(Request(42, "initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
        }));

        if (response["id"]?.GetValueKind() is not JsonValueKind.Number)
            return $"数字 id 被改成了 {response["id"]?.GetValueKind()}";

        return response["id"]!.GetValue<long>() == 42 ? null : "id 值不符";
    }

    private static async Task<string?> StringIdAsync()
    {
        await using var harness = new Harness(TextRuntime("hi"));
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "req-abc",
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        };

        await harness.SendAsync(message);

        var response = await harness.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "req-abc",
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = 1 },
        });
        return response["id"]?.GetValueKind() is JsonValueKind.String
            ? null : $"字符串 id 被改成了 {response["id"]?.GetValueKind()}";
    }

    /// <summary>
    /// 硬约束：stdout 只能有协议帧。
    /// </summary>
    /// <remarks>
    /// 这条最容易踩：任何一处 <c>Console.WriteLine</c> 都会污染协议。
    /// 验证方式是把 host 的输出接到内存流，逐行检查每行都能解析为 JSON。
    /// </remarks>
    private static async Task<string?> StdoutPurityAsync()
    {
        var output = new MemoryStream();
        var input = new MemoryStream();

        var host = new AcpAgentHost(
            input,
            output,
            _ => TextRuntime("hi"),
            _ => new ToolDispatcher());

        // 让 host 读到一条完整请求后停止。
        var frame = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"terminate-check","params":{}}""" + "\n");
        await input.WriteAsync(frame);
        input.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = host.RunAsync(cts.Token);
        await Task.Delay(300);
        host.Stop();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var index = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            index++;
            try { JsonNode.Parse(line); }
            catch (JsonException)
            {
                return $"stdout 第 {index} 行不是协议帧：{line[..Math.Min(120, line.Length)]}";
            }
        }

        return index == 0 ? "host 没有向 stdout 写任何东西（协议不可用）" : null;
    }

    // ── 假客户端 ─────────────────────────────────────────────────

    private static IEnumerable<ChatResponseUpdate> ToUpdates(string text) =>
        [new ChatResponseUpdate(ChatRole.Assistant, text)];

    private sealed class FixedClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var update in ToUpdates(text)) yield return update;
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class MultiChunkClient(string[] chunks) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in chunks) yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class SlowClient(TimeSpan delay) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "too late");
        }

        public object? GetService(Type t, object? k = null) => null;
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
