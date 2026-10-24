using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;

namespace Dougong.Acp;

/// <summary>
/// ACP Agent 端的 stdio 宿主：从 stdin 读 JSON-RPC 消息，向 stdout 写协议帧。
/// </summary>
/// <remarks>
/// 设计约束（docs/architecture.md §5.3、§5.4）：
/// <list type="bullet">
/// <item><b>stdout 只能有协议帧</b>。任何日志都必须走 stderr，
/// 混进 stdout 会直接破坏协议——这是最容易犯的错</item>
/// <item>协议版本锁定 v1；对端给出非 1 的版本时明确拒绝，不尝试降级通信</item>
/// <item>首版只实现能被外部客户端驱动跑通一轮对话的最小闭环，
/// 其余能力**声明不支持**，而不是"实现了但走不通"</item>
/// <item>入站消息严格按到达顺序处理，响应等待由同一循环推进</item>
/// </list>
/// </remarks>
public sealed class AcpAgentHost
{
    /// <summary>v1 是唯一支持的协议版本。</summary>
    public const int SupportedProtocolVersion = 1;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<AcpSessionContext, AcpSessionRuntime> _runtimeFactory;
    private readonly TextWriter _log;
    private readonly Dictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextRequestId;

    /// <param name="log">
    /// 日志出口。**必须是 stderr**：stdout 归协议所有。
    /// 默认写 <see cref="Console.Error"/>。
    /// </param>
    public AcpAgentHost(
        Stream input,
        Stream output,
        Func<AcpSessionContext, AcpSessionRuntime> runtimeFactory,
        TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        _input = input;
        _output = output;
        _runtimeFactory = runtimeFactory;
        _log = log ?? Console.Error;
    }

    /// <summary>当前活跃会话数，供诊断与检查项使用。</summary>
    public int SessionCount
    {
        get { lock (_sessions) return _sessions.Count; }
    }

