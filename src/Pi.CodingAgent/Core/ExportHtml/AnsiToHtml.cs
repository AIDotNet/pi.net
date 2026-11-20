using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Core;

/// <summary>ANSI 转 HTML。对应 TS <c>core/export-html/ansi-to-html.ts</c>。</summary>
public static partial class AnsiToHtml
{
    private static readonly string[] AnsiColors =
    [
        "#000000", "#800000", "#008000", "#808000",
        "#000080", "#800080", "#008080", "#c0c0c0",
        "#808080", "#ff0000", "#00ff00", "#ffff00",
        "#0000ff", "#ff00ff", "#00ffff", "#ffffff",
    ];

    // SGR（ESC[...m）、其它 CSI、OSC（BEL/ST 结束）与 APC（BEL/ST 结束）。
    [GeneratedRegex(@"\x1b\[([0-9;]*)m|\x1b\[[0-?]*[ -/]*[@-~]|\x1b\]([^\x07\x1b]*)(?:\x07|\x1b\\)|\x1b_[^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex AnsiRegex();

    // OSC 8 链接载荷："8;params;url"。
    [GeneratedRegex(@"^8;[^;]*;(.*)$", RegexOptions.Singleline)]
    private static partial Regex Osc8Regex();

    [GeneratedRegex(@"^(?:https?:|mailto:)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeLinkRegex();

    [GeneratedRegex(@"[\x00-\x1f\x7f]")]
    private static partial Regex ControlCharsRegex();

    /// <summary>去掉转换器识别的全部转义序列，以及游离的 ESC 字符。</summary>
    public static string StripEscapes(string text)
        => AnsiRegex().Replace(text, "").Replace("\x1b", "");

    public static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        var lastIndex = 0;
        var current = StyleState.Default;
        var open = false;
        var openLink = false;

        void CloseSpan()
        {
            if (!open) return;
            result.Append("</span>");
            open = false;
        }

        void OpenCurrentSpan()
        {
            if (open || current.IsDefault) return;
            result.Append(OpenSpan(current));
            open = true;
        }

        foreach (Match match in AnsiRegex().Matches(text))
        {
            if (match.Index > lastIndex)
            {
                var chunk = text[lastIndex..match.Index].Replace("\x1b", "");
                if (chunk.Length > 0)
                {
                    OpenCurrentSpan();
                    result.Append(EscapeHtml(chunk));
                }
            }

            if (match.Groups[1].Success)
            {
                current = ApplyCodes(current, match.Groups[1].Value);
                CloseSpan();
            }
            else if (match.Groups[2].Success)
            {
                var hyperlink = Osc8Regex().Match(match.Groups[2].Value);
                if (hyperlink.Success)
                {
                    // 浏览器会剥掉 URL 里的 C0 控制字符，所以先剥再检查 scheme。
                    var url = ControlCharsRegex().Replace(hyperlink.Groups[1].Value.Trim(), "");
                    CloseSpan();
                    if (openLink)
                    {
                        result.Append("</a>");
                        openLink = false;
                    }

                    if (url.Length > 0 && SafeLinkRegex().IsMatch(url))
                    {
                        result.Append($"<a href=\"{EscapeHtml(url)}\">");
                        openLink = true;
                    }

                    // span 保持嵌套在链接内部。
                    OpenCurrentSpan();
                }
                // 其它 OSC 序列丢弃。
            }
            // 其它 CSI / APC 序列丢弃。

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            var chunk = text[lastIndex..].Replace("\x1b", "");
            if (chunk.Length > 0)
            {
                OpenCurrentSpan();
                result.Append(EscapeHtml(chunk));
            }
        }

        CloseSpan();
        if (openLink)
        {
            result.Append("</a>");
        }

        return result.ToString();
    }

    private static string OpenSpan(StyleState style)
    {
        var css = new List<string>();
        if (style.Bold) css.Add("font-weight:bold");
        if (style.Dim) css.Add("opacity:0.6");
        if (style.Italic) css.Add("font-style:italic");
        if (style.Underline) css.Add("text-decoration:underline");
        if (style.Fg is not null) css.Add($"color:{style.Fg}");
        if (style.Bg is not null) css.Add($"background-color:{style.Bg}");
        return css.Count == 0 ? "<span>" : $"<span style=\"{string.Join(";", css)}\">";
    }

    private static StyleState ApplyCodes(StyleState current, string codes)
    {
        if (string.IsNullOrEmpty(codes))
        {
            return StyleState.Default;
        }

        var parts = codes.Split(';', StringSplitOptions.None);
        var i = 0;
        var next = current;
        while (i < parts.Length)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                i++;
                continue;
            }

            switch (code)
            {
                case 0:
                    next = StyleState.Default;
                    break;
                case 1:
                    next = next with { Bold = true };
                    break;
                case 2:
                    next = next with { Dim = true };
                    break;
                case 3:
                    next = next with { Italic = true };
                    break;
                case 4:
                    next = next with { Underline = true };
                    break;
                case 22:
                    next = next with { Bold = false, Dim = false };
                    break;
                case 23:
                    next = next with { Italic = false };
                    break;
                case 24:
                    next = next with { Underline = false };
                    break;
                case 39:
                    next = next with { Fg = null };
                    break;
                case 49:
                    next = next with { Bg = null };
                    break;
                case 38:
                case 48:
                    var isFg = code == 38;
                    if (i + 1 < parts.Length && int.TryParse(parts[i + 1], out var mode))
                    {
                        if (mode == 5 && i + 2 < parts.Length && int.TryParse(parts[i + 2], out var indexed))
                        {
                            var hex = Color256ToHex(indexed);
                            next = isFg ? next with { Fg = hex } : next with { Bg = hex };
                            i += 2;
                        }
                        else if (mode == 2 && i + 4 < parts.Length
                                 && int.TryParse(parts[i + 2], out var r)
                                 && int.TryParse(parts[i + 3], out var g)
                                 && int.TryParse(parts[i + 4], out var b))
                        {
                            var rgb = $"rgb({Clamp(r)},{Clamp(g)},{Clamp(b)})";
                            next = isFg ? next with { Fg = rgb } : next with { Bg = rgb };
                            i += 4;
                        }
                    }

                    break;
                default:
                    if (code is >= 30 and <= 37)
                    {
                        next = next with { Fg = AnsiColors[code - 30] };
                    }
                    else if (code is >= 90 and <= 97)
                    {
                        next = next with { Fg = AnsiColors[code - 90 + 8] };
                    }
                    else if (code is >= 40 and <= 47)
                    {
                        next = next with { Bg = AnsiColors[code - 40] };
                    }
                    else if (code is >= 100 and <= 107)
                    {
                        next = next with { Bg = AnsiColors[code - 100 + 8] };
                    }

                    break;
            }

            i++;
        }

        return next;
    }

    private static string? Color256ToHex(int n)
    {
        if (n is < 0 or > 255)
        {
            return null;
        }

        if (n < 16)
        {
            return AnsiColors[n];
        }

        if (n < 232)
        {
            var idx = n - 16;
            var r = idx / 36;
            var g = idx % 36 / 6;
            var b = idx % 6;
            static string ToHex(int v)
            {
                var c = v == 0 ? 0 : 40 * v + 55;
                return c.ToString("x2", CultureInfo.InvariantCulture);
            }

            return $"#{ToHex(r)}{ToHex(g)}{ToHex(b)}";
        }

        var gray = 8 + (n - 232) * 10;
        var hex = gray.ToString("x2", CultureInfo.InvariantCulture);
        return $"#{hex}{hex}{hex}";
    }

    private static int Clamp(int value) => Math.Min(255, Math.Max(0, value));

    internal static string EscapeHtml(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#039;", StringComparison.Ordinal);

    /// <summary>对应 TS <c>ansiLinesToHtml</c>。</summary>
    public static string LinesToHtml(IEnumerable<string> lines)
        => string.Concat(lines.Select(line =>
        {
            var html = Convert(line);
            return $"<div class=\"ansi-line\">{(html.Length == 0 ? "&nbsp;" : html)}</div>";
        }));

    private readonly record struct StyleState(bool Bold, bool Dim, bool Italic, bool Underline, string? Fg, string? Bg)
    {
        public static StyleState Default { get; } = new(false, false, false, false, null, null);

        public bool IsDefault => !Bold && !Dim && !Italic && !Underline && Fg is null && Bg is null;
    }
}
