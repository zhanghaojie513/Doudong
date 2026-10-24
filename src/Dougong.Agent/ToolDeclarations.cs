using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Dougong.Agent;

/// <summary>
/// 运行时与连接器之间的工具声明通道。
/// </summary>
/// <remarks>
/// 为什么不直接用 <c>ChatOptions.Tools</c>：<c>Microsoft.Extensions.AI 10.11.0</c> 的
/// <see cref="AITool"/> 是抽象类且只有非公开构造函数，无法子类化，
/// 也就无法把宿主冻结的 <see cref="ToolDescriptor"/> 表达成 <c>AITool</c>。
/// 直接拼进系统提示文本又会让模型输出非结构化伪调用（真实 provider 已实测）。
/// <para>
/// 因此用一个**双方都认识的附加属性键**：运行时写，连接器读。
/// 运行时不需要知道连接器怎么序列化，连接器也不需要知道 dispatcher 的内部结构；
/// 唯一共享的是 <see cref="ToolDescriptor"/> 这个冻结 DTO。
/// </para>
/// </remarks>
public static class ToolDeclarations
{
    /// <summary>附加属性键。连接器按此键读取工具声明。</summary>
    public const string OptionsKey = "dougong.toolDeclarations";

    /// <summary>把 dispatcher 的冻结描述符写入 <see cref="ChatOptions"/>。</summary>
    public static ChatOptions Attach(this ChatOptions options, IReadOnlyList<ToolDescriptor> declarations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(declarations);
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[OptionsKey] = declarations;
        return options;
    }

    /// <summary>连接器读取工具声明；不存在时返回空集合，表示本次请求不声明工具。</summary>
    public static IReadOnlyList<ToolDescriptor> ReadToolDeclarations(this ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties) return [];
        return properties.TryGetValue(OptionsKey, out var value)
            && value is IReadOnlyList<ToolDescriptor> declarations
            ? declarations
            : [];
    }

    /// <summary>
    /// 把冻结 schema 序列化为连接器可直接放入请求体的 JsonNode。
    /// 每次调用都重新解析，避免共享可变 DOM。
    /// </summary>
    public static JsonNode? ToSchemaNode(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonNode.Parse(descriptor.InputSchema.GetRawText());
    }

    /// <summary>把一组工具声明序列化为 OpenAI 兼容的 <c>tools</c> 数组。</summary>
    /// <remarks>
    /// 这是连接器的公共实现细节：任何 OpenAI 兼容端点都需要同样的形状，
    /// 放在契约层可以避免每个连接器各写一遍并各自出错。
    /// </remarks>
    public static JsonArray ToOpenAiToolsArray(IReadOnlyList<ToolDescriptor> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var tools = new JsonArray();
        foreach (var tool in declarations)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ToSchemaNode(tool),
                },
            });
        }

        return tools;
    }
}

/// <summary>
/// 工具调用的失败诊断。与 <see cref="ToolDispatchResult.Diagnostic"/> 一起使用时，
/// 只保留下游可读的信息，不把插件异常对象带出调用边界。
/// </summary>
public sealed record ToolDiagnostic(string Code, string Message);
