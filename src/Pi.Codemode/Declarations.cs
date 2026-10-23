using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Codemode;

/// <summary>渲染选项。对应 TS <c>RenderDeclarationsOptions</c>（declarations.ts）。</summary>
public sealed record RenderDeclarationsOptions
{
    public IReadOnlyList<CodemodeTool>? Tools { get; init; }

    public IReadOnlyList<CodemodeTool>? Globals { get; init; }
}

/// <summary>
/// 把 JSON Schema 渲染成 TypeScript 声明。对应 TS <c>declarations.ts</c>：
/// 工具成为 <c>declare const tools</c> 的成员，全局函数成为 <c>declare function</c>，
/// <c>ns.member</c> 形式的全局函数归入 <c>declare const ns</c>。描述变文档注释，schema 变类型。
/// </summary>
public static partial class CodemodeDeclarations
{
    private const string Indent = "  ";

    /// <summary>渲染后的输入类型字符上限，超出则退化为 <c>unknown</c>。对应 TS <c>DEFAULT_INPUT_SCHEMA_MAX_CHARS</c>。</summary>
    public const int DefaultInputSchemaMaxChars = 16_000;

    /// <summary>单个 schema 允许的本地 <c>$ref</c> 展开次数，避免共享定义撑爆输出。</summary>
    private const int MaxRefExpansions = 32;

    /// <summary>以 <c>@keyword value</c> 形式进入属性注释的 JSON Schema 关键字（值须为数字或字符串）。</summary>
    private static readonly string[] TagKeywords =
    [
        "minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "format", "minItems", "maxItems",
    ];

    /// <summary>同时成立的下界：只保留最大的那个。对应 TS <c>LOWER_BOUNDS</c>。</summary>
    private static readonly HashSet<string> LowerBounds = new(StringComparer.Ordinal)
    {
        "minimum", "exclusiveMinimum", "minLength", "minItems",
    };

    /// <summary>同时成立的上界：只保留最小的那个。对应 TS <c>UPPER_BOUNDS</c>。</summary>
    private static readonly HashSet<string> UpperBounds = new(StringComparer.Ordinal)
    {
        "maximum", "exclusiveMaximum", "maxLength", "maxItems",
    };

    /// <summary>最长的属性标签；更长的（大段 default、超长 pattern）整条略去。对应 TS <c>MAX_TAG_CHARS</c>。</summary>
    private const int MaxTagChars = 80;

