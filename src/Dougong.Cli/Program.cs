using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;

namespace Dougong.Cli;

/// <summary>
/// headless 参考宿主：把 Agent 契约、循环、提交屏障、审批与压缩接到一个可运行的 REPL。
/// </summary>
/// <remarks>
/// 环境变量：
/// <list type="bullet">
/// <item><c>DOUGONG_PROVIDER_BASE</c>、<c>DOUGONG_PROVIDER_KEY</c>、<c>DOUGONG_PROVIDER_MODEL</c> 必填</item>
/// <item><c>DOUGONG_PROVIDER_PROTOCOL</c> 可选：<c>openai</c>（默认）/ <c>anthropic</c> / <c>responses</c></item>
/// <item><c>DOUGONG_ANTHROPIC_PATH</c> 可选：Anthropic 兼容层路径，默认 <c>/v1/messages</c></item>
/// <item><c>DOUGONG_WORKSPACE</c> 可选，默认当前目录</item>
/// <item><c>DOUGONG_SESSION_DIR</c> 可选，默认 ~/.dougong/sessions</item>
/// <item><c>DOUGONG_SHELL</c> 可选：设为 <c>1</c> 时启用 <c>shell</c> 工具（默认关闭）</item>
/// </list>
/// 会话参数：
/// <list type="bullet">
/// <item><c>--list</c> 列出会话（文件、记录数、序号范围、序号缺口）</item>
/// <item><c>--resume &lt;会话&gt;</c> 恢复会话；接受完整路径、会话标识或序号（1 = 最新）</item>
/// <item><c>--branch &lt;会话&gt; [--at N]</c> 从第 N 条已提交记录分叉（缺省为最后一条）</item>
/// </list>
/// 恢复或分支时若存在结果未知的调用，命令会拒绝继续：不能把"没有结果"当成"没有执行"。
/// 密钥只从环境变量读取，不写入文件、不打印。
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("dougong 0.1.0-alpha.1");
            return 0;
        }

        var config = ChatClientFactory.FromEnvironment();
        if (config is null)
        {
            Console.Error.WriteLine(
                "缺少 provider 配置。请设置 DOUGONG_PROVIDER_BASE / KEY / MODEL 环境变量。");
            return 2;
        }

        if (Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY") is not { Length: > 0 })
        {
            Console.Error.WriteLine("缺少 DOUGONG_PROVIDER_KEY 环境变量。");
            return 2;
        }

        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Directory.GetCurrentDirectory();
        var sessionDir = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dougong", "sessions");

        // --list / --resume / --branch 都是"先决定用哪个会话，再开 REPL"。
        if (args.Contains("--list"))
        {
            PrintSessionList(sessionDir);
            return 0;
        }

        var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sessionPath = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        IReadOnlyList<AgentMessage> history = [];

        if (FindArg(args, "--resume") is { } resumeArg)
        {
            var resolved = ResolveSessionPath(sessionDir, resumeArg);
            if (resolved is null)
            {
                Console.Error.WriteLine($"找不到会话：{resumeArg}");
                return 3;
            }

            var recovered = await ReplayAsync(resolved);
            if (recovered is null) return 3;

            // 需要人工对账时必须停下：不能把"没有结果"当成"没有执行"。
            if (recovered.DescribeBlockingReason() is { } blocking)
            {
                Console.Error.WriteLine($"拒绝恢复会话：{blocking}");
                return 4;
            }

            sessionPath = resolved;
            sessionId = Path.GetFileNameWithoutExtension(resolved);
            history = recovered.HistoryForResume;

            Console.WriteLine($"已恢复会话：{sessionId}（{history.Count} 条历史）");
        }

        if (FindArg(args, "--branch") is { } branchArg)
        {
            var resolved = ResolveSessionPath(sessionDir, branchArg);
            if (resolved is null)
            {
                Console.Error.WriteLine($"找不到会话：{branchArg}");
                return 3;
            }

            var recovered = await ReplayAsync(resolved);
            if (recovered is null) return 3;
            if (recovered.DescribeBlockingReason() is { } blocking)
            {
                Console.Error.WriteLine($"拒绝分支：{blocking}");
                return 4;
            }

            // 分支点默认取最后一条已提交记录；也可用 --at 指定。
            long? at = long.TryParse(FindArg(args, "--at"), out var parsedAt) ? parsedAt : null;
            var branchId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            var branchPath = Path.Combine(sessionDir, $"{branchId}.jsonl");

            BranchResult result;
            try
            {
                result = await SessionBranch.CreateAsync(resolved, branchPath, branchId, at);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"分支失败：{ex.Message}");
                return 4;
            }

            Console.WriteLine($"已从 #{result.LastSequence} 分支：{branchId}（{result.RecordCount} 条）");
            sessionPath = result.Path;
            sessionId = result.SessionId;

            var forked = await ReplayAsync(result.Path);
            history = forked?.HistoryForResume ?? [];
        }

        var shellEnabled = Environment.GetEnvironmentVariable("DOUGONG_SHELL") is "1" or "true";

        Console.WriteLine($"dougong 0.1.0-alpha.1  模型={config.Model}  协议={config.Protocol}");
        Console.WriteLine($"工作区={workspace}");
        Console.WriteLine($"会话={sessionPath}");
        Console.WriteLine($"shell 工具={(shellEnabled ? "已启用" : "未启用（DOUGONG_SHELL=1 开启）")}");

        await using var store = await JsonlAgentStore.OpenAsync(sessionPath, sessionId)
            .ConfigureAwait(false);
        using var client = ChatClientFactory.Create(config);
        var dispatcher = new ToolDispatcher(
            new ConsoleApprover(),
            new ToolResourceGate(),
            sessionId, "turn-1", workspace,
            TimeSpan.FromMinutes(5),
            new WorkspacePathGuard(workspace));

        // 默认只给工作区内文件读写。shell 权限最大，必须显式开启：
        // 它按 Execute 处理并取工作区独占锁，但无法把命令限制在工作区内。
        dispatcher.Register(new ReadFileTool(workspace));
        dispatcher.Register(new WriteFileTool(workspace));

        if (shellEnabled) dispatcher.Register(new ShellTool(workspace));

        var runtime = new LoopAgentRuntime(client, dispatcher, store);
        var messages = new List<AgentMessage>(history);
        var turn = 0;

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.StartsWith('/'))
            {
                if (!HandleCommand(input, store, messages, sessionPath)) return 0;
                continue;
            }

            turn++;
            var request = new AgentRunRequest(sessionId, $"turn-{turn}", messages, input);
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
                messages.Add(new AgentMessage(AgentRole.Assistant, assistantText.ToString()));
            messages.Add(new AgentMessage(AgentRole.User, input));
            Console.WriteLine();
        }

        return 0;
    }

    private static bool HandleCommand(
        string input, IAgentStore store, List<AgentMessage> history, string sessionPath)
    {
        switch (input.Trim().ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;

            case "/help":
                Console.WriteLine("  /exit     退出");
                Console.WriteLine("  /history  查看内存中的历史条数（会话日志以 JSONL 为准）");
                Console.WriteLine("  /where    显示当前会话文件");
                return true;

            case "/history":
                Console.WriteLine($"  内存历史 {history.Count} 条；持久化记录见会话 {store.SessionId}。");
                return true;

            case "/where":
                Console.WriteLine($"  {sessionPath}");
                return true;

            default:
                Console.WriteLine($"  未知命令：{input}");
                return true;
        }
    }

    /// <summary>读取会话列表。只显示文件与序号范围，不解析完整历史。</summary>
    private static void PrintSessionList(string sessionDir)
    {
        var sessions = SessionBranch.List(sessionDir);
        if (sessions.Count == 0)
        {
            Console.WriteLine($"目录下没有会话：{sessionDir}");
            return;
        }

        Console.WriteLine($"{"会话",-28} {"记录",6} {"序号范围",14}  {"最后写入",-20} 备注");
        foreach (var session in sessions)
        {
            var note = session.HasSequenceGap ? "序号缺口（不可续跑）" : string.Empty;
            Console.WriteLine(
                $"{session.SessionId,-28} {session.RecordCount,6} " +
                $"{session.FirstSequence + ".." + session.LastSequence,14}  " +
                $"{session.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {note}");
        }
    }

    /// <summary>把命令行参数解析成会话文件路径；接受完整路径或会话标识。</summary>
    private static string? ResolveSessionPath(string sessionDir, string argument)
    {
        if (File.Exists(argument)) return Path.GetFullPath(argument);

        var candidate = Path.Combine(sessionDir, argument + ".jsonl");
        if (File.Exists(candidate)) return candidate;

        // 也允许直接传最近会话的序号（1 = 最新），省去复制长文件名。
        if (int.TryParse(argument, out var index) && index >= 1)
        {
            var sessions = SessionBranch.List(sessionDir);
            if (index <= sessions.Count) return sessions[index - 1].Path;
        }

        return null;
    }

    /// <summary>重放会话；失败时打印原因并返回 null。</summary>
    private static async Task<RecoveredSession?> ReplayAsync(string path)
    {
        try
        {
            await using var store = await JsonlAgentStore.OpenAsync(
                path, Path.GetFileNameWithoutExtension(path)).ConfigureAwait(false);
            return await SessionRecovery.ReplayAsync(store).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or StoreCommitException)
        {
            Console.Error.WriteLine($"无法读取会话 {path}：{ex.Message}");
            return null;
        }
    }

    private static string? FindArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
        }

        return null;
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
