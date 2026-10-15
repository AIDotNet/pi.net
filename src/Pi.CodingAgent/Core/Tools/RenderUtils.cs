// ============================================================================
// Helpers shared by the built-in tool renderers.
// Port of core/tools/render-utils.ts
// ============================================================================

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Options for <see cref="RenderUtils.RenderToolPath"/>. TS inline options type.</summary>
public sealed record RenderToolPathOptions
{
    /// <summary>Shown when the path is the empty string.</summary>
    public string? EmptyFallback { get; init; }
}

/// <summary>
/// One raw argument read that keeps the TS distinction between <c>undefined</c> and <c>null</c>.
/// </summary>
/// <remarks>
/// The distinction is observable in the renderers: <c>args.limit !== undefined</c> is <em>true</em> for
/// an explicit JSON null, so <c>{ "limit": null }</c> renders as <c>(limit null)</c> while an omitted
/// <c>limit</c> renders nothing. JSON has no <c>undefined</c>, so a member missing from the argument
/// object is the only source of it.
/// </remarks>
public readonly record struct ToolArgRead(bool Present, object? Value)
{
    /// <summary>TS <c>undefined</c>: the member is absent from the argument object.</summary>
    public static readonly ToolArgRead Undefined = new(false, null);

    /// <summary>TS <c>v === undefined</c>.</summary>
    public bool IsUndefined => !Present;

    /// <summary>TS <c>v == null</c> (undefined or null).</summary>
    public bool IsNullish => !Present || Value is null;

    /// <summary>TS <c>typeof v === "string" ? v : undefined</c>.</summary>
    public string? AsString => RenderUtils.AsString(Value);

    /// <summary>TS <c>typeof v === "number" ? v : undefined</c>.</summary>
    public double? AsNumber => RenderUtils.AsNumber(Value);
}

/// <summary>
/// Port of <c>core/tools/render-utils.ts</c>: the path, text and argument formatting shared by the
/// built-in tool renderers.
/// </summary>
/// <remarks>
/// The TS module is written against the structural result type
/// <c>{ content: Array&lt;{type, text?, data?, mimeType?}&gt; }</c>. The port's typed entry point takes
/// the <see cref="AgentToolResult"/> the renderers already receive, and matches content blocks by
/// their CLR type, which is the C# counterpart of the TS <c>type</c> discriminator.
/// </remarks>
public static class RenderUtils
{
    /// <summary>TS <c>COLLAPSED_ARGS_CHARS</c>: the collapsed argument preview's character budget.</summary>
    public const int CollapsedArgsChars = 100;

    // ------------------------------------------------------------------ paths

    /// <summary>Replace a home-directory prefix with <c>~</c>. TS <c>shortenPath</c>.</summary>
    /// <remarks>
    /// The TS guard is <c>typeof path !== "string"</c>, so a non-string argument yields the empty
    /// string rather than the argument echoed back. <c>os.homedir()</c> can be the empty string, in
    /// which case every path starts with it and the result is <c>~</c> plus the whole path — the port
    /// keeps that behaviour rather than special-casing it.
    /// </remarks>
    public static string ShortenPath(object? path)
    {
        if (path is not string text)
        {
            return "";
        }

        var home = HomeDirectory;
        return text.StartsWith(home, StringComparison.Ordinal) ? $"~{text[home.Length..]}" : text;
    }

