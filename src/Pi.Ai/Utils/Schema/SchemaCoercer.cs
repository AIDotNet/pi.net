using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// Coercion of loosely typed values (a model's tool call arguments) towards a schema, before it is checked.
/// Port of the TS <c>schema/coerce.ts</c> — three passes:
/// <list type="number">
/// <item>an optional <c>null</c> whose schema rejects <c>null</c> is dropped, as if omitted;</item>
/// <item>TypeBox 1.3.27's <c>Value.Convert</c> rules (<c>null</c> becomes <c>"null"</c> for a string, <c>"3.5"</c>
/// becomes 3 for an integer, a single value becomes a one-item array, a union takes the first branch the value
/// converts to);</item>
/// <item>primitive coercion by <c>type</c>, as in Ajv's <c>coerceTypes</c> (also for type arrays).</item>
/// </list>
/// Every pass is copy-on-write: a container is copied only when one of its members changes, and the input is
/// never modified. <c>.NET</c> JSON nodes cannot have two parents, so a copy deep-clones its members; the value
/// identities the TS relies on only matter while a value is being converted, and those are passed through
/// untouched.
/// </summary>
internal sealed class SchemaCoercer
{
    private readonly SchemaChecker _checker;
    private readonly SchemaResolver _resolver;
    private readonly JsonNode? _schema;

    /// <summary>(schema node, value) pairs pass 2 is converting through a re-entry point, as a stack.</summary>
    private readonly List<(JsonObject Node, JsonNode? Value)> _entered = [];

    /// <summary>How often a re-entry was cut short in this call; results computed across one are not memoized.</summary>
    private int _cuts;

    private Dictionary<JsonObject, Dictionary<object, JsonNode?>>? _memo;

    public SchemaCoercer(SchemaChecker checker, SchemaResolver resolver, JsonNode? schema)
    {
        _checker = checker;
        _resolver = resolver;
        _schema = schema;
    }

    /// <summary><paramref name="value"/> coerced: a copy along the changed paths only, or the value itself.</summary>
    public JsonNode? Coerce(JsonNode? value)
    {
        var withoutNulls = DropOptionalNulls(value, _schema, _resolver.RootBase);
        return CoerceTypes(ConvertOnly(withoutNulls), _schema, _resolver.RootBase);
    }

    /// <summary>Pass 2 alone, as TypeBox's <c>Value.Convert</c>.</summary>
    public JsonNode? ConvertOnly(JsonNode? value)
    {
        try
        {
            return Convert(_schema, _resolver.RootBase, value);
        }
        finally
        {
            _memo = null;
            _cuts = 0;
        }
    }

    // ---------------------------------------------------------------- pass 1: optional nulls

