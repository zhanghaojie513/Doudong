using Dougong.Agent;

namespace Dougong.Cli;

/// <summary>
/// 控制台审批器。默认拒绝未知权限，且必须显式输入 y 才批准。
/// </summary>
/// <remarks>
/// 设计约束：未知权限不得自动放行（docs/architecture.md §4.3）。
/// 因此 <c>IsKnown=false</c> 时先展示警告再询问，不因"看起来没风险"而跳过。
/// </remarks>
public sealed class ConsoleApprover : IToolApprover
{
    public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var args = request.NormalizedArguments.ValueKind is System.Text.Json.JsonValueKind.Undefined
            ? "{}"
            : request.NormalizedArguments.GetRawText();

        Console.WriteLine();
        Console.WriteLine($"── 需要审批 ──────────────────────────────");
        Console.WriteLine($"  工具    : {request.Tool.Name} (v{request.Tool.Version})");
        Console.WriteLine($"  参数    : {Truncate(args, 200)}");
        Console.WriteLine($"  工作区  : {request.Workspace}");
        Console.WriteLine($"  并发    : {request.Execution.Concurrency}");

        if (!request.Permission.IsKnown)
        {
            // 未知权限不能被当作无风险：显式警告，仍然询问而非放行。
            Console.WriteLine("  ⚠ 权限未知：该工具无法可靠描述自身访问范围。");
        }
        else
        {
            Console.WriteLine($"  权限    : {request.Permission.Access}");
            if (request.Permission.Paths.Count > 0)
                Console.WriteLine($"  路径    : {string.Join(", ", request.Permission.Paths)}");
        }

        Console.WriteLine($"  到期    : {request.Binding.ExpiresAt:HH:mm:ss}");
        Console.Write("  批准？[y/N] ");
        Console.Out.Flush();

        var answer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

        // EOF 或空输入一律视为拒绝：非交互式输入（管道/重定向）不能隐式批准。
        // 这不是缺陷而是有意的失败关闭（fail-closed）。
        if (answer is null)
        {
            Console.WriteLine();
            Console.WriteLine("  （输入结束，按拒绝处理）");
            return ToolApprovalDecision.Deny("无交互输入，按拒绝处理");
        }

        return answer.Trim().ToLowerInvariant() is "y" or "yes"
            ? ToolApprovalDecision.Allow("控制台批准")
            : ToolApprovalDecision.Deny("控制台拒绝");
    }

    private static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        // Console.ReadLine 是阻塞的；用 Task.Run 包一层并响应取消。
        var read = Task.Run(Console.ReadLine, cancellationToken);
        try { return await read.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
