using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// The errors of a value that failed a schema: a walk over the schema reporting each failing keyword at the
/// value it failed on. Port of the TS <c>schema/explain.ts</c> — the messages are what the model sees, so they
/// are reproduced verbatim.
/// </summary>
internal sealed class SchemaExplainer
{
    /// <summary>Enum errors name the allowed values; a long list is cut, so the message stays readable.</summary>
    private const int ListedValues = 20;

    private readonly SchemaChecker _checker;
    private readonly SchemaResolver _resolver;
    private readonly JsonNode? _schema;

    /// <summary>
    /// The errors of <c>$ref</c> targets for object values, within one <see cref="Errors"/> call: a recursive
    /// union explains each branch to pick the one to blame, and every branch would explain the same children
    /// again, exponentially in the depth.
    /// </summary>
    private Dictionary<JsonObject, Dictionary<object, (string Path, List<SchemaValidationError> Errors)>>? _memo;

    public SchemaExplainer(SchemaChecker checker, SchemaResolver resolver, JsonNode? schema)
    {
        _checker = checker;
        _resolver = resolver;
        _schema = schema;
    }

    /// <summary>The errors of <paramref name="value"/>, at least one (the value failed the check).</summary>
    public IReadOnlyList<SchemaValidationError> Errors(JsonNode? value)
    {
        var output = new List<SchemaValidationError>();
        try
        {
            Walk(_schema, _resolver.RootBase, value, "", output);
        }
        finally
        {
            _memo = null;
        }
        if (output.Count == 0) output.Add(new SchemaValidationError("", "schema", "invalid"));
        return output;
    }

    private bool Ok(JsonNode? node, string baseUri, JsonNode? value) => _checker.Check(node, baseUri, value);

