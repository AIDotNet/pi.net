// ============================================================================
// Tests for modes/interactive/components/oauth-selector.ts
//
// The upstream suite's first case exercises `InteractiveMode.prototype.getLoginProviderOptions`,
// which lives in `interactive-mode.ts` and is therefore deferred to 4f-4. The remaining five cases
// are transcribed here, plus supplementary vectors taken from the source's own behaviour
// (title per mode, auth-type labels, the env-var regex, scroll info, the empty-list messages).
// ============================================================================

using Pi.Ai.Auth;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>Minimal <see cref="IApiKeyAuth"/> double: the selector only reads <c>name</c>.</summary>
internal sealed class FakeApiKeyAuth(string name, bool hasLogin = false) : IApiKeyAuth
{
    public string Name { get; } = name;

    public bool HasLogin { get; } = hasLogin;

    public Task<AuthResult?> ResolveAsync(
        Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Minimal <see cref="IOAuthAuth"/> double: the selector only reads <c>name</c>.</summary>
internal sealed class FakeOAuthAuth(string name, bool isSubscription = true) : IOAuthAuth
{
    public string Name { get; } = name;

    public bool IsSubscription { get; } = isSubscription;

    public Task<Credential.OAuth> LoginAsync(
        ProviderAuthInteraction interaction,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Credential.OAuth> RefreshAsync(
        Credential.OAuth credential, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ModelAuth> ToAuthAsync(
        Credential.OAuth credential, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class OAuthSelectorTests : SelectorTestFixture
{
    private static AuthSelectorProvider Provider(
        string id,
        string name,
        CredentialKind authType,
        AuthSelectorMethod? method = null,
        AuthCheck? status = null,
        bool? subscription = null) =>
        new()
        {
            Id = id,
            Name = name,
            AuthType = authType,
            Method = method,
            Status = status,
            Subscription = subscription,
        };

    private static OAuthSelectorComponent Create(
        AuthSelectorMode mode = AuthSelectorMode.Login,
        IReadOnlyList<AuthSelectorProvider>? providers = null,
        Action<string, CredentialKind>? onSelect = null,
        Action? onCancel = null,
        string? initialSearchInput = null) =>
        new(
            mode,
            providers ?? [],
            onSelect ?? ((_, _) => { }),
            onCancel ?? (() => { }),
            initialSearchInput);

    // ---------------------------------------------------------------- 上游转写（5 例）

    [Fact]
    public void RendersAnOptionWithoutCompiledAuthStatusAsNotConfigured()
    {
        var selector = Create(providers:
            [Provider("google", "Google", CredentialKind.ApiKey, status: null)]);

        var output = RenderText(selector, 120);
        Assert.Contains("not configured", output, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ configured", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsOAuthAuthDistinctlyInTheApiKeySelector()
    {
        var selector = Create(providers:
        [
            Provider(
                "anthropic",
                "Anthropic",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.OAuth, Source = "OAuth" }),
        ]);

        Assert.Contains("subscription configured", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsEnvironmentApiKeyAuthAsConfigured()
    {
        var selector = Create(providers:
        [
            Provider(
                "openai",
                "OpenAI",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "OPENAI_API_KEY" }),
        ]);

        var output = RenderText(selector, 120);
        Assert.Contains("✓ env: OPENAI_API_KEY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not configured", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsModelsJsonApiKeyAuthAsConfigured()
    {
        var selector = Create(providers:
        [
            Provider(
                "local-proxy",
                "local-proxy",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "key in models.json" }),
        ]);

        Assert.Contains("✓ key in models.json", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsModelsJsonCommandAuthAsConfigured()
    {
        var selector = Create(providers:
        [
            Provider(
                "op-proxy",
                "op-proxy",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "command in models.json" }),
        ]);

        Assert.Contains("✓ command in models.json", RenderText(selector, 120), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 状态后缀（补）

    [Fact]
    public void OAuthAndStoredCredentialSourcesCollapseToPlainConfigured()
    {
        Assert.Equal(
            RenderText(Create(providers:
            [
                Provider(
                    "a",
                    "A",
                    CredentialKind.OAuth,
                    status: new AuthCheck { Type = CredentialKind.OAuth, Source = "OAuth" }),
            ]), 120),
            RenderText(Create(providers:
            [
                Provider(
                    "a",
                    "A",
                    CredentialKind.OAuth,
                    status: new AuthCheck { Type = CredentialKind.OAuth, Source = "stored credential" }),
            ]), 120));
        Assert.Contains(
            "✓ configured",
            RenderText(Create(providers:
            [
                Provider(
                    "a",
                    "A",
                    CredentialKind.OAuth,
                    status: new AuthCheck { Type = CredentialKind.OAuth, Source = "stored credential" }),
            ]), 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySourceFallsBackToPlainConfigured()
    {
        // `!provider.status.source` is a falsy test, so "" takes the plain "✓ configured" branch
        // rather than rendering "✓ " with a dangling prefix.
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "" }),
        ]);

        Assert.Contains("✓ configured", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OPENAI_API_KEY", "✓ env: OPENAI_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY, OPENAI_API_KEY", "✓ env: ANTHROPIC_API_KEY, OPENAI_API_KEY")]
    [InlineData("A1_B2", "✓ env: A1_B2")]
    public void ScreamingSnakeSourceIsLabelledAsAnEnvVar(string source, string expected)
    {
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = source }),
        ]);

        Assert.Contains(expected, RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("key in models.json")]
    [InlineData("lowercase")]
    [InlineData("_LEADING")]
    [InlineData("MIXED_Case")]
    [InlineData("A, b")]
    [InlineData("TRAILING ")]
    public void OtherSourcesAreRenderedVerbatim(string source)
    {
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = source }),
        ]);

        Assert.Contains($"✓ {source}", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.DoesNotContain("env: ", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingNewlineIsNotTreatedAsAnEnvVar()
    {
        // JS's `$` without `m` only matches at the very end; .NET's `$` also matches before a trailing
        // "\n", so the port anchors with \z. This is the regression guard for that choice.
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "OPENAI_API_KEY\n" }),
        ]);

        Assert.Contains("✓ OPENAI_API_KEY", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.DoesNotContain("env: ", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CredentialKind.ApiKey, null, "API key")]
    [InlineData(CredentialKind.OAuth, null, "subscription")]
    [InlineData(CredentialKind.OAuth, true, "subscription")]
    [InlineData(CredentialKind.OAuth, false, "account")]
    [InlineData(CredentialKind.ApiKey, false, "API key")]
    public void FormatAuthSelectorProviderTypeLabelsTheAuthType(
        CredentialKind authType, bool? subscription, string expected) =>
        Assert.Equal(expected, OAuthSelectorComponent.FormatAuthSelectorProviderType(authType, subscription));

    [Fact]
    public void MismatchedStatusTypeIsShownAsAWarningWithItsOwnLabel()
    {
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.OAuth,
                status: new AuthCheck { Type = CredentialKind.ApiKey, Source = "OPENAI_API_KEY" }),
        ]);

        var output = RenderText(selector, 120);
        Assert.Contains("API key configured", output, StringComparison.Ordinal);
        Assert.DoesNotContain("env: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountSubscriptionLabelIsUsedForAMismatchedOAuthStatus()
    {
        var selector = Create(providers:
        [
            Provider(
                "a",
                "A",
                CredentialKind.ApiKey,
                status: new AuthCheck { Type = CredentialKind.OAuth, Source = "OAuth" },
                subscription: false),
        ]);

        Assert.Contains("account configured", RenderText(selector, 120), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 标题与标签（补）

    [Fact]
    public void TitleDependsOnTheMode()
    {
        Assert.Contains(
            "Select provider to configure:",
            RenderText(Create(AuthSelectorMode.Login), 120),
            StringComparison.Ordinal);
        Assert.Contains(
            "Select provider to logout:",
            RenderText(Create(AuthSelectorMode.Logout), 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuthTypeLabelsOnlyAppearWhenTheProviderSetIsMixed()
    {
        var mixed = Create(providers:
        [
            Provider("a", "A", CredentialKind.OAuth),
            Provider("b", "B", CredentialKind.ApiKey),
        ]);
        Assert.Contains("[subscription]", RenderText(mixed, 120), StringComparison.Ordinal);
        Assert.Contains("[API key]", RenderText(mixed, 120), StringComparison.Ordinal);

        var uniform = Create(providers:
        [
            Provider("a", "A", CredentialKind.OAuth),
            Provider("b", "B", CredentialKind.OAuth),
        ]);
        Assert.DoesNotContain("[subscription]", RenderText(uniform, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedRowIsMarkedAndIndentedRowsAreNot()
    {
        var selector = Create(providers:
        [
            Provider("a", "Alpha", CredentialKind.OAuth),
            Provider("b", "Beta", CredentialKind.OAuth),
        ]);

        var lines = RenderLines(selector, 120);
        Assert.Contains(lines, line => line.TrimStart().StartsWith("→ Alpha", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.TrimStart().StartsWith("Beta", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 空列表（补）

    [Fact]
    public void EmptyProviderListExplainsThatNothingIsAvailable()
    {
        Assert.Contains(
            "No providers available",
            RenderText(Create(AuthSelectorMode.Login), 120),
            StringComparison.Ordinal);
        Assert.Contains(
            "No providers logged in. Use /login first.",
            RenderText(Create(AuthSelectorMode.Logout), 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FilteringToNothingReportsNoMatches()
    {
        var selector = Create(providers: [Provider("a", "Alpha", CredentialKind.OAuth)]);

        selector.HandleInput("zzz");

        var output = RenderText(selector, 120);
        Assert.Contains("No matching providers", output, StringComparison.Ordinal);
        Assert.DoesNotContain("No providers available", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 过滤（补）

    [Fact]
    public void SearchHaystackCoversNameIdAuthTypeAndMethodName()
    {
        var providers = new[]
        {
            Provider(
                "provider-id",
                "Display Name",
                CredentialKind.ApiKey,
                method: AuthSelectorMethod.FromApiKey(new FakeApiKeyAuth("Anthropic API key"))),
            Provider("other", "Other", CredentialKind.OAuth, method: AuthSelectorMethod.FromOAuth(new FakeOAuthAuth("Claude Pro"))),
        };

        // name
        var byName = Create(providers: providers);
        byName.HandleInput("Display");
        Assert.Single(byName.GetFilteredProviders());

        // id
        var byId = Create(providers: providers);
        byId.HandleInput("provider-id");
        Assert.Single(byId.GetFilteredProviders());

        // authType — the raw wire spelling is part of the haystack
        var byAuthType = Create(providers: providers);
        byAuthType.HandleInput("api_key");
        Assert.Single(byAuthType.GetFilteredProviders());
        Assert.Equal("provider-id", byAuthType.GetFilteredProviders()[0].Id);

        // method name — "Claude" is not a subsequence of the other provider's haystack
        var byMethod = Create(providers: providers);
        byMethod.HandleInput("Claude");
        Assert.Single(byMethod.GetFilteredProviders());
        Assert.Equal("other", byMethod.GetFilteredProviders()[0].Id);
    }

    [Fact]
    public void ProvidersWithoutAMethodStillMatchOnTheirOtherFields()
    {
        var selector = Create(providers: [Provider("a", "Alpha", CredentialKind.OAuth)]);

        selector.HandleInput("Alpha");

        Assert.Single(selector.GetFilteredProviders());
    }

    [Fact]
    public void BackspaceRestoresEveryProvider()
    {
        var selector = Create(providers:
        [
            Provider("a", "Alpha", CredentialKind.OAuth),
            Provider("b", "Beta", CredentialKind.OAuth),
        ]);

        selector.HandleInput("Beta");
        Assert.Single(selector.GetFilteredProviders());

        for (var i = 0; i < 4; i++)
        {
            selector.HandleInput("\x7f");
        }

        Assert.Equal(2, selector.GetFilteredProviders().Count);
    }

    [Fact]
    public void InitialSearchInputSeedsTheBoxAndTheFilter()
    {
        var selector = Create(
            providers:
            [
                Provider("a", "Alpha", CredentialKind.OAuth),
                Provider("b", "Beta", CredentialKind.OAuth),
            ],
            initialSearchInput: "Beta");

        Assert.Equal("Beta", selector.GetSearchInput().GetValue());
        Assert.Single(selector.GetFilteredProviders());
    }

    [Fact]
    public void SelectedIndexIsClampedWhenTheFilterShrinksTheList()
    {
        var selector = Create(providers:
        [
            Provider("a", "Alpha", CredentialKind.OAuth),
            Provider("b", "Beta", CredentialKind.OAuth),
            Provider("c", "Gamma", CredentialKind.OAuth),
        ]);

        selector.HandleInput("\x1b[B");
        selector.HandleInput("\x1b[B");
        Assert.Equal(2, selector.SelectedIndex);

        // Filtering down to a single row must pull the selection back into range.
        selector.HandleInput("Alpha");
        Assert.Equal(0, selector.SelectedIndex);
    }

    // ---------------------------------------------------------------- 键盘（补）

    [Fact]
    public void NavigationClampsAtBothEnds()
    {
        var selector = Create(providers:
        [
            Provider("a", "Alpha", CredentialKind.OAuth),
            Provider("b", "Beta", CredentialKind.OAuth),
        ]);

        selector.HandleInput("\x1b[A");
        Assert.Equal(0, selector.SelectedIndex);

        selector.HandleInput("\x1b[B");
        selector.HandleInput("\x1b[B");
        Assert.Equal(1, selector.SelectedIndex);
    }

    [Fact]
    public void NavigationOnAnEmptyListIsANoOp()
    {
        var selector = Create();

        selector.HandleInput("\x1b[A");
        selector.HandleInput("\x1b[B");

        Assert.Equal(0, selector.SelectedIndex);
    }

    [Fact]
    public void NavigationKeysDoNotReachTheSearchBox()
    {
        var selector = Create(providers: [Provider("a", "Alpha", CredentialKind.OAuth)]);

        selector.HandleInput("\x1b[B");

        Assert.Equal("", selector.GetSearchInput().GetValue());
    }

    [Fact]
    public void EnterReportsTheSelectedProviderAndItsAuthType()
    {
        var seen = new List<(string Id, CredentialKind AuthType)>();
        var selector = Create(
            providers:
            [
                Provider("a", "Alpha", CredentialKind.OAuth),
                Provider("b", "Beta", CredentialKind.ApiKey),
            ],
            onSelect: (id, authType) => seen.Add((id, authType)));

        selector.HandleInput("\x1b[B");
        selector.HandleInput("\r");

        Assert.Equal(new[] { (Id: "b", AuthType: CredentialKind.ApiKey) }, seen);
    }

    [Fact]
    public void EnterOnAnEmptyListDoesNotReportAnything()
    {
        var selected = false;
        var selector = Create(onSelect: (_, _) => selected = true);

        selector.HandleInput("\r");

        Assert.False(selected);
    }

    [Fact]
    public void EscapeInvokesTheCancelCallback()
    {
        var cancelled = false;
        var selector = Create(providers: [Provider("a", "Alpha", CredentialKind.OAuth)], onCancel: () => cancelled = true);

        selector.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void SearchSubmitIsWiredToTheSelection()
    {
        var seen = new List<(string Id, CredentialKind AuthType)>();
        var selector = Create(
            providers:
            [
                Provider("a", "Alpha", CredentialKind.OAuth),
                Provider("b", "Beta", CredentialKind.ApiKey),
            ],
            onSelect: (id, authType) => seen.Add((id, authType)));

        selector.HandleInput("\x1b[B");
        selector.GetSearchInput().OnSubmit!("");

        Assert.Equal(new[] { (Id: "b", AuthType: CredentialKind.ApiKey) }, seen);
    }

    [Fact]
    public void FocusIsForwardedToTheSearchBox()
    {
        var selector = Create(providers: [Provider("a", "Alpha", CredentialKind.OAuth)]);

        Assert.False(selector.Focused);
        Assert.False(selector.GetSearchInput().Focused);

        selector.Focused = true;

        Assert.True(selector.Focused);
        Assert.True(selector.GetSearchInput().Focused);
    }

    // ---------------------------------------------------------------- 滚动信息（补）

    [Fact]
    public void ScrollInfoAppearsOnlyWhenTheListOverflows()
    {
        var many = Enumerable.Range(0, 12)
            .Select(i => Provider($"p{i}", $"Provider {i:D2}", CredentialKind.OAuth))
            .ToList();

        var overflow = Create(providers: many);
        Assert.Contains("(1/12)", RenderText(overflow, 120), StringComparison.Ordinal);

        var fits = Create(providers: many.Take(8).ToList());
        Assert.DoesNotContain("(1/8)", RenderText(fits, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollInfoTracksTheSelectionAndWindowsTheRows()
    {
        var many = Enumerable.Range(0, 12)
            .Select(i => Provider($"p{i}", $"Provider {i:D2}", CredentialKind.OAuth))
            .ToList();
        var selector = Create(providers: many);

        // maxVisible = 8, so moving to the 8th row (index 7) still leaves startIndex at 0.
        for (var i = 0; i < 7; i++)
        {
            selector.HandleInput("\x1b[B");
        }
        Assert.Equal(7, selector.SelectedIndex);
        Assert.Contains("(8/12)", RenderText(selector, 120), StringComparison.Ordinal);

        // index 8 pushes the window: startIndex = min(8 - 4, 12 - 8) = 4, endIndex = 12.
        selector.HandleInput("\x1b[B");
        var output = RenderText(selector, 120);
        Assert.Contains("(9/12)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider 03", output, StringComparison.Ordinal);
        Assert.Contains("Provider 04", output, StringComparison.Ordinal);
        Assert.Contains("Provider 11", output, StringComparison.Ordinal);
    }
}
