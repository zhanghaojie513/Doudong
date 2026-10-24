using System.Reflection;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 门 3「生产路径可达」的半自动检查：确认关键能力在生产代码里被引用。
/// </summary>
/// <remarks>
/// 为什么需要它：M1 出现过「压缩能力实现完整、测试全绿，但循环从不调用它」的问题。
/// 单元测试测的是算法，没有任何检查验证算法是否被生产路径使用。
/// <para>
/// 这个检查用反射看公开类型是否出现在生产宿主的公开面上，属于**近似**：
/// 它能发现"完全没被接进去"，但发现不了"接进去了但走不到"。
/// 因此它是门 3 的必要非充分条件，人工复核仍不可省。
/// </para>
/// </remarks>
public static class ReachabilityChecks
{
    public static int Run()
    {
        var failures = 0;
        failures += Check("compaction-wired-into-runtime", CompactionWired);
        failures += Check("approval-persisted-by-dispatcher", ApprovalPersisted);
        failures += Check("usage-recorded-by-host", UsageRecorded);
        failures += Check("shell-tool-reachable", ShellReachable);

        Console.WriteLine(failures == 0
            ? "REACHABILITY: ALL PASS"
            : $"REACHABILITY: {failures} UNREACHABLE CAPABILIT{(failures == 1 ? "Y" : "IES")}");
        return failures;
    }

    /// <summary>压缩器必须出现在 <see cref="LoopAgentRuntime"/> 的构造参数里。</summary>
    private static string? CompactionWired()
    {
        var parameters = typeof(LoopAgentRuntime).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .ToArray();

        var hasCompactor = parameters.Any(p => typeof(IContextCompactor).IsAssignableFrom(p.ParameterType));
        var hasLimits = parameters.Any(p => p.ParameterType == typeof(ContextLimits));

        if (!hasCompactor) return "LoopAgentRuntime 不接受 IContextCompactor：压缩器无法接入";
        if (!hasLimits) return "LoopAgentRuntime 不接受 ContextLimits：无法表达上下文预算";
        return null;
    }

    /// <summary>dispatcher 必须能接受 store，否则审批记录无处可写。</summary>
    private static string? ApprovalPersisted()
    {
        var hasStore = typeof(ToolDispatcher).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(IAgentStore));

        if (!hasStore) return "ToolDispatcher 不接受 IAgentStore：审批决定无法持久化";

        // ApprovalLog 必须能从 store 读回，否则"记录"没有消费者。
        var readMethod = typeof(ApprovalLog).GetMethod(
            nameof(ApprovalLog.ReadAsync),
            BindingFlags.Public | BindingFlags.Static);

        return readMethod is null ? "ApprovalLog.ReadAsync 缺失：写入的记录无法恢复" : null;
    }

    /// <summary>用量记录装饰器必须存在且可包装任意 IChatClient。</summary>
    private static string? UsageRecorded()
    {
        var type = Type.GetType("Dougong.Ai.UsageRecordingChatClient, Dougong.Ai");
        if (type is null) return "UsageRecordingChatClient 类型未找到";

        var constructor = type.GetConstructors().FirstOrDefault();
        if (constructor is null) return "UsageRecordingChatClient 无公开构造函数";

        var hasInner = constructor.GetParameters().Any(p =>
            p.ParameterType == typeof(Microsoft.Extensions.AI.IChatClient));
        return hasInner ? null : "UsageRecordingChatClient 不接受 IChatClient：无法装饰连接器";
    }

    /// <summary>shell 工具必须可实现 IAgentTool，才能交给同一个 dispatcher。</summary>
    private static string? ShellReachable()
    {
        var type = Type.GetType("Dougong.Agent.ShellTool, Dougong.Agent");
        if (type is null) return "ShellTool 类型未找到";

        if (!typeof(IAgentTool).IsAssignableFrom(type))
            return "ShellTool 未实现 IAgentTool：无法经过唯一 dispatcher";

        // 声明工作区独占是 shell 的硬约束。
        var descriptorProperty = type.GetProperty(nameof(IAgentTool.Descriptor));
        return descriptorProperty is null ? "ShellTool 缺少 Descriptor" : null;
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
}
