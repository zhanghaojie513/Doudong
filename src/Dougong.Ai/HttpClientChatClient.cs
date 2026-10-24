using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 把一个 <see cref="IChatWireFormat"/> 接到 HTTP 上，得到可用的 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// 只有传输与流解析在这里；协议形状全在 <see cref="IChatWireFormat"/> 里。
/// 这样同一份传输代码服务三种协议，而协议差异各自独立、可单独做确定性校验。
/// <para>
/// 不做重试、不做多模态、不做熔断：这些不属于 M1 的连接器职责，
/// 加进来只会让"失败必须可见"这条约束变得模糊。
/// </para>
/// </remarks>
public sealed class HttpClientChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly IChatWireFormat _format;

    public HttpClientChatClient(
        string baseUrl, string apiKey, string model,
        IChatWireFormat format, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(format);

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _format = format;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>本连接器使用的协议。</summary>
    public ChatProtocol Protocol => _format.Protocol;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 非流式只是"把流全部收集起来"：这样两种调用路径共用同一套解析，
        // 不会出现流式正确、非流式漏字段的双实现漂移。
        var contents = new List<AIContent>();
        UsageDetails? usage = null;

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                // 用量走标准的 UsageContent，而不是自定义附加属性：
                // M.E.AI 的中间件与 ChatResponse 聚合都识别 UsageContent，
                // 自定义键对它们不可见。
                if (content is UsageContent usageContent)
                    usage = usageContent.Details;
                else
                    contents.Add(content);
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse(message) { Usage = usage };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var body = _format.BuildBody(_model, [.. messages], options, stream: true);
        using var request = CreateRequest(body);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"provider 返回 {(int)response.StatusCode}：{error[..Math.Min(400, error.Length)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var decoder = _format.CreateDecoder();
        var updates = new List<ChatResponseUpdate>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // 只处理 SSE 的 data 行；注释行与 event 行不含负载。
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..];
            if (payload is "[DONE]") break;

            updates.Clear();
            decoder.Decode(payload, updates);
            foreach (var update in updates) yield return update;
            if (decoder.IsTerminal) break;
        }

        foreach (var update in decoder.Flush()) yield return update;

        if (decoder.Usage is { } usage)
        {
            // 用量用标准的 UsageContent 承载：M.E.AI 的聚合与中间件认这个类型，
            // 自定义附加属性对它们不可见，账目会静默丢失。
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}{_format.RelativePath}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        _format.ApplyHeaders(request, _apiKey);
        return request;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
