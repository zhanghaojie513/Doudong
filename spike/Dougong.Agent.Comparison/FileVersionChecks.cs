using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 文件版本复核与工作区路径守卫的确定性验证。
/// 重点是"审批后文件被改动必须拒绝"，以及"越界路径必须拦下"。
/// </summary>
public static class FileVersionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("file-change-invalidates-approval", FileChangeInvalidatesAsync);
        failures += await CheckAsync("unchanged-file-still-executes", UnchangedFileExecutesAsync);
        failures += await CheckAsync("missing-file-created-invalidates", FileCreatedInvalidatesAsync);
        failures += await CheckAsync("path-escape-is-rejected", PathEscapeRejectedAsync);
        failures += await CheckAsync("dotdot-escape-is-rejected", DotDotEscapeRejectedAsync);
        failures += await CheckAsync("case-insensitive-root-match", CaseInsensitiveAsync);
        failures += await CheckAsync("file-dependency-without-guard-fails", NoGuardFailsAsync);
        failures += await CheckAsync("version-set-diff-detects-content", VersionDiffAsync);

        Console.WriteLine(failures == 0
            ? "FILEVERSION: ALL PASS"
            : $"FILEVERSION: {failures} FAILURE(S)");
        return failures;
    }

    // 审批后文件内容被改：必须拒绝。
    private static async Task<string?> FileChangeInvalidatesAsync()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(target, "original");

            var tool = new FileTool("write_file", target);
            var dispatcher = new ToolDispatcher(
                new Approver(async () =>
                {
                    // 模拟"批准之后、执行之前"文件被替换。
                    await File.WriteAllTextAsync(target, "tampered");
                    return ToolApprovalDecision.Allow();
                }),
                workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var result = await dispatcher.DispatchAsync(Invocation("c1", tool));
            if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
                return $"文件变化未被拦截，结果 {result.Outcome}";
            return tool.ExecutionCount == 0 ? null : $"变化后仍执行 {tool.ExecutionCount} 次";
        }
        finally { TryDelete(root); }
    }

    private static async Task<string?> UnchangedFileExecutesAsync()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(target, "stable");

            var tool = new FileTool("write_file", target);
            var dispatcher = new ToolDispatcher(
                new Approver(() => Task.FromResult(ToolApprovalDecision.Allow())),
                workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var result = await dispatcher.DispatchAsync(Invocation("c1", tool));
            if (result.Outcome != ToolCallOutcome.Succeeded) return $"结果 {result.Outcome}";
            return tool.ExecutionCount == 1 ? null : $"执行 {tool.ExecutionCount} 次";
        }
        finally { TryDelete(root); }
    }

    // 审批时文件不存在，执行前被创建：同样是变化。
    private static async Task<string?> FileCreatedInvalidatesAsync()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "new.txt");

            var tool = new FileTool("write_file", target);
            var dispatcher = new ToolDispatcher(
                new Approver(async () =>
                {
                    await File.WriteAllTextAsync(target, "appeared");
                    return ToolApprovalDecision.Allow();
                }),
                workspace: root,
                pathGuard: new WorkspacePathGuard(root));
            dispatcher.Register(tool);

            var result = await dispatcher.DispatchAsync(Invocation("c1", tool));
            if (result.Outcome != ToolCallOutcome.RejectedNotApproved)
                return $"新建文件未被识别为变化，结果 {result.Outcome}";
            return tool.ExecutionCount == 0 ? null : $"变化后仍执行 {tool.ExecutionCount} 次";
        }
        finally { TryDelete(root); }
    }

    private static Task<string?> PathEscapeRejectedAsync()
    {
        var root = NewTempRoot();
        try
        {
            var guard = new WorkspacePathGuard(root);
            var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.txt"));
            var ok = guard.TryResolve(outside, out _, out _);
            return Task.FromResult<string?>(ok ? "绝对路径越界未被拒绝" : null);
        }
        finally { TryDelete(root); }
    }

    private static Task<string?> DotDotEscapeRejectedAsync()
    {
        var root = NewTempRoot();
        try
        {
            var guard = new WorkspacePathGuard(root);
            var ok = guard.TryResolve("../../etc/passwd", out _, out _);
            return Task.FromResult<string?>(ok ? "../ 越界未被拒绝" : null);
        }
        finally { TryDelete(root); }
    }

    private static Task<string?> CaseInsensitiveAsync()
    {
        var root = NewTempRoot();
        try
        {
            var guard = new WorkspacePathGuard(root);
            var upper = Path.Combine(root.ToUpperInvariant(), "a.txt");
            var ok = guard.TryResolve(upper, out _, out _);
            return Task.FromResult<string?>(ok ? null : "大写路径被误判为越界");
        }
        finally { TryDelete(root); }
    }

    // 声明了文件依赖但没有守卫：必须拒绝执行而不是跳过检查。
    private static async Task<string?> NoGuardFailsAsync()
    {
        var root = NewTempRoot();
        try
        {
            var tool = new FileTool("write_file", Path.Combine(root, "a.txt"));
            var dispatcher = new ToolDispatcher(
                new Approver(() => Task.FromResult(ToolApprovalDecision.Allow())),
                workspace: root);
            dispatcher.Register(tool);

            var result = await dispatcher.DispatchAsync(Invocation("c1", tool));
            if (result.Outcome is ToolCallOutcome.Succeeded)
                return "缺少守卫时仍然执行了声明文件依赖的工具";
            return tool.ExecutionCount == 0 ? null : $"执行 {tool.ExecutionCount} 次";
        }
        finally { TryDelete(root); }
    }

    private static Task<string?> VersionDiffAsync()
    {
        var root = NewTempRoot();
        try
        {
            var target = Path.Combine(root, "a.txt");
            File.WriteAllText(target, "one");
            var before = FileVersionSet.Capture([target]);

            var unchanged = before.Diff(FileVersionSet.Capture([target]));
            if (unchanged is not null) return Task.FromResult<string?>($"未变化时误报：{unchanged}");

            File.WriteAllText(target, "two");
            var changed = before.Diff(FileVersionSet.Capture([target]));
            return Task.FromResult<string?>(changed is null ? "内容变化未被检测到" : null);
        }
        finally { TryDelete(root); }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dougong-fv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static ToolInvocation Invocation(string id, IAgentTool tool)
    {
        using var document = JsonDocument.Parse("""{"path":"a.txt"}""");
        return new ToolInvocation(id, tool.Descriptor.Id, document.RootElement.Clone());
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

    private sealed class Approver(Func<Task<ToolApprovalDecision>> decide) : IToolApprover
    {
        public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            await decide().ConfigureAwait(false);
    }

    /// <summary>声明对指定文件依赖的工具。</summary>
    private sealed class FileTool(string id, string dependency) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            id, id, "1.0.0", "file tool",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement arguments) =>
            new(ToolAccess.Write, true, [dependency], [], []);

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) => ToolExecutionPlan.Serial;

        public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [dependency];

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
    }
}
