using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dougong.Agent;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 候选 B：直接使用 <see cref="IChatClient"/>，由 Dougong 自己驱动循环。
/// 刻意不套 <c>FunctionInvokingChatClient</c>，工具只能经由 <see cref="IToolDispatcher"/> 执行。
/// </summary>
public sealed class LoopAgentRuntime(IChatClient client, IToolDispatcher dispatcher) : IAgentRuntime
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentEvent.RunStarted(request.SessionId, request.TurnId);

        var history = new List<ChatMessage>();
        foreach (var message in request.History)
            history.Add(new ChatMessage(ToRole(message.Role), message.Content));
        history.Add(new ChatMessage(ChatRole.User, request.UserInput));

        for (var round = 0; round < request.MaxToolRounds; round++)
        {
            // 工具 schema 以系统消息注入：AITool 在 10.11.0 无法子类化，
            // 而候选 B 的定位正是"模型只产出描述，宿主自己执行"。
            if (dispatcher.Tools.Count > 0)
                history.Add(new ChatMessage(ChatRole.System, DescribeTools(dispatcher.Tools)));

            // 聚合本轮流式输出：文本直接投影，函数调用按 ID 收集完整参数。
            var text = new StringBuilder();
            var calls = new List<PendingCall>();
            var fragmentCount = new Dictionary<string, int>(StringComparer.Ordinal);

            await using var enumerator = client
                .GetStreamingResponseAsync(history, null, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool moved;
                string? streamFailure = null;
                var cancelled = false;
                try { moved = await enumerator.MoveNextAsync(); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    moved = false;
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    moved = false;
                    streamFailure = $"模型流中断：{ex.Message}";
                }

                if (cancelled)
                {
                    yield return new AgentEvent.RunFinished(AgentRunOutcome.Cancelled, "模型调用被取消。");
                    yield break;
                }

                if (streamFailure is not null)
                {
                    yield return new AgentEvent.RunFinished(AgentRunOutcome.Failed, streamFailure);
                    yield break;
                }

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
                            // 同一 ID 的分片累积：参数按 key 合并，后到片段覆盖同名 key。
                            var id = functionCall.CallId;
                            if (string.IsNullOrEmpty(id))
                            {
                                calls.Add(new PendingCall(string.Empty, functionCall.Name,
                                    [], IsMalformed: true));
                                break;
                            }

                            fragmentCount[id] = fragmentCount.GetValueOrDefault(id) + 1;
                            var existing = calls.FindIndex(c => c.Id == id);
                            if (existing < 0)
                            {
                                calls.Add(new PendingCall(id, functionCall.Name,
                                    functionCall.Arguments is null ? [] : new Dictionary<string, object?>(functionCall.Arguments),
                                    IsMalformed: false));
                            }
                            else
                            {
                                var merged = new Dictionary<string, object?>(calls[existing].Arguments);
                                if (functionCall.Arguments is not null)
                                    foreach (var pair in functionCall.Arguments)
                                        if (merged.TryGetValue(pair.Key, out var previous)
                                            && !Equals(previous, pair.Value))
                                        {
                                            // 同一键被赋予不同值：这是冲突而不是分片。
                                            calls[existing] = calls[existing] with { IsMalformed = true };
                                        }
                                        else
                                        {
                                            merged[pair.Key] = pair.Value;
                                        }
                                calls[existing] = calls[existing] with { Arguments = merged };
                            }

                            break;
                    }
                }
            }

            if (text.Length > 0)
                history.Add(new ChatMessage(ChatRole.Assistant, text.ToString()));

            if (calls.Count == 0)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Completed);
                yield break;
            }

            // 先整体校验调用集，任一无效则整轮不执行。
            var invalid = calls.FirstOrDefault(c => c.IsMalformed);
            if (invalid is not null)
            {
                foreach (var call in calls)
                {
                    var rejected = new ToolDispatchResult(
                        call.Id, call.ToolId, ToolCallOutcome.RejectedInvalidArguments,
                        Diagnostic: "调用 ID 缺失或参数不完整。");
                    yield return new AgentEvent.ToolCallCompleted(rejected);
                }

                yield return new AgentEvent.RunFinished(
                    AgentRunOutcome.InvalidModelResponse, "模型响应包含无效调用。");
                yield break;
            }

            // 同一 ID 出现多次是分片累积，不算重复；
            // 重复指同一 ID 携带了相同参数键的第二个独立片段，这里以“参数键冲突”识别。
            var duplicate = calls
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                foreach (var call in calls)
                {
                    yield return new AgentEvent.ToolCallCompleted(new ToolDispatchResult(
                        call.Id, call.ToolId, ToolCallOutcome.RejectedDuplicateInvocation,
                        Diagnostic: "本轮存在重复调用 ID。"));
                }

                yield return new AgentEvent.RunFinished(
                    AgentRunOutcome.InvalidModelResponse, "模型响应包含重复调用 ID。");
                yield break;
            }

            var toolResults = new List<string>();
            foreach (var call in calls)
            {
                var args = JsonSerializer.SerializeToElement(call.Arguments);

                yield return new AgentEvent.ToolCallRequested(call.Id, call.ToolId, args);

                var invocation = new ToolInvocation(call.Id, call.ToolId, args);
                var result = await dispatcher.DispatchAsync(invocation, cancellationToken);
                yield return new AgentEvent.ToolCallCompleted(result);
                toolResults.Add($"{call.ToolId}:{result.Outcome}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Cancelled);
                yield break;
            }

            // 工具终态回填后进入下一轮模型调用。
            history.Add(new ChatMessage(ChatRole.Tool, string.Join(";", toolResults)));
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

    private sealed record PendingCall(
        string Id, string ToolId, Dictionary<string, object?> Arguments, bool IsMalformed);

    private static string DescribeTools(IReadOnlyList<ToolDescriptor> tools)
    {
        var sb = new StringBuilder("可用工具：");
        foreach (var tool in tools)
            sb.AppendLine().Append(
                $"- {tool.Name} (id={tool.Id})：{tool.Description}；schema={tool.InputSchema.GetRawText()}");
        return sb.ToString();
    }
}
