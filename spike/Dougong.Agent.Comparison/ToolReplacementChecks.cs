using System.Text.Json;
using Dougong.Agent;
using Dougong.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G6 验收：工具替换后旧审批必须失效（docs/architecture.md §4.3、§5.2）。
/// </summary>
/// <remarks>
/// 这是"批准了 A、执行了 B"的最后一环。tools/list_changed 之后工具的 schema 变了，
/// 若旧审批仍然有效，用户批准的是旧 schema 的调用，远端正按新 schema 解释同一批参数。
/// <para>
/// 用真实 MCP server 变更 schema 触发，而不是直接改桥接对象——
/// 后一种只能验证我的假设，测不到"通知 → 刷新 → 审批绑定更新"这条完整链路。
/// </para>
/// </remarks>
public static class ToolReplacementChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("schema-change-invalidates-approval", SchemaChangeInvalidatesAsync);
        failures += await CheckAsync("same-schema-keeps-approval-valid", SameSchemaAsync);
        failures += await CheckAsync("removed-tool-cannot-be-approved", RemovedToolAsync);
        failures += await CheckAsync("reregistered-tool-gets-new-version", ReregisterAsync);

        Console.WriteLine(failures == 0
            ? "TOOL-REPLACEMENT: ALL PASS"
            : $"TOOL-REPLACEMENT: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 核心场景：批准时的 schema 与执行时的 schema 不同 → 复核必须拒绝。
    /// </summary>
    private static async Task<string?> SchemaChangeInvalidatesAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            // 审批器在批准时记录当前绑定；远端随后改 schema，复核应发现不一致。
            ApprovalBinding? approvedAt = null;
            var approver = new CapturingApprover(binding => approvedAt = binding);

            var dispatcher = new ToolDispatcher(
                approver,
                sessionId: "s1", turnId: "t1", workspace: Path.GetTempPath());
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            using var arguments = JsonDocument.Parse("""{"x":"hello"}""");

            // 第一次调用：正常批准并执行成功。
            var first = await dispatcher.DispatchAsync(new ToolInvocation(
                "c1", "demo__alpha", arguments.RootElement.Clone()));
            if (first.Outcome != ToolCallOutcome.Succeeded)
                return $"首次调用应成功，实际 {first.Outcome}（{first.Diagnostic}）";
            if (approvedAt is null) return "审批器未收到绑定";

            var versionBefore = approvedAt.ToolVersion;

            // 远端改变 schema 并通知。
            server.ReplaceTools([BuildTool("alpha",
                """{"type":"object","properties":{"x":{"type":"string"},"y":{"type":"number"}},"required":["x","y"]}""")]);
            await notify();

            // 第二次调用：同名工具、同参数，但 schema 已变。
            // 参数本身不满足新 schema 的 required，因此会在 schema 校验阶段被拒；
            // 这里要验证的是"不会用旧审批放行"，两种拒绝都算通过。
            var second = await dispatcher.DispatchAsync(new ToolInvocation(
                "c2", "demo__alpha", arguments.RootElement.Clone()));

            if (second.Outcome == ToolCallOutcome.Succeeded)
                return "schema 变更后旧审批仍然放行：批准了 A、执行了 B";

            // 版本确实变了（这是失效的根因，单独确认）。
            var current = dispatcher.Tools.Single(t => t.Name == "demo__alpha");
            return current.Version != versionBefore
                ? null
                : $"schema 变更未改变版本（仍为 {versionBefore}），失效只是偶然";
        }
    }

    /// <summary>schema 未变时，新调用应能正常批准执行——不能因为刷新就永久失效。</summary>
    private static async Task<string?> SameSchemaAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher(
                new AllowAll(), sessionId: "s1", turnId: "t1", workspace: Path.GetTempPath());
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            using var arguments = JsonDocument.Parse("""{"x":"hello"}""");
            var first = await dispatcher.DispatchAsync(new ToolInvocation(
                "c1", "demo__alpha", arguments.RootElement.Clone()));
            if (first.Outcome != ToolCallOutcome.Succeeded)
                return $"首次调用应成功，实际 {first.Outcome}";

            // 发通知但工具集合未变。
            await notify();

            var second = await dispatcher.DispatchAsync(new ToolInvocation(
                "c2", "demo__alpha", arguments.RootElement.Clone()));
            return second.Outcome == ToolCallOutcome.Succeeded
                ? null : $"schema 未变却失效：{second.Outcome}（{second.Diagnostic}）";
        }
    }

    /// <summary>工具被移除后不能再被批准执行。</summary>
    private static async Task<string?> RemovedToolAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher(
                new AllowAll(), sessionId: "s1", turnId: "t1", workspace: Path.GetTempPath());
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            server.ReplaceTools([]);
            await notify();

            using var arguments = JsonDocument.Parse("""{"x":"hello"}""");
            var result = await dispatcher.DispatchAsync(new ToolInvocation(
                "c1", "demo__alpha", arguments.RootElement.Clone()));

            return result.Outcome == ToolCallOutcome.RejectedUnknownTool
                ? null : $"已移除的工具仍被处理：{result.Outcome}";
        }
    }

    /// <summary>同一 schema 重新注册后，版本必须稳定（否则无法判断"是否真的变了"）。</summary>
    private static async Task<string?> ReregisterAsync()
    {
        await using var server = new MutableServer();
        var (client, notify) = await server.StartAsync();
        await using (client)
        {
            var dispatcher = new ToolDispatcher();
            using var registry = await McpToolRegistry.ConnectAsync(dispatcher, "demo", client);

            var before = dispatcher.Tools.Single().Version;
            await notify();
            var after = dispatcher.Tools.Single().Version;

            return before == after
                ? null : $"相同 schema 重新注册后版本变化：{before} → {after}";
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

    private sealed class MutableServer : IAsyncDisposable
    {
        private readonly List<Tool> _tools = [];
        private readonly SemaphoreSlim _ready = new(0);
        private McpServer? _server;
        private Task? _loop;
        private McpClient? _client;

        public MutableServer()
        {
            _tools.Add(BuildTool("alpha",
                """{"type":"object","properties":{"x":{"type":"string"}},"required":["x"]}"""));
        }

        public async Task<(McpClient Client, Func<Task> NotifyChanged)> StartAsync()
        {
            var clientToServer = new System.IO.Pipelines.Pipe();
            var serverToClient = new System.IO.Pipelines.Pipe();

            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(leaveOpen: true),
                serverToClient.Writer.AsStream(leaveOpen: true),
                "replacement-server", null);

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "replacement-server", Version = "1.0.0" },
                Capabilities = new ServerCapabilities
                {
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
                await _server!.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, CancellationToken.None);
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

    private sealed class AllowAll : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolApprovalDecision.Allow("test"));
    }

    /// <summary>记录批准时的绑定，用于观察 ToolVersion 是否随 schema 变化。</summary>
    private sealed class CapturingApprover(Action<ApprovalBinding> capture) : IToolApprover
    {
        public ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            capture(request.Binding);
            return ValueTask.FromResult(ToolApprovalDecision.Allow("captured"));
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
