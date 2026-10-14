// ============================================================================
// The `system` theme — port of modes/interactive/theme/system-theme.ts
// ============================================================================
//
// pi's colors derived from the terminal's own theme. Every token belongs to a color family (its hue)
// and has contrast rules: it must reach a contrast level on the background and on the panels it is
// drawn on. Hue and saturation come from the terminal's palette color for the family's ANSI slot, or
// from the family's own hue when the terminal reports no palette. Lightness comes from the rules alone.
// Colors are built in OKHSL, whose saturation is relative to the sRGB gamut, and fade toward gray near
// black and white. A palette color never gains OKLCH chroma when it moves to another lightness, so
// pastel palettes stay pastel.
//
// A contrast level is a target-lightness curve: the OKLab lightness a token needs, given the lightness
// of the surface below it. The curves were fitted to the reference theme design. On dark backgrounds
// they aim for nearly fixed lightness; on light backgrounds the required difference grows as the
// background darkens.
//
// Depending on what the terminal reports, the theme is generated in one of three tiers:
// - background and palette: hues from the palette, lightness from the background;
// - background only: the families' own hues, lightness from the background;
// - nothing: ANSI palette indices and the default colors, which the terminal renders itself.

using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Inputs for <see cref="SystemTheme.GenerateColors"/>. TS <c>SystemThemeInput</c>.</summary>
public sealed record SystemThemeInput
{
    /// <summary>The terminal's default foreground, when it reported one.</summary>
    public RgbColor? Foreground { get; init; }

    /// <summary>The terminal's default background; without it the theme falls back to palette indices.</summary>
    public RgbColor? Background { get; init; }

    /// <summary>ANSI colors 0-15, when the terminal reported all of them.</summary>
    public IReadOnlyList<RgbColor>? Palette { get; init; }

    /// <summary>Saturation multiplier 0-1. The first frame renders in grayscale until colors arrive.</summary>
    public double? Saturation { get; init; }

    /// <summary>
    /// Appearance when the terminal did not report its background, e.g. from its light/dark report or
    /// <c>COLORFGBG</c>.
    /// </summary>
    public string? AppearanceHint { get; init; }
}

/// <summary>Generated system-theme colors. TS <c>SystemThemeColors</c>.</summary>
public sealed record SystemThemeColors
{
    /// <summary>Hex colors, ANSI palette indices, or <c>""</c> for the terminal default.</summary>
    public required IReadOnlyDictionary<string, ThemeJsonColorValue> Colors { get; init; }

    /// <summary>Foreground tokens rendered faint (SGR 2), for terminals that did not report colors.</summary>
    public required IReadOnlyList<string> Dim { get; init; }

    public required string? Appearance { get; init; }
}

/// <summary>Port of <c>system-theme.ts</c>: generate the <c>system</c> theme from the terminal's colors.</summary>
public static class SystemTheme
{
    /// <summary>The reserved theme name. TS <c>SYSTEM_THEME_NAME</c>.</summary>
    public const string SystemThemeName = "system";

    // ------------------------------------------------------------------
    // Recipe: color families and their tokens
    // ------------------------------------------------------------------

    /// <summary>A family's OKHSL hue and saturation: <c>Max</c> at mid lightness, falling toward <c>Min</c> at black and white.</summary>
    private sealed record Family(double Hue, double MinSaturation, double MaxSaturation, int Slot);

    private static readonly IReadOnlyDictionary<string, Family> Families =
        new Dictionary<string, Family>(StringComparer.Ordinal)
        {
            ["neutral"] = new(231.49, 0.02, 0.08, 8),
            ["blue"] = new(231.49, 0.1, 0.68, 4),
            ["green"] = new(158.68, 0.1, 0.76, 2),
            ["red"] = new(20, 0.1, 0.92, 1),
            ["yellow"] = new(82.36, 0.5, 1, 3),
            ["orange"] = new(52, 0.12, 0.85, 3),
            ["violet"] = new(295, 0.2, 0.6, 5),
            ["calamine"] = new(202.43, 0.1, 0.74, 6),
            ["thinkingSlate"] = new(231.49, 0.08, 0.2, 4),
            ["thinkingBlue"] = new(231.49, 0.2, 0.45, 4),
            ["thinkingPeriwinkle"] = new(263.25, 0.3, 0.6, 6),
            ["thinkingViolet"] = new(295, 0.4, 0.75, 5),
            ["thinkingMagenta"] = new(337.5, 0.5, 0.85, 13),
            ["thinkingRed"] = new(20, 0.95, 1, 1),
        };

