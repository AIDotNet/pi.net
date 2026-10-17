// ============================================================================
// Tests for the autocomplete helpers of interactive-mode.ts (4f-4e).
//
// Upstream has no test file for these; the vectors are derived from the TS bodies. The login-provider
// cases mirror what OAuthSelectorTests already pins for the selector, because both feed the same
// formatAuthSelectorProviderType labels.
// ============================================================================

using Pi.Ai.Auth;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeAutocompleteTests
{
    private static SourceInfo Source(string source, string scope = SourceScope.User) =>
        new() { Path = "/tmp/x.ts", Source = source, Scope = scope, Origin = SourceOrigin.TopLevel };

    private static AuthSelectorProvider Provider(
        string id,
        string name,
        CredentialKind authType,
        bool? subscription = null) =>
        new() { Id = id, Name = name, AuthType = authType, Subscription = subscription };

    // ---------------------------------------------------------------- fuzzy items

    [Fact]
    public void FuzzyItemsAreNullWhenNothingMatches()
    {
        // TS returns null, not [], so the editor treats it as "no suggestions".
        Assert.Null(InteractiveModeAutocomplete.CreateFuzzyItems(
            new[] { "alpha", "beta" },
            "zzz",
            item => item,
            item => new AutocompleteItem { Value = item, Label = item }));
    }

    [Fact]
    public void FuzzyItemsMapEveryMatch()
    {
        var items = InteractiveModeAutocomplete.CreateFuzzyItems(
            new[] { "beta", "alpha", "gamma" },
            "a",
            item => item,
            item => new AutocompleteItem { Value = item, Label = item });

        Assert.NotNull(items);
        Assert.All(items!, item => Assert.Contains("a", item.Value));
        Assert.Equal(3, items!.Count);
    }

    [Fact]
    public void AnEmptyPrefixKeepsEveryItem()
    {
        // fuzzyFilter returns the input untouched when the query is blank.
        var items = InteractiveModeAutocomplete.CreateFuzzyItems(
            new[] { "alpha", "beta" },
            "   ",
            item => item,
            item => new AutocompleteItem { Value = item, Label = item });

        Assert.NotNull(items);
        Assert.Equal(["alpha", "beta"], items!.Select(i => i.Value));
    }

    // ---------------------------------------------------------------- login providers

    [Fact]
    public void OneProviderIdCollapsesIntoASingleOptionWithBothAuthTypes()
    {
        var options = InteractiveModeAutocomplete.BuildLoginProviderCompletionOptions(
        [
            Provider("anthropic", "Anthropic", CredentialKind.ApiKey, subscription: false),
            Provider("anthropic", "Anthropic", CredentialKind.OAuth, subscription: false),
        ]);

        var option = Assert.Single(options);
        Assert.Equal("anthropic", option.Id);
        Assert.Equal("Anthropic", option.Name);
        // AUTH_TYPE_ORDER puts oauth first even though api_key arrived first.
        Assert.Equal([CredentialKind.OAuth, CredentialKind.ApiKey], option.AuthTypes);
        Assert.False(option.Subscription);
    }

    [Fact]
    public void ARepeatedAuthTypeIsNotDuplicated()
    {
        var options = InteractiveModeAutocomplete.BuildLoginProviderCompletionOptions(
        [
            Provider("openai", "OpenAI", CredentialKind.OAuth),
            Provider("openai", "OpenAI", CredentialKind.OAuth),
        ]);

        var option = Assert.Single(options);
        Assert.Equal([CredentialKind.OAuth], option.AuthTypes);
    }

    [Fact]
    public void OptionsAreSortedByProviderName()
    {
        var options = InteractiveModeAutocomplete.BuildLoginProviderCompletionOptions(
        [
            Provider("zeta", "zeta", CredentialKind.ApiKey),
            Provider("alpha", "alpha", CredentialKind.ApiKey),
            Provider("mid", "mid", CredentialKind.ApiKey),
        ]);

        Assert.Equal(["alpha", "mid", "zeta"], options.Select(o => o.Id));
    }

    [Fact]
    public void LoginSearchTextCarriesIdNameAndBothSpellingsPerAuthType()
    {
        var option = new InteractiveModeAutocomplete.LoginProviderCompletionOption
        {
            Id = "anthropic",
            Name = "Anthropic",
            AuthTypes = [CredentialKind.OAuth, CredentialKind.ApiKey],
            Subscription = false,
        };

        Assert.Equal(
            "anthropic Anthropic oauth account api_key API key",
            InteractiveModeAutocomplete.GetLoginProviderSearchText(option));
    }

    [Fact]
    public void LoginDescriptionJoinsLabelsWithASlashWhenNameEqualsId()
    {
        var option = new InteractiveModeAutocomplete.LoginProviderCompletionOption
        {
            Id = "openai",
            Name = "openai",
            AuthTypes = [CredentialKind.OAuth, CredentialKind.ApiKey],
        };

        Assert.Equal("subscription/API key", InteractiveModeAutocomplete.FormatLoginProviderCompletionDescription(option));
    }

    [Fact]
    public void LoginDescriptionPrefixesTheNameWhenItDiffersFromTheId()
    {
        var option = new InteractiveModeAutocomplete.LoginProviderCompletionOption
        {
            Id = "anthropic",
            Name = "Anthropic",
            AuthTypes = [CredentialKind.ApiKey],
        };

        Assert.Equal("Anthropic · API key", InteractiveModeAutocomplete.FormatLoginProviderCompletionDescription(option));
    }

    // ---------------------------------------------------------------- source tags

    [Fact]
    public void BuiltInAndMissingSourcesAreUntagged()
    {
        Assert.Null(InteractiveModeAutocomplete.GetSourceTag(null));
        Assert.Null(InteractiveModeAutocomplete.GetSourceTag(Source("builtin")));
    }

    [Theory]
    [InlineData(SourceScope.User, "u")]
    [InlineData(SourceScope.Project, "p")]
    [InlineData(SourceScope.Temporary, "t")]
    public void PlainLocalSourcesGetJustTheScopeLetter(string scope, string expected)
    {
        Assert.Equal(expected, InteractiveModeAutocomplete.GetSourceTag(Source("local", scope)));
        Assert.Equal(expected, InteractiveModeAutocomplete.GetSourceTag(Source("auto", scope)));
        Assert.Equal(expected, InteractiveModeAutocomplete.GetSourceTag(Source("cli", scope)));
    }

    [Fact]
    public void AnUnrecognisedSourceFallsBackToTheScopeLetter()
    {
        Assert.Equal("u", InteractiveModeAutocomplete.GetSourceTag(Source("something-else")));
    }

    [Fact]
    public void NpmSourcesKeepTheirSpec()
    {
        Assert.Equal(
            "p:npm:@scope/pkg@1.2.3",
            InteractiveModeAutocomplete.GetSourceTag(Source("npm:@scope/pkg@1.2.3", SourceScope.Project)));
    }

    [Fact]
    public void TheSourceIsTrimmedBeforeTagging()
    {
        Assert.Equal("u:npm:foo", InteractiveModeAutocomplete.GetSourceTag(Source("  npm:foo  ")));
    }

    [Fact]
    public void GitSourcesAreRewrittenToHostAndPath()
    {
        var source = Source("git:github.com/owner/repo", SourceScope.User);

        Assert.Equal("u:git:github.com/owner/repo", InteractiveModeAutocomplete.GetSourceTag(source));
    }

    [Fact]
    public void GitRefsAreAppendedAfterAnAt()
    {
        var gitSource = Git.ParseGitUrl("git:github.com/owner/repo#v1.0.0");
        Assert.NotNull(gitSource);

        Assert.Equal(
            $"u:git:{gitSource!.Host}/{gitSource.Path}@v1.0.0",
            InteractiveModeAutocomplete.GetSourceTag(Source("git:github.com/owner/repo#v1.0.0")));
    }

    [Fact]
    public void PrefixDescriptionLeavesAnUntaggedDescriptionAlone()
    {
        Assert.Null(InteractiveModeAutocomplete.PrefixDescription(null, null));
        Assert.Equal("does things", InteractiveModeAutocomplete.PrefixDescription("does things", Source("builtin")));
    }

    [Fact]
    public void PrefixDescriptionAddsTheTagInBrackets()
    {
        Assert.Equal(
            "[u:npm:foo] does things",
            InteractiveModeAutocomplete.PrefixDescription("does things", Source("npm:foo")));
    }

    [Fact]
    public void AnEmptyDescriptionBecomesJustTheTag()
    {
        Assert.Equal("[u]", InteractiveModeAutocomplete.PrefixDescription("", Source("local")));
        Assert.Equal("[u]", InteractiveModeAutocomplete.PrefixDescription(null, Source("local")));
    }

    // ---------------------------------------------------------------- built-in conflicts

    private static ResolvedCommand Command(string name, string invocationName, string path = "/tmp/ext.ts") =>
        new()
        {
            Name = name,
            InvocationName = invocationName,
            Description = "d",
            SourceInfo = new SourceInfo
            {
                Path = path,
                Source = "local",
                Scope = SourceScope.User,
                Origin = SourceOrigin.TopLevel,
            },
            Handler = (_, _) => Task.CompletedTask,
        };

    [Fact]
    public void AShadowingCommandWithoutAnAliasIsSkipped()
    {
        var diagnostics = InteractiveModeAutocomplete.GetBuiltInCommandConflictDiagnostics(
            [Command("model", "model")],
            new HashSet<string>(["model"]));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ResourceDiagnosticType.Warning, diagnostic.Type);
        Assert.Equal(
            "Extension command '/model' conflicts with built-in interactive command. Skipping in autocomplete.",
            diagnostic.Message);
        Assert.Equal("/tmp/ext.ts", diagnostic.Path);
    }

    [Fact]
    public void AShadowingCommandWithAnAliasPointsAtTheAlias()
    {
        var diagnostics = InteractiveModeAutocomplete.GetBuiltInCommandConflictDiagnostics(
            [Command("model", "my-model")],
            new HashSet<string>(["model"]));

        Assert.Equal(
            "Extension command '/model' conflicts with built-in interactive command. Available as '/my-model'.",
            Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void CommandsThatDoNotShadowABuiltInAreNotReported()
    {
        var diagnostics = InteractiveModeAutocomplete.GetBuiltInCommandConflictDiagnostics(
            [Command("deploy", "deploy"), Command("model", "model")],
            new HashSet<string>(["model", "settings"]));

        Assert.Single(diagnostics);
    }
}
