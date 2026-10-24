using Dougong.Agent;

namespace Dougong.Agent.Comparison;

/// <summary>
/// G8 验收：token 估算必须接近实测，且偏差方向安全。
/// </summary>
/// <remarks>
/// 基准来自 2026-10-24 对 DeepSeek deepseek-flash 的实测（见各用例注释）。
/// 判定标准不是"精确"，而是两条：
/// <list type="number">
/// <item>不能显著低估——低估会让压缩来不及触发，会话撞上 provider 上限才失败</item>
/// <item>不能高估超过 2 倍——高估会让压缩过早触发，丢掉还能用的上下文</item>
/// </list>
/// 这两条比"误差小于 10%"更符合用途：估算是为了触发压缩，不是为了计费。
/// </remarks>
public static class TokenEstimateChecks
{
    public static int Run()
    {
        var failures = 0;
        failures += Check("ascii-long-within-bounds", AsciiLong);
        failures += Check("cjk-within-bounds", Cjk);
        failures += Check("never-underestimates-by-much", NeverUnder);
        failures += Check("short-text-includes-overhead", ShortText);
        failures += Check("non-empty-text-is-at-least-one-token", AtLeastOne);
        failures += Check("message-overhead-is-counted", MessageOverhead);
        failures += Check("tool-call-arguments-are-counted", ToolCallCost);

        Console.WriteLine(failures == 0
            ? "TOKEN-ESTIMATE: ALL PASS"
            : $"TOKEN-ESTIMATE: {failures} FAILURE(S)");
        return failures;
    }

    /// <summary>
    /// 实测：纯 ASCII（'a' 重复 3200 次）→ 430 tokens，即约 7.4 字符/token。
    /// </summary>
    private static string? AsciiLong()
    {
        var text = new string('a', 3200);
        const int actual = 430;
        var estimate = TokenBudget.Estimate(text);

        return WithinBounds(estimate, actual, "ASCII 长文本");
    }

    /// <summary>实测：中文重复 5 次（130 字符）→ 105 tokens，即约 1.24 字符/token。</summary>
    private static string? Cjk()
    {
        const string unit = "请用一句话解释什么是插件化架构，并说明它的主要优点。";
        var text = string.Concat(Enumerable.Repeat(unit, 5));
        const int actual = 105;

        var estimate = TokenBudget.Estimate(text);
        return WithinBounds(estimate, actual, "中文文本");
    }

    /// <summary>估算不得低于实测的 80%：低估会让压缩来不及触发。</summary>
    private static string? NeverUnder()
    {
        var samples = new (string Text, int Actual)[]
        {
            (new string('a', 800), 130),
            (new string('a', 12800), 1630),
            (string.Concat(Enumerable.Repeat("请用一句话解释什么是插件化架构。", 10)), 160),
            (new string('x', 550), 130),
        };

        foreach (var (text, actual) in samples)
        {
            var estimate = TokenBudget.Estimate(text);
            if (estimate < actual * 0.8)
                return $"低估：估算 {estimate}，实测 {actual}（文本长度 {text.Length}）";
        }

        return null;
    }

    /// <summary>
    /// 实测：31 字符短句用 37 tokens——固定开销占主导，估算不能只按字符数算。
    /// </summary>
    private static string? ShortText()
    {
        const string text = "Reply with exactly the word OK.";
        var estimate = TokenBudget.Estimate(text);

        // 实测 37；允许较宽区间，但不能算出个位数。
        return estimate >= 8 && estimate <= 74
            ? null : $"短文本估算 {estimate}，实测 37，超出合理区间";
    }

    private static string? AtLeastOne()
    {
        return TokenBudget.Estimate("x") >= 1
            ? null : "单字符被估成 0 token";
    }

    private static string? MessageOverhead()
    {
        var one = new[] { new AgentMessage(AgentRole.User, "a") };
        var ten = Enumerable.Range(0, 10)
            .Select(_ => new AgentMessage(AgentRole.User, "a")).ToArray();

        var single = TokenBudget.Estimate(one);
        var batch = TokenBudget.Estimate(ten);

        // 10 条消息的估算必须显著大于 1 条：否则每条消息的固定开销没被计入。
        return batch > single * 5
            ? null : $"消息级开销未计入：1 条 {single}，10 条 {batch}";
    }

    private static string? ToolCallCost()
    {
        using var small = System.Text.Json.JsonDocument.Parse("""{"a":1}""");
        using var large = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { data = new string('x', 2000) }));

        var withoutCall = new[] { new AgentMessage(AgentRole.Assistant, "ok") };
        var withSmall = new[]
        {
            new AgentMessage(AgentRole.Assistant, "ok")
            {
                ToolCalls = [new AgentToolCallRef("c1", "read", small.RootElement.Clone())],
            },
        };
        var withLarge = new[]
        {
            new AgentMessage(AgentRole.Assistant, "ok")
            {
                ToolCalls = [new AgentToolCallRef("c2", "read", large.RootElement.Clone())],
            },
        };

        var baseCost = TokenBudget.Estimate(withoutCall);
        var smallCost = TokenBudget.Estimate(withSmall);
        var largeCost = TokenBudget.Estimate(withLarge);

        if (smallCost <= baseCost) return "工具调用本身未被计入";
        return largeCost > smallCost + 300
            ? null : $"工具参数内容未被计入：小 {smallCost}，大 {largeCost}";
    }

    private static string? WithinBounds(int estimate, int actual, string what)
    {
        var ratio = (double)estimate / actual;
        if (ratio < 0.8) return $"{what}低估：估算 {estimate}，实测 {actual}（比例 {ratio:0.00}）";
        if (ratio > 2.0) return $"{what}高估过多：估算 {estimate}，实测 {actual}（比例 {ratio:0.00}）";
        return null;
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
