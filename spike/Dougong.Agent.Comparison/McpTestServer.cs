using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 最小 stdio MCP server，供 CLI 端到端验证使用（不是产品代码）。
/// </summary>
internal static class McpTestServer
{
    public static async Task<int> RunAsync()
    {
        var transport = new StreamServerTransport(
            Console.OpenStandardInput(), Console.OpenStandardOutput(), "dougong-test-server", null);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "dougong-test-server", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools =
                    [
                        new Tool
                        {
                            Name = "remote_echo",
                            Description = "Echo text back from the remote MCP server.",
                            InputSchema = Json("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
                            Annotations = new ToolAnnotations { ReadOnlyHint = true },
                        },
                    ],
                }),
                CallToolHandler = (request, _) =>
                {
                    var args = request.Params?.Arguments;
                    var text = args is not null && args.TryGetValue("text", out var v) ? v.ToString() : "";
                    return ValueTask.FromResult(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"remote:{text}" }],
                    });
                },
            },
        };

        await using var server = McpServer.Create(transport, options, null, null);
        await server.RunAsync(CancellationToken.None);
        return 0;
    }

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
