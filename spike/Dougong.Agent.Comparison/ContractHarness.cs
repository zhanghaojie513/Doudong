using System.Text;
using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>一个反例场景：给定脚本与工具，跑运行时并检查可观察行为。</summary>
public sealed record Scenario(
    string Name,
    string Description,
    ScriptedTurn[] Turns,
    FakeTool[] Tools,
    string Expectation);

/// <summary>场景运行结果。</summary>
public sealed record ScenarioResult(
    string Scenario,
    string Candidate,
    bool Passed,
    string Detail,
    IReadOnlyList<AgentEvent> Events);

/// <summary>
/// 用同一套场景驱动任意 <see cref="IAgentRuntime"/>，只观察事件序列与副作用，
/// 不关心内部实现。这是比较两个候选的共同基准。
/// </summary>
public static class ContractHarness
{
    public static async Task<ScenarioResult> RunAsync(
        string candidate,
        IAgentRuntime runtime,
        Scenario scenario,
        IToolDispatcher dispatcher,
        BypassDetector detector,
        CancellationToken cancellationToken = default)
    {
        var events = new List<AgentEvent>();
        string detail;
        var passed = false;

        var request = new AgentRunRequest(
            "harness", $"turn-{scenario.Name}", [], "run");

        try
        {
            await foreach (var evt in runtime.RunAsync(request, cancellationToken))
            {
                events.Add(evt);
                if (evt is AgentEvent.ToolCallCompleted completed)
                    await Task.Yield();  // 让并发实现有机会继续推进
            }

            (passed, detail) = Evaluate(scenario, events, scenario.Tools, detector);
        }
        catch (OperationCanceledException)
        {
            (passed, detail) = Evaluate(scenario, events, scenario.Tools, detector);
        }
        catch (Exception ex)
        {
            detail = $"运行时抛出未归类异常：{ex.GetType().Name}: {ex.Message}";
        }

        return new ScenarioResult(scenario.Name, candidate, passed, detail, events);
    }

    private static (bool Passed, string Detail) Evaluate(
        Scenario scenario,
        IReadOnlyList<AgentEvent> events,
        FakeTool[] tools,
        BypassDetector detector)
    {
        var expectedCalls = scenario.Turns
            .SelectMany(t => t.Updates)
            .Where(u => u.FunctionName is not null && u.FunctionCallId is not null)
            .Select(u => u.FunctionCallId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var requested = events.OfType<AgentEvent.ToolCallRequested>()
            .Select(e => e.InvocationId).ToList();
        var completed = events.OfType<AgentEvent.ToolCallCompleted>()
            .Select(e => e.Result.InvocationId).ToList();

        // 每个请求的调用都必须恰好有一个终态。
        var missingTerminals = requested.Except(completed, StringComparer.Ordinal).ToList();
        if (missingTerminals.Count > 0)
            return (false, $"调用缺少终态：{string.Join(",", missingTerminals)}");

        // 旁路检测：工具执行次数不得超过 dispatcher 报告的已派发次数。
        var bypass = detector.Executions
            .Where(exec => !events.OfType<AgentEvent.ToolCallCompleted>()
                .Any(c => c.Result.Executed && $"{(c.Result.ToolId)}:{c.Result.InvocationId}" == exec))
            .ToList();
        if (bypass.Count > 0)
            return (false, $"检测到旁路执行：{string.Join(",", bypass)}");

        // 场景自身期望。
        var resolved = ScenarioExpectations.Resolve(scenario, events, tools);
        if (resolved is not null) return (false, resolved);

        var outcome = events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Outcome;
        if (outcome is null)
            return (false, "没有 RunFinished 事件，运行没有明确终止原因。");

        return (true, $"outcome={outcome}, requested={requested.Count}, completed={completed.Count}");
    }

    public static string Describe(AgentEvent evt) => evt switch
    {
        AgentEvent.RunStarted s => $"RunStarted",
        AgentEvent.TextDelta t => $"Text({Truncate(t.Text)})",
        AgentEvent.ToolCallRequested r => $"Call({r.InvocationId},{r.ToolId})",
        AgentEvent.ToolCallCompleted c => $"Done({c.Result.InvocationId},{c.Result.Outcome})",
        AgentEvent.RunFinished f => $"Finished({f.Outcome})",
        _ => evt.GetType().Name,
    };

    private static string Truncate(string text) =>
        text.Length <= 12 ? text : text[..12] + "…";

    public static string FormatTrace(IEnumerable<AgentEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events) sb.Append(Describe(evt)).Append(" → ");
        return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd(' ', '→', ' ');
    }
}
