using System.Globalization;
using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>
/// A model plus the thinking level its pattern explicitly asked for.
/// Port of the TS <c>ScopedModel</c> (core/model-resolver.ts).
/// </summary>
public sealed record ScopedModel
{
    public required ModelSpec Model { get; init; }

    /// <summary>
    /// Set when the pattern carried an explicit level (e.g. <c>"model:high"</c>); null otherwise, so the
    /// caller applies its own default.
    /// </summary>
    public ThinkingLevel? ThinkingLevel { get; init; }
}

/// <summary>Port of the TS <c>ParsedModelResult</c>.</summary>
public sealed record ParsedModelResult
{
    public ModelSpec? Model { get; init; }

    /// <summary>Set only when the pattern carried an explicit level, like <see cref="ScopedModel.ThinkingLevel"/>.</summary>
    public ThinkingLevel? ThinkingLevel { get; init; }

    public string? Warning { get; init; }
}

/// <summary>
/// Model reference resolution. Candidates are collected before a provider is chosen, so catalog order
/// never decides the provider and an ambiguous match is reported instead of falling through to a weaker
/// one. Port of the TS <c>ModelReferenceRequest</c> / <c>ModelReferenceResult</c> /
/// <c>ModelReferenceTarget</c> (upstream <c>d7f7e0dfe</c> / #10160).
/// </summary>
public sealed record ModelReferenceRequest
{
    /// <summary>Bare model id or <c>provider/id</c>, optionally followed by <c>:&lt;thinking&gt;</c>.</summary>
    public required string Text { get; init; }

    /// <summary>Only consider models from this provider.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// A trailing <c>:&lt;suffix&gt;</c> is only considered when the full text matches no model, since ids such
    /// as OpenRouter's <c>qwen/qwen3-coder:exacto</c> contain colons.
    /// </summary>
    public ModelReferenceThinkingSuffix ThinkingSuffix { get; init; }

    /// <summary>Match id and name substrings when no exact match exists.</summary>
    public bool Fuzzy { get; init; }
}

/// <summary>Port of TS's <c>thinkingSuffix</c> literal union.</summary>
public enum ModelReferenceThinkingSuffix
{
    /// <summary>The suffix stays part of the id (<c>none</c>).</summary>
    None,

    /// <summary>A valid thinking level is parsed; any other suffix stays part of the id (<c>strict</c>).</summary>
    Strict,

    /// <summary>Like <see cref="Strict"/>, but an invalid suffix is dropped with a warning (<c>lenient</c>).</summary>
    Lenient,
}

/// <summary>Port of the TS <c>ModelReferenceResult</c> union.</summary>
public abstract record ModelReferenceResult
{
    private ModelReferenceResult() { }

    public sealed record Resolved(ModelSpec Model, ThinkingLevel? ThinkingLevel, string? Warning)
        : ModelReferenceResult;

    public sealed record Ambiguous(
        IReadOnlyList<ModelSpec> Candidates,
        int UsableCount,
        ThinkingLevel? ThinkingLevel,
        string? Warning) : ModelReferenceResult;

    public sealed record NotFound(ModelReferenceTarget? Target) : ModelReferenceResult;
}

/// <summary>Port of the TS <c>ModelReferenceTarget</c>: what a custom fallback model is built from.</summary>
public sealed record ModelReferenceTarget
{
    public required string Provider { get; init; }

    /// <summary>The provider's models, anchor first. Never empty.</summary>
    public required IReadOnlyList<ModelSpec> ProviderModels { get; init; }

    /// <summary>Text after the provider prefix as typed, including any <c>:&lt;suffix&gt;</c>.</summary>
    public required string ModelText { get; init; }

    /// <summary><see cref="ModelText"/> without a valid thinking suffix.</summary>
    public required string ModelId { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }
}

/// <summary>
/// One scoping warning. Port of the TS <c>ModelScopeDiagnostic</c>, whose <c>type</c> / <c>code</c> are
/// string-literal unions; the port keeps them as strings with the literal values named below (the
/// convention already used by <see cref="VirtualModels.RouteReasons"/>).
/// </summary>
public sealed record ModelScopeDiagnostic
{
    /// <summary>The only value TS's <c>type</c> union admits.</summary>
    public static class Types
    {
        public const string Warning = "warning";
    }

    /// <summary>The values TS's <c>code</c> union admits.</summary>
    public static class Codes
    {
        /// <summary>No model matched the pattern.</summary>
        public const string NoMatch = "no-match";

        /// <summary>A model matched, but the pattern's <c>:level</c> suffix was not a thinking level.</summary>
        public const string InvalidThinkingLevel = "invalid-thinking-level";

        /// <summary>The pattern matched models from several providers; <c>provider/id</c> chooses one.</summary>
        public const string Ambiguous = "ambiguous";
    }

    /// <summary>Always <see cref="Types.Warning"/>.</summary>
    public required string Type { get; init; }

    /// <summary>One of <see cref="Codes"/>.</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>The pattern as the user wrote it, including any glob characters.</summary>
    public required string Pattern { get; init; }
}

/// <summary>Port of the TS <c>ResolveModelScopeResult</c>.</summary>
public sealed record ResolveModelScopeResult
{
    public required IReadOnlyList<ScopedModel> ScopedModels { get; init; }

    public required IReadOnlyList<ModelScopeDiagnostic> Diagnostics { get; init; }
}

/// <summary>Port of the TS inline options object for <c>resolveCliModel</c>.</summary>
public sealed record ResolveCliModelOptions
{
    public string? CliProvider { get; init; }

    public string? CliModel { get; init; }

    public ThinkingLevel? CliThinking { get; init; }

    /// <summary><c>--api-key</c> was supplied for the selected provider.</summary>
    public bool InlineApiKey { get; init; }

    public required IModelResolverRuntime Runtime { get; init; }
}

