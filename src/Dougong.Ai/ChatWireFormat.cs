using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 一种线的格式：请求体怎么拼、响应流怎么解。
/// </summary>
/// <remarks>
/// 把"拼请求/解响应"从 HTTP 传输里拆出来，是为了能对固定帧做确定性校验：
/// 三种协议的结构差异（工具声明形状、调用形状、参数片段字段、终结信号）
/// 都可以用录下来的帧复现，不必依赖网络与某个 provider 当天的心情。
/// </remarks>
public interface IChatWireFormat
{
    /// <summary>本格式对应的协议标识。</summary>
    ChatProtocol Protocol { get; }

    /// <summary>相对于 base URL 的端点路径。</summary>
    string RelativePath { get; }

    /// <summary>写入鉴权与协议版本等请求头。</summary>
    void ApplyHeaders(HttpRequestMessage request, string apiKey);

    /// <summary>
    /// 构造请求体。工具声明从 <see cref="ToolDeclarations.OptionsKey"/> 读取，
    /// 连接器不自行推断"该给模型看哪些工具"。
    /// </summary>
    JsonObject BuildBody(
        string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream);

    /// <summary>为本格式创建一个流式解码器。每次请求都需要新实例。</summary>
    ChatStreamDecoder CreateDecoder();
}

/// <summary>
/// 把一段 SSE 的 <c>data:</c> 负载解码为 <see cref="ChatResponseUpdate"/>。
/// </summary>
/// <remarks>
/// 解码器持有本轮的中间状态（累积的参数片段、待下发的调用），
/// 因此不能跨请求复用。流正常结束时用 <see cref="Flush"/> 兜底下发未终结的调用。
/// </remarks>
public abstract class ChatStreamDecoder
{
    /// <summary>是否已收到协议层的终结信号。</summary>
    public bool IsTerminal { get; protected set; }

    /// <summary>本轮用量（若协议提供）。</summary>
    public UsageDetails? Usage { get; protected set; }

    /// <summary>解码一条负载，把产生的结果追加到 <paramref name="updates"/>。</summary>
    public abstract void Decode(string payload, List<ChatResponseUpdate> updates);

    /// <summary>流结束时调用：下发仍待下发的调用，避免它们丢失终态。</summary>
    public abstract IReadOnlyList<ChatResponseUpdate> Flush();
}

/// <summary>连接器共用的小工具，避免三个实现各写一遍并且各自出错。</summary>
internal static class WireFormatHelpers
{
    /// <summary>
    /// 把协议给出的 JSON 字符串解析为参数。
    /// 空串视为"无参数"；畸形 JSON 返回 null 而不是抛错——
    /// 参数不完整应由 dispatcher 拒绝，不该让连接器替它判断。
    /// </summary>
    internal static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var element = System.Text.Json.JsonDocument.Parse(json).RootElement;
            if (element.ValueKind is not System.Text.Json.JsonValueKind.Object) return null;
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>把参数对象序列化为协议要求的 JSON 字符串。</summary>
    internal static string SerializeArguments(IDictionary<string, object?>? arguments) =>
        arguments is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(arguments);

    /// <summary>只以非空字符串覆盖：多数端点的后续帧用空串重复字段，直接赋值会抹掉首帧的值。</summary>
    internal static string NonEmpty(string? candidate, string fallback) =>
        string.IsNullOrEmpty(candidate) ? fallback : candidate;

    /// <summary>非空即视为终结信号。空串与 "null" 都是"未完成"的变体。</summary>
    internal static bool IsTerminalReason(string? reason) =>
        !string.IsNullOrEmpty(reason) && reason != "null";

    internal static void ApplyBearer(HttpRequestMessage request, string apiKey) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
}
