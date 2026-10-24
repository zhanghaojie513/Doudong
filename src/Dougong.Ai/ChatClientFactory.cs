using Microsoft.Extensions.AI;

namespace Dougong.Ai;

/// <summary>
/// 一个具名 provider 配置：端点、模型、密钥来源与线协议。
/// </summary>
/// <remarks>
/// 密钥不放在这里，只放"从哪里取密钥"。配置对象可能被记录、序列化或打印，
/// 明文密钥一旦进入它就是事故；M1 已发生过一次误把密钥打到终端的事。
/// </remarks>
public sealed record ChatProviderConfig
{
    /// <summary>配置名，用于在多个 provider 之间选择。</summary>
    public required string Name { get; init; }

    /// <summary>API 基地址，例如 <c>https://api.deepseek.com</c>。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>模型标识。</summary>
    public required string Model { get; init; }

    /// <summary>线协议。同一 provider 可能同时支持多种，必须显式指定。</summary>
    public ChatProtocol Protocol { get; init; } = ChatProtocol.OpenAiChatCompletions;

    /// <summary>读取密钥的环境变量名。为空表示该端点无需鉴权。</summary>
    public string? ApiKeyEnvironmentVariable { get; init; }

    /// <summary>
    /// Anthropic 兼容层的端点路径覆盖。多数是 <c>/v1/messages</c>；
    /// DeepSeek 挂在 <c>/anthropic/v1/messages</c>。仅对 <see cref="ChatProtocol.AnthropicMessages"/> 有效。
    /// </summary>
    public string? MessagesPath { get; init; }
}

/// <summary>
/// 按具名配置建立 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// 存在的理由只有一个：让"用哪个 provider"成为配置，而不是散落在宿主里的一堆 if。
/// 连接器本身不含重试与路由，这里也不加——那会让"失败必须可见"变模糊。
/// </remarks>
public static class ChatClientFactory
{
    /// <summary>按配置建立连接器；密钥只从环境变量读取。</summary>
    /// <exception cref="InvalidOperationException">配置缺少必需的密钥环境变量或环境变量未设置。</exception>
    public static IChatClient Create(ChatProviderConfig config, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        string apiKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(config.ApiKeyEnvironmentVariable))
        {
            apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"provider '{config.Name}' 需要环境变量 '{config.ApiKeyEnvironmentVariable}'，但它未设置。");
        }

        return Create(config, apiKey, http);
    }

    /// <summary>
    /// 用显式传入的密钥建立连接器。仅用于宿主自己已有密钥通道的场景
    /// （例如从凭据管理器读取），不要用于把密钥写进配置文件。
    /// </summary>
    public static IChatClient Create(
        ChatProviderConfig config, string apiKey, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        IChatWireFormat format = config.Protocol switch
        {
            ChatProtocol.OpenAiChatCompletions => new OpenAiChatCompletionsFormat(),
            ChatProtocol.OpenAiResponses => new OpenAiResponsesFormat(),
            ChatProtocol.AnthropicMessages => new AnthropicMessagesFormat(
                config.MessagesPath ?? "/v1/messages"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(config), config.Protocol, "未知的线协议。"),
        };

        return new HttpClientChatClient(config.BaseUrl, apiKey, config.Model, format, http);
    }

    /// <summary>
    /// 从环境变量读一份配置：<c>DOUGONG_PROVIDER_BASE</c>、<c>DOUGONG_PROVIDER_KEY</c>、
    /// <c>DOUGONG_PROVIDER_MODEL</c>、<c>DOUGONG_PROVIDER_PROTOCOL</c>、<c>DOUGONG_ANTHROPIC_PATH</c>。
    /// </summary>
    public static ChatProviderConfig? FromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return null;

        var protocol = (Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai")
            .ToLowerInvariant() switch
            {
                "anthropic" => ChatProtocol.AnthropicMessages,
                "responses" => ChatProtocol.OpenAiResponses,
                _ => ChatProtocol.OpenAiChatCompletions,
            };

        return new ChatProviderConfig
        {
            Name = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_NAME") ?? "default",
            BaseUrl = baseUrl,
            Model = model,
            Protocol = protocol,
            ApiKeyEnvironmentVariable = "DOUGONG_PROVIDER_KEY",
            MessagesPath = Environment.GetEnvironmentVariable("DOUGONG_ANTHROPIC_PATH"),
        };
    }
}
