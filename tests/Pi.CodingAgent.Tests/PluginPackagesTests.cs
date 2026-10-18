using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Chord.Node;
using Pi.CodingAgent.Experimental.Plugins;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the plugin package profiles (4g-3c): the profile files (validation, format, hashing),
/// the normalized package paths, and the serialized server-owned builder.
/// </summary>
public class PluginPackagesTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-plugin-packages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreatePackage(params string[] entries)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-plugin-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), """
        { "name": "acme.demo", "version": "1.0.0" }
        """);
        foreach (var entry in entries)
        {
            var path = Path.Combine(directory, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "export default {};");
        }

        return directory;
    }

    /// <summary>A bundler that writes real bundle outputs (like esbuild would) and tracks concurrency.</summary>
    private sealed class RecordingBundler : IFacetEntryBundler
    {
        private int _concurrent;

        public int MaxConcurrent { get; private set; }

        public bool Fail { get; set; }

        public async Task<FacetEntryBundleOutput> BundleEntryAsync(FacetEntryBundleRequest request,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            try
            {
                if (Fail)
                {
                    throw new InvalidOperationException("bundler exploded");
                }

                // Give a concurrent second build a chance to interleave when the tail is broken.
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                var outputPath = Path.Combine(request.TemporaryDirectory,
                    $"{request.EntryNames.Replace("[hash]", "h")}.cjs");
                File.WriteAllText(outputPath, $"// {request.EntryName}\nmodule.exports = {{}};");
                var outputs = new List<string> { outputPath };
                string? sourceMapPath = null;
                if (request.SourceMap)
                {
                    sourceMapPath = $"{outputPath}.map";
                    File.WriteAllText(sourceMapPath, "{}");
                    outputs.Add(sourceMapPath);
                }

                return new FacetEntryBundleOutput
                {
                    OutputPath = outputPath,
                    ExternalImports = request.External,
                    OutputPaths = outputs,
                    SourceMapPath = sourceMapPath,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    // ---------------------------------------------------------------- paths

    [Fact]
    public void NormalizePluginPackagePaths_ResolvesRelativePaths()
    {
        var normalized = PluginPackages.NormalizePluginPackagePaths(["plugins/one", "plugins/two"]);

        Assert.Equal(2, normalized.Count);
        Assert.All(normalized, path => Assert.True(Path.IsPathRooted(path)));
        Assert.EndsWith(Path.Combine("plugins", "one"), normalized[0], StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("plugins", "two"), normalized[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizePluginPackagePaths_RejectsAnEmptyPath()
    {
        var error = Assert.Throws<ArgumentException>(
            () => PluginPackages.NormalizePluginPackagePaths([""]));

        Assert.Contains("must not be empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizePluginPackagePaths_RejectsDuplicatesAfterResolving()
    {
        Assert.Throws<ArgumentException>(
            () => PluginPackages.NormalizePluginPackagePaths(["plugins/one", "./plugins/one"]));
    }

    [Fact]
    public void PluginBuildDirectoryName_DerivesAReadableCollisionFreeName()
    {
        // The directory name is sanitized, always labelled "-plugin", and disambiguated by a hash.
        Assert.Equal("demo-plugin-d4301e73dc7d", PluginPackages.PluginBuildDirectoryName("C:/plugins/demo-plugin"));
        Assert.Equal("my-plugin-a2c97e8bfafc", PluginPackages.PluginBuildDirectoryName("my plugin"));
        Assert.Equal("my.plugin-plugin-093a1f0f0f5f", PluginPackages.PluginBuildDirectoryName("my.plugin"));
        Assert.Equal("plugin-plugin-732c4e971163", PluginPackages.PluginBuildDirectoryName("///"));
    }

    [Fact]
    public void PluginBuildDirectoryName_UsesThePackageDirectoryForAPackageJsonPath()
    {
        Assert.Equal("demo-plugin-24ec972545e0",
            PluginPackages.PluginBuildDirectoryName("C:/plugins/demo/package.json"));
    }

    [Fact]
    public void PluginBuildDirectoryName_IgnoresATrailingSeparator()
    {
        // Node's basename strips a trailing separator; the hash still covers the path as given.
        Assert.Equal("demo-plugin-6228660df831", PluginPackages.PluginBuildDirectoryName("C:/plugins/demo/"));
        Assert.NotEqual(
            PluginPackages.PluginBuildDirectoryName("C:/plugins/demo"),
            PluginPackages.PluginBuildDirectoryName("C:/plugins/demo/"));
    }

    // ---------------------------------------------------------------- profiles

    [Fact]
    public async Task RestoreServerPluginPackageProfile_PersistsRestoresAndRemoves()
    {
        var directory = TempDirectory();
        try
        {
            var first = Path.GetFullPath("C:/pkg/a");
            var second = Path.GetFullPath("C:/pkg/b");
            var stored = await PluginPackages.RestoreServerPluginPackageProfileAsync(
                directory, "server-1", ["C:/pkg/a", "C:/pkg/b"]);

            Assert.Equal([first, second], stored);

            // JSON.stringify(…, null, 2) + "\n": two-space indent, LF endings, trailing newline.
            var path = Path.Combine(directory, "plugin-packages-server-1.json");
            var expected = $"{{\n  \"version\": 1,\n  \"packagePaths\": [\n    \"{Escape(first)}\",\n    \"{Escape(second)}\"\n  ]\n}}\n";
            Assert.Equal(expected, await File.ReadAllTextAsync(path));

            // An omitted selection restores what the last generation stored.
            Assert.Equal([first, second], await PluginPackages.RestoreServerPluginPackageProfileAsync(
                directory, "server-1", null));

            // An explicit empty selection removes the profile; restoring then yields nothing.
            Assert.Empty(await PluginPackages.RestoreServerPluginPackageProfileAsync(directory, "server-1", []));
            Assert.False(File.Exists(path));
            Assert.Empty(await PluginPackages.RestoreServerPluginPackageProfileAsync(directory, "server-1", null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionPluginPackageProfile_RoundTripsAndRemoves()
    {
        var directory = TempDirectory();
        try
        {
            await PluginPackages.WriteSessionPluginPackageProfileAsync(
                directory, "server-1", "/sessions/alpha", ["C:/pkg/a"]);

            var path = PluginPackages.SessionProfilePath(directory, "server-1", "/sessions/alpha");
            Assert.Equal(
                Path.Combine(directory, "session-plugin-packages-server-1-65ae41d20bf2a3dc17f36fdc.json"),
                path);

            Assert.Equal(
                [Path.GetFullPath("C:/pkg/a")],
                await PluginPackages.ReadSessionPluginPackageProfileAsync(directory, "server-1", "/sessions/alpha"));

            // Another Session hashes to another file: nothing is stored for it.
            Assert.Null(await PluginPackages.ReadSessionPluginPackageProfileAsync(
                directory, "server-1", "/sessions/beta"));

            await PluginPackages.RemoveSessionPluginPackageProfileAsync(directory, "server-1", "/sessions/alpha");
            Assert.Null(await PluginPackages.ReadSessionPluginPackageProfileAsync(
                directory, "server-1", "/sessions/alpha"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionPluginPackageProfile_StoresTheSessionPath()
    {
        var directory = TempDirectory();
        try
        {
            await PluginPackages.WriteSessionPluginPackageProfileAsync(
                directory, "server-1", "/sessions/alpha", ["C:/pkg/a"]);

            var path = PluginPackages.SessionProfilePath(directory, "server-1", "/sessions/alpha");
            // The stored package path is resolved, so it is escaped like the server profile's.
            var resolved = Path.GetFullPath("C:/pkg/a");
            var expected = "{\n  \"version\": 1,\n  \"sessionPath\": \"/sessions/alpha\",\n"
                + $"  \"packagePaths\": [\n    \"{Escape(resolved)}\"\n  ]\n}}\n";
            Assert.Equal(expected, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ \"version\": 1, \"packagePaths\": [\"C:/pkg/a\"], \"extra\": true }")]
    [InlineData("{ \"version\": 2, \"packagePaths\": [\"C:/pkg/a\"] }")]
    [InlineData("{ \"version\": \"1\", \"packagePaths\": [\"C:/pkg/a\"] }")]
    [InlineData("{ \"version\": true, \"packagePaths\": [\"C:/pkg/a\"] }")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("{ \"version\": 1, \"packagePaths\": [] }")]
    [InlineData("{ \"version\": 1, \"packagePaths\": [\"\"] }")]
    [InlineData("{ \"version\": 1, \"packagePaths\": [1] }")]
    [InlineData("{ \"version\": 1, \"packagePaths\": null }")]
    [InlineData("{ \"version\": 1, \"sessionPath\": \"/sessions/alpha\", \"packagePaths\": [\"C:/pkg/a\"] }")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public async Task ServerPluginPackageProfile_RejectsInvalidDocuments(string json)
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "plugin-packages-server-1.json"), json);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginPackages.RestoreServerPluginPackageProfileAsync(directory, "server-1", null));

            Assert.Contains("Invalid experimental plugin package profile", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionPluginPackageProfile_RejectsAMismatchedSessionPath()
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                PluginPackages.SessionProfilePath(directory, "server-1", "/sessions/alpha"),
                "{ \"version\": 1, \"sessionPath\": \"/sessions/other\", \"packagePaths\": [\"C:/pkg/a\"] }");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginPackages.ReadSessionPluginPackageProfileAsync(
                    directory, "server-1", "/sessions/alpha"));

            Assert.Contains("Invalid experimental plugin package profile", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PluginPackageProfile_FailsLoudlyOnUnreadableContent()
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "plugin-packages-server-1.json"), "not json");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginPackages.RestoreServerPluginPackageProfileAsync(directory, "server-1", null));

            Assert.Contains("Could not read experimental plugin package profile", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- builder

    [Fact]
    public async Task CreateServerPluginPackage_BuildsTheTuiArtifact()
    {
        var package = CreatePackage("src/session.ts", "src/tui.ts");
        var directory = TempDirectory();
        try
        {
            var configured = PluginPackages.CreateServerPluginPackage(
                directory, "server-1", package, new RecordingBundler());

            Assert.Equal("chord-facets.json", Path.GetFileName(configured.ManifestPath));
            Assert.StartsWith(Path.Combine(directory, "plugin-builds", "server-1"),
                configured.ManifestPath, StringComparison.Ordinal);

            var artifacts = await configured.Build();

            // Only the TUI facet is published to presentations.
            var artifact = Assert.Single(artifacts);
            Assert.Equal("tui", artifact.EntryName);
            Assert.Equal("acme.demo", artifact.Plugin.Id);
            Assert.Contains("// tui", artifact.Source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task CreateServerPluginPackage_WithoutATuiEntry_BuildsNothing()
    {
        var package = CreatePackage("src/session.ts");
        var directory = TempDirectory();
        try
        {
            var configured = PluginPackages.CreateServerPluginPackage(
                directory, "server-1", package, new RecordingBundler());

            Assert.Empty(await configured.Build());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task CreateServerPluginPackage_SerialisesBuilds()
    {
        var package = CreatePackage("src/session.ts", "src/tui.ts");
        var directory = TempDirectory();
        try
        {
            var bundler = new RecordingBundler();
            var configured = PluginPackages.CreateServerPluginPackage(directory, "server-1", package, bundler);

            var first = configured.Build();
            var second = configured.Build();
            var third = configured.Build();
            await Task.WhenAll(first, second, third);

            // Six entries (session + tui per build) without ever overlapping.
            Assert.Equal(1, bundler.MaxConcurrent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task CreateServerPluginPackage_AFailedBuildDoesNotBlockTheNext()
    {
        var package = CreatePackage("src/session.ts", "src/tui.ts");
        var directory = TempDirectory();
        try
        {
            var bundler = new RecordingBundler { Fail = true };
            var configured = PluginPackages.CreateServerPluginPackage(directory, "server-1", package, bundler);

            await Assert.ThrowsAsync<InvalidOperationException>(() => configured.Build());

            bundler.Fail = false;
            var artifacts = await configured.Build();
            Assert.Equal("tui", Assert.Single(artifacts).EntryName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    private static string Escape(string path)
        => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    // Keeps the hashing honest: the expectations above are the first hex characters of the
    // SHA-256 of the path, which is what TS's createHash("sha256").digest("hex").slice(0, n) gives.
    private static string Hash(string value, int hexChars)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..hexChars];
}
