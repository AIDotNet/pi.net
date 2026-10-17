// ============================================================================
// Auth provider selector (login / logout).
// Port of modes/interactive/components/oauth-selector.ts
// ============================================================================

using System.Text.RegularExpressions;
using Pi.Ai.Auth;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>ApiKeyAuth | OAuthAuth</c>. The two interfaces share only <see cref="Name"/>, and the port
/// keeps both branches so callers can ask which one they got — the way TS does with
/// <c>"loginLabel" in method</c> (OAuth only) and <c>method?.login</c> (API key only).
/// </summary>
public sealed class AuthSelectorMethod
{
    private AuthSelectorMethod(string name, IApiKeyAuth? apiKey, IOAuthAuth? oauth)
    {
        Name = name;
        ApiKey = apiKey;
        OAuth = oauth;
    }

    /// <summary>The method's display name. Both TS branches have a <c>name</c> field.</summary>
    public string Name { get; }

    /// <summary>The API-key branch, when the provider's option came from <c>auth.apiKey</c>.</summary>
    public IApiKeyAuth? ApiKey { get; }

    /// <summary>The OAuth branch, when the provider's option came from <c>auth.oauth</c>.</summary>
    public IOAuthAuth? OAuth { get; }

    public static AuthSelectorMethod FromApiKey(IApiKeyAuth method) => new(method.Name, method, null);

    public static AuthSelectorMethod FromOAuth(IOAuthAuth method) => new(method.Name, null, method);

    public override string ToString() => Name;
}

/// <summary>TS <c>"login" | "logout"</c>: which set of providers the selector is showing.</summary>
public enum AuthSelectorMode
{
    Login,
    Logout,
}

/// <summary>TS <c>AuthSelectorProvider</c>.</summary>
public sealed record AuthSelectorProvider
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>TS <c>authType: "oauth" | "api_key"</c>. Same value set as <see cref="CredentialKind"/>.</summary>
    public required CredentialKind AuthType { get; init; }

    /// <summary>TS <c>method?: ApiKeyAuth | OAuthAuth</c>.</summary>
    public AuthSelectorMethod? Method { get; init; }

    /// <summary>TS <c>status?: AuthCheck</c>.</summary>
    public AuthCheck? Status { get; init; }

    /// <summary>
    /// Whether the provider's OAuth sign-in is backed by a subscription. <c>false</c> labels it as an
    /// account; unset keeps the "subscription" label.
    /// </summary>
    public bool? Subscription { get; init; }
}