/// <summary>Port of the TS <c>ResolveCliModelResult</c>.</summary>
public sealed record ResolveCliModelResult
{
    public ModelSpec? Model { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public string? Warning { get; init; }

    /// <summary>
    /// Message suitable for CLI display. When set, <see cref="Model"/> is null — except in
    /// <see cref="ModelResolver.ResolveCliModel"/>, which never sets both.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>Port of the TS inline options object for <c>findInitialModel</c>.</summary>
public sealed record InitialModelOptions
{
    public required IReadOnlyList<ScopedModel> ScopedModels { get; init; }

    public bool IsContinuing { get; init; }

    public string? DefaultProvider { get; init; }

    public string? DefaultModelId { get; init; }

    public ThinkingLevel? DefaultThinkingLevel { get; init; }

    /// <summary>Per-model overrides keyed <c>"provider/modelId"</c>. TS <c>Record&lt;string, ThinkingLevel&gt;</c>.</summary>
    public IReadOnlyDictionary<string, ThinkingLevel>? ModelThinkingLevels { get; init; }

    public required IModelResolverRuntime Runtime { get; init; }
}

/// <summary>Port of the TS <c>InitialModelResult</c>.</summary>
public sealed record InitialModelResult
{
    public ModelSpec? Model { get; init; }

    /// <summary>Never null — TS initializes it to <c>DEFAULT_THINKING_LEVEL</c> on every path.</summary>
    public required ThinkingLevel ThinkingLevel { get; init; }

    public string? FallbackMessage { get; init; }
}

/// <summary>Port of the TS <c>restoreModelFromSession</c> return type.</summary>
public sealed record RestoreModelResult
{
    public ModelSpec? Model { get; init; }

    public string? FallbackMessage { get; init; }
}

/// <summary>
/// The slice of <see cref="ModelRuntime"/> this module reads. TS takes the concrete class; C# names the
/// slice so the differential corpus can supply a fixture catalog instead of a real runtime, whose catalog
/// always also holds the builtin providers (difference C81).
/// </summary>
public interface IModelResolverRuntime
{
    /// <summary>Every known model, not only the authenticated ones (TS <c>getModels()</c>).</summary>
    IReadOnlyList<ModelSpec> GetModels();

    /// <summary>One model by provider and id, or null (TS <c>getModel()</c>).</summary>
    ModelSpec? GetModel(string providerId, string modelId);

    /// <summary>Whether the provider has usable credentials (TS <c>hasConfiguredAuth()</c>).</summary>
    bool HasConfiguredAuth(string providerId);

    /// <summary>The authenticated models known so far (TS <c>getAvailableSnapshot()</c>).</summary>
    IReadOnlyList<ModelSpec> GetAvailableSnapshot();

    /// <summary>Refreshes and returns the authenticated models (TS <c>getAvailable()</c>).</summary>
    Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(string? providerId, CancellationToken signal = default);
}

/// <summary>
/// Model resolution, scoping, and initial selection. Port of <c>core/model-resolver.ts</c> (783 lines).
/// </summary>
/// <remarks>
/// <para>
/// Differences recorded against the TS original:
/// <list type="bullet">
/// <item>C74：<c>defaultModelPerProvider</c> 用有序 <see cref="KeyValuePair{TKey,TValue}"/> 列表承载
/// （TS 是对象字面量，<c>Object.keys</c> 的顺序就是插入顺序，而两处优先序遍历依赖它）。查找另走
/// <see cref="DefaultModelIds"/>，等价于 TS 的索引访问。</item>
/// <item>C75：<c>Model&lt;Api&gt;</c> → <see cref="ModelSpec"/>；<c>Model&lt;Api&gt; | undefined</c> →
/// <c>ModelSpec?</c>。TS 的 <c>{...baseModel, id, name}</c> 展开变成 <c>with</c> 表达式。</item>
/// <item>C76：<c>AuthOperationOptions</c> 展开为末位 <see cref="CancellationToken"/>（同 C65）。</item>
/// <item>C77：<c>isValidThinkingLevel</c> 就地复用 <see cref="ThinkingLevels.Parse"/>
/// （其字符串集与 <c>cli/args.ts</c> 的 <c>VALID_THINKING_LEVELS</c> 一致，含 <c>off</c>）。</item>
/// <item>C78（1.4.0 起退休）：<c>findInitialModel</c> 的 <c>console.error</c> + <c>process.exit(1)</c> 一
/// 并消失——CLI 参数解析已迁到 <c>resolveCliModel</c> 与调用方，该函数不再抛错，本地不再需要
/// <c>ModelResolutionException</c>。</item>
/// <item>C79：<c>toLowerCase()</c> 走 <see cref="JsString.ToLowerCase"/>（JS 的大小写映射，与
/// <c>ToLowerInvariant</c> 在个别码点上不同）；<c>localeCompare()</c> 走
/// <see cref="Collate"/>（Node 与 .NET 都用 ICU，与 <c>Pi.Tui.Autocomplete</c> 的既有结论一致）。</item>
/// <item>C80：诊断的 <c>type</c> / <c>code</c> 字符串字面量联合保留为 <c>string</c> 加常量类，见
/// <see cref="ModelScopeDiagnostic"/>。</item>
/// <item>C81：运行时入参的类型由 <see cref="ModelRuntime"/> 收窄为 <see cref="IModelResolverRuntime"/>
/// （本模块实际读到的五个成员）。TS 靠结构类型接受任何鸭子对象，C# 需要具名；<see cref="ModelRuntime"/>
/// 隐式实现它，故生产调用点不变。</item>
/// </list>
/// </para>
/// </remarks>
public static class ModelResolver
{
    /// <summary>
    /// Default chat model IDs for providers with built-in chat models. Port of the TS
    /// <c>defaultModelPerProvider</c>.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing: <see cref="FindInitialModelAsync"/> and
    /// <see cref="RestoreModelFromSessionAsync"/> walk it in this order and take the first provider that
    /// has the model available, so reordering changes which model a fresh install picks.
    /// </remarks>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> DefaultModelPerProvider =
    [
        new("amazon-bedrock", "us.anthropic.claude-opus-4-6-v1"),
        new("ant-ling", "Ring-2.6-1T"),
        new("anthropic", "claude-opus-4-8"),
        new("openai", "gpt-5.5"),
        new("azure", "gpt-5.4"),
        new("openai-codex", "gpt-6.1-sol"),
        new("radius", "balanced"),
        new("nvidia", "nvidia/nemotron-3.5-super-vl-120b-a12b"),
        new("deepseek", "deepseek-v4-pro"),
        new("google", "gemini-3.1-pro-preview"),
        new("google-vertex", "gemini-3.1-pro-preview"),
        new("github-copilot", "gpt-6.1-sol"),
        new("openrouter", "moonshotai/kimi-k2.6"),
        new("vercel-ai-gateway", "zai/glm-5.1"),
        new("xai", "grok-4.7"),
        new("groq", "openai/gpt-oss-120b"),
        new("cerebras", "gpt-oss-120b"),
        new("zai", "glm-5.3"),
        new("zai-coding-cn", "glm-5.3"),
        new("mistral", "devstral-medium-latest"),
        new("minimax", "MiniMax-M2.7"),
        new("minimax-cn", "MiniMax-M2.7"),
        new("moonshotai", "kimi-k2.6"),
        new("moonshotai-cn", "kimi-k2.6"),
        new("huggingface", "moonshotai/Kimi-K2.6"),
        new("fireworks", "accounts/fireworks/models/kimi-k3"),
        new("together", "moonshotai/Kimi-K3"),
        new("baseten", "zai-org/GLM-5.2"),
        new("opencode", "kimi-k2.6"),
        new("opencode-go", "kimi-k3"),
        new("kimi-coding", "kimi-for-coding"),
        new("meta", "muse-spark-1.3"),
        new("cloudflare-workers-ai", "@cf/moonshotai/kimi-k2.6"),
        new("cloudflare-ai-gateway", "workers-ai/@cf/moonshotai/kimi-k2.6"),
        new("qwen-token-plan", "qwen3.7-max"),
        new("qwen-token-plan-cn", "qwen3.7-max"),
        new("qwen-token-plan-individual", "qwen3.8-max"),
        new("xiaomi", "mimo-v2.6-pro"),
        new("xiaomi-token-plan-cn", "mimo-v2.6-pro"),
        new("xiaomi-token-plan-ams", "mimo-v2.6-pro"),
        new("xiaomi-token-plan-sgp", "mimo-v2.6-pro"),
    ];

    private static readonly Dictionary<string, string> DefaultModelIds =
        DefaultModelPerProvider.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Trailing date suffix <c>-YYYYMMDD</c>. TS writes <c>\d</c>, which in .NET would also match
    /// non-ASCII digits, so the port spells out <c>[0-9]</c>.</summary>
    private static readonly Regex DatedSuffix = new(@"-[0-9]{8}$", RegexOptions.CultureInvariant);

    /// <summary>The default chat model id for a provider, or null when the provider has none.</summary>
    public static string? GetDefaultModelId(string providerId) => DefaultModelIds.GetValueOrDefault(providerId);

    /// <summary>Whether the provider appears in <see cref="DefaultModelPerProvider"/> (TS <c>id in table</c>).</summary>
    public static bool HasDefaultModelProvider(string providerId) => DefaultModelIds.ContainsKey(providerId);

    /// <summary>
    /// Where <c>console.log</c> / <c>console.warn</c> lines go. Defaults to <see cref="Console.Out"/>;
    /// tests substitute a writer to observe the line without touching the process (convention C3).
    /// </summary>
    internal static TextWriter? OutWriterOverride { get; set; }

    /// <summary>Where <c>console.error</c> lines go. Defaults to <see cref="Console.Error"/>.</summary>
    internal static TextWriter? ErrorWriterOverride { get; set; }

    internal static TextWriter OutWriter => OutWriterOverride ?? Console.Out;

    internal static TextWriter ErrorWriter => ErrorWriterOverride ?? Console.Error;

    // ================================================================= exact / fuzzy matching

    /// <summary>
    /// Helper to check if a model ID looks like an alias (no date suffix). Dates are typically in the
    /// format <c>-20241022</c> or <c>-20250929</c>. Corresponds to the TS <c>isAlias</c>.
    /// </summary>
    private static bool IsAlias(string id)
    {
        if (id.EndsWith("-latest", StringComparison.Ordinal)) return true;

        // Check if ID ends with a date pattern (-YYYYMMDD)
        return !DatedSuffix.IsMatch(id);
    }

    // ================================================================= reference resolution

    /// <summary>
    /// Resolve one reference. <paramref name="usable"/> only breaks ties between providers, so a unique
    /// match resolves even without credentials (<c>--api-key</c> first-time setup).
    /// </summary>
    public static ModelReferenceResult ResolveModelReference(
        ModelReferenceRequest request,
        IReadOnlyList<ModelSpec> models,
        Func<string, bool> usable)
    {
        var text = JsString.Trim(request.Text);
        if (text.Length == 0) return new ModelReferenceResult.NotFound(null);
        var constraint = request.Provider is null ? null : JsString.ToLowerCase(request.Provider);
        var pool = constraint is null
            ? models
            : models.Where(model => JsString.ToLowerCase(model.Provider) == constraint).ToList();
        var result = ResolveText(text, request, pool, usable);
        if (result is not ModelReferenceResult.NotFound) return result;

        var split = SplitProviderPrefix(text, pool);
        var anchor = constraint is not null
            ? pool.FirstOrDefault()
            : pool.FirstOrDefault(model => model.Provider == split?.Provider);
        if (anchor is null) return result;
        var modelText = split?.ModelText ?? text;
        var suffix = request.ThinkingSuffix == ModelReferenceThinkingSuffix.None
            ? null
            : SplitThinkingSuffix(modelText);
        return new ModelReferenceResult.NotFound(new ModelReferenceTarget
        {
            Provider = anchor.Provider,
            ProviderModels =
            [
                anchor,
                .. pool.Where(model => model.Provider == anchor.Provider && !ReferenceEquals(model, anchor)),
            ],
            ModelText = modelText,
            ModelId = suffix?.Base ?? modelText,
            ThinkingLevel = suffix?.Level,
        });
    }

    /// <summary>Exact <c>provider/id</c> or bare id lookup without thinking suffixes or fuzzy matching.</summary>
    public static ModelSpec? FindExactModelReferenceMatch(
        string modelReference, IReadOnlyList<ModelSpec> availableModels)
    {
        var result = ResolveModelReference(
            new ModelReferenceRequest
            {
                Text = modelReference,
                ThinkingSuffix = ModelReferenceThinkingSuffix.None,
                Fuzzy = false,
            },
            availableModels,
            _ => true);
        return result is ModelReferenceResult.Resolved resolved ? resolved.Model : null;
    }

    private static ModelReferenceResult ResolveText(
        string text,
        ModelReferenceRequest request,
        IReadOnlyList<ModelSpec> pool,
        Func<string, bool> usable)
    {
        if (SelectReference(text, pool, request.Fuzzy, usable) is { } reference) return reference;

        if (request.Fuzzy)
        {
            var fuzzy = SelectFuzzy(FuzzyMatches(text, pool), usable);
            if (fuzzy is not null) return fuzzy;
        }

        if (request.ThinkingSuffix == ModelReferenceThinkingSuffix.None)
            return new ModelReferenceResult.NotFound(null);
        if (SplitThinkingSuffix(text) is { } thinking)
        {
            var inner = ResolveText(thinking.Base, request, pool, usable);
            if (inner is ModelReferenceResult.NotFound) return inner;
            // Only use this thinking level if the inner recursion produced no warning.
            return WithThinkingLevel(
                inner, inner is ModelReferenceResult.Resolved { Warning: not null } ? null : thinking.Level);
        }

        // Keep an invalid suffix as part of the id and fail, rather than resolve a different model.
        if (request.ThinkingSuffix == ModelReferenceThinkingSuffix.Strict)
            return new ModelReferenceResult.NotFound(null);
        var colonIndex = JsString.LastIndexOf(text, ":");
        if (colonIndex == -1) return new ModelReferenceResult.NotFound(null);
        var suffixText = text[(colonIndex + 1)..];
        var fallback = ResolveText(text[..colonIndex], request, pool, usable);
        if (fallback is ModelReferenceResult.NotFound) return fallback;
        return WithWarning(
            WithThinkingLevel(fallback, null),
            $"Invalid thinking level \"{suffixText}\" in pattern \"{text}\". Using default instead.");
    }

    /// <summary>Attach a warning to a resolved or ambiguous result, keeping its other fields.</summary>
    private static ModelReferenceResult WithWarning(ModelReferenceResult result, string warning)
        => result switch
        {
            ModelReferenceResult.Resolved resolved => resolved with { Warning = warning },
            ModelReferenceResult.Ambiguous ambiguous => ambiguous with { Warning = warning },
            _ => result,
        };

    /// <summary>Replace a resolved or ambiguous result's thinking level, keeping its other fields.</summary>
    private static ModelReferenceResult WithThinkingLevel(ModelReferenceResult result, ThinkingLevel? level)
        => result switch
        {
            ModelReferenceResult.Resolved resolved => resolved with { ThinkingLevel = level },
            ModelReferenceResult.Ambiguous ambiguous => ambiguous with { ThinkingLevel = level },
            _ => result,
        };

    /// <summary>
    /// <c>provider/rest</c> is only a hint: the whole text may also be a model id on another provider, such
    /// as gateway id <c>zai/glm-5</c>. The named provider wins when usable, since that is what the syntax
    /// usually means; otherwise a sole usable raw match wins, since the named provider could not serve the
    /// request. An unusable named match is still preferred over unusable raw matches, so <c>--api-key</c>
    /// can set up the named provider.
    /// </summary>
    private static ModelReferenceResult? SelectReference(
        string text, IReadOnlyList<ModelSpec> pool, bool fuzzy, Func<string, bool> usable)
    {
        var lowerText = JsString.ToLowerCase(text);
        var raw = pool.Where(model => JsString.ToLowerCase(model.Id) == lowerText).ToList();
        var split = SplitProviderPrefix(text, pool);
        ModelSpec? named = null;
        if (split is { } prefix)
        {
            var providerModels = pool.Where(model => model.Provider == prefix.Provider).ToList();
            var lowerModelText = JsString.ToLowerCase(prefix.ModelText);
            // The provider's own literal id (OpenRouter's `openrouter/free`) beats its fuzzy matches.
            named = providerModels.FirstOrDefault(model => JsString.ToLowerCase(model.Id) == lowerModelText)
                ?? raw.FirstOrDefault(model => model.Provider == prefix.Provider)
                ?? (fuzzy ? BestFuzzyMatch(FuzzyMatches(prefix.ModelText, providerModels)) : null);
        }

        var others = raw.Where(model => !ReferenceEquals(model, named)).ToList();
        List<ModelSpec> candidates = [.. others];
        // The named provider leads the candidate list, mirroring `[named, ...others]` upstream.
        if (named is not null) candidates.Insert(0, named);
        if (candidates.Count == 0) return null;
        if (candidates.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() == 1)
            return new ModelReferenceResult.Resolved(candidates[0], null, null);
        if (named is not null && usable(named.Provider))
            return new ModelReferenceResult.Resolved(named, null, null);
        var usableOthers = others.Where(model => usable(model.Provider)).ToList();
        if (usableOthers.Count == 1) return new ModelReferenceResult.Resolved(usableOthers[0], null, null);
        if (usableOthers.Count == 0 && named is not null)
            return new ModelReferenceResult.Resolved(named, null, null);
        return new ModelReferenceResult.Ambiguous(candidates, usableOthers.Count, null, null);
    }

    private static List<ModelSpec> FuzzyMatches(string text, IReadOnlyList<ModelSpec> models)
    {
        var lowerText = JsString.ToLowerCase(text);
        return models
            .Where(model => JsString.ToLowerCase(model.Id).Contains(lowerText, StringComparison.Ordinal)
                || (model.Name is { } name
                    && JsString.ToLowerCase(name).Contains(lowerText, StringComparison.Ordinal)))
            .ToList();
    }

    private static ModelReferenceResult? SelectFuzzy(List<ModelSpec> matches, Func<string, bool> usable)
    {
        if (matches.Count == 0) return null;
        var multipleProviders = matches.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() > 1;
        var usableMatches = multipleProviders ? matches.Where(model => usable(model.Provider)).ToList() : [];
        var pool = usableMatches.Count > 0 ? usableMatches : matches;
        var best = BestFuzzyMatch(pool);
        if (best is null) return null;
        // The same id on several providers would otherwise be decided by catalog order.
        var tied = pool.Where(model => model.Id == best.Id).ToList();
        if (tied.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() == 1)
            return new ModelReferenceResult.Resolved(best, null, null);
        return new ModelReferenceResult.Ambiguous(tied, usableMatches.Count > 0 ? tied.Count : 0, null, null);
    }

    /// <summary>
    /// Prefer aliases (<c>claude-sonnet-4-5</c>) over dated versions
    /// (<c>claude-sonnet-4-5-20250929</c>), then the latest.
    /// </summary>
    private static ModelSpec? BestFuzzyMatch(IReadOnlyList<ModelSpec> matches)
    {
        if (matches.Count == 0) return null;
        var aliases = matches.Where(model => IsAlias(model.Id)).ToList();
        var pool = aliases.Count > 0 ? aliases : matches;
        return pool.OrderBy(model => model, Comparer<ModelSpec>.Create((a, b) => Collate(b.Id, a.Id))).First();
    }

    /// <summary>Split a trailing valid <c>:&lt;thinking&gt;</c> off <paramref name="text"/>.</summary>
    private static (string Base, ThinkingLevel Level)? SplitThinkingSuffix(string text)
    {
        var colonIndex = JsString.LastIndexOf(text, ":");
        if (colonIndex == -1) return null;
        var suffix = text[(colonIndex + 1)..];
        return ThinkingLevels.Parse(suffix) is { } level ? (text[..colonIndex], level) : null;
    }

    /// <summary>
    /// Split a leading <c>provider/</c> off <paramref name="text"/> when it names a provider of the pool.
    /// </summary>
    private static (string Provider, string ModelText)? SplitProviderPrefix(
        string text, IReadOnlyList<ModelSpec> models)
    {
        var slashIndex = text.IndexOf('/');
        if (slashIndex == -1) return null;
        var prefix = JsString.Trim(text[..slashIndex]);
        var modelText = JsString.Trim(text[(slashIndex + 1)..]);
        if (prefix.Length == 0 || modelText.Length == 0) return null;
        prefix = JsString.ToLowerCase(prefix);
        var provider = models.FirstOrDefault(model => JsString.ToLowerCase(model.Provider) == prefix)?.Provider;
        return provider is null ? null : (provider, modelText);
    }

    /// <summary>
    /// Build a synthetic model by re-labelling a real model of the provider. Corresponds to the TS
    /// <c>buildFallbackModel</c>.
    /// </summary>
    private static ModelSpec? BuildFallbackModel(ModelReferenceTarget target, string modelId)
    {
        if (target.ProviderModels.Count == 0) return null;
        var defaultId = GetDefaultModelId(target.Provider);
        var baseModel = defaultId is not null
            ? target.ProviderModels.FirstOrDefault(m => m.Id == defaultId) ?? target.ProviderModels[0]
            : target.ProviderModels[0];
        return baseModel with { Id = modelId, Name = modelId };
    }

    // ================================================================= pattern parsing

    /// <summary>
    /// Parse a pattern to extract model and thinking level. Handles models with colons in their IDs
    /// (e.g. OpenRouter's <c>:exacto</c> suffix).
    /// </summary>
    /// <remarks>
    /// Port-only wrapper: upstream <c>d7f7e0dfe</c> deleted <c>parseModelPattern</c> in favour of
    /// <see cref="ResolveModelReference"/>, but the port keeps it for the corpus surface and for callers that
    /// only want "one model or nothing". An ambiguous match resolves to no model — that is the point of the
    /// rewrite: catalog order must not pick a provider.
    /// </remarks>
    /// <param name="allowInvalidThinkingLevelFallback">
    /// TS <c>options.allowInvalidThinkingLevelFallback</c>, which itself defaults to true. In strict mode
    /// (<c>false</c>, used by CLI <c>--model</c> parsing) an invalid suffix is treated as part of the model
    /// id and the whole pattern fails, so a typo cannot silently resolve to a different model.
    /// </param>
    public static ParsedModelResult ParseModelPattern(
        string pattern,
        IReadOnlyList<ModelSpec> availableModels,
        bool allowInvalidThinkingLevelFallback = true)
    {
        var result = ResolveModelReference(
            new ModelReferenceRequest
            {
                Text = pattern,
                ThinkingSuffix = allowInvalidThinkingLevelFallback
                    ? ModelReferenceThinkingSuffix.Lenient
                    : ModelReferenceThinkingSuffix.Strict,
                Fuzzy = true,
            },
            availableModels,
            _ => true);
        return result switch
        {
            ModelReferenceResult.Resolved resolved => new ParsedModelResult
            {
                Model = resolved.Model,
                ThinkingLevel = resolved.ThinkingLevel,
                Warning = resolved.Warning,
            },
            ModelReferenceResult.Ambiguous ambiguous => new ParsedModelResult
            {
                Model = null,
                ThinkingLevel = ambiguous.ThinkingLevel,
                Warning = ambiguous.Warning,
            },
            _ => new ParsedModelResult(),
        };
    }

    // ================================================================= scope resolution

    /// <summary>
    /// Resolve model patterns to actual <see cref="ModelSpec"/> objects with optional thinking levels.
    /// Format: <c>"pattern:level"</c> where <c>:level</c> is optional.
    /// </summary>
    /// <remarks>
    /// Every model in <paramref name="models"/> is usable, so credentials cannot choose between providers
    /// offering the same id. All of them are added, with a warning so the user can pick one with
    /// <c>provider/id</c>.
    /// </remarks>
    public static ResolveModelScopeResult ResolveModelScopeFromModels(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ModelSpec> models)
    {
        var availableModels = models.ToList();
        var scopedModels = new List<ScopedModel>();
        var diagnostics = new List<ModelScopeDiagnostic>();

        void Add(ModelSpec model, ThinkingLevel? thinkingLevel)
        {
            if (!scopedModels.Any(scoped => ModelOperations.ModelsAreEqual(scoped.Model, model)))
            {
                scopedModels.Add(new ScopedModel { Model = model, ThinkingLevel = thinkingLevel });
            }
        }

        void NoMatch(string pattern) => diagnostics.Add(new ModelScopeDiagnostic
        {
            Type = ModelScopeDiagnostic.Types.Warning,
            Code = ModelScopeDiagnostic.Codes.NoMatch,
            Message = $"No models match pattern \"{pattern}\"",
            Pattern = pattern,
        });

        void Ambiguous(string pattern, IReadOnlyList<ModelSpec> candidates)
        {
            var providers = string.Join(
                ", ",
                candidates
                    .Select(model => model.Provider)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, Comparer<string>.Create(Collate)));
            diagnostics.Add(new ModelScopeDiagnostic
            {
                Type = ModelScopeDiagnostic.Types.Warning,
                Code = ModelScopeDiagnostic.Codes.Ambiguous,
                Message = $"Pattern \"{pattern}\" matches several providers: {providers}."
                    + " Use provider/id to choose one.",
                Pattern = pattern,
            });
        }

        foreach (var pattern in patterns)
        {
            // Check if pattern contains glob characters
            if (pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('['))
            {
                // Extract optional thinking level suffix (e.g., "provider/*:high")
                var thinking = SplitThinkingSuffix(pattern);
                var globPattern = thinking?.Base ?? pattern;
                var thinkingLevel = thinking?.Level;

                // Ids such as `bracketed-model[1m]` are not globs
                var exact = ResolveModelReference(
                    new ModelReferenceRequest
                    {
                        Text = globPattern,
                        ThinkingSuffix = ModelReferenceThinkingSuffix.None,
                        Fuzzy = false,
                    },
                    availableModels,
                    _ => true);
                IReadOnlyList<ModelSpec> globMatches = [];
                if (exact is ModelReferenceResult.Resolved resolvedGlob)
                {
                    globMatches = [resolvedGlob.Model];
                }
                else if (exact is ModelReferenceResult.Ambiguous ambiguousGlob)
                {
                    globMatches = ambiguousGlob.Candidates;
                    Ambiguous(pattern, globMatches);
                }
                else
                {
                    // Match against "provider/modelId" format OR just model ID. This allows "*sonnet*" to
                    // match without requiring "anthropic/*sonnet*".
                    globMatches = availableModels
                        .Where(m => Glob.Match($"{m.Provider}/{m.Id}", globPattern, new GlobOptions { NoCase = true })
                            || Glob.Match(m.Id, globPattern, new GlobOptions { NoCase = true }))
                        .ToList();
                }

                if (globMatches.Count == 0) NoMatch(pattern);
                foreach (var model in globMatches) Add(model, thinkingLevel);
                continue;
            }

            var result = ResolveModelReference(
                new ModelReferenceRequest
                {
                    Text = pattern,
                    ThinkingSuffix = ModelReferenceThinkingSuffix.Lenient,
                    Fuzzy = true,
                },
                availableModels,
                _ => true);
            if (result is ModelReferenceResult.NotFound)
            {
                NoMatch(pattern);
                continue;
            }

            if (result is ModelReferenceResult.Resolved { Warning: { } warning })
            {
                diagnostics.Add(new ModelScopeDiagnostic
                {
                    Type = ModelScopeDiagnostic.Types.Warning,
                    Code = ModelScopeDiagnostic.Codes.InvalidThinkingLevel,
                    Message = warning,
                    Pattern = pattern,
                });
            }

            IReadOnlyList<ModelSpec> matches;
            ThinkingLevel? level;
            switch (result)
            {
                case ModelReferenceResult.Resolved single:
                    matches = [single.Model];
                    level = single.ThinkingLevel;
                    break;
                case ModelReferenceResult.Ambiguous multiple:
                    matches = multiple.Candidates;
                    level = multiple.ThinkingLevel;
                    Ambiguous(pattern, matches);
                    break;
                default:
                    continue;
            }

            foreach (var model in matches) Add(model, level);
        }

        return new ResolveModelScopeResult { ScopedModels = scopedModels, Diagnostics = diagnostics };
    }

    /// <summary>Resolve patterns against the runtime's authenticated models, returning warnings instead
    /// of printing them. Corresponds to the TS <c>resolveModelScopeWithDiagnostics</c>.</summary>
    public static async Task<ResolveModelScopeResult> ResolveModelScopeWithDiagnosticsAsync(
        IReadOnlyList<string> patterns,
        IModelResolverRuntime runtime,
        CancellationToken signal = default)
        => ResolveModelScopeFromModels(patterns, await runtime.GetAvailableAsync(null, signal).ConfigureAwait(false));

    /// <summary>Resolve patterns against the runtime's authenticated models, printing every warning in
    /// yellow. Corresponds to the TS <c>resolveModelScope</c>.</summary>
    public static async Task<IReadOnlyList<ScopedModel>> ResolveModelScopeAsync(
        IReadOnlyList<string> patterns,
        IModelResolverRuntime runtime,
        CancellationToken signal = default)
    {
        var resolved =
            await ResolveModelScopeWithDiagnosticsAsync(patterns, runtime, signal).ConfigureAwait(false);
        foreach (var diagnostic in resolved.Diagnostics)
        {
            ErrorWriter.WriteLine(Chalk.Yellow($"Warning: {diagnostic.Message}"));
        }

        return resolved.ScopedModels;
    }

    // ================================================================= CLI model

    /// <summary>
    /// Resolve a single model from CLI flags. Supports <c>--provider &lt;provider&gt; --model &lt;pattern&gt;</c>,
    /// <c>--model &lt;provider&gt;/&lt;pattern&gt;</c>, and fuzzy matching (same rules as model scoping:
    /// exact id, then partial id/name).
    /// </summary>
    /// <remarks>
    /// This does not apply the thinking level by itself, but it may <em>parse</em> and return one from
    /// <c>"&lt;pattern&gt;:&lt;thinking&gt;"</c> so the caller can apply it.
    /// </remarks>
    public static ResolveCliModelResult ResolveCliModel(ResolveCliModelOptions options)
    {
        var cliProvider = options.CliProvider;
        var cliModel = options.CliModel;
        var cliThinking = options.CliThinking;
        var runtime = options.Runtime;

        if (string.IsNullOrEmpty(cliModel))
        {
            return new ResolveCliModelResult { Model = null, Warning = null, Error = null };
        }

        // Important: use *all* models here, not just models with pre-configured auth.
        // This allows "--api-key" to be used for first-time setup.
        var availableModels = runtime.GetModels().ToList();
        if (availableModels.Count == 0)
        {
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = "No models available. Check your installation or add models to models.json.",
            };
        }

        var provider = string.IsNullOrEmpty(cliProvider)
            ? null
            : availableModels
                .FirstOrDefault(m => JsString.ToLowerCase(m.Provider) == JsString.ToLowerCase(cliProvider!))
                ?.Provider;
        if (!string.IsNullOrEmpty(cliProvider) && provider is null)
        {
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.",
            };
        }

