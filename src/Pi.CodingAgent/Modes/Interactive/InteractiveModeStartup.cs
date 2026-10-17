// ============================================================================
// Startup listing and changelog notice helpers for InteractiveMode.
// Port of interactive-mode.ts's showStartupNoticesIfNeeded (L836-865) and the leaf parts of
// showLoadedResources (L1764-1962): sectionHeader / formatCompactList / the condensed changelog
// line / the extension-diagnostic fold.
//
// The container assembly itself stays in the class (4f-4m) because it mutates
// `loadedResourcesContainer` and reads `this.session.resourceLoader`.
// ============================================================================

using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the startup-listing and changelog helpers of <c>interactive-mode.ts</c>.</summary>
public static partial class InteractiveModeStartup
{
    /// <summary>TS <c>/##\s+\[?(\d+\.\d+\.\d+)\]?/</c> — no <c>m</c> flag, so it matches anywhere.</summary>
    [GeneratedRegex(@"##\s+\[?(\d+\.\d+\.\d+)\]?")]
    private static partial Regex ChangelogVersionRegex();

    /// <summary>TS <c>sectionHeader(name, color)</c>: <c>[name]</c> in the given token.</summary>
    public static string FormatSectionHeader(string name, Theme theme, string color = ThemeColors.MdHeading) =>
        theme.Fg(color, $"[{name}]");

    /// <summary>
    /// TS <c>formatCompactList(items, { sort })</c>: trim, drop empties, optionally sort with
    /// <c>localeCompare</c>, join with <c>", "</c> and indent by two spaces in the dim token.
    /// </summary>
    public static string FormatCompactList(IReadOnlyList<string> items, Theme theme, bool sort = true)
    {
        var labels = items.Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        if (sort)
        {
            // TS uses localeCompare; CurrentCulture is the closest .NET equivalent (as in 4f-4a).
            labels.Sort(StringComparer.CurrentCulture);
        }

        return theme.Fg(ThemeColors.Dim, $"  {string.Join(", ", labels)}");
    }

    /// <summary>
    /// TS's version probe inside the condensed changelog branch: the first <c>## x.y.z</c> heading
    /// (brackets optional), or <see langword="null"/> when the changelog has none.
    /// </summary>
    public static string? ExtractLatestChangelogVersion(string? changelogMarkdown)
    {
        if (changelogMarkdown is null)
        {
            return null;
        }

        var match = ChangelogVersionRegex().Match(changelogMarkdown);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// TS's <c>collapseChangelog</c> branch: <c>Updated to v{version}. Use /changelog to view full
    /// changelog.</c>, where the version falls back to the running version and <c>/changelog</c> is
    /// bolded.
    /// </summary>
    public static string BuildCondensedChangelogNotice(string? changelogMarkdown, string fallbackVersion, Theme theme)
    {
        var latestVersion = ExtractLatestChangelogVersion(changelogMarkdown) ?? fallbackVersion;
        return $"Updated to v{latestVersion}. Use {theme.Bold("/changelog")} to view full changelog.";
    }

    /// <summary>
    /// TS's fold of the extension loader's <c>errors</c> / <c>warnings</c> into
    /// <see cref="ResourceDiagnostic"/>s. Command, built-in-conflict and shortcut diagnostics are
    /// appended by the caller, which owns the <c>ExtensionRunner</c>.
    /// </summary>
    public static IReadOnlyList<ResourceDiagnostic> CollectExtensionDiagnostics(
        IEnumerable<ExtensionLoadError> errors,
        IEnumerable<ExtensionLoadWarning>? warnings = null)
    {
        var diagnostics = new List<ResourceDiagnostic>();
        foreach (var error in errors)
        {
            diagnostics.Add(new ResourceDiagnostic
            {
                Type = ResourceDiagnosticType.Error,
                Message = error.Error,
                Path = error.Path,
            });
        }

        foreach (var warning in warnings ?? [])
        {
            diagnostics.Add(new ResourceDiagnostic
            {
                Type = ResourceDiagnosticType.Warning,
                Message = warning.Warning,
                Path = warning.Path,
            });
        }

        return diagnostics;
    }
}
