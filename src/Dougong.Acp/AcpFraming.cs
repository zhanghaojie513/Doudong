using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dougong.Acp;

/// <summary>
/// ACP 的 stdio 分帧：**每条消息一行 JSON，以 LF 分隔，没有 Content-Length 头**。
/// </summary>
/// <remarks>
/// 这一点与常见的 JSON-RPC 分帧（Content-Length + 空行）不同，也与 MCP 的
/// 传输层不同。搞错会直接连不通，且症状是"对面永远不回消息"。
/// <para>
/// 三条来自真实实现的容错要求（见 docs/acp-reference-survey.md）：
/// <list type="bullet">
/// <item>单行没有长度上限——diff、图片、rawOutput 都可能到数 MB；
/// 用可增长缓冲 + 保护上限，超限判定连接损坏</item>
/// <item>无法解析为 JSON 的行**跳过而不是断连**：有些 Agent 启动时会往 stdout 打横幅</item>
/// <item>行尾可能有 <c>\r</c>，要剥掉；空行与纯空白行跳过</item>
/// </list>
/// </para>
/// </remarks>
public static class AcpFraming
{
    /// <summary>单行保护上限。超过视为连接损坏而不是继续累积内存。</summary>
    public const int MaxLineBytes = 64 * 1024 * 1024;

    /// <summary>连续多少行无法解析后判定连接损坏。</summary>
    public const int MaxConsecutiveUnparsableLines = 100;

    /// <summary>
    /// 序列化为一行（紧凑格式 + LF）。
    /// </summary>
    /// <remarks>
    /// 必须紧凑：<c>WriteIndented</c> 会插入裸换行，直接破坏按行分帧。
    /// System.Text.Json 会把字符串内的换行转义成 <c>\n</c>，所以紧凑输出天然安全。
    /// </remarks>
    public static string Encode(JsonNode message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n";
    }

    /// <summary>写入一条消息并刷新。</summary>
    public static async Task WriteAsync(
        Stream stream, JsonNode message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Encoding.UTF8.GetBytes(Encode(message));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按行读取消息。无法解析的行被跳过；连续过多则抛出表示连接损坏。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> 而不是自己切字节：
    /// 它按需增长缓冲，天然满足"单行无上限"。保护上限单独检查。
    /// </remarks>
    public static async IAsyncEnumerable<JsonNode> ReadAsync(
        Stream stream,
        Action<string>? onUnparsableLine = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192, leaveOpen: true);

        var consecutiveFailures = 0;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 对端关闭或流损坏：正常结束读取，由调用方根据挂起请求决定如何报错。
                yield break;
            }

            if (line is null) yield break;

            // 行尾的 \r 要剥掉（对端可能用 CRLF）。
            if (line.EndsWith('\r')) line = line[..^1];

            // 空行与纯空白行跳过：不是错误。
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.Length > MaxLineBytes)
            {
                throw new AcpProtocolException(
                    $"单行超过 {MaxLineBytes} 字节上限，判定连接损坏。");
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                node = null;
            }

            if (node is null)
            {
                // 跳过而不是断连：真实 Agent 可能往 stdout 打横幅或警告。
                consecutiveFailures++;
                onUnparsableLine?.Invoke(line.Length > 1024 ? line[..1024] : line);

                if (consecutiveFailures >= MaxConsecutiveUnparsableLines)
                {
                    throw new AcpProtocolException(
                        $"连续 {consecutiveFailures} 行无法解析为 JSON，判定连接损坏。");
                }

                continue;
            }

            consecutiveFailures = 0;
            yield return node;
        }
    }
}

/// <summary>
/// 一条入站消息的分类结果（JSON-RPC 2.0）。
/// </summary>
/// <remarks>
/// 判别只看三个字段是否存在：<c>method</c>、<c>id</c>、<c>result</c>/<c>error</c>。
/// <list type="bullet">
/// <item>有 method 且有 id → 请求</item>
/// <item>有 method 无 id → 通知</item>
/// <item>无 method、有 id、且有 result 或 error → 响应</item>
/// <item>其它形状 → 丢弃并记录</item>
/// </list>
/// <para>
/// 请求 id 可以是整数或字符串。<b>回复时必须原样回传</b>，不能转换类型——
/// 转换后对端按自己的类型匹配会找不到对应请求。
/// </para>
/// </remarks>
public sealed record AcpMessage
{
    public required AcpMessageKind Kind { get; init; }

    /// <summary>请求或响应的 id；通知与不合法消息为 null。</summary>
    public JsonNode? Id { get; init; }

    public string? Method { get; init; }

    public JsonNode? Params { get; init; }

    public JsonNode? Result { get; init; }

    public JsonNode? Error { get; init; }

    /// <summary>该 id 是字符串还是数字——回复时必须保持一致。</summary>
    public bool IdIsString => Id is JsonValue value
        && value.GetValueKind() is JsonValueKind.String;

    /// <summary>从原始节点分类。形状不合法时返回 <see cref="AcpMessageKind.Invalid"/>。</summary>
    public static AcpMessage Classify(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is not JsonObject obj) return new AcpMessage { Kind = AcpMessageKind.Invalid };

        var hasMethod = obj.TryGetPropertyValue("method", out var method) && method is JsonValue;
        var hasId = obj.TryGetPropertyValue("id", out var id) && id is not null;
        var hasResult = obj.TryGetPropertyValue("result", out var result);
        var hasError = obj.TryGetPropertyValue("error", out var error);

        if (hasMethod && hasId)
            return new AcpMessage
            {
                Kind = AcpMessageKind.Request,
                Id = id,
                Method = method!.GetValue<string>(),
                Params = obj.TryGetPropertyValue("params", out var p) ? p : null,
            };

        if (hasMethod)
            return new AcpMessage
            {
                Kind = AcpMessageKind.Notification,
                Method = method!.GetValue<string>(),
                Params = obj.TryGetPropertyValue("params", out var p) ? p : null,
            };

        if (hasId && (hasResult || hasError))
            return new AcpMessage
            {
                Kind = AcpMessageKind.Response,
                Id = id,
                Result = result,
                Error = error,
            };

        return new AcpMessage { Kind = AcpMessageKind.Invalid };
    }
}

public enum AcpMessageKind
{
    Invalid,
    Request,
    Notification,
    Response,
}

/// <summary>协议层错误。与"业务失败"区分：这条表示连接或用例本身不成立。</summary>
public sealed class AcpProtocolException(string message) : Exception(message);
