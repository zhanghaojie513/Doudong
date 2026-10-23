using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>只操作内存的假工具，用于确定性反例，不触碰真实文件系统。</summary>
public sealed class FakeTool(
    string id,
    string[] requiredParameters,
    Func<ToolInvocation, CancellationToken, ValueTask<AgentToolResult>>? execute = null,
    ToolAccess access = ToolAccess.Read) : IAgentTool
{
    public int ExecutionCount { get; private set; }

    public ToolDescriptor Descriptor { get; } = new(
        id,
        id,
        "1.0.0",
        $"fake tool {id}",
        BuildSchema(requiredParameters));

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(access, true, [], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) => ToolExecutionPlan.Serial;

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        if (execute is not null) return await execute(invocation, cancellationToken);

        using var doc = JsonDocument.Parse(
            JsonSerializer.Serialize(new { ok = true, tool = id }));
        return new AgentToolResult(doc.RootElement.Clone());
    }

    private static JsonElement BuildSchema(string[] required)
    {
        var schema = new
        {
            type = "object",
            properties = required.ToDictionary(
                name => name,
                _ => new { type = "string" }),
            required,
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        return doc.RootElement.Clone();
    }
}

/// <summary>记录每次执行以便检测旁路：凡未经 dispatcher 的执行都会留下证据。</summary>
public sealed class BypassDetector
{
    private readonly List<string> _executions = [];

    public IReadOnlyList<string> Executions
    {
        get { lock (_executions) { return [.. _executions]; } }
    }

    public Func<ToolInvocation, CancellationToken, ValueTask<AgentToolResult>> Track(
        string toolId, Func<ToolInvocation, CancellationToken, ValueTask<AgentToolResult>> inner) =>
        (invocation, token) =>
        {
            lock (_executions) { _executions.Add($"{toolId}:{invocation.InvocationId}"); }
            return inner(invocation, token);
        };
}
