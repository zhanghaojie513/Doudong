using System.Text.Json;
using Dougong.Agent;
using Dougong.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2.2 MCP 变化检测验收（docs/architecture.md §5.2）。
/// </summary>
/// <remarks>
/// 要解决的问题：远端工具集合或 schema 变化后，宿主注册的工具仍是旧的。
/// 后果不是"功能不可用"，而是更糟的**批准了 A、执行了 B**——
/// 用户批准的是旧 schema 的调用，远端正按新 schema 解释同一批参数。
/// <para>
/// 用真实 client/server 配对，通过 server 主动发 <c>notifications/tools/list_changed</c>
/// 触发，而不是直接调桥接方法——后者只能验证我的假设。
/// </para>
/// </remarks>
public static class McpReloadChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("tools-refresh-after-change", ToolsRefreshAsync);
        failures += await CheckAsync("changed-schema-invalidates-version", SchemaVersionChangesAsync);
        failures += await CheckAsync("removed-tool-stops-routing", ToolRemovalAsync);
        failures += await CheckAsync("refresh-preserves-other-servers", IsolationAsync);

        Console.WriteLine(failures == 0
            ? "MCP-RELOAD: ALL PASS"
            : $"MCP-RELOAD: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 可变的测试 server：能在运行中增删工具并主动发变更通知。
    /// </summary>
    private sealed class MutableServer : IAsyncDisposable
    {
        private readonly List<Tool> _tools = [];
        private readonly SemaphoreSlim _ready = new(0);
        private McpServer? _server;
        private Task? _loop;
        private McpClient? _client;

        public MutableServer()
        {
            _tools.Add(BuildTool("alpha", """{"type":"object","properties":{"x":{"type":"string"}}}"""));
        }

        public IReadOnlyList<Tool> Tools => _tools;

        public async Task<(McpClient Client, Func<Task> NotifyChanged)> StartAsync()
        {
            var clientToServer = new System.IO.Pipelines.Pipe();
            var serverToClient = new System.IO.Pipelines.Pipe();

            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(leaveOpen: true),
                serverToClient.Writer.AsStream(leaveOpen: true),
                "mutable-server", null);

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "mutable-server", Version = "1.0.0" },
                Capabilities = new ServerCapabilities
                {
                    // ListChanged=true 才能让 server 主动发变更通知。
                    Tools = new ToolsCapability { ListChanged = true },
                },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) =>
                        ValueTask.FromResult(new ListToolsResult { Tools = [.. _tools] }),
                    CallToolHandler = (request, _) =>
                    {
                        var text = request.Params?.Arguments is { } args
                            && args.TryGetValue("x", out var v) ? v.ToString() : "";
                        return ValueTask.FromResult(new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = $"alpha:{text}" }],
                        });
                    },
                },
            };

            _loop = Task.Run(async () =>
            {
                _server = McpServer.Create(serverTransport, options, null, null);
                _ready.Release();
                await _server.RunAsync(CancellationToken.None);
            });

            await _ready.WaitAsync(TimeSpan.FromSeconds(10));

            var clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(leaveOpen: true),
                serverToClient.Reader.AsStream(leaveOpen: true),
                null);

            _client = await McpClient.CreateAsync(clientTransport, null, null, CancellationToken.None);

            return (_client, async () =>
            {
                // 主动通知客户端工具集合已变化。
                await _server!.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, CancellationToken.None);
                // 给客户端一点时间处理通知。
                await Task.Delay(150);
            });
        }

        public void ReplaceTools(IEnumerable<Tool> tools)
        {
            _tools.Clear();
            _tools.AddRange(tools);
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null) await _client.DisposeAsync();
            _ = _loop;
        }
    }

    private static Tool BuildTool(string name, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new Tool
        {
            Name = name,
            Description = $"tool {name}",
            InputSchema = document.RootElement.Clone(),
        };
    }

    private static async Task<string?> ToolsRefreshAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher();
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            if (dispatcher.Tools.Count != 1)
                return $"初始应桥接 1 个工具，实际 {dispatcher.Tools.Count}";

            // 远端新增一个工具并通知。
            server.ReplaceTools([BuildTool("alpha", """{"type":"object"}"""), BuildTool("beta", """{"type":"object"}""")]);
            await notify();

            var names = dispatcher.Tools.Select(t => t.Name).OrderBy(x => x).ToArray();
            // 模型可见名称带 server 前缀，保证跨 server 唯一。
            return names.SequenceEqual(["demo__alpha", "demo__beta"])
                ? null : $"变更后工具未刷新：{string.Join(",", names)}";
        }
    }

    private static async Task<string?> SchemaVersionChangesAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher();
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            var before = dispatcher.Tools.Single(t => t.Name == "demo__alpha").Version;

            // 同一工具名，schema 变更。
            server.ReplaceTools([BuildTool("alpha", """{"type":"object","properties":{"y":{"type":"number"}}}""")]);
            await notify();

            var after = dispatcher.Tools.Single(t => t.Name == "demo__alpha").Version;
            return before != after
                ? null : $"schema 变更后版本未变（{before}）：旧审批会继续有效";
        }
    }

    private static async Task<string?> ToolRemovalAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher();
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            // 远端移除全部工具。
            server.ReplaceTools([]);
            await notify();

            if (dispatcher.Tools.Count != 0)
                return $"移除后仍有 {dispatcher.Tools.Count} 个工具注册";

            // 已被移除的工具不能再被派发——否则等于执行一个远端已不存在的工具。
            using var document = JsonDocument.Parse("""{"x":"1"}""");
            var result = await dispatcher.DispatchAsync(
                new ToolInvocation("c1", "demo__alpha", document.RootElement.Clone()));
            return result.Outcome == ToolCallOutcome.RejectedUnknownTool
                ? null : $"已移除的工具仍被派发：{result.Outcome}";
        }
    }

    private static async Task<string?> IsolationAsync()
    {
        await using var serverA = new MutableServer();
        await using var serverB = new MutableServer();
        var (clientA, notifyA) = await serverA.StartAsync();
        var (clientB, _) = await serverB.StartAsync();

        await using (clientA)
        await using (clientB)
        {
            var dispatcher = new ToolDispatcher();
            using var regA = await McpToolRegistry.ConnectAsync(dispatcher, "a", clientA);
            using var regB = await McpToolRegistry.ConnectAsync(dispatcher, "b", clientB);

            // 两边各有 alpha，ID 应带 server 前缀而不冲突。
            if (dispatcher.Tools.Count != 2)
                return $"应注册 2 个工具，实际 {dispatcher.Tools.Count}";

            // A 变化不应影响 B。
            serverA.ReplaceTools([BuildTool("alpha", """{"type":"object"}"""), BuildTool("gamma", """{"type":"object"}""")]);
            await notifyA();

            var ids = dispatcher.Tools.Select(t => t.Id).OrderBy(x => x).ToArray();
            var hasB = ids.Any(i => i.StartsWith("b__", StringComparison.Ordinal));
            var hasGamma = ids.Any(i => i.StartsWith("a__gamma", StringComparison.Ordinal));
            return hasB && hasGamma
                ? null : $"刷新破坏了隔离：{string.Join(",", ids)}";
        }
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
