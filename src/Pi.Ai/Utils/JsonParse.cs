using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>
/// 流式部分 JSON 解析。对应 TS <c>utils/json-parse.ts</c>：修复字符串字面量中的
/// 原始控制字符与非法转义；不完全 JSON 用括号补全容错解析；全部失败返回空对象。
/// </summary>
public static class JsonParse
{
    // 'u' 不在此列：非法 \u 转义（如 \usepackage）必须按普通非法转义加倍反斜杠保留为字面量。
    // 合法的 \uXXXX 由上面的四位十六进制分支单独处理（上游 #10868）。
    private static readonly HashSet<char> ValidJsonEscapes = ['"', '\\', '/', 'b', 'f', 'n', 'r', 't'];

    private static bool IsControlCharacter(char ch) => ch <= 0x1f;

    private static string EscapeControlCharacter(char ch) => ch switch
    {
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => $"\\u{((int)ch).ToString("x4")}",
    };

    /// <summary>
    /// 修复畸形 JSON 字符串字面量：字符串内原始控制字符转义、非法转义字符前的
    /// 反斜杠加倍。对应 TS <c>repairJson</c>。
    /// </summary>
    public static string RepairJson(string json)
    {
        var repaired = new StringBuilder(json.Length);
        var inString = false;

        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];

            if (!inString)
            {
                repaired.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (ch == '"')
            {
                repaired.Append(ch);
                inString = false;
                continue;
            }

            if (ch == '\\')
            {
                var next = index + 1 < json.Length ? json[index + 1] : '\0';
                if (next == '\0')
                {
                    repaired.Append("\\\\");
                    continue;
                }

                if (next == 'u')
                {
                    var unicodeDigits = index + 6 <= json.Length ? json.Substring(index + 2, 4) : "";
                    if (unicodeDigits.Length == 4 && unicodeDigits.All(c => Uri.IsHexDigit(c)))
                    {
                        repaired.Append("\\u").Append(unicodeDigits);
                        index += 5;
                        continue;
                    }
                }

                if (ValidJsonEscapes.Contains(next))
                {
                    repaired.Append('\\').Append(next);
                    index += 1;
                    continue;
                }

                repaired.Append("\\\\");
                continue;
            }

            repaired.Append(IsControlCharacter(ch) ? EscapeControlCharacter(ch) : ch);
        }

        return repaired.ToString();
    }

    /// <summary>解析（失败时尝试修复）。对应 TS <c>parseJsonWithRepair</c>。</summary>
    public static JsonNode? ParseJsonWithRepair(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            var repaired = RepairJson(json);
            if (repaired != json) return JsonNode.Parse(repaired);
            throw;
        }
    }

    /// <summary>
    /// 解析已完成的工具调用参数文本。与 <see cref="ParseStreamingJson"/> 不同，这里**绝不保留**
    /// 部分解析出的前缀：调用已结束而文本不是合法 JSON 对象，说明模型发来的参数是畸形的，
    /// 此时参数回退为空对象、由 <c>ArgumentsError</c> 带上失败原因与模型原文，
    /// 交给工具参数校验向模型报错（上游 #10897）。
    /// </summary>
    public static (JsonObject Arguments, string? ArgumentsError) ParseToolCallArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (new JsonObject(), null);

        try
        {
            var parsed = ParseJsonWithRepair(json);
            if (parsed is not JsonObject obj)
            {
                throw new InvalidOperationException("Tool arguments must be a JSON object");
            }

            return (obj, null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return (new JsonObject(), $"{error.Message}\n\nReceived arguments:\n{json}");
        }
    }

    /// <summary>
    /// 补全未闭合的结构符号（字符串外）后尝试解析——TS 用 partial-json 库的
    /// C# 等价简化实现：补齐引号/中括号/大括号。
    /// </summary>
    private static JsonNode? ParsePartial(string json)
    {
        var (stack, inString, trailingQuote) = ScanStructure(json);
        var repaired = json;
        if (inString) repaired += '"';
        // 尾部悬挂修剪：冒号后补 null、逗号悬挂移除，再补全未闭合结构符号。
        repaired = repaired.TrimEnd();
        while (repaired.EndsWith(',') || repaired.EndsWith(':'))
        {
            repaired = repaired.TrimEnd(',');
            if (repaired.EndsWith(':')) repaired += "null";
            repaired = repaired.TrimEnd();
        }
        foreach (var open in stack)
        {
            repaired += open == '{' ? '}' : ']';
        }
        try
        {
            return JsonNode.Parse(repaired);
        }
        catch (JsonException)
        {
            return null; // 候选失败：交给后续候选
        }
    }

    /// <summary>带孤立引号修剪的第二候选：尾部多余的未闭合引号视作垃圾移除。</summary>
    private static JsonNode? ParsePartialTrimmed(string json)
    {
        var (stack, inString, _) = ScanStructure(json);
        var repaired = json;
        if (inString)
        {
            // 悬挂字符串：补引号闭合。
            repaired += '"';
        }
        repaired = repaired.TrimEnd();
        while (repaired.EndsWith(',') || repaired.EndsWith(':'))
        {
            repaired = repaired.TrimEnd(',');
            if (repaired.EndsWith(':')) repaired += "null";
            repaired = repaired.TrimEnd();
        }
        // 扫描结束后仍多一个未闭合引号（值后孤立开引号）：去掉它再补结构符号。
        if (inString && repaired.EndsWith("\"\"" + new string(stack.Reverse().Select(o => o == '{' ? '}' : ']').ToArray())))
        {
            repaired = repaired[..^2];
        }
        foreach (var open in stack)
        {
            repaired += open == '{' ? '}' : ']';
        }
        try
        {
            return JsonNode.Parse(repaired);
        }
        catch (JsonException)
        {
            return null; // 候选失败：交给后续候选
        }
    }

    private static (Stack<char> Stack, bool InString, bool TrailingQuote) ScanStructure(string json)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var ch in json)
        {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inString) { escaped = true; continue; }
            if (ch == '"') inString = !inString;
            else if (!inString)
            {
                if (ch is '{' or '[') stack.Push(ch);
                else if (ch == '}' && (stack.Count == 0 || stack.Pop() != '{')) throw new JsonException("unbalanced }");
                else if (ch == ']' && (stack.Count == 0 || stack.Pop() != '[')) throw new JsonException("unbalanced ]");
            }
        }
        return (stack, inString, false);
    }

    /// <summary>
    /// 流式期间解析可能不完整的 JSON；始终返回合法对象（失败退空对象）。
    /// 对应 TS <c>parseStreamingJson</c>。
    /// </summary>
    public static JsonObject ParseStreamingJson(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson)) return [];
        JsonNode? result = null;
        try
        {
            result = ParseJsonWithRepair(partialJson);
        }
        catch
        {
            try
            {
                result = ParsePartial(partialJson)
                    ?? ParsePartial(RepairJson(partialJson))
                    ?? ParsePartialTrimmed(partialJson)
                    ?? ParsePartialTrimmed(RepairJson(partialJson));
            }
            catch
            {
                return [];
            }
        }
        return result as JsonObject ?? [];
    }
}
