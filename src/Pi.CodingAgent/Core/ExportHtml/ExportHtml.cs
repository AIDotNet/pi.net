using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>自定义工具的 HTML 渲染。对应 TS <c>ToolHtmlRenderer</c>。</summary>
public interface IToolHtmlRenderer
{
    string? RenderCall(string toolCallId, string toolName, object? args);

    RenderedToolHtmlParts? RenderResult(
        string toolCallId,
        string toolName,
        object? result,
        object? details,
        bool isError);
}

/// <summary>对应 TS <c>{ collapsed?: string; expanded?: string }</c>。</summary>
public sealed record RenderedToolHtmlParts
{
    public string? Collapsed { get; init; }

    public string? Expanded { get; init; }
}

/// <summary>对应 TS <c>ExportOptions</c>。</summary>
public sealed record ExportHtmlOptions
{
    public string? OutputPath { get; init; }

    public string? ThemeName { get; init; }

    public IToolHtmlRenderer? ToolRenderer { get; init; }

    internal ConfigSeams? Seams { get; init; }
}

/// <summary>
/// 会话 HTML 导出。对应 TS <c>core/export-html/index.ts</c>。
/// 主题色解析依赖 4f <c>loadTheme</c>；本阶段用默认暗色导出色，4f 接入后替换。
/// </summary>
public static partial class ExportHtml
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>对应 TS <c>generateHtml</c>。</summary>
    public static string GenerateHtml(
        SessionManager session,
        string? themeName = null,
        IToolHtmlRenderer? toolRenderer = null,
        string? templateDir = null)
    {
        _ = themeName;
        _ = toolRenderer;
        templateDir ??= Config.GetExportTemplateDir();
        var html = File.ReadAllText(Path.Combine(templateDir, "template.html"));
        var css = File.ReadAllText(Path.Combine(templateDir, "template.css"));
        var js = File.ReadAllText(Path.Combine(templateDir, "template.js"));
        var marked = File.ReadAllText(Path.Combine(templateDir, "vendor", "marked.min.js"));
        var markedCjkFriendly = File.ReadAllText(Path.Combine(templateDir, "vendor", "marked-cjk-friendly.min.js"));
        var highlight = File.ReadAllText(Path.Combine(templateDir, "vendor", "highlight.min.js"));

        var exportColors = DeriveExportColors("#343541");
        var payload = new
        {
            header = session.GetHeader(),
            entries = session.GetEntries(),
            leafId = session.GetLeafId(),
            sessionId = session.GetSessionId(),
            cwd = session.GetCwd(),
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        css = css
            .Replace("{{THEME_VARS}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{BODY_BG}}", exportColors.PageBg, StringComparison.Ordinal)
            .Replace("{{CONTAINER_BG}}", exportColors.CardBg, StringComparison.Ordinal)
            .Replace("{{INFO_BG}}", exportColors.InfoBg, StringComparison.Ordinal);

        return html
            .Replace("{{CSS}}", css, StringComparison.Ordinal)
            .Replace("{{JS}}", js, StringComparison.Ordinal)
            .Replace("{{MARKED_JS}}", marked, StringComparison.Ordinal)
            .Replace("{{MARKED_CJK_FRIENDLY_JS}}", markedCjkFriendly, StringComparison.Ordinal)
            .Replace("{{HIGHLIGHT_JS}}", highlight, StringComparison.Ordinal)
            .Replace("{{SESSION_DATA}}", json, StringComparison.Ordinal);
    }

    /// <summary>对应 TS <c>exportSessionToHtml</c>。</summary>
    public static string ExportSessionToHtml(SessionManager session, ExportHtmlOptions? options = null)
    {
        options ??= new ExportHtmlOptions();
        var html = GenerateHtml(session, options.ThemeName, options.ToolRenderer, options.Seams is null ? null : Config.GetExportTemplateDir(options.Seams));
        var outputPath = options.OutputPath;
        if (string.IsNullOrEmpty(outputPath))
        {
            var id = session.GetSessionId();
            var name = string.IsNullOrEmpty(id) ? "session" : id;
            outputPath = Path.Combine(session.GetCwd(), $"{name}.html");
        }

        File.WriteAllText(outputPath, html, Encoding.UTF8);
        return outputPath;
    }

    /// <summary>对应 TS <c>exportFromFile</c>。</summary>
    public static string ExportFromFile(string sessionFile, ExportHtmlOptions? options = null)
    {
        var session = SessionManager.Open(sessionFile);
        return ExportSessionToHtml(session, options);
    }

    internal static (string PageBg, string CardBg, string InfoBg) DeriveExportColors(string baseColor)
    {
        var parsed = ParseColor(baseColor);
        if (parsed is null)
        {
            return ("rgb(24, 24, 30)", "rgb(30, 30, 36)", "rgb(60, 55, 40)");
        }

        var (r, g, b) = parsed.Value;
        var luminance = GetLuminance(r, g, b);
        if (luminance > 0.5)
        {
            return (
                AdjustBrightness(baseColor, 0.96),
                baseColor,
                $"rgb({Math.Min(255, r + 10)}, {Math.Min(255, g + 5)}, {Math.Max(0, b - 20)})");
        }

        return (
            AdjustBrightness(baseColor, 0.7),
            AdjustBrightness(baseColor, 0.85),
            $"rgb({Math.Min(255, r + 20)}, {Math.Min(255, g + 15)}, {b})");
    }

    private static (int R, int G, int B)? ParseColor(string color)
    {
        var hex = HexColorRegex().Match(color);
        if (hex.Success)
        {
            return (
                int.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        var rgb = RgbColorRegex().Match(color);
        if (rgb.Success)
        {
            return (
                int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return null;
    }

    private static double GetLuminance(int r, int g, int b)
    {
        static double ToLinear(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * ToLinear(r) + 0.7152 * ToLinear(g) + 0.0722 * ToLinear(b);
    }

    private static string AdjustBrightness(string color, double factor)
    {
        var parsed = ParseColor(color);
        if (parsed is null)
        {
            return color;
        }

        static int Adjust(int c, double factor) => Math.Min(255, Math.Max(0, (int)Math.Round(c * factor)));
        return $"rgb({Adjust(parsed.Value.R, factor)}, {Adjust(parsed.Value.G, factor)}, {Adjust(parsed.Value.B, factor)})";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})$")]
    private static partial System.Text.RegularExpressions.Regex HexColorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$")]
    private static partial System.Text.RegularExpressions.Regex RgbColorRegex();
}