    private JsonNode? DropOptionalNulls(JsonNode? value, JsonNode? schema, string baseUri)
    {
        if (schema is not JsonObject node) return value;
        var current = _resolver.IndexedBaseOf(node, baseUri);

        if (value is JsonArray array)
        {
            return MapItems(array, (item, index) =>
            {
                var itemSchema = ItemSchemaAt(node, index);
                return itemSchema is null ? item : DropOptionalNulls(item, itemSchema, current);
            });
        }
        if (value is not JsonObject obj) return value;

        var properties = node["properties"] as JsonObject;
        var patterns = SchemaChecker.Patterns(node);
        var additional = node["additionalProperties"] as JsonObject;
        if (patterns.Count == 0 && additional is null && properties is null) return value;

        var required = SchemaChecker.Strings(node, "required");
        JsonObject? output = null;
        void Set(string key, JsonNode? next)
        {
            if (SchemaJson.SameValue(next, obj[key])) return;
            output ??= CloneObject(obj);
            output[key] = next?.DeepClone();
        }

        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                if (!obj.ContainsKey(key)) continue;
                var isRef = property is JsonObject candidate && SchemaJson.IsString(candidate["$ref"]);
                if (SchemaJson.IsNull(obj[key]) && !required.Contains(key) && !isRef
                    && Passes(property, current, null) == false)
                {
                    output ??= CloneObject(obj);
                    output.Remove(key);
                    continue;
                }
                Set(key, DropOptionalNulls(obj[key], property, current));
            }
        }

        if (patterns.Count > 0 || additional is not null)
        {
            // Values of pattern and additional properties are walked for their own optional nulls; the keys
            // themselves are not optional properties, so they stay.
            foreach (var (key, _) in obj.ToList())
            {
                var matched = properties?.ContainsKey(key) == true;
                foreach (var (regex, patternSchema) in patterns)
                {
                    if (!regex.IsMatch(key)) continue;
                    matched = true;
                    Set(key, DropOptionalNulls((output ?? obj)[key], patternSchema, current));
                }
                if (!matched && additional is not null) Set(key, DropOptionalNulls(obj[key], additional, current));
            }
        }

        return output ?? value;
    }

    // ---------------------------------------------------------------- pass 2: TypeBox conversions

    private JsonNode? Convert(JsonNode? schema, string baseUri, JsonNode? value)
    {
        if (schema is not JsonObject node) return value;
        var current = _resolver.IndexedBaseOf(node, baseUri);
        if (SchemaChecker.String(node, "$ref") is { } reference)
        {
            var target = _resolver.Resolve(reference, current);
            return SchemaJson.IsSchemaObject(target.Node) ? Reenter((JsonObject)target.Node!, target.Base, value, true) : value;
        }
        return ConvertNode(node, current, value);
    }

    /// <summary>One schema node's conversion: its first keyword among const, enum, the unions, allOf and type.</summary>
    private JsonNode? ConvertNode(JsonObject node, string baseUri, JsonNode? value)
    {
        if (node.ContainsKey("const")) return ConvertLiteral(node["const"], value);
        if (node["enum"] is JsonArray enumValues) return ConvertEnum(node, enumValues, baseUri, value);
        if (node["anyOf"] is JsonArray anyOf) return ConvertUnion(node, anyOf, baseUri, value);
        if (node["oneOf"] is JsonArray oneOf) return ConvertUnion(node, oneOf, baseUri, value);
        if (node["allOf"] is JsonArray allOf)
        {
            var next = value;
            foreach (var member in allOf) next = Convert(member, baseUri, next);
            return ConvertMembers(node, baseUri, next);
        }
        switch (SchemaChecker.String(node, "type"))
        {
            case "string":
                return OrValue(TryStringValue(value, out var text), text, value);
            case "number":
                return OrValue(TryNumberValue(value, out var number), JsonValue.Create(number), value);
            case "integer":
                return TryNumberValue(value, out var integer) ? JsonValue.Create(Math.Truncate(integer)) : value;
            case "boolean":
                return OrValue(TryBooleanValue(value, out var flag), JsonValue.Create(flag), value);
            case "null":
                return OrValue(TryNullValue(value), null, value);
            case "array":
                return ConvertArray(node, baseUri, value);
            case "object":
                return ConvertObject(node, baseUri, value);
            case null:
                if (node.ContainsKey("properties") || node.ContainsKey("patternProperties"))
                {
                    return ConvertObject(node, baseUri, value);
                }
                if (node.ContainsKey("items") || node.ContainsKey("prefixItems")) return ConvertArray(node, baseUri, value);
                return value;
            default:
                return value;
        }
    }

    /// <summary>An enum is a union of its values: a member stays, else the first value the input converts to.</summary>
    private JsonNode? ConvertEnum(JsonObject node, JsonArray values, string baseUri, JsonNode? value)
    {
        foreach (var member in values)
        {
            if (SchemaJson.SameValue(member, value)) return value;
        }
        foreach (var member in values)
        {
            var candidate = ConvertLiteral(member, value);
            if (!SchemaJson.SameValue(candidate, value) && Passes(node, baseUri, candidate) == true) return candidate;
        }
        return value;
    }

    /// <summary>
    /// A value one branch accepts stays; else the first branch's conversion that the whole union accepts.
    /// A node that also declares members converts them into each candidate before it is checked.
    /// </summary>
    private JsonNode? ConvertUnion(JsonObject node, JsonArray branches, string baseUri, JsonNode? value)
    {
        foreach (var branch in branches)
        {
            if (Passes(branch, baseUri, value) == true) return ConvertMembers(node, baseUri, value);
        }
        foreach (var branch in branches)
        {
            var candidate = ConvertMembers(node, baseUri, Convert(branch, baseUri, value));
            if (Passes(node, baseUri, candidate) == true) return candidate;
        }
        return ConvertMembers(node, baseUri, value);
    }

    /// <summary>The object members of a union or intersection node, when it declares any.</summary>
    private JsonNode? ConvertMembers(JsonObject node, string baseUri, JsonNode? value)
    {
        var declares = node.ContainsKey("properties") || node.ContainsKey("patternProperties")
            || node.ContainsKey("additionalProperties");
        return declares ? ConvertObject(node, baseUri, value) : value;
    }

    private JsonNode? ConvertArray(JsonObject node, string baseUri, JsonNode? value)
    {
        var prefix = node["items"] is JsonArray tuple
            ? tuple
            : node["prefixItems"] as JsonArray;
        if (prefix is not null)
        {
            // A tuple converts its items and never wraps a single value.
            if (value is not JsonArray items) return value;
            var rest = node["items"] is JsonArray ? null : node["items"];
            return MapItems(items, (item, index) =>
            {
                var itemSchema = index < prefix.Count ? prefix[index] : rest;
                return itemSchema is null ? item : Convert(itemSchema, baseUri, item);
            });
        }
        if (value is JsonArray list)
        {
            return node["items"] is null ? value : MapItems(list, (item, _) => Convert(node["items"], baseUri, item));
        }
        // The wrapped single value is converted by the item schema with the same value: a re-entry point.
        var item = node["items"];
        var converted = item is JsonObject itemNode ? Reenter(itemNode, baseUri, value, false) : value;
        return new JsonArray(converted?.DeepClone());
    }

    private JsonNode? ConvertObject(JsonObject node, string baseUri, JsonNode? value)
    {
        if (value is not JsonObject obj) return value;
        var properties = node["properties"] as JsonObject;
        var patterns = SchemaChecker.Patterns(node);
        var additional = node["additionalProperties"] as JsonObject;
        JsonObject? output = null;

        foreach (var (key, member) in obj.ToList())
        {
            var next = member;
            var matched = properties?.ContainsKey(key) == true;
            if (matched) next = Convert(properties![key], baseUri, next);
            foreach (var (regex, patternSchema) in patterns)
            {
                if (!regex.IsMatch(key)) continue;
                matched = true;
                next = Convert(patternSchema, baseUri, next);
            }
            if (!matched && additional is not null) next = Convert(additional, baseUri, next);
            if (SchemaJson.SameValue(next, member)) continue;
            output ??= CloneObject(obj);
            output[key] = next?.DeepClone();
        }

        return output ?? value;
    }

    // ---------------------------------------------------------------- pass 3: primitive coercion by type

    private JsonNode? CoerceTypes(JsonNode? value, JsonNode? schema, string baseUri)
    {
        if (schema is not JsonObject node) return value;
        var current = _resolver.IndexedBaseOf(node, baseUri);
        var next = value;

        if (node["allOf"] is JsonArray allOf)
        {
            foreach (var member in allOf) next = CoerceTypes(next, member, current);
        }
        if (node["anyOf"] is JsonArray anyOf) next = CoerceUnion(next, anyOf, current);
        if (node["oneOf"] is JsonArray oneOf) next = CoerceUnion(next, oneOf, current);

        var types = SchemaChecker.Types(node);
        var matchesUnionMember = types.Count > 1 && types.Any(type => SchemaChecker.MatchesType(next, type));
        if (types.Count > 0 && !matchesUnionMember)
        {
            foreach (var type in types)
            {
                var candidate = CoercePrimitive(next, type);
                if (SchemaJson.SameValue(candidate, next)) continue;
                next = candidate;
                break;
            }
        }

        if (types.Contains("object") && next is JsonObject obj) next = CoerceProperties(obj, node, current);
        if (types.Contains("array") && next is JsonArray array) next = CoerceItems(array, node, current);
        return next;
    }

    private JsonNode? CoerceUnion(JsonNode? value, JsonArray branches, string baseUri)
    {
        foreach (var branch in branches)
        {
            if (Passes(branch, baseUri, value) == true) return value;
        }
        foreach (var branch in branches)
        {
            var coerced = CoerceTypes(value, branch, baseUri);
            if (Passes(branch, baseUri, coerced) == true) return coerced;
        }
        return value;
    }

    private JsonNode? CoerceProperties(JsonObject value, JsonObject schema, string baseUri)
    {
        var properties = schema["properties"] as JsonObject;
        var patterns = SchemaChecker.Patterns(schema);
        var additional = schema["additionalProperties"] as JsonObject;
        JsonObject? output = null;
        void Set(string key, JsonNode? next)
        {
            if (SchemaJson.SameValue(next, value[key])) return;
            output ??= CloneObject(value);
            output[key] = next?.DeepClone();
        }

        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                if (value.ContainsKey(key)) Set(key, CoerceTypes(value[key], property, baseUri));
            }
        }
        foreach (var (key, _) in value.ToList())
        {
            var matched = properties?.ContainsKey(key) == true;
            foreach (var (regex, patternSchema) in patterns)
            {
                if (!regex.IsMatch(key)) continue;
                matched = true;
                Set(key, CoerceTypes((output ?? value)[key], patternSchema, baseUri));
            }
            if (!matched && additional is not null) Set(key, CoerceTypes(value[key], additional, baseUri));
        }
        return output ?? value;
    }

    private JsonNode? CoerceItems(JsonArray value, JsonObject schema, string baseUri) =>
        MapItems(value, (item, index) =>
        {
            var itemSchema = ItemSchemaAt(schema, index);
            return SchemaJson.IsSchemaObject(itemSchema) ? CoerceTypes(item, itemSchema, baseUri) : item;
        });

    // ---------------------------------------------------------------- helpers

    /// <summary>The schema of an array's item at <paramref name="index"/>, or null when nothing applies.</summary>
    private static JsonNode? ItemSchemaAt(JsonObject schema, int index)
    {
        if (schema["items"] is JsonArray tuple) return index < tuple.Count ? tuple[index] : null;
        if (schema["prefixItems"] is JsonArray prefix && index < prefix.Count) return prefix[index];
        return schema["items"];
    }

    /// <summary><c>items</c> mapped by <paramref name="map"/>; the same array when every item maps to itself.</summary>
    private static JsonArray MapItems(JsonArray items, Func<JsonNode?, int, JsonNode?> map)
    {
        JsonArray? output = null;
        for (var index = 0; index < items.Count; index++)
        {
            var original = items[index];
            var next = map(original, index);
            if (SchemaJson.SameValue(next, original))
            {
                output?.Add(original?.DeepClone());
                continue;
            }
            output ??= ClonePrefix(items, index);
            output.Add(next?.DeepClone());
        }
        return output ?? items;
    }

    private static JsonArray ClonePrefix(JsonArray items, int count)
    {
        var copy = new JsonArray();
        for (var index = 0; index < count; index++) copy.Add(items[index]?.DeepClone());
        return copy;
    }

    private static JsonObject CloneObject(JsonObject value)
    {
        var copy = new JsonObject();
        foreach (var (key, member) in value) copy[key] = member?.DeepClone();
        return copy;
    }

    /// <summary>
    /// <paramref name="node"/> converted for <paramref name="value"/> at a re-entry point: unchanged when that
    /// pair is already being converted, so a recursive schema cannot convert a value without end.
    /// </summary>
    private JsonNode? Reenter(JsonObject node, string baseUri, JsonNode? value, bool memoize)
    {
        foreach (var (enteredNode, enteredValue) in _entered)
        {
            if (ReferenceEquals(enteredNode, node) && SchemaJson.SameValue(enteredValue, value))
            {
                _cuts++;
                return value;
            }
        }
        if (memoize && _memo is not null && _memo.TryGetValue(node, out var values)
            && value is not null && values.TryGetValue(value, out var known))
        {
            return known;
        }

        var cuts = _cuts;
        _entered.Add((node, value));
        JsonNode? result;
        try
        {
            result = Convert(node, baseUri, value);
        }
        finally
        {
            _entered.RemoveAt(_entered.Count - 1);
        }

        if (memoize && _cuts == cuts && value is not null)
        {
            _memo ??= new Dictionary<JsonObject, Dictionary<object, JsonNode?>>(ReferenceEqualityComparer.Instance);
            if (!_memo.TryGetValue(node, out var bucket))
            {
                bucket = new Dictionary<object, JsonNode?>(ReferenceEqualityComparer.Instance);
                _memo[node] = bucket;
            }
            bucket[value] = result;
        }
        return result;
    }

    /// <summary>Whether <paramref name="value"/> passes <paramref name="schema"/>; null when it cannot be compiled.</summary>
    private bool? Passes(JsonNode? schema, string baseUri, JsonNode? value)
    {
        try
        {
            var current = schema is JsonObject node ? _resolver.IndexedBaseOf(node, baseUri) : baseUri;
            return _checker.Check(schema, current, value);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // The TS swallows a schema it cannot compile (a malformed `pattern`, a bad subschema) here.
            return null;
        }
    }

    // ---------------------------------------------------------------- TypeBox's conversions of one value

    private static JsonNode? OrValue(bool converted, JsonNode? result, JsonNode? value) => converted ? result : value;

    /// <summary>A literal (const, an enum value) taken from a value of another type that converts to exactly it.</summary>
    private static JsonNode? ConvertLiteral(JsonNode? literal, JsonNode? value)
    {
        if (SchemaJson.SameValue(literal, value)) return value;
        if (SchemaJson.IsString(literal))
        {
            return TryStringValue(value, out var text) && SchemaJson.SameValue(text, literal) ? text : value;
        }
        if (SchemaJson.IsNumber(literal, out var number))
        {
            return TryNumberValue(value, out var converted) && converted.Equals(number) ? JsonValue.Create(converted) : value;
        }
        if (SchemaJson.IsBoolean(literal))
        {
            return TryBooleanValue(value, out var flag) && SchemaJson.SameValue(JsonValue.Create(flag), literal)
                ? JsonValue.Create(flag)
                : value;
        }
        return value;
    }

    private static bool TryStringValue(JsonNode? value, out JsonNode? result)
    {
        result = null;
        if (SchemaJson.IsNumber(value, out var number))
        {
            result = JsonValue.Create(SchemaJson.NumberToString(number));
            return true;
        }
        if (SchemaJson.IsBoolean(value))
        {
            result = JsonValue.Create(value!.GetValueKind() == JsonValueKind.True ? "true" : "false");
            return true;
        }
        if (SchemaJson.IsNull(value)) { result = JsonValue.Create("null"); return true; }
        if (SchemaJson.IsString(value)) { result = value; return true; }
        return false;
    }

    private static bool TryNumberValue(JsonNode? value, out double result)
    {
        result = 0;
        if (SchemaJson.IsBoolean(value))
        {
            result = value!.GetValueKind() == JsonValueKind.True ? 1 : 0;
            return true;
        }
        if (SchemaJson.IsNumber(value, out var number))
        {
            result = number;
            return !double.IsInfinity(number) && !double.IsNaN(number);
        }
        if (SchemaJson.IsNull(value)) { result = 0; return true; }
        if (!SchemaJson.IsString(value)) return false;

        var text = value!.GetValue<string>();
        var parsed = SchemaJson.ParseJsNumber(text);
        if (!double.IsNaN(parsed) && !double.IsInfinity(parsed)) { result = parsed; return true; }
        var lowercase = text.ToLowerInvariant();
        if (lowercase == "false") { result = 0; return true; }
        if (lowercase == "true") { result = 1; return true; }
        if (!SchemaJson.TryBigInteger(text, out var big) || !SchemaJson.IsSafeInteger(big)) return false;
        result = (double)big;
        return true;
    }

    private static bool TryBooleanValue(JsonNode? value, out bool result)
    {
        result = false;
        if (SchemaJson.IsBoolean(value)) { result = value!.GetValueKind() == JsonValueKind.True; return true; }
        if (SchemaJson.IsNumber(value, out var number))
        {
            if (number == 0) return true;
            if (number == 1) { result = true; return true; }
            return false;
        }
        if (SchemaJson.IsNull(value)) return true;
        if (!SchemaJson.IsString(value)) return false;
        var text = value!.GetValue<string>();
        var lowercase = text.ToLowerInvariant();
        if (lowercase == "false" || text == "0") return true;
        if (lowercase == "true" || text == "1") { result = true; return true; }
        return false;
    }

    private static bool TryNullValue(JsonNode? value)
    {
        if (SchemaJson.IsBoolean(value)) return value!.GetValueKind() == JsonValueKind.False;
        if (SchemaJson.IsNumber(value, out var number)) return number == 0;
        if (SchemaJson.IsNull(value)) return true;
        if (!SchemaJson.IsString(value)) return false;
        var text = value!.GetValue<string>();
        var lowercase = text.ToLowerInvariant();
        return lowercase is "undefined" or "null" || text.Length == 0 || text == "0";
    }

    /// <summary>Primitive coercion by type (pass 3), as in Ajv's <c>coerceTypes</c>.</summary>
    private static JsonNode? CoercePrimitive(JsonNode? value, string type)
    {
        switch (type)
        {
            case "number":
            case "integer":
            {
                if (SchemaJson.IsNull(value)) return JsonValue.Create(0d);
                if (SchemaJson.IsString(value))
                {
                    var text = value!.GetValue<string>();
                    if (text.Trim().Length > 0)
                    {
                        var parsed = SchemaJson.ParseJsNumber(text);
                        var usable = type == "number"
                            ? !double.IsNaN(parsed) && !double.IsInfinity(parsed)
                            : !double.IsNaN(parsed) && !double.IsInfinity(parsed) && Math.Floor(parsed) == parsed;
                        if (usable) return JsonValue.Create(parsed);
                    }
                }
                if (SchemaJson.IsBoolean(value))
                {
                    return JsonValue.Create(value!.GetValueKind() == JsonValueKind.True ? 1d : 0d);
                }
                return value;
            }
            case "boolean":
            {
                if (SchemaJson.IsNull(value)) return JsonValue.Create(false);
                if (SchemaJson.IsString(value))
                {
                    var text = value!.GetValue<string>();
                    if (text == "true") return JsonValue.Create(true);
                    if (text == "false") return JsonValue.Create(false);
                }
                if (SchemaJson.IsNumber(value, out var number))
                {
                    if (number == 1) return JsonValue.Create(true);
                    if (number == 0) return JsonValue.Create(false);
                }
                return value;
            }
            case "string":
            {
                if (SchemaJson.IsNull(value)) return JsonValue.Create("");
                if (SchemaJson.IsNumber(value, out var number)) return JsonValue.Create(SchemaJson.NumberToString(number));
                if (SchemaJson.IsBoolean(value))
                {
                    return JsonValue.Create(value!.GetValueKind() == JsonValueKind.True ? "true" : "false");
                }
                return value;
            }
            case "null":
            {
                if (SchemaJson.IsString(value) && value!.GetValue<string>().Length == 0) return null;
                if (SchemaJson.IsNumber(value, out var number) && number == 0) return null;
                if (SchemaJson.IsBoolean(value) && value!.GetValueKind() == JsonValueKind.False) return null;
                return value;
            }
            default:
                return value;
        }
    }
}