    /// <summary>会结束 <c>//</c> 注释的字符。对应 TS <c>LINE_TERMINATORS</c>。</summary>
    private static readonly Regex LineTerminators = new("[\r\n\u2028\u2029]", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonStringifyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>渲染脚本可见 API 的 TypeScript 声明。对应 TS <c>renderDeclarations</c>。</summary>
    public static string RenderDeclarations(RenderDeclarationsOptions options)
    {
        var sections = new List<string>();
        var tools = options.Tools ?? [];
        if (tools.Count > 0)
        {
            var members = tools.Select(tool =>
                $"{DocComment(tool.Description, Indent)}{Indent}{RenderToolSignature(tool)}");
            sections.Add($"declare const tools: {{\n{string.Join("\n", members)}\n}};");
        }

        var namespaces = new List<(string Namespace, List<string> Members)>();
        foreach (var global in options.Globals ?? [])
        {
            var dot = global.Name.IndexOf('.');
            if (dot == -1)
            {
                sections.Add(RenderGlobal($"declare function {global.Name}", global, ""));
                continue;
            }
            var ns = global.Name[..dot];
            var entry = namespaces.FirstOrDefault(candidate => candidate.Namespace == ns);
            if (entry.Members is null)
            {
                entry = (ns, []);
                namespaces.Add(entry);
            }
            entry.Members.Add(RenderGlobal(global.Name[(dot + 1)..], global, Indent));
        }
        foreach (var (ns, members) in namespaces)
        {
            sections.Add($"declare const {ns}: {{\n{string.Join("\n", members)}\n}};");
        }
        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// 单个工具作为 <c>tools</c> 对象的成员：<c>name(args: T): Promise&lt;R&gt;;</c>，
    /// 名字用脚本侧标识符。输入类型超过 <paramref name="inputMaxChars"/> 时渲染为 <c>unknown</c>。
    /// 输出 schema 是 MCP <c>CallToolResult</c> 时渲染为 <c>Promise&lt;CallToolResult&lt;T&gt;&gt;</c>。
    /// </summary>
    public static string RenderToolSignature(CodemodeTool tool, int? inputMaxChars = null)
    {
        var input = tool.InputSchema is null
            ? "unknown"
            : SchemaToType(tool.InputSchema, inputMaxChars ?? DefaultInputSchemaMaxChars);
        return $"{CodemodeIdentifiers.ToCodemodeIdentifier(tool.Name)}(args: {input}): " +
               $"Promise<{RenderToolOutputType(tool.OutputSchema)}>;";
    }

    /// <summary>
    /// 工具的样例文本：描述 + 工具声明。用于工具列表与 <c>ALL_TOOLS</c> 条目。
    /// 对应 TS <c>renderToolSample</c>。
    /// </summary>
    public static string RenderToolSample(CodemodeTool tool, int? inputMaxChars = null)
    {
        var declaration = $"declare const tools: {{ {RenderToolSignature(tool, inputMaxChars)} }};";
        return $"{(tool.Description ?? "").Trim()}\n\ncodemode tool declaration:\n```ts\n{declaration}\n```";
    }

    /// <summary>
    /// 工具调用的解析类型：输出 schema 的类型；无 schema 时 <c>unknown</c>。
    /// 对应 TS <c>renderToolOutputType</c>。
    /// </summary>
    /// <remarks>
    /// 上游 <c>24382bedd</c> 把 <c>CallToolResult&lt;T&gt;</c> 那套 MCP 特化删掉了：codemode 不再认识
    /// MCP，MCP 工具的 <c>CallToolResult</c> 由 MCP 扩展自己声明成普通对象 schema。
    /// </remarks>
    public static string RenderToolOutputType(JsonNode? schema) =>
        schema is null ? "unknown" : SchemaToType(schema);

    private static string RenderGlobal(string head, CodemodeTool global, string indent)
    {
        if (global.Signature is not null)
        {
            return $"{DocComment(global.Description, indent)}{indent}{head}{global.Signature};";
        }
        var input = global.InputSchema is null ? "unknown" : SchemaToType(global.InputSchema);
        var output = global.OutputSchema is null ? "unknown" : SchemaToType(global.OutputSchema);
        return $"{DocComment(global.Description, indent)}{indent}{head}(args: {input}): Promise<{output}>;";
    }

    private static string DocComment(string? description, string indent)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("*/", "*\\/").Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 1) return $"{indent}/** {lines[0]} */\n";
        var body = string.Join("\n", lines.Select(line => $"{indent} *{(line.Length > 0 ? $" {line}" : "")}"));
        return $"{indent}/**\n{body}\n{indent} */\n";
    }

    private static string PropertyKey(string name)
        => IdentifierPattern().IsMatch(name) ? name : JsonSerializer.Serialize(name, JsonStringifyOptions);

    private static bool IsBoolean(JsonNode? value)
        => value is JsonValue primitive
            && primitive.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static string Union(IEnumerable<string> types)
    {
        var unique = new List<string>();
        foreach (var type in types)
        {
            if (!unique.Contains(type)) unique.Add(type);
        }
        if (unique.Contains("unknown")) return "unknown";
        return unique.Count == 0 ? "never" : string.Join(" | ", unique);
    }

