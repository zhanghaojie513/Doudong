using System.Diagnostics;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 真实 provider 对比：用 <see cref="OpenAiCompatibleChatClient"/> 接真实端点，
/// 跑同一套反例集中的可确定性复现项与一次真实编码任务。
/// </summary>
/// <remarks>
/// 密钥只从环境变量读取；缺失时明确输出「未运行」，绝不静默跳过或伪造结果。
/// 环境变量：DOUGONG_PROVIDER_BASE / DOUGONG_PROVIDER_KEY / DOUGONG_PROVIDER_MODEL。
/// </remarks>
public static class LiveRun
{
    public static async Task<int> RunAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var apiKey = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");

        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(model))
        {
            Console.WriteLine("LIVE: 未运行（缺少 DOUGONG_PROVIDER_BASE / KEY / MODEL）");
            return 2;
        }

        var protocol = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai";
        Console.WriteLine($"LIVE: model={model}, base={baseUrl}, protocol={protocol}");
        var failures = 0;

        failures += await RunScenariosAsync(baseUrl, apiKey, model, protocol);
        failures += await RunCodingTaskAsync(baseUrl, apiKey, model, protocol);

        Console.WriteLine(failures == 0 ? "LIVE: ALL PASS" : $"LIVE: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunScenariosAsync(string baseUrl, string apiKey, string model, string protocol)
    {
        Console.WriteLine();
        Console.WriteLine("== 真实模型反例 ==");
        var failures = 0;

        // 1. 工具调用是否经由 dispatcher 且参数完整
        failures += await CheckAsync("simple-tool-call", async () =>
        {
            var dispatcher = new ToolDispatcher();
            var tool = new FakeTool("read_file", ["path"]);
            dispatcher.Register(tool);
            var client = CreateClient(baseUrl, apiKey, model, protocol);
            var runtime = new LoopAgentRuntime(client, dispatcher);

            var events = await CollectAsync(runtime,
                "Read /tmp/a.txt using the read_file tool. Do not explain, just call it.");
            var completed = events.OfType<AgentEvent.ToolCallCompleted>().ToList();
            var executed = completed.Where(c => c.Result.Executed).ToList();

            if (executed.Count == 0)
            {
                var diag = events.OfType<AgentEvent.RunFinished>().LastOrDefault()?.Diagnostic;
                return $"没有执行任何工具；diag={diag}；事件={ContractHarness.FormatTrace(events)}";
            }
            if (tool.ExecutionCount != executed.Count)
                return $"工具执行 {tool.ExecutionCount} 次但 dispatcher 报告 {executed.Count} 次（旁路？）";
            return null;
        });

        // 2. 参数经过真实流式分片后仍完整（用 required 校验兜底）
        failures += await CheckAsync("fragmented-args-real", async () =>
        {
            var dispatcher = new ToolDispatcher();
            var tool = new FakeTool("write_file", ["path", "content"]);
            dispatcher.Register(tool);
            var client = CreateClient(baseUrl, apiKey, model, protocol);
            var runtime = new LoopAgentRuntime(client, dispatcher);

            var events = await CollectAsync(runtime,
                "Write the text 'hello' to /tmp/out.txt using the write_file tool. " +
                "You must provide both path and content.");
            var completed = events.OfType<AgentEvent.ToolCallCompleted>().ToList();
            if (completed.Count == 0)
                return $"没有工具调用；事件={ContractHarness.FormatTrace(events)}";
            var bad = completed.FirstOrDefault(c =>
                c.Result.Outcome == ToolCallOutcome.RejectedInvalidArguments);
            return bad is null ? null : $"参数被判无效：{bad.Result.Diagnostic}";
        });

        // 3. 未注册的工具名不得被执行
        //    模型未必听话去调用一个不存在的工具名，它可能改调已注册的那个。
        //    因此只断言"凡被执行者，工具名必须是已注册的"。
        failures += await CheckAsync("unknown-tool-real", async () =>
        {
            var dispatcher = new ToolDispatcher();
            var registered = new FakeTool("read_file", ["path"]);
            dispatcher.Register(registered);
            var client = CreateClient(baseUrl, apiKey, model, protocol);
            var runtime = new LoopAgentRuntime(client, dispatcher);

            var events = await CollectAsync(runtime,
                "Call the tool named 'definitely_not_registered' with no arguments.");
            var executed = events.OfType<AgentEvent.ToolCallCompleted>()
                .Where(c => c.Result.Executed).ToList();

            var unexpected = executed
                .Where(c => !string.Equals(c.Result.ToolId, "read_file", StringComparison.Ordinal))
                .ToList();
            if (unexpected.Count > 0)
                return "执行了未注册工具：" +
                       string.Join(",", unexpected.Select(c => c.Result.ToolId));

            return executed.Count > registered.ExecutionCount
                ? $"执行计数不一致：dispatcher={executed.Count}, 工具={registered.ExecutionCount}"
                : null;
        });

        // 4. 纯文本不产生工具调用
        failures += await CheckAsync("text-only-real", async () =>
        {
            var dispatcher = new ToolDispatcher();
            dispatcher.Register(new FakeTool("read_file", ["path"]));
            var client = CreateClient(baseUrl, apiKey, model, protocol);
            var runtime = new LoopAgentRuntime(client, dispatcher);

            var events = await CollectAsync(runtime, "Say exactly: hello");
            var text = string.Concat(events.OfType<AgentEvent.TextDelta>().Select(e => e.Text));
            var calls = events.OfType<AgentEvent.ToolCallRequested>().Count();
            if (calls > 0) return $"纯文本请求产生了 {calls} 次工具调用";
            return string.IsNullOrWhiteSpace(text) ? "没有任何文本输出" : null;
        });

        return failures;
    }

    private static async Task<int> RunCodingTaskAsync(string baseUrl, string apiKey, string model, string protocol)
    {
        Console.WriteLine();
        Console.WriteLine("== 真实编码任务 ==");
        var workspace = Path.Combine(Path.GetTempPath(), "dougong-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "answer.txt");

        try
        {
            return await CheckAsync("coding-task", async () =>
            {
                var dispatcher = new ToolDispatcher();
                var writeCount = 0;
                var write = new FakeTool("write_file", ["path", "content"],
                    async (invocation, ct) =>
                    {
                        // 真实写盘，但限定在临时工作区内。
                        var path = invocation.Arguments.GetProperty("path").GetString() ?? string.Empty;
                        var content = invocation.Arguments.GetProperty("content").GetString() ?? string.Empty;
                        var resolved = Path.GetFullPath(Path.Combine(workspace, Path.GetFileName(path)));
                        if (!resolved.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
                            throw new UnauthorizedAccessException($"越界路径被拒绝：{resolved}");
                        await File.WriteAllTextAsync(resolved, content, ct);
                        Interlocked.Increment(ref writeCount);
                        using var doc = JsonDocument.Parse(
                            JsonSerializer.Serialize(new { ok = true, path = resolved }));
                        return new AgentToolResult(doc.RootElement.Clone());
                    },
                    ToolAccess.Write);
                var list = new FakeTool("list_files", [],
                    (_, _) =>
                    {
                        var files = Directory.GetFiles(workspace).Select(Path.GetFileName).ToArray();
                        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { files }));
                        return ValueTask.FromResult(new AgentToolResult(doc.RootElement.Clone()));
                    },
                    ToolAccess.Read);

                dispatcher.Register(write);
                dispatcher.Register(list);

                var client = CreateClient(baseUrl, apiKey, model, protocol);
                var runtime = new LoopAgentRuntime(client, dispatcher);

                var events = await CollectAsync(runtime,
                    "Create a file named answer.txt containing exactly the text: 42. " +
                    "Use the write_file tool with path 'answer.txt'. Then stop.");

                var executed = events.OfType<AgentEvent.ToolCallCompleted>()
                    .Count(c => c.Result.Executed);
                if (executed == 0)
                    return $"没有执行任何工具；事件={ContractHarness.FormatTrace(events)}";
                if (!File.Exists(target))
                    return $"文件未创建；事件={ContractHarness.FormatTrace(events)}";
                var content = (await File.ReadAllTextAsync(target)).Trim();
                return content.Contains("42", StringComparison.Ordinal)
                    ? null
                    : $"文件内容不含 42：'{content}'";
            });
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 按协议选择连接器。两者实现同一 <see cref="IChatClient"/>，
    /// 因此被比较的始终是 Dougong 的循环，而不是连接器差异。
    /// </summary>
    private static IChatClient CreateClient(
        string baseUrl, string apiKey, string model, string protocol) =>
        protocol.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicMessagesChatClient(baseUrl, apiKey, model)
            : new OpenAiCompatibleChatClient(baseUrl, apiKey, model);

    private static async Task<IReadOnlyList<AgentEvent>> CollectAsync(
        IAgentRuntime runtime, string input)
    {
        var events = new List<AgentEvent>();
        var request = new AgentRunRequest("live", "turn-1", [], input);
        await foreach (var evt in runtime.RunAsync(request)) events.Add(evt);
        return events;
    }

    private static async Task<int> CheckAsync(string name, Func<Task<string?>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var failure = await action();
            sw.Stop();
            if (failure is null)
            {
                Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
                return 0;
            }

            Console.WriteLine($"FAIL {name} ({sw.ElapsedMilliseconds} ms): {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"FAIL {name} ({sw.ElapsedMilliseconds} ms): " +
                $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
