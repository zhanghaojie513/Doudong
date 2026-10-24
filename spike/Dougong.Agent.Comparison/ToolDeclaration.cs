using System.Text.Json;
using System.Text.Json.Nodes;
using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 连接器需要知道"宿主冻结了哪些工具"才能让模型走标准 function calling。
/// 这里用最小接口表达，避免把 M.E.AI 的 <c>AITool</c>（10.11.0 无法子类化）
/// 硬塞进公开契约。
/// </summary>
/// <remarks>
/// 正式实现里这个职责应由 <c>Dougong.Agent</c> 提供：运行时把 dispatcher 的
/// 冻结描述符交给连接器工厂，而不是让循环自己拼系统提示文本。
/// 之所以现在放在对比工程里，是因为该契约尚未定稿——真实 provider 已经证明
/// "文本描述工具"这条路径会让模型输出非结构化伪调用。
/// </remarks>
public interface IToolDeclarationSource
{
    /// <summary>模型可见的工具声明，schema 由宿主冻结。</summary>
    IReadOnlyList<ToolDescriptor> ToolDeclarations { get; }
}

/// <summary>把 dispatcher 的冻结描述符暴露给连接器。</summary>
public sealed class DispatcherToolSource(IToolDispatcher dispatcher) : IToolDeclarationSource
{
    public IReadOnlyList<ToolDescriptor> ToolDeclarations =>
        dispatcher is ToolDispatcher concrete ? concrete.Tools : [];
}

/// <summary>JSON schema 辅助。</summary>
public static class ToolSchema
{
    /// <summary>把冻结 schema 序列化为连接器可直接放入请求体的 JsonNode。</summary>
    public static JsonNode? ToNode(JsonElement schema) =>
        JsonNode.Parse(schema.GetRawText());
}
