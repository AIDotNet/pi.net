// ============================================================================
// JavaScript extension runtime
// ============================================================================
//
// One runtime per extension entry file:
//
//   engine  → Jint instance that owns the extension's module graph
//   prelude → globals an extension expects (console / process / timers / Buffer)
//   shims   → node:* built-ins and the @earendil-works/* runtime surface
//   modules → CommonJS registry, keyed by resolved path
//
// Loading model: the entry file (and everything it imports) is transpiled from
// TypeScript to CommonJS, wrapped in `function (module, exports, require, ...)`
// and executed. The module's `exports.default` is the extension factory, exactly
// as with the upstream jiti loader.
//
// Threading: a Jint engine is single-threaded. Every entry into JavaScript goes
// through <see cref="Gate"/>; a nested host call (JS → C# → JS) re-enters on the
// same thread and is allowed, because `lock` is re-entrant.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Pi.CodingAgent.Core.Extensions.Js;

/// <summary>One module in the extension's CommonJS registry.</summary>
internal sealed class JsModule(string id, string fileName, string directory)
{
    public string Id { get; } = id;

    public string FileName { get; } = fileName;

    public string Directory { get; } = directory;

    public JsValue? Exports { get; set; }
}

internal sealed class JsExtensionRuntime : IDisposable
{
    private readonly object gate = new();
    private readonly Engine engine;
    private readonly string entryPath;
    private readonly Dictionary<string, JsModule> modules = new(StringComparer.Ordinal);
    private readonly HashSet<string> evaluating = new(StringComparer.Ordinal);
    private readonly JsExtensionHost host;

    private JsExtensionRuntime(Engine engine, string entryPath, JsExtensionHost host)
    {
        this.engine = engine;
        this.entryPath = entryPath;
        this.host = host;
    }

    /// <summary>Create a runtime for <paramref name="entryPath"/> and load its module graph.</summary>
    internal static JsExtensionRuntime Create(string entryPath, string cwd)
    {
        var fullPath = Path.GetFullPath(entryPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Cannot find extension module '{entryPath}'.", fullPath);
        }

        var engine = new Engine(options =>
        {
            options.Constraints.StackOverflowGuard = true;
            // An extension handler may legitimately block on a host dialog; the
            // timeout only guards runaway JavaScript (an infinite loop).
            options.TimeoutInterval(TimeSpan.FromMinutes(10));
        });

        var host = new JsExtensionHost(cwd, engine);
        var runtime = new JsExtensionRuntime(engine, fullPath, host);
        host.Invoke = runtime.InvokeCallback;
        runtime.Initialize();
        return runtime;
    }

    private void Initialize()
    {
        engine.SetValue("__piHost", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["call"] = new Func<string, string?, string?>(host.Call),
            ["log"] = new Action<string, string>(host.Log),
            ["env"] = new Func<string>(host.EnvironmentJson),
            ["platform"] = new Func<string>(host.Platform),
            ["cwd"] = new Func<string>(() => host.Cwd),
        });

        engine.Execute("globalThis.__pi_builtins = new Map();");
        engine.Execute(ExtensionRuntimeAssets.ReadText("prelude.js"), "pi:prelude");

