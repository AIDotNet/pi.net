// ============================================================================
// Theme JSON validation — port of modes/interactive/theme/theme-json.ts
// ============================================================================
//
// TS keeps this out of `theme.ts` on purpose: validating user-authored theme files needs typebox
// (~17 MB of module graph), while palette lookup does not, so a presentation that only uses built-in
// themes never pays for it. `interactive-mode.ts` installs the validator; anything that does not
// install one accepts documents as-is, which is what built-in themes already do.
//
// Divergence C194: the port has no typebox. The schema is re-implemented by hand over `JsonNode`, which
// keeps the same accept/reject set, the same `Invalid theme "<label>":` header, the same sorted
// "Missing required color tokens" section and the same `  - <path>: <message>` shape for the rest. The
// per-error wording is the port's own (typebox's is not reproducible without its message table), so
// only the wording of the "Other errors" lines differs.

using System.Text;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// A theme color value: a color string (hex, OKLCH, OKHSL, a <c>vars</c> reference, or <c>""</c> for the
/// terminal default) or a 256-color index. TS <c>ColorValue</c> (<c>string | number</c>).
/// </summary>
public readonly record struct ThemeJsonColorValue
{
    private ThemeJsonColorValue(string? text, int? index)
    {
        Text = text;
        Index = index;
    }

    /// <summary>The color string, when the value was a string.</summary>
    public string? Text { get; }

    /// <summary>The 256-color index, when the value was an integer.</summary>
    public int? Index { get; }

    /// <summary>Whether the value is a string rather than an index.</summary>
    public bool IsText => Text is not null;

    public static ThemeJsonColorValue FromText(string text) => new(text, null);

    public static ThemeJsonColorValue FromIndex(int index) => new(null, index);

    public static implicit operator ThemeJsonColorValue(string text) => FromText(text);

    public static implicit operator ThemeJsonColorValue(int index) => FromIndex(index);

    public override string ToString() => Text ?? Index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>The theme's explicit HTML-export colors. TS <c>ThemeJson["export"]</c>.</summary>
public sealed record ThemeJsonExport
{
    public ThemeJsonColorValue? PageBg { get; init; }

    public ThemeJsonColorValue? CardBg { get; init; }

    public ThemeJsonColorValue? InfoBg { get; init; }
}

/// <summary>A validated theme document. TS <c>ValidatedThemeJson</c>.</summary>
public sealed record ValidatedThemeJson
{
    /// <summary>The <c>$schema</c> URL, kept only so a re-serialised theme round-trips.</summary>
    public string? Schema { get; init; }

    public required string Name { get; init; }

    /// <summary>The background the theme is designed for. Detected from the theme colors when omitted.</summary>
    public string? Appearance { get; init; }

    public IReadOnlyDictionary<string, ThemeJsonColorValue>? Vars { get; init; }

    public required IReadOnlyDictionary<string, ThemeJsonColorValue> Colors { get; init; }

    public ThemeJsonExport? Export { get; init; }
}

/// <summary>Full theme validation, installed with <c>ThemeRuntime.SetThemeJsonValidator</c>. TS <c>ThemeJsonValidator</c>.</summary>
public delegate ValidatedThemeJson ThemeJsonValidator(string label, JsonNode? json);

/// <summary>Port of <c>theme-json.ts</c>: validate one theme document.</summary>
public static class ThemeJson
{
    /// <summary>The colors a theme must declare. Derived from the token sets minus the optional ones.</summary>
    public static readonly IReadOnlyList<string> RequiredTokens =
    [
        .. ThemeColors.All.Where(token => !ThemeColors.Optional.Contains(token)),
        .. ThemeBackgrounds.All.Where(token => !ThemeBackgrounds.Optional.Contains(token)),
    ];

    /// <summary>Validate one theme document, throwing a message that names the offending tokens.</summary>
    /// <exception cref="InvalidOperationException">The document is not a valid theme.</exception>
    public static ValidatedThemeJson ValidateThemeJson(string label, JsonNode? json)
    {
        if (json is not JsonObject document)
        {
            throw InvalidTheme(label, [], ["  - /: Expected an object with a \"colors\" map."]);
        }

        // The `required` error typebox reports for the `colors` object, collected the same way: every
        // missing token goes into one sorted list rather than one error each.
        var missingColors = new SortedSet<string>(StringComparer.Ordinal);
        var otherErrors = new List<string>();

        if (document["colors"] is not JsonObject colors)
        {
            if (document.ContainsKey("colors"))
            {
                otherErrors.Add("  - /colors: Expected an object");
            }
            else
            {
                otherErrors.Add("  - /: Expected required property colors");
            }
            foreach (var token in RequiredTokens)
            {
                missingColors.Add(token);
            }
        }
        else
        {
            foreach (var token in RequiredTokens)
            {
                if (!colors.ContainsKey(token))
                {
                    missingColors.Add(token);
                }
            }
            foreach (var (token, value) in colors)
            {
                if (!ThemeTokens.IsToken(token))
                {
                    // The schema does not forbid extra properties, so an unknown key is accepted.
                    continue;
                }
                if (!IsColorValue(value))
                {
                    otherErrors.Add($"  - /colors/{token}: {ColorValueExpectation}");
                }
            }
        }

        ValidateName(document, otherErrors);
        ValidateAppearance(document, otherErrors);
        ValidateVars(document, otherErrors);
        ValidateExport(document, otherErrors);

        if (missingColors.Count > 0 || otherErrors.Count > 0)
        {
            throw InvalidTheme(label, missingColors, otherErrors);
        }

        var themeJson = ThemeJsonReader.Read(document);
        if (themeJson.Name.Contains('/'))
        {
            throw new InvalidOperationException(
                $"Invalid theme name \"{themeJson.Name}\": theme names cannot contain \"/\" because it is "
                + "reserved for automatic light/dark theme settings.");
        }
        return themeJson;
    }

    private const string ColorValueExpectation = "Expected a color string or an integer between 0 and 255";

    /// <summary>Whether <paramref name="node"/> is a <c>ColorValue</c>: a string, or an integer 0-255.</summary>
    private static bool IsColorValue(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out _) => true,
        JsonValue value when value.TryGetValue<int>(out var index) => index is >= 0 and <= 255,
        _ => false,
    };

    private static void ValidateName(JsonObject document, List<string> errors)
    {
        if (!document.ContainsKey("name"))
        {
            errors.Add("  - /: Expected required property name");
            return;
        }
        if (document["name"] is not JsonValue value || !value.TryGetValue<string>(out _))
        {
            errors.Add("  - /name: Expected a string");
        }
    }

    private static void ValidateAppearance(JsonObject document, List<string> errors)
    {
        if (!document.ContainsKey("appearance"))
        {
            return;
        }
        var appearance = document["appearance"];
        if (appearance is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || (text != ThemeAppearance.Dark && text != ThemeAppearance.Light))
        {
            errors.Add($"  - /appearance: Expected \"{ThemeAppearance.Dark}\" or \"{ThemeAppearance.Light}\"");
        }
    }

    private static void ValidateVars(JsonObject document, List<string> errors)
    {
        if (!document.ContainsKey("vars"))
        {
            return;
        }
        if (document["vars"] is not JsonObject vars)
        {
            errors.Add("  - /vars: Expected an object");
            return;
        }
        foreach (var (name, value) in vars)
        {
            if (!IsColorValue(value))
            {
                errors.Add($"  - /vars/{name}: {ColorValueExpectation}");
            }
        }
    }

    private static void ValidateExport(JsonObject document, List<string> errors)
    {
        if (!document.ContainsKey("export"))
        {
            return;
        }
        if (document["export"] is not JsonObject export)
        {
            errors.Add("  - /export: Expected an object");
            return;
        }
        foreach (var name in new[] { "pageBg", "cardBg", "infoBg" })
        {
            if (export.ContainsKey(name) && !IsColorValue(export[name]))
            {
                errors.Add($"  - /export/{name}: {ColorValueExpectation}");
            }
        }
    }

    private static InvalidOperationException InvalidTheme(
        string label,
        IEnumerable<string> missingColors,
        IReadOnlyList<string> otherErrors)
    {
        var message = new StringBuilder($"Invalid theme \"{label}\":\n");
        var missing = missingColors.ToList();
        if (missing.Count > 0)
        {
            message.Append("\nMissing required color tokens:\n");
            message.Append(string.Join('\n', missing.Select(color => $"  - {color}")));
            message.Append("\n\nPlease add these colors to your theme's \"colors\" object.");
            message.Append("\nSee the built-in themes (dark.json, light.json) for reference values.");
        }
        if (otherErrors.Count > 0)
        {
            message.Append($"\n\nOther errors:\n{string.Join('\n', otherErrors)}");
        }
        return new InvalidOperationException(message.ToString());
    }
}