        var result = ResolveModelReference(
            new ModelReferenceRequest
            {
                Text = cliModel,
                Provider = provider,
                ThinkingSuffix = ModelReferenceThinkingSuffix.Strict,
                Fuzzy = true,
            },
            availableModels,
            // An inline key goes to whichever provider is selected, so stored credentials must not choose it.
            options.InlineApiKey ? _ => false : runtime.HasConfiguredAuth);
        if (result is ModelReferenceResult.Resolved resolvedModel)
        {
            return new ResolveCliModelResult
            {
                Model = resolvedModel.Model,
                ThinkingLevel = resolvedModel.ThinkingLevel,
                Warning = resolvedModel.Warning,
                Error = null,
            };
        }

        if (result is ModelReferenceResult.Ambiguous ambiguousModel)
        {
            var matches = string.Join(
                ", ",
                ambiguousModel.Candidates
                    .Select(m => $"{m.Provider}/{m.Id}")
                    .OrderBy(value => value, Comparer<string>.Create(Collate)));
            var authHint = options.InlineApiKey
                ? "--api-key does not choose a provider."
                : ambiguousModel.UsableCount == 0
                    ? "No matching provider is authenticated."
                    : "More than one matching provider is authenticated.";
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = $"Model \"{cliModel}\" is ambiguous across providers: {matches}. {authHint}"
                    + " Use --provider or provider/model.",
            };
        }

        var target = ((ModelReferenceResult.NotFound)result).Target;
        if (target is null)
        {
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = $"Model \"{cliModel}\" not found. Use --list-models to see available models.",
            };
        }

        // Use the thinking level suffix only when --thinking is not explicitly provided; otherwise it stays
        // part of the custom model id.
        // e.g. "zai-org/GLM-5.1-FP8:high" -> modelId="zai-org/GLM-5.1-FP8", fallbackThinking="high"
        var targetModelId = cliThinking is not null ? target.ModelText : target.ModelId;
        var fallbackThinking = cliThinking is not null ? null : target.ThinkingLevel;
        var fallbackModel = BuildFallbackModel(target, targetModelId);
        var requestedThinking = cliThinking ?? fallbackThinking;
        return new ResolveCliModelResult
        {
            Model = fallbackModel is not null && requestedThinking is { } level && level != ThinkingLevel.Off
                ? fallbackModel with { Reasoning = true }
                : fallbackModel,
            ThinkingLevel = fallbackThinking,
            Warning = $"Model \"{targetModelId}\" not found for provider \"{target.Provider}\"."
                + " Using custom model id.",
            Error = null,
        };
    }

    // ================================================================= initial model

    /// <summary>
    /// Find the initial model to use based on priority:
    /// <list type="number">
    /// <item>First model from scoped models (if not continuing/resuming)</item>
    /// <item>Saved default from settings, if auth is configured</item>
    /// <item>First available model with a valid API key, preferring <see cref="DefaultModelPerProvider"/></item>
    /// <item>Nothing available</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <c>--provider</c> / <c>--model</c> are resolved by <see cref="ResolveCliModel"/> at the CLI boundary and
    /// arrive as an explicit model; 1.4.0 removed the CLI branch this function used to carry, which is why a
    /// failed <c>--model</c> is reported by the caller rather than here.
    /// </remarks>
    public static Task<InitialModelResult> FindInitialModelAsync(InitialModelOptions options)
    {
        var scopedModels = options.ScopedModels;
        var isContinuing = options.IsContinuing;
        var defaultProvider = options.DefaultProvider;
        var defaultModelId = options.DefaultModelId;
        var defaultThinkingLevel = options.DefaultThinkingLevel;
        var modelThinkingLevels = options.ModelThinkingLevels;
        var runtime = options.Runtime;

        // 1. Use first model from scoped models (skip if continuing/resuming)
        if (scopedModels.Count > 0 && !isContinuing)
        {
            var scopedModel = scopedModels[0];
            var perModel = ReadThinkingLevel(
                modelThinkingLevels, $"{scopedModel.Model.Provider}/{scopedModel.Model.Id}");
            return Task.FromResult(new InitialModelResult
            {
                Model = scopedModel.Model,
                ThinkingLevel = scopedModel.ThinkingLevel
                    ?? perModel
                    ?? defaultThinkingLevel
                    ?? Defaults.DefaultThinkingLevel,
                FallbackMessage = null,
            });
        }

        // 2. Try saved default from settings if auth is configured.
        if (!string.IsNullOrEmpty(defaultProvider) && !string.IsNullOrEmpty(defaultModelId))
        {
            var found = runtime.GetModel(defaultProvider, defaultModelId);
            if (found is not null && runtime.HasConfiguredAuth(found.Provider))
            {
                var thinkingLevel = Defaults.DefaultThinkingLevel;
                var perModel = ReadThinkingLevel(modelThinkingLevels, $"{defaultProvider}/{defaultModelId}");
                if (perModel is { } perModelLevel) thinkingLevel = perModelLevel;
                else if (defaultThinkingLevel is { } defaultLevel) thinkingLevel = defaultLevel;
                return Task.FromResult(new InitialModelResult
                {
                    Model = found,
                    ThinkingLevel = thinkingLevel,
                    FallbackMessage = null,
                });
            }
        }

        // 3. Try first available model with valid API key
        var availableModels = runtime.GetAvailableSnapshot();

        if (availableModels.Count > 0)
        {
            // Try to find a default model from known providers
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    return Task.FromResult(new InitialModelResult
                    {
                        Model = match,
                        ThinkingLevel = Defaults.DefaultThinkingLevel,
                        FallbackMessage = null,
                    });
                }
            }

            // If no default found, use first available
            return Task.FromResult(new InitialModelResult
            {
                Model = availableModels[0],
                ThinkingLevel = Defaults.DefaultThinkingLevel,
                FallbackMessage = null,
            });
        }

        // 4. No model found
        return Task.FromResult(new InitialModelResult
        {
            Model = null,
            ThinkingLevel = Defaults.DefaultThinkingLevel,
            FallbackMessage = null,
        });
    }

    // ================================================================= session restore

    /// <summary>Restore model from session, with fallback to available models. Corresponds to the TS
    /// <c>restoreModelFromSession</c>.</summary>
    public static Task<RestoreModelResult> RestoreModelFromSessionAsync(
        string savedProvider,
        string savedModelId,
        ModelSpec? currentModel,
        bool shouldPrintMessages,
        IModelResolverRuntime runtime)
    {
        var restoredModel = runtime.GetModel(savedProvider, savedModelId);

        // Check if restored model exists and still has auth configured
        var hasConfiguredAuth = restoredModel is not null
            && runtime.HasConfiguredAuth(restoredModel.Provider);

        if (restoredModel is not null && hasConfiguredAuth)
        {
            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Restored model: {savedProvider}/{savedModelId}"));
            }

            return Task.FromResult(new RestoreModelResult { Model = restoredModel, FallbackMessage = null });
        }

        // Model not found or no API key - fall back
        var reason = restoredModel is null ? "model no longer exists" : "no auth configured";

        if (shouldPrintMessages)
        {
            ErrorWriter.WriteLine(
                Chalk.Yellow($"Warning: Could not restore model {savedProvider}/{savedModelId} ({reason})."));
        }

        // If we already have a model, use it as fallback
        if (currentModel is not null)
        {
            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Falling back to: {currentModel.Provider}/{currentModel.Id}"));
            }

            return Task.FromResult(new RestoreModelResult
            {
                Model = currentModel,
                FallbackMessage = $"Could not restore model {savedProvider}/{savedModelId} ({reason})."
                    + $" Using {currentModel.Provider}/{currentModel.Id}.",
            });
        }

        // Try to find any available model
        var availableModels = runtime.GetAvailableSnapshot();

        if (availableModels.Count > 0)
        {
            // Try to find a default model from known providers
            ModelSpec? fallbackModel = null;
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    fallbackModel = match;
                    break;
                }
            }

            // If no default found, use first available
            fallbackModel ??= availableModels[0];

            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Falling back to: {fallbackModel.Provider}/{fallbackModel.Id}"));
            }

            return Task.FromResult(new RestoreModelResult
            {
                Model = fallbackModel,
                FallbackMessage = $"Could not restore model {savedProvider}/{savedModelId} ({reason})."
                    + $" Using {fallbackModel.Provider}/{fallbackModel.Id}.",
            });
        }

        // No models available
        return Task.FromResult(new RestoreModelResult { Model = null, FallbackMessage = null });
    }

    // ================================================================= helpers

    private static ThinkingLevel? ReadThinkingLevel(
        IReadOnlyDictionary<string, ThinkingLevel>? levels, string key)
        => levels is not null && levels.TryGetValue(key, out var level) ? level : null;

    /// <summary>
    /// <c>String.prototype.localeCompare</c>. Both Node and .NET use ICU for the current culture, so this
    /// matches (the same conclusion <c>Pi.Tui.Autocomplete</c> reached for its labels). Only the model ids
    /// and <c>"provider/modelId"</c> strings of this module are ever collated.
    /// </summary>
    private static int Collate(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.None);
}