        // Node built-ins and the pi-* surface are ordinary modules in the same
        // registry; the shims register themselves while they evaluate.
        engine.Execute(ExtensionRuntimeAssets.ReadText(Path.Combine("shims", "node-builtins.js")), "pi:node-builtins");
        engine.SetValue("__pi_import_meta", engine.Evaluate("({ url: '' })"));
    }

    /// <summary>The extension factory (`export default`), or null when the module exports none.</summary>
    internal JsValue? GetDefaultExport()
    {
        lock (gate)
        {
            var exports = RequireFile(entryPath);
            if (!exports.IsObject())
            {
                return null;
            }

            var value = exports.AsObject().Get("default");
            return value.IsUndefined() || value.IsNull() ? null : value;
        }
    }

    /// <summary>Run the extension factory against <paramref name="api"/>.</summary>
    internal void InvokeFactory(JsValue factory, IExtensionApi api)
    {
        lock (gate)
        {
            host.Bind(api);
            try
            {
                var apiObject = engine.Evaluate("__pi_make_api()");
                var result = engine.Call(factory, JsValue.Undefined, [apiObject]);
                Await(result, "extension factory");
            }
            finally
            {
                // The api is only valid while the factory and its callbacks run; a
                // later event dispatch rebinds it.
                host.Unbind(api);
            }
        }
    }

    /// <summary>
    /// Invoke a registered JavaScript callback by id, awaiting its promise.
    /// </summary>
    internal JsValue InvokeCallback(string callbackId, params JsValue[] args)
    {
        lock (gate)
        {
            var invoke = engine.Evaluate("__pi_invoke_callback");
            var array = engine.Evaluate("[]").AsArray();
            foreach (var arg in args)
            {
                array.Push(arg);
            }

            var result = engine.Call(invoke, JsValue.Undefined, [JsValue.FromObject(engine, callbackId), array]);
            return Await(result, $"extension callback {callbackId}");
        }
    }

    /// <summary>Run <paramref name="action"/> with the api bound (event dispatch and friends).</summary>
    internal void WithApi(IExtensionApi api, Action action)
    {
        lock (gate)
        {
            host.Bind(api);
            try
            {
                action();
            }
            finally
            {
                host.Unbind(api);
            }
        }
    }

    /// <summary>
    /// Settle a JavaScript value: promises yield to the host's task pump and timer
    /// queue, plain values return as-is.
    /// </summary>
    private JsValue Await(JsValue value, string what)
    {
        if (!value.IsObject() || value.AsObject() is not { } instance || !IsPromise(instance))
        {
            return value;
        }

        var box = engine.Evaluate("__pi_track()").AsObject();
        engine.Call(box.Get("settle"), box, [value]);

        var stopwatch = Stopwatch.StartNew();
        var idlePasses = 0;
        while (!box.Get("done").AsBoolean())
        {
            var before = engine.Evaluate("__pi_pending_timers()").AsNumber();
            engine.Advanced.ProcessTasks();
            var fired = engine.Evaluate("__pi_fire_timers()").AsNumber();

            if (box.Get("done").AsBoolean())
            {
                break;
            }

            if (fired == 0 && before == engine.Evaluate("__pi_pending_timers()").AsNumber())
            {
                idlePasses++;
            }
            else
            {
                idlePasses = 0;
            }

            if (idlePasses > 5000)
            {
                throw new InvalidOperationException(
                    $"The {what} returned a promise that never settled. The host settles promises "
                    + "synchronously and cannot wait on external I/O the extension started itself.");
            }

            if (stopwatch.Elapsed > TimeSpan.FromMinutes(10))
            {
                throw new TimeoutException($"The {what} did not finish within 10 minutes.");
            }
        }

        if (!box.Get("ok").AsBoolean())
        {
            var error = box.Get("value");
            throw error.IsObject() && error.AsObject().HasProperty("message")
                ? new InvalidOperationException(error.AsObject().Get("message").ToString())
                : new InvalidOperationException(error.ToString());
        }

        return box.Get("value");
    }

    private static bool IsPromise(ObjectInstance instance)
    {
        var then = instance.Get("then");
        return !then.IsUndefined() && !then.IsNull();
    }

    // ---------------------------------------------------------------- modules

    /// <summary>
    /// CommonJS `require`: built-in shims first, then the pi-* virtual packages,
    /// then the file system (relative and node_modules lookup).
    /// </summary>
    internal JsValue Require(string specifier, string fromDirectory)
    {
        var builtin = ResolveBuiltin(specifier);
        if (builtin is not null)
        {
            return engine.Evaluate($"__pi_builtins.get({JsonSerializer.Serialize(builtin)})");
        }

        var asset = ResolveAsset(specifier);
        if (asset is not null)
        {
            return RequireAsset(asset, specifier);
        }

        return RequireFile(ResolveFile(specifier, fromDirectory));
    }

    private static string? ResolveBuiltin(string specifier)
    {
        var normalized = specifier.StartsWith("node:", StringComparison.Ordinal) ? specifier : "node:" + specifier;
        return normalized switch
        {
            "node:path" or "node:fs" or "node:fs/promises" or "node:os" or "node:process" or "node:timers"
                or "node:timers/promises" or "node:url" or "node:crypto" or "node:util" or "node:events"
                or "node:child_process" or "node:buffer" or "node:module" or "node:zlib" or "node:readline"
                or "node:readline/promises" => normalized,
            _ => null,
        };
    }

    /// <summary>The pi-* packages the host answers for, mapped to their shim file.</summary>
    private static string? ResolveAsset(string specifier) => specifier switch
    {
        "@earendil-works/pi-ai" or "@earendil-works/pi-ai/compat" => Path.Combine("shims", "pi-ai.js"),
        "@earendil-works/pi-coding-agent" => Path.Combine("shims", "pi-coding-agent.js"),
        "@earendil-works/pi-tui" => Path.Combine("shims", "pi-tui.js"),
        _ => null,
    };

    private JsValue RequireAsset(string assetRelativePath, string specifier)
    {
        var id = "asset:" + specifier;
        if (modules.TryGetValue(id, out var cached) && cached.Exports is not null)
        {
            return cached.Exports;
        }

        var path = ExtensionRuntimeAssets.RequirePath(assetRelativePath);
        var module = new JsModule(id, path, Path.GetDirectoryName(path)!);
        modules[id] = module;
        module.Exports = Evaluate(module, ExtensionRuntimeAssets.ReadText(assetRelativePath), transpile: false);
        return module.Exports;
    }

    private JsValue RequireFile(string absolutePath)
    {
        var id = "file:" + absolutePath;
        if (modules.TryGetValue(id, out var cached) && cached.Exports is not null)
        {
            return cached.Exports;
        }

        if (!evaluating.Add(id))
        {
            // A cycle: Node hands back the partially filled exports object.
            if (modules.TryGetValue(id, out var partial) && partial.Exports is not null)
            {
                return partial.Exports;
            }

            throw new InvalidOperationException($"Circular import while loading '{absolutePath}'.");
        }

        try
        {
            var module = new JsModule(id, absolutePath, Path.GetDirectoryName(absolutePath)!)
            {
                Exports = engine.Evaluate("({})"),
            };
            modules[id] = module;

            if (absolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                module.Exports = ParseJsonModule(File.ReadAllText(absolutePath));
                return module.Exports;
            }

            var source = File.ReadAllText(absolutePath);
            // `.cjs` is CommonJS by definition; everything else goes through Babel with
            // `sourceType: unambiguous`, so TypeScript, ESM inside node_modules and plain
            // CommonJS all take the same path.
            var transpile = !absolutePath.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);
            module.Exports = Evaluate(module, source, transpile);
            return module.Exports;
        }
        finally
        {
            evaluating.Remove(id);
        }
    }

    private JsValue Evaluate(JsModule module, string source, bool transpile)
    {
        var code = transpile ? TypeScriptTranspiler.Transpile(source, module.FileName) : source;

        // `import.meta` is global state in this host: it is set to the module being
        // evaluated, which is correct for top-level code (the only place upstream
        // extensions read it).
        engine.SetValue("__pi_import_meta", engine.Evaluate($"({{\"url\": {JsonSerializer.Serialize("file://" + module.FileName.Replace('\\', '/'))}}})"));

        var wrapped = new StringBuilder(code.Length + 128)
            .Append("(function (module, exports, require, __filename, __dirname) {\n")
            .Append(code)
            .Append("\n})")
            .ToString();

        JsValue factory;
        try
        {
            factory = engine.Evaluate(wrapped, module.FileName);
        }
        catch (JavaScriptException error)
        {
            throw new InvalidOperationException(
                $"Failed to parse '{module.FileName}': {error.Message}", error);
        }

        var moduleObject = engine.Evaluate("({ exports: {} })").AsObject();
        var requireFunction = new Jint.Runtime.Interop.ClrFunction(engine, "require", (_, arguments) =>
        {
            var specifier = arguments.Length > 0 ? arguments[0].AsString() : throw new ArgumentException("require() needs a specifier");
            return Require(specifier, module.Directory);
        });

        // `node:module`'s createRequire hands this back to code that cannot use the
        // module-scoped require directly.
        engine.SetValue("__pi_require", requireFunction);

        try
        {
            engine.Call(factory, JsValue.Undefined,
            [
                moduleObject,
                moduleObject.Get("exports"),
                requireFunction,
                JsValue.FromObject(engine, module.FileName),
                JsValue.FromObject(engine, module.Directory),
            ]);
        }
        catch (JavaScriptException error)
        {
            throw new InvalidOperationException(
                $"Extension module '{module.FileName}' threw while loading: {error.Message}", error);
        }

        // `module.exports = …` replaces the object the wrapper received.
        return moduleObject.Get("exports");
    }

    private JsValue ParseJsonModule(string text)
    {
        engine.SetValue("__pi_json_text", text);
        try
        {
            return engine.Evaluate("JSON.parse(__pi_json_text)");
        }
        finally
        {
            engine.SetValue("__pi_json_text", JsValue.Undefined);
        }
    }

    // ------------------------------------------------------------- resolution

    private static string ResolveFile(string specifier, string fromDirectory)
    {
        if (specifier.StartsWith('.') || Path.IsPathRooted(specifier) || specifier.StartsWith("file:", StringComparison.Ordinal))
        {
            var candidate = specifier.StartsWith("file:", StringComparison.Ordinal)
                ? new Uri(specifier).LocalPath
                : Path.GetFullPath(Path.Combine(fromDirectory, specifier));
            return ResolvePath(candidate)
                ?? throw new FileNotFoundException(
                    $"Cannot find module '{specifier}' imported from '{fromDirectory}'.", candidate);
        }

        // node_modules lookup, walking up from the importing directory.
        var directory = new DirectoryInfo(fromDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "node_modules", specifier);
            var resolved = ResolvePath(candidate);
            if (resolved is not null)
            {
                return resolved;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Cannot find module '{specifier}' imported from '{fromDirectory}'. The JavaScript extension host "
            + "provides node:* built-ins and the @earendil-works/* runtime, and resolves other packages from "
            + "node_modules next to the extension.");
    }

    /// <summary>Resolve a path to a file: exact, with an extension, or a directory entry.</summary>
    private static string? ResolvePath(string candidate)
    {
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        foreach (var extension in (string[])[".ts", ".tsx", ".js", ".mjs", ".cjs", ".json"])
        {
            if (File.Exists(candidate + extension))
            {
                return Path.GetFullPath(candidate + extension);
            }
        }

        if (!Directory.Exists(candidate))
        {
            return null;
        }

        var packageJson = Path.Combine(candidate, "package.json");
        if (File.Exists(packageJson))
        {
            var entry = ReadPackageEntry(packageJson);
            if (entry is not null)
            {
                var resolved = ResolvePath(Path.Combine(candidate, entry));
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        foreach (var extension in (string[])[".ts", ".tsx", ".js", ".mjs", ".cjs", ".json"])
        {
            var index = Path.Combine(candidate, "index" + extension);
            if (File.Exists(index))
            {
                return Path.GetFullPath(index);
            }
        }

        return null;
    }

    /// <summary>Read `exports` / `module` / `main` from a package.json, CommonJS first.</summary>
    private static string? ReadPackageEntry(string packageJsonPath)
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
            if (manifest is null)
            {
                return null;
            }

            if (manifest["exports"] is JsonValue exportValue && exportValue.TryGetValue<string>(out var direct))
            {
                return direct;
            }

            if (manifest["exports"] is JsonObject exports)
            {
                var root = exports["."] ?? exports;
                if (root is JsonValue rootValue && rootValue.TryGetValue<string>(out var rootPath))
                {
                    return rootPath;
                }

                if (root is JsonObject conditions)
                {
                    foreach (var condition in (string[])["require", "import", "default"])
                    {
                        if (conditions[condition] is JsonValue value && value.TryGetValue<string>(out var path))
                        {
                            return path;
                        }
                    }
                }
            }

            foreach (var field in (string[])["module", "main"])
            {
                if (manifest[field] is JsonValue value && value.TryGetValue<string>(out var path))
                {
                    return path;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public void Dispose() => host.Dispose();
}