    /// <summary>
    /// The family of every token, in TS <c>Object.keys(TOKEN_FAMILIES)</c> order. The order is
    /// load-bearing: <c>GenerateColors</c> emits its result in it.
    /// </summary>
    private static readonly (string Token, string Family)[] TokenFamilyPairs =
    [
        (ThemeBackgrounds.SelectedBg, "blue"),
        (ThemeBackgrounds.SearchMatchBg, "orange"),
        (ThemeBackgrounds.UserMessageBg, "blue"),
        (ThemeBackgrounds.CustomMessageBg, "violet"),
        (ThemeBackgrounds.ToolPendingBg, "neutral"),
        (ThemeBackgrounds.ToolSuccessBg, "green"),
        (ThemeBackgrounds.ToolErrorBg, "red"),

        (ThemeColors.Text, "neutral"),
        (ThemeColors.UserMessageText, "neutral"),
        (ThemeColors.CustomMessageText, "neutral"),
        (ThemeColors.ToolTitle, "neutral"),
        (ThemeColors.SyntaxOperator, "neutral"),
        (ThemeColors.SyntaxPunctuation, "neutral"),
        (ThemeColors.Muted, "neutral"),
        (ThemeColors.Dim, "neutral"),
        (ThemeColors.ThinkingText, "neutral"),
        (ThemeColors.ToolOutput, "neutral"),
        (ThemeColors.MdLinkUrl, "neutral"),
        (ThemeColors.MdQuote, "neutral"),
        (ThemeColors.MdQuoteBorder, "neutral"),
        (ThemeColors.MdHr, "neutral"),
        (ThemeColors.MdCodeBlockBorder, "neutral"),
        (ThemeColors.ToolDiffContext, "neutral"),
        (ThemeColors.SyntaxComment, "neutral"),
        (ThemeColors.ScrollbarTrack, "neutral"),
        (ThemeColors.ScrollbarThumb, "neutral"),
        (ThemeColors.SearchMatchText, "neutral"),
        (ThemeColors.BorderMuted, "neutral"),

        (ThemeColors.Accent, "violet"),
        (ThemeColors.BorderAccent, "violet"),
        (ThemeColors.CustomMessageLabel, "violet"),
        (ThemeColors.MdCode, "violet"),
        (ThemeColors.MdListBullet, "violet"),
        (ThemeColors.SyntaxType, "violet"),
        (ThemeColors.Border, "blue"),
        (ThemeColors.MdLink, "blue"),
        (ThemeColors.SyntaxKeyword, "blue"),
        (ThemeColors.SyntaxVariable, "calamine"),
        (ThemeColors.Success, "green"),
        (ThemeColors.MdCodeBlock, "green"),
        (ThemeColors.ToolDiffAdded, "green"),
        (ThemeColors.BashMode, "green"),
        (ThemeColors.SyntaxNumber, "green"),
        (ThemeColors.Error, "red"),
        (ThemeColors.ToolDiffRemoved, "red"),
        (ThemeColors.Warning, "yellow"),
        (ThemeColors.MdHeading, "yellow"),
        (ThemeColors.SyntaxFunction, "yellow"),
        (ThemeColors.SyntaxString, "orange"),

        (ThemeColors.ThinkingOff, "neutral"),
        (ThemeColors.ThinkingMinimal, "thinkingSlate"),
        (ThemeColors.ThinkingLow, "thinkingBlue"),
        (ThemeColors.ThinkingMedium, "thinkingPeriwinkle"),
        (ThemeColors.ThinkingHigh, "thinkingViolet"),
        (ThemeColors.ThinkingXhigh, "thinkingMagenta"),
        (ThemeColors.ThinkingMax, "thinkingRed"),
    ];

    private static readonly IReadOnlyDictionary<string, string> TokenFamilies =
        TokenFamilyPairs.ToDictionary(pair => pair.Token, pair => pair.Family, StringComparer.Ordinal);

