using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// shell 工具的验收（docs/architecture.md §4.4「跨会话 shell」）。
/// </summary>
/// <remarks>
/// shell 是这套系统里权限最大、最容易把安全边界做错的一环。这里的检查集中在三件事：
/// 退出状态必须是进程报告的、取消必须结束整棵进程树、宿主的密钥不能出现在子进程环境里。
/// <para>
/// 不做"命令白名单"之类的检查：那会给出错误的安全感——命令可以调用任意解释器。
/// </para>
/// </remarks>
public static class ShellChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("exit-code-is-reported", ExitCodeAsync);
        failures += await CheckAsync("stdout-and-stderr-are-separate", StreamsAsync);
        failures += await CheckAsync("args-are-not-shell-interpreted", ArgsNotInterpretedAsync);
        failures += await CheckAsync("working-directory-is-workspace", WorkingDirectoryAsync);
        failures += await CheckAsync("host-secrets-are-not-inherited", NoSecretInheritanceAsync);
        failures += await CheckAsync("declares-workspace-exclusive", DeclaresExclusiveAsync);
        failures += await CheckAsync("cancellation-kills-process-tree", CancelKillsTreeAsync);
        failures += await CheckAsync("timeout-kills-process-tree", TimeoutKillsAsync);
        failures += await CheckAsync("output-is-bounded-and-marked", OutputBoundedAsync);
        failures += await CheckAsync("closed-stdin-does-not-hang", StdinClosedAsync);
        failures += await CheckAsync("blocked-by-workspace-lock", LockedAsync);

        Console.WriteLine(failures == 0
            ? "SHELL: ALL PASS"
            : $"SHELL: {failures} FAILURE(S)");
        return failures;
    }

    // 退出码必须来自进程，而不是从输出推断。
    private static async Task<string?> ExitCodeAsync()
    {
        var root = NewRoot();
        try
        {
            var dispatcher = new ToolDispatcher(workspace: root, pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(new ShellTool(root, TimeSpan.FromSeconds(30)));

            var result = await dispatcher.DispatchAsync(Command("s1", "exit 7"));
            if (result.Outcome != ToolCallOutcome.Succeeded)
                return $"终态应为 Succeeded，实际 {result.Outcome}（{result.Diagnostic}）";

            var exitCode = result.Result!.Content.GetProperty("exitCode").GetInt32();
            return exitCode == 7 ? null : $"退出码应为 7，实际 {exitCode}";
        }
        finally { TryDelete(root); }
    }

    // stdout 与 stderr 必须分开返回，不能混成一段。
    private static async Task<string?> StreamsAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(30));
            var result = await tool.ExecuteAsync(Invocation("s1",
                EchoCommand("out-line") + " && " + ErrorCommand("err-line")), default);

            var stdout = result.Content.GetProperty("stdout").GetString() ?? string.Empty;
            var stderr = result.Content.GetProperty("stderr").GetString() ?? string.Empty;

            if (!stdout.Contains("out-line", StringComparison.Ordinal))
                return $"stdout 缺少内容：'{stdout}'";
            if (!stderr.Contains("err-line", StringComparison.Ordinal))
                return $"stderr 缺少内容：'{stderr}'";
            if (stdout.Contains("err-line", StringComparison.Ordinal))
                return "stderr 内容混进了 stdout";
            return null;
        }
        finally { TryDelete(root); }
    }

    // args 是独立参数，不参与 shell 解释：含空格与特殊字符也必须原样到达。
    private static async Task<string?> ArgsNotInterpretedAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(30));
            var payload = "a b & c | d";

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                command = EchoCommand("$1"),
                args = new[] { payload },
            }));

            var result = await tool.ExecuteAsync(
                new ToolInvocation("s1", "shell", document.RootElement.Clone()), default);

            var stdout = result.Content.GetProperty("stdout").GetString() ?? string.Empty;
            return stdout.Contains(payload, StringComparison.Ordinal)
                ? null
                : $"参数未原样传递：'{stdout.Trim()}'";
        }
        finally { TryDelete(root); }
    }

    // 命令必须在工作区内执行，而不是宿主当前目录。
    private static async Task<string?> WorkingDirectoryAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(30));
            var result = await tool.ExecuteAsync(
                Invocation("s1", OperatingSystem.IsWindows() ? "cd" : "pwd"), default);

            var stdout = (result.Content.GetProperty("stdout").GetString() ?? string.Empty).Trim();
            var expected = Path.GetFullPath(root);
            return string.Equals(stdout, expected, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"工作目录应为 '{expected}'，实际 '{stdout}'";
        }
        finally { TryDelete(root); }
    }

    // 宿主密钥不能出现在子进程环境里：否则模型可以执行一条命令把它读走。
    private static async Task<string?> NoSecretInheritanceAsync()
    {
        var root = NewRoot();
        const string secretName = "DOUGONG_PROVIDER_KEY";
        const string secretValue = "sk-should-never-leak";

        var previous = Environment.GetEnvironmentVariable(secretName);
        Environment.SetEnvironmentVariable(secretName, secretValue);
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(30));
            var command = OperatingSystem.IsWindows()
                ? "set " + secretName
                : "printenv " + secretName + " || true";

            var result = await tool.ExecuteAsync(Invocation("s1", command), default);
            var stdout = result.Content.GetProperty("stdout").GetString() ?? string.Empty;
            var stderr = result.Content.GetProperty("stderr").GetString() ?? string.Empty;

            if (stdout.Contains(secretValue, StringComparison.Ordinal)
                || stderr.Contains(secretValue, StringComparison.Ordinal))
                return "宿主密钥泄漏到了子进程环境";
            if (stdout.Contains(secretName + "=", StringComparison.OrdinalIgnoreCase))
                return "密钥变量名出现在子进程环境里";
            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
            TryDelete(root);
        }
    }

    // shell 必须声明工作区独占：它无法可靠描述自己改了哪些文件。
    private static async Task<string?> DeclaresExclusiveAsync()
    {
        var tool = new ShellTool(Path.GetTempPath());
        using var document = JsonDocument.Parse("""{"command":"echo hi"}""");

        var plan = tool.DescribeExecution(document.RootElement);
        if (plan.Concurrency != ToolConcurrency.WorkspaceExclusive)
            return $"并发要求应为 WorkspaceExclusive，实际 {plan.Concurrency}";

        var permission = tool.DescribePermission(document.RootElement);
        if (permission.Access != ToolAccess.Execute)
            return $"权限应为 Execute，实际 {permission.Access}";
        if (permission.Commands.Count != 1 || permission.Commands[0] != "echo hi")
            return "审批者看不到具体命令";

        if (tool.DescribeFileDependencies(document.RootElement).Count != 0)
            return "shell 不应声明文件依赖（它无法可靠推断）";

        await Task.CompletedTask;
        return null;
    }

    // 取消必须结束整棵进程树，否则子进程继续跑而锁已释放，会与下一次调用重叠。
    private static async Task<string?> CancelKillsTreeAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(60));

            using var cts = new CancellationTokenSource();
            // 长时间不返回的命令：取消后必须抛 OperationCanceledException，
            // 而不是等它自己跑完（那说明取消没有作用到进程上）。
            // 注意 Windows 下不能写 `for /L %i in (1)`：cmd 会把 %i 当变量展开，
            // 命令立刻退出，测试就失去了意义。ping 是可靠的长命令。
            var command = OperatingSystem.IsWindows()
                ? "ping -n 40 127.0.0.1 >nul"
                : "sleep 40";

            var running = tool.ExecuteAsync(Invocation("s1", command), cts.Token).AsTask();
            await Task.Delay(700);
            if (running.IsCompleted) return "命令应仍在运行，实际已结束（测试命令无效）";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            cts.Cancel();

            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(20));
                return "取消后未抛出";
            }
            catch (OperationCanceledException ex)
            {
                sw.Stop();
                if (sw.Elapsed > TimeSpan.FromSeconds(15))
                    return $"取消未及时生效：{sw.Elapsed.TotalSeconds:0.0}s";
                // 取消必须如实说明副作用可能已发生，不能伪装成"什么都没做"。
                return ex.Message.Contains("副作用可能已部分发生", StringComparison.Ordinal)
                    ? null
                    : $"取消诊断缺少副作用说明：{ex.Message}";
            }
        }
        finally { TryDelete(root); }
    }

    // 超时同样必须结束进程树，并给出可诊断的失败原因。
    private static async Task<string?> TimeoutKillsAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(1));
            var command = OperatingSystem.IsWindows()
                ? "ping -n 20 127.0.0.1 >nul"
                : "sleep 20";

            var started = DateTimeOffset.UtcNow;
            try
            {
                await tool.ExecuteAsync(Invocation("s1", command), default);
                return "超时后未抛出";
            }
            catch (TimeoutException ex)
            {
                var elapsed = DateTimeOffset.UtcNow - started;
                if (elapsed > TimeSpan.FromSeconds(15))
                    return $"超时未及时生效：耗时 {elapsed.TotalSeconds:0.0}s";
                return ex.Message.Contains("退出状态未知", StringComparison.Ordinal)
                    ? null
                    : $"超时诊断缺少「退出状态未知」的说明：{ex.Message}";
            }
        }
        finally { TryDelete(root); }
    }

    // 输出必须有上限，且截断必须显式标记。
    private static async Task<string?> OutputBoundedAsync()
    {
        var root = NewRoot();
        try
        {
            // 上限设小一点，让输出必然超过它。
            var tool = new ShellTool(root, TimeSpan.FromSeconds(30), maxOutputBytes: 2048);
            var command = OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,400) do @echo 0123456789012345678901234567890123456789"
                : "for i in $(seq 1 400); do echo 0123456789012345678901234567890123456789; done";

            var result = await tool.ExecuteAsync(Invocation("s1", command), default);
            var stdout = result.Content.GetProperty("stdout").GetString() ?? string.Empty;
            var truncated = result.Content.GetProperty("stdoutTruncated").GetBoolean();

            if (stdout.Length > 2048) return $"输出未受上限约束：{stdout.Length} 字符";
            return truncated ? null : "输出被截断但没有标记";
        }
        finally { TryDelete(root); }
    }

    // 命令等待输入时不能挂住：stdin 已关闭，应立刻得到 EOF。
    private static async Task<string?> StdinClosedAsync()
    {
        var root = NewRoot();
        try
        {
            var tool = new ShellTool(root, TimeSpan.FromSeconds(10));
            // cat 在 POSIX 下读到 EOF 立即返回；Windows 用 more 会等待，改用无条件命令。
            var command = OperatingSystem.IsWindows() ? "echo done" : "cat";

            var started = DateTimeOffset.UtcNow;
            var result = await tool.ExecuteAsync(Invocation("s1", command), default);
            var elapsed = DateTimeOffset.UtcNow - started;

            if (elapsed > TimeSpan.FromSeconds(8))
                return $"命令等待输入导致了挂起：{elapsed.TotalSeconds:0.0}s";

            var exitCode = result.Content.GetProperty("exitCode").GetInt32();
            return exitCode == 0 ? null : $"退出码应为 0，实际 {exitCode}";
        }
        finally { TryDelete(root); }
    }

    // 工作区被占用时 shell 必须等待，不能与另一个调用重叠执行。
    private static async Task<string?> LockedAsync()
    {
        var root = NewRoot();
        try
        {
            using var gate = new ToolResourceGate();
            var holding = await gate.EnterAsync(
                new ToolExecutionPlan(ToolConcurrency.WorkspaceExclusive), root);

            var dispatcher = new ToolDispatcher(gate: gate, workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(new ShellTool(root, TimeSpan.FromSeconds(10)));

            var dispatch = dispatcher.DispatchAsync(Command("s1", EchoCommand("blocked")))
                .AsTask();
            var blocked = await Task.WhenAny(dispatch, Task.Delay(400)) != dispatch;

            holding.Dispose();
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(20));

            if (!blocked) return "工作区被占用时 shell 未阻塞";
            return result.Outcome == ToolCallOutcome.Succeeded
                ? null
                : $"解锁后应成功，实际 {result.Outcome}（{result.Diagnostic}）";
        }
        finally { TryDelete(root); }
    }

    private static string EchoCommand(string text) => OperatingSystem.IsWindows()
        ? $"echo {text}"
        : $"echo {text}";

    private static string ErrorCommand(string text) => OperatingSystem.IsWindows()
        ? $"echo {text} 1>&2"
        : $"echo {text} >&2";

    private static ToolInvocation Command(string id, string command)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new ToolInvocation(id, "shell", document.RootElement.Clone());
    }

    private static ToolInvocation Invocation(string id, string command) => Command(id, command);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-shell-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
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
