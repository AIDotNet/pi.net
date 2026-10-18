// ============================================================================
// Plugin bundle loading — port of experimental/plugins/bundled.ts (4g-3c)
// ============================================================================
//
// The two ends of the plugin bundle pipeline that the services layer needs:
//
// - the session worker builds a loader from the manifest paths of the packages selected for a
//   Session (a package without a "session" entry contributes nothing instead of failing);
// - the server packs the TUI artifacts of the selected packages into the presentation plugin data,
//   and the presentation turns that data back into local loaders.
//
// TS resolves the bundles' `@earendil-works/pi-coding-agent/experimental/plugin` import to the host's
// plugin module by URL. The C# port has no JavaScript module graph — extensions load as
// AssemblyLoadContext plugins — so the resolver maps the specifier to the assembly that carries the
// plugin API, and the default module host still refuses to execute JavaScript (C335).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Node;

namespace Pi.CodingAgent.Experimental.Plugins;

/// <summary>Port of <c>plugins/bundled.ts</c>.</summary>
public static class PluginBundles
{
    /// <summary>TS <c>PRESENTATION_FACET_BUNDLES_KEY</c>.</summary>
    public const string PresentationFacetBundlesKey = "presentationFacetBundles";

    /// <summary>TS <c>PI_PLUGIN_API</c>: the specifier bundles import the pi plugin API under.</summary>
    public const string PluginApiSpecifier = "@earendil-works/pi-coding-agent/experimental/plugin";

    /// <summary>
    /// TS <c>createSessionPluginFacetLoader</c>: the session-entry loader of every selected package,
    /// or <c>null</c> when nothing is selected.
    /// </summary>
    public static IFacetLoader? CreateSessionPluginFacetLoader(
        IReadOnlyList<string> manifestPaths, IFacetModuleHost? moduleHost = null)
    {
        if (manifestPaths.Count == 0)
        {
            return null;
        }

        return Api.CombineFacetLoaders(
            [.. manifestPaths.Select(manifestPath => CreateOptionalSessionFacetLoader(manifestPath, moduleHost))]);
    }

    /// <summary>
    /// TS <c>createOptionalSessionFacetLoader</c>: the package's session entry when it has one, an
    /// empty facet set when it does not.
    /// </summary>
    public static IFacetLoader CreateOptionalSessionFacetLoader(string manifestPath, IFacetModuleHost? moduleHost = null)
    {
        var loader = FacetBundleLoader.CreateFacetBundleLoader(new FacetBundleLoaderOptions
        {
            ManifestPath = manifestPath,
            Entry = "session",
            ResolveExternal = ResolvePluginExternal,
        }, moduleHost);
        return new OptionalSessionFacetLoader(manifestPath, loader);
    }

    /// <summary>TS <c>createPresentationFacetData</c>: the artifacts a presentation will load locally.</summary>
    public static JsonObject CreatePresentationFacetData(IReadOnlyList<FacetBundleArtifact> artifacts)
        => new()
        {
            [PresentationFacetBundlesKey] = new JsonArray([.. artifacts.Select(ArtifactNode)]),
        };

    /// <summary>
    /// TS <c>createPresentationFacetLoaders</c>: local loaders only for the artifacts the connected
    /// server selected and sent.
    /// </summary>
    public static IReadOnlyList<IFacetLoader> CreatePresentationFacetLoaders(
        object? data, IFacetModuleHost? moduleHost = null)
    {
        if (data is null || data is JsonArray || data is not JsonObject plugins)
        {
            throw new InvalidOperationException("Invalid presentation plugin data");
        }

        // A missing key means the server published no bundles; an explicit null is not a list.
        if (!plugins.ContainsKey(PresentationFacetBundlesKey))
        {
            return [];
        }

        if (plugins[PresentationFacetBundlesKey] is not JsonArray artifacts)
        {
            throw new InvalidOperationException("Invalid presentation plugin bundle list");
        }

        return
        [
            .. artifacts.Select(artifact => FacetBundleLoader.CreateFacetBundleArtifactLoader(
                new FacetBundleArtifactLoaderOptions
                {
                    Artifact = artifact,
                    ResolveExternal = ResolvePluginExternal,
                }, moduleHost)),
        ];
    }

    /// <summary>
    /// TS <c>resolvePluginExternal</c>. The C# analogue of the host's plugin module is the assembly
    /// that carries the plugin API; every other specifier stays unresolved.
    /// </summary>
    public static string? ResolvePluginExternal(string specifier)
        => specifier == PluginApiSpecifier ? typeof(PluginBundles).Assembly.Location : null;

    private static JsonNode? ArtifactNode(FacetBundleArtifact artifact)
        => JsonSerializer.SerializeToNode(artifact, ArtifactJson.Options);

    /// <summary>The artifact wire shape (camelCase, nulls dropped — TS <c>JSON.stringify</c> semantics).</summary>
    private static class ArtifactJson
    {
        public static JsonSerializerOptions Options { get; } = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// The manifest is re-read on every load so a package whose "session" entry disappeared (or never
    /// existed) loads as an empty facet set instead of failing the generation.
    /// </summary>
    private sealed class OptionalSessionFacetLoader(string manifestPath, IFacetLoader loader) : IFacetLoader
    {
        public async Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
        {
            var manifest = await FacetBundleLoader.ReadFacetBundleManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            if (!manifest.Entries.ContainsKey("session"))
            {
                return new LoadedFacets { Facets = [], Dispose = () => Task.CompletedTask };
            }

            return await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
