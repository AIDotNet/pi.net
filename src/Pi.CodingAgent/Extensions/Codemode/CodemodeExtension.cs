using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Extensions.Codemode;

/// <summary>
/// The <c>codemode</c> tool as an extension. Port of the TS
/// <c>extensions/codemode/index.ts</c>: the CLI loads it as a built-in extension; SDK users add
/// <c>createCodemodeExtension()</c> to their extension factories.
/// </summary>
/// <remarks>
/// <c>codemode</c> is registered inactive. Activate it with <c>--tools</c>, the <c>defaultTools</c>
/// setting, or <c>setActiveTools()</c>; the MCP extension activates it when MCP tools are only
/// reachable from scripts.
/// </remarks>
public static class CodemodeExtension
{
    /// <summary>TS <c>CodememodeExtensionOptions</c> (spelling: <c>CodemodeExtensionOptions</c>).</summary>
    public sealed record CodemodeExtensionOptions
    {
        /// <summary>Overrides the <c>codemode.mode</c> setting. TS <c>CodemodeMode</c>: "on" | "only".</summary>
        public string? Mode { get; init; }

        /// <summary>Overrides the <c>codemode.inlineBudget</c> setting.</summary>
        public double? InlineBudget { get; init; }

        /// <summary>Expose the model catalog and classifiers to scripts as <c>models</c>. Default: <c>true</c>.</summary>
        public bool? Models { get; init; }
    }

    private static string ReadMode(IExtensionApi pi) =>
        (pi.GetSettings().Codemode as JsonObject)?["mode"] is JsonValue mode
            && mode.GetValueKind() == JsonValueKind.String
            && mode.GetValue<string>() == "only"
            ? "only"
            : "on";

    private static double? ReadInlineBudget(IExtensionApi pi)
    {
        var budget = (pi.GetSettings().Codemode as JsonObject)?["inlineBudget"];
        if (budget is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        var number = value.GetValue<double>();
        return !double.IsFinite(number) || number < 0 ? null : number;
    }

    /// <summary>TS <c>createCodemodeExtension(options)</c>.</summary>
    public static ExtensionFactory CreateCodemodeExtension(CodemodeExtensionOptions? options = null)
    {
        options ??= new CodemodeExtensionOptions();
        return pi =>
        {
            var definition = CodemodeToolApi.CreateCodemodeToolDefinition(new CodemodeToolApi.CodemodeToolOptions
            {
                AppendEntry = (customType, data) =>
                    pi.AppendEntry(customType, JsonEntryData(data)),
                Models = options.Models ?? true,
                GetToolNamespace = name => pi.GetAllTools().FirstOrDefault(tool => tool.Name == name)?.Namespace,
                GetToolGuidelines = () => new Dictionary<string, IReadOnlyList<string>>(
                    pi.GetAllTools().Select(tool => new KeyValuePair<string, IReadOnlyList<string>>(
                        tool.Name, tool.PromptGuidelines ?? [])),
                    StringComparer.Ordinal),
                GetMode = () => options.Mode ?? ReadMode(pi),
                GetInlineBudget = () => options.InlineBudget ?? ReadInlineBudget(pi),
            });
            pi.RegisterTool(definition with { DefaultActive = false });
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// TS passes the store-entry data object by reference; C# carries it as JSON so the session
    /// entry keeps the TS wire shape (<c>{ set: {...}, delete: [...] }</c>).
    /// </summary>
    private static object? JsonEntryData(CodemodeToolApi.CodemodeStoreEntryData data) => new JsonObject
    {
        ["set"] = new JsonObject([.. data.Set.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))]),
        ["delete"] = new JsonArray([.. data.Delete.Select(key => (JsonNode?)key)]),
    };
}
