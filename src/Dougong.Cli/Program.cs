using System.Text;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Cli;

/// <summary>
/// headless 参考宿主：把 Agent 契约、循环、提交屏障、审批与压缩接到一个可运行的 REPL。
/// </summary>
/// <remarks>
/// 环境变量：
/// <list type="bullet">
/// <item><c>DOUGONG_PROVIDER_BASE</c>、<c>DOUGONG_PROVIDER_KEY</c>、<c>DOUGONG_PROVIDER_MODEL</c> 必填</item>
/// <item><c>DOUGONG_WORKSPACE</c> 可选，默认当前目录</item>
/// <item><c>DOUGONG_SESSION_DIR</c> 可选，默认 ~/.dougong/sessions</item>
/// </list>
/// 密钥只从环境变量读取，不写入文件、不打印。
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var apiKey = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("dougong 0.1.0-alpha.1");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(model))
        {
            Console.Error.WriteLine(
                "缺少 provider 配置。请设置 DOUGONG_PROVIDER_BASE / KEY / MODEL 环境变量。");
            return 2;
        }

        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Directory.GetCurrentDirectory();
        var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "sessions");

        var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sessionPath = Path.Combine(sessionDir, $"{sessionId}.jsonl");

        Console.WriteLine($"dougong 0.1.0-alpha.1  模型={model}");
        Console.WriteLine($"工作区={workspace}");
        Console.WriteLine($"会话={sessionPath}");
        Console.WriteLine("输入 /exit 退出，/help 查看命令。");
        Console.WriteLine();

        await using var store = await JsonlAgentStore.OpenAsync(sessionPath, sessionId)
            .ConfigureAwait(false);
        using var client = new OpenAiChatClient(baseUrl, apiKey, model);
        var dispatcher = new ToolDispatcher(
            new ConsoleApprover(),
            new ToolResourceGate(),
            sessionId, "turn-1", workspace,
            TimeSpan.FromMinutes(5),
            new WorkspacePathGuard(workspace));

        // 注册最小工具集：只做工作区内文件读写，避免一上手就给 shell。
        dispatcher.Register(new ReadFileTool(workspace));
        dispatcher.Register(new WriteFileTool(workspace));

        var runtime = new LoopAgentRuntime(client, dispatcher, store);
        var history = new List<AgentMessage>();
        var turn = 0;

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith('/'))
            {
                if (!HandleCommand(input, store, history)) return 0;
                continue;
            }

            turn++;
            var request = new AgentRunRequest(sessionId, $"turn-{turn}", history, input);
            var assistantText = new StringBuilder();

            try
            {
                await foreach (var evt in runtime.RunAsync(request))
                {
                    switch (evt)
                    {
                        case AgentEvent.TextDelta delta:
                            assistantText.Append(delta.Text);
                            Console.Write(delta.Text);
                            break;

                        case AgentEvent.ToolCallRequested call:
                            Console.WriteLine();
                            Console.WriteLine($"  → 调用 {call.ToolId} {Truncate(call.Arguments.GetRawText())}");
                            break;

                        case AgentEvent.ToolCallCompleted done:
                            Console.WriteLine($"  ← {done.Result.Outcome}" +
                                (done.Result.Diagnostic is { } d ? $"（{d}）" : string.Empty));
                            break;

                        case AgentEvent.RunFinished finished:
                            Console.WriteLine();
                            if (finished.Outcome != AgentRunOutcome.Completed)
                                Console.Error.WriteLine($"  [结束：{finished.Outcome}] {finished.Diagnostic}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.Error.WriteLine("  [已取消]");
            }

            if (assistantText.Length > 0)
                history.Add(new AgentMessage(AgentRole.Assistant, assistantText.ToString()));
            history.Add(new AgentMessage(AgentRole.User, input));
            Console.WriteLine();
        }

        return 0;
    }

    private static bool HandleCommand(string input, IAgentStore store, List<AgentMessage> history)
    {
        switch (input.Trim().ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;

            case "/help":
                Console.WriteLine("  /exit     退出");
                Console.WriteLine("  /history  查看内存中的历史条数（会话日志以 JSONL 为准）");
                return true;

            case "/history":
                Console.WriteLine($"  内存历史 {history.Count} 条；持久化记录见会话 {store.SessionId}。");
                return true;

            default:
                Console.WriteLine($"  未知命令：{input}");
                return true;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>读取工作区内文件。</summary>
    private sealed class ReadFileTool(string workspace) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "read_file", "read_file", "1.0.0", "读取工作区内的文本文件",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Read, true,
                [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) => ToolExecutionPlan.Serial;

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
            [arguments.GetProperty("path").GetString() ?? ""];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var path = ResolvePath(workspace, invocation.Arguments.GetProperty("path").GetString()!);
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(new { path, content }));
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    /// <summary>写入工作区内文件；声明为工作区独占。</summary>
    private sealed class WriteFileTool(string workspace) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "write_file", "write_file", "1.0.0", "写入工作区内的文本文件",
            JsonDocument.Parse(
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""")
                .RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Write, true,
                [arguments.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
            new(ToolConcurrency.WorkspaceExclusive);

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) =>
            [arguments.GetProperty("path").GetString() ?? ""];

        public async ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var path = ResolvePath(workspace, invocation.Arguments.GetProperty("path").GetString()!);
            var content = invocation.Arguments.GetProperty("content").GetString() ?? "";

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(new { path, bytes = content.Length }));
            return new AgentToolResult(document.RootElement.Clone());
        }
    }

    private static string ResolvePath(string workspace, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
}