    private void Walk(JsonNode? node, string baseUri, JsonNode? value, string path, List<SchemaValidationError> output)
    {
        if (node is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (flag.GetValueKind() == JsonValueKind.False)
            {
                output.Add(new SchemaValidationError(path, "false", "no value is allowed here"));
            }
            return;
        }
        if (node is not JsonObject schema) return;
        var current = _resolver.BaseFor(schema, baseUri);
        if (Ok(schema, current, value)) return;

        var start = output.Count;
        void Push(string keyword, string message, string? at = null) =>
            output.Add(new SchemaValidationError(at ?? path, keyword, message));

        if (schema.ContainsKey("type"))
        {
            var types = SchemaChecker.Types(schema);
            if (!types.Any(type => SchemaChecker.MatchesType(value, type)))
            {
                Push("type", $"expected {string.Join(" or ", types)}, got {SchemaJson.TypeName(value)}");
                return;
            }
        }

        if (schema["enum"] is JsonArray enumValues
            && !enumValues.Any(member => SchemaJson.DeepEqual(member, value)))
        {
            Push("enum", $"expected one of {ListValues(enumValues)}");
        }
        if (schema.TryGetPropertyValue("const", out var constant) && !SchemaJson.DeepEqual(constant, value))
        {
            Push("const", $"expected {SchemaJson.Stringify(constant)}");
        }

        if (SchemaJson.IsString(value)) WalkString(schema, value!.GetValue<string>(), Push);
        if (SchemaJson.IsNumber(value, out var number)) WalkNumber(schema, number, Push);
        if (value is JsonObject obj) WalkObject(schema, current, obj, path, output, Push);
        if (value is JsonArray array) WalkArray(schema, current, array, path, output, Push);

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var member in allOf) Walk(member, current, value, path, output);
        }
        if (schema["anyOf"] is JsonArray anyOf) WalkUnion(anyOf, current, value, path, output, false);
        if (schema["oneOf"] is JsonArray oneOf) WalkUnion(oneOf, current, value, path, output, true);
        if (schema.TryGetPropertyValue("not", out var not) && Ok(not, current, value))
        {
            Push("not", "must not match the schema in not");
        }
        if (schema.TryGetPropertyValue("if", out var condition))
        {
            var branch = Ok(condition, current, value)
                ? (schema.TryGetPropertyValue("then", out var then) ? then : null)
                : (schema.TryGetPropertyValue("else", out var otherwise) ? otherwise : null);
            if (branch is not null) Walk(branch, current, value, path, output);
        }
        if (SchemaChecker.String(schema, "$ref") is { } reference) WalkRef(reference, current, value, path, output);
    }

    private void WalkRef(string reference, string baseUri, JsonNode? value, string path, List<SchemaValidationError> output)
    {
        var target = _resolver.Resolve(reference, baseUri);
        if (target.Node is JsonValue flag && flag.GetValueKind() == JsonValueKind.False
            && _resolver.IsUnresolved(reference, baseUri))
        {
            output.Add(new SchemaValidationError(path, "$ref", $"unresolved reference {reference}"));
            return;
        }
        if (target.Node is not JsonObject targetNode || value is not (JsonObject or JsonArray))
        {
            Walk(target.Node, target.Base, value, path, output);
            return;
        }

        _memo ??= new Dictionary<JsonObject, Dictionary<object, (string Path, List<SchemaValidationError> Errors)>>(
            ReferenceEqualityComparer.Instance);
        if (!_memo.TryGetValue(targetNode, out var values))
        {
            values = new Dictionary<object, (string Path, List<SchemaValidationError> Errors)>(
                ReferenceEqualityComparer.Instance);
            _memo[targetNode] = values;
        }
        if (values.TryGetValue(value!, out var known) && known.Path == path)
        {
            output.AddRange(known.Errors);
            return;
        }
        var errors = new List<SchemaValidationError>();
        Walk(targetNode, target.Base, value, path, errors);
        values[value!] = (path, errors);
        output.AddRange(errors);
    }

    private void WalkString(JsonObject schema, string value, Action<string, string, string?> push)
    {
        var length = SchemaJson.CodePoints(value);
        if (SchemaChecker.Number(schema, "minLength") is { } minLength && length < minLength)
        {
            push("minLength", $"expected at least {SchemaJson.NumberToString(minLength)} characters", null);
        }
        if (SchemaChecker.Number(schema, "maxLength") is { } maxLength && length > maxLength)
        {
            push("maxLength", $"expected at most {SchemaJson.NumberToString(maxLength)} characters", null);
        }
        if (SchemaChecker.String(schema, "pattern") is { } pattern && !SchemaChecker.RegexFor(pattern).IsMatch(value))
        {
            push("pattern", $"expected to match {pattern}", null);
        }
        if (_checker.FormatOf(schema) is { } format && !format(value))
        {
            push("format", $"expected format {SchemaChecker.String(schema, "format")}", null);
        }
    }

    private static void WalkNumber(JsonObject schema, double value, Action<string, string, string?> push)
    {
        if (SchemaChecker.Number(schema, "minimum") is { } minimum && value < minimum)
        {
            push("minimum", $"expected >= {SchemaJson.NumberToString(minimum)}", null);
        }
        if (SchemaChecker.Number(schema, "maximum") is { } maximum && value > maximum)
        {
            push("maximum", $"expected <= {SchemaJson.NumberToString(maximum)}", null);
        }
        if (SchemaChecker.Number(schema, "exclusiveMinimum") is { } exclusiveMinimum && value <= exclusiveMinimum)
        {
            push("exclusiveMinimum", $"expected > {SchemaJson.NumberToString(exclusiveMinimum)}", null);
        }
        if (SchemaChecker.Number(schema, "exclusiveMaximum") is { } exclusiveMaximum && value >= exclusiveMaximum)
        {
            push("exclusiveMaximum", $"expected < {SchemaJson.NumberToString(exclusiveMaximum)}", null);
        }
        if (SchemaChecker.Number(schema, "multipleOf") is { } multipleOf && !SchemaChecker.IsMultiple(value, multipleOf))
        {
            push("multipleOf", $"expected a multiple of {SchemaJson.NumberToString(multipleOf)}", null);
        }
    }

    private void WalkObject(
        JsonObject schema,
        string baseUri,
        JsonObject value,
        string path,
        List<SchemaValidationError> output,
        Action<string, string, string?> push)
    {
        var properties = schema["properties"] as JsonObject;
        foreach (var name in SchemaChecker.Strings(schema, "required"))
        {
            if (!value.ContainsKey(name)) push("required", "missing", SchemaJson.Pointer(path, name));
        }

        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                if (!value.ContainsKey(key)) continue;
                Walk(property, baseUri, value[key], SchemaJson.Pointer(path, key), output);
            }
        }

        var patterns = SchemaChecker.Patterns(schema);
        var hasAdditional = schema.ContainsKey("additionalProperties");
        var additional = schema.TryGetPropertyValue("additionalProperties", out var extra) ? extra : null;
        foreach (var (key, member) in value.ToList())
        {
            var matched = properties?.ContainsKey(key) == true;
            foreach (var (regex, patternSchema) in patterns)
            {
                if (!regex.IsMatch(key)) continue;
                matched = true;
                Walk(patternSchema, baseUri, member, SchemaJson.Pointer(path, key), output);
            }
            if (!matched && hasAdditional)
            {
                if (additional is JsonValue flag && flag.GetValueKind() == JsonValueKind.False)
                {
                    push("additionalProperties", "unknown field", SchemaJson.Pointer(path, key));
                }
                else
                {
                    Walk(additional, baseUri, member, SchemaJson.Pointer(path, key), output);
                }
            }
            if (schema.TryGetPropertyValue("propertyNames", out var propertyNames)
                && !Ok(propertyNames, baseUri, JsonValue.Create(key)))
            {
                push("propertyNames", "invalid property name", SchemaJson.Pointer(path, key));
            }
        }

        if (SchemaChecker.Number(schema, "minProperties") is { } minProperties && value.Count < minProperties)
        {
            push("minProperties", $"expected at least {SchemaJson.NumberToString(minProperties)} properties", null);
        }
        if (SchemaChecker.Number(schema, "maxProperties") is { } maxProperties && value.Count > maxProperties)
        {
            push("maxProperties", $"expected at most {SchemaJson.NumberToString(maxProperties)} properties", null);
        }

        foreach (var (key, dependency) in SchemaChecker.Dependencies(schema))
        {
            if (!value.ContainsKey(key)) continue;
            if (dependency is not JsonArray names)
            {
                Walk(dependency, baseUri, value, path, output);
                continue;
            }
            foreach (var name in names)
            {
                if (name is not JsonValue nameValue || nameValue.GetValueKind() != JsonValueKind.String) continue;
                var required = nameValue.GetValue<string>();
                if (!value.ContainsKey(required))
                {
                    push("dependentRequired", $"required when {key} is present", SchemaJson.Pointer(path, required));
                }
            }
        }
    }

    private void WalkArray(
        JsonObject schema,
        string baseUri,
        JsonArray value,
        string path,
        List<SchemaValidationError> output,
        Action<string, string, string?> push)
    {
        var (prefix, rest) = SchemaChecker.ItemSchemas(schema);
        for (var index = 0; index < value.Count; index++)
        {
            var item = index < prefix.Count ? prefix[index] : rest;
            if (item is null) continue;
            Walk(item, baseUri, value[index], $"{path}/{index}", output);
        }

        if (SchemaChecker.Number(schema, "minItems") is { } minItems && value.Count < minItems)
        {
            push("minItems", $"expected at least {SchemaJson.NumberToString(minItems)} items", null);
        }
        if (SchemaChecker.Number(schema, "maxItems") is { } maxItems && value.Count > maxItems)
        {
            push("maxItems", $"expected at most {SchemaJson.NumberToString(maxItems)} items", null);
        }
        if (schema["uniqueItems"] is JsonValue unique && unique.GetValueKind() == JsonValueKind.True && !IsUnique(value))
        {
            push("uniqueItems", "expected unique items", null);
        }
        if (schema.TryGetPropertyValue("contains", out var contains))
        {
            var min = SchemaChecker.Number(schema, "minContains") ?? 1;
            var max = SchemaChecker.Number(schema, "maxContains") ?? double.PositiveInfinity;
            var count = 0;
            var within = true;
            foreach (var item in value)
            {
                if (!Ok(contains, baseUri, item)) continue;
                if (++count > max) { within = false; break; }
            }
            if (!within || count < min) push("contains", ContainsMessage(min, max), null);
        }
    }

    private void WalkUnion(
        JsonArray branches,
        string baseUri,
        JsonNode? value,
        string path,
        List<SchemaValidationError> output,
        bool exactlyOne)
    {
        var matches = branches.Count(branch => Ok(branch, baseUri, value));
        if (matches == 1 || (!exactlyOne && matches > 0)) return;
        var keyword = exactlyOne ? "oneOf" : "anyOf";
        if (matches > 1)
        {
            output.Add(new SchemaValidationError(path, keyword, "matches more than one alternative"));
            return;
        }

        // A tagged union reports the branch its tag selects; otherwise the branch whose first error is deepest.
        if (TagOf(branches, baseUri) is { } tag && value is JsonObject obj)
        {
            var index = -1;
            for (var candidate = 0; candidate < tag.Values.Count; candidate++)
            {
                if (SchemaJson.SameValue(tag.Values[candidate], obj[tag.Key])) { index = candidate; break; }
            }
            if (index < 0)
            {
                output.Add(new SchemaValidationError(SchemaJson.Pointer(path, tag.Key), keyword,
                    $"expected one of {ListValues(tag.Values)}"));
            }
            else
            {
                Walk(branches[index], baseUri, value, path, output);
            }
            return;
        }

        // An object is blamed on the branch that declares most of its properties (the one it was most likely
        // meant for), then on the branch whose first error is deepest.
        (List<SchemaValidationError> Errors, int Declared)? best = null;
        foreach (var branch in branches)
        {
            var errors = new List<SchemaValidationError>();
            Walk(branch, baseUri, value, path, errors);
            if (errors.Count == 0) continue;
            var declared = value is JsonObject obj2 ? DeclaredKeys(branch, baseUri, obj2) : 0;
            var better = best is null
                || declared > best.Value.Declared
                || (declared == best.Value.Declared && errors[0].Path.Length > best.Value.Errors[0].Path.Length);
            if (better) best = (errors, declared);
        }
        if (best is null)
        {
            output.Add(new SchemaValidationError(path, keyword, "matches no alternative"));
            return;
        }
        output.AddRange(best.Value.Errors);
    }

    /// <summary>How many of the keys of <paramref name="value"/> the branch declares in <c>properties</c>.</summary>
    private int DeclaredKeys(JsonNode? branch, string baseUri, JsonObject value)
    {
        var node = branch is JsonObject candidate && SchemaChecker.String(candidate, "$ref") is { } reference
            ? _resolver.Resolve(reference, baseUri).Node
            : branch;
        var properties = node is JsonObject schema ? schema["properties"] as JsonObject : null;
        if (properties is null) return 0;
        return value.Count(entry => properties.ContainsKey(entry.Key));
    }

    /// <summary>The tag of a tagged union: a key whose <c>const</c> differs across every branch.</summary>
    private (string Key, List<JsonNode?> Values)? TagOf(JsonArray branches, string baseUri)
    {
        var resolved = new List<JsonNode?>();
        foreach (var branch in branches)
        {
            resolved.Add(branch is JsonObject candidate && SchemaChecker.String(candidate, "$ref") is { } reference
                ? _resolver.Resolve(reference, baseUri).Node
                : branch);
        }
        if (resolved.Count == 0 || resolved[0] is not JsonObject first) return null;
        if (first["properties"] is not JsonObject properties) return null;

        foreach (var (key, _) in properties)
        {
            var values = new List<JsonNode?>();
            foreach (var branch in resolved)
            {
                var tag = branch is JsonObject schema ? schema["properties"] as JsonObject : null;
                var constant = tag is not null && tag.TryGetPropertyValue(key, out var value)
                    && value is JsonObject tagSchema && tagSchema.ContainsKey("const")
                        ? tagSchema["const"]
                        : null;
                values.Add(constant);
            }
            var primitive = values.All(item => item is not null && item is not JsonObject and not JsonArray);
            if (!primitive) continue;
            var distinct = true;
            for (var index = 0; index < values.Count && distinct; index++)
            {
                for (var other = 0; other < index; other++)
                {
                    if (SchemaJson.SameValue(values[index], values[other])) { distinct = false; break; }
                }
            }
            if (distinct) return (key, values);
        }
        return null;
    }

    private static bool IsUnique(JsonArray value)
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

    /// <summary><paramref name="values"/> as JSON, at most <see cref="ListedValues"/> of them, then the rest.</summary>
    private static string ListValues(IEnumerable<JsonNode?> values)
    {
        var all = values.ToList();
        if (all.Count <= ListedValues) return SchemaJson.Stringify(new JsonArray(all.Select(Clone).ToArray()));
        var listed = SchemaJson.Stringify(new JsonArray(all.Take(ListedValues).Select(Clone).ToArray()));
        return $"{listed[..^1]}, ... and {all.Count - ListedValues} more]";
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static string ContainsMessage(double min, double max)
    {
        if (double.IsPositiveInfinity(max))
        {
            return $"expected at least {SchemaJson.NumberToString(min)} matching {(min == 1 ? "item" : "items")}";
        }
        if (min == 0) return $"expected at most {SchemaJson.NumberToString(max)} matching {(max == 1 ? "item" : "items")}";
        return $"expected between {SchemaJson.NumberToString(min)} and {SchemaJson.NumberToString(max)} matching items";
    }
}
