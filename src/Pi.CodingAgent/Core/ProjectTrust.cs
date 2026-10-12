using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Core;

/// <summary>How the process was started. Port of <c>AppMode</c> in <c>core/project-trust.ts</c>.</summary>
public static class AppModes
{
    public const string Interactive = "interactive";
    public const string Print = "print";
    public const string Json = "json";
    public const string Rpc = "rpc";
}

// The seam types this file used to declare (`IProjectTrustUi`, `ProjectTrustContext`,
// `IProjectTrustExtensions`, `ProjectTrustExtensionError`, `ProjectTrustEventOutcome`,
// `ProjectTrustEventEmitter`) are gone: TS declares a single `ProjectTrustContext` in
// core/extensions/types.ts and a single error type (`ExtensionError`), and both now have faithful ports
// in Pi.CodingAgent.Core.Extensions. Keeping the seam alongside them meant two records with the same TS
// name but different shapes, which shadowed each other at every use site (and collapsed TS's
// `trusted: "yes" | "no"` string into a bool one layer too early).

/// <summary>Options for <see cref="ProjectTrust.ResolveProjectTrustedAsync"/>. Port of <c>ResolveProjectTrustedOptions</c>.</summary>
public sealed record ResolveProjectTrustedOptions
{
    public required string Cwd { get; init; }

    public required ProjectTrustStore TrustStore { get; init; }

    /// <summary>An explicit <c>--trust</c> / <c>--no-trust</c> override; wins over everything else.</summary>
    public bool? TrustOverride { get; init; }

    public string? DefaultProjectTrust { get; init; }

    /// <summary>Loaded extensions whose <c>project_trust</c> handlers get the first say, when present.</summary>
    public LoadExtensionsResult? ExtensionsResult { get; init; }

    public required ProjectTrustContext ProjectTrustContext { get; init; }

    public Action<string>? OnExtensionError { get; init; }
}

/// <summary>Port of <c>core/project-trust.ts</c>.</summary>
public static class ProjectTrust
{
    /// <summary>The trust prompt text. Port of <c>formatProjectTrustPrompt</c>.</summary>
    internal static string FormatProjectTrustPrompt(string cwd) =>
        $"Trust project folder?\n{cwd}\n\nThis allows {Config.AppName} to load {Config.ConfigDirName} settings and resources, install missing project packages, and execute project extensions.";

    /// <summary>
    /// Decide whether the project at <paramref name="options"/>.<see cref="ResolveProjectTrustedOptions.Cwd"/>
    /// is trusted: explicit override, then extensions, then the stored decision, then the default policy,
    /// then the interactive prompt. Port of <c>resolveProjectTrusted</c>.
    /// </summary>
    public static async Task<bool> ResolveProjectTrustedAsync(
        ResolveProjectTrustedOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.TrustOverride is bool overrideValue) return overrideValue;
        if (!TrustManager.HasTrustRequiringProjectResources(options.Cwd)) return true;

        if (options.ExtensionsResult is { } extensionsResult)
        {
            // TS calls the imported `emitProjectTrustEvent` directly; there is no injectable hook, so the
            // port does the same now that runner.ts has landed.
            var (result, errors) = await ExtensionRunner.EmitProjectTrustEventAsync(
                extensionsResult,
                new ExtensionEvent.ProjectTrust(options.Cwd),
                options.ProjectTrustContext).ConfigureAwait(false);
            foreach (var error in errors)
            {
                options.OnExtensionError?.Invoke(
                    $"Extension \"{error.ExtensionPath}\" project_trust error: {error.Error}");
            }

            if (result is not null)
            {
                // TS: `const trusted = result.trusted === "yes"`.
                var trusted = result.Trusted == ProjectTrustDecision.Yes;
                if (result.Remember == true) options.TrustStore.Set(options.Cwd, trusted);
                return trusted;
            }
        }

        var decision = options.TrustStore.Get(options.Cwd);
        if (decision is not null) return decision.Value;

        switch (options.DefaultProjectTrust ?? "ask")
        {
            case "always":
                return true;
            case "never":
                return false;
            case "ask":
                break;
        }

        if (!options.ProjectTrustContext.HasUI) return false;

        var selected = await SelectProjectTrustOptionAsync(options.Cwd, options.ProjectTrustContext, cancellationToken)
            .ConfigureAwait(false);
        if (selected is not null)
        {
            SaveProjectTrustPromptResult(options.TrustStore, selected);
            return selected.Trusted;
        }
        return false;
    }

    private static async Task<ProjectTrustOption?> SelectProjectTrustOptionAsync(
        string cwd,
        ProjectTrustContext context,
        CancellationToken cancellationToken)
    {
        var options = TrustManager.GetProjectTrustOptions(cwd, includeSessionOnly: true);
        var labels = options.Select(option => option.Label).ToList();
        var selected = await context.Ui.Select(FormatProjectTrustPrompt(cwd), labels, null)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return selected is null ? null : options.FirstOrDefault(option => option.Label == selected);
    }

    private static void SaveProjectTrustPromptResult(ProjectTrustStore trustStore, ProjectTrustOption result)
    {
        if (result.Updates.Count > 0) trustStore.SetMany(result.Updates);
    }
}
