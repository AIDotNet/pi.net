using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Codemode;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using ToolNamespace = Pi.CodingAgent.Core.Extensions.ToolNamespace;

namespace Pi.CodingAgent.Extensions.Codemode;

/// <summary>
/// The <c>codemode</c> tool: the model writes JavaScript that calls other tools. Port of the TS
/// <c>extensions/codemode/tool.ts</c>.
/// </summary>
/// <remarks>
/// Scripts use <c>tools</c>, <c>ALL_TOOLS</c>, <c>text()</c>, <c>image()</c>, <c>exit()</c>,
/// <c>store()</c>/<c>load()</c>, <c>console.*</c>, and <c>return &lt;value&gt;</c>, may start with a
/// <c>// @options:</c> line, and reach the model catalog, classifiers, and image models through
/// <c>models.*</c>. Results start with a "Script completed" or "Script failed" header.
/// </remarks>
public static partial class CodemodeToolApi
{
    public const string CodemodeToolName = "codemode";

    /// <summary>Custom entry type holding one script's <c>store()</c> writes.</summary>
    public const string CodemodeStoreEntryType = "codemode-store";

    private static readonly JsonSerializerOptions JsonStringifyOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One script's <c>store()</c> writes. TS <c>CodemodeStoreEntryData</c>.</summary>
    public sealed record CodemodeStoreEntryData
    {
        public required IReadOnlyDictionary<string, JsonNode?> Set { get; init; }

        public required IReadOnlyList<string> Delete { get; init; }
    }

    /// <summary>
    /// The part of the model registry that scripts reach through <c>models</c>. TS
    /// <c>CodemodeModelRuntime = Pick&lt;ModelRegistry, …&gt;</c>; C# has no structural typing, so the
    /// slice is an interface and <see cref="ModelRegistry"/> satisfies it through the adapter below
    /// (same device as C112's <c>IToolSearchTools</c>).
    /// </summary>
    public interface ICodemodeModelRuntime
    {
        IReadOnlyList<ModelSpec> GetModelsOfType(ModelType type, string? provider = null);

        Task<IReadOnlyList<ModelSpec>> GetAvailableOfTypeAsync(
            ModelType type, string? provider = null, CancellationToken signal = default);

        ModelSpec? GetModelOfType(ModelType type, string provider, string modelId);

        Task<ClassifierResult> ClassifyAsync(
            ModelSpec model, ClassifierContext context, CancellationToken signal = default);

        Task<AssistantImages> GenerateImagesAsync(
            ModelSpec model, ImagesContext context, CancellationToken signal = default);
    }

    /// <summary>Adapts the session's <see cref="ModelRegistry"/> to <see cref="ICodemodeModelRuntime"/>.</summary>
    public sealed class ModelRegistryRuntime(ModelRegistry registry) : ICodemodeModelRuntime
    {
        public IReadOnlyList<ModelSpec> GetModelsOfType(ModelType type, string? provider = null)
            => registry.GetModelsOfType(type, provider);

        public Task<IReadOnlyList<ModelSpec>> GetAvailableOfTypeAsync(
            ModelType type, string? provider = null, CancellationToken signal = default)
            => registry.GetAvailableOfTypeAsync(type, provider, signal);

        public ModelSpec? GetModelOfType(ModelType type, string provider, string modelId)
            => registry.GetModelOfType(type, provider, modelId);

        public Task<ClassifierResult> ClassifyAsync(
            ModelSpec model, ClassifierContext context, CancellationToken signal = default)
            => registry.ClassifyAsync(model, context, cancellationToken: signal);

        public Task<AssistantImages> GenerateImagesAsync(
            ModelSpec model, ImagesContext context, CancellationToken signal = default)
            => registry.GenerateImagesAsync(model, context, cancellationToken: signal);
    }

    /// <summary>TS <c>CodememodeToolOptions</c> (spelling: <c>CodemodeToolOptions</c>).</summary>
    public sealed record CodemodeToolOptions
    {
        /// <summary>Namespace of a tool, for <c>searchTools()</c> ranking and its <c>namespace</c> filter.</summary>
        public Func<string, ToolNamespace?>? GetToolNamespace { get; init; }

        /// <summary>
        /// Prompt guidelines of every tool, by tool name, shown with declarations by
        /// <c>describeTool()</c> and <c>ALL_TOOLS</c>.
        /// </summary>
        public Func<IReadOnlyDictionary<string, IReadOnlyList<string>>>? GetToolGuidelines { get; init; }

