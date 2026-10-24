using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 校验工具声明通道的契约本身，不依赖任何真实连接器：
/// 运行时写进 <see cref="ChatOptions"/>，连接器读出来，两边只共享冻结 DTO。
/// </summary>
public static class ContractChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("declarations-roundtrip", () =>
        {
            var descriptor = new ToolDescriptor(
                "read_file", "read_file", "1.0.0", "read a file",
                JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")
                    .RootElement.Clone());

            var options = new ChatOptions().Attach([descriptor]);
            var read = options.ReadToolDeclarations();

            if (read.Count != 1) return $"读回 {read.Count} 个声明，期望 1";
            if (read[0].Id != "read_file") return $"Id 不符：{read[0].Id}";
            return null;
        });

        failures += Check("absent-key-yields-empty", () =>
        {
            var options = new ChatOptions();
            return options.ReadToolDeclarations().Count == 0
                ? null
                : "未附加时读到了声明";
        });

        failures += Check("null-options-yields-empty", () =>
            ToolDeclarations.ReadToolDeclarations(null).Count == 0
                ? null
                : "null options 读到了声明");

        failures += Check("schema-is-reparsed-not-shared", () =>
        {
            var element = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
            var descriptor = new ToolDescriptor("a", "a", "1", "", element);
            var first = ToolDeclarations.ToSchemaNode(descriptor);
            var second = ToolDeclarations.ToSchemaNode(descriptor);

            // 修改第一个不应影响第二个：每次都是独立 DOM。
            first!["type"] = "mutated";
            return second!["type"]!.GetValue<string>() == "object"
                ? null
                : "schema 节点被共享，修改第一个污染了第二个";
        });

        failures += Check("openai-shape", () =>
        {
            var descriptor = new ToolDescriptor(
                "read_file", "read_file", "1.0.0", "read a file",
                JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
            var array = ToolDeclarations.ToOpenAiToolsArray([descriptor]);

            var item = array[0]!;
            if (item["type"]!.GetValue<string>() != "function") return "type 不是 function";
            var function = item["function"]!;
            if (function["name"]!.GetValue<string>() != "read_file") return "name 不符";
            return function["parameters"] is JsonObject ? null : "parameters 缺失";
        });

        failures += Check("descriptor-schema-is-frozen-copy", () =>
        {
            // dispatcher 注册时应复制 schema，注册后外部改动不得影响已发布声明。
            var dispatcher = new ToolDispatcher();
            using var document = JsonDocument.Parse("""{"type":"object","required":["path"]}""");
            var tool = new ProbeTool(document.RootElement);
            dispatcher.Register(tool);
            document.Dispose();

            var published = dispatcher.Tools.Single().InputSchema;
            return published.TryGetProperty("required", out _)
                ? null
                : "注册后 schema 失效（未复制）";
        });

        Console.WriteLine(failures == 0
            ? "CONTRACT: ALL PASS"
            : $"CONTRACT: {failures} FAILURE(S)");
        return failures;
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
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

    private sealed class ProbeTool(JsonElement schema) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "probe", "probe", "1.0.0", "probe", schema);

        public ToolPermission DescribePermission(JsonElement arguments) => ToolPermission.Unknown;

        public ToolExecutionPlan DescribeExecution(JsonElement arguments) => ToolExecutionPlan.Serial;

        public ValueTask<AgentToolResult> ExecuteAsync(
            ToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
