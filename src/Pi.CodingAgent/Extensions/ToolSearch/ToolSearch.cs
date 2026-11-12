using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Extensions.ToolSearch;

// ============================================================================
// tool-search extension — port of extensions/tool-search/{tool.ts,index.ts} (4d-6)
// ============================================================================

/// <summary>Port of the TS <c>ToolSearchDocument</c>.</summary>
/// <param name="Name">The tool name, which the ranker weighs separately.</param>
/// <param name="Text">Description, schema descriptions, and property names.</param>
/// <param name="NamespaceText">
/// Name, description, and instructions of the tool's namespace; the empty string when the tool has no
/// namespace (TS builds it the same way, so it is never <see langword="null"/> for a built document).
/// </param>
public sealed record ToolSearchDocument(string Name, string Text, string NamespaceText = "");

/// <summary>Port of the TS <c>ToolSearchMatch</c>.</summary>
public sealed record ToolSearchMatch(string Name, double Score);

/// <summary>
/// Ranks tools for a query. Port of the TS <c>ToolRanker</c> (BM25 today; a hybrid ranker with
/// embeddings can replace it).
/// </summary>
public interface IToolRanker
{
    IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit);
}

/// <summary>
/// The slice of TS <c>Pick&lt;ExtensionAPI, "getAllTools" | "getActiveTools" | "setActiveTools"&gt;</c>
/// that <c>tool_search</c> reads. Difference C112 (the same minimal-interface device as C84): C# has
/// no structural typing, so callers pass this interface (an <see cref="IExtensionApi"/> fits through
/// <see cref="ExtensionApiToolSearchTools"/>).
/// </summary>
public interface IToolSearchTools
{
    IReadOnlyList<string> GetActiveTools();

    IReadOnlyList<ToolInfo> GetAllTools();

    void SetActiveTools(IReadOnlyList<string> toolNames);
}

/// <summary>Adapts an <see cref="IExtensionApi"/> to <see cref="IToolSearchTools"/>.</summary>
public sealed class ExtensionApiToolSearchTools(IExtensionApi api) : IToolSearchTools
{
    public IReadOnlyList<string> GetActiveTools() => api.GetActiveTools();

    public IReadOnlyList<ToolInfo> GetAllTools() => api.GetAllTools();

    public void SetActiveTools(IReadOnlyList<string> toolNames) => api.SetActiveTools(toolNames);
}

/// <summary>Port of the TS <c>ToolSearchToolOptions</c>.</summary>
public sealed record ToolSearchToolOptions
{
    /// <summary>
    /// The session's tools. <c>tool_search</c> searches the tools that are not declared to the model
    /// and activates the matches. Without it, the tool finds nothing.
    /// </summary>
    public IToolSearchTools? Tools { get; init; }
}

/// <summary>Port of the TS <c>ToolSearchResultTool</c>.</summary>
public sealed record ToolSearchResultTool(string Name, string Description);

/// <summary>Port of the TS <c>ToolSearchToolDetails</c>.</summary>
public sealed record ToolSearchToolDetails
{
    /// <summary>Tools loaded by this call.</summary>
    public required IReadOnlyList<string> Loaded { get; init; }
}

/// <summary>The <c>tool_search</c> tool. Port of <c>extensions/tool-search/tool.ts</c>.</summary>
public static partial class ToolSearch
{
    public const string ToolSearchToolName = "tool_search";

    public const int DefaultToolSearchLimit = 8;

