// ============================================================================
// Utilities for formatting keybinding hints in the UI.
// Port of modes/interactive/components/keybinding-hints.ts
// ============================================================================

using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Formatting options for <see cref="KeybindingHints.FormatKeyText"/>. TS <c>KeyTextFormatOptions</c>.</summary>
public sealed record KeyTextFormatOptions
{
    public bool Capitalize { get; init; }
}

/// <summary>
/// Port of <c>keybinding-hints.ts</c>. Renders the keys bound to a keybinding id as the styled hint
/// text used across the transcript and the interactive UI.
/// </summary>
public static class KeybindingHints
{
    /// <summary>
    /// Test seam for the platform check. TS reads <c>process.platform === "darwin"</c> directly; the
    /// process platform is not injectable in .NET, so tests override it to cover the macOS branch.
    /// </summary>
    internal static bool? MacOsOverride { get; set; }

    private static bool IsMacOs => MacOsOverride ?? OperatingSystem.IsMacOS();

    /// <summary>
    /// A single key part (between <c>+</c> or <c>/</c> separators). On macOS <c>alt</c> is shown as
    /// <c>option</c>; <paramref name="options"/>.Capitalize uppercases the first character.
    /// </summary>
    private static string FormatKeyPart(string part, KeyTextFormatOptions options)
    {
        var displayPart = IsMacOs && part.ToLowerInvariant() == "alt" ? "option" : part;
        if (!options.Capitalize || displayPart.Length == 0) return displayPart;
        return displayPart.Substring(0, 1).ToUpperInvariant() + displayPart.Substring(1);
    }

    /// <summary>
    /// Format a key spec such as <c>ctrl+b/left</c>. Slash separates alternative keys, plus separates
    /// the parts of one chord. TS <c>formatKeyText</c>.
    /// </summary>
    public static string FormatKeyText(string key, KeyTextFormatOptions? options = null)
    {
        options ??= new KeyTextFormatOptions();
        return string.Join("/", key.Split('/').Select(part =>
            string.Join("+", part.Split('+').Select(inner => FormatKeyPart(inner, options)))));
    }

    private static string FormatKeys(IReadOnlyList<string> keys, KeyTextFormatOptions? options = null)
    {
        if (keys.Count == 0) return "";
        return FormatKeyText(string.Join("/", keys), options);
    }

    /// <summary>The keys bound to <paramref name="keybinding"/>, formatted for display. TS <c>keyText</c>.</summary>
    public static string KeyText(string keybinding) =>
        FormatKeys(GlobalKeybindings.Get().GetKeys(keybinding));

    /// <summary>Like <see cref="KeyText"/> but with the first letter of each part capitalized.</summary>
    public static string KeyDisplayText(string keybinding) =>
        FormatKeys(GlobalKeybindings.Get().GetKeys(keybinding), new KeyTextFormatOptions { Capitalize = true });

    /// <summary>A dim key followed by a muted description. TS <c>keyHint</c>.</summary>
    public static string KeyHint(string keybinding, string description) =>
        ThemeRuntime.Current.Fg("dim", KeyText(keybinding)) + ThemeRuntime.Current.Fg("muted", $" {description}");

    /// <summary>Like <see cref="KeyHint"/> but takes a raw key spec instead of a keybinding id.</summary>
    public static string RawKeyHint(string key, string description) =>
        ThemeRuntime.Current.Fg("dim", FormatKeyText(key)) + ThemeRuntime.Current.Fg("muted", $" {description}");
}
