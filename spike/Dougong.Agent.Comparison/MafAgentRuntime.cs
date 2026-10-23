using System.Runtime.CompilerServices;
using System.Text;
using Dougong.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 候选 A：用 MAF 的 <see cref="ChatClientAgent"/> 包装模型调用，工具仍经由
/// Dougong 的 <see cref="IToolDispatcher"/> 执行。
/// </summary>
/// <remarks>
/// 关键设计决定：**不向 MAF 传递任何工具（<c>tools</c> / <c>ChatOptions.Tools</c>）**。
/// 这样 MAF 不会自动调用任何函数，工具 schema 只以文本形式注入系统提示，
/// 函数调用由本类解析后交给 dispatcher。
/// 理由见 docs/m1-candidate-comparison.md：MAF 1.26.0 关闭旁路所需的
/// <c>EnableInvocableFunctionBypassing</c> 带 ExperimentalAttribute，不适合作为生产依赖。
/// </remarks>
public sealed class MafAgentRuntime(IChatClient client, IToolDispatcher dispatcher) : IAgentRuntime
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentEvent.RunStarted(request.SessionId, request.TurnId);

        // 刻意不传 tools：任何自动函数调用都必须在这里被关掉。
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = "dougong-comparison",
                ChatOptions = new ChatOptions(),
            });

        var history = new List<ChatMessage>();
        foreach (var message in request.History)
            history.Add(new ChatMessage(ToRole(message.Role), message.Content));
        if (dispatcher.Tools.Count > 0)
            history.Add(new ChatMessage(ChatRole.System, DescribeTools(dispatcher.Tools)));
        history.Add(new ChatMessage(ChatRole.User, request.UserInput));

        for (var round = 0; round < request.MaxToolRounds; round++)
        {
            var text = new StringBuilder();
            var calls = new List<PendingCall>();
            string? failure = null;
            var cancelled = false;

            IAsyncEnumerator<AgentResponseUpdate> enumerator =
                agent.RunStreamingAsync(history, cancellationToken: cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            await using (enumerator)
            {
                while (true)
                {
                    bool moved;
                    try { moved = await enumerator.MoveNextAsync(); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        moved = false;
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        moved = false;
                        failure = $"模型流中断：{ex.GetType().Name}: {ex.Message}";
                    }

                    if (cancelled || failure is not null) break;
                    if (!moved) break;

                    foreach (var content in enumerator.Current.Contents)
                    {
                        switch (content)
                        {
                            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                                text.Append(textContent.Text);
                                yield return new AgentEvent.TextDelta(textContent.Text);
                                break;

                            case FunctionCallContent functionCall:
                                if (string.IsNullOrEmpty(functionCall.CallId))
                                {
                                    calls.Add(new PendingCall(string.Empty, functionCall.Name,
                                        [], IsMalformed: true));
                                    break;
                                }

                                var index = calls.FindIndex(c => c.Id == functionCall.CallId);
                                if (index < 0)
                                {
                                    calls.Add(new PendingCall(
                                        functionCall.CallId, functionCall.Name,
                                        functionCall.Arguments is null
                                            ? []
                                            : new Dictionary<string, object?>(functionCall.Arguments),
                                        IsMalformed: false));
                                }
                                else
                                {
                                    var merged = new Dictionary<string, object?>(calls[index].Arguments);
                                    if (functionCall.Arguments is not null)
                                        foreach (var pair in functionCall.Arguments)
                                            if (merged.TryGetValue(pair.Key, out var previous)
                                                && !Equals(previous, pair.Value))
                                                calls[index] = calls[index] with { IsMalformed = true };
                                            else
                                                merged[pair.Key] = pair.Value;
                                    calls[index] = calls[index] with { Arguments = merged };
                                }

                                break;
                        }
                    }
                }
            }

            if (cancelled)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Cancelled, "模型调用被取消。");
                yield break;
            }

            if (failure is not null)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Failed, failure);
                yield break;
            }

            if (text.Length > 0)
                history.Add(new ChatMessage(ChatRole.Assistant, text.ToString()));

            if (calls.Count == 0)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Completed);
                yield break;
            }

            if (calls.Any(c => c.IsMalformed))
            {
                foreach (var call in calls)
                    yield return new AgentEvent.ToolCallCompleted(new ToolDispatchResult(
                        call.Id, call.ToolId, ToolCallOutcome.RejectedInvalidArguments,
                        Diagnostic: "调用 ID 缺失或参数冲突。"));
                yield return new AgentEvent.RunFinished(
                    AgentRunOutcome.InvalidModelResponse, "模型响应包含无效调用。");
                yield break;
            }

            var results = new List<string>();
            foreach (var call in calls)
            {
                var args = System.Text.Json.JsonSerializer.SerializeToElement(call.Arguments);
                yield return new AgentEvent.ToolCallRequested(call.Id, call.ToolId, args);

                var outcome = await dispatcher.DispatchAsync(
                    new ToolInvocation(call.Id, call.ToolId, args), cancellationToken);
                yield return new AgentEvent.ToolCallCompleted(outcome);
                results.Add($"{call.ToolId}:{outcome.Outcome}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Cancelled);
                yield break;
            }

            history.Add(new ChatMessage(ChatRole.Tool, string.Join(";", results)));
        }

        yield return new AgentEvent.RunFinished(AgentRunOutcome.ToolRoundLimitReached);
    }

    private static ChatRole ToRole(AgentRole role) => role switch
    {
        AgentRole.System => ChatRole.System,
        AgentRole.Assistant => ChatRole.Assistant,
        AgentRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static string DescribeTools(IReadOnlyList<ToolDescriptor> tools)
    {
        var sb = new StringBuilder("可用工具：");
        foreach (var tool in tools)
            sb.AppendLine().Append(
                $"- {tool.Name} (id={tool.Id})：{tool.Description}；schema={tool.InputSchema.GetRawText()}");
        return sb.ToString();
    }

    private sealed record PendingCall(
        string Id, string ToolId, Dictionary<string, object?> Arguments, bool IsMalformed);
}
