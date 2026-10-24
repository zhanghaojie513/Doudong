using System.Text.Json;
using Dougong.Agent;
using Dougong.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dougong.Agent.Comparison;

/// <summary>
/// M2.1 MCP 工具桥接验收（docs/architecture.md §5.2）。
/// </summary>
/// <remarks>
/// 用内存双向流配对真实的 client 与 server，而不是 mock 一个 <see cref="McpClientTool"/>。
/// 理由与 M1 的协议教训一致：mock 只能验证我的假设，真实 SDK 才能裁判
/// "schema 怎么传、结果怎么回、错误怎么表示"。
/// <para>
/// 重点不是"能调通"，而是三条硬约束：
/// annotations 不能当授权依据、schema 变化必须使版本变化、远端报错不能伪装成功。
/// </para>
/// </remarks>
public static class McpBridgeChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("remote-tool-becomes-agent-tool", BridgeShape);
        failures += await CheckAsync("call-roundtrip-returns-content", Roundtrip);
        failures += await CheckAsync("annotations-are-not-authorization", AnnotationsNotTrusted);
        failures += await CheckAsync("schema-change-changes-version", SchemaVersion);
        failures += await CheckAsync("remote-error-is-failure-not-success", RemoteError);
        failures += await CheckAsync("declares-workspace-exclusive", Exclusive);
        failures += await CheckAsync("unreachable-server-is-reported", UnreachableAsync);
        failures += await CheckAsync("model-name-routes-to-registration", NameRouting);

        Console.WriteLine(failures == 0
            ? "MCP-BRIDGE: ALL PASS"
            : $"MCP-BRIDGE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>启动一个最小 MCP server，返回可用的工具集合。</summary>
    private static async Task<(McpClient Client, IReadOnlyList<McpClientTool> Tools)> StartAsync(
        Func<string, string>? echo = null)
    {
        // 双向管道：client 写入 → server 读取；server 写入 → client 读取。
        // 用 System.IO.Pipelines 而不是手写流：它内置并发安全的读写端分离。
        var clientToServer = new System.IO.Pipelines.Pipe();
        var serverToClient = new System.IO.Pipelines.Pipe();

        // AsStream 默认只能读或只能写，必须显式声明方向，否则出现 "Stream was not readable"。
        var serverTransport = new ModelContextProtocol.Server.StreamServerTransport(
            clientToServer.Reader.AsStream(leaveOpen: true),
            serverToClient.Writer.AsStream(leaveOpen: true),
            "dougong-test", null);
        // StreamClientTransport(serverInput, serverOutput)：
        // serverInput 是"写进去就发给 server"的流，因此是 clientToServer.Writer；
        // serverOutput 是"从 server 收消息"的流，因此是 serverToClient.Reader。
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(leaveOpen: true),
            serverToClient.Reader.AsStream(leaveOpen: true),
            null);

        _ = Task.Run(async () =>
        {
            var options = new ModelContextProtocol.Server.McpServerOptions
            {
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                },
                Handlers = new ModelContextProtocol.Server.McpServerHandlers
                {
                    ListToolsHandler = (request, ct) =>
                    {
                        _ = request;
                        _ = ct;
                        var tools = new List<Tool>
                        {
                            new()
                            {
                                Name = "echo",
                                Description = "Echo the input text back.",
                                InputSchema = Parse(
                                    """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
                                Annotations = new ToolAnnotations
                                {
                                    // 故意声称只读：桥接不得据此放宽权限。
                                    ReadOnlyHint = true,
                                    DestructiveHint = false,
                                    Title = "Echo (claimed read-only)",
                                },
                            },
                            new()
                            {
                                Name = "boom",
                                Description = "Always reports an error.",
                                InputSchema = Parse("""{"type":"object"}"""),
                            },
                        };

                        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
                    },
                    CallToolHandler = (request, ct) =>
                    {
                        _ = ct;
                        var name = request.Params?.Name;
                        if (name == "boom")
                        {
                            return ValueTask.FromResult(new CallToolResult
                            {
                                IsError = true,
                                Content = [new TextContentBlock { Text = "remote says no" }],
                            });
                        }

                        var text = request.Params?.Arguments is { } args
                            && args.TryGetValue("text", out var value)
                            ? value.ToString()
                            : string.Empty;

                        return ValueTask.FromResult(new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = echo?.Invoke(text) ?? text }],
                        });
                    },
                },
            };

            // 必须调用 RunAsync 才会真正开始处理消息；只 Create 不会启动循环。
            await using var server = ModelContextProtocol.Server.McpServer.Create(
                serverTransport, options, null, null);
            await server.RunAsync(CancellationToken.None);
        });

        var client = await McpClient.CreateAsync(clientTransport, null, null, CancellationToken.None);
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        return (client, [.. tools]);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string?> BridgeShape(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            if (tools.Count == 0) return "server 没有返回工具";

            var bridged = new McpAgentTool(tools.First(t => t.Name == "echo"), "demo");
            var descriptor = bridged.Descriptor;

            if (!descriptor.Id.StartsWith("demo__", StringComparison.Ordinal))
                return $"工具 ID 未带 server 前缀：{descriptor.Id}";
            if (descriptor.InputSchema.ValueKind is not JsonValueKind.Object)
                return "schema 未透传";
            if (!descriptor.InputSchema.TryGetProperty("required", out var required)
                || required.ValueKind is not JsonValueKind.Array)
                return "schema 的 required 丢失";

            // 必须是 IAgentTool，才能交给同一个 dispatcher。
            return bridged is IAgentTool ? null : "桥接对象未实现 IAgentTool";
        }
    }

    private static async Task<string?> Roundtrip(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            var bridged = new McpAgentTool(tools.First(t => t.Name == "echo"), "demo");
            using var document = JsonDocument.Parse("""{"text":"hello-mcp"}""");

            var result = await bridged.ExecuteAsync(
                new ToolInvocation("c1", bridged.Descriptor.Id, document.RootElement.Clone()),
                CancellationToken.None);

            var text = result.Content.GetProperty("text").GetString() ?? string.Empty;
            return text.Contains("hello-mcp", StringComparison.Ordinal)
                ? null : $"内容未回传：'{text}'";
        }
    }

    /// <summary>
    /// 硬约束：远端声称 readOnly 也不能被当成已知权限。
    /// </summary>
    private static async Task<string?> AnnotationsNotTrusted(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            var tool = tools.First(t => t.Name == "echo");
            var bridged = new McpAgentTool(tool, "demo");
            using var document = JsonDocument.Parse("""{"text":"x"}""");

            var permission = bridged.DescribePermission(document.RootElement.Clone());

            if (permission.IsKnown)
                return "远端工具被报成已知权限：annotations 被当成授权依据了";

            if (permission.Access.HasFlag(ToolAccess.Read))
                return "远超只读声明的风险：把远端的 readOnlyHint 翻译成了 Read 权限";

            // 但 annotations 本身仍应可见，供宿主自行决定策略。
            return bridged.Annotations?.ReadOnlyHint == true
                ? null : "annotations 未暴露给宿主（应可见但不可信）";
        }
    }

    /// <summary>
    /// schema 变化必须改变工具版本，否则旧审批会继续有效。
    /// </summary>
    private static async Task<string?> SchemaVersion(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            var tool = tools.First(t => t.Name == "echo");
            var a = new McpAgentTool(tool, "demo");

            // 同一个 tool 对象 → 同一版本。
            var b = new McpAgentTool(tool, "demo");
            if (a.Descriptor.Version != b.Descriptor.Version)
                return "相同 schema 得到不同版本";

            // 构造一个 schema 不同的 tool（用另一个工具名不影响版本判定，因为版本只由 schema 决定）。
            var changed = tools.First(t => t.Name == "boom");
            var c = new McpAgentTool(changed, "demo");
            return a.Descriptor.Version == c.Descriptor.Version
                ? "不同 schema 得到相同版本：schema 变化不会使旧审批失效"
                : null;
        }
    }

    /// <summary>远端 IsError 必须是失败终态，不能当成功。</summary>
    private static async Task<string?> RemoteError(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            var bridged = new McpAgentTool(tools.First(t => t.Name == "boom"), "demo");
            using var document = JsonDocument.Parse("""{}""");

            try
            {
                await bridged.ExecuteAsync(
                    new ToolInvocation("c1", bridged.Descriptor.Id, document.RootElement.Clone()),
                    CancellationToken.None);
                return "远端报错却按成功返回";
            }
            catch (McpToolReportedError ex)
            {
                // 仍然要带回远端说的内容，便于对账。
                var text = ex.Content.Content.GetProperty("text").GetString() ?? string.Empty;
                return text.Contains("remote says no", StringComparison.Ordinal)
                    ? null : "远端错误内容丢失";
            }
        }
    }

    /// <summary>远端工具无法描述影响范围，必须工作区独占。</summary>
    private static async Task<string?> Exclusive(Func<string, string>? echo)
    {
        var (client, tools) = await StartAsync(echo);
        await using (client)
        {
            var bridged = new McpAgentTool(tools.First(t => t.Name == "echo"), "demo");
            using var document = JsonDocument.Parse("""{}""");

            var plan = bridged.DescribeExecution(document.RootElement.Clone());
            if (plan.Concurrency != ToolConcurrency.WorkspaceExclusive)
                return $"并发要求应为 WorkspaceExclusive，实际 {plan.Concurrency}";

            if (bridged.DescribeFileDependencies(document.RootElement.Clone()).Count != 0)
                return "不应声明文件依赖（远端可能操作任何路径）";

            return null;
        }
    }

    /// <summary>连不上 server 时必须报错，不能静默返回空工具集。</summary>
    private static async Task<string?> UnreachableAsync(Func<string, string>? _)
    {
        var options = new StdioClientTransportOptions
        {
            Command = "definitely-not-a-real-command-dougong",
            Arguments = [],
        };

        try
        {
            var transport = new StdioClientTransport(options, null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = await McpClient.CreateAsync(transport, null, null, cts.Token);
            return "不存在的命令却连接成功";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 期望：明确失败，且消息里能看出是启动/连接问题。
            return ex.Message.Length > 0 ? null : "失败但没有诊断信息";
        }
        catch (OperationCanceledException)
        {
            return "取消/超时未被翻译成可诊断的失败";
        }
    }

    /// <summary>
    /// 回归：模型只看得到工具的 Name，回传调用里也是 Name。
    /// 若 dispatcher 只按 Id 查找，Id 与 Name 不同的工具就会"已注册却被判未注册"。
    /// </summary>
    /// <remarks>
    /// 不用 MCP 桥接来测：桥接现在让 Id 与 Name 相同（provider 约束所致），
    /// 测不到差异。改为直接构造一个 Id ≠ Name 的工具，钉住 dispatcher 的路由语义。
    /// </remarks>
    private static async Task<string?> NameRouting(Func<string, string>? echo)
    {
        _ = echo;
        await Task.CompletedTask;

        var tool = new AliasProbeTool();
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(tool);

        if (tool.Descriptor.Id == tool.Descriptor.Name)
            return "测试前提不成立：Id 与 Name 相同";

        using var document = JsonDocument.Parse("""{}""");

        // 按 Name 派发：模拟模型回传。
        var byName = await dispatcher.DispatchAsync(new ToolInvocation(
            "c1", tool.Descriptor.Name, document.RootElement.Clone()));
        if (byName.Outcome != ToolCallOutcome.Succeeded)
            return $"按 Name 派发失败：{byName.Outcome}（{byName.Diagnostic}）";

        // 按 Id 派发：宿主内部路径也必须可用。
        var byId = await dispatcher.DispatchAsync(new ToolInvocation(
            "c2", tool.Descriptor.Id, document.RootElement.Clone()));
        return byId.Outcome == ToolCallOutcome.Succeeded
            ? null : $"按 Id 派发失败：{byId.Outcome}（{byId.Diagnostic}）";
    }

    /// <summary>Id 与 Name 故意不同，用于验证 dispatcher 的 Name 别名路由。</summary>
    private sealed class AliasProbeTool : IAgentTool
    {
        private int _calls;

        public int ExecutionCount => Volatile.Read(ref _calls);

        public ToolDescriptor Descriptor { get; } = new(
            "internal/id-1", "probe_name", "1.0.0", "alias probe",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }

    private static async Task<int> CheckAsync(
        string name, Func<Func<string, string>?, Task<string?>> action)
    {
        try
        {
            var failure = await action(null);
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