    /// <summary>Palette slots for tokens that would otherwise share a hue with a similar token.</summary>
    private static readonly IReadOnlyDictionary<string, int> TokenSlots =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ThemeColors.SyntaxString] = 2,
            [ThemeColors.SyntaxNumber] = 5,
            [ThemeBackgrounds.SearchMatchBg] = 3,
        };

    // ------------------------------------------------------------------
    // Contrast levels and rules
    // ------------------------------------------------------------------

    /// <summary>
    /// Target-lightness curve: a polynomial in the surface's OKLab lightness giving the OKLab lightness
    /// a token needs on it. <see cref="Reachable"/> is the range of surface lightness where the level can
    /// be reached; beyond it the level is relaxed.
    /// </summary>
    private sealed record Curve(double[] Coefficients, double[] Reachable);

    private static readonly IReadOnlyDictionary<string, (Curve Dark, Curve Light)> Levels =
        new Dictionary<string, (Curve, Curve)>(StringComparer.Ordinal)
        {
            ["panel"] = (
                new Curve([0.29131, -0.39746, 2.33185, -0.85524, -1.2076, 0.86276], [0, 0.979]),
                new Curve([-3.74073, 27.94549, -78.44258, 112.6798, -79.60015, 22.11277], [0.348, 1])),
            ["track"] = (
                new Curve([0.39028, -0.23015, 0.83573, 2.43829, -4.38292, 2.01582], [0, 0.946]),
                new Curve([-5.24921, 38.37322, -107.28833, 152.10005, -106.17127, 29.18061], [0.368, 1])),
            ["thinking0"] = (
                new Curve([0.52988, -0.05809, -0.30924, 4.63567, -6.52933, 2.89108], [0, 0.873]),
                new Curve([-28.27749, 182.85284, -469.62416, 603.15916, -384.59976, 97.35147], [0.51, 1])),
            ["thinking1"] = (
                new Curve([0.55278, -0.03667, -0.45659, 4.95347, -6.90265, 3.0706], [0, 0.858]),
                new Curve([-37.10484, 235.86282, -596.62344, 754.3633, -474.00763, 118.3551], [0.535, 1])),
            ["thinking2"] = (
                new Curve([0.57486, -0.01765, -0.58987, 5.25227, -7.27175, 3.25532], [0, 0.842]),
                new Curve([-59.89653, 377.05024, -945.07843, 1182.03145, -734.96375, 181.68658], [0.556, 1])),
            ["thinking3"] = (
                new Curve([0.59621, -0.00062, -0.71148, 5.53588, -7.6392, 3.44606], [0, 0.827]),
                new Curve([-72.07122, 445.84082, -1099.57352, 1353.88793, -829.53392, 202.26164], [0.58, 1])),
            ["thinking4"] = (
                new Curve([0.61691, 0.01462, -0.82288, 5.80651, -8.00641, 3.64333], [0, 0.811]),
                new Curve([-110.14338, 674.21488, -1645.75941, 2004.32367, -1215.15899, 293.3183], [0.6, 1])),
            ["thinking5"] = (
                new Curve([0.63702, 0.02826, -0.92498, 6.06465, -8.37246, 3.84651], [0, 0.795]),
                new Curve([-175.47701, 1063.54495, -2570.70594, 3098.80776, -1860.15527, 444.76392], [0.62, 1])),
            ["thinking6"] = (
                new Curve([0.65658, 0.04044, -1.01835, 6.30989, -8.73529, 4.05439], [0, 0.779]),
                new Curve([-183.81712, 1094.70055, -2602.68539, 3088.71276, -1826.91131, 430.75931], [0.643, 1])),
            ["subtle"] = (
                new Curve([0.56762, -0.02475, -0.5383, 5.12628, -7.10931, 3.17324], [0, 0.848]),
                new Curve([-232.85459, 1376.54473, -3249.11801, 3827.91186, -2248.29472, 526.55751], [0.657, 1])),
            ["thumb"] = (
                new Curve([0.60323, 0.00278, -0.73328, 5.57157, -7.68067, 3.46933], [0, 0.823]),
                new Curve([-82.89897, 511.01355, -1255.98095, 1540.76821, -940.68087, 228.58523], [0.586, 1])),
            ["readable"] = (
                new Curve([0.66937, 0.04704, -1.06871, 6.43941, -8.9332, 4.17229], [0, 0.77]),
                new Curve([-1554.52576, 8733.56817, -19604.93507, 21977.72696, -12300.99599, 2749.81288], [0.751, 1])),
            ["emphasis"] = (
                new Curve([0.7303, 0.07695, -1.31626, 7.1681, -10.14436, 4.92846], [0, 0.712]),
                new Curve([-4948.31942, 26870.91986, -58334.48399, 63280.17197, -34298.01053, 7430.30146], [0.811, 1])),
            ["textOnPanel"] = (
                new Curve([0.86713, 0.05232, -0.89428, 4.79014, -5.5432, 1.75023], [0, 0.542]),
                new Curve([-8570.89457, 43954.60805, -90084.00702, 92220.6791, -47152.15802, 9632.27113], [0.867, 1])),
            ["text"] = (
                new Curve([0.89242, 0.02311, -0.44862, 2.34417, -0.06084, -2.63844], [0, 0.5]),
                new Curve([-2004.67048, 6664.47299, -6060.70202, -1792.61209, 5133.82359, -1939.85583], [0.894, 1])),
        };

    private static Curve CurveOf(string level, string appearance) =>
        appearance == ThemeAppearance.Dark ? Levels[level].Dark : Levels[level].Light;

    /// <summary>A surface a token can be drawn on. TS <c>Surface</c>.</summary>
    private const string Background = "background";

    private static readonly string[] ToolPanels =
    [
        ThemeBackgrounds.ToolPendingBg,
        ThemeBackgrounds.ToolSuccessBg,
        ThemeBackgrounds.ToolErrorBg,
    ];

    private static readonly string[] MessagePanels =
    [
        ThemeBackgrounds.UserMessageBg,
        ThemeBackgrounds.CustomMessageBg,
    ];

    private static readonly string[] Panels = [.. ThemeBackgrounds.All];

    private static readonly string[] Thinking =
    [
        ThemeColors.ThinkingOff,
        ThemeColors.ThinkingMinimal,
        ThemeColors.ThinkingLow,
        ThemeColors.ThinkingMedium,
        ThemeColors.ThinkingHigh,
        ThemeColors.ThinkingXhigh,
        ThemeColors.ThinkingMax,
    ];

    private static readonly string[] ThinkingLevels =
    [
        "thinking0",
        "thinking1",
        "thinking2",
        "thinking3",
        "thinking4",
        "thinking5",
        "thinking6",
    ];

    /// <summary>One token's contrast requirement on a set of surfaces.</summary>
    private sealed record Rule(string Token, IReadOnlyList<string> On, string Level);

    private static IEnumerable<Rule> Each(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> on,
        string level) =>
        tokens.Select(token => new Rule(token, on, level));

    private static readonly IReadOnlyList<Rule> Rules =
    [
        .. Each(Panels, [Background], "panel"),
        new Rule(ThemeColors.Text, [Background], "text"),
        new Rule(ThemeColors.Text, [ThemeBackgrounds.SelectedBg], "textOnPanel"),
        new Rule(ThemeColors.UserMessageText, [ThemeBackgrounds.UserMessageBg], "textOnPanel"),
        new Rule(ThemeColors.ToolTitle, ToolPanels, "textOnPanel"),
        .. Each(
            [ThemeColors.Accent, ThemeColors.Success, ThemeColors.Error, ThemeColors.Warning],
            [Background, ThemeBackgrounds.SelectedBg, .. ToolPanels],
            "readable"),
        new Rule(
            ThemeColors.Muted,
            [Background, ThemeBackgrounds.SelectedBg, ThemeBackgrounds.CustomMessageBg, .. ToolPanels],
            "readable"),
        new Rule(
            ThemeColors.Dim,
            [Background, ThemeBackgrounds.SelectedBg, ThemeBackgrounds.CustomMessageBg, .. ToolPanels],
            "subtle"),
        new Rule(ThemeColors.ThinkingText, [Background], "readable"),
        new Rule(ThemeColors.CustomMessageText, [ThemeBackgrounds.CustomMessageBg, .. ToolPanels], "readable"),
        new Rule(
            ThemeColors.CustomMessageLabel,
            [Background, ThemeBackgrounds.CustomMessageBg, ThemeBackgrounds.SelectedBg, .. ToolPanels],
            "readable"),
        new Rule(ThemeColors.ToolOutput, [Background, .. ToolPanels], "readable"),
        .. Each(
            [
                ThemeColors.MdHeading,
                ThemeColors.MdLink,
                ThemeColors.MdLinkUrl,
                ThemeColors.MdCode,
                ThemeColors.MdQuote,
                ThemeColors.MdCodeBlockBorder,
                ThemeColors.MdListBullet,
            ],
            [Background, .. MessagePanels],
            "readable"),
        new Rule(ThemeColors.MdCodeBlock, [Background, .. MessagePanels, .. ToolPanels], "readable"),
        .. Each(
            [ThemeColors.ToolDiffAdded, ThemeColors.ToolDiffRemoved, ThemeColors.ToolDiffContext],
            [Background, .. ToolPanels],
            "readable"),
        .. Each(
            [
                ThemeColors.SyntaxComment,
                ThemeColors.SyntaxKeyword,
                ThemeColors.SyntaxFunction,
                ThemeColors.SyntaxVariable,
                ThemeColors.SyntaxString,
                ThemeColors.SyntaxNumber,
                ThemeColors.SyntaxType,
                ThemeColors.SyntaxOperator,
                ThemeColors.SyntaxPunctuation,
            ],
            [Background, .. MessagePanels, .. ToolPanels],
            "readable"),
        new Rule(ThemeColors.SearchMatchText, [ThemeBackgrounds.SearchMatchBg], "readable"),
        .. Each([ThemeColors.BashMode, ThemeColors.Border, ThemeColors.BorderAccent], [Background], "readable"),
        new Rule(ThemeColors.BorderMuted, [Background], "subtle"),
        .. Each(
            [ThemeColors.MdQuoteBorder, ThemeColors.MdHr],
            [Background, .. MessagePanels, .. ToolPanels],
            "readable"),
        new Rule(ThemeColors.ScrollbarTrack, [Background], "track"),
        new Rule(ThemeColors.ScrollbarThumb, [ThemeColors.ScrollbarTrack], "thumb"),
        .. Thinking.Select((token, index) => new Rule(token, [Background], ThinkingLevels[index])),
    ];

    /// <summary>Relaxation compresses levels stronger than this one toward it before weakening all levels.</summary>
    private static readonly IReadOnlyDictionary<string, string> ReadableFloor =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ThemeAppearance.Dark] = "readable",
            [ThemeAppearance.Light] = "subtle",
        };

    /// <summary>Body text uses the terminal's foreground when it reaches this level, clearly stronger than muted.</summary>
    private const string ForegroundLevel = "emphasis";

    /// <summary>Text-level tokens that take the terminal's foreground.</summary>
    private static readonly string[] ForegroundTokens =
    [
        ThemeColors.Text,
        ThemeColors.UserMessageText,
        ThemeColors.ToolTitle,
    ];

    /// <summary>WCAG 2 contrast ratio that body text must reach on the surfaces it is drawn on.</summary>
    private const double TextMinimumWcagContrast = 4.5;

    /// <summary>Tokens in dependency order: every surface before the tokens drawn on it.</summary>
    private static readonly IReadOnlyList<string> SolveOrder = BuildSolveOrder();

    private static IReadOnlyList<string> BuildSolveOrder()
    {
        var order = new List<string>();

        void Visit(string token)
        {
            if (order.Contains(token, StringComparer.Ordinal))
            {
                return;
            }
            foreach (var rule in Rules)
            {
                if (rule.Token != token)
                {
                    continue;
                }
                foreach (var surface in rule.On)
                {
                    if (surface != Background)
                    {
                        Visit(surface);
                    }
                }
            }
            order.Add(token);
        }

        foreach (var rule in Rules)
        {
            Visit(rule.Token);
        }
        return order;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>OKLab lightness of an sRGB color, 0-1.</summary>
    private static double OklabLightness(RgbColor color) =>
        Colors.ColorToOklch(Colors.Rgb(color.R, color.G, color.B)).L;

    /// <summary>WCAG 2 relative luminance.</summary>
    public static double RelativeLuminance(RgbColor color)
    {
        static double Linear(double channel)
        {
            var value = channel / 255;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    /// <summary>WCAG 2 contrast ratio, 1-21.</summary>
    public static double WcagContrast(RgbColor first, RgbColor second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// Whether a terminal is dark or light, from its reported colors: the direction of its own foreground
    /// when text can be readable that way, otherwise dark when white text has more contrast on the
    /// background than black text.
    /// </summary>
    public static string TerminalAppearance(RgbColor background, RgbColor? foreground = null)
    {
        var white = new RgbColor(255, 255, 255);
        var black = new RgbColor(0, 0, 0);
        var whiteContrast = WcagContrast(white, background);
        var blackContrast = WcagContrast(black, background);
        if (foreground is { } fg)
        {
            var foregroundL = OklabLightness(fg);
            var backgroundL = OklabLightness(background);
            if (Math.Abs(foregroundL - backgroundL) > 0.05)
            {
                var appearance = foregroundL > backgroundL ? ThemeAppearance.Dark : ThemeAppearance.Light;
                var best = appearance == ThemeAppearance.Dark ? whiteContrast : blackContrast;
                if (best >= TextMinimumWcagContrast)
                {
                    return appearance;
                }
            }
        }
        return whiteContrast >= blackContrast ? ThemeAppearance.Dark : ThemeAppearance.Light;
    }

    // ------------------------------------------------------------------
    // Generation
    // ------------------------------------------------------------------

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private static string HexOf(RgbColor color) =>
        $"#{Channel(color.R)}{Channel(color.G)}{Channel(color.B)}";

    private static string Channel(double value) =>
        ((int)JsMath.Round(value)).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Saturation weight at a lightness: a Gaussian (center 0.5, sigma 0.25), 0 at black and white, 1 in the middle.</summary>
    private static double BellWeight(double lightness)
    {
        static double Gaussian(double x) => Math.Exp(-((x - 0.5) * (x - 0.5)) / (2 * 0.25 * 0.25));
        return (Gaussian(lightness) - Gaussian(0)) / (1 - Gaussian(0));
    }

    /// <summary>A family's saturation curve relative to its maximum: 1 at mid lightness, <c>Min/Max</c> at black and white.</summary>
    private static double SaturationCurve(Family family, double lightness)
    {
        var floor = family.MaxSaturation > 0 ? family.MinSaturation / family.MaxSaturation : 1;
        return floor + (1 - floor) * BellWeight(lightness);
    }

    /// <summary>The target lightness for a level on a surface, or <see langword="null"/> where the level cannot be reached.</summary>
    private static double? LevelTarget(string level, string appearance, double surfaceL)
    {
        var curve = CurveOf(level, appearance);
        if (surfaceL < curve.Reachable[0] || surfaceL > curve.Reachable[1])
        {
            return null;
        }
        var sum = 0.0;
        for (var power = 0; power < curve.Coefficients.Length; power++)
        {
            sum += curve.Coefficients[power] * Math.Pow(surfaceL, power);
        }
        return sum;
    }

    /// <summary>Generate the system theme's colors from the terminal's reported colors.</summary>
    public static SystemThemeColors GenerateColors(SystemThemeInput input)
    {
        var saturation = Clamp(input.Saturation ?? 1, 0, 1);
        var background = input.Background;
        var foreground = input.Foreground;
        if (background is not { } backgroundColor)
        {
            return IndexedColors(saturation, input.AppearanceHint);
        }
        var palette = input.Palette is { Count: 16 } reportedPalette
            ? reportedPalette.Select(SourceOf).ToArray()
            : null;

        var appearance = TerminalAppearance(backgroundColor, foreground);
        var lighter = appearance == ThemeAppearance.Dark;
        var extreme = lighter ? 1.0 : 0.0;
        var backgroundL = OklabLightness(backgroundColor);

        /// <summary>
        /// A token's color at an OKLab lightness. With a palette, the palette color's saturation applies at
        /// its own lightness and falls off toward black and white along the family's curve, never rising
        /// above it.
        /// </summary>
        RgbColor Paint(string token, double oklabL)
        {
            var lightness = Oklab.OklabToOkhslLightness(oklabL);
            var family = Families[TokenFamilies[token]];
            if (palette is null)
            {
                var (min, max) = (family.MinSaturation, family.MaxSaturation);
                return Colors.ColorToRgb(Colors.Okhsl(
                    family.Hue,
                    (min + (max - min) * BellWeight(lightness)) * saturation,
                    lightness));
            }
            var slot = TokenSlots.TryGetValue(token, out var overrideSlot) ? overrideSlot : family.Slot;
            return Anchored(palette[slot], family, lightness, saturation);
        }

        /// <summary>
        /// The lightness a rule needs on a surface, relaxed by <paramref name="t"/>: from 0 to 1, levels
        /// stronger than the readable floor move toward it; from 1 to 2, all levels move toward the
        /// surface itself.
        /// </summary>
        double? Target(string level, double surfaceL, double t)
        {
            var reached = LevelTarget(level, appearance, surfaceL);
            if (reached is null && t == 0)
            {
                return null;
            }
            var distance = (reached ?? extreme) - surfaceL;
            var floor = (LevelTarget(ReadableFloor[appearance], appearance, surfaceL) ?? extreme) - surfaceL;
            var compressed = Math.Abs(distance) > Math.Abs(floor)
                ? distance - (distance - floor) * Math.Min(t, 1)
                : distance;
            return surfaceL + compressed * (1 - Math.Max(0, t - 1));
        }

        /// <summary>
        /// Keep a panel light enough (or dark enough) that white (or black) text still reaches the body
        /// text minimum on it. This only matters for backgrounds near mid-gray, where it barely does on
        /// the background.
        /// </summary>
        var extremeText = lighter ? new RgbColor(255, 255, 255) : new RgbColor(0, 0, 0);
        bool Readable(RgbColor color) => WcagContrast(extremeText, color) >= TextMinimumWcagContrast;

        RgbColor LimitPanel(string token, double l)
        {
            var color = Paint(token, l);
            if (Readable(color))
            {
                return color;
            }
            var (low, high) = (backgroundL, l);
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                if (Readable(Paint(token, middle)))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
            return Paint(token, low);
        }

        Dictionary<string, RgbColor>? Solve(double t)
        {
            var colors = new Dictionary<string, RgbColor>(StringComparer.Ordinal)
            {
                [Background] = backgroundColor,
            };
            foreach (var token in SolveOrder)
            {
                var targets = new List<double>();
                foreach (var rule in Rules)
                {
                    if (rule.Token != token)
                    {
                        continue;
                    }
                    foreach (var surface in rule.On)
                    {
                        var value = Target(rule.Level, OklabLightness(colors[surface]), t);
                        if (value is null || value < 0 || value > 1)
                        {
                            return null;
                        }
                        targets.Add(value.Value);
                    }
                }
                var l = lighter ? targets.Max() : targets.Min();
                colors[token] = ThemeTokens.IsBackground(token) ? LimitPanel(token, l) : Paint(token, l);
            }
            return colors;
        }

        var relaxation = 0.0;
        var colors = Solve(0);
        if (colors is null)
        {
            // Mid-gray backgrounds cannot fit every level: relax as little as possible. Full relaxation always fits.
            var (low, high) = (0.0, 2.0);
            colors = Solve(high);
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                var attempt = Solve(middle);
                if (attempt is not null)
                {
                    (high, colors) = (middle, attempt);
                }
                else
                {
                    low = middle;
                }
            }
            relaxation = high;
        }
        var solved = colors ?? new Dictionary<string, RgbColor>(StringComparer.Ordinal);

        List<RgbColor> SurfacesOf(string token) =>
        [
            .. Rules
                .Where(rule => rule.Token == token)
                .SelectMany(rule => rule.On.Select(surface => solved.TryGetValue(surface, out var color) ? color : backgroundColor)),
        ];

        var result = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        foreach (var (token, _) in TokenFamilyPairs)
        {
            result[token] = solved.TryGetValue(token, out var color)
                ? ThemeJsonColorValue.FromText(HexOf(color))
                : ThemeJsonColorValue.FromText("");
        }

        foreach (var token in ForegroundTokens)
        {
            var surfaces = SurfacesOf(token);
            // Body text uses the terminal's own foreground where it is clearly stronger than muted text;
            // otherwise the foreground's hue at just enough lightness.
            RgbColor? text = solved.TryGetValue(token, out var solvedText) ? solvedText : null;
            if (foreground is { } foregroundColor)
            {
                var targets = surfaces.Select(surface => Target(ForegroundLevel, OklabLightness(surface), relaxation)).ToArray();
                if (targets.All(value => value is not null && value >= 0 && value <= 1))
                {
                    var neededValues = targets.Select(value => value!.Value).ToArray();
                    var needed = lighter ? neededValues.Max() : neededValues.Min();
                    var foregroundL = OklabLightness(foregroundColor);
                    if (lighter ? foregroundL >= needed : foregroundL <= needed)
                    {
                        result[token] = ThemeJsonColorValue.FromText("");
                        continue;
                    }
                    text = Anchored(
                        SourceOf(foregroundColor),
                        Families["neutral"],
                        Oklab.OklabToOkhslLightness(needed),
                        saturation);
                }
            }
            // Body text keeps at least 4.5:1 on the surfaces it is drawn on, even on relaxed mid-gray backgrounds.
            if (text is { } textColor)
            {
                result[token] = ThemeJsonColorValue.FromText(HexOf(WithTextContrast(textColor, surfaces, lighter)));
            }
        }
        return new SystemThemeColors { Colors = result, Dim = [], Appearance = appearance };
    }

    private static OkhslChannels OkhslOf(RgbColor color) => Colors.ColorToOkhsl(AsColor(color));

    /// <summary>An sRGB color reported by the terminal as the <see cref="Color"/> the color math takes.</summary>
    private static Color AsColor(RgbColor color) => Colors.Rgb(color.R, color.G, color.B);

    /// <summary>A terminal color's OKHSL channels and its OKLCH chroma.</summary>
    private readonly record struct SourceColor(double H, double S, double L, double Chroma);

    private static SourceColor SourceOf(RgbColor color)
    {
        var channels = OkhslOf(color);
        var chroma = Colors.ColorToOklch(Colors.Rgb(color.R, color.G, color.B)).C;
        return new SourceColor(channels.H, channels.S, channels.L, chroma);
    }

    /// <summary>
    /// A source color's hue at another OKHSL lightness. Its saturation applies at its own lightness and
    /// falls off toward black and white along the family's saturation curve, never rising above it.
    /// </summary>
    /// <remarks>
    /// OKHSL saturation is relative to the most chroma sRGB allows at a lightness, so the same saturation
    /// can mean more chroma elsewhere: Catppuccin Frappe's pink <c>#f4b8e4</c> (chroma 0.089) would become
    /// <c>#eb76d1</c> (0.180) at the lightness the accent needs. Chroma is therefore also capped at the
    /// source's, with the same falloff.
    /// </remarks>
    private static RgbColor Anchored(SourceColor source, Family family, double lightness, double saturation)
    {
        var anchor = SaturationCurve(family, source.L);
        var falloff = anchor > 0 ? Math.Min(1, SaturationCurve(family, lightness) / anchor) : 1;
        var color = Colors.ColorToRgb(Colors.Okhsl(source.H, source.S * falloff * saturation, lightness));
        var cap = source.Chroma * falloff * saturation;
        var channels = Colors.ColorToOklch(AsColor(color));
        return channels.C <= cap ? color : Colors.ColorToRgb(Colors.Oklch(channels.L, cap, source.H));
    }

    /// <summary>Move a text color toward white or black until it reaches the WCAG minimum on every surface.</summary>
    private static RgbColor WithTextContrast(RgbColor color, IReadOnlyList<RgbColor> surfaces, bool lighter)
    {
        bool Meets(RgbColor candidate) =>
            surfaces.All(surface => WcagContrast(candidate, surface) >= TextMinimumWcagContrast);

        if (Meets(color))
        {
            return color;
        }
        var (h, s, l) = OkhslOf(color);
        RgbColor At(double lightness) => Colors.ColorToRgb(Colors.Okhsl(h, s, lightness));
        var extreme = lighter ? 1.0 : 0.0;
        if (!Meets(At(extreme)))
        {
            return At(extreme);
        }
        var (low, high) = (l, extreme);
        for (var index = 0; index < 20; index++)
        {
            var middle = (low + high) / 2;
            if (Meets(At(middle)))
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }
        return At(high);
    }

    /// <summary>
    /// Colors for terminals that reported nothing: the terminal renders ANSI indices 0-15 and the default
    /// colors with its own theme, so they fit any background. Neutral tokens below body text are faint
    /// (SGR 2) instead of bright black, which some themes make nearly invisible. Panels have no background.
    /// </summary>
    private static SystemThemeColors IndexedColors(double saturation, string? appearance)
    {
        var colors = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        var dim = new List<string>();
        foreach (var (token, familyName) in TokenFamilyPairs)
        {
            if (ThemeTokens.IsBackground(token))
            {
                colors[token] = ThemeJsonColorValue.FromText("");
                continue;
            }
            var neutral = familyName == "neutral";
            colors[token] = !neutral && saturation > 0
                ? ThemeJsonColorValue.FromIndex(
                    TokenSlots.TryGetValue(token, out var slot) ? slot : Families[familyName].Slot)
                : ThemeJsonColorValue.FromText("");
            if (neutral && !ForegroundTokens.Contains(token, StringComparer.Ordinal))
            {
                dim.Add(token);
            }
        }
        return new SystemThemeColors { Colors = colors, Dim = dim, Appearance = appearance };
    }
}
