using System.Text.Json.Nodes;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// Reference resolution for one compiled schema: schema resources, anchors and JSON Pointers over the root
/// schema. Port of the TS <c>schema/resolver.ts</c> (the registered-document registry is unused: tool schemas
/// are self-contained).
/// </summary>
internal sealed class SchemaResolver
{
    /// <summary>The base URI of a root schema without <c>$id</c>; relative references resolve against it.</summary>
    public const string DefaultBase = "https://json-schema.invalid/root.json";

    private static readonly string[] SchemaKeywords =
    [
        "additionalProperties", "propertyNames", "contains", "not", "if", "then", "else",
        "unevaluatedProperties", "unevaluatedItems", "additionalItems", "contentSchema", "items",
    ];

    private static readonly string[] MapKeywords =
    [
        "properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies",
    ];

    private static readonly string[] ArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems", "items"];

    private readonly JsonNode? _root;
    private readonly Dictionary<JsonObject, string> _baseOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, JsonNode?> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _anchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
    private bool _indexed;

    public SchemaResolver(JsonNode? root)
    {
        _root = root;
        RootBase = root is JsonObject schema ? BaseFor(schema, DefaultBase) : DefaultBase;
    }

    /// <summary>The base URI of the root schema.</summary>
    public string RootBase { get; }

    /// <summary>
    /// The base URI of <paramref name="node"/>, whose parent has <paramref name="base"/>: its <c>$id</c> changes
    /// it, unless the schema is already indexed (a resolved reference target keeps its own).
    /// </summary>
    public string BaseFor(JsonObject node, string @base)
    {
        if (_baseOf.TryGetValue(node, out var indexed)) return indexed;
        if (!node.TryGetPropertyValue("$id", out var idNode) || !SchemaJson.IsString(idNode)) return @base;
        var id = idNode!.GetValue<string>();
        return DocumentOf(ResolveUri(id, @base));
    }

    /// <summary>
    /// The base URI <paramref name="node"/> was indexed under, or <paramref name="base"/> while the schema is not
    /// indexed yet (the raw map the coercer reads, not <see cref="BaseFor"/>'s <c>$id</c> computation).
    /// </summary>
    public string IndexedBaseOf(JsonObject node, string @base) =>
        _baseOf.TryGetValue(node, out var indexed) ? indexed : @base;

    /// <summary>
    /// The schema a reference names, with the base URI it compiles under. A reference that names nothing
    /// resolves to the <c>false</c> schema and is remembered, so only values at that place fail
    /// (<c>unresolvedRefs: "reject"</c>, the mode <c>utils/validation.ts</c> compiles with).
    /// </summary>
    public (JsonNode? Node, string Base) Resolve(string reference, string @base)
    {
        try
        {
            return Find(reference, @base);
        }
        catch (InvalidOperationException)
        {
            _unresolved.Add(ResolveUri(reference, @base));
            return (FalseSchema, @base);
        }
    }

    /// <summary>Whether <paramref name="reference"/>, from <paramref name="base"/>, named nothing.</summary>
    public bool IsUnresolved(string reference, string @base) => _unresolved.Contains(ResolveUri(reference, @base));

    private static readonly JsonValue FalseSchema = JsonValue.Create(false);

    private (JsonNode? Node, string Base) Find(string reference, string @base)
    {
        EnsureIndexed();
        var uri = ResolveUri(reference, @base);
        var document = DocumentOf(uri);
        var fragment = uri.Length > document.Length ? DecodeUriComponent(uri[(document.Length + 1)..]) : "";
        var root = _resources.TryGetValue(document, out var known) ? known : null;
        if (root is null) throw new InvalidOperationException($"Unresolved $ref {reference} (no schema for {document})");
        if (fragment.Length == 0) return (root, document);
        if (fragment.StartsWith('/')) return Pointer(root, document, fragment);
        if (_anchors.TryGetValue($"{document}#{fragment}", out var anchor))
        {
            return (anchor, _baseOf.TryGetValue(anchor, out var anchorBase) ? anchorBase : document);
        }
        throw new InvalidOperationException($"Unresolved $ref {reference} (no anchor {fragment} in {document})");
    }

    private void EnsureIndexed()
    {
        if (_indexed) return;
        lock (_gate)
        {
            if (_indexed) return;
            _resources[DefaultBase] = _root;
            Index(_root, DefaultBase);
            _indexed = true;
        }
    }

    /// <summary>Indexes the root schema now, so a shared validator never indexes it from two threads.</summary>
    public void Warm() => EnsureIndexed();

    private readonly Lock _gate = new();

    // Walks only keywords that hold subschemas: an $id inside enum, const or an unknown keyword names nothing.
    private void Index(JsonNode? node, string @base)
    {
        if (node is not JsonObject schema || _baseOf.ContainsKey(schema)) return;
        var current = @base;
        if (schema.TryGetPropertyValue("$id", out var idNode) && SchemaJson.IsString(idNode))
        {
            var id = idNode!.GetValue<string>();
            var uri = ResolveUri(id, current);
            current = DocumentOf(uri);
            _resources[current] = schema;
            if (uri.Length > current.Length + 1) _anchors[uri] = schema;
        }
        _baseOf[schema] = current;
        foreach (var keyword in SchemaKeywords) Index(schema[keyword], current);
        foreach (var keyword in MapKeywords)
        {
            if (schema[keyword] is not JsonObject map) continue;
            foreach (var (_, value) in map) Index(value, current);
        }
        foreach (var keyword in ArrayKeywords)
        {
            if (schema[keyword] is not JsonArray list) continue;
            foreach (var value in list) Index(value, current);
        }
    }

    private (JsonNode? Node, string Base) Pointer(JsonNode? root, string @base, string fragment)
    {
        var node = root;
        var current = @base;
        foreach (var raw in fragment.Split('/').Skip(1))
        {
            var key = raw.Replace("~1", "/").Replace("~0", "~");
            node = node is JsonObject obj && obj.TryGetPropertyValue(key, out var child) ? child : null;
            if (node is null) throw new InvalidOperationException($"Unresolved $ref #{fragment} in {@base}");
            if (node is JsonObject childObject && _baseOf.TryGetValue(childObject, out var indexed)) current = indexed;
        }
        return (node, current);
    }

    /// <summary><paramref name="reference"/> resolved against <paramref name="base"/>.</summary>
    public static string ResolveUri(string reference, string @base)
    {
        // Fragment-only references are the overwhelming case for tool schemas: skip the URI machinery.
        if (reference.StartsWith('#')) return DocumentOf(@base) + reference;
        if (Uri.TryCreate(@base, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, reference, out var resolved))
        {
            return resolved.AbsoluteUri;
        }
        return reference;
    }

    /// <summary><paramref name="uri"/> without its fragment.</summary>
    public static string DocumentOf(string uri)
    {
        var hash = uri.IndexOf('#');
        return hash < 0 ? uri : uri[..hash];
    }

    private static string DecodeUriComponent(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
