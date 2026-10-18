// ============================================================================
// InteractiveMode — autocomplete assembly (batch 4f-4m-5a, landed early).
//
// Port of interactive-mode.ts's createBaseAutocompleteProvider (L718-816) and
// setupAutocompleteProvider (L818-835), plus getLoginProviderOptions (L5767-5800)
// which the login command's argument completions share with the 4f-4m-5b login
// family.
//
// This batch was pulled ahead of 4f-4m-6 because ResetExtensionUI (registered as
// the runtime's before-session-invalidate hook) calls setupAutocompleteProvider:
// while the seam throws, every session replacement (fork / clone / new / resume /
// import) fails before Apply(), which blocks the 4f-4m-5a clone-command tests.
// ============================================================================

using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>TS <c>getLoginProviderOptions</c> (L5767-5800).</summary>
    /// <param name="authType">Filter by auth type; <c>null</c> offers every method the provider has.</param>
    private IReadOnlyList<AuthSelectorProvider> GetLoginProviderOptions(CredentialKind? authType = null)
    {
        var options = new List<AuthSelectorProvider>();
        foreach (var provider in Session.ModelRuntime.GetProviders())
        {
            var authStatus = Session.ModelRuntime.GetProviderAuthStatus(provider.Id);
            AuthCheck? status = authStatus.Configured
                ? new AuthCheck
                {
                    Type = Session.ModelRuntime.IsUsingOAuth(provider.Id) ? CredentialKind.OAuth : CredentialKind.ApiKey,
                    Source = authStatus.Label ?? authStatus.Source,
                }
                : null;
            // TS: `provider.auth.oauth?.isSubscription === true` — only an explicit false is observable.
            var subscription = provider.Auth?.OAuth is { } oauth && oauth.IsSubscription;
            if ((authType is null || authType == CredentialKind.OAuth) && provider.Auth?.OAuth is { } oauthMethod)
            {
                options.Add(new AuthSelectorProvider
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    AuthType = CredentialKind.OAuth,
                    Method = AuthSelectorMethod.FromOAuth(oauthMethod),
                    Status = status,
                    Subscription = subscription,
                });
            }

            if ((authType is null || authType == CredentialKind.ApiKey) && provider.Auth?.ApiKey is { } apiKeyMethod)
            {
                options.Add(new AuthSelectorProvider
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    AuthType = CredentialKind.ApiKey,
                    Method = AuthSelectorMethod.FromApiKey(apiKeyMethod),
                    Status = status,
                    Subscription = subscription,
                });
            }
        }

        // TS sorts in place with localeCompare; OrderBy is stable like Array.prototype.sort.
        return options
            .OrderBy(option => option.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>TS <c>createBaseAutocompleteProvider</c> (L718-816).</summary>
    private IAutocompleteProvider CreateBaseAutocompleteProvider()
    {
        // TS builds the list first and then assigns getArgumentCompletions onto the found entries
        // (the property is mutable there); the C# SlashCommand carries it init-only, so the three
        // argument-completing commands are created with their callback inline. The resulting list
        // and command set are identical.
        var slashCommands = SlashCommands.BuiltinSlashCommands
            .Select(command => new SlashCommand
            {
                Name = command.Name,
                Description = command.Description,
                ArgumentHint = command.ArgumentHint,
                GetArgumentCompletions = command.Name switch
                {
                    "model" => prefix => Task.FromResult(GetModelArgumentCompletions(prefix)),
                    "thinking" => prefix => Task.FromResult(GetThinkingArgumentCompletions(prefix)),
                    "login" => prefix => Task.FromResult(GetLoginArgumentCompletions(prefix)),
                    _ => null,
                },
            })
            .ToList();

        var templateCommands = Session.PromptTemplates
            .Select(template => new SlashCommand
            {
                Name = template.Name,
                Description = InteractiveModeAutocomplete.PrefixDescription(template.Description, template.SourceInfo),
                ArgumentHint = template.ArgumentHint,
            })
            .ToList();

        var builtinCommandNames = new HashSet<string>(slashCommands.Select(command => command.Name), StringComparer.Ordinal);
        var extensionCommands = Session.ExtensionRunner.GetRegisteredCommands()
            .Where(command => !builtinCommandNames.Contains(command.Name))
            .Select(command => new SlashCommand
            {
                Name = command.InvocationName,
                Description = InteractiveModeAutocomplete.PrefixDescription(command.Description, command.SourceInfo),
                GetArgumentCompletions = command.GetArgumentCompletions,
            })
            .ToList();

        _skillCommands.Clear();
        var skillCommandList = new List<SlashCommand>();
        if (SettingsManager.GetEnableSkillCommands())
        {
            foreach (var skill in Session.ResourceLoader.Skills)
            {
                var commandName = $"skill:{skill.Name}";
                _skillCommands[commandName] = skill.FilePath;
                skillCommandList.Add(new SlashCommand
                {
                    Name = commandName,
                    Description = InteractiveModeAutocomplete.PrefixDescription(skill.Description, skill.SourceInfo),
                });
            }
        }

        // The C# constructor takes (basePath, commands, fdPath); TS takes (commands, cwd, fdPath).
        return new CombinedAutocompleteProvider(
            SessionManager.GetCwd(),
            [.. slashCommands, .. templateCommands, .. extensionCommands, .. skillCommandList],
            _fdPath);
    }

    /// <summary>TS <c>modelCommand.getArgumentCompletions</c> (L728-749).</summary>
    private IReadOnlyList<AutocompleteItem>? GetModelArgumentCompletions(string prefix)
    {
        var models = Session.ScopedModels.Count > 0
            ? Session.ScopedModels.Select(scoped => scoped.Model).ToList()
            : Session.ModelRuntime.GetAvailableSnapshot().ToList();
        if (models.Count == 0)
        {
            return null;
        }

        var items = models.Select(model => new ModelSearchItem
        {
            Id = model.Id,
            Provider = model.Provider,
            Name = model.Name,
        }).ToList();
        return InteractiveModeAutocomplete.CreateFuzzyItems(
            items,
            prefix,
            ModelSearch.GetModelSearchText,
            item => new AutocompleteItem
            {
                Value = $"{item.Provider}/{item.Id}",
                Label = item.Id,
                Description = item.Provider,
            });
    }

    /// <summary>TS <c>thinkingCommand.getArgumentCompletions</c> (L754-765).</summary>
    private IReadOnlyList<AutocompleteItem>? GetThinkingArgumentCompletions(string prefix)
    {
        var levels = Session.GetAvailableThinkingLevels()
            .Select(Pi.Ai.Types.ThinkingLevelText.ToText)
            .ToList();
        return InteractiveModeAutocomplete.CreateFuzzyItems(
            levels,
            prefix,
            level => level,
            level => new AutocompleteItem { Value = level, Label = level });
    }

    /// <summary>TS <c>loginCommand.getArgumentCompletions</c> (L769-777).</summary>
    private IReadOnlyList<AutocompleteItem>? GetLoginArgumentCompletions(string prefix)
    {
        var providers = InteractiveModeAutocomplete.BuildLoginProviderCompletionOptions(
            GetLoginProviderOptions());
        return InteractiveModeAutocomplete.CreateFuzzyItems(
            providers,
            prefix,
            InteractiveModeAutocomplete.GetLoginProviderSearchText,
            provider => new AutocompleteItem
            {
                Value = provider.Id,
                Label = provider.Id,
                Description = InteractiveModeAutocomplete.FormatLoginProviderCompletionDescription(provider),
            });
    }

    /// <summary>TS <c>setupAutocompleteProvider</c> (L818-835).</summary>
    private void SetupAutocompleteProvider()
    {
        var provider = CreateBaseAutocompleteProvider();
        var triggerCharacters = new List<string>();
        foreach (var wrapProvider in _autocompleteProviderWrappers)
        {
            provider = wrapProvider(provider);
            triggerCharacters.AddRange(provider.TriggerCharacters ?? []);
        }

        if (triggerCharacters.Count > 0)
        {
            // TS assigns the deduplicated union onto the final provider; the C# interface exposes
            // TriggerCharacters get-only, so the union is applied through a decorator instead.
            provider = new TriggerCharacterOverrideProvider(provider, [.. triggerCharacters.Distinct(StringComparer.Ordinal)]);
        }

        _autocompleteProvider = provider;
        _defaultEditor.SetAutocompleteProvider(provider);
        if (!ReferenceEquals(_editor, _defaultEditor))
        {
            _editor.SetAutocompleteProvider(provider);
        }
    }

    /// <summary>
    /// Decorator that overrides <see cref="IAutocompleteProvider.TriggerCharacters"/> with the
    /// union collected across the wrapper chain (TS <c>setupAutocompleteProvider</c> L822-827).
    /// </summary>
    private sealed class TriggerCharacterOverrideProvider(
        IAutocompleteProvider inner,
        IReadOnlyList<string> triggerCharacters) : IAutocompleteProvider
    {
        public IReadOnlyList<string>? TriggerCharacters => triggerCharacters;

        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(
            string[] lines, int cursorLine, int cursorCol, AutocompleteRequest options)
            => inner.GetSuggestionsAsync(lines, cursorLine, cursorCol, options);

        public CompletionApplication ApplyCompletion(
            string[] lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix)
            => inner.ApplyCompletion(lines, cursorLine, cursorCol, item, prefix);

        public bool ShouldTriggerFileCompletion(string[] lines, int cursorLine, int cursorCol)
            => inner.ShouldTriggerFileCompletion(lines, cursorLine, cursorCol);
    }
}
