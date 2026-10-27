// ============================================================================
// JavaScript extension module loader
// ============================================================================
//
// `IExtensionModuleLoader` implementation that loads TypeScript/JavaScript
// extensions, which is what the upstream ecosystem ships. The assembly loader
// (`AssemblyLoadContextExtensionLoader`) keeps handling compiled `.dll`
// extensions; <see cref="CompositeExtensionModuleLoader"/> picks between them by
// file extension so both kinds load from the same directories.

using Jint.Native;

namespace Pi.CodingAgent.Core.Extensions.Js;

/// <summary>Loads `.ts` / `.tsx` / `.js` / `.mjs` / `.cjs` / `.jsx` extensions through Jint.</summary>
public sealed class JsExtensionModuleLoader : IExtensionModuleLoader
{
    /// <summary>Shared instance; a runtime is created per load, so no state lives here.</summary>
    public static JsExtensionModuleLoader Instance { get; } = new();

    public ValueTask<ExtensionFactory?> LoadAsync(string extensionPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(extensionPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Cannot find extension module '{extensionPath}'.", fullPath);
        }

        JsExtensionRuntime runtime;
        try
        {
            runtime = JsExtensionRuntime.Create(fullPath, Directory.GetCurrentDirectory());
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Could not load JavaScript extension '{extensionPath}': {error.Message}", error);
        }

        JsValue? factory;
        try
        {
            factory = runtime.GetDefaultExport();
        }
        catch
        {
            runtime.Dispose();
            throw;
        }

        if (factory is null)
        {
            runtime.Dispose();
            return new ValueTask<ExtensionFactory?>((ExtensionFactory?)null);
        }

        ExtensionFactory extensionFactory = pi =>
        {
            runtime.InvokeFactory(factory, pi);
            return Task.CompletedTask;
        };

        return new ValueTask<ExtensionFactory?>(extensionFactory);
    }
}

/// <summary>
/// Dispatches by file extension: compiled assemblies go to the
/// AssemblyLoadContext loader, TypeScript/JavaScript goes to the Jint host.
/// </summary>
public sealed class CompositeExtensionModuleLoader(IExtensionModuleLoader assemblyLoader, IExtensionModuleLoader scriptLoader)
    : IExtensionModuleLoader
{
    /// <summary>The default pairing used when no loader is supplied.</summary>
    public static CompositeExtensionModuleLoader Default { get; } =
        new(AssemblyLoadContextExtensionLoader.Instance, JsExtensionModuleLoader.Instance);

    public ValueTask<ExtensionFactory?> LoadAsync(string extensionPath, CancellationToken cancellationToken = default)
        => IsScript(extensionPath)
            ? scriptLoader.LoadAsync(extensionPath, cancellationToken)
            : assemblyLoader.LoadAsync(extensionPath, cancellationToken);

    /// <summary>Whether <paramref name="path"/> is a script the JavaScript host loads.</summary>
    public static bool IsScript(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);
}
