using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// The compiled check of a schema: whether a value is valid. Port of the TS <c>schema/compiler.ts</c> +
/// <c>checks.ts</c> — the TS builds one closure per subschema, this walks the schema for every value, which is
/// the same predicate with less bookkeeping. Keywords that only annotate (<c>title</c>, <c>description</c>,
/// <c>default</c>, <c>$comment</c>, …) are ignored; <c>unevaluatedProperties</c>/<c>unevaluatedItems</c> and
/// <c>$dynamicRef</c> are not supported (no tool schema uses them).
/// </summary>
internal sealed class SchemaChecker
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    private readonly SchemaResolver _resolver;
    private readonly IReadOnlyDictionary<string, Func<string, bool>>? _formats;

    public SchemaChecker(SchemaResolver resolver, IReadOnlyDictionary<string, Func<string, bool>>? formats)
    {
        _resolver = resolver;
        _formats = formats;
    }

    public SchemaResolver Resolver => _resolver;

    public bool Check(JsonNode? node, string baseUri, JsonNode? value)
    {
        switch (node)
        {
            case null:
                return true;
            case JsonValue flag when flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return flag.GetValueKind() == JsonValueKind.True;
            case not JsonObject:
                throw new InvalidOperationException($"A schema must be an object or a boolean, got {SchemaJson.Stringify(node)}");
        }

        var schema = (JsonObject)node;
        var current = _resolver.BaseFor(schema, baseUri);

        if (schema.ContainsKey("type"))
        {
            var types = Types(schema);
            var matches = false;
            foreach (var type in types)
            {
                if (MatchesType(value, type)) { matches = true; break; }
            }
            if (!matches) return false;
        }

        if (schema["enum"] is JsonArray enumValues && !enumValues.Any(member => SchemaJson.DeepEqual(member, value)))
        {
            return false;
        }
        if (schema.TryGetPropertyValue("const", out var constant) && !SchemaJson.DeepEqual(constant, value)) return false;

        if (SchemaJson.IsString(value) && !StringKeywords(schema, value!.GetValue<string>())) return false;
        if (SchemaJson.IsNumber(value, out var number) && !NumberKeywords(schema, number)) return false;
        if (value is JsonObject obj && !ObjectKeywords(schema, current, obj)) return false;
        if (value is JsonArray array && !ArrayKeywords(schema, current, array)) return false;

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var member in allOf)
            {
                if (!Check(member, current, value)) return false;
            }
        }
        if (schema["anyOf"] is JsonArray anyOf && !anyOf.Any(member => Check(member, current, value))) return false;
        if (schema["oneOf"] is JsonArray oneOf)
        {
            var matches = 0;
            foreach (var member in oneOf)
            {
                if (Check(member, current, value) && ++matches > 1) return false;
            }
            if (matches != 1) return false;
        }
        if (schema.TryGetPropertyValue("not", out var not) && Check(not, current, value)) return false;
        if (schema.TryGetPropertyValue("if", out var condition)
            && (schema.ContainsKey("then") || schema.ContainsKey("else")))
        {
            var branch = Check(condition, current, value)
                ? (schema.TryGetPropertyValue("then", out var then) ? then : null)
                : (schema.TryGetPropertyValue("else", out var otherwise) ? otherwise : null);
            if (branch is not null && !Check(branch, current, value)) return false;
        }
        if (schema.TryGetPropertyValue("$ref", out var reference) && SchemaJson.IsString(reference))
        {
            var target = _resolver.Resolve(reference!.GetValue<string>(), current);
            if (!Check(target.Node, target.Base, value)) return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- keyword groups

    private bool StringKeywords(JsonObject schema, string value)
    {
        var min = Number(schema, "minLength") ?? 0;
        var max = Number(schema, "maxLength") ?? double.PositiveInfinity;
        if (!SchemaJson.LengthOk(value, min, max)) return false;
        if (String(schema, "pattern") is { } pattern && !RegexFor(pattern).IsMatch(value)) return false;
        if (FormatOf(schema) is { } format && !format(value)) return false;
        return true;
    }

    private static bool NumberKeywords(JsonObject schema, double value)
    {
        if (Number(schema, "minimum") is { } minimum && value < minimum) return false;
        if (Number(schema, "maximum") is { } maximum && value > maximum) return false;
        if (Number(schema, "exclusiveMinimum") is { } exclusiveMinimum && value <= exclusiveMinimum) return false;
        if (Number(schema, "exclusiveMaximum") is { } exclusiveMaximum && value >= exclusiveMaximum) return false;
        if (Number(schema, "multipleOf") is { } multipleOf && !IsMultiple(value, multipleOf)) return false;
        return true;
    }

    private bool ObjectKeywords(JsonObject schema, string baseUri, JsonObject value)
    {
        var properties = schema["properties"] as JsonObject;
        var required = Strings(schema, "required");

        foreach (var name in required)
        {
            if (!value.ContainsKey(name)) return false;
        }

        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                if (!value.ContainsKey(key)) continue;
                if (!Check(property, baseUri, value[key])) return false;
            }
        }

        var patterns = Patterns(schema);
        var additional = schema.TryGetPropertyValue("additionalProperties", out var extra) ? extra : null;
        if (patterns.Count > 0 || additional is not null)
        {
            foreach (var (key, member) in value)
            {
                var matched = properties?.ContainsKey(key) == true;
                foreach (var (regex, patternSchema) in patterns)
                {
                    if (!regex.IsMatch(key)) continue;
                    matched = true;
                    if (!Check(patternSchema, baseUri, member)) return false;
                }
                if (matched || additional is null) continue;
                if (additional is JsonValue flag && flag.GetValueKind() == JsonValueKind.False) return false;
                if (!Check(additional, baseUri, member)) return false;
            }
        }

        if (schema.TryGetPropertyValue("propertyNames", out var propertyNames))
        {
            foreach (var (key, _) in value)
            {
                if (!Check(propertyNames, baseUri, JsonValue.Create(key))) return false;
            }
        }

        if (Number(schema, "minProperties") is { } minProperties && value.Count < minProperties) return false;
        if (Number(schema, "maxProperties") is { } maxProperties && value.Count > maxProperties) return false;

        foreach (var (key, dependency) in Dependencies(schema))
        {
            if (!value.ContainsKey(key)) continue;
            if (dependency is JsonArray names)
            {
                foreach (var name in names)
                {
                    if (name is JsonValue nameValue && nameValue.GetValueKind() == JsonValueKind.String
                        && !value.ContainsKey(nameValue.GetValue<string>()))
                    {
                        return false;
                    }
                }
            }
            else if (!Check(dependency, baseUri, value))
            {
                return false;
            }
        }

        return true;
    }

    private bool ArrayKeywords(JsonObject schema, string baseUri, JsonArray value)
    {
        var (prefix, rest) = ItemSchemas(schema);
        for (var index = 0; index < value.Count; index++)
        {
            var item = index < prefix.Count ? prefix[index] : rest;
            if (item is null) continue;
            if (!Check(item, baseUri, value[index])) return false;
        }

        if (Number(schema, "minItems") is { } minItems && value.Count < minItems) return false;
        if (Number(schema, "maxItems") is { } maxItems && value.Count > maxItems) return false;
        if (schema["uniqueItems"] is JsonValue unique && unique.GetValueKind() == JsonValueKind.True && !Unique(value))
        {
            return false;
        }
        if (schema.TryGetPropertyValue("contains", out var contains))
        {
            var min = Number(schema, "minContains") ?? 1;
            var max = Number(schema, "maxContains") ?? double.PositiveInfinity;
            var count = 0;
            foreach (var item in value)
            {
                if (Check(contains, baseUri, item) && ++count > max) return false;
            }
            if (count < min) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- shared pieces

    /// <summary>The tuple prefix and the schema of the items after it (the TS <c>arrayGroup</c> split).</summary>
    public static (IReadOnlyList<JsonNode?> Prefix, JsonNode? Items) ItemSchemas(JsonObject schema)
    {
        if (schema["items"] is JsonArray tuple)
        {
            return (tuple.ToList(), schema.TryGetPropertyValue("additionalItems", out var additional) ? additional : null);
        }
        var prefix = schema["prefixItems"] is JsonArray prefixItems ? prefixItems.ToList() : [];
        return (prefix, schema.TryGetPropertyValue("items", out var items) ? items : null);
    }

    /// <summary>The pattern properties of a schema, compiled (the TS <c>patternProperties</c> entries).</summary>
    public static IReadOnlyList<(Regex Regex, JsonNode? Schema)> Patterns(JsonObject schema)
    {
        if (schema["patternProperties"] is not JsonObject patterns) return [];
        var result = new List<(Regex, JsonNode?)>(patterns.Count);
        foreach (var (pattern, value) in patterns) result.Add((RegexFor(pattern), value));
        return result;
    }

    /// <summary>
    /// <c>dependentRequired</c> and <c>dependencies</c> merged the way the TS spreads them (a key both name takes
    /// the <c>dependencies</c> value), then <c>dependentSchemas</c>.
    /// </summary>
    public static IEnumerable<(string Key, JsonNode? Dependency)> Dependencies(JsonObject schema)
    {
        var dependencies = schema["dependencies"] as JsonObject;
        if (schema["dependentRequired"] is JsonObject dependentRequired)
        {
            foreach (var (key, list) in dependentRequired)
            {
                if (list is JsonArray && dependencies?.ContainsKey(key) != true) yield return (key, list);
            }
        }
        if (dependencies is not null)
        {
            foreach (var (key, dependency) in dependencies) yield return (key, dependency);
        }
        if (schema["dependentSchemas"] is JsonObject dependentSchemas)
        {
            foreach (var (key, dependency) in dependentSchemas) yield return (key, dependency);
        }
    }

    public static Regex RegexFor(string pattern) =>
        RegexCache.GetOrAdd(pattern, static source => new Regex(source, RegexOptions.None, RegexTimeout));

    /// <summary>The format check of a schema, when the format is one this build knows.</summary>
    public Func<string, bool>? FormatOf(JsonObject schema)
    {
        if (_formats is null) return null;
        return String(schema, "format") is { } name && _formats.TryGetValue(name, out var check) ? check : null;
    }

    /// <summary>Whether <paramref name="value"/> has one of the schema's <c>type</c> names.</summary>
    public static bool MatchesType(JsonNode? value, string type) => type switch
    {
        // 上游 1.5.1（#11833 配套，schema/checks.ts）：number 必须有限；
        // JSON 里没有 NaN/Infinity，只有代码里构造的值才可能是，TypeBox 语义同样拒收。
        "number" => SchemaJson.IsNumber(value, out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number),
        "integer" => SchemaJson.IsInteger(value, out _),
        "boolean" => SchemaJson.IsBoolean(value),
        "string" => SchemaJson.IsString(value),
        "null" => SchemaJson.IsNull(value),
        "array" => value is JsonArray,
        "object" => value is JsonObject,
        // "bytes" is a Uint8Array upstream; a JSON value never is one.
        _ => false,
    };

    /// <summary>The <c>type</c> names of a schema (a string or an array of strings).</summary>
    public static IReadOnlyList<string> Types(JsonObject schema)
    {
        var node = schema["type"];
        if (SchemaJson.IsString(node)) return [node!.GetValue<string>()];
        if (node is not JsonArray array) return [];
        return array.OfType<JsonValue>()
            .Where(value => value.GetValueKind() == JsonValueKind.String)
            .Select(value => value.GetValue<string>())
            .ToList();
    }

    /// <summary>Whether <paramref name="value"/> is a multiple of <paramref name="divisor"/>, exactly for decimals.</summary>
    public static bool IsMultiple(double value, double divisor)
    {
        var quotient = value / divisor;
        if (!double.IsInfinity(quotient) && Math.Floor(quotient) == quotient) return true;
        if (double.IsNaN(quotient) || double.IsInfinity(quotient)) return value % divisor == 0;
        var (valueMantissa, valueExponent) = Decimal(value);
        var (divisorMantissa, divisorExponent) = Decimal(divisor);
        return valueExponent >= divisorExponent
            ? (valueMantissa * BigInteger.Pow(10, valueExponent - divisorExponent)) % divisorMantissa == 0
            : valueMantissa % (divisorMantissa * BigInteger.Pow(10, divisorExponent - valueExponent)) == 0;
    }

    // The TS decimal(): the shortest decimal digits of |x| and their power of ten.
    private static (BigInteger Mantissa, int Exponent) Decimal(double value)
    {
        var text = SchemaJson.NumberToString(Math.Abs(value));
        var exponent = 0;
        var eIndex = text.IndexOf('e');
        if (eIndex >= 0)
        {
            exponent = int.Parse(text[(eIndex + 1)..], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture);
            text = text[..eIndex];
        }
        var point = text.IndexOf('.');
        var fraction = point < 0 ? 0 : text.Length - point - 1;
        var digits = point < 0 ? text : text.Remove(point, 1);
        return (BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), exponent - fraction);
    }

    private static bool Unique(JsonArray value)
    {
        for (var index = 1; index < value.Count; index++)
        {
            for (var previous = 0; previous < index; previous++)
            {
                if (SchemaJson.DeepEqual(value[index], value[previous])) return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- keyword readers

    public static double? Number(JsonObject schema, string key) =>
        schema.TryGetPropertyValue(key, out var node) && SchemaJson.IsNumber(node, out var value) ? value : null;

    public static string? String(JsonObject schema, string key) =>
        schema.TryGetPropertyValue(key, out var node) && SchemaJson.IsString(node) ? node!.GetValue<string>() : null;

    public static IReadOnlyList<string> Strings(JsonObject schema, string key)
    {
        if (schema[key] is not JsonArray array) return [];
        return array.OfType<JsonValue>()
            .Where(value => value.GetValueKind() == JsonValueKind.String)
            .Select(value => value.GetValue<string>())
            .ToList();
    }
}
