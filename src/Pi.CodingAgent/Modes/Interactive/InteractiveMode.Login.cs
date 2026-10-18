// ============================================================================
// InteractiveMode — login / logout authentication family, batch 4f-4m-5b.
//
// Port of interactive-mode.ts L5767-6397:
//   getLoginProviderOptions (already landed with the autocomplete batch —
//   InteractiveMode.Autocomplete.cs), getLogoutProviderOptions (L5802-5815),
//   findLoginProviderOptions (L5817-5828), handleLoginCommand (L5830-5851),
//   startProviderLogin (L5853-5862), showLoginAuthTypeSelector (L5864-5938),
//   showLoginProviderSelector (L5940-5983), showOAuthSelector (L5985-6043),
//   completeProviderAuthentication (L6045-6139), showAmbientAuthDialog
//   (L6141-6169), showApiKeyLoginDialog (L6171-6220), showAuthSelect
//   (L6222-6255), showAuthPrompt (L6257-6279), notifyAuthDialog (L6281-6292),
//   loginProvider (L6294-6309), showLoginDialog (L6311-6344),
//   offerRadiusMcpServer (L6346-6397), plus the /login and /logout dispatch
//   branches (L3273-3283).
//
// The submit if-chain that calls HandleLoginCommandAsync / HandleLogoutCommand
// lands with batch 4f-4m-6.
// ============================================================================