    /// <summary>The TS <c>STOP_WORDS</c>.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "with",
    };

    /// <summary>The TS <c>createToolSearchDocument</c> description of <c>tool_search</c>.</summary>
    public const string ToolSearchDescription =
        "# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools " +
        "for the next model call.\n\nSome of the tools, such as tools of MCP servers, may not have been " +
        "provided to you upfront, and you should use this tool (`tool_search`) to search for the " +
        "required tools. For MCP tool discovery, always use `tool_search`.";

    /// <summary>The TS <c>toolSearchSchema</c> (a single shared instance, so identity guards work).</summary>
    public static readonly ToolSchema ToolSearchSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Search query for deferred tools.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = $"Maximum number of tools to return. Defaults to {DefaultToolSearchLimit}.",
            },
        },
        ["required"] = new[] { "query" },
    });

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelLowerUpper();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])")]
    private static partial Regex CamelUpperUpperLower();

    /// <summary>TS <c>/[^\p{L}\p{M}\p{N}]+/gu</c>: every script's letters and digits survive.</summary>
    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("(ches|shes|sses|xes|zes)$")]
    private static partial Regex PluralSuffix();

    [GeneratedRegex("\\r?\\n")]
    private static partial Regex LineBreak();

    /// <summary>Naive singular form, so <c>issues</c> matches <c>issue</c>. Port of the TS <c>stem</c>.</summary>
    public static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
        {
            return string.Concat(term.AsSpan(0, term.Length - 3), "y");
        }

        if (term.Length > 4 && PluralSuffix().IsMatch(term))
        {
            return term[..^2];
        }

        if (term.Length > 3 && term.EndsWith("s", StringComparison.Ordinal) && !term.EndsWith("ss", StringComparison.Ordinal))
        {
            return term[..^1];
        }

        return term;
    }

    /// <summary>
    /// Lowercase terms, split at camelCase boundaries and non-alphanumerics, without stop words.
    /// Port of the TS <c>tokenize</c>.
    /// </summary>
    /// <remarks>
    /// TS segments with <c>Intl.Segmenter</c>, whose dictionary segmentation has no .NET equivalent.
    /// The port keeps ASCII on the fast path, splits other spaced scripts into whole words, and turns
    /// runs of no-space CJK into overlapping bigrams — the standard CJK IR substitute. A one-character
    /// run stays a single term. Difference C391.
    /// </remarks>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var spaced = CamelLowerUpper().Replace(text.Normalize(System.Text.NormalizationForm.FormC), "$1 $2");
        spaced = CamelUpperUpperLower().Replace(spaced, "$1 $2");
        var lower = spaced.ToLowerInvariant();
        var terms = new List<string>();
        foreach (var word in NonAlphanumeric().Split(lower))
        {
            if (word.Length == 0)
            {
                continue;
            }

            if (ContainsNoSpaceScript(word))
            {
                terms.AddRange(SegmentNoSpaceScript(word));
                continue;
            }

            if (!StopWords.Contains(word))
            {
                // TS stems only pure ASCII words: `(/^[a-z0-9]+$/.test(word) ? stem(word) : word)`.
                terms.Add(IsAsciiWord(word) ? Stem(word) : word);
            }
        }

        return terms;
    }

    private static bool IsAsciiWord(string word)
    {
        foreach (var c in word)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Han, kana, and Hangul: scripts written without spaces between words.</summary>
    private static bool ContainsNoSpaceScript(string word)
    {
        foreach (var c in word)
        {
            if (c is (>= '\u3040' and <= '\u30ff') or (>= '\u3400' and <= '\u4dbf') or (>= '\u4e00' and <= '\u9fff')
                or (>= '\uf900' and <= '\ufaff') or (>= '\uac00' and <= '\ud7af'))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SegmentNoSpaceScript(string word)
    {
        if (word.Length == 1)
        {
            return [word];
        }

        var bigrams = new List<string>(word.Length - 1);
        for (var i = 0; i + 1 < word.Length; i++)
        {
            bigrams.Add(word.Substring(i, 2));
        }

        return bigrams;
    }

    /// <summary>The TS <c>isObject</c>: a JSON object, not null, not an array.</summary>
    private static bool TryGetObject(object? value, out IReadOnlyDictionary<string, object?> map)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnly:
                map = readOnly;
                return true;
            case IDictionary<string, object?> mutable:
                map = new Dictionary<string, object?>(mutable);
                return true;
            default:
                map = null!;
                return false;
        }
    }

    /// <summary>The TS <c>Array.isArray</c> over a JSON array, as an object sequence.</summary>
    private static IEnumerable<object?> AsArray(object? value) => value switch
    {
        string => [],
        IReadOnlyDictionary<string, object?> => [],
        System.Collections.IEnumerable items => items.Cast<object?>(),
        _ => [],
    };

    /// <summary>
    /// 上游 1.5.1 #11803：把本地 <c>$ref</c>（<c>#</c> / <c>#/$defs/Item</c>）解析到根 schema，
    /// 让藏在 <c>$defs</c> 里的属性名与描述也进入搜索文本；<c>seen</c> 防止递归/共享定义被重复展开。
    /// 端口 schema 是 dictionary 形态，故在此走 dictionary 版的 JSON Pointer（~0/~1、%XX 与
    /// <c>Declarations.ResolveSchemaRef</c> 语义一致）。畸形 escape 返回 null，交给 unknown 分支。
    /// </summary>
    private static object? ResolveLocalRef(string reference, object? root)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        var path = reference.Length <= 2 ? "" : reference[2..];
        foreach (var rawSegment in path.Split('/').Where(segment => segment.Length > 0))
        {
            string key;
            try
            {
                key = Uri.UnescapeDataString(rawSegment).Replace("~1", "/").Replace("~0", "~");
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (!TryGetObject(current, out var obj) || !obj.TryGetValue(key, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Schema descriptions and property names, recursively following local <c>$ref</c>s (#11803).</summary>
    private static void SchemaText(object? schema, object? root, List<string> parts, HashSet<object> seen)
    {
        if (!TryGetObject(schema, out var map))
        {
            return;
        }

        if (map.TryGetValue("$ref", out var refValue) && refValue is string reference)
        {
            var target = ResolveLocalRef(reference, root);
            // 对齐上游 seen.has(target)：在原始对象引用上判重（dictionary 包装可能生成副本）。
            if (target is not null && TryGetObject(target, out _) && !seen.Contains(target))
            {
                seen.Add(target);
                SchemaText(target, root, parts, seen);
            }
        }

        if (map.TryGetValue("description", out var description) && description is string text)
        {
            parts.Add(text);
        }

        if (map.TryGetValue("properties", out var properties) && TryGetObject(properties, out var props))
        {
            foreach (var (name, property) in props)
            {
                parts.Add(name);
                SchemaText(property, root, parts, seen);
            }
        }

        map.TryGetValue("items", out var items);
        SchemaText(items, root, parts, seen);

        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (map.TryGetValue(key, out var variants))
            {
                foreach (var variant in AsArray(variants))
                {
                    SchemaText(variant, root, parts, seen);
                }
            }
        }
    }

    /// <summary>
    /// Search text of a tool: the name, the name with <c>_</c> as spaces, the description, schema
    /// descriptions and property names, and the namespace with its description and instructions.
    /// Port of the TS <c>createToolSearchDocument</c>.
    /// </summary>
    public static ToolSearchDocument CreateToolSearchDocument(ToolInfo tool, ToolNamespace? ns = null) =>
        CreateToolSearchDocument(tool.Name, tool.Description, tool.Parameters, ns);

    /// <summary>
    /// The <c>Pick&lt;ToolInfo, "name" | "description" | "parameters"&gt;</c> slice as explicit
    /// arguments, so callers holding an <see cref="AgentTool"/> (the codemode nested-tool path)
    /// build the same document without faking the registry-only members.
    /// </summary>
    public static ToolSearchDocument CreateToolSearchDocument(
        string name, string description, ToolSchema parameters, ToolNamespace? ns = null)
    {
        // The ranker reads the name from Name and the namespace from NamespaceText; only the
        // description and the schema go into Text (TS `cc44fa216`).
        var parts = new List<string> { description };
        // #11803：以根 schema 本身为 root 起点，并把根加入 seen（防止 `#` 自引用递归）。
        // seed 用 parameters.JsonSchema 原对象，与 ResolveLocalRef("#") 返回的对象同引用。
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { parameters.JsonSchema };
        SchemaText(parameters.JsonSchema, parameters.JsonSchema, parts, seen);
        // TS joins the three namespace parts unconditionally, so a namespace without description or
        // instructions still yields the name plus two spaces.
        var namespaceText = ns is null ? "" : $"{ns.Name} {ns.Description ?? ""} {ns.Instructions ?? ""}";

        return new ToolSearchDocument(
            name,
            string.Join(" ", parts.Where(part => part.Trim().Length > 0)),
            namespaceText);
    }

    /// <summary>Whether <c>tool_search</c> can load a tool with this exposure.</summary>
    public static bool IsSearchable(string exposure) =>
        exposure is ToolExposure.Codemode or ToolExposure.Deferred;

    /// <summary>The TS <c>tool_search</c> prompt snippet.</summary>
    public const string PromptSnippet = "Search for tools that are not loaded yet and load the matches";

    /// <summary>
    /// Rank the searchable tools that are not active yet and activate the matches, so the next model
    /// call declares them. Port of the TS <c>searchAndLoad</c>.
    /// </summary>
    public static IReadOnlyList<ToolSearchResultTool> SearchAndLoad(IToolSearchTools tools, string query, int limit)
    {
        var active = tools.GetActiveTools();
        var candidates = tools.GetAllTools()
            .Where(tool => IsSearchable(tool.Exposure) && !active.Contains(tool.Name))
            .ToList();
        var documents = candidates.Select(tool => CreateToolSearchDocument(tool, tool.Namespace)).ToList();
        var matches = new Bm25Ranker().Rank(query, documents, limit);
        if (matches.Count > 0)
        {
            tools.SetActiveTools([.. active, .. matches.Select(match => match.Name)]);
        }

        return matches
            .Select(match => new ToolSearchResultTool(
                match.Name,
                candidates.FirstOrDefault(tool => tool.Name == match.Name)?.Description ?? ""))
            .ToList();
    }

    /// <summary>The TS <c>tool.description.trim().split(/\r?\n/)[0]</c>.</summary>
    private static string FirstLine(string description)
    {
        var trimmed = description.Trim();
        var match = LineBreak().Match(trimmed);
        return match.Success ? trimmed[..match.Index] : trimmed;
    }

    /// <summary>The TS <c>createToolSearchToolDefinition(options)</c>.</summary>
    public static ToolDefinition CreateToolSearchToolDefinition(ToolSearchToolOptions? options = null)
    {
        options ??= new ToolSearchToolOptions();
        return new ToolDefinition
        {
            Name = ToolSearchToolName,
            Label = ToolSearchToolName,
            Description = ToolSearchDescription,
            PromptSnippet = PromptSnippet,
            Parameters = ToolSearchSchema,
            // Searching is not something scripts need; it changes what the model sees.
            Exposure = ToolExposure.ModelOnly,
            // A query can match any tool that is not declared yet.
            ReachesTools = _ => ToolReach.All,
            Execute = (_, args, _, _, _) =>
            {
                var query = ToolArgs.GetString(args, "query") ?? "";
                if (query.Trim().Length == 0)
                {
                    throw new InvalidOperationException("query must not be empty");
                }

                var limitNumber = GetNumber(args, "limit");
                var max = limitNumber ?? DefaultToolSearchLimit;
                var isInteger = !double.IsNaN(max) && !double.IsInfinity(max) && Math.Floor(max) == max;
                if (!isInteger || max <= 0)
                {
                    throw new InvalidOperationException("limit must be a positive integer");
                }

                var tools = options.Tools is not null ? SearchAndLoad(options.Tools, query, (int)max) : [];
                var text = tools.Count == 0
                    ? "No matching tools found."
                    : $"Loaded {tools.Count} tool{(tools.Count == 1 ? "" : "s")}. They are available from " +
                      $"your next call:\n{string.Join("\n", tools.Select(tool => $"- {tool.Name}: {FirstLine(tool.Description)}"))}";
                return Task.FromResult(new AgentToolResult(
                    [new TextContent(text)],
                    Details: new ToolSearchToolDetails { Loaded = tools.Select(tool => tool.Name).ToList() }));
            },
        };
    }

    /// <summary>The TS <c>createToolSearchExtension()</c> factory.</summary>
    public static ExtensionFactory CreateToolSearchExtension() => pi =>
    {
        pi.RegisterTool(CreateToolSearchToolDefinition(new ToolSearchToolOptions
        {
            Tools = new ExtensionApiToolSearchTools(pi),
        }) with { DefaultActive = false });
        return Task.CompletedTask;
    };

    /// <summary>The TS <c>Number.isInteger</c>-style numeric read of an optional argument.</summary>
    private static double? GetNumber(IReadOnlyDictionary<string, object?> args, string key)
    {
        var node = ToolArgs.GetNode(args, key);
        if (node is not System.Text.Json.Nodes.JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<long>(out var l) ? l : null;
    }

    /// <summary>Okapi BM25 with the usual parameters. Ties keep document order. Port of the TS <c>Bm25Ranker</c>.</summary>
    public sealed class Bm25Ranker : IToolRanker
    {
        private readonly double _k1;
        private readonly double _b;

        public Bm25Ranker(double? k1 = null, double? b = null)
        {
            _k1 = k1 ?? 1.2;
            _b = b ?? 0.75;
        }

        /// <summary>小写化，并把标识符里不合法的字符逐个换成 <c>_</c>，让 <c>dev-radius</c> 能匹配 <c>dev_radius</c>。</summary>
        private static string NormalizeName(string name)
        {
            var trimmed = name.Trim().ToLowerInvariant();
            var chars = new char[trimmed.Length];
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                chars[i] = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '$' ? c : '_';
            }

            return new string(chars);
        }

        /// <summary>查询是否就是在点名这个工具：全名，或 <c>__</c> 之后的末段。</summary>
        private static bool IsNameQuery(string normalizedQuery, string name)
        {
            var normalized = NormalizeName(name);
            return normalized == normalizedQuery || normalized.EndsWith($"__{normalizedQuery}", StringComparison.Ordinal);
        }

        /// <summary>
        /// A query term in the name counts this many times its IDF. The text part of a term is at most
        /// <c>k1 + 1</c> (2.2) times its IDF and about 1 for a single mention, so a name match beats any
        /// number of mentions in the description.
        /// </summary>
        private const double NameWeight = 3;

        /// <summary>
        /// A query term in the namespace name, description, or instructions counts this many times its
        /// IDF. They are shared by every tool of the server, so they help find the server but not the
        /// tool in it.
        /// </summary>
        private const double NamespaceWeight = 0.5;

        /// <summary>A document split into the fields the ranker weighs differently.</summary>
        private sealed record IndexedDocument(
            string Name,
            IReadOnlySet<string> NameTerms,
            IReadOnlyList<string> LocalNameTerms,
            IReadOnlyDictionary<string, int> TextCounts,
            int TextLength,
            IReadOnlySet<string> NamespaceTerms);

        private static IndexedDocument IndexDocument(ToolSearchDocument document)
        {
            var textCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in Tokenize(document.Text))
            {
                textCounts[term] = textCounts.GetValueOrDefault(term) + 1;
            }

            var segments = document.Name.Split("__");
            var localTerms = Tokenize(segments[^1]).Distinct(StringComparer.Ordinal).ToList();
            return new IndexedDocument(
                document.Name,
                Tokenize(document.Name).ToHashSet(StringComparer.Ordinal),
                localTerms,
                textCounts,
                textCounts.Values.Sum(),
                Tokenize(document.NamespaceText).ToHashSet(StringComparer.Ordinal));
        }

        /// <summary>
        /// Okapi BM25 over the description, with the name and the namespace as separate fields
        /// (TS <c>cc44fa216</c> / <c>e033f255f</c> / <c>ae823bc0b</c>):
        /// <list type="bullet">
        /// <item>a tool that matches more of the query's terms, in any field, ranks above one that matches
        /// fewer, whatever their scores;</item>
        /// <item>a query term in the name scores more than any number of mentions in the description;</item>
        /// <item>a tool whose whole local name is in the query gets a bonus worth one more match of every
        /// query term;</item>
        /// <item>a query that is exactly a tool's name ranks that tool first, before coverage.</item>
        /// </list>
        /// The description alone is length normalized, so a long name or long namespace instructions do
        /// not lower a tool's score. Ties keep document order.
        /// </summary>
        public IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit)
        {
            var queryTerms = Tokenize(query).ToHashSet(StringComparer.Ordinal);
            if (queryTerms.Count == 0 || documents.Count == 0 || limit <= 0)
            {
                return [];
            }

            var indexed = documents.Select(IndexDocument).ToList();
            var averageLength = (double)indexed.Sum(document => document.TextLength) / indexed.Count;
            if (averageLength == 0)
            {
                averageLength = 1;
            }

            var idf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var term in queryTerms)
            {
                var frequency = indexed.Count(document =>
                    document.NameTerms.Contains(term)
                    || document.TextCounts.ContainsKey(term)
                    || document.NamespaceTerms.Contains(term));
                idf[term] = Math.Log(1 + (indexed.Count - frequency + 0.5) / (frequency + 0.5));
            }

            var fullNameBonus = idf.Values.Sum();
            var exactQuery = NormalizeName(query);
            var matches = new List<(ToolSearchMatch Match, int Covered, bool Exact)>();
            foreach (var document in indexed)
            {
                var norm = _k1 * (1 - _b + (_b * document.TextLength) / averageLength);
                double score = 0;
                var covered = 0;
                foreach (var term in queryTerms)
                {
                    var count = document.TextCounts.GetValueOrDefault(term);
                    var weight = (document.NameTerms.Contains(term) ? NameWeight : 0)
                                 + (document.NamespaceTerms.Contains(term) ? NamespaceWeight : 0)
                                 + (count * (_k1 + 1)) / (count + norm);
                    if (weight > 0)
                    {
                        covered++;
                    }

                    score += idf.GetValueOrDefault(term) * weight;
                }

                if (score == 0)
                {
                    continue;
                }

                var local = document.LocalNameTerms;
                if (local.Count > 0 && local.All(queryTerms.Contains))
                {
                    score += fullNameBonus;
                }

                matches.Add((
                    new ToolSearchMatch(document.Name, score),
                    covered,
                    IsNameQuery(exactQuery, document.Name)));
            }

            // 点名查询优先，其次命中词数，最后分数。JS Array.prototype.sort 稳定（ES2019）；LINQ 的
            // OrderBy 同样稳定，所以同分保持文档顺序。
            return matches
                .OrderByDescending(entry => entry.Exact)
                .ThenByDescending(entry => entry.Covered)
                .ThenByDescending(entry => entry.Match.Score)
                .Take(limit)
                .Select(entry => entry.Match)
                .ToList();
        }
    }
}
