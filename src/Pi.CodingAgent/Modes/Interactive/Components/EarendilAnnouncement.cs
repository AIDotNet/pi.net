using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Announcement card for the Earendil acquisition (TS <c>EarendilAnnouncementComponent</c>).
/// Shows the blog link and, when the bundled asset is present, an inline image.
/// </summary>
public sealed class EarendilAnnouncementComponent : Container
{
    private const string BlogUrl = "https://mariozechner.at/posts/2026-04-08-ive-sold-out/";
    private const string ImageFilename = "clankolas.png";

    private static string? _cachedImageBase64;
    private static bool _attemptedImageLoad;

    private static string? LoadImageBase64()
    {
        if (_attemptedImageLoad)
        {
            return _cachedImageBase64;
        }

        _attemptedImageLoad = true;
        try
        {
            _cachedImageBase64 = Convert.ToBase64String(
                File.ReadAllBytes(Config.GetBundledInteractiveAssetPath(ImageFilename)));
        }
        catch
        {
            _cachedImageBase64 = null;
        }
        return _cachedImageBase64;
    }

    public EarendilAnnouncementComponent()
    {
        var theme = ThemeRuntime.Current;
        AddChild(new DynamicBorder(text => theme.Fg(ThemeColors.Accent, text)));
        AddChild(new Text(theme.Bold(theme.Fg(ThemeColors.Accent, "pi has joined Earendil")), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg(ThemeColors.Muted, "Read the blog post:"), 1, 0));
        AddChild(new Text(theme.Fg(ThemeColors.MdLink, BlogUrl), 1, 0));
        AddChild(new Spacer(1));

        var imageBase64 = LoadImageBase64();
        if (imageBase64 is not null)
        {
            AddChild(new Image(
                imageBase64,
                "image/png",
                new ImageTheme { FallbackColor = text => theme.Fg(ThemeColors.Muted, text) },
                new ImageOptions { MaxWidthCells = 56, Filename = ImageFilename }));
            AddChild(new Spacer(1));
        }

        AddChild(new DynamicBorder(text => theme.Fg(ThemeColors.Accent, text)));
    }
}