using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Compat;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>TS <c>getLogoutProviderOptions</c> (L5802-5815): the stored credentials, read under a 15s timeout.</summary>
    private async Task<IReadOnlyList<AuthSelectorProvider>> GetLogoutProviderOptionsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var credentials = await Session.ModelRuntime.ListCredentialsAsync(timeout.Token).ConfigureAwait(true);
        var options = new List<AuthSelectorProvider>();
        foreach (var (providerId, type) in credentials)
        {
            var provider = Session.ModelRuntime.GetProvider(providerId);
            options.Add(new AuthSelectorProvider
            {
                Id = providerId,
                Name = provider?.Name ?? providerId,
                AuthType = type,
                Status = new AuthCheck { Type = type, Source = "stored credential" },
                // TS: `provider?.auth.oauth?.isSubscription === true`.
                Subscription = provider?.Auth?.OAuth is { } oauth && oauth.IsSubscription,
            });
        }

        // Same collation as getLoginProviderOptions: stable OrderBy over CurrentCulture.
        return options
            .OrderBy(option => option.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>TS <c>findLoginProviderOptions</c> (L5817-5828).</summary>
    private IReadOnlyList<AuthSelectorProvider> FindLoginProviderOptions(string providerRef)
    {
        var normalizedProviderRef = providerRef.Trim().ToLowerInvariant();
        if (normalizedProviderRef.Length == 0)
        {
            return [];
        }

        var all = GetLoginProviderOptions();
        var matches = new List<AuthSelectorProvider>();
        foreach (var provider in all)
        {
            if (provider.Id.ToLowerInvariant() == normalizedProviderRef
                || provider.Name.ToLowerInvariant() == normalizedProviderRef)
            {
                matches.Add(provider);
            }
        }

        return matches;
    }

    /// <summary>TS <c>handleLoginCommand</c> (L5830-5851).</summary>
    internal async Task HandleLoginCommandAsync(string? providerRef = null)
    {
        if (string.IsNullOrEmpty(providerRef))
        {
            ShowLoginAuthTypeSelector();
            return;
        }

        var providerOptions = FindLoginProviderOptions(providerRef);
        if (providerOptions.Count == 1)
        {
            await StartProviderLoginAsync(providerOptions[0]).ConfigureAwait(true);
            return;
        }

        if (providerOptions.Count > 1)
        {
            var providerIds = new HashSet<string>(
                providerOptions.Select(provider => provider.Id), StringComparer.Ordinal);
            if (providerIds.Count == 1)
            {
                ShowLoginAuthTypeSelector(providerOptions);
                return;
            }
        }

        ShowLoginProviderSelector(initialSearchInput: providerRef);
    }

    /// <summary>
    /// TS <c>startProviderLogin</c> (L5853-5862). <paramref name="onBack"/> reopens the selector
    /// the login was started from when the user cancels it.
    /// </summary>
    private async Task StartProviderLoginAsync(AuthSelectorProvider providerOption, Action? onBack = null)
    {
        if (providerOption.AuthType == CredentialKind.OAuth)
        {
            await ShowLoginDialogAsync(providerOption.Id, providerOption.Name, onBack).ConfigureAwait(true);
        }
        else if (providerOption.Method?.ApiKey is { HasLogin: true })
        {
            await ShowApiKeyLoginDialogAsync(providerOption.Id, providerOption.Name, onBack).ConfigureAwait(true);
        }
        else
        {
            ShowAmbientAuthDialog(providerOption, onBack);
        }
    }

    /// <summary>TS <c>showLoginAuthTypeSelector</c> (L5864-5938).</summary>
    private void ShowLoginAuthTypeSelector(IReadOnlyList<AuthSelectorProvider>? providerOptions = null)
    {
        // The top-level selector offers Radius directly, as its last option.
        var radiusOption = providerOptions is null
            ? GetLoginProviderOptions(CredentialKind.OAuth)
                .FirstOrDefault(provider => provider.Id == Radius.ProviderId)
            : null;
        var radiusText = radiusOption is not null ? $"Sign in with {radiusOption.Name}" : null;
        var radiusLabel = radiusOption is not null
            ? $"{radiusText}{OAuthSelectorComponent.FormatAuthSelectorProviderStatus(radiusOption)}"
            : null;
        var oauthProvider = providerOptions?.FirstOrDefault(provider => provider.AuthType == CredentialKind.OAuth);
        // TS: `"loginLabel" in oauthProvider.method` — only the OAuth branch carries it.
        var oauthLoginLabel = oauthProvider?.Method?.OAuth?.LoginLabel;
        var subscriptionLabel = oauthLoginLabel ?? "Sign in with an account";
        const string apiKeyLabel = "Sign in with an API key";
        var availableAuthTypes = providerOptions is not null
            ? providerOptions.Select(provider => provider.AuthType).ToHashSet()
            : new HashSet<CredentialKind> { CredentialKind.OAuth, CredentialKind.ApiKey };
        var options = new List<string>();
        if (availableAuthTypes.Contains(CredentialKind.OAuth))
        {
            options.Add(subscriptionLabel);
        }

        if (availableAuthTypes.Contains(CredentialKind.ApiKey))
        {
            options.Add(apiKeyLabel);
        }

        if (radiusLabel is not null)
        {
            options.Add(radiusLabel);
        }

        if (options.Count == 0)
        {
            ShowStatus("No login methods available.");
            return;
        }

        if (providerOptions is not null && options.Count == 1)
        {
            var onlyOption = providerOptions[0];
            if (onlyOption is not null)
            {
                _ = StartProviderLoginAsync(onlyOption);
            }

            return;
        }

        var title = providerOptions is { Count: > 0 }
            ? $"Select authentication method for {providerOptions[0].Name}:"
            : "Select authentication method:";
        ShowSelector(done =>
        {
            void OnSelect(string option)
            {
                done();
                if (radiusOption is not null && option == radiusLabel)
                {
                    _ = StartProviderLoginAsync(radiusOption, () => ShowLoginAuthTypeSelector());
                    return;
                }

                var authType = option == subscriptionLabel ? CredentialKind.OAuth : CredentialKind.ApiKey;
                if (providerOptions is not null)
                {
                    var providerOption = providerOptions.FirstOrDefault(provider => provider.AuthType == authType);
                    if (providerOption is not null)
                    {
                        _ = StartProviderLoginAsync(providerOption, () => ShowLoginAuthTypeSelector(providerOptions));
                    }

                    return;
                }

                ShowLoginProviderSelector(authType);
            }

            void OnCancel()
            {
                done();
                _ui.RequestRender();
            }

            var selector = radiusLabel is not null && radiusText is not null
                ? RadiusLoginSelector.CreateLoginMenuSelector(
                    _ui, title, options, new RadiusOption(radiusLabel, radiusText), OnSelect, OnCancel)
                : new ExtensionSelectorComponent(title, options, OnSelect, OnCancel);
            // TS disposes the selector (it owns the Radius shimmer timer); the base
            // ExtensionSelectorComponent.Dispose stops the countdown timer.
            return (selector, selector, () => selector.Dispose());
        });
    }

    /// <summary>TS <c>showLoginProviderSelector</c> (L5940-5983).</summary>
    private void ShowLoginProviderSelector(
        CredentialKind? authType = null, string? initialSearchInput = null)
    {
        var providerOptions = GetLoginProviderOptions(authType);
        if (providerOptions.Count == 0)
        {
            var message = authType == CredentialKind.OAuth
                ? "No account providers available."
                : authType == CredentialKind.ApiKey
                    ? "No API key providers available."
                    : "No login providers available.";
            ShowStatus(message);
            return;
        }

        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent(
                AuthSelectorMode.Login,
                providerOptions,
                (providerId, selectedAuthType) =>
                {
                    done();
                    var providerOption = providerOptions.FirstOrDefault(
                        provider => provider.Id == providerId && provider.AuthType == selectedAuthType);
                    if (providerOption is null)
                    {
                        return;
                    }

                    // TS awaits inside the selector's async onSelect; the port drops the task
                    // the same way (the component's callback is a plain Action).
                    _ = StartProviderLoginAsync(
                        providerOption, () => ShowLoginProviderSelector(authType, initialSearchInput));
                },
                () =>
                {
                    done();
                    if (authType is not null)
                    {
                        ShowLoginAuthTypeSelector();
                    }
                    else
                    {
                        _ui.RequestRender();
                    }
                },
                initialSearchInput);
            return (selector, selector, null);
        });
    }

    /// <summary>TS <c>showOAuthSelector</c> (L5985-6043).</summary>
    private async Task ShowOAuthSelectorAsync(AuthSelectorMode mode)
    {
        if (mode == AuthSelectorMode.Login)
        {
            ShowLoginAuthTypeSelector();
            return;
        }

        IReadOnlyList<AuthSelectorProvider> providerOptions;
        try
        {
            providerOptions = await GetLogoutProviderOptionsAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ShowError($"Could not read stored credentials: {error.Message}");
            return;
        }

        if (providerOptions.Count == 0)
        {
            ShowStatus(
                "No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");
            return;
        }

        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent(
                mode,
                providerOptions,
                (providerId, _selectedAuthType) =>
                {
                    done();
                    var providerOption = providerOptions.FirstOrDefault(provider => provider.Id == providerId);
                    if (providerOption is null)
                    {
                        return;
                    }

                    _ = LogoutProviderAsync(providerOption);
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });
            return (selector, selector, null);
        });
    }

    /// <summary>The logout branch of TS <c>showOAuthSelector</c>'s onSelect (L6009-6035).</summary>
    private async Task LogoutProviderAsync(AuthSelectorProvider providerOption)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Session.ModelRuntime.LogoutAsync(providerOption.Id, timeout.Token).ConfigureAwait(true);
            UpdateAvailableProviderCount();
            var message = providerOption.AuthType == CredentialKind.OAuth
                ? $"Logged out of {providerOption.Name}"
                : $"Removed stored API key for {providerOption.Name}. Environment variables and models.json config are unchanged.";
            ShowStatus(message);
        }
        catch (Exception error)
        {
            var message = error.Message;
            ShowError(error is CredentialSynchronizationError
                ? $"Credentials removed for {providerOption.Name}, but local model state could not be synchronized: {message}"
                : $"Logout failed: {message}");
        }
    }

    /// <summary>TS <c>completeProviderAuthentication</c> (L6045-6139).</summary>
    private async Task CompleteProviderAuthenticationAsync(
        string providerId, string providerName, CredentialKind authType, ModelSpec? previousModel)
    {
        var actionLabel = authType == CredentialKind.OAuth
            ? $"Logged in to {providerName}"
            : $"Saved API key for {providerName}";
        var session = Session;
        var defaultModelId = ModelResolver.GetDefaultModelId(providerId);

        // Dynamic catalogs may be empty until the first authenticated network refresh.
        var deferSelection =
            InteractiveModeRender.IsUnknownModel(previousModel) &&
            ModelResolver.HasDefaultModelProvider(providerId) &&
            !session.ModelRuntime.GetAvailableSnapshot()
                .Any(model => model.Provider == providerId && model.Id == defaultModelId);

        async Task FinishAuthenticationAsync()
        {
            ModelSpec? selectedModel = null;
            string? selectionError = null;
            if (InteractiveModeRender.IsUnknownModel(previousModel))
            {
                var availableModels = session.ModelRuntime.GetAvailableSnapshot();
                var providerModels = availableModels
                    .Where(model => model.Provider == providerId)
                    .ToList();
                // Matches LLAMA_PROVIDER_ID from extensions/llama/provider.ts; kept inline to avoid
                // coupling interactive mode to the built-in extension.
                if (providerId == "llama.cpp")
                {
                    selectionError = InteractiveModeSupport.LlamaCppPostLoginGuidance(actionLabel, providerModels.Count);
                }
                else if (!ModelResolver.HasDefaultModelProvider(providerId))
                {
                    selectionError = $"{actionLabel}, but no default model is configured for provider \"{providerId}\". Use /model to select a model.";
                }
                else if (providerModels.Count == 0)
                {
                    selectionError = $"{actionLabel}, but no models are available for that provider. Use /model to select a model.";
                }
                else
                {
                    // Radius catalogs vary by account; prefer balanced, then use catalog order.
                    selectedModel = providerModels.FirstOrDefault(model => model.Id == defaultModelId)
                        ?? (providerId == Radius.ProviderId ? providerModels[0] : null);
                    if (selectedModel is null)
                    {
                        selectionError = $"{actionLabel}, but its default model \"{defaultModelId}\" is not available. Use /model to select a model.";
                    }
                    else
                    {
                        try
                        {
                            await session.SetModelAsync(
                                selectedModel, new ModelMutationOptions { Persist = true }).ConfigureAwait(true);
                        }
                        catch (Exception error)
                        {
                            selectedModel = null;
                            selectionError = $"{actionLabel}, but selecting its default model failed: {error.Message}. Use /model to select a model.";
                        }
                    }
                }
            }

            UpdateAvailableProviderCount();
            _footer.Invalidate();
            UpdateEditorBorderColor();
            if (selectedModel is not null)
            {
                ShowStatus($"{actionLabel}. Selected {selectedModel.Id}. Credentials saved to {Config.GetAuthPath()}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(selectedModel);
            }
            else
            {
                ShowStatus($"{actionLabel}. Credentials saved to {Config.GetAuthPath()}");
                if (selectionError is not null)
                {
                    ShowError(selectionError);
                }
                else
                {
                    _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();
                }
            }
        }

        if (deferSelection)
        {
            ShowStatus($"{actionLabel}. Credentials saved to {Config.GetAuthPath()}. Refreshing model catalog…");
        }
        else
        {
            await FinishAuthenticationAsync().ConfigureAwait(true);
        }

        // TS aborts the refresh after 15s and clears the timeout in `finally`; the CTS disposal on
        // completion is the port's equivalent (a fired cancel has no observers left).
        using var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = RefreshCatalogAfterLoginAsync(
            session, providerId, actionLabel, deferSelection, previousModel, FinishAuthenticationAsync,
            refreshTimeout.Token);
    }

    /// <summary>The fire-and-forget tail of TS <c>completeProviderAuthentication</c> (L6115-6138).</summary>
    private async Task RefreshCatalogAfterLoginAsync(
        AgentSession session,
        string providerId,
        string actionLabel,
        bool deferSelection,
        ModelSpec? previousModel,
        Func<Task> finishAuthentication,
        CancellationToken signal)
    {
        try
        {
            var result = await session.ModelRuntime.RefreshAsync(new ModelsRefreshOptions
            {
                Providers = [providerId],
                Signal = signal,
            }).ConfigureAwait(true);
            if (result.Aborted)
            {
                ShowWarning($"{actionLabel}, but its model catalog refresh timed out; using cached models.");
            }
            else if (result.Errors.Count > 0)
            {
                ShowWarning($"{actionLabel}, but its model catalog could not be refreshed; using cached models.");
            }

            // Do not replace a model or session selected while the refresh was running. TS compares
            // with `===` (reference equality), so the port uses ReferenceEquals rather than the
            // record's structural equality.
            if (deferSelection && ReferenceEquals(Session, session) && ReferenceEquals(session.Model, previousModel))
            {
                await finishAuthentication().ConfigureAwait(true);
            }

            UpdateAvailableProviderCount();
            _footer.Invalidate();
            _ui.RequestRender();
        }
        catch (Exception error)
        {
            ShowWarning(
                $"{actionLabel}, but its model catalog could not be refreshed: {error.Message}");
        }
    }

    /// <summary>TS <c>showAmbientAuthDialog</c> (L6141-6169).</summary>
    private void ShowAmbientAuthDialog(AuthSelectorProvider providerOption, Action? onBack = null)
    {
        void RestoreEditor()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }

        var dialog = new LoginDialogComponent(
            _ui,
            providerOption.Id,
            (_, _) =>
            {
                RestoreEditor();
                onBack?.Invoke();
            },
            providerOption.Name,
            $"{providerOption.Name} setup");
        dialog.ShowInfo(
            $"{providerOption.Method?.Name ?? "Authentication"} is configured outside {Config.AppName}.",
            [],
            true);

        _editorContainer.Clear();
        _editorContainer.AddChild(dialog);
        _ui.SetFocus(dialog);
        _ui.RequestRender();
    }

    /// <summary>TS <c>showApiKeyLoginDialog</c> (L6171-6220).</summary>
    private async Task ShowApiKeyLoginDialogAsync(string providerId, string providerName, Action? onBack = null)
    {
        var previousModel = Session.Model;

        var dialog = new LoginDialogComponent(
            _ui,
            providerId,
            (_, _) =>
            {
                // Completion handled below
            },
            providerName);

        if (providerId == "amazon-bedrock")
        {
            var theme = ThemeRuntime.Current;
            dialog.ShowDetails(
            [
                theme.Fg("text", "You can also use an AWS profile, IAM keys, or role-based credentials."),
                theme.Fg("muted", "See:"),
                theme.Fg("accent", $"  {NodePath.Join(Config.GetDocsPath(), "providers.md")}"),
            ]);
        }

        _editorContainer.Clear();
        _editorContainer.AddChild(dialog);
        _ui.SetFocus(dialog);
        _ui.RequestRender();

        void RestoreEditor()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }

        try
        {
            await LoginProviderAsync(dialog, providerId, CredentialKind.ApiKey).ConfigureAwait(true);
            RestoreEditor();
            await CompleteProviderAuthenticationAsync(
                providerId, providerName, CredentialKind.ApiKey, previousModel).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            RestoreEditor();
            var errorMsg = error.Message;
            if (error is CredentialSynchronizationError)
            {
                ShowError(
                    $"Saved API key for {providerName}, but local model state could not be synchronized: {errorMsg}");
            }
            else if (errorMsg == "Login cancelled")
            {
                onBack?.Invoke();
            }
            else
            {
                ShowError($"Failed to save API key for {providerName}: {errorMsg}");
            }
        }
    }

    /// <summary>TS <c>showAuthSelect</c> (L6222-6255).</summary>
    private Task<string> ShowAuthSelectAsync(LoginDialogComponent dialog, AuthPrompt.Select prompt, string providerId)
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void RestoreDialog()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(dialog);
            _ui.SetFocus(dialog);
            _ui.RequestRender();
        }

        var labels = prompt.Options.Select(option => option.Label).ToList();
        var selector = new ExtensionSelectorComponent(
            prompt.Message,
            labels,
            optionLabel =>
            {
                RestoreDialog();
                var id = prompt.Options.FirstOrDefault(option => option.Label == optionLabel)?.Id;
                // TS: `if (id) resolve(id); else reject(…)` — an empty id is falsy there too.
                if (!string.IsNullOrEmpty(id))
                {
                    response.TrySetResult(id);
                }
                else
                {
                    response.TrySetException(new InvalidOperationException("Login cancelled"));
                }
            },
            () =>
            {
                RestoreDialog();
                response.TrySetException(new InvalidOperationException("Login cancelled"));
            },
            new ExtensionSelectorOptions
            {
                Description = providerId == Radius.ProviderId
                    ? InteractiveModeSupport.RadiusLoginIntro
                    : null,
            });
        _editorContainer.Clear();
        _editorContainer.AddChild(selector);
        _ui.SetFocus(selector);
        _ui.RequestRender();
        return response.Task;
    }

    /// <summary>TS <c>showAuthPrompt</c> (L6257-6279).</summary>
    private async Task<string> ShowAuthPromptAsync(
        LoginDialogComponent dialog, AuthPrompt prompt, string providerId, CancellationToken cancellationToken)
    {
        // TS: select → showAuthSelect, manual_code → showManualInput, else (text/secret) → showPrompt.
        var response = prompt switch
        {
            AuthPrompt.Select select => ShowAuthSelectAsync(dialog, select, providerId),
            AuthPrompt.ManualCode manualCode => dialog.ShowManualInputAsync(manualCode.Message),
            AuthPrompt.Text text => dialog.ShowPromptAsync(text.Message, text.Placeholder),
            AuthPrompt.Secret secret => dialog.ShowPromptAsync(secret.Message, secret.Placeholder),
            _ => throw new InvalidOperationException($"Unsupported auth prompt: {prompt.GetType().Name}"),
        };
        if (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Login cancelled");
        }

        // TS races the response against prompt.signal and rejects with "Login cancelled" on abort.
        // The port carries the per-prompt signal as the interaction's cancellation token (see
        // IAuthInteraction); the overall flow's signal is the dialog's.
        var aborted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<string>)state!).TrySetException(
                new InvalidOperationException("Login cancelled")),
            aborted);
        var completed = await Task.WhenAny(response, aborted.Task).ConfigureAwait(true);
        return await completed.ConfigureAwait(true);
    }

    /// <summary>TS <c>notifyAuthDialog</c> (L6281-6292).</summary>
    private void NotifyAuthDialog(LoginDialogComponent dialog, AuthEvent @event)
    {
        if (@event is AuthEvent.AuthUrl authUrl)
        {
            dialog.ShowAuth(authUrl.Url, authUrl.Instructions);
        }
        else if (@event is AuthEvent.DeviceCode deviceCode)
        {
            dialog.ShowDeviceCode(new OAuthDeviceCodeInfo(
                deviceCode.UserCode,
                deviceCode.VerificationUri,
                deviceCode.IntervalSeconds,
                deviceCode.ExpiresInSeconds));
            dialog.ShowWaiting("Waiting for authentication...");
        }
        else if (@event is AuthEvent.Info info)
        {
            dialog.ShowInfo(info.Message, info.Links);
        }
        else if (@event is AuthEvent.Progress progress)
        {
            dialog.ShowProgress(progress.Message);
        }
    }

    /// <summary>TS <c>loginProvider</c> (L6294-6309).</summary>
    private async Task LoginProviderAsync(LoginDialogComponent dialog, string providerId, CredentialKind method)
    {
        var interaction = new DialogAuthInteraction(dialog, this, providerId);
        await Session.ModelRuntime.LoginAsync(
            providerId,
            method,
            new ProviderAuthInteraction(interaction, dialog.Signal),
            new LoginOptions { GetDeviceId = () => SettingsManager.GetOrCreateDeviceId() }).ConfigureAwait(true);
    }

    /// <summary>
    /// Routes the auth flow's prompt/notify callbacks into the login dialog — the TS inline
    /// <c>{ signal, prompt, notify }</c> object passed to <c>modelRuntime.login</c>.
    /// </summary>
    private sealed class DialogAuthInteraction(LoginDialogComponent dialog, InteractiveMode mode, string providerId)
        : IAuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => mode.ShowAuthPromptAsync(dialog, prompt, providerId, cancellationToken);

        public void Notify(AuthEvent @event) => mode.NotifyAuthDialog(dialog, @event);
    }

    /// <summary>TS <c>showLoginDialog</c> (L6311-6344).</summary>
    private async Task ShowLoginDialogAsync(string providerId, string providerName, Action? onBack = null)
    {
        var previousModel = Session.Model;
        var dialog = new LoginDialogComponent(_ui, providerId, (_, _) => { }, providerName);
        _editorContainer.Clear();
        _editorContainer.AddChild(dialog);
        _ui.SetFocus(dialog);
        _ui.RequestRender();

        void RestoreEditor()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }

        try
        {
            await LoginProviderAsync(dialog, providerId, CredentialKind.OAuth).ConfigureAwait(true);
            RestoreEditor();
            await CompleteProviderAuthenticationAsync(
                providerId, providerName, CredentialKind.OAuth, previousModel).ConfigureAwait(true);
            if (providerId == Radius.ProviderId)
            {
                OfferRadiusMcpServer(providerId, providerName);
            }
        }
        catch (Exception error)
        {
            RestoreEditor();
            var errorMsg = error.Message;
            if (error is CredentialSynchronizationError)
            {
                ShowError(
                    $"Logged in to {providerName}, but local model state could not be synchronized: {errorMsg}");
            }
            else if (errorMsg == "Login cancelled")
            {
                onBack?.Invoke();
            }
            else
            {
                ShowError($"Failed to login to {providerName}: {errorMsg}");
            }
        }
    }

    /// <summary>
    /// Offer to point the Radius MCP server in the global mcp.json at the Radius login, adding the
    /// server when missing. Nothing is asked when a global server already uses this login.
    /// TS <c>offerRadiusMcpServer</c> (L6346-6397).
    /// </summary>
    private void OfferRadiusMcpServer(string providerId, string providerName)
    {
        // TS reads the global agent dir (getAgentDir()); the port's session agent dir defaults to
        // exactly that and is what the rest of the mode already uses (e.g. the trust store).
        var agentDir = _runtimeHost.Services.AgentDir;
        var mcpPath = NodePath.Join(agentDir, "mcp.json");
        static string NormalizeUrl(string url) => url.TrimEnd('/');
        var servers = McpConfig.Load(agentDir, SessionManager.GetCwd(), projectTrusted: false).Servers;
        var existing = servers.FirstOrDefault(server =>
            server.Config.Url is { } url && NormalizeUrl(url) == NormalizeUrl(Radius.McpUrl));
        if (existing is not null && existing.Config.AuthProvider == providerId)
        {
            return;
        }

        var name = existing?.Name ?? "radius";
        if (existing is null && servers.Any(server => server.Name == name))
        {
            name = "radius-mcp";
        }

        // TS narrows with `"url" in existing.config`; the entry was found by its url, so the port
        // checks the projected Url. `auth` replaces the MCP OAuth sign-in.
        JsonObject config = existing is not null && existing.Config.Url is not null
            ? (JsonObject)existing.Config.Value.DeepClone()
            : new JsonObject();
        config.Remove("oauth");
        if (existing is null)
        {
            config["url"] = Radius.McpUrl;
        }

        config["auth"] = new JsonObject { ["provider"] = providerId };

        ShowSelector(done =>
        {
            var selector = new ExtensionSelectorComponent(
                $"Configure {providerName} MCP in {mcpPath}?",
                ["Yes", "No"],
                option =>
                {
                    done();
                    if (option != "Yes")
                    {
                        return;
                    }

                    try
                    {
                        McpConfig.Add(mcpPath, name, new McpServerConfig(config));
                    }
                    catch (Exception error)
                    {
                        ShowError($"Could not update {mcpPath}: {error.Message}");
                        return;
                    }

                    // The MCP extension reads mcp.json when the session starts.
                    _ = HandleReloadCommandAsync();
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });
            return (selector, selector, null);
        });
    }

    /// <summary>TS <c>/logout</c> dispatch (L3279-3283): <c>showOAuthSelector("logout")</c>, not awaited.</summary>
    internal void HandleLogoutCommand() => _ = ShowOAuthSelectorAsync(AuthSelectorMode.Logout);

    /// <summary>Test seam: the <c>/logout</c> entry with the selector task handed back.</summary>
    internal Task HandleLogoutCommandForTestsAsync() => ShowOAuthSelectorAsync(AuthSelectorMode.Logout);

    /// <summary>Test seam: the post-login completion flow (TS <c>completeProviderAuthentication</c>).</summary>
    internal Task CompleteProviderAuthenticationForTestsAsync(
        string providerId, string providerName, CredentialKind authType, ModelSpec? previousModel)
        => CompleteProviderAuthenticationAsync(providerId, providerName, authType, previousModel);

    /// <summary>Test seam: the Radius MCP offer (TS <c>offerRadiusMcpServer</c>).</summary>
    internal void OfferRadiusMcpServerForTests(string providerId, string providerName)
        => OfferRadiusMcpServer(providerId, providerName);
}