    /// <summary>
    /// 把 JSON Schema 转成 TypeScript 类型表达式：对象单行渲染（属性按名排序），
    /// 属性带描述时改为每行一条并加 <c>//</c> 注释；数组渲染为 <c>Array&lt;T&gt;</c>。
    /// 本地引用（<c>#/$defs/...</c>、<c>#/definitions/...</c>）按 <paramref name="schema"/> 解析；
    /// 递归与远程引用渲染为 <c>unknown</c>；结果超过 <paramref name="maxChars"/> 时渲染为 <c>unknown</c>。
    /// 对应 TS <c>schemaToType</c>。
    /// </summary>
    public static string SchemaToType(JsonNode? schema, int? maxChars = null)
    {
        string Render(bool tags) => ToType(schema, new SchemaContext
        {
            Root = schema, Resolving = [], Expansions = 0, Tags = tags,
        });

        // 约束标签会让类型变长：先带着标签渲染，超出预算就退到不带标签的版本，仍超出才 unknown。
        var type = Render(true);
        if (maxChars is null || type.Length <= maxChars) return type;
        var untagged = Render(false);
        return untagged.Length <= maxChars ? untagged : "unknown";
    }

    private sealed class SchemaContext
    {
        public required JsonNode? Root { get; init; }

        /// <summary>当前路径上正在展开的引用，用于在递归类型处停下。</summary>
        public required HashSet<string> Resolving { get; init; }

        public int Expansions { get; set; }

        /// <summary>属性注释是否带约束标签。对应 TS <c>SchemaContext.tags</c>。</summary>
        public bool Tags { get; init; }
    }

