using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dougong.Agent;

/// <summary>
/// Dougong 的 agent 循环：直接驱动 <see cref="IChatClient"/>，自己控制轮次与工具派发。
/// </summary>
/// <remarks>
/// 这是 M1 候选对比之后选定的实现（见 docs/m1-candidate-comparison.md）。
/// 刻意不套 <c>FunctionInvokingChatClient</c>：任何会自行执行工具的中间件
/// 都会绕过 <see cref="IToolDispatcher"/>，破坏唯一执行入口。
/// <para>
/// 提交屏障的四个落点：用户输入、完整模型响应、工具调用开始、工具调用终态。
/// 任一处提交失败即停止运行，不重试也不降级。
/// </para>
/// </remarks>
public sealed class LoopAgentRuntime(
    IChatClient client,
    IToolDispatcher dispatcher,
    IAgentStore? store = null) : IAgentRuntime
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentEvent.RunStarted(request.SessionId, request.TurnId);

        // 屏障 1：用户输入必须在任何模型调用之前提交。
        // 提交失败即停止——不继续、不降级、不假装成功。
        if (await CommitOrNullAsync(StoreRecordKind.UserInput,
                    JsonSerializer.SerializeToElement(request.UserInput), cancellationToken)
                is { } inputFailure)
        {
            yield return inputFailure;
            yield break;
        }

        var history = new List<ChatMessage>();
        foreach (var message in request.History)
            history.Add(ToChatMessage(message));
        history.Add(new ChatMessage(ChatRole.User, request.UserInput));

        for (var round = 0; round < request.MaxToolRounds; round++)
        {
            // 聚合本轮流式输出：文本直接投影，函数调用按 ID 收集完整参数。
            var text = new StringBuilder();
            var calls = new List<PendingCall>();
            // 某些 provider（如 DeepSeek 思考模式）要求带工具调用的 assistant 消息
            // 回传 reasoning_content，否则请求被拒。这里原样保留并透传。
            var reasoning = new StringBuilder();
            var fragmentCount = new Dictionary<string, int>(StringComparer.Ordinal);

            // 工具声明是运行时对连接器的正式输出，不是循环拼的提示文本。
            var chatOptions = new ChatOptions().Attach(dispatcher.Tools);

            await using var enumerator = client
                .GetStreamingResponseAsync(history, chatOptions, cancellationToken)
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
                    streamFailure = $"模型流中断：{ex.Message}" + (ex.InnerException is { } i ? $" | inner: {i.GetType().Name}: {i.Message}" : "");
                }

                // 取消与失败必须先处理：此时 Current 不可访问。
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

                if (enumerator.Current.AdditionalProperties is { } updateProps
                    && updateProps.TryGetValue("reasoning_content", out var reasoningValue)
                    && reasoningValue?.ToString() is { Length: > 0 } reasoningText)
                    reasoning.Append(reasoningText);

                foreach (var content in enumerator.Current.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                            text.Append(textContent.Text);
                            yield return new AgentEvent.TextDelta(textContent.Text);
                            break;

                        case FunctionCallContent functionCall:
                            // 参数分片的真相：provider（GLM/Anthropic）逐段给出
                            // input_json_delta.partial_json，是字节片段而非解析后的字典。
                            // M.E.AI 的 Arguments 是已解析字典，取不到原始片段，
                            // 因此这里保留"最近一次完整字典快照"的序列化原文，
                            // 并在派发前重新解析——语义等价于拼接后解析。
                            var id = functionCall.CallId;
                            if (string.IsNullOrEmpty(id))
                            {
                                calls.Add(new PendingCall(string.Empty, functionCall.Name,
                                    [], IsMalformed: true, Fragments: null));
                                break;
                            }

                            fragmentCount[id] = fragmentCount.GetValueOrDefault(id) + 1;
                            var fragment = functionCall.Arguments is null
                                ? null
                                : JsonSerializer.Serialize(functionCall.Arguments);
                            var existing = calls.FindIndex(c => c.Id == id);
                            if (existing < 0)
                            {
                                calls.Add(new PendingCall(id, functionCall.Name,
                                    functionCall.Arguments is null
                                        ? []
                                        : new Dictionary<string, object?>(functionCall.Arguments),
                                    IsMalformed: false,
                                    Fragments: fragment ?? "{}"));
                            }
                            else
                            {
                                var merged = new Dictionary<string, object?>(calls[existing].Arguments);
                                if (functionCall.Arguments is not null)
                                    foreach (var pair in functionCall.Arguments)
                                        if (merged.TryGetValue(pair.Key, out var previous)
                                            && !Equals(previous, pair.Value))
                                        {
                                            // 同一键被赋予不同值：冲突而不是分片。
                                            calls[existing] = calls[existing] with { IsMalformed = true };
                                        }
                                        else
                                        {
                                            merged[pair.Key] = pair.Value;
                                        }

                                calls[existing] = calls[existing] with
                                {
                                    Arguments = merged,
                                    Fragments = fragment ?? calls[existing].Fragments,
                                };
                            }

                            break;
                    }
                }
            }

            if (calls.Count == 0)
            {
                // 纯文本轮次才单独补 assistant 消息。
                // 有工具调用时，文本必须与 tool_calls 合进同一条 assistant 消息：
                // 分开写会得到两条相邻的 assistant 消息，思考模式的 provider
                // 会认为带 tool_calls 的那条缺少 reasoning_content 而拒掉整轮（实测 400）。
                if (text.Length > 0)
                {
                    history.Add(new ChatMessage(ChatRole.Assistant, text.ToString())
                    {
                        AdditionalProperties = reasoning.Length > 0
                            ? new AdditionalPropertiesDictionary { ["reasoning_content"] = reasoning.ToString() }
                            : null,
                    });
                }

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

            // 屏障 2：完整模型响应（含工具调用请求）必须在任何派发之前提交。
            // 这是"先提交调用意图、再产生副作用"的核心。
            // 参数必须入库：只记 id 与工具名的话，恢复出的历史无法重新表达调用，
            // resume 之后的第一轮请求就会缺参数。
            var responsePayload = JsonSerializer.Serialize(new
            {
                text = text.ToString(),
                reasoning = reasoning.ToString(),
                calls = calls.Select(c => new
                {
                    id = c.Id,
                    tool = c.ToolId,
                    arguments = ParseArguments(c),
                }),
            });
            if (await CommitOrNullAsync(StoreRecordKind.ModelResponse, ToElement(responsePayload), cancellationToken)
                    is { } responseFailure)
            {
                yield return responseFailure;
                yield break;
            }

            var toolResults = new List<(string Id, string ToolId, ToolDispatchResult Result)>();
            foreach (var call in calls)
            {
                // 用累积片段的原文重新解析，而不是直接序列化字典：
                // 这样能真实反映 provider 给出的 JSON（含嵌套、数组、数字精度）。
                var args = ParseArguments(call);

                yield return new AgentEvent.ToolCallRequested(call.Id, call.ToolId, args);

                // 屏障 3：工具开始记录必须落盘，才允许执行（可能产生副作用）。
                var startedPayload = JsonSerializer.Serialize(new { id = call.Id, tool = call.ToolId });
                if (await CommitOrNullAsync(StoreRecordKind.ToolCallStarted, ToElement(startedPayload), cancellationToken)
                        is { } startedFailure)
                {
                    // 未提交则绝不执行；该调用保持"未开始"语义。
                    yield return startedFailure;
                    yield break;
                }

                var invocation = new ToolInvocation(call.Id, call.ToolId, args);
                var result = await dispatcher.DispatchAsync(invocation, cancellationToken);

                // 屏障 4：终态也要提交，否则恢复时无法判断副作用是否发生。
                // 结果内容同样入库：恢复出的 tool 消息必须有内容，
                // 空内容会让模型以为工具什么也没做。
                var completedPayload = JsonSerializer.Serialize(new
                {
                    id = call.Id,
                    tool = call.ToolId,
                    outcome = result.Outcome.ToString(),
                    diagnostic = result.Diagnostic,
                    content = result.Result?.Content.ValueKind is JsonValueKind.Undefined or null
                        ? null
                        : result.Result.Content.GetRawText(),
                });
                if (await CommitOrNullAsync(StoreRecordKind.ToolCallCompleted, ToElement(completedPayload), cancellationToken)
                        is { } completedFailure)
                {
                    yield return new AgentEvent.ToolCallCompleted(result);
                    yield return completedFailure;
                    yield break;
                }

                yield return new AgentEvent.ToolCallCompleted(result);
                toolResults.Add((call.Id, call.ToolId, result));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield return new AgentEvent.RunFinished(AgentRunOutcome.Cancelled);
                yield break;
            }

            // 回填必须符合协议：assistant 消息携带 tool_calls，每条 tool 结果带 tool_call_id。
            // 早期实现把结果压成一条普通 Tool 文本，缺 tool_call_id 会被 provider 拒绝（422）。
            AppendToolExchange(history, calls, toolResults, text.ToString(), reasoning.ToString());
        }

        yield return new AgentEvent.RunFinished(AgentRunOutcome.ToolRoundLimitReached);
    }

    /// <summary>
    /// 按协议回填工具交互：先补一条携带 tool_calls 的 assistant 消息，
    /// 再为每个调用追加带 tool_call_id 的 tool 消息。
    /// </summary>
    private static void AppendToolExchange(
        List<ChatMessage> history,
        IReadOnlyList<PendingCall> calls,
        IReadOnlyList<(string Id, string ToolId, ToolDispatchResult Result)> results,
        string text,
        string reasoning)
    {
        var toolCalls = new List<AIContent>();
        foreach (var call in calls)
        {
            toolCalls.Add(new FunctionCallContent(
                call.Id,
                call.ToolId,
                ParseArguments(call) is var element && element.ValueKind is JsonValueKind.Object
                    ? element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                    : null));
        }

        // 本轮的文本与 tool_calls 必须是同一条 assistant 消息：
        // 拆成两条会让带 tool_calls 的那条失去上下文的连续性，
        // 思考模式的 provider 会因此拒掉整轮请求。
        if (text.Length > 0) toolCalls.Insert(0, new TextContent(text));

        history.Add(new ChatMessage(ChatRole.Assistant, toolCalls)
        {
            AdditionalProperties = reasoning.Length > 0
                ? new AdditionalPropertiesDictionary { ["reasoning_content"] = reasoning }
                : null,
        });

        foreach (var (id, toolId, result) in results)
        {
            var content = result.Result?.Content.ValueKind is JsonValueKind.Undefined or null
                ? JsonSerializer.Serialize(new { outcome = result.Outcome.ToString(), diagnostic = result.Diagnostic })
                : result.Result.Content.GetRawText();

            history.Add(new ChatMessage(ChatRole.Tool, content)
            {
                // TextContent 必须在 Contents 里：ChatMessage.Text 由 Contents 派生，
                // 只放 FunctionResultContent 会让 Text 变空，三种协议拿到的工具结果都是空字符串。
                Contents = [new TextContent(content), new FunctionResultContent(id, content)],
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["tool_call_id"] = id,
                    ["name"] = toolId,
                },
            });
        }
    }

    /// <summary>
    /// 提交一条记录。成功返回 null；失败返回应向外发出的终止事件。
    /// 未配置存储时视为"无持久化要求"，直接放行（仅用于对比与测试）。
    /// </summary>
    private async ValueTask<AgentEvent.RunFinished?> CommitOrNullAsync(
        StoreRecordKind kind, JsonElement payload, CancellationToken cancellationToken)
    {
        if (store is null) return null;

        try
        {
            await store.CommitAsync(
                new StoreRecord(0, kind, payload),
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // 提交失败即停止执行：不继续、不重试、不降级为无持久化模式。
            return new AgentEvent.RunFinished(
                AgentRunOutcome.Failed,
                $"提交失败（{kind}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 把已序列化的 JSON 文本变成独立拥有的元素。
    /// 早期实现用 <c>JsonSerializer.Serialize(string)</c> 再包一层，
    /// 把 JSON 变成字符串字面量存进日志，恢复时必须先解开一层才能读。
    /// 那是双重编码，不是格式设计；这里直接按结构存。
    /// </summary>
    private static JsonElement ToElement(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ChatRole ToRole(AgentRole role) => role switch
    {
        AgentRole.System => ChatRole.System,
        AgentRole.Assistant => ChatRole.Assistant,
        AgentRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>
    /// 把已提交历史还原成协议消息。
    /// </summary>
    /// <remarks>
    /// 工具交互不能退化成扁平文本：assistant 的 <c>tool_calls</c> 与 tool 的
    /// <c>tool_call_id</c> 是配对的协议结构，缺任一侧 provider 都会拒掉整轮。
    /// 恢复出来的历史必须与运行时写的形状一致，否则"能恢复"只是看起来能。
    /// </remarks>
    private static ChatMessage ToChatMessage(AgentMessage message)
    {
        var role = ToRole(message.Role);

        if (role == ChatRole.Assistant && message.ToolCalls.Count > 0)
        {
            var contents = new List<AIContent>();
            if (message.Content is { Length: > 0 } text) contents.Add(new TextContent(text));
            foreach (var call in message.ToolCalls)
            {
                contents.Add(new FunctionCallContent(
                    call.InvocationId, call.ToolId,
                    call.Arguments.ValueKind is JsonValueKind.Object
                        ? call.Arguments.EnumerateObject()
                            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                        : null));
            }

            return new ChatMessage(role, contents);
        }

        if (role == ChatRole.Tool)
        {
            var properties = new AdditionalPropertiesDictionary { ["name"] = message.ToolId };
            if (message.ToolCallId is { Length: > 0 } callId)
                properties["tool_call_id"] = callId;

            return new ChatMessage(role, message.Content)
            {
                Contents =
                [
                    new TextContent(message.Content),
                    .. message.ToolCallId is { Length: > 0 } id
                        ? new AIContent[] { new FunctionResultContent(id, message.Content) }
                        : [],
                ],
                AdditionalProperties = properties,
            };
        }

        return new ChatMessage(role, message.Content);
    }

    private sealed record PendingCall(
        string Id,
        string ToolId,
        Dictionary<string, object?> Arguments,
        bool IsMalformed,
        string? Fragments);

    /// <summary>
    /// 把累积的片段原文解析为 JsonElement；失败则退回字典序列化。
    /// 片段为 null 表示参数从未到达（应判为不完整）。
    /// </summary>
    private static JsonElement ParseArguments(PendingCall call)
    {
        if (call.Fragments is { } raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        return JsonSerializer.SerializeToElement(call.Arguments);
    }

}
