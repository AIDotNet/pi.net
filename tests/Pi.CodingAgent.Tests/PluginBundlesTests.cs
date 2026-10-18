using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Chord.Facets;
using Pi.Chord.Node;
using Pi.CodingAgent.Experimental.Plugins;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the plugin bundle loading (4g-3c): the session loader tolerates packages without a
/// "session" entry, and the presentation data round-trips the selected TUI artifacts.
/// </summary>
public class PluginBundlesTests
{
    private sealed class FakeFacet(string id) : IFacet
    {
        public string Id { get; } = id;

        public void Setup(IFacetEnvironment environment)
        {
        }
    }

    private sealed class FakeModuleHost(object? exports) : IFacetModuleHost
    {
        public object? ExecuteCommonJsModule(string source, string modulePath,
            IReadOnlyList<string> externalImports, FacetBundleExternalResolver? resolver) => exports;
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-plugin-bundles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Write a one-entry bundle whose integrity matches its source.</summary>
    private static (string Directory, string ManifestPath) WriteBundle(string entryName, string source)
    {
        var directory = TempDirectory();
        var entry = new JsonObject
        {
            ["file"] = "facet-entry.cjs",
            ["integrity"] = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            ["externalImports"] = new JsonArray(),
        };
        var manifest = new JsonObject
        {
            ["format"] = FacetBundleFormats.Format,
            ["formatVersion"] = FacetBundleFormats.FormatVersion,
            ["plugin"] = new JsonObject { ["id"] = "acme.plugin", ["version"] = "1.0.0" },
            ["entries"] = new JsonObject { [entryName] = entry },
        };
        var manifestPath = Path.Combine(directory, FacetBundleFormats.ManifestFile);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "facet-entry.cjs"), source);
        return (directory, manifestPath);
    }

    [Fact]
    public void SessionPluginFacetLoader_WithoutManifests_IsAbsent()
    {
        Assert.Null(PluginBundles.CreateSessionPluginFacetLoader([]));
    }

    [Fact]
    public async Task SessionPluginFacetLoader_WithoutASessionEntry_LoadsAnEmptySet()
    {
        var (directory, manifestPath) = WriteBundle("tui", "module.exports = {};");
        try
        {
            var loader = PluginBundles.CreateSessionPluginFacetLoader([manifestPath]);

            Assert.NotNull(loader);
            var loaded = await loader!.LoadAsync();
            Assert.Empty(loaded.Facets);
            await loaded.DisposeAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OptionalSessionLoader_WithASessionEntry_LoadsTheBundle()
    {
        var (directory, manifestPath) = WriteBundle("session", "module.exports = {};");
        try
        {
            var loader = PluginBundles.CreateOptionalSessionFacetLoader(manifestPath,
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("alpha") }));

            var loaded = await loader.LoadAsync();
            Assert.Equal("alpha", Assert.Single(loaded.Facets).Id);

            await loaded.DisposeAsync();
            Assert.Empty(loaded.Facets);
            await loaded.DisposeAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionPluginFacetLoader_CombinesEveryPackage()
    {
        var (firstDirectory, firstManifest) = WriteBundle("session", "module.exports = {};");
        var (secondDirectory, secondManifest) = WriteBundle("tui", "module.exports = {};");
        try
        {
            // The second package has no session entry: it contributes nothing instead of failing.
            var loader = PluginBundles.CreateSessionPluginFacetLoader([firstManifest, secondManifest],
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("alpha") }));

            var loaded = await loader!.LoadAsync();
            Assert.Equal("alpha", Assert.Single(loaded.Facets).Id);
        }
        finally
        {
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatePresentationFacetData_WrapsTheArtifacts()
    {
        var (directory, manifestPath) = WriteBundle("tui", "module.exports = {};");
        try
        {
            var artifact = await FacetBundleLoader.ReadFacetBundleArtifactAsync(manifestPath, "tui");

            var data = PluginBundles.CreatePresentationFacetData([artifact]);

            var bundles = Assert.IsType<JsonArray>(data[PluginBundles.PresentationFacetBundlesKey]);
            var single = Assert.IsType<JsonObject>(Assert.Single(bundles));
            Assert.Equal(FacetBundleFormats.ArtifactFormat, single["format"]!.GetValue<string>());
            Assert.Equal("tui", single["entryName"]!.GetValue<string>());
            Assert.Equal("acme.plugin", single["plugin"]!["id"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatePresentationFacetLoaders_RoundTripsTheArtifacts()
    {
        var (directory, manifestPath) = WriteBundle("tui", "module.exports = {};");
        try
        {
            var artifact = await FacetBundleLoader.ReadFacetBundleArtifactAsync(manifestPath, "tui");
            var data = PluginBundles.CreatePresentationFacetData([artifact]);

            var loaders = PluginBundles.CreatePresentationFacetLoaders(data,
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("alpha") }));

            var loaded = await Assert.Single(loaders).LoadAsync();
            Assert.Equal("alpha", Assert.Single(loaded.Facets).Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreatePresentationFacetLoaders_RejectsNonObjectData()
    {
        Assert.Throws<InvalidOperationException>(() => PluginBundles.CreatePresentationFacetLoaders(null));
        Assert.Throws<InvalidOperationException>(() =>
            PluginBundles.CreatePresentationFacetLoaders(new JsonArray()));
        Assert.Throws<InvalidOperationException>(() =>
            PluginBundles.CreatePresentationFacetLoaders(JsonValue.Create("plugins")));
    }

    [Fact]
    public void CreatePresentationFacetLoaders_WithoutTheKey_LoadsNothing()
    {
        Assert.Empty(PluginBundles.CreatePresentationFacetLoaders(new JsonObject()));
    }

    [Fact]
    public void CreatePresentationFacetLoaders_RejectsANonArrayBundleList()
    {
        foreach (var value in new JsonNode?[] { JsonValue.Create("one"), null })
        {
            var error = Assert.Throws<InvalidOperationException>(() => PluginBundles.CreatePresentationFacetLoaders(
                new JsonObject { [PluginBundles.PresentationFacetBundlesKey] = value }));
            Assert.Contains("Invalid presentation plugin bundle list", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CreatePresentationFacetLoaders_RejectsAnInvalidArtifact()
    {
        var data = new JsonObject
        {
            [PluginBundles.PresentationFacetBundlesKey] = new JsonArray(new JsonObject()),
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => PluginBundles.CreatePresentationFacetLoaders(data));

        Assert.Contains("Invalid facet bundle artifact", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePluginExternal_MapsOnlyThePluginApi()
    {
        var resolved = PluginBundles.ResolvePluginExternal(PluginBundles.PluginApiSpecifier);

        Assert.NotNull(resolved);
        Assert.EndsWith("Pi.CodingAgent.dll", resolved, StringComparison.Ordinal);
        Assert.Null(PluginBundles.ResolvePluginExternal("lodash"));
    }
}