/// <summary>
/// Component that renders an auth provider selector. TS
/// <c>OAuthSelectorComponent extends Container implements Focusable</c>.
/// </summary>
public sealed partial class OAuthSelectorComponent : Container, IFocusable
{
    /// <summary>
    /// JS <c>/^[A-Z][A-Z0-9_]*(?:, [A-Z][A-Z0-9_]*)*$/</c>: a comma-separated list of SCREAMING_SNAKE
    /// env-var names, which the status suffix renders as <c>env: NAME</c>.
    /// </summary>
    /// <remarks>
    /// <c>\z</c> rather than <c>$</c>: JS's <c>$</c> without the <c>m</c> flag only matches at the very
    /// end of the input, while .NET's <c>$</c> also matches just before a trailing <c>\n</c>. A source
    /// of <c>"OPENAI_API_KEY\n"</c> must not be treated as an env var.
    /// </remarks>
    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]*(?:, [A-Z][A-Z0-9_]*)*\z")]
    private static partial Regex EnvVarSourceRegex();

    private const int MaxVisible = 8;

    private readonly Input _searchInput;
    private readonly Container _listContainer;
    private readonly IReadOnlyList<AuthSelectorProvider> _allProviders;
    private readonly AuthSelectorMode _mode;
    private readonly Action<string, CredentialKind> _onSelect;
    private readonly Action _onCancel;
    private readonly bool _showAuthTypeLabels;

    private IReadOnlyList<AuthSelectorProvider> _filteredProviders;
    private int _selectedIndex;
    private bool _focused;

    public OAuthSelectorComponent(
        AuthSelectorMode mode,
        IReadOnlyList<AuthSelectorProvider> providers,
        Action<string, CredentialKind> onSelect,
        Action onCancel,
        string? initialSearchInput = null)
    {
        var theme = ThemeRuntime.Current;
        _mode = mode;
        _allProviders = providers;
        _filteredProviders = providers;
        _showAuthTypeLabels = providers.Select(provider => provider.AuthType).Distinct().Count() > 1;
        _onSelect = onSelect;
        _onCancel = onCancel;

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add title
        var title = mode == AuthSelectorMode.Login
            ? "Select provider to configure:"
            : "Select provider to logout:";
        AddChild(new TruncatedText(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(new Spacer(1));

        _searchInput = new Input();
        if (!string.IsNullOrEmpty(initialSearchInput))
        {
            _searchInput.SetValue(initialSearchInput);
        }
        _searchInput.OnSubmit = _ =>
        {
            var selectedProvider = SelectedProvider();
            if (selectedProvider is not null)
            {
                _onSelect(selectedProvider.Id, selectedProvider.AuthType);
            }
        };
        AddChild(_searchInput);
        AddChild(new Spacer(1));

        // Create list container
        _listContainer = new Container();
        AddChild(_listContainer);

        AddChild(new Spacer(1));

        // Add bottom border
        AddChild(new DynamicBorder());

        // Initial render
        FilterProviders(initialSearchInput ?? "");
    }

    /// <summary>Focusable implementation — propagate to the search input for IME cursor positioning.</summary>
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    /// <summary>TS <c>formatAuthSelectorProviderType</c>.</summary>
    public static string FormatAuthSelectorProviderType(CredentialKind authType, bool? subscription)
    {
        if (authType == CredentialKind.ApiKey)
        {
            return "API key";
        }

        // TS uses `subscription === false`, a strict comparison: an unset value keeps "subscription".
        return subscription is false ? "account" : "subscription";
    }

    /// <summary>
    /// TS <c>formatAuthSelectorProviderStatus</c>: the themed suffix describing whether and how a login
    /// option is configured, for example <c>" ✓ configured"</c>.
    /// </summary>
    public static string FormatAuthSelectorProviderStatus(AuthSelectorProvider provider)
    {
        var theme = ThemeRuntime.Current;
        if (provider.Status is null)
        {
            return theme.Fg("muted", " • not configured");
        }

        if (provider.Status.Type != provider.AuthType)
        {
            var mismatchLabel =
                $"{FormatAuthSelectorProviderType(provider.Status.Type, provider.Subscription)} configured";
            return theme.Fg("muted", " • ") + theme.Fg("warning", mismatchLabel);
        }

        var source = provider.Status.Source;
        if (string.IsNullOrEmpty(source) || source == "OAuth" || source == "stored credential")
        {
            return theme.Fg("success", " ✓ configured");
        }

        var label = EnvVarSourceRegex().IsMatch(source) ? $"env: {source}" : source;
        return theme.Fg("success", $" ✓ {label}");
    }

    private AuthSelectorProvider? SelectedProvider() =>
        _selectedIndex >= 0 && _selectedIndex < _filteredProviders.Count
            ? _filteredProviders[_selectedIndex]
            : null;

    private void FilterProviders(string query)
    {
        _filteredProviders = query.Length > 0
            ? Fuzzy.Filter(
                _allProviders,
                query,
                provider =>
                    $"{provider.Name} {provider.Id} {AuthTypeText(provider.AuthType)} {provider.Method?.Name ?? ""}")
            : _allProviders;
        _selectedIndex = Math.Max(
            0,
            Math.Min(_selectedIndex, Math.Max(0, _filteredProviders.Count - 1)));
        UpdateList();
    }

    private void UpdateList()
    {
        var theme = ThemeRuntime.Current;
        _listContainer.Clear();

        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (MaxVisible / 2), _filteredProviders.Count - MaxVisible));
        var endIndex = Math.Min(startIndex + MaxVisible, _filteredProviders.Count);

        for (var i = startIndex; i < endIndex; i++)
        {
            var provider = _filteredProviders[i];
            var isSelected = i == _selectedIndex;

            var statusIndicator = FormatAuthSelectorProviderStatus(provider);
            var authTypeLabel = _showAuthTypeLabels
                ? theme.Fg(
                    "muted",
                    $" [{FormatAuthSelectorProviderType(provider.AuthType, provider.Subscription)}]")
                : "";

            string line;
            if (isSelected)
            {
                var prefix = theme.Fg("accent", "→ ");
                var text = theme.Fg("accent", provider.Name);
                line = prefix + text + authTypeLabel + statusIndicator;
            }
            else
            {
                var text = $"  {theme.Fg("text", provider.Name)}";
                line = text + authTypeLabel + statusIndicator;
            }

            _listContainer.AddChild(new TruncatedText(line, 1, 0));
        }

        if (startIndex > 0 || endIndex < _filteredProviders.Count)
        {
            var scrollInfo = theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredProviders.Count})");
            _listContainer.AddChild(new TruncatedText(scrollInfo, 1, 0));
        }

        // Show "no providers" if empty
        if (_filteredProviders.Count == 0)
        {
            var message = _allProviders.Count == 0
                ? _mode == AuthSelectorMode.Login
                    ? "No providers available"
                    : "No providers logged in. Use /login first."
                : "No matching providers";
            _listContainer.AddChild(new TruncatedText(theme.Fg("muted", $"  {message}"), 1, 0));
        }
    }

    /// <summary>
    /// Port of <c>OAuthSelectorComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built on the class that lists
    /// <see cref="IComponent"/>, so a fresh public method here would never become the implementation.
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();

        // Up arrow
        if (keybindings.Matches(data, TuiKeybindingIds.SelectUp))
        {
            if (_filteredProviders.Count == 0)
            {
                return;
            }
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }

        // Down arrow
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectDown))
        {
            if (_filteredProviders.Count == 0)
            {
                return;
            }
            _selectedIndex = Math.Min(_filteredProviders.Count - 1, _selectedIndex + 1);
            UpdateList();
        }

        // Enter
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm))
        {
            var selectedProvider = SelectedProvider();
            if (selectedProvider is not null)
            {
                _onSelect(selectedProvider.Id, selectedProvider.AuthType);
            }
        }

        // Escape or Ctrl+C
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancel();
        }

        // Pass everything else to the search input
        else
        {
            _searchInput.HandleInput(data);
            FilterProviders(_searchInput.GetValue());
        }
    }

    /// <summary>The live search box. Not in TS (its <c>private</c> is erased at runtime); the tests need
    /// it to seed a query and to check that focus is forwarded.</summary>
    internal Input GetSearchInput() => _searchInput;

    /// <summary>The live provider list. Not in TS (same reason); the tests observe the filter and the
    /// selected row through it.</summary>
    internal IReadOnlyList<AuthSelectorProvider> GetFilteredProviders() => _filteredProviders;

    /// <summary>The current selection index. Not in TS (same reason).</summary>
    internal int SelectedIndex => _selectedIndex;

    /// <summary>
    /// TS interpolates <c>provider.authType</c> straight into the fuzzy haystack, so the wire spelling
    /// is part of the observable behaviour: searching <c>api_key</c> must match API-key providers.
    /// Public because the login-command autocomplete builds the same haystack (4f-4e).
    /// </summary>
    public static string AuthTypeText(CredentialKind authType) =>
        authType == CredentialKind.OAuth ? "oauth" : "api_key";
}