/// <summary>
/// Read a parsed theme document into <see cref="ValidatedThemeJson"/> without validating it. TS casts
/// (<c>as ThemeJson</c>) here, which is what built-in themes rely on: they are shipped, not authored.
/// </summary>
internal static class ThemeJsonReader
{
    internal static ValidatedThemeJson Read(JsonNode? node)
    {
        if (node is not JsonObject document)
        {
            throw new InvalidOperationException("Expected a theme object");
        }

        return new ValidatedThemeJson
        {
            Schema = ReadString(document["$schema"]),
            Name = ReadString(document["name"]) ?? "",
            Appearance = ReadString(document["appearance"]),
            Vars = ReadColorMap(document["vars"]),
            Colors = ReadColorMap(document["colors"]) ?? new Dictionary<string, ThemeJsonColorValue>(),
            Export = ReadExport(document["export"]),
        };
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IReadOnlyDictionary<string, ThemeJsonColorValue>? ReadColorMap(JsonNode? node)
    {
        if (node is not JsonObject map)
        {
            return null;
        }
        var result = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            if (ReadColorValue(value) is { } color)
            {
                result[key] = color;
            }
        }
        return result;
    }

    private static ThemeJsonColorValue? ReadColorValue(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => ThemeJsonColorValue.FromText(text),
        JsonValue value when value.TryGetValue<int>(out var index) => ThemeJsonColorValue.FromIndex(index),
        // The cast is required: the implicit `string -> ThemeJsonColorValue` conversion would otherwise
        // make the compiler read `null` as a null string rather than a null value.
        _ => (ThemeJsonColorValue?)null,
    };

    private static ThemeJsonExport? ReadExport(JsonNode? node)
    {
        if (node is not JsonObject export)
        {
            return null;
        }
        return new ThemeJsonExport
        {
            PageBg = ReadColorValue(export["pageBg"]),
            CardBg = ReadColorValue(export["cardBg"]),
            InfoBg = ReadColorValue(export["infoBg"]),
        };
    }
}
