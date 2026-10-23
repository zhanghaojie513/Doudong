using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 按脚本产出流式更新的假模型。它不调用任何网络，因此对比的是循环实现，
/// 而不是连接器或模型能力。
/// </summary>
public sealed class ScriptedChatClient(params ScriptedTurn[] turns) : IChatClient
{
    private readonly Queue<ScriptedTurn> _turns = new(turns);
    public int CallCount { get; private set; }
    public List<IReadOnlyList<ChatMessage>> ObservedHistory { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CollectAsync(messages, options, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        ObservedHistory.Add([.. messages]);
        if (!_turns.TryDequeue(out var turn))
            throw new InvalidOperationException("脚本已耗尽：模型被调用了超出预期的次数。");

        foreach (var update in turn.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (update.AbortAfter)
                throw new InvalidOperationException("模拟响应中途中断。");
            yield return update.ToUpdate();
            if (update.Delay is { } delay) await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task<ChatResponse> CollectAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            contents.AddRange(update.Contents);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>一轮模型输出：若干流式片段。</summary>
public sealed record ScriptedTurn(params ScriptedUpdate[] Updates);

/// <summary>
/// 一个流式片段。<see cref="FunctionName"/> 非空时生成函数调用内容，否则生成文本。
/// <see cref="ArgumentsJson"/> 按原文累积，用于模拟被拆分的流式参数。
/// </summary>
public sealed record ScriptedUpdate(
    string? Text = null,
    string? FunctionCallId = null,
    string? FunctionName = null,
    string? ArgumentsJson = null,
    bool AbortAfter = false,
    TimeSpan? Delay = null)
{
    public ChatResponseUpdate ToUpdate()
    {
        if (FunctionName is null)
            return new ChatResponseUpdate(ChatRole.Assistant, Text ?? string.Empty);

        var contents = new List<AIContent>
        {
            new FunctionCallContent(
                FunctionCallId ?? string.Empty,
                FunctionName,
                ParseArguments(ArgumentsJson)),
        };

        return new ChatResponseUpdate(ChatRole.Assistant, contents);
    }

    /// <summary>
    /// 返回 null 表示“参数尚未完整”；调用方据此判断是否需要继续聚合。
    /// 刻意不抛异常：畸形 JSON 必须作为可观察结果出现在循环里，而不是在假模型内部炸掉。
    /// </summary>
    private static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (json is null) return null;
        try
        {
            var element = JsonDocument.Parse(json).RootElement;
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
