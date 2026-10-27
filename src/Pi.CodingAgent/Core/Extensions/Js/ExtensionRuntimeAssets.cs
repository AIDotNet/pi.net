// ============================================================================
// JavaScript extension runtime assets
// ============================================================================
//
// The host needs three kinds of files at runtime: the Babel build that turns
// TypeScript into CommonJS, the prelude that installs the globals an extension
// expects, and the shim modules behind `node:*` / `@earendil-works/*`.
//
// They live under Assets/extension-runtime and are copied next to the binary,
// the same arrangement Assets/theme and Assets/export-html already use. A source
// tree fallback keeps `dotnet test` working when the test output did not copy
// them.

namespace Pi.CodingAgent.Core.Extensions.Js;

/// <summary>Locates the JavaScript runtime assets and caches the resolved directory.</summary>
internal static class ExtensionRuntimeAssets
{
    private static readonly Lazy<string?> Resolved = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The asset directory, or null when the runtime assets are missing.</summary>
    internal static string? Directory => Resolved.Value;

    internal static string RequirePath(string relativePath)
    {
        var directory = Directory
            ?? throw new InvalidOperationException(
                "The JavaScript extension runtime assets are missing. Expected Assets/extension-runtime next to "
                + $"'{AppContext.BaseDirectory}'. A rebuild of Pi.CodingAgent restores them.");

        var path = Path.Combine(directory, relativePath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"JavaScript extension runtime asset not found: {path}");
        }

        return path;
    }

    internal static string ReadText(string relativePath) => File.ReadAllText(RequirePath(relativePath));

    private static string? Locate()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "extension-runtime"),
        };

        // Source-tree fallback: walk up from the binary looking for the project.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "Assets", "extension-runtime"));
            candidates.Add(Path.Combine(directory.FullName, "src", "Pi.CodingAgent", "Assets", "extension-runtime"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "vendor", "babel.min.js"))
                && File.Exists(Path.Combine(candidate, "prelude.js")))
            {
                return candidate;
            }
        }

        return null;
    }
}
