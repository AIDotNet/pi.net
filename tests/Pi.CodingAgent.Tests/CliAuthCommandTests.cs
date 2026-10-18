// ============================================================================
// Tests for the `pi auth …` command parser (TS `cli/auth-command.ts`), landed
// with 4g-1.
//
// The parse cases mirror test/auth-check.test.ts and test/credential-print.test.ts
// case-for-case; the extra cases below pin the branches those suites leave
// implicit (unit suffixes, the case-sensitive unit comparison, the
// kind/flag pairing rules, and validateAuthCommandArgs's rejection order).
// ============================================================================

using Pi.CodingAgent.Cli;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CliAuthCommandTests
{
    // ------------------------------------------------------------------ parse

    [Fact]
    public void ParsesCheckCommandWithProvider()
    {
        var command = AuthCommands.ParseAuthCommand(["auth", "check", "--provider", "openai"]);
        Assert.NotNull(command);
        Assert.Equal(AuthCommandKind.Check, command.Kind);
        Assert.Equal(["--provider", "openai"], command.Args);
        Assert.False(command.Json);
        Assert.False(command.Credentials);
        Assert.False(command.NoRefresh);
        Assert.Null(command.MinExpiryMs);
    }

    [Fact]
    public void ParsesCheckCommandWithAllOutputFlags()
    {
        var command = AuthCommands.ParseAuthCommand(
            ["auth", "check", "--json", "--credentials", "--no-refresh", "--provider", "openai"]);
        Assert.NotNull(command);
        Assert.Equal(AuthCommandKind.Check, command.Kind);
        Assert.Equal(["--provider", "openai"], command.Args);
        Assert.True(command.Json);
        Assert.True(command.Credentials);
        Assert.True(command.NoRefresh);
    }

    [Fact]
    public void ParsesApiKeyCommand()
    {
        var command = AuthCommands.ParseAuthCommand(["auth", "print-api-key", "--provider", "openai"]);
        Assert.NotNull(command);
        Assert.Equal(AuthCommandKind.ApiKey, command.Kind);
        Assert.Equal(["--provider", "openai"], command.Args);
        Assert.False(command.Json);
        Assert.False(command.Credentials);
        Assert.False(command.NoRefresh);
    }

    [Fact]
    public void ParsesBearerTokenCommandWithoutOptions()
    {
        var command = AuthCommands.ParseAuthCommand(["auth", "print-bearer-token"]);
        Assert.NotNull(command);
        Assert.Equal(AuthCommandKind.BearerToken, command.Kind);
        Assert.Empty(command.Args);
        Assert.Null(command.MinExpiryMs);
    }

    [Fact]
    public void ParsesMinExpiryDuration()
    {
        var command = AuthCommands.ParseAuthCommand(["auth", "print-bearer-token", "--min-expiry", "30m"]);
        Assert.NotNull(command);
        Assert.Equal(AuthCommandKind.BearerToken, command.Kind);
        Assert.Empty(command.Args);
        Assert.Equal(30 * 60_000, command.MinExpiryMs);
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("45s", 45_000)]
    [InlineData("30m", 1_800_000)]
    [InlineData("2h", 7_200_000)]
    public void ParsesEveryMinExpiryUnit(string value, long expected)
    {
        var command = AuthCommands.ParseAuthCommand(["auth", "print-bearer-token", "--min-expiry", value]);
        Assert.Equal(expected, command!.MinExpiryMs);
    }

    [Fact]
    public void MinExpiryUnitComparisonIsCaseSensitive()
    {
        // TS compares `unit === "ms"` etc. against the raw match, so an uppercase
        // unit falls through every branch and lands on the hour factor.
        Assert.Equal(30 * 3_600_000, AuthCommands.ParseAuthCommand(
            ["auth", "print-bearer-token", "--min-expiry", "30M"])!.MinExpiryMs);
        Assert.Equal(30 * 3_600_000, AuthCommands.ParseAuthCommand(
            ["auth", "print-bearer-token", "--min-expiry", "30MS"])!.MinExpiryMs);
        // …but the regex itself is case-insensitive, so `30mS` still parses —
        // and its unit ("mS") matches no branch either, so it is also an hour.
        Assert.Equal(30 * 3_600_000, AuthCommands.ParseAuthCommand(
            ["auth", "print-bearer-token", "--min-expiry", "30mS"])!.MinExpiryMs);
    }

    [Theory]
    [InlineData("30x")]
    [InlineData("abc")]
    [InlineData("m")]
    [InlineData("")]
    [InlineData("30 m")]
    [InlineData("-30m")]
    public void RejectsMalformedMinExpiryValues(string value)
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ParseAuthCommand(["auth", "print-bearer-token", "--min-expiry", value]));
        Assert.Equal("--min-expiry must use a duration such as 30m or 1h", error.Message);
    }

    [Fact]
    public void RejectsMissingMinExpiryValue()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ParseAuthCommand(["auth", "print-bearer-token", "--min-expiry"]));
        Assert.Equal("--min-expiry must use a duration such as 30m or 1h", error.Message);
    }

    [Fact]
    public void RejectsMinExpiryOnNonBearerTokenCommands()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ParseAuthCommand(["auth", "print-api-key", "--min-expiry", "30m"]));
        Assert.Equal("--min-expiry is only supported by print-bearer-token", error.Message);
    }

    [Fact]
    public void RejectsCheckOnlyFlagsOnCredentialCommands()
    {
        foreach (var flag in new[] { "--json", "--credentials", "--no-refresh" })
        {
            var error = Assert.Throws<AuthCommandError>(() =>
                AuthCommands.ParseAuthCommand(["auth", "print-api-key", flag]));
            Assert.Equal($"{flag} is only supported by auth check", error.Message);

            var bearerError = Assert.Throws<AuthCommandError>(() =>
                AuthCommands.ParseAuthCommand(["auth", "print-bearer-token", flag]));
            Assert.Equal($"{flag} is only supported by auth check", bearerError.Message);
        }
    }

    [Fact]
    public void RejectsUnknownAuthCommands()
    {
        var error = Assert.Throws<AuthCommandError>(() => AuthCommands.ParseAuthCommand(["auth", "unknown"]));
        Assert.Equal(
            "Unknown auth command \"unknown\". Use \"pi auth print-api-key\", \"pi auth print-bearer-token\", or \"pi auth check\".",
            error.Message);
    }

    [Fact]
    public void RejectsMissingAuthSubcommand()
    {
        var error = Assert.Throws<AuthCommandError>(() => AuthCommands.ParseAuthCommand(["auth"]));
        Assert.Equal(
            "Unknown auth command \"\". Use \"pi auth print-api-key\", \"pi auth print-bearer-token\", or \"pi auth check\".",
            error.Message);
    }

    [Fact]
    public void ReturnsNullForNonAuthCommands()
    {
        Assert.Null(AuthCommands.ParseAuthCommand([]));
        Assert.Null(AuthCommands.ParseAuthCommand(["install", "foo"]));
        Assert.Null(AuthCommands.ParseAuthCommand(["not-auth"]));
    }

    [Fact]
    public void CollectsUnrecognisedArgumentsVerbatim()
    {
        var command = AuthCommands.ParseAuthCommand(
            ["auth", "check", "extra", "--provider", "openai", "tail"]);
        Assert.NotNull(command);
        Assert.Equal(["extra", "--provider", "openai", "tail"], command.Args);
    }

    // -------------------------------------------------------------------- help

    public static TheoryData<string[]> AuthHelpCases { get; } = new()
    {
        new string[] { "auth", "--help" },
        new string[] { "auth", "print-api-key", "--help" },
        new string[] { "auth", "print-bearer-token", "-h" },
        new string[] { "auth", "check", "--help" },
        new string[] { "auth" },
        new string[] { "auth", "help" },
    };

    [Theory]
    [MemberData(nameof(AuthHelpCases))]
    public void DetectsAuthCommandHelp(string[] args)
    {
        Assert.True(AuthCommands.IsAuthCommandHelp(args));
    }

    public static TheoryData<string[]> NonAuthHelpCases { get; } = new()
    {
        new string[0],
        new string[] { "not-auth", "--help" },
        new string[] { "install", "-h" },
        new string[] { "auth", "check", "--provider", "openai" },
    };

    [Theory]
    [MemberData(nameof(NonAuthHelpCases))]
    public void RejectsNonAuthHelp(string[] args)
    {
        Assert.False(AuthCommands.IsAuthCommandHelp(args));
    }

    [Fact]
    public void AuthCommandNamesAndUsage()
    {
        Assert.Equal("auth check", AuthCommands.GetAuthCommandName(AuthCommandKind.Check));
        Assert.Equal("auth print-api-key", AuthCommands.GetAuthCommandName(AuthCommandKind.ApiKey));
        Assert.Equal("auth print-bearer-token", AuthCommands.GetAuthCommandName(AuthCommandKind.BearerToken));
        Assert.EndsWith("auth check --provider <provider> [--json] [--credentials] [--no-refresh]",
            AuthCommands.GetAuthCommandUsage(AuthCommandKind.Check));
        Assert.EndsWith("auth print-api-key --provider <provider> [--model <model>]",
            AuthCommands.GetAuthCommandUsage(AuthCommandKind.ApiKey));
        Assert.EndsWith(
            "auth print-bearer-token --provider <provider> [--model <model>] [--min-expiry <duration>]",
            AuthCommands.GetAuthCommandUsage(AuthCommandKind.BearerToken));
    }

    // -------------------------------------------------------- validateAuthArgs

    [Fact]
    public void ValidatesTrimmedProviderAndModel()
    {
        var target = AuthCommands.ValidateAuthCommandArgs(
            CliArgs.Parse(["--provider", " openai ", "--model", " gpt-5.5 "]), AuthCommandKind.Check);
        Assert.Equal("openai", target.Provider);
        Assert.Equal("gpt-5.5", target.Model);
    }

    [Fact]
    public void TreatsWhitespaceOnlyProviderAsMissing()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse(["--provider", "   "]), AuthCommandKind.Check));
        Assert.Equal("Auth checks require --provider <provider> or --model <model>", error.Message);
    }

    [Fact]
    public void AcceptsModelOnlyForCheck()
    {
        var target = AuthCommands.ValidateAuthCommandArgs(
            CliArgs.Parse(["--model", "gpt-5.5"]), AuthCommandKind.Check);
        Assert.Null(target.Provider);
        Assert.Equal("gpt-5.5", target.Model);
    }

    [Fact]
    public void RejectsUnknownOptionsWithTheFirstFlagName()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse(["--credentails"]), AuthCommandKind.Check));
        Assert.Equal("Unknown option --credentails for \"auth check\".", error.Message);

        var bearerError = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(
                CliArgs.Parse(["--provider", "openai", "--oops"]), AuthCommandKind.ApiKey));
        Assert.Equal("Unknown option --oops for \"auth print-api-key\".", bearerError.Message);
    }

    [Fact]
    public void RejectsNonProviderOptions()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse(["--api-key", "k"]), AuthCommandKind.Check));
        Assert.Equal("Auth commands only accept --provider and --model", error.Message);
    }

    [Fact]
    public void OtherValueFlagsSlipPastTheOptionCheck()
    {
        // TS only inspects apiKey / messages / fileArgs in that guard, so a flag like
        // --system-prompt is accepted by validation and then fails the provider check.
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(
                CliArgs.Parse(["--system-prompt", "be nice"]), AuthCommandKind.Check));
        Assert.Equal("Auth checks require --provider <provider> or --model <model>", error.Message);
    }

    [Fact]
    public void RejectsMessagesAndFileArgs()
    {
        var withMessage = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse(["do the thing"]), AuthCommandKind.Check));
        Assert.Equal("Auth commands only accept --provider and --model", withMessage.Message);

        var withFileArg = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse(["@file.txt"]), AuthCommandKind.Check));
        Assert.Equal("Auth commands only accept --provider and --model", withFileArg.Message);
    }

    [Fact]
    public void CredentialPrintRequiresProviderOrModel()
    {
        var error = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse([]), AuthCommandKind.ApiKey));
        Assert.Equal("Credential printing requires --provider <provider> or --model <model>", error.Message);

        var bearerError = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.ValidateAuthCommandArgs(CliArgs.Parse([]), AuthCommandKind.BearerToken));
        Assert.Equal("Credential printing requires --provider <provider> or --model <model>", bearerError.Message);
    }

    [Fact]
    public void CredentialPrintAcceptsProviderOrModel()
    {
        var byProvider = AuthCommands.ValidateAuthCommandArgs(
            CliArgs.Parse(["--provider", "openai"]), AuthCommandKind.ApiKey);
        Assert.Equal("openai", byProvider.Provider);
        Assert.Null(byProvider.Model);

        var byModel = AuthCommands.ValidateAuthCommandArgs(
            CliArgs.Parse(["--model", "gpt-5.5"]), AuthCommandKind.BearerToken);
        Assert.Null(byModel.Provider);
        Assert.Equal("gpt-5.5", byModel.Model);
    }
}