    /// <summary>
    /// Wrap styled text in an OSC 8 hyperlink to the resolved path, when the terminal supports them.
    /// TS <c>linkPath</c>.
    /// </summary>
    public static string LinkPath(string styledText, string rawPath, string cwd)
    {
        if (!TerminalImage.GetCapabilities().Hyperlinks)
        {
            return styledText;
        }

        var absolutePath = Paths.ResolvePath(rawPath, cwd);
        return TerminalImage.Hyperlink(styledText, PathToFileUrl(absolutePath));
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// The TS <c>str</c> coercion: strings pass through, <c>null</c>/<c>undefined</c> become the empty
    /// string, and <em>any other type becomes null</em> — the caller distinguishes "absent" from
    /// "wrong type" with that null.
    /// </summary>
    public static string? Str(object? value) => value switch
    {
        null => "",
        string text => text,
        _ => null,
    };

    /// <summary>Replace every tab with three spaces. TS <c>replaceTabs</c>.</summary>
    public static string ReplaceTabs(string text) => text.Replace("\t", "   ", StringComparison.Ordinal);

    /// <summary>Drop carriage returns, so CRLF content does not leave stray characters. TS <c>normalizeDisplayText</c>.</summary>
    public static string NormalizeDisplayText(string text) => text.Replace("\r", "", StringComparison.Ordinal);

    /// <summary>
    /// The text a tool result displays: its text blocks with ANSI stripped and binary control
    /// characters removed, joined by newlines, plus image placeholders when the terminal cannot show
    /// the images inline. TS <c>getTextOutput</c>.
    /// </summary>
    public static string GetTextOutput(AgentToolResult? result, bool showImages)
    {
        if (result is null)
        {
            return "";
        }

        var textBlocks = result.Content.OfType<TextContent>().ToList();
        var imageBlocks = result.Content.OfType<ImageContent>().ToList();

        var output = string.Join(
            "\n",
            textBlocks.Select(block => NormalizeDisplayText(
                Shell.SanitizeBinaryOutput(AnsiText.StripAnsi(block.Text ?? "")))));

        var capabilities = TerminalImage.GetCapabilities();
        if (imageBlocks.Count > 0 && (capabilities.Images == ImageProtocol.None || !showImages))
        {
            var indicators = string.Join(
                "\n",
                imageBlocks.Select(image =>
                {
                    var mimeType = image.MimeType ?? "image/unknown";
                    var dimensions = image.Data.Length > 0 && image.MimeType is not null
                        ? TerminalImage.GetImageDimensions(image.Data, image.MimeType)
                        : null;
                    return TerminalImage.ImageFallback(mimeType, dimensions);
                }));
            output = output.Length > 0 ? $"{output}\n{indicators}" : indicators;
        }

        return output;
    }

    // ------------------------------------------------------------------ tool call headers

    /// <summary>
    /// Generic tool call header: the title followed by the arguments. Collapsed, they are
    /// <c>key=value</c> pairs on the title line, cut to <see cref="CollapsedArgsChars"/>. Expanded, each
    /// is a <c>key: value</c> line below the title, with strings shown raw and continuation lines
    /// indented. TS <c>formatToolCallWithArgs</c>.
    /// </summary>
    public static string FormatToolCallWithArgs(string title, object? args, Theme theme, bool expanded)
    {
        var header = theme.Fg(ThemeColors.ToolTitle, theme.Bold(title));
        if (args is null)
        {
            return header;
        }

        var entries = Entries(args);
        if (entries.Count == 0)
        {
            return header;
        }

        if (expanded)
        {
            var lines = entries.Select(entry =>
            {
                var text = entry.Value is string raw ? raw : JsonStringify(entry.Value, indented: true);
                var normalized = NormalizeDisplayText(ReplaceTabs(text));
                return $"  {entry.Key}: {normalized.Replace("\n", "\n    ", StringComparison.Ordinal)}";
            });
            return $"{header}\n{theme.Fg(ThemeColors.Muted, string.Join("\n", lines))}";
        }

        var pairs = string.Join(" ", entries.Select(entry => $"{entry.Key}={JsonStringify(entry.Value, indented: false)}"));
        var preview = pairs.Length > CollapsedArgsChars
            ? $"{pairs[..(CollapsedArgsChars - 3)]}..."
            : pairs;
        return $"{header} {theme.Fg(ThemeColors.Muted, preview)}";
    }

    /// <summary>The styled <c>[invalid arg]</c> marker. TS <c>invalidArgText</c>.</summary>
    public static string InvalidArgText(Theme theme) => theme.Fg(ThemeColors.Error, "[invalid arg]");

    /// <summary>
    /// A tool call's path argument: the shortened, accent-styled path linked to the file, the invalid
    /// marker for a non-string argument, and <c>...</c> when there is nothing to show. TS
    /// <c>renderToolPath</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="rawPath"/> is the TS <c>string | null</c>, where null means "the argument was not
    /// a string" — a distinction <see cref="Str"/> establishes and this method reports as the invalid
    /// marker. An empty string is a different case: it falls back to
    /// <see cref="RenderToolPathOptions.EmptyFallback"/>.
    /// </remarks>
    public static string RenderToolPath(
        string? rawPath,
        Theme theme,
        string cwd,
        RenderToolPathOptions? options = null)
    {
        if (rawPath is null)
        {
            return InvalidArgText(theme);
        }

        var value = rawPath.Length > 0 ? rawPath : options?.EmptyFallback;
        if (string.IsNullOrEmpty(value))
        {
            return theme.Fg(ThemeColors.ToolOutput, "...");
        }

        return LinkPath(theme.Fg(ThemeColors.Accent, ShortenPath(value)), value, cwd);
    }

    // ------------------------------------------------------------------ argument reads

    /// <summary>
    /// Read one member of a raw tool-call argument object, preserving the TS
    /// <c>undefined</c>-versus-<c>null</c> distinction (see <see cref="ToolArgRead"/>).
    /// </summary>
    public static ToolArgRead Arg(object? args, string name)
    {
        switch (args)
        {
            case JsonObject json when json.TryGetPropertyValue(name, out var node):
                return new ToolArgRead(true, node);
            case IReadOnlyDictionary<string, object?> dictionary when dictionary.TryGetValue(name, out var raw):
                return new ToolArgRead(true, raw);
            default:
                return ToolArgRead.Undefined;
        }
    }

    /// <summary>
    /// The TS <c>a ?? b</c> over two argument reads: the first that is neither <c>undefined</c> nor
    /// <c>null</c>.
    /// </summary>
    public static ToolArgRead Coalesce(ToolArgRead first, ToolArgRead second) =>
        first.IsNullish ? second : first;

    /// <summary>The TS <c>typeof value === "string" ? value : undefined</c>.</summary>
    public static string? AsString(object? value) => value switch
    {
        string text => text,
        JsonValue json when json.GetValueKind() == JsonValueKind.String => FromJson<string>(json),
        _ => null,
    };

    /// <summary>The TS <c>typeof value === "number" ? value : undefined</c>.</summary>
    public static double? AsNumber(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        decimal m => (double)m,
        JsonValue json when json.GetValueKind() == JsonValueKind.Number => FromJson<double>(json),
        _ => null,
    };

    /// <summary>
    /// The TS <c>Array.isArray(value)</c> read of an argument member, returning the elements as a list
    /// of raw values.
    /// </summary>
    public static IReadOnlyList<object?>? AsArray(object? value) => value switch
    {
        JsonArray array => array.ToList(),
        IReadOnlyList<object?> list => list,
        _ => null,
    };

    /// <summary>
    /// A JSON-shaped view of a raw value, for the argument members whose type the renderers inspect
    /// structurally. Non-JSON CLR values pass through unchanged.
    /// </summary>
    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        IReadOnlyDictionary<string, object?> dictionary => new JsonObject(
            dictionary.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ToNode(pair.Value)))),
        System.Collections.IEnumerable enumerable => new JsonArray(
            enumerable.Cast<object?>().Select(ToNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>The TS <c>JSON.stringify(value)</c> / <c>JSON.stringify(value, null, 2)</c>.</summary>
    /// <remarks>
    /// JS has no <c>undefined</c> in JSON, so a nullish value stringifies to the literal <c>null</c>
    /// rather than to the JS <c>String(undefined)</c> fallback <c>"undefined"</c>; the TS
    /// <c>?? String(value)</c> arm is therefore unreachable from JSON-shaped arguments. The relaxed
    /// encoder matches JS, which does not escape non-ASCII characters.
    /// </remarks>
    internal static string JsonStringify(object? value, bool indented)
    {
        var node = ToNode(value);
        if (node is null)
        {
            return "null";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The JS template-literal interpolation <c>${value}</c>, i.e. <c>String(value)</c>. The renderers
    /// interpolate argument members whose guard is a presence test rather than a type test, so the
    /// argument can be <c>null</c> here and must render as <c>"null"</c> the way JS does.
    /// </summary>
    public static string JsToString(object? value) => value switch
    {
        null => "null",
        string text => text,
        bool flag => flag ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Js.NumberToString(d),
        float f => Js.NumberToString(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        JsonValue json => JsonValueToString(json),
        _ => value.ToString() ?? "",
    };

    private static string JsonValueToString(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => FromJson<string>(value) ?? "",
        JsonValueKind.Number => Js.NumberToString(FromJson<double>(value)),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.ToJsonString(),
    };

    /// <summary>The TS <c>pathToFileURL(path).href</c>.</summary>
    private static string PathToFileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>
    /// <c>os.homedir()</c>. Node falls back to the environment when the platform call fails; .NET's
    /// user-profile lookup has the same fallback shape.
    /// </summary>
    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The TS <c>Object.entries(args)</c>, or the single <c>["args", args]</c> entry the TS uses for
    /// non-object arguments (including arrays, which <c>Array.isArray</c> excludes).
    /// </summary>
    private static List<(string Key, object? Value)> Entries(object args)
    {
        switch (args)
        {
            case JsonObject json:
                return json.Select(pair => (pair.Key, (object?)pair.Value)).ToList();
            case IReadOnlyDictionary<string, object?> dictionary:
                return dictionary.Select(pair => (pair.Key, pair.Value)).ToList();
            default:
                return [("args", args)];
        }
    }

    /// <summary>
    /// Read a JSON value through its serialized form. <c>JsonValue.GetValue&lt;T&gt;</c> only succeeds
    /// when <c>T</c> matches the node's own backing type, which differs between values built with
    /// <c>JsonValue.Create</c> and values produced by <c>JsonNode.Parse</c>; going through the
    /// serialized text is correct for both representations.
    /// </summary>
    private static T? FromJson<T>(JsonValue value) =>
        JsonSerializer.Deserialize<T>(value.ToJsonString());
}