    private static JsonNode? ResolveRef(string reference, JsonNode? root)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        var path = reference.Length <= 2 ? "" : reference[2..];
        foreach (var rawSegment in path.Split('/').Where(segment => segment.Length > 0))
        {
            var key = Uri.UnescapeDataString(rawSegment).Replace("~1", "/").Replace("~0", "~");
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(key, out var next)) return null;
            current = next;
        }
        return current is JsonObject || IsBoolean(current) ? current : null;
    }

    private static string ToType(JsonNode? schema, SchemaContext context)
    {
        if (IsBoolean(schema))
        {
            return schema!.GetValue<bool>() ? "unknown" : "never";
        }
        if (schema is not JsonObject obj) return "unknown";

        if (StringValue(obj["$ref"]) is { } reference)
        {
            if (context.Resolving.Contains(reference) || context.Expansions >= MaxRefExpansions) return "unknown";
            var target = ResolveRef(reference, context.Root);
            if (target is null) return "unknown";
            context.Expansions++;
            context.Resolving.Add(reference);
            try
            {
                return ToType(target, context);
            }
            finally
            {
                context.Resolving.Remove(reference);
            }
        }

        if (obj.ContainsKey("const")) return Stringify(obj["const"]);
        if (obj["enum"] is JsonArray enumValues)
        {
            return Union(enumValues.Select(Stringify));
        }

        var variants = obj["anyOf"] as JsonArray ?? obj["oneOf"] as JsonArray;
        if (variants is not null)
        {
            return Union(variants.Select(variant => ToType(variant, context)));
        }
        if (obj["allOf"] is JsonArray allOf)
        {
            var parts = allOf.Select(part => ToType(part, context))
                .Where(part => part != "unknown")
                .ToList();
            return parts.Count == 0
                ? "unknown"
                : string.Join(" & ", parts.Select(part => part.Contains(" | ", StringComparison.Ordinal)
                    ? $"({part})"
                    : part));
        }

        var type = obj["type"];
        if (type is JsonArray typeArray)
        {
            return Union(typeArray.Select(entry =>
            {
                var clone = (JsonObject)obj.DeepClone();
                clone["type"] = entry?.DeepClone();
                return ToType(clone, context);
            }));
        }

        var typeName = StringValue(type);
        switch (typeName)
        {
            case "string":
                return "string";
            case "number":
            case "integer":
                return "number";
            case "boolean":
                return "boolean";
            case "null":
                return "null";
            case "array":
                return ArrayType(obj, context);
            case "object":
                return ObjectType(obj, context);
            case null:
                if (obj.ContainsKey("properties") || obj.ContainsKey("additionalProperties")
                    || obj.ContainsKey("required"))
                {
                    return ObjectType(obj, context);
                }
                if (obj.ContainsKey("items") || obj.ContainsKey("prefixItems")) return ArrayType(obj, context);
                return "unknown";
            default:
                return "unknown";
        }
    }

    private static string ArrayType(JsonObject schema, SchemaContext context)
    {
        if (schema["items"] is { } items && items is not JsonArray)
        {
            return $"Array<{ToType(items, context)}>";
        }
        var tuple = schema["prefixItems"] as JsonArray ?? schema["items"] as JsonArray;
        if (tuple is { Count: > 0 })
        {
            return $"[{string.Join(", ", tuple.Select(item => ToType(item, context)))}]";
        }
        return "unknown[]";
    }

    private static string DescriptionOf(JsonNode? property)
        => property is JsonObject obj && StringValue(obj["description"]) is { } description
            ? description.Trim()
            : "";

    private static string ObjectType(JsonObject schema, SchemaContext context)
    {
        var properties = schema["properties"] as JsonObject ?? [];
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray requiredArray)
        {
            foreach (var item in requiredArray)
            {
                if (StringValue(item) is { } name) required.Add(name);
            }
        }
        var names = properties.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var members = names.Select(name =>
        {
            var optional = required.Contains(name) ? "" : "?";
            return $"{PropertyKey(name)}{optional}: {ToType(properties[name], context)};";
        }).ToList();

        // TS 用 `additionalProperties !== undefined` 区分「缺失」与「JSON null」。
        var hasAdditional = schema.ContainsKey("additionalProperties");
        var additional = schema["additionalProperties"];
        if (hasAdditional && !IsFalse(additional))
        {
            var type = IsTrue(additional) ? "unknown" : ToType(additional, context);
            members.Add($"[key: string]: {type};");
        }
        else if (!hasAdditional && names.Count == 0)
        {
            members.Add("[key: string]: unknown;");
        }

        if (members.Count == 0) return "{}";

        // 每个属性的注释行：说明在前，约束标签在后（标签只在带 tags 的那次渲染里出现）。
        var comments = names.Select(name =>
        {
            var comment = LineTerminators.Split(DescriptionOf(properties[name]))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            var tags = context.Tags ? ConstraintTags(properties[name]) : "";
            if (tags.Length > 0) comment.Add(tags);
            return comment;
        }).ToList();
        if (!comments.Any(comment => comment.Count > 0))
        {
            return $"{{ {string.Join(" ", members)} }}";
        }

        var lines = new List<string> { "{" };
        for (var index = 0; index < names.Count; index++)
        {
            foreach (var line in comments[index]) lines.Add($"{Indent}// {line}");
            lines.Add($"{Indent}{members[index].Replace("\n", $"\n{Indent}")}");
        }
        for (var index = names.Count; index < members.Count; index++) lines.Add($"{Indent}{members[index]}");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 属性上 TypeScript 类型表达不出来的约束，写成 JSDoc 风格标签：
    /// <c>@integer @minimum 1 @maximum 100 @default 20</c>。没有则返回空串。
    /// 对应 TS <c>constraintTags</c>（`8a9c051f8` / #10707）。
    /// </summary>
    private static string ConstraintTags(JsonNode? property)
    {
        if (property is not JsonObject obj) return "";

        // 可选字段（Pydantic 的 `int | None`）把约束放在非 null 分支里；该分支的约束与属性自身的
        // 同时成立，两处都读。
        var variants = obj["anyOf"] as JsonArray ?? obj["oneOf"] as JsonArray;
        var nonNull = new List<JsonObject>();
        if (variants is not null)
        {
            foreach (var variant in variants)
            {
                if (variant is JsonObject branch && StringValue(branch["type"]) != "null") nonNull.Add(branch);
            }
        }

        var schemas = new List<JsonObject> { obj };
        if (nonNull.Count == 1) schemas.Add(nonNull[0]);

        var tags = new List<string>();
        if (schemas.Any(schema => IsIntegerType(schema["type"]))) tags.Add("@integer");
        foreach (var keyword in TagKeywords)
        {
            var values = schemas.Select(schema => KeywordValue(schema, keyword)).ToList();
            if (LowerBounds.Contains(keyword) || UpperBounds.Contains(keyword))
            {
                var numbers = new List<double>();
                foreach (var value in values)
                {
                    if (TryNumber(value, out var number)) numbers.Add(number);
                }
                // 同时成立的下界只有最大的那个有意义，上界反之。
                if (numbers.Count == 0) continue;
                tags.Add($"@{keyword} {FormatNumber(LowerBounds.Contains(keyword) ? numbers.Max() : numbers.Min())}");
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var rendered = TryNumber(value, out var number) ? FormatNumber(number) : StringValue(value);
                if (rendered is null || !seen.Add(rendered)) continue;
                tags.Add($"@{keyword} {rendered}");
            }
        }
        if (schemas.Any(schema => IsTrue(schema["uniqueItems"]))) tags.Add("@uniqueItems");
        // 标注（而非约束）只看属性自身。
        if (obj["default"] is { } fallback) tags.Add($"@default {Stringify(fallback)}");
        if (IsTrue(obj["deprecated"])) tags.Add("@deprecated");

        return string.Join(" ", tags
            .Where(tag => tag.Length <= MaxTagChars)
            // 注释里不能出现换行（含 U+2028 / U+2029），转成 \uXXXX 转义。
            .Select(tag => LineTerminators.Replace(tag, match => $"\\u{(int)match.Value[0]:x4}")));
    }

    /// <summary>
    /// 读一个约束关键字的值，并处理 Draft-04 / OpenAPI 3.0 的布尔形式
    /// （<c>exclusiveMinimum: true</c> 表示 <c>minimum</c> 本身是排他的）。
    /// </summary>
    private static JsonNode? KeywordValue(JsonObject schema, string keyword)
    {
        var exclusiveAsBoolean = keyword is "minimum" or "maximum"
            && IsTrue(schema[keyword == "minimum" ? "exclusiveMinimum" : "exclusiveMaximum"]);
        if (exclusiveAsBoolean) return null;
        if (keyword is "exclusiveMinimum" && IsTrue(schema["exclusiveMinimum"])) return schema["minimum"];
        if (keyword is "exclusiveMaximum" && IsTrue(schema["exclusiveMaximum"])) return schema["maximum"];
        return schema[keyword];
    }

    private static bool IsIntegerType(JsonNode? type)
        => StringValue(type) == "integer"
            || (type is JsonArray array && array.Any(item => StringValue(item) == "integer"));

    /// <summary>取 JSON 数字值（对齐 TS 的 <c>typeof value === "number"</c>）。</summary>
    private static bool TryNumber(JsonNode? value, out double number)
    {
        number = 0;
        return value is JsonValue primitive
            && primitive.GetValueKind() == JsonValueKind.Number
            && primitive.TryGetValue(out number);
    }

    /// <summary>JS 的数字字面量：整数不带小数点。</summary>
    private static string FormatNumber(double value)
        => value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>取 JSON 字符串值；非字符串（含缺省）返回 null（对齐 TS 的 typeof 检查）。</summary>
    private static string? StringValue(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.String
            ? primitive.GetValue<string>()
            : null;

    private static bool IsTrue(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.True;

    private static bool IsFalse(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.False;

    /// <summary>JS <c>JSON.stringify(value) ?? "unknown"</c>：缺省为 <c>unknown</c>，JSON null 为 <c>null</c>。</summary>
    private static string Stringify(JsonNode? value)
    {
        if (value is null) return "null";
        return value.ToJsonString(JsonStringifyOptions);
    }
}
