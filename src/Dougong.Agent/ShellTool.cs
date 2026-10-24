using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 在工作区内执行命令的 shell 工具。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.3、§4.4）：
/// <list type="bullet">
/// <item>shell 按 <see cref="ToolAccess.Execute"/> 处理，取工作区独占锁——
/// 不能靠命令文本静态推断它改了什么</item>
/// <item>退出状态必须由进程明确报告，不从输出文本或提示符猜测</item>
/// <item>取消时结束整个进程树；已开始的副作用可能已发生，必须如实说明而不是报"已撤销"</item>
/// <item>输出有上限；截断要显式标记，不能让模型以为看到了全部输出</item>
/// </list>
/// <para>
/// **不宣称隔离**：命令可以读写进程权限内的一切，本类只保证它经过唯一 dispatcher、
/// 取到工作区独占锁、且结果如实上报。审批约束的是执行入口，不是沙箱。
/// </para>
/// <para>
/// 子进程默认使用最小环境变量集合（见 <see cref="ShellEnvironment"/>）：
/// 直接继承宿主环境会把 provider 密钥交给模型可以随意执行的命令。
/// </para>
/// </remarks>
public sealed class ShellTool : IAgentTool
{
    private readonly string _workspace;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputBytes;

    public ShellTool(string workspace, TimeSpan? timeout = null, int maxOutputBytes = 64 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputBytes, 1024);
        _workspace = Path.GetFullPath(workspace);
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxOutputBytes = maxOutputBytes;
    }

    public ToolDescriptor Descriptor { get; } = new(
        "shell", "shell", "1.0.0",
        "在工作区内执行一条命令并返回退出码、标准输出与标准错误。",
        JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "要执行的命令（经系统 shell 解释）" },
                "args": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "可选：作为独立参数追加到命令后，不参与 shell 解释"
                }
              },
              "required": ["command"]
            }
            """).RootElement.Clone());

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        // 无法枚举命令的影响范围，但可以如实告知将要执行什么：
        // 审批者看到的是具体命令行，而不是"某些命令"。
        var command = arguments.TryGetProperty("command", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

        return new ToolPermission(
            ToolAccess.Execute,
            IsKnown: true,
            Paths: [_workspace],
            Commands: [command],
            Domains: []);
    }

    /// <summary>始终工作区独占：shell 能改变进程与文件状态，不能与其他调用重叠。</summary>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    /// <summary>
    /// 不声明文件依赖：无法从命令文本可靠推断它读写哪些文件。
    /// 返回空表示"不做文件版本复核"，而不是"没有文件依赖"。
    /// </summary>
    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = invocation.Arguments.TryGetProperty("command", out var commandElement)
            ? commandElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("缺少 command 参数。");

        var arguments = ReadArgs(invocation.Arguments);
        var started = Start(command, arguments);

        // 输出上限是累计的：两个流各自有界，且都会标记是否被截断。
        var stdout = new BoundedBuffer(_maxOutputBytes);
        var stderr = new BoundedBuffer(_maxOutputBytes);
        var readOut = DrainAsync(started.Process.StandardOutput, stdout);
        var readErr = DrainAsync(started.Process.StandardError, stderr);

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await started.Process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消或超时：必须结束整棵进程树，否则子进程继续跑而锁被释放，
            // 下一次调用就会与"已取消"的命令重叠执行。
            KillTree(started.Process);
            await AwaitQuietlyAsync(started.Process.WaitForExitAsync(CancellationToken.None))
                .ConfigureAwait(false);
            await AwaitQuietlyAsync(readOut).ConfigureAwait(false);
            await AwaitQuietlyAsync(readErr).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                // 已执行的命令不能伪装成"未发生"：取消时它可能已经产生了副作用。
                throw new OperationCanceledException(
                    $"命令被取消并已结束进程树；副作用可能已部分发生。已捕获输出：\n{Summarize(stdout, stderr)}",
                    cancellationToken);
            }

            throw new TimeoutException(
                $"命令超过 {_timeout.TotalSeconds:0} 秒仍未结束，已结束进程树。"
                + $"退出状态未知；副作用可能已部分发生。已捕获输出：\n{Summarize(stdout, stderr)}");
        }

        // 进程已退出：读净剩余输出后再取退出码，避免读到一半。
        await AwaitQuietlyAsync(readOut).ConfigureAwait(false);
        await AwaitQuietlyAsync(readErr).ConfigureAwait(false);

        var exitCode = started.Process.ExitCode;
        var result = new
        {
            exitCode,
            stdout = stdout.Text,
            stderr = stderr.Text,
            stdoutTruncated = stdout.Truncated,
            stderrTruncated = stderr.Truncated,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return new AgentToolResult(document.RootElement.Clone());
    }

    private StartedProcess Start(string command, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 重定向 stdin 并立即关闭：命令等待输入时拿到 EOF，而不是挂住直到超时。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildWindowsCommand(command, arguments));
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildPosixCommand(command, arguments));
        }

        // 从零开始构建环境：不继承宿主环境，避免把密钥交给模型可执行的命令。
        // 必须先 Clear()：ProcessStartInfo.Environment 初始就装着父进程的全部变量，
        // 只往里加白名单项并不等于"只传这些"，密钥仍会一起被继承。
        startInfo.Environment.Clear();
        foreach (var pair in ShellEnvironment.Minimal())
            startInfo.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动命令：{ex.Message}", ex);
        }

        // 立刻关闭 stdin：命令若尝试读取输入会得到 EOF。
        try { process.StandardInput.Close(); } catch (IOException) { }

        return new StartedProcess(process);
    }

    /// <summary>
    /// Windows 下把参数按 cmd 的引用规则拼接。
    /// 参数是独立项、不参与 shell 解释，因此只需处理空白与引号。
    /// </summary>
    private static string BuildWindowsCommand(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return command;

        var builder = new StringBuilder(command);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteForCmd(argument));
        }

        return builder.ToString();
    }

    private static string QuoteForCmd(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;

        // cmd 用双引号分组；内部的引号需要成对转义（cmd 没有统一转义规则，
        // 这里按最常见的双写方式处理，并在文档里说明它不保证覆盖所有情形）。
        return "\"" + argument.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>POSIX 下用单引号包裹参数；内部单引号按标准方式转义。</summary>
    private static string BuildPosixCommand(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return command;

        var builder = new StringBuilder(command);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append('\'');
            builder.Append(argument.Replace("'", "'\\''", StringComparison.Ordinal));
            builder.Append('\'');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ReadArgs(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("args", out var args)
            || args.ValueKind is not JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in args.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { } value)
                result.Add(value);
        }

        return result;
    }

    private static async Task DrainAsync(StreamReader reader, BoundedBuffer buffer)
    {
        var chunk = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Append(chunk.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程被结束时流会关闭；已读到的内容仍然有效。
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自行退出；这不是错误。
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static string Summarize(BoundedBuffer stdout, BoundedBuffer stderr)
    {
        var builder = new StringBuilder();
        if (stdout.Text.Length > 0) builder.Append("stdout: ").AppendLine(stdout.Text);
        if (stderr.Text.Length > 0) builder.Append("stderr: ").AppendLine(stderr.Text);
        return builder.Length == 0 ? "(无输出)" : builder.ToString();
    }

    private sealed class StartedProcess(Process process)
    {
        public Process Process { get; } = process;
    }

    /// <summary>有上限的输出缓冲：超出后停止追加并标记截断。</summary>
    private sealed class BoundedBuffer(int capacity)
    {
        private readonly StringBuilder _builder = new();

        public bool Truncated { get; private set; }

        public string Text => _builder.ToString();

        public void Append(ReadOnlySpan<char> chunk)
        {
            if (Truncated) return;

            var remaining = capacity - _builder.Length;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            if (chunk.Length <= remaining)
            {
                _builder.Append(chunk);
                return;
            }

            _builder.Append(chunk[..remaining]);
            Truncated = true;
        }
    }
}

/// <summary>
/// 子进程的环境变量集合。
/// </summary>
/// <remarks>
/// 刻意采用**白名单**而不是黑名单：黑名单永远漏（自定义变量名、凭据管理器的注入变量）。
/// 宿主自己的密钥（例如 <c>DOUGONG_PROVIDER_KEY</c>）绝不能出现在模型可以随意执行的
/// 命令环境里——那等于把审批与权限描述全部绕过。
/// </remarks>
public static class ShellEnvironment
{
    private static readonly string[] PreservedNames =
    [
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "USERNAME", "USER",
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "TMPDIR",
        "COMSPEC", "SHELL", "LANG", "LC_ALL", "TERM", "PWD",
    ];

    /// <summary>取当前进程里允许传给子进程的变量。</summary>
    public static IReadOnlyDictionary<string, string> Minimal()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PreservedNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        return result;
    }

    /// <summary>判断某个变量名是否在允许传递的白名单里。</summary>
    public static bool IsPreserved(string name) =>
        PreservedNames.Any(preserved =>
            string.Equals(preserved, name, StringComparison.OrdinalIgnoreCase));
}
