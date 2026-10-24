using System.Text.Json;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G4 验收：工具参数必须按 schema 校验，而不只是检查顶层 type 与 required。
/// </summary>
/// <remarks>
/// 为什么现在必须做：M2.1 接入了 MCP，工具 schema 来自**不可信的远端**。
/// 只校验 required 意味着远端声明 <c>{"count":{"type":"number"}}</c> 时，
/// 模型给出 <c>{"count":"abc"}</c> 也能通过——参数会被原样发给远端，
/// 由远端的实现去处理一个它自己声明为非法的输入。
/// <para>
/// 校验器的范围是刻意限定的：只覆盖工具参数实际会用到的那部分 JSON Schema，
/// 不实现完整规范（$ref、allOf、条件 schema 等）。写了不支持的构造必须**报错**，
/// 不能静默跳过——静默跳过等于假装校验过。
/// </para>
/// </remarks>
public static class SchemaValidationChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        failures += await CheckAsync("rejects-wrong-primitive-type", WrongTypeAsync);
        failures += await CheckAsync("rejects-missing-nested-required", NestedRequiredAsync);
        failures += await CheckAsync("enforces-enum", EnumAsync);
        failures += await CheckAsync("enforces-minimum", MinimumAsync);
        failures += await CheckAsync("rejects-additional-when-declared", AdditionalPropertiesAsync);
        failures += await CheckAsync("validates-array-items", ArrayItemsAsync);
        failures += await CheckAsync("accepts-valid-nested-payload", ValidPayloadAsync);
        failures += await CheckAsync("unsupported-keyword-is-reported", UnsupportedAsync);

        Console.WriteLine(failures == 0
            ? "SCHEMA-VALIDATION: ALL PASS"
            : $"SCHEMA-VALIDATION: {failures} FAILURE(S)");
        return failures;
    }

    private static async Task<string?> WrongTypeAsync()
    {
        const string schema = """{"type":"object","properties":{"count":{"type":"number"}},"required":["count"]}""";
        return await ExpectRejectedAsync(schema, """{"count":"abc"}""", "字符串冒充 number");
    }

    private static async Task<string?> NestedRequiredAsync()
    {
        const string schema = """
            {"type":"object","properties":{"opts":{"type":"object","properties":{"mode":{"type":"string"}},"required":["mode"]}},"required":["opts"]}
            """;
        return await ExpectRejectedAsync(schema, """{"opts":{}}""", "嵌套对象缺 required");
    }

    private static async Task<string?> EnumAsync()
    {
        const string schema = """{"type":"object","properties":{"mode":{"type":"string","enum":["fast","safe"]}},"required":["mode"]}""";
        return await ExpectRejectedAsync(schema, """{"mode":"turbo"}""", "enum 之外的值");
    }

    private static async Task<string?> MinimumAsync()
    {
        const string schema = """{"type":"object","properties":{"n":{"type":"integer","minimum":3}},"required":["n"]}""";
        return await ExpectRejectedAsync(schema, """{"n":1}""", "低于 minimum");
    }

    private static async Task<string?> AdditionalPropertiesAsync()
    {
        const string schema = """
            {"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}
            """;
        return await ExpectRejectedAsync(schema, """{"a":"x","b":"y"}""", "声明了 additionalProperties:false 仍接受多余字段");
    }

    private static async Task<string?> ArrayItemsAsync()
    {
        const string schema = """
            {"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"}}},"required":["paths"]}
            """;
        return await ExpectRejectedAsync(schema, """{"paths":["a.txt",42]}""", "数组元素类型不符");
    }

    /// <summary>合法参数必须通过——否则校验器会误伤正常调用。</summary>
    private static async Task<string?> ValidPayloadAsync()
    {
        const string schema = """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "opts":{"type":"object","properties":{"mode":{"type":"string","enum":["fast","safe"]}},"required":["mode"]},
              "lines":{"type":"array","items":{"type":"string"}}
            },"required":["path"],"additionalProperties":false}
            """;

        const string arguments = """
            {"path":"a.txt","opts":{"mode":"fast"},"lines":["one","two"]}
            """;

        var result = await DispatchAsync(schema, arguments);
        return result.Outcome == ToolCallOutcome.Succeeded
            ? null : $"合法参数被拒绝：{result.Diagnostic}";
    }

    /// <summary>
    /// 不支持的 schema 关键字必须报错，不能静默跳过。
    /// </summary>
    /// <remarks>
    /// 这条是校验器自身的诚实性约束：远端可以给任意 schema，
    /// 如果遇到 <c>$ref</c> 就跳过校验，等于向使用者宣称"已校验"而实际没有。
    /// </remarks>
    private static async Task<string?> UnsupportedAsync()
    {
        const string schema = """{"type":"object","properties":{"x":{"$ref":"#/defs/a"}}}""";
        var result = await DispatchAsync(schema, """{"x":1}""");

        if (result.Outcome == ToolCallOutcome.Succeeded)
            return "含不支持关键字的 schema 被静默放行";

        return result.Diagnostic?.Contains("$ref", StringComparison.Ordinal) == true
            ? null : $"拒绝原因未指出不支持的关键字：{result.Diagnostic}";
    }

    private static async Task<string?> ExpectRejectedAsync(
        string schema, string arguments, string what)
    {
        var result = await DispatchAsync(schema, arguments);
        if (result.Outcome == ToolCallOutcome.Succeeded)
            return $"{what} 被放行";

        return result.Outcome == ToolCallOutcome.RejectedInvalidArguments
            ? null : $"预期 RejectedInvalidArguments，实际 {result.Outcome}";
    }

    private static async Task<ToolDispatchResult> DispatchAsync(string schema, string arguments)
    {
        var dispatcher = new ToolDispatcher();
        dispatcher.Register(new SchemaProbeTool(schema));

        using var document = JsonDocument.Parse(arguments);
        return await dispatcher.DispatchAsync(
            new ToolInvocation("c1", "probe", document.RootElement.Clone()));
    }

    private sealed class SchemaProbeTool(string schemaJson) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "probe", "probe", "1.0.0", "schema probe",
            JsonDocument.Parse(schemaJson).RootElement.Clone());

        public ToolPermission DescribePermission(JsonElement a) =>
            new(ToolAccess.None, true, [], [], []);
        public ToolExecutionPlan DescribeExecution(JsonElement a) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return ValueTask.FromResult(new AgentToolResult(document.RootElement.Clone()));
        }
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
