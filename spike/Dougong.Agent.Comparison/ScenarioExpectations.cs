using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>各场景期望的可观察行为。返回 null 表示通过，否则返回失败原因。</summary>
public static class ScenarioExpectations
{
    /// <summary>必须被拒绝、且不得执行工具的终态集合。</summary>
    private static readonly HashSet<ToolCallOutcome> NonExecuting =
    [
        ToolCallOutcome.RejectedInvalidArguments,
        ToolCallOutcome.RejectedDuplicateInvocation,
        ToolCallOutcome.RejectedUnknownTool,
        ToolCallOutcome.CancelledBeforeExecution,
    ];

    public static string? Resolve(
        Scenario scenario, IReadOnlyList<AgentEvent> events, FakeTool[] tools)
    {
        var completions = events.OfType<AgentEvent.ToolCallCompleted>()
            .Select(e => e.Result).ToList();
        var executions = tools.Sum(t => t.ExecutionCount);

        switch (scenario.Name)
        {
            case "nested-arguments":
                var nested = completions.FirstOrDefault();
                if (nested is null || nested.Outcome != ToolCallOutcome.Succeeded)
                    return $"期望成功，实际 {nested?.Outcome.ToString() ?? "无调用"}";
                return null;

            case "fragmented-arguments":
                // 参数分片：必须聚合完整后才派发，且成功。
                return completions.Count switch
                {
                    0 => "没有派发任何调用。",
                    _ when completions.Any(c => c.Outcome != ToolCallOutcome.Succeeded)
                        => $"期望全部成功，实际：{string.Join(",", completions.Select(c => c.Outcome))}",
                    _ => null,
                };

            case "multiple-tools":
                return completions.Count < 2
                    ? $"期望两个调用，实际 {completions.Count}。"
                    : null;

            case "duplicate-invocation-id":
            case "missing-invocation-id":
                return completions.Any(c => c.Executed)
                    ? "重复或缺失调用 ID 的调用被执行了。"
                    : null;

            case "malformed-json":
                return executions > 0 ? "畸形 JSON 的调用被执行了。" : null;

            case "schema-mismatch":
                return completions.Any(c => c.Outcome == ToolCallOutcome.Succeeded)
                    ? "不满足 schema 的调用被执行成功。"
                    : null;

            case "response-aborted":
                return completions.Any(c => c.Executed)
                    ? "响应中断后仍有调用被执行。"
                    : null;

            case "tool-throws":
                return completions.All(c => c.Outcome == ToolCallOutcome.Succeeded)
                    ? "工具抛异常却报告成功。"
                    : null;

            case "cancellation":
                return completions.Any(c => c.Outcome is ToolCallOutcome.Succeeded)
                    ? "取消后仍有调用报告成功。"
                    : null;

            case "text-streaming":
                return events.OfType<AgentEvent.TextDelta>().Any()
                    ? null
                    : "没有任何 TextDelta，流式输出退化。";

            case "details-not-in-history":
                var finished = events.OfType<AgentEvent.RunFinished>().LastOrDefault();
                return finished is null ? "没有 RunFinished。" : null;

            default:
                return null;
        }
    }
}
