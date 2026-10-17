// ============================================================================
// Autocomplete helpers for InteractiveMode.
// Port of interactive-mode.ts's createFuzzyAutocompleteItems /
// getLoginProviderCompletionOptions / getLoginProviderSearchText /
// formatLoginProviderCompletionDescription / getAutocompleteSourceTag /
// prefixAutocompleteDescription / getBuiltInCommandConflictDiagnostics.
//
// These are the pieces of createBaseAutocompleteProvider that do not touch the class's own state;
// the command-list assembly itself stays in the class (4f-4m) because it reads `this.session`.
// ============================================================================

using Pi.Ai.Auth;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the autocomplete helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeAutocomplete
{
    /// <summary>
    /// The free-form <c>SourceInfo.Source</c> tags the tag logic special-cases. TS compares against
    /// these string literals directly; the port keeps them named so the branches read the same.
    /// </summary>
    private static class SourceTags
    {
        /// <summary>Built-in command or extension; untagged in autocomplete.</summary>
        public const string Builtin = "builtin";

        /// <summary>Discovered automatically from the agent/project directory.</summary>
        public const string Auto = "auto";

        /// <summary>A loose local file or directory entry.</summary>
        public const string Local = "local";

        /// <summary>Passed on the command line.</summary>
        public const string Cli = "cli";
    }

    // ---------------------------------------------------------------- fuzzy items

    /// <summary>
    /// TS <c>createFuzzyAutocompleteItems</c>: fuzzy-filter with <see cref="Fuzzy.Filter{T}"/> and
    /// return <c>null</c> (not an empty list) when nothing matches, which the editor reads as
    /// "no suggestions".
    /// </summary>
    public static IReadOnlyList<AutocompleteItem>? CreateFuzzyItems<T>(
        IReadOnlyList<T> items,
        string prefix,
        Func<T, string> getSearchText,
        Func<T, AutocompleteItem> toAutocompleteItem)
    {
        var filtered = Fuzzy.Filter(items, prefix, getSearchText);
        return filtered.Count == 0 ? null : filtered.Select(toAutocompleteItem).ToList();
    }

    // ---------------------------------------------------------------- login providers

    /// <summary>TS <c>LoginProviderCompletionOption</c>: one login entry, possibly spanning both auth types.</summary>
    public sealed record LoginProviderCompletionOption
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        /// <summary>Every auth type the provider offers, ordered oauth before api_key.</summary>
        public required IReadOnlyList<CredentialKind> AuthTypes { get; init; }

        /// <summary>TS <c>subscription?: boolean</c>; unset keeps the "subscription" label.</summary>
        public bool? Subscription { get; init; }
    }

    /// <summary>
    /// TS <c>AUTH_TYPE_ORDER</c>: <c>oauth</c> sorts before <c>api_key</c>.
    /// </summary>
    private static int AuthTypeOrder(CredentialKind authType) =>
        authType == CredentialKind.OAuth ? 0 : 1;

    /// <summary>
    /// TS <c>getLoginProviderCompletionOptions</c>: collapse the per-auth-type provider rows into one
    /// entry per provider id, union the auth types (deduplicated, oauth first) and sort by name.
    /// </summary>
    public static IReadOnlyList<LoginProviderCompletionOption> BuildLoginProviderCompletionOptions(
        IReadOnlyList<AuthSelectorProvider> providerOptions)
    {
        var byId = new Dictionary<string, (string Name, List<CredentialKind> AuthTypes, bool? Subscription)>(StringComparer.Ordinal);
        // Insertion order of the dictionary is not part of the contract; the final sort by name is.
        foreach (var provider in providerOptions)
        {
            if (byId.TryGetValue(provider.Id, out var existing))
            {
                if (!existing.AuthTypes.Contains(provider.AuthType))
                {
                    existing.AuthTypes.Add(provider.AuthType);
                    // TS sorts in place with a two-value comparator; List.Sort is unstable but the
                    // key set has at most two distinct values, so the result is unambiguous.
                    existing.AuthTypes.Sort((a, b) => AuthTypeOrder(a) - AuthTypeOrder(b));
                }

                continue;
            }

            byId[provider.Id] = (provider.Name, [provider.AuthType], provider.Subscription);
        }

        // TS uses Array.prototype.sort, which is stable: OrderBy keeps first-seen order for ties.
        return byId
            .Select(pair => new LoginProviderCompletionOption
            {
                Id = pair.Key,
                Name = pair.Value.Name,
                AuthTypes = pair.Value.AuthTypes,
                Subscription = pair.Value.Subscription,
            })
            .OrderBy(option => option.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// TS <c>getLoginProviderSearchText</c>: id, name and, for each auth type, both its wire spelling
    /// and its label.
    /// </summary>
    public static string GetLoginProviderSearchText(LoginProviderCompletionOption provider)
    {
        var authTypes = string.Join(
            " ",
            provider.AuthTypes.Select(authType =>
                $"{OAuthSelectorComponent.AuthTypeText(authType)} {OAuthSelectorComponent.FormatAuthSelectorProviderType(authType, provider.Subscription)}"));
        return $"{provider.Id} {provider.Name} {authTypes}";
    }

    /// <summary>
    /// TS <c>formatLoginProviderCompletionDescription</c>: the auth-type labels joined by <c>/</c>,
    /// prefixed with the name when it differs from the id.
    /// </summary>
    public static string FormatLoginProviderCompletionDescription(LoginProviderCompletionOption provider)
    {
        var authTypes = string.Join(
            "/",
            provider.AuthTypes.Select(authType => OAuthSelectorComponent.FormatAuthSelectorProviderType(authType, provider.Subscription)));
        return provider.Name == provider.Id ? authTypes : $"{provider.Name} · {authTypes}";
    }

    // ---------------------------------------------------------------- source tags

    /// <summary>
    /// TS <c>getAutocompleteSourceTag</c>: a compact provenance prefix (<c>u</c>/<c>p</c>/<c>t</c>,
    /// optionally followed by the package or git source). Built-in and untagged sources get none.
    /// </summary>
    public static string? GetSourceTag(SourceInfo? sourceInfo)
    {
        // Built-in extension commands are untagged, like built-in commands.
        if (sourceInfo is null || sourceInfo.Source == SourceTags.Builtin)
        {
            return null;
        }

        var scopePrefix = sourceInfo.Scope switch
        {
            SourceScope.User => "u",
            SourceScope.Project => "p",
            _ => "t",
        };

        var source = sourceInfo.Source.Trim();

        if (source is SourceTags.Auto or SourceTags.Local or SourceTags.Cli)
        {
            return scopePrefix;
        }

        if (source.StartsWith("npm:", StringComparison.Ordinal))
        {
            return $"{scopePrefix}:{source}";
        }

        var gitSource = Git.ParseGitUrl(source);
        if (gitSource is not null)
        {
            var gitRef = gitSource.Ref is not null ? $"@{gitSource.Ref}" : "";
            return $"{scopePrefix}:git:{gitSource.Host}/{gitSource.Path}{gitRef}";
        }

        return scopePrefix;
    }

    /// <summary>
    /// TS <c>prefixAutocompleteDescription</c>: <c>[tag] description</c>, or just <c>[tag]</c> when
    /// the description is empty. Returns the description unchanged when there is no tag.
    /// </summary>
    public static string? PrefixDescription(string? description, SourceInfo? sourceInfo)
    {
        var sourceTag = GetSourceTag(sourceInfo);
        if (sourceTag is null)
        {
            return description;
        }

        return string.IsNullOrEmpty(description) ? $"[{sourceTag}]" : $"[{sourceTag}] {description}";
    }

    // ---------------------------------------------------------------- built-in conflicts

    /// <summary>
    /// TS <c>getBuiltInCommandConflictDiagnostics</c>: one warning per extension command whose name
    /// shadows a built-in interactive command. When the extension also registered an alternate
    /// invocation name the warning points at it; otherwise the command is dropped from autocomplete.
    /// </summary>
    public static IReadOnlyList<ResourceDiagnostic> GetBuiltInCommandConflictDiagnostics(
        IReadOnlyList<ResolvedCommand> commands,
        IReadOnlySet<string> builtinNames)
    {
        var diagnostics = new List<ResourceDiagnostic>();
        foreach (var command in commands)
        {
            if (!builtinNames.Contains(command.Name))
            {
                continue;
            }

            var message = command.InvocationName == command.Name
                ? $"Extension command '/{command.Name}' conflicts with built-in interactive command. Skipping in autocomplete."
                : $"Extension command '/{command.Name}' conflicts with built-in interactive command. Available as '/{command.InvocationName}'.";
            diagnostics.Add(ResourceDiagnostic.Warning(message, command.SourceInfo.Path));
        }

        return diagnostics;
    }
}