    /// <summary>
    /// 运行消息循环，直到 stdin 结束或取消。
    /// </summary>
    /// <remarks>
    /// 单循环处理所有入站消息：请求、通知、响应都在同一条顺序上，
    /// 避免"响应走挂起表、通知走队列"造成的顺序错乱。
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);

        await foreach (var node in AcpFraming.ReadAsync(
            _input,
            line => _log.WriteLine($"[acp] 跳过无法解析的行：{line}"),
            linked.Token).ConfigureAwait(false))
        {
            var message = AcpMessage.Classify(node);

            // session/prompt 会跑完整轮对话，期间读循环不能停：
            // 否则后续的 session/cancel 通知根本读不到，取消永远无效。
            // 因此长请求不 await，登记到待完成表后立即继续读取。
            if (message.Kind is AcpMessageKind.Request
                && message.Method is "session/prompt")
            {
                TrackLongRunning(DispatchAsync(message, linked.Token));
                continue;
            }

            try
            {
                await DispatchAsync(message, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单条消息处理失败不能拖垮整个连接，但必须可诊断。
                _log.WriteLine($"[acp] 处理 {message.Method} 失败：{ex.GetType().Name}: {ex.Message}");
                if (message.Kind is AcpMessageKind.Request)
                    await SendErrorAsync(message.Id, -32603, ex.Message, linked.Token).ConfigureAwait(false);
            }
        }
    }

    private readonly List<Task> _longRunning = [];

    /// <summary>
    /// 登记一个长请求，确保它的异常被观察、且能在关闭时等待。
    /// </summary>
    /// <remarks>
    /// 不 await 不等于不管：未观察的任务异常会静默丢失，
    /// 而"取消后没有响应"正是这类问题最容易被误认为正常的表现。
    /// </remarks>
    private void TrackLongRunning(Task task)
    {
        lock (_longRunning) _longRunning.Add(task);
        _ = task.ContinueWith(
            t =>
            {
                lock (_longRunning) _longRunning.Remove(t);
                if (t.IsFaulted)
                    _log.WriteLine($"[acp] 长请求失败：{t.Exception?.GetBaseException().Message}");
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>请求停止消息循环。</summary>
    public void Stop() => _lifetime.Cancel();

    private async Task DispatchAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case AcpMessageKind.Request:
                await HandleRequestAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case AcpMessageKind.Notification:
                await HandleNotificationAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case AcpMessageKind.Response:
                // 这是对 host 主动发出的请求（session/request_permission）的回复。
                // 必须把结果交给等待方，否则权限请求永远等不到答复，
                // 表现为"审批一直挂着"——而症状看起来像客户端不配合。
                if (!CompletePendingRequest(message))
                    _log.WriteLine("[acp] 收到未匹配的响应，已忽略");
                break;

            default:
                _log.WriteLine("[acp] 收到形状不合法的消息，已丢弃");
                break;
        }
    }

    private async Task HandleRequestAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        switch (message.Method)
        {
            case "initialize":
                await HandleInitializeAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "session/new":
                await HandleNewSessionAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "session/prompt":
                await HandlePromptAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "session/load":
            case "session/resume":
            case "session/list":
            case "session/close":
                // 首版明确声明不支持，而不是静默成功。
                await SendErrorAsync(message.Id, -32601,
                    $"方法 '{message.Method}' 在 Dougong 的 ACP 首版中未实现；"
                    + "请使用 session/new + session/prompt。", cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendErrorAsync(message.Id, -32601,
                    $"方法 '{message.Method}' 不受支持。", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleNotificationAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        if (message.Method == "session/cancel")
        {
            var sessionId = ReadString(message.Params, "sessionId");
            if (sessionId is not null && FindSession(sessionId) is { } session)
            {
                session.Cancel();
                _log.WriteLine($"[acp] 会话 {sessionId} 已请求取消");
            }
        }
        // 其它通知（含 $/cancel_request、未知扩展）一律忽略——通知不需要回复。
    }

    private async Task HandleInitializeAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        var requested = ReadInt(message.Params, "protocolVersion");
        if (requested is not null && requested != SupportedProtocolVersion)
        {
            // 版本不匹配时不"尽力兼容"：不同主版本的语义差异无法可靠降级。
            await SendErrorAsync(message.Id, -32602,
                $"Dougong 只支持 ACP v{SupportedProtocolVersion}，收到 v{requested}。",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = new JsonObject
        {
            ["protocolVersion"] = SupportedProtocolVersion,
            ["agentCapabilities"] = new JsonObject
            {
                // 首版不声明 loadSession：没实现的能力不能声明。
                ["loadSession"] = false,
                ["promptCapabilities"] = new JsonObject
                {
                    ["image"] = false,
                    ["audio"] = false,
                    ["embeddedContext"] = false,
                },
                ["mcpCapabilities"] = new JsonObject
                {
                    ["http"] = false,
                    ["sse"] = false,
                },
            },
            ["agentInfo"] = new JsonObject
            {
                ["name"] = "dougong",
                ["title"] = "Dougong",
                ["version"] = "0.1.0-alpha.1",
            },
            ["authMethods"] = new JsonArray(),
        };

        await SendResultAsync(message.Id, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNewSessionAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        var cwd = ReadString(message.Params, "cwd");
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd))
        {
            await SendErrorAsync(message.Id, -32602,
                "session/new 需要绝对的 cwd。", cancellationToken).ConfigureAwait(false);
            return;
        }

        var sessionId = "sess_" + Guid.NewGuid().ToString("N")[..12];
        var context = new AcpSessionContext(sessionId, cwd);

        // 一次工厂调用产出一对 runtime + dispatcher：
        // 若分别构造，审批器会挂在另一个实例上，工具执行根本不会经过它。
        var pair = _runtimeFactory(context);
        var session = new AcpSession(sessionId, cwd, pair.Runtime, this, _log);

        lock (_sessions) _sessions[sessionId] = session;

        var result = new JsonObject
        {
            ["sessionId"] = sessionId,
            // 首版不暴露模型配置项。
            ["modes"] = null,
        };

        await SendResultAsync(message.Id, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePromptAsync(AcpMessage message, CancellationToken cancellationToken)
    {
        var sessionId = ReadString(message.Params, "sessionId");
        if (sessionId is null || FindSession(sessionId) is not { } session)
        {
            await SendErrorAsync(message.Id, -32602,
                $"会话 '{sessionId}' 不存在。", cancellationToken).ConfigureAwait(false);
            return;
        }

        var prompt = ExtractPromptText(message.Params);
        if (prompt is null)
        {
            await SendErrorAsync(message.Id, -32602,
                "session/prompt 需要至少一个 text 内容块。", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 关键时序：所有 session/update 必须在响应之前发出。
        // 用 await 串起来而不是并行发送，否则客户端会丢最后几条增量。
        var outcome = await session.RunPromptAsync(prompt, cancellationToken).ConfigureAwait(false);

        await SendResultAsync(message.Id, new JsonObject
        {
            ["stopReason"] = outcome,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从 prompt 参数里取纯文本；非文本块首版忽略。</summary>
    private static string? ExtractPromptText(JsonNode? parameters)
    {
        if (parameters?["prompt"] is not JsonArray blocks) return null;

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block?["type"]?.GetValue<string>() != "text") continue;
            if (block["text"]?.GetValue<string>() is { Length: > 0 } text) parts.Add(text);
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private AcpSession? FindSession(string sessionId)
    {
        lock (_sessions) return _sessions.GetValueOrDefault(sessionId);
    }

    internal async Task SendNotificationAsync(
        string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };

        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 向客户端发起请求并等待响应（用于 session/request_permission）。
    /// </summary>
    /// <remarks>
    /// 入站响应由同一读循环分派：这里注册一个待完成项，等 <see cref="CompletePendingRequest"/>
    /// 被调用。因此调用方**不能**在读循环自身里同步阻塞等待——那会死锁。
    /// </remarks>
    internal async Task<JsonNode?> RequestAsync(
        string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRequest();
        lock (_pending) _pending[id] = pending;

        try
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            };

            await WriteAsync(message, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => pending.Completion.TrySetCanceled(cancellationToken));
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    private readonly Dictionary<long, PendingRequest> _pending = [];

    /// <summary>
    /// 把一条响应交给对应的挂起请求。返回是否匹配到。
    /// </summary>
    /// <remarks>
    /// 入站响应由同一读循环分派，所以这里只做"唤醒"而不阻塞——
    /// 若在读循环里同步等待，请求方和响应处理会互相死锁。
    /// </remarks>
    private bool CompletePendingRequest(AcpMessage response)
    {
        if (response.Id is not JsonValue idValue) return false;

        // 我们发出的请求 id 是 long；客户端应原样回传。
        if (!idValue.TryGetValue<long>(out var id)) return false;

        PendingRequest? pending;
        lock (_pending)
        {
            if (!_pending.TryGetValue(id, out pending)) return false;
            _pending.Remove(id);
        }

        pending.Completion.TrySetResult(response.Error is { } error
            // 错误响应也交给调用方判断：它比"静默超时"可诊断得多。
            ? new JsonObject { ["error"] = error.DeepClone() }
            : response.Result?.DeepClone());

        return true;
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonNode?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task SendResultAsync(
        JsonNode? id, JsonNode result, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            // id 原样回传：不能把字符串 id 转成数字，对端会匹配不到。
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(
        JsonNode? id, int code, string message, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        await WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        // 单写者队列：保证一条消息完整写出后再写下一条。
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AcpFraming.WriteAsync(_output, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string? ReadString(JsonNode? node, string name) =>
        node?[name]?.GetValue<string>();

    private static int? ReadInt(JsonNode? node, string name) =>
        node?[name]?.GetValue<int>();
}
