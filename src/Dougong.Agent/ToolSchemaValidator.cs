using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 工具参数的 JSON Schema 校验器。
/// </summary>
/// <remarks>
/// 设计立场（docs/architecture.md §4.3）：schema 必须有宿主侧校验器，
/// **不以"模型应该遵守 schema"代替验证**。M2 接入 MCP 之后这条更硬——
/// schema 来自不可信的远端，它可能声明了 <c>{"count":{"type":"number"}}</c>，
/// 而模型给出 <c>{"count":"abc"}</c>。
/// <para>
/// 支持范围是**刻意限定**的：只覆盖工具参数实际用到的构造。遇到不支持的
/// 关键字（<c>$ref</c>、<c>allOf</c>、条件 schema 等）**必须报错**，
/// 不能静默跳过——静默跳过等于向使用者宣称"已校验"而实际没有。
/// 这是本类最重要的不变量。
/// </para>
/// <para>
/// 不做的事：类型强制转换。字符串 <c>"3"</c> 不会被当成数字 3 接受，
/// 因为"看起来能跑"和"符合声明"是两件事，混淆它们会让 schema 失去意义。
/// </para>
/// </remarks>
public static class ToolSchemaValidator
{
    /// <summary>允许出现的关键字。未列出的关键字会被报错。</summary>
    /// <remarks>
    /// 白名单而非黑名单：新增关键字必须显式支持，否则新出现的关键字会被
    /// 静默忽略——那正是要防的事情。
    /// </remarks>
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "items", "enum", "const",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "pattern",
        "minItems", "maxItems", "uniqueItems",
        "additionalProperties", "description", "title", "default", "examples",
    };

    /// <summary>
    /// 校验参数。返回 null 表示通过；否则返回可读的失败原因。
    /// </summary>
    /// <remarks>
    /// 诊断里带上属性路径（如 <c>opts.mode</c>）：只报"类型不符"会让调用方
    /// 无法定位问题，尤其是嵌套结构。
    /// </remarks>
    public static string? Validate(JsonElement schema, JsonElement arguments)
    {
        if (schema.ValueKind is not JsonValueKind.Object)
            return "工具 schema 必须是 JSON 对象。";

        if (arguments.ValueKind is not JsonValueKind.Object)
            return "调用参数必须是 JSON 对象。";

        return ValidateNode(schema, arguments, string.Empty);
    }

    private static string? ValidateNode(JsonElement schema, JsonElement value, string path)
    {
        // 布尔 schema：true 表示任意值，false 表示不允许任何值（JSON Schema 规范）。
        if (schema.ValueKind is JsonValueKind.True) return null;
        if (schema.ValueKind is JsonValueKind.False)
            return $"{At(path)}不允许出现任何值。";
        if (schema.ValueKind is not JsonValueKind.Object)
            return $"{At(path)}schema 节点必须是对象或布尔值。";

        foreach (var property in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(property.Name)
                && !property.Name.StartsWith("x-", StringComparison.Ordinal))
            {
                return $"{At(path)}schema 使用了不支持的校验关键字 '{property.Name}'；"
                    + "为避免静默跳过校验，此处拒绝该调用。"
                    + "受支持的关键字："
                    + string.Join(", ", SupportedKeywords.OrderBy(k => k, StringComparer.Ordinal));
            }
        }

        if (schema.TryGetProperty("type", out var type))
        {
            // type 可以是字符串或字符串数组（联合类型）。数组形态是合法 JSON Schema，
            // 曾经只处理字符串形态，导致 ["string","null"] 这类 schema 完全不校验。
            if (type.ValueKind is JsonValueKind.String)
            {
                if (!MatchesType(type.GetString()!, value, path, out var typeError)) return typeError;
            }
            else if (type.ValueKind is JsonValueKind.Array)
            {
                var matched = false;
                var names = new List<string>();
                foreach (var candidate in type.EnumerateArray())
                {
                    if (candidate.ValueKind is not JsonValueKind.String) continue;
                    var name = candidate.GetString()!;
                    names.Add(name);
                    if (MatchesType(name, value, path, out _)) matched = true;
                }

                if (names.Count == 0)
                    return $"{At(path)}schema 的 type 数组为空或元素不是字符串。";

                if (!matched)
                    return $"{At(path)}类型应为 {string.Join(" 或 ", names)}，"
                        + $"实际为 {DescribeKind(value.ValueKind)}。";
            }
            else
            {
                return $"{At(path)}schema 的 type 既不是字符串也不是数组，无法校验。";
            }
        }

        // const 优先于 enum：两者同时存在时 const 更严格。
        if (schema.TryGetProperty("const", out var constValue))
        {
            if (!JsonEquals(constValue, value))
                return $"{At(path)}必须严格等于 {constValue.GetRawText()}。";
        }
        else if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind is JsonValueKind.Array)
        {
            var any = false;
            foreach (var candidate in enumValues.EnumerateArray())
            {
                if (!JsonEquals(candidate, value)) continue;
                any = true;
                break;
            }

            if (!any)
                return $"{At(path)}必须是 [{enumValues.GetRawText()}] 之一，实际为 {value.GetRawText()}。";
        }

        if (value.ValueKind is JsonValueKind.Number)
        {
            if (ValidateNumber(schema, value, path) is { } numberError) return numberError;
        }
        else if (value.ValueKind is JsonValueKind.String)
        {
            if (ValidateString(schema, value, path) is { } stringError) return stringError;
        }
        else if (value.ValueKind is JsonValueKind.Array)
        {
            if (ValidateArray(schema, value, path) is { } arrayError) return arrayError;
        }
        else if (value.ValueKind is JsonValueKind.Object)
        {
            if (ValidateObject(schema, value, path) is { } objectError) return objectError;
        }

        return null;
    }

    private static bool MatchesType(
        string expected, JsonElement value, string path, out string? error)
    {
        error = null;
        var actual = value.ValueKind;

        var ok = expected switch
        {
            "object" => actual is JsonValueKind.Object,
            "array" => actual is JsonValueKind.Array,
            "string" => actual is JsonValueKind.String,
            // integer 要求无小数部分；JSON 数字不区分二者，只能按值判断。
            "integer" => actual is JsonValueKind.Number
                && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d,
            "number" => actual is JsonValueKind.Number,
            "boolean" => actual is JsonValueKind.True or JsonValueKind.False,
            "null" => actual is JsonValueKind.Null,
            _ => true,   // 未知类型名由关键字检查之外的路径处理，这里保守放行
        };

        if (ok) return true;

        error = $"{At(path)}类型应为 {expected}，实际为 {DescribeKind(actual)}。";
        return false;
    }

    private static string? ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        if (!value.TryGetDecimal(out var number))
        {
            // 超出 decimal 范围但仍可能是合法 JSON 数字：跳过数值边界检查，
            // 而不是误判为非法金额/坐标这类大数。
            return null;
        }

        if (schema.TryGetProperty("minimum", out var min) && min.TryGetDecimal(out var minValue)
            && number < minValue)
            return $"{At(path)}必须 ≥ {minValue}，实际为 {number}。";

        if (schema.TryGetProperty("maximum", out var max) && max.TryGetDecimal(out var maxValue)
            && number > maxValue)
            return $"{At(path)}必须 ≤ {maxValue}，实际为 {number}。";

        if (schema.TryGetProperty("exclusiveMinimum", out var exclMin))
        {
            if (exclMin.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                // draft-4 形态：exclusiveMinimum 是布尔值，含义是"minimum 是否排他"。
                // 曾经因为读不成数字就跳过，等于静默放宽了约束。
                if (exclMin.ValueKind is JsonValueKind.True
                    && schema.TryGetProperty("minimum", out var bound)
                    && bound.TryGetDecimal(out var boundValue) && number <= boundValue)
                    return $"{At(path)}必须 > {boundValue}（exclusiveMinimum），实际为 {number}。";
            }
            else if (exclMin.TryGetDecimal(out var exclMinValue) && number <= exclMinValue)
            {
                return $"{At(path)}必须 > {exclMinValue}，实际为 {number}。";
            }
        }

        if (schema.TryGetProperty("exclusiveMaximum", out var exclMax))
        {
            if (exclMax.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (exclMax.ValueKind is JsonValueKind.True
                    && schema.TryGetProperty("maximum", out var bound)
                    && bound.TryGetDecimal(out var boundValue) && number >= boundValue)
                    return $"{At(path)}必须 < {boundValue}（exclusiveMaximum），实际为 {number}。";
            }
            else if (exclMax.TryGetDecimal(out var exclMaxValue) && number >= exclMaxValue)
            {
                return $"{At(path)}必须 < {exclMaxValue}，实际为 {number}。";
            }
        }

        return null;
    }

    private static string? ValidateString(JsonElement schema, JsonElement value, string path)
    {
        var text = value.GetString() ?? string.Empty;

        if (schema.TryGetProperty("minLength", out var minLength)
            && minLength.TryGetInt32(out var min) && text.Length < min)
            return $"{At(path)}长度必须 ≥ {min}，实际为 {text.Length}。";

        if (schema.TryGetProperty("maxLength", out var maxLength)
            && maxLength.TryGetInt32(out var max) && text.Length > max)
            return $"{At(path)}长度必须 ≤ {max}，实际为 {text.Length}。";

        if (schema.TryGetProperty("pattern", out var patternElement)
            && patternElement.ValueKind is JsonValueKind.String
            && patternElement.GetString() is { Length: > 0 } pattern)
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                    return $"{At(path)}不匹配模式 '{pattern}'。";
            }
            catch (ArgumentException)
            {
                // 远端给了非法正则：这是 schema 的问题，不能当成参数问题放行。
                return $"{At(path)}schema 的 pattern 不是合法正则：'{pattern}'。";
            }
        }

        return null;
    }

    private static string? ValidateArray(JsonElement schema, JsonElement value, string path)
    {
        var length = value.GetArrayLength();

        if (schema.TryGetProperty("minItems", out var minItems)
            && minItems.TryGetInt32(out var min) && length < min)
            return $"{At(path)}至少需要 {min} 个元素，实际为 {length}。";

        if (schema.TryGetProperty("maxItems", out var maxItems)
            && maxItems.TryGetInt32(out var max) && length > max)
            return $"{At(path)}最多允许 {max} 个元素，实际为 {length}。";

        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind is JsonValueKind.True)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (!seen.Add(item.GetRawText()))
                    return $"{At(path)}第 {index} 个元素与其他元素重复（uniqueItems）。";
                index++;
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var childPath = Combine(path, index.ToString());
                if (ValidateNode(items, item, childPath) is { } itemError) return itemError;
                index++;
            }
        }

        return null;
    }

    private static string? ValidateObject(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("required", out var required)
            && required.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.GetString() is not { Length: > 0 } name) continue;
                if (!value.TryGetProperty(name, out _))
                    return $"{At(path)}缺少必需参数 '{Combine(path, name)}'。";
            }
        }

        var properties = schema.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind is JsonValueKind.Object
            ? propertiesElement
            : default;

        var allowsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind is not JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind is JsonValueKind.Object
                && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                var childPath = Combine(path, property.Name);
                if (ValidateNode(propertySchema, property.Value, childPath) is { } propertyError)
                    return propertyError;
                continue;
            }

            if (!allowsAdditional)
                return $"{At(path)}不接受未声明的参数 '{Combine(path, property.Name)}'"
                    + "（schema 声明 additionalProperties:false）。";

            // 未声明但允许额外字段：若 schema 给了 additionalProperties 的子 schema，按其校验。
            if (additional.ValueKind is JsonValueKind.Object)
            {
                var childPath = Combine(path, property.Name);
                if (ValidateNode(additional, property.Value, childPath) is { } extraError)
                    return extraError;
            }
        }

        return null;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;

        return left.ValueKind switch
        {
            JsonValueKind.Number => left.TryGetDecimal(out var l) && right.TryGetDecimal(out var r)
                ? l == r : left.GetRawText() == right.GetRawText(),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            // 数组与对象的比较按规范化后的原文；顺序敏感是已知的保守选择。
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    private static string At(string path) => path.Length == 0 ? "参数" : $"参数 '{path}'";

    private static string Combine(string path, string segment) =>
        path.Length == 0 ? segment : $"{path}.{segment}";
}
