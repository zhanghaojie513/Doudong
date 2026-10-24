namespace Dougong.Ai;

/// <summary>连接器要说的协议。同一个 provider 可能同时提供多种，必须显式指定。</summary>
/// <remarks>
/// 三者不是"同一件事的三种写法"：工具声明的形状、工具调用的形状、
/// 流式参数片段所在字段、终结信号位置、system 提示的位置全都不同。
/// 这正是必须分别实现、分别做确定性校验的原因。
/// </remarks>
public enum ChatProtocol
{
    /// <summary>OpenAI <c>/chat/completions</c>：工具调用在 <c>tool_calls</c>，终结看每个 chunk 的 <c>finish_reason</c>。</summary>
    OpenAiChatCompletions,

    /// <summary>OpenAI <c>/responses</c>：输入是 <c>input</c> 条目数组，终结是 <c>response.completed</c>。</summary>
    OpenAiResponses,

    /// <summary>Anthropic <c>/v1/messages</c>：工具调用是 <c>tool_use</c> content block，终结在 <c>message_delta.stop_reason</c>。</summary>
    AnthropicMessages,
}