        /// <summary>
        /// Expose the <c>models</c> namespace to scripts, backed by the session's model registry.
        /// Without it, <c>models</c> is not declared.
        /// </summary>
        public bool? Models { get; init; }

        /// <summary>
        /// Persists <c>store()</c> writes as a session custom entry. Without it, writes last only for
        /// the current script; <c>load()</c> still reads entries already on the branch.
        /// </summary>
        public Action<string, CodemodeStoreEntryData>? AppendEntry { get; init; }

        /// <summary>How the tool presents the loadout while active (the <c>codemode.mode</c> setting). Default: <c>on</c>.</summary>
        public Func<string>? GetMode { get; init; }

        /// <summary>Token budget for tool declarations in the description. Default: <see cref="DefaultCodemodeInlineBudget"/>.</summary>
        public Func<long?>? GetInlineBudget { get; init; }
    }

    private static readonly ToolSchema CodemodeSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["code"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Raw JavaScript source.",
            },
        },
        ["required"] = new[] { "code" },
    });

    private static readonly ToolSchema TextOutputSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "string",
    });

    /// <summary>
    /// Whether a registered tool is this package's <c>codemode</c> tool rather than another
    /// extension's tool with the same name. Compares the parameter schema, which the definition
    /// passes through by reference (difference C113's identity device).
    /// </summary>
    public static bool IsCodemodeTool(ToolInfo tool) =>
        tool.Name == CodemodeToolName && ReferenceEquals(tool.Parameters, CodemodeSchema);

    /// <summary>TS <c>CodemodeNestedCallStatus</c>.</summary>
    public static class NestedCallStatus
    {
        public const string Running = "running";
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
    }

    /// <summary>One nested tool or model call row. TS <c>CodemodeNestedCall</c>.</summary>
    public sealed record CodemodeNestedCall
    {
        /// <summary>Tool call id of the nested call, <c>&lt;codemode call id&gt;/&lt;n&gt;</c>.</summary>
        public required string Id { get; set; }

        public required string Name { get; init; }

        /// <summary>Compact JSON of the arguments, truncated for display.</summary>
        public required string Args { get; init; }

        public string Status { get; set; } = NestedCallStatus.Running;

        public double? DurationMs { get; set; }

        /// <summary>Error text, truncated for display.</summary>
        public string? Error { get; set; }

        /// <summary>Cost in USD of a <c>models.*</c> call that reported usage.</summary>
        public double? Cost { get; set; }
    }

    /// <summary>TS <c>CodemodeToolDetails</c>.</summary>
    public sealed record CodemodeToolDetails
    {
        public required IReadOnlyList<CodemodeNestedCall> Calls { get; init; }

        /// <summary>Temp file with the full text output, when the output was truncated.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("fullOutputPath")]
        [System.Text.Json.Serialization.JsonIgnore(
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? FullOutputPath { get; init; }
    }

    /// <summary>TS <c>codemodeToolSystemPromptContribution</c>.</summary>
    public const string PromptSnippet = "Run JavaScript that calls other tools";

    public static readonly IReadOnlyList<string> PromptGuidelines =
    [
        "Use codemode to batch independent tool calls (Promise.allSettled), chain them, or filter large output, instead of many separate calls.",
    ];

    /// <summary>The reference for scripts: globals, tool results, <c>store()</c>, the <c>models</c> API, and limits.</summary>
    public static readonly string CodemodeDocsPath =
        NodePath.Join(Config.GetDocsPath(), "codemode.md");

    private const string DescriptionIntro = """
        Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence), run as an async function body in a QuickJS sandbox: top-level `await` and `return` work. No Node, file system, network, or timers.
        - `await tools.<name>({ ...args })` resolves to a string, or an object if the tool's declaration says so, and rejects with an Error on failure. Calls still running when the script ends are cancelled.
        - Optional first line: `// @options: {"max_output_tokens": 10000, "timeout_ms": 60000}`
        """;

    /// <summary>One line per global. The details live in <see cref="CodemodeDocsPath"/>.</summary>
    private static string DescribeGlobals(bool models)
    {
        var lines = new List<string>
        {
            "Globals:",
            "- `text(value)`, `image(dataUrlOrImageBlock)`, `console.log(...)`, and top-level `return` add output; `exit()` ends the script. With several text items, each starts with a `==> text N/M <==` line, and `console` lines follow the other output in one `<console_output>` block. `image()` also saves the image to a temp file and the result names its path.",
            "- `store(key, value)` and `load(key)` keep JSON values across codemode calls.",
            "- `ALL_TOOLS`, `await searchTools(query, { limit?, namespace? })`, `await describeTool(name)`, `await describeNamespace(name)`: find unlisted tools, such as MCP tools.",
        };
        if (models)
        {
            lines.Add($"- `models`: classifiers and image generation. Read {CodemodeDocsPath} first.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Default for <c>CodemodeDescriptionOptions.inlineBudget</c>, in estimated tokens.</summary>
    public const long DefaultCodemodeInlineBudget = 3000;

    /// <summary>Characters per token when estimating the cost of a tool section.</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// What a script sees of a tool: its description followed by its prompt guidelines, which the
    /// system prompt only has for declared tools. Tools without an output schema resolve to their
    /// text output. TS <c>toCodemodeDeclaration</c> (returns <c>Omit&lt;CodemodeTool, "execute"&gt;</c>;
    /// the port carries the declaration as a record and converts to a sandbox tool on demand).
    /// </summary>
    public static CodemodeDeclaration ToCodemodeDeclaration(AgentTool tool, IReadOnlyList<string>? guidelines = null)
    {
        var bullets = (guidelines ?? [])
            .Where(guideline => guideline.Trim().Length > 0)
            .Select(guideline => $"- {guideline.Trim()}")
            .ToList();
        var description = bullets.Count > 0
            ? $"{tool.Description.Trim()}\n\n{string.Join("\n", bullets)}"
            : tool.Description;
        return new CodemodeDeclaration(
            tool.Name,
            description,
            SchemaToJsonNode(tool.Parameters),
            tool.OutputSchema is not null ? SchemaToJsonNode(tool.OutputSchema) : SchemaToJsonNode(TextOutputSchema));
    }

    private static readonly CodemodeDeclaration TextOutputDeclaration =
        new("", "", null, SchemaToJsonNode(TextOutputSchema));

    private static JsonNode? DeclarationOutputSchema(CodemodeDeclaration declaration) => declaration.OutputSchema;

    /// <summary>Tools a script may call: every given tool except the codemode tool itself.</summary>
    public static IReadOnlyList<AgentTool> GetCodemodeCallableTools(IEnumerable<AgentTool> tools) =>
        [.. tools.Where(tool => tool.Name != CodemodeToolName)];

    /// <summary>TS <c>CodemodeDescriptionOptions</c>.</summary>
    public sealed record CodemodeDescriptionOptions
    {
        /// <summary>Declare the <c>models</c> namespace; only for tools created with model access.</summary>
        public bool? Models { get; init; }

        /// <summary>Namespace of each tool, by tool name. Tools of one namespace are listed under one heading.</summary>
        public IReadOnlyDictionary<string, ToolNamespace>? Namespaces { get; init; }

        /// <summary>Tools that are callable but never listed with their declaration (<c>deferred</c> exposure).</summary>
        public IReadOnlySet<string>? Deferred { get; init; }

        /// <summary>Prompt guidelines of each tool, by tool name, listed after its description.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Guidelines { get; init; }

        /// <summary>
        /// Estimated tokens (characters / 4) the tool sections may use. Tools that do not fit are
        /// left out, like deferred tools. Null lists every tool that is not deferred.
        /// </summary>
        public long? InlineBudget { get; init; }
    }

    /// <summary><c>### `id` (`raw name`)</c> followed by the tool's description and declaration.</summary>
    private static string RenderToolSection(CodemodeDeclaration declaration)
    {
        var id = CodemodeIdentifiers.ToCodemodeIdentifier(declaration.Name);
        var heading = id == declaration.Name ? $"### `{id}`" : $"### `{id}` (`{declaration.Name}`)";
        return $"{heading}\n{CodemodeDeclarations.RenderToolSample(declaration.ToSandboxTool()).Trim()}";
    }

    private sealed record CatalogEntry(string Name, string Section, double Cost);

    private sealed class CatalogGroup(ToolNamespace? ns)
    {
        public ToolNamespace? Namespace { get; } = ns;

        public List<CatalogEntry> Entries { get; } = [];
    }

    /// <summary>
    /// Pick the tool sections that fit the budget, like OpenCode's catalog: in each round every
    /// group (tools without a namespace first, then namespaces by name) places its cheapest
    /// remaining tool; a group whose next tool does not fit drops out while the others continue.
    /// Every namespace is represented before any namespace is complete. TS <c>selectCatalog</c>.
    /// </summary>
    private static HashSet<string> SelectCatalog(IEnumerable<CatalogGroup> groups, long? budget)
    {
        if (budget is null)
        {
            return [.. groups.SelectMany(group => group.Entries.Select(entry => entry.Name))];
        }

        var queues = groups
            .Select(group => new Queue<CatalogEntry>([.. group.Entries.OrderBy(entry => entry.Cost)]))
            .Where(queue => queue.Count > 0)
            .ToList();
        var shown = new HashSet<string>();
        var remaining = (double)budget.Value;
        var active = queues.Where(queue => queue.Count > 0).ToList();
        while (active.Count > 0)
        {
            var nextActive = new List<Queue<CatalogEntry>>();
            foreach (var queue in active)
            {
                var next = queue.Peek();
                if (next.Cost > remaining)
                {
                    continue;
                }

                remaining -= next.Cost;
                shown.Add(next.Name);
                queue.Dequeue();
                if (queue.Count > 0)
                {
                    nextActive.Add(queue);
                }
            }

            active = nextActive;
        }

        return shown;
    }

    /// <summary>
    /// Model-facing description: the helper list, guidance for finding tools that are not listed,
    /// the shared MCP types when listed tools need them, the <c>models</c> API, and one section per
    /// listed tool, grouped by namespace. Deferred tools are never listed and do not affect the
    /// description at all, so it stays the same while MCP servers connect or change their tools.
    /// Tool sections are limited to <c>inlineBudget</c>. TS <c>createCodemodeDescription</c>.
    /// </summary>
    public static string CreateCodemodeDescription(
        IEnumerable<AgentTool> tools, CodemodeDescriptionOptions? options = null)
    {
        options ??= new CodemodeDescriptionOptions();
        var declarations = GetCodemodeCallableTools(tools)
            .Where(tool => options.Deferred?.Contains(tool.Name) != true)
            .Select(tool => ToCodemodeDeclaration(
                tool,
                options.Guidelines is not null && options.Guidelines.TryGetValue(tool.Name, out var guidelines)
                    ? guidelines
                    : null))
            .ToList();
        var groups = new Dictionary<string, CatalogGroup>(StringComparer.Ordinal)
        {
            [""] = new CatalogGroup(null),
        };
        foreach (var declaration in declarations)
        {
            ToolNamespace? ns = null;
            options.Namespaces?.TryGetValue(declaration.Name, out ns);
            var key = ns is not null ? $"ns:{ns.Name}" : "";
            if (!groups.TryGetValue(key, out var group))
            {
                group = new CatalogGroup(ns);
                groups[key] = group;
            }

            var section = RenderToolSection(declaration);
            group.Entries.Add(
                new CatalogEntry(declaration.Name, section, Math.Ceiling((double)section.Length / CharsPerToken)));
        }

        var ordered = [.. groups.Values
            .OrderBy(group => group.Namespace is null ? 0 : 1)
            .ThenBy(group => group.Namespace?.Name, StringComparer.Ordinal)];
        var shown = SelectCatalog(ordered, options.InlineBudget);

        var sections = new List<string> { DescriptionIntro, DescribeGlobals(options.Models == true) };
        if (declarations.Any(declaration =>
                shown.Contains(declaration.Name)
                && CodemodeDeclarations.McpStructuredContentSchema(DeclarationOutputSchema(declaration)) is not null))
        {
            sections.Add($"Shared MCP Types:\n```ts\n{CodemodeDeclarations.McpTypeScriptPreamble}\n```");
        }

        if (declarations.Count == 0)
        {
            return string.Join("\n\n", sections);
        }

        var toolSections = new List<string> { "Nested tools:" };
        foreach (var group in ordered)
        {
            var visible = group.Entries.Where(entry => shown.Contains(entry.Name)).ToList();
            if (group.Namespace is { } ns)
            {
                // Only tools that did not fit the budget are counted as not listed here.
                var listing = visible.Count == group.Entries.Count
                    ? ""
                    : visible.Count == 0
                        ? " (tools not listed)"
                        : " (some tools not listed)";
                var description = ns.Description?.Trim();
                toolSections.Add($"## {ns.Name}{listing}{(string.IsNullOrEmpty(description) ? "" : $"\n{description}")}");
            }

            foreach (var entry in visible)
            {
                toolSections.Add(entry.Section);
            }
        }

        sections.Add(string.Join("\n\n", toolSections));
        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// What a script call resolves to, in one line: <c>a string</c>, the field names of an object
    /// (<c>{ output, exit_code, full_output_path? }</c>), or the rendered type for anything else.
    /// TS <c>describeOutput</c>.
    /// </summary>
    private static string DescribeOutput(JsonNode? schema)
    {
        var type = CodemodeDeclarations.RenderToolOutputType(schema);
        if (type == "string")
        {
            return "a string";
        }

        if (schema is JsonObject @object
            && @object["type"]?.GetValue<string>() == "object"
            && @object["properties"] is JsonObject properties
            && CodemodeDeclarations.McpStructuredContentSchema(schema) is null)
        {
            var required = new HashSet<string>(
                (@object["required"] as JsonArray ?? [])?.Select(node => node?.GetValue<string>())
                    .OfType<string>() ?? [],
                StringComparer.Ordinal);
            var fields = properties.Select(property =>
                required.Contains(property.Key) ? property.Key : $"{property.Key}?");
            return $"`{{ {string.Join(", ", fields)} }}`";
        }

        return $"`{Whitespace().Replace(type, " ")}`";
    }

    /// <summary>
    /// A declared tool's description followed by how scripts call it and what the call resolves to.
    /// The arguments are the tool's declared parameters, so they are not repeated.
    /// TS <c>describeScriptCall</c>.
    /// </summary>
    private static string DescribeScriptCall(AgentTool tool)
    {
        var declaration = ToCodemodeDeclaration(tool);
        return $"{tool.Description.Trim()}\n\nCodemode: `tools.{CodemodeIdentifiers.ToCodemodeIdentifier(tool.Name)}(args)` resolves to {DescribeOutput(declaration.OutputSchema)}.";
    }

    /// <summary>
    /// How the codemode tool presents tools that are both declared and callable from scripts:
    /// <c>on</c> — their descriptions say how scripts call them, and the codemode description lists
    /// only the callable tools without <c>direct</c> exposure; <c>only</c> — the codemode
    /// description lists every callable tool, and requests leave out the declarations of active
    /// <c>direct</c> tools. Listed tools carry their prompt guidelines, which the system prompt
    /// only has for declared tools. TS <c>prepareCodemodeLoadout</c>.
    /// </summary>
    private static ToolLoadoutChanges PrepareCodemodeLoadout(IToolLoadout loadout, CodemodeToolOptions options)
    {
        var mode = options.GetMode?.Invoke() ?? "on";
        bool IsDirect(AgentTool tool) => loadout.GetExposure(tool.Name) == ToolExposure.Direct;
        var callable = GetCodemodeCallableTools(loadout.Callable);
        var callableNames = new HashSet<string>(callable.Select(tool => tool.Name), StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode == "on")
        {
            foreach (var tool in loadout.Declared)
            {
                if (callableNames.Contains(tool.Name))
                {
                    descriptions[tool.Name] = DescribeScriptCall(tool);
                }
            }
        }

        var listed = mode == "only"
            ? callable
            : [.. callable.Where(tool => !IsDirect(tool))];
        var namespaces = listed
            .Select(tool => (tool.Name, ns: loadout.GetNamespace(tool.Name)))
            .Where(pair => pair.ns is not null)
            .Select(pair => new KeyValuePair<string, ToolNamespace>(pair.Name, pair.ns!))
            .ToList();
        var guidelines = listed
            .Select(tool => new KeyValuePair<string, IReadOnlyList<string>>(tool.Name, loadout.GetPromptGuidelines(tool.Name)))
            .ToList();
        descriptions[CodemodeToolName] = CreateCodemodeDescription(listed, new CodemodeDescriptionOptions
        {
            Models = options.Models == true,
            Namespaces = new Dictionary<string, ToolNamespace>(namespaces, StringComparer.Ordinal),
            Guidelines = new Dictionary<string, IReadOnlyList<string>>(guidelines, StringComparer.Ordinal),
            Deferred = new HashSet<string>(
                listed.Where(tool => loadout.GetExposure(tool.Name) == ToolExposure.Deferred)
                    .Select(tool => tool.Name),
                StringComparer.Ordinal),
            InlineBudget = options.GetInlineBudget?.Invoke() ?? DefaultCodemodeInlineBudget,
        });
        var declaredNames = new HashSet<string>(
            loadout.Declared.Select(tool => tool.Name), StringComparer.Ordinal);
        return new ToolLoadoutChanges
        {
            Descriptions = descriptions,
            HiddenDeclarations = mode == "only"
                ? [.. callable.Where(tool => IsDirect(tool) && declaredNames.Contains(tool.Name)).Select(tool => tool.Name)]
                : [],
        };
    }

    /// <summary>TS <c>createCodemodeToolDefinition(options)</c>.</summary>
    public static ToolDefinition CreateCodemodeToolDefinition(CodemodeToolOptions? options = null)
    {
        options ??= new CodemodeToolOptions();
        return new ToolDefinition
        {
            Name = CodemodeToolName,
            Label = CodemodeToolName,
            // Replaced with the declarations of the callable tools when the tool is activated.
            Description = CreateCodemodeDescription([], new CodemodeDescriptionOptions { Models = options.Models == true }),
            PromptSnippet = PromptSnippet,
            PromptGuidelines = [.. PromptGuidelines],
            Parameters = CodemodeSchema,
            // Scripts must not start other scripts.
            Exposure = ToolExposure.ModelOnly,
            PrepareLoadout = loadout => PrepareCodemodeLoadout(loadout, options),
            // Capable models write the script as raw text instead of a JSON-escaped string.
            ConstrainedSampling = new JsonObject
            {
                ["type"] = "grammar",
                ["variants"] = new JsonObject
                {
                    ["openai_lark"] = CodemodeSource.Grammar,
                },
            },
            // The sandbox (VM) loads on the first call, not at startup.
            Execute = (toolCallId, parameters, signal, onUpdate, ctx) =>
                CodemodeExecute.ExecuteCodemodeAsync(
                    toolCallId, parameters, signal, onUpdate, ctx as IExtensionToolContext, options),
        };
    }

    /// <summary>
    /// Create the codemode tool as an AgentTool. The description lists the given tools; the script
    /// can call whatever tools the agent loop provides at execution time.
    /// TS <c>createCodemodeTool</c>.
    /// </summary>
    public static AgentTool CreateCodemodeTool(
        IReadOnlyList<AgentTool>? tools = null, CodemodeToolOptions? options = null)
    {
        var definition = CreateCodemodeToolDefinition(options);
        var tool = ToolDefinitionWrapper.WrapToolDefinition(definition);
        return tool with
        {
            Description = CreateCodemodeDescription(tools ?? [], new CodemodeDescriptionOptions { Models = options?.Models == true }),
            PromptSnippet = definition.PromptSnippet,
            PromptGuidelines = definition.PromptGuidelines,
        };
    }

    /// <summary>TS <c>JSON.stringify</c>-compatible compact serialization.</summary>
    internal static string JsonStringify(JsonNode? value) => value?.ToJsonString(JsonStringifyOptions) ?? "null";

    /// <summary>The port's <c>ToolSchema</c> carries the schema as a JSON-shaped dictionary; the codemode side reads JSON nodes.</summary>
    internal static JsonNode? SchemaToJsonNode(ToolSchema schema) =>
        System.Text.Json.JsonSerializer.SerializeToNode(schema.JsonSchema);

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();
}

/// <summary>
/// A tool's declaration for scripts (TS <c>Omit&lt;CodemodeTool, "execute"&gt;</c>): name, model-facing
/// description, and the input/output JSON schemas. The port keeps this as a plain record and
/// materializes a sandbox tool (with a no-op execute) only where the declarative renderer needs one.
/// </summary>
public sealed record CodemodeDeclaration(string Name, string Description, JsonNode? InputSchema, JsonNode? OutputSchema)
{
    /// <summary>Bridge to Pi.Codemode's declarative renderer, which consumes a <see cref="CodemodeTool"/>.</summary>
    internal CodemodeTool ToSandboxTool() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Execute = static (_, _) => Task.FromResult<JsonNode?>(null),
    };
}
