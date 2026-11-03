using System.Text.Json.Nodes;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// One failure of a value: where it is (a JSON Pointer, "" for the value itself), the failing keyword and a
/// message. Port of the TS <c>ValidationError</c>.
/// </summary>
public sealed record SchemaValidationError(string Path, string Keyword, string Message);

/// <summary>The result of <see cref="ToolSchemaValidator.Parse"/>: the coerced value, or the errors of it.</summary>
public sealed record SchemaParseResult(bool Ok, JsonNode? Value, IReadOnlyList<SchemaValidationError> Errors)
{
    public static SchemaParseResult Success(JsonNode? value) => new(true, value, []);

    public static SchemaParseResult Failure(IReadOnlyList<SchemaValidationError> errors) => new(false, null, errors);
}

/// <summary>
/// A compiled tool-parameter schema: <c>check</c> is the boolean path, <see cref="Parse"/> coerces then checks.
/// Port of the TS <c>compile(schema, { formats, coerce: true, unresolvedRefs: "reject" })</c> validator that
/// <c>utils/validation.ts</c> builds once per tool schema.
/// </summary>
public sealed class ToolSchemaValidator
{
    private readonly JsonObject _schema;
    private readonly SchemaResolver _resolver;
    private readonly SchemaChecker _checker;

    private ToolSchemaValidator(JsonObject schema)
    {
        _schema = schema;
        _resolver = new SchemaResolver(schema);
        _checker = new SchemaChecker(_resolver, SchemaFormats.All);
        // Index eagerly: the resolver's maps are read concurrently once the validator is shared.
        _resolver.Warm();
    }

    /// <summary>Compiles <paramref name="schema"/>; the schema is read, never modified.</summary>
    public static ToolSchemaValidator Compile(JsonObject schema) => new(schema);

    /// <summary>Whether <paramref name="value"/> is valid (no coercion).</summary>
    public bool Check(JsonNode? value) => _checker.Check(_schema, _resolver.RootBase, value);

    /// <summary>
    /// <paramref name="value"/> coerced towards the schema: numeric strings to numbers, <c>"true"</c> to
    /// <c>true</c>, a single value to a one-item array, an optional <c>null</c> dropped, the first union branch
    /// the value converts to. The input is never modified.
    /// </summary>
    public JsonNode? Coerce(JsonNode? value) => new SchemaCoercer(_checker, _resolver, _schema).Coerce(value);

    /// <summary>Why <paramref name="value"/> is invalid: one error per failing keyword, empty when it is valid.</summary>
    public IReadOnlyList<SchemaValidationError> Errors(JsonNode? value) =>
        Check(value) ? [] : new SchemaExplainer(_checker, _resolver, _schema).Errors(value);

    /// <summary>Coerces <paramref name="value"/>, then checks it: the coerced value when valid, else its errors.</summary>
    public SchemaParseResult Parse(JsonNode? value)
    {
        var coerced = Coerce(value);
        return Check(coerced) ? SchemaParseResult.Success(coerced) : SchemaParseResult.Failure(Errors(coerced));
    }
}
