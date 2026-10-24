using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dougong.Mcp;

/// <summary>
/// 把一个 MCP 远端工具桥接成 Dougong 的 <see cref="IAgentTool"/>。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §5.2）：
/// <list type="bullet">
/// <item>远端工具必须经过同一个 dispatcher，不存在绕过入口</item>
/// <item>server 提供的 <c>annotations</c> 仅作提示，<b>不是</b>可信授权依据</item>
/// <item>工具列表 / schema / 来源身份变化时更新注册版本，使旧审批失效</item>
/// <item>连接断开或取消后不假定远端操作已撤销，缺结果调用按未知状态处理</item>
/// </list>
/// <para>
/// 为什么不继承 <see cref="McpClientTool"/>：它是 <c>AIFunction</c> 的子类，
/// 而 <c>AIFunction</c> 在 M.E.AI 10.11.0 里不可子类化（构造函数非公开）。
/// 因此这里做**适配**而不是继承，只用它暴露的数据与调用入口。
/// </para>
/// </remarks>
public sealed class McpAgentTool : IAgentTool
{
    private readonly McpClientTool _tool;
    private readonly string _serverName;
    private readonly TimeSpan _timeout;

    public McpAgentTool(McpClientTool tool, string serverName, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        _tool = tool;
        _serverName = serverName;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);

        // 分隔符必须满足 provider 对工具名的字符约束：实测 OpenAI 兼容端点
        // 只接受 ^[a-zA-Z0-9_-]+$，用 '/' 会被直接拒绝（400）。
        // 用双下划线既满足约束，也足够少见，不易与远端工具名本身冲突。
        var qualifiedName = $"{serverName}__{tool.Name}";

        Descriptor = new ToolDescriptor(
            // 工具 ID 与模型可见的 Name 都必须跨 server 唯一。
            // 若两个 server 各自暴露 alpha 而 Name 都是 "alpha"，模型无法区分，
            // dispatcher 的 Name 别名也只能指向其中一个。
            qualifiedName,
            qualifiedName,
            // 工具版本用 schema 的摘要代替：远端没有版本概念，
            // 但"schema 变了"必须能反映成版本变化，否则旧审批会继续有效。
            SchemaFingerprint(tool.JsonSchema),
            tool.Description ?? $"MCP tool '{tool.Name}' from server '{serverName}'.",
            tool.JsonSchema.ValueKind is JsonValueKind.Object
                ? tool.JsonSchema.Clone()
                : EmptySchema());
    }

    public ToolDescriptor Descriptor { get; }

    /// <summary>远端 server 名称，用于诊断与来源追踪。</summary>
    public string ServerName => _serverName;

    /// <summary>
    /// 远端的 annotations 原样暴露，**仅供展示**。
    /// </summary>
    /// <remarks>
    /// 它们来自不可信的远端，不能用来决定"是否自动批准"。
    /// 宿主如果想让 <c>readOnlyHint</c> 影响审批策略，必须自己另设白名单。
    /// </remarks>
    public ToolAnnotations? Annotations => _tool.ProtocolTool.Annotations;

    /// <summary>
    /// 权限一律报 <see cref="ToolPermission.Unknown"/>。
    /// </summary>
    /// <remarks>
    /// 不把 annotations 的 <c>readOnlyHint</c> 翻译成 <see cref="ToolAccess.Read"/>：
    /// 那是远端自己声明的，一个恶意或有 bug 的 server 可以声称只读却写文件。
    /// 未知权限默认不自动放行——这与 architecture.md §4.3 的立场一致。
    /// <para>
    /// 唯一确定的是"这次调用会通过 MCP 协议发出去"，因此带上 Network 访问类别，
    /// 但它不代表远端实际做了什么。
    /// </para>
    /// </remarks>
    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Network, IsKnown: false, Paths: [], Commands: [], Domains: []);

    /// <summary>
    /// 远端工具一律工作区独占。
    /// </summary>
    /// <remarks>
    /// 无法知道它是否修改了本工作区（甚至可能是同一份文件），
    /// 因此按最保守的档处理：不与任何其它调用重叠。
    /// </remarks>
    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.WorkspaceExclusive);

    /// <summary>
    /// 不声明文件依赖：远端可能操作任何路径，包括本机看不到的。
    /// </summary>
    public IReadOnlyList<string> DescribeFileDependencies(JsonElement arguments) => [];

    public async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var arguments = ToArgumentDictionary(invocation.Arguments);

        CallToolResult result;
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            result = await _tool.CallAsync(arguments, cancellationToken: linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 本地取消：远端可能已经执行。调用方按"结果未知"处理，
            // 这里不捕获成成功，也不伪装成未执行。
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP 工具 '{Descriptor.Id}' 超过 {_timeout.TotalSeconds:0} 秒未返回；"
                + "远端可能仍在执行，副作用可能已发生。");
        }

        // IsError=true 是"工具报告失败"，不是协议错误：仍要回传内容，
        // 但终态必须是失败，不能伪装成成功。
        var isError = result.IsError is true;
        var content = RenderContent(result);

        if (isError) throw new McpToolReportedError(Descriptor.Id, content);

        return content;
    }

    /// <summary>
    /// 把 <see cref="CallToolResult"/> 折叠成 <see cref="AgentToolResult"/>。
    /// </summary>
    /// <remarks>
    /// <c>StructuredContent</c> 优先：机器可读的结构比拼接文本有用。
    /// 没有结构化内容时回退到内容块的文本拼接——纯文本 server 很常见。
    /// 两者都没有时返回空对象并标记，不伪造成功内容。
    /// </remarks>
    private static AgentToolResult RenderContent(CallToolResult result)
    {
        if (result.StructuredContent is { } structured
            && structured.ValueKind is not JsonValueKind.Undefined)
        {
            return new AgentToolResult(structured.Clone());
        }

        var texts = new List<string>();
        if (result.Content is { Count: > 0 } blocks)
        {
            foreach (var block in blocks)
            {
                if (block is TextContentBlock text && text.Text is { Length: > 0 } value)
                    texts.Add(value);
                else if (block is not null && block.Type is { Length: > 0 } type)
                    texts.Add($"[{type}]");
            }
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            text = string.Join("\n", texts),
            contentBlocks = result.Content?.Count ?? 0,
        }));
        return new AgentToolResult(document.RootElement.Clone());
    }

    /// <summary>
    /// 把参数 JSON 转成远端调用要求的字典。
    /// 只接受对象：MCP 工具的参数是命名参数，数组或标量没有意义。
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToArgumentDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    /// <summary>schema 的稳定摘要，用作工具版本。</summary>
    private static string SchemaFingerprint(JsonElement schema)
    {
        var raw = schema.ValueKind is JsonValueKind.Undefined ? "{}" : schema.GetRawText();
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return "schema-" + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static JsonElement EmptySchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }
}

/// <summary>
/// 远端工具通过 <c>IsError</c> 报告失败时抛出。
/// </summary>
/// <remarks>
/// 单独一个异常类型是为了让 dispatcher 的失败诊断能一眼区分
/// "远端说这个调用失败了"与"传输层出错/超时"——两者的对账方式不同。
/// </remarks>
public sealed class McpToolReportedError(string toolId, AgentToolResult content)
    : Exception($"MCP 工具 '{toolId}' 报告失败。")
{
    public AgentToolResult Content { get; } = content;
}
