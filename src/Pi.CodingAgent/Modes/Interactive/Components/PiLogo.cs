// ============================================================================
// The pi logo (half-block pixel art + wordmark fallback).
// Port of modes/interactive/components/pi-logo.ts
// ============================================================================

using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// The pi logo: 4 cells wide and 2 lines tall. Each cell shows two square pixels with half blocks:
/// <code>
///   coral coral coral .
///   blue  .     coral .
///   blue  blue  .     yellow
///   blue  .     .     yellow
/// </code>
/// The brand colors stay fixed across themes; they follow the terminal's color mode.
/// Port of <c>piLogoLines</c> / <c>supportsPiLogo</c> / <c>piWordmark</c>.
/// </summary>
public static class PiLogo
{
    private static readonly Color Coral = Colors.Rgb(228, 138, 122);
    private static readonly Color Blue = Colors.Rgb(79, 142, 179);
    private static readonly Color Yellow = Colors.Rgb(234, 182, 93);
    private const string Reset = "\x1b[0m";

    /// <summary>TS <c>piLogoLines()</c>: the two logo lines, colored for the current color mode.</summary>
    public static (string Top, string Bottom) LogoLines()
    {
        var mode = ThemeRuntime.Current.GetColorMode();
        var fg = (Color color) => Colors.ForegroundAnsi(color, mode);
        // The fourth cell of the top line is empty, so it is padded to the same width as the bottom line.
        var top = $"{fg(Coral)}{Colors.BackgroundAnsi(Blue, mode)}▀{Reset}{fg(Coral)}▀█{Reset} ";
        var bottom = $"{fg(Blue)}█▀{Reset} {fg(Yellow)}█{Reset}";
        return (top, bottom);
    }

    /// <summary>
    /// Whether the terminal renders the half-block logo correctly. Apple Terminal draws gaps between
    /// rows and misaligns the half blocks, so it gets the text wordmark instead.
    /// TS <c>supportsPiLogo()</c>.
    /// </summary>
    public static bool SupportsLogo() => !TerminalSession.IsAppleTerminalSession();

    /// <summary>Text fallback for the logo: "Pi" with the logo's coral and yellow. TS <c>piWordmark()</c>.</summary>
    public static string Wordmark()
    {
        var mode = ThemeRuntime.Current.GetColorMode();
        return $"{Colors.ForegroundAnsi(Coral, mode)}P{Reset}{Colors.ForegroundAnsi(Yellow, mode)}i{Reset}";
    }
}
