// ============================================================================
// TypeScript → CommonJS transpiler
// ============================================================================
//
// The upstream port compiles extensions with jiti (esbuild under the hood). .NET
// has no equivalent, and asking the user for a Node install would defeat the
// point of loading `.ts` extensions in-process.
//
// This transpiler hosts @babel/standalone inside Jint. Babel is the same compiler
// family the upstream examples assume, so enums, decorators, generics, `satisfies`
// and type-only imports all behave. The engine is expensive to create (~2s for a
// 3 MB bundle) and therefore built once per process and reused under a lock; the
// per-file result is cached by content hash.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jint;

namespace Pi.CodingAgent.Core.Extensions.Js;

internal static class TypeScriptTranspiler
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static Engine? babel;
    private static string? babelVersion;

    /// <summary>Transpile one TypeScript module to CommonJS.</summary>
    internal static string Transpile(string source, string fileName)
    {
        var cacheKey = CacheKey(source, fileName);
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // Babel is a recursive-descent parser: deeply nested expressions (TypeBox's
        // generated parser is the worst case in the upstream corpus) exhaust the
        // default 1 MB thread stack. Jint reports that as "Maximum call stack size
        // exceeded" rather than crashing, so the compiler runs on a thread with a
        // stack large enough to finish.
        string? compiled = null;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    lock (Gate)
                    {
                        if (!Cache.TryGetValue(cacheKey, out compiled))
                        {
                            var engine = EnsureBabel();
                            compiled = Compile(engine, source, fileName);
                            Cache[cacheKey] = compiled;
                        }
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
            },
            MaxStackSize);

        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return compiled!;
    }

    /// <summary>64 MB: enough for the deepest parser the corpus produces, still bounded.</summary>
    private const int MaxStackSize = 64 * 1024 * 1024;

    private static string CacheKey(string source, string fileName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fileName + "\0" + source));
        return Convert.ToHexStringLower(hash);
    }

    private static Engine EnsureBabel()
    {
        if (babel is not null)
        {
            return babel;
        }

        var path = ExtensionRuntimeAssets.RequirePath(Path.Combine("vendor", "babel.min.js"));
        var engine = new Engine(options =>
        {
            options.Constraints.StackOverflowGuard = true;
            // A cold Babel parse of a large extension can take a few seconds; the
            // budget is per transpile call, not per extension lifetime.
            options.TimeoutInterval(TimeSpan.FromMinutes(2));
        });

        // Babel's UMD build probes a handful of host globals while it initialises.
        engine.Execute("""
            var console = { log: function () {}, warn: function () {}, error: function () {}, info: function () {}, debug: function () {} };
            var process = { env: {}, platform: "dotnet", cwd: function () { return "/"; }, versions: { node: "22.0.0" } };
            var setTimeout = function () { return 0; };
            var clearTimeout = function () {};
            var setInterval = function () { return 0; };
            var clearInterval = function () {};
            """);

        engine.Execute(File.ReadAllText(path));
        if (engine.GetValue("Babel").IsUndefined())
        {
            throw new InvalidOperationException(
                $"Babel did not initialise from '{path}'; the JavaScript extension runtime is unusable.");
        }

        babelVersion = engine.Evaluate("Babel.version").AsString();
        babel = engine;
        return engine;
    }

    private static string Compile(Engine engine, string source, string fileName)
    {
        var isTsx = fileName.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);

        // `import.meta` has no CommonJS meaning. Babel leaves it in place, so it is
        // rewritten to a prelude-provided object before compiling.
        var prepared = source
            .Replace("import.meta.url", "__pi_import_meta.url", StringComparison.Ordinal)
            .Replace("import.meta", "__pi_import_meta", StringComparison.Ordinal);

        engine.SetValue("__pi_src", prepared);
        engine.SetValue("__pi_file", Path.GetFileName(fileName));
        engine.SetValue("__pi_is_tsx", isTsx);

        try
        {
            var result = engine.Evaluate("""
                (function () {
                  var presets = [["typescript", { allExtensions: true, isTSX: __pi_is_tsx }]];
                  var plugins = ["transform-modules-commonjs", "transform-export-namespace-from"];
                  if (__pi_is_tsx) plugins.push(["transform-react-jsx", { pragma: "__pi_jsx" }]);
                  var out = Babel.transform(__pi_src, {
                    presets: presets,
                    plugins: plugins,
                    sourceType: "unambiguous",
                    filename: __pi_file,
                    compact: false,
                    comments: false,
                    babelrc: false,
                    configFile: false
                  });
                  return out.code;
                })()
                """);

            return result.AsString();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Failed to compile '{fileName}' as TypeScript: {error.Message}", error);
        }
    }

    /// <summary>The Babel version in use, for diagnostics and cache keys.</summary>
    internal static string Version => babelVersion ?? "unknown";
}
