using Dougong.Agent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dougong.Mcp;

/// <summary>
/// 把一个 MCP server 的工具集合注册进 dispatcher，并在远端变更时刷新。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §5.2）：工具列表 / 参数 schema / 来源身份变化时
/// 更新注册版本，使旧审批失效。
/// <para>
/// 为什么要单独的注册器而不是"加载时注册一次"：远端的工具集合是**动态的**。
/// 只在连接时注册一次，会导致用户在会话中途看到的是过期工具集；
/// 更糟的是批准了旧 schema 的调用，而远端正按新 schema 解释同一批参数。
/// </para>
/// <para>
/// 刷新策略是"全量替换本 server 的工具"而不是增量 diff：增量需要判断
/// "哪个工具被移除、哪个被改"，而全量替换天然正确，代价只是多几次注册/注销。
/// 工具数量在真实场景里是几十量级，不值得为性能引入 diff 的正确性风险。
/// </para>
/// </remarks>
public sealed class McpToolRegistry : IDisposable, IAsyncDisposable
{
    private readonly IToolDispatcher _dispatcher;
    private readonly McpClient _client;
    private readonly string _serverName;
    private readonly object _sync = new();
    private readonly List<IDisposable> _registrations = [];
    private IAsyncDisposable? _notificationRegistration;
    private bool _disposed;

    private McpToolRegistry(IToolDispatcher dispatcher, McpClient client, string serverName)
    {
        _dispatcher = dispatcher;
        _client = client;
        _serverName = serverName;
    }

    /// <summary>
    /// 连接一个已初始化的 client，注册其工具并订阅变更通知。
    /// </summary>
    /// <remarks>
    /// 订阅必须在首次 <see cref="RefreshAsync"/> 之前完成，否则两次 ListTools 之间
    /// 到达的变更通知会丢失。
    /// </remarks>
    public static async Task<McpToolRegistry> ConnectAsync(
        IToolDispatcher dispatcher,
        string serverName,
        McpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(client);

        var registry = new McpToolRegistry(dispatcher, client, serverName);
        // 通知处理器返回 ValueTask；刷新是 async Task，因此包一层。
        registry._notificationRegistration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) => new ValueTask(registry.RefreshAsync(CancellationToken.None)));

        await registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return registry;
    }

    /// <summary>当前已注册的工具数。</summary>
    public int ToolCount
    {
        get { lock (_sync) return _registrations.Count; }
    }

    /// <summary>
    /// 重新拉取远端工具集合并替换本 server 的注册。
    /// </summary>
    /// <remarks>
    /// 失败时**保留旧注册**而不是清空：远端暂时不可用时，
    /// 把工具全部撤掉会让正在进行的会话突然失去能力，
    /// 而保留旧注册至少语义明确（工具存在，调用会失败并如实报告）。
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<McpClientTool> tools;
        try
        {
            tools = [.. await _client.ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false)];
        }
        catch (Exception)
        {
            // 保留旧注册，见方法注释。
            return;
        }

        // dispatcher 拒绝重复注册同一个 Id，因此刷新必须是"先全部注销、再全部注册"。
        // 这意味着存在一个极短的工具不可用窗口。接受这个窗口的理由：
        // 此时的正确行为是"工具暂时不在列表里"，而不是"工具在列表里但指向旧 schema"。
        // 后者会让模型按旧 schema 生成参数、远端正按新 schema 解释——
        // 也就是"批准了 A、执行了 B"，比短暂不可用危险得多。
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var registration in _registrations) registration.Dispose();
            _registrations.Clear();
        }

        foreach (var tool in tools)
        {
            IDisposable registration;
            try
            {
                registration = _dispatcher.Register(new McpAgentTool(tool, _serverName));
            }
            catch (InvalidOperationException)
            {
                // 同名冲突（例如远端返回了重复工具名）：跳过该工具而非整体失败。
                continue;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    registration.Dispose();
                    return;
                }

                _registrations.Add(registration);
            }
        }
    }

    /// <summary>异步释放：等待通知订阅真正注销完，供 async 上下文使用。</summary>
    public async ValueTask DisposeAsync()
    {
        List<IDisposable> registrations;
        IAsyncDisposable? subscription;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
            subscription = _notificationRegistration;
            _notificationRegistration = null;
        }

        foreach (var registration in registrations) registration.Dispose();
        if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task DisposeSubscriptionAsync(IAsyncDisposable subscription)
    {
        try { await subscription.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* 注销失败不影响已释放的工具。 */ }
    }

    /// <summary>
    /// 释放本注册器的全部工具与通知订阅。
    /// </summary>
    /// <remarks>
    /// 只注销自己注册的工具，不影响其它 server 的注册——
    /// dispatcher 是共享的，误删别人的工具会让无关会话突然失去能力。
    /// </remarks>
    public void Dispose()
    {
        List<IDisposable> registrations;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        foreach (var registration in registrations) registration.Dispose();

        // 通知订阅是 IAsyncDisposable：这里同步释放会阻塞，但 Dispose 是同步契约。
        // 用 fire-and-forget 并在异常时静默——注销失败不影响已释放的工具。
        if (_notificationRegistration is { } subscription)
        {
            _notificationRegistration = null;
            _ = DisposeSubscriptionAsync(subscription);
        }
    }
}
