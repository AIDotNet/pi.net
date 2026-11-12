using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// v1.5.1 (9b9cb30ae) `pi auth login/logout` 回归。向量逐字对照上游
// packages/coding-agent/test/auth-login.test.ts 与 auth-command.ts 的 help 文案。
// 用脚本化 terminal + runtime 复刻 provider 登录流，不触网、不触真实终端。
// ============================================================================

public sealed class AuthLoginTests
{
    private const string ProviderId = "login-test";
    private const string ProviderName = "Login Test";
    private const string AppName = "pi";

    // ---------------------------------------------------------------- fakes

    private sealed class FakeOAuth : IOAuthAuth
    {
        public string Name => "Login Test account";
        public bool IsSubscription => true;
        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
            => Task.FromResult(credential);
        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    private sealed class FakeApiKey(bool hasLogin, string name = "Login Test API key") : IApiKeyAuth
    {
        public string Name => name;
        public bool HasLogin => hasLogin;
        public Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(null);
    }

    private sealed class FakeProvider(string id, string name, ProviderAuth? auth) : IProvider
    {
        public string Id => id;
        public string Name => name;
        public string? BaseUrl => null;
        public ProviderAuth? Auth => auth;
        public IReadOnlyList<ModelSpec> GetModels() => [];
        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();
        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();
    }

    private sealed class ScriptedTerminal(string[] answers, bool interactive = true) : ILoginTerminal
    {
        public readonly List<string> Lines = [];
        public readonly List<string> Errors = [];
        public readonly List<string> Questions = [];
        public readonly List<string> Opened = [];
        private readonly Queue<string> _answers = new(answers);
        public bool Interactive { get; } = interactive;

        public void Write(string line) => Lines.Add(line);
        public void Error(string line) => Errors.Add(line);
        public void OpenUrl(string url) => Opened.Add(url);

        public Task<string> Question(string message, CancellationToken ct) => Answer(message, ct);
        public Task<string> Secret(string message, CancellationToken ct) => Answer(message, ct);

        private Task<string> Answer(string message, CancellationToken ct)
        {
            Questions.Add(message);
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (_answers.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected prompt: {message}");
            }

            return Task.FromResult(_answers.Dequeue());
        }
    }

    private sealed class ScriptedRuntime : IAuthLoginRuntime
    {
        public List<IProvider> Providers = [];
        public List<CredentialInfo> Stored = [];
        public Func<ProviderAuthInteraction, CredentialKind, Task<Credential>>? LoginScript;
        public Func<string, CancellationToken, Task>? LogoutScript;
        public Func<string, CancellationToken, Task<ModelsRefreshResult>>? RefreshScript;

        public IReadOnlyList<IProvider> GetProviders() => Providers;

        public Task<Credential> LoginAsync(string providerId, CredentialKind type,
            ProviderAuthInteraction interaction, LoginOptions? options)
            => LoginScript is not null
                ? LoginScript(interaction, type)
                : Task.FromResult<Credential>(new Credential.ApiKey("ok"));

        public Task LogoutAsync(string providerId, CancellationToken ct)
            => LogoutScript is not null ? LogoutScript(providerId, ct) : Task.CompletedTask;

        public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CredentialInfo>>(Stored);

        public Task<ModelsRefreshResult> RefreshAsync(string providerId, CancellationToken ct)
            => RefreshScript is not null
                ? RefreshScript(providerId, ct)
                : Task.FromResult(ModelsRefreshResult.Empty);
    }

    // ---------------------------------------------------------------- helpers

    private static ProviderAuth BothMethods() => new()
    {
        OAuth = new FakeOAuth(),
        ApiKey = new FakeApiKey(hasLogin: true),
    };

    private static ScriptedRuntime RuntimeWith(ProviderAuth? auth, string id = ProviderId)
    {
        var runtime = new ScriptedRuntime();
        runtime.Providers.Add(new FakeProvider(id, ProviderName, auth));
        return runtime;
    }

    private static AuthLoginOptions LoginOptions(
        ScriptedRuntime runtime, ScriptedTerminal terminal,
        Func<IReadOnlyList<McpServerEntry>>? servers = null,
        Func<RadiusMcpPlan, bool>? apply = null,
        CancellationToken signal = default,
        string agentDir = "/nonexistent-agent-dir")
        => new(
            runtime, terminal, agentDir, "/nonexistent-cwd", () => "device", signal,
            AppName, "/nonexistent-agent-dir/auth.json", "https://radius.pi.dev/mcp",
            servers ?? (() => []), apply ?? (_ => false), McpServers.Namespace);

    private static AuthLogoutOptions LogoutOptions(
        ScriptedRuntime runtime, ScriptedTerminal terminal, CancellationToken signal = default)
        => new(runtime, terminal, signal, AppName);

    // 上游 bothMethods 的 oauth.login：先 select 方法，再按方法给 auth_url / device_code，
    // 最后要一个 manual_code；api_key.login 要一个 secret。
    private static async Task<Credential> DefaultLoginScript(
        ProviderAuthInteraction interaction, CredentialKind type)
    {
        if (type == CredentialKind.ApiKey)
        {
            var key = await interaction.PromptAsync(
                new AuthPrompt.Secret("Enter Login Test API key"));
            return new Credential.ApiKey(key);
        }

        var method = await interaction.PromptAsync(new AuthPrompt.Select("Sign in to Login Test:",
        [
            new AuthSelectOption("browser", "Sign in with browser"),
            new AuthSelectOption("device", "Sign in with device code"),
        ]));
        if (method == "device")
        {
            interaction.Notify(new AuthEvent.DeviceCode("ABCD-1234", "https://example.test/device"));
            return new Credential.OAuth("refresh", "access-device", 3_600_000);
        }

        interaction.Notify(new AuthEvent.AuthUrl("https://example.test/authorize", "Finish in the browser"));
        var code = await interaction.PromptAsync(new AuthPrompt.ManualCode("Paste the code"));
        return new Credential.OAuth("refresh", $"access-{code}", 3_600_000);
    }

    // ---------------------------------------------------------------- parse leaf

    [Fact]
    public void ParseLoginRequest_RequiresExactlyOneProviderAndKnownOptions()
    {
        var usage = "Usage: pi auth login <provider> [--api-key] [--no-browser]";
        foreach (var args in new[]
                 {
                     Array.Empty<string>(), new[] { "--api-key" },
                     new[] { "--provider", ProviderId }, new[] { ProviderId, "extra" },
                 })
        {
            var parsed = AuthLoginLogic.ParseLoginRequest(args, AppName);
            Assert.False(parsed.Valid);
            Assert.Equal(usage, parsed.Message);
        }
    }

    [Fact]
    public void ParseLoginRequest_RejectsNoBrowserWithApiKey()
    {
        var parsed = AuthLoginLogic.ParseLoginRequest([ProviderId, "--api-key", "--no-browser"], AppName);
        Assert.False(parsed.Valid);
        Assert.Equal("--no-browser only applies to account sign-in; omit it with --api-key.", parsed.Message);
    }

    [Fact]
    public void ParseLoginRequest_AcceptsFlagsInEitherOrder()
    {
        var parsed = AuthLoginLogic.ParseLoginRequest(["--no-browser", ProviderId], AppName);
        Assert.True(parsed.Valid);
        Assert.Equal(ProviderId, parsed.Request!.ProviderRef);
        Assert.False(parsed.Request.ApiKey);
        Assert.False(parsed.Request.Browser);
    }

    // ---------------------------------------------------------------- matching leaf

    [Fact]
    public void FindMatchingProviders_MatchesIdAndNameIgnoringCase()
    {
        var providers = new[] { new FakeProvider(ProviderId, ProviderName, BothMethods()) };
        Assert.Single(AuthLoginLogic.FindMatchingProviders(
            providers, "LOGIN-TEST", p => p.Id, p => p.Name));
        Assert.Single(AuthLoginLogic.FindMatchingProviders(
            providers, "  login test  ", p => p.Id, p => p.Name));
        Assert.Empty(AuthLoginLogic.FindMatchingProviders(
            providers, "nope", p => p.Id, p => p.Name));
        Assert.Empty(AuthLoginLogic.FindMatchingProviders(
            providers, "   ", p => p.Id, p => p.Name));
    }

    // ---------------------------------------------------------------- selectAuthType leaf

    [Fact]
    public void SelectAuthType_AccountByDefault_FallsBackToApiKeyLogin()
    {
        Assert.Equal(CredentialKind.OAuth,
            AuthLoginLogic.SelectAuthType(BothMethods(), ProviderName, false, AppName).Type);
        Assert.Equal(CredentialKind.ApiKey,
            AuthLoginLogic.SelectAuthType(new ProviderAuth { ApiKey = new FakeApiKey(true) },
                ProviderName, false, AppName).Type);
    }

    [Fact]
    public void SelectAuthType_ApiKeyFlagOnOauthOnlyIsUnavailable()
    {
        var choice = AuthLoginLogic.SelectAuthType(
            new ProviderAuth { OAuth = new FakeOAuth() }, ProviderName, true, AppName);
        Assert.False(choice.Available);
        Assert.Equal("Login Test has no API key login. Omit --api-key to sign in.", choice.UnavailableMessage);
    }

    [Fact]
    public void SelectAuthType_ReportsProvidersConfiguredOutsidePi()
    {
        var choice = AuthLoginLogic.SelectAuthType(
            new ProviderAuth { ApiKey = new FakeApiKey(false, "AWS profile") },
            ProviderName, false, AppName);
        Assert.False(choice.Available);
        Assert.Equal("Login Test is configured outside pi (AWS profile).", choice.UnavailableMessage);
    }

    // ---------------------------------------------------------------- URL leaf

    [Fact]
    public void WebUrl_OnlyHttpAndHttpsAreOpenable()
    {
        Assert.Equal("https://example.test/authorize",
            AuthLoginLogic.WebUrl("https://example.test/authorize"));
        Assert.Null(AuthLoginLogic.WebUrl("ftp://example.test/x"));
        Assert.Null(AuthLoginLogic.WebUrl("not a url"));
        // replaceControlCharacters 把成串控制字符折叠为一个空格（对齐上游，防终端注入）。
        Assert.Equal("plain text", AuthLoginLogic.DisplayUrl("plain\u0007text"));
    }

    // ---------------------------------------------------------------- orchestration: errors

    [Fact]
    public async Task Login_InvalidArgsReportsUsage()
    {
        var runtime = RuntimeWith(BothMethods());
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            [ProviderId, "extra"], LoginOptions(runtime, terminal)));
        Assert.Equal(["Usage: pi auth login <provider> [--api-key] [--no-browser]"], terminal.Errors);
    }

    [Fact]
    public async Task Login_UnknownProviderListsLoginProviders()
    {
        var runtime = RuntimeWith(BothMethods());
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            ["nope"], LoginOptions(runtime, terminal)));
        Assert.Equal("Unknown provider \"nope\".", terminal.Errors[0]);
        Assert.Contains(ProviderId, terminal.Errors[1]);
    }

    [Fact]
    public async Task Login_RequiresInteractiveTerminalAfterArgChecks()
    {
        var runtime = RuntimeWith(BothMethods());
        var terminal = new ScriptedTerminal([], interactive: false);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal)));
        Assert.Equal(
            ["\"pi auth login <provider> [--api-key] [--no-browser]\" requires an interactive terminal"],
            terminal.Errors);
    }

    [Fact]
    public async Task Login_ApiKeyFlagOnOauthOnlyReportsUnavailable()
    {
        var runtime = RuntimeWith(new ProviderAuth { OAuth = new FakeOAuth() });
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            [ProviderId, "--api-key"], LoginOptions(runtime, terminal)));
        Assert.Equal(["Login Test has no API key login. Omit --api-key to sign in."], terminal.Errors);
    }

    // ---------------------------------------------------------------- orchestration: oauth method choice

    [Fact]
    public async Task Login_BrowserDefaultTakesFirstMethodWithoutAsking()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = DefaultLoginScript;
        var terminal = new ScriptedTerminal(["the-code"]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            ["login test"], LoginOptions(runtime, terminal)));
        Assert.Equal(["Paste the code: "], terminal.Questions);
        Assert.Contains("Open this URL to sign in:\n  https://example.test/authorize", terminal.Lines);
        Assert.Contains("Finish in the browser", terminal.Lines);
        Assert.Equal(["https://example.test/authorize"], terminal.Opened);
        Assert.Contains(
            "To use \"Sign in with device code\" instead, run \"pi auth login login-test --no-browser\"",
            terminal.Lines);
        Assert.Contains(terminal.Lines, line => line.StartsWith("Logged in to Login Test."));
    }

    [Fact]
    public async Task Login_ListsSeveralOtherSignInMethods()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = async (interaction, _) =>
        {
            await interaction.PromptAsync(new AuthPrompt.Select("Sign in to Login Test:",
            [
                new AuthSelectOption("browser", "Browser"),
                new AuthSelectOption("device", "Device code"),
                new AuthSelectOption("copy", "Copy code"),
            ]));
            interaction.Notify(new AuthEvent.AuthUrl("https://example.test/authorize"));
            return new Credential.OAuth("refresh", "access", 3_600_000);
        };
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal)));
        Assert.Contains(
            "Other sign-in methods: \"Device code\", \"Copy code\". Run \"pi auth login login-test --no-browser\" to choose one.",
            terminal.Lines);
    }

    [Fact]
    public async Task Login_NoOtherMethodOffersNoNoBrowserHint()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = async (interaction, _) =>
        {
            interaction.Notify(new AuthEvent.AuthUrl("https://example.test/authorize"));
            await interaction.PromptAsync(new AuthPrompt.ManualCode("Paste the code"));
            return new Credential.OAuth("refresh", "access", 3_600_000);
        };
        var terminal = new ScriptedTerminal(["the-code"]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal)));
        Assert.Equal(["https://example.test/authorize"], terminal.Opened);
        Assert.DoesNotContain(terminal.Lines, line => line.Contains("--no-browser"));
    }

    [Fact]
    public async Task Login_NoBrowserTakesOnlyOtherMethodAndOpensNothing()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = DefaultLoginScript;
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId, "--no-browser"], LoginOptions(runtime, terminal)));
        Assert.Empty(terminal.Questions);
        Assert.Equal("Sign in to Login Test: Sign in with device code", terminal.Lines[0]);
        Assert.Contains("Open https://example.test/device and enter code: ABCD-1234", terminal.Lines);
        Assert.Empty(terminal.Opened);
        Assert.DoesNotContain(terminal.Lines, line => line.Contains("--no-browser"));
    }

    [Fact]
    public async Task Login_NoBrowserAsksAmongSeveralOthersAndReasks()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = async (interaction, _) =>
        {
            var method = await interaction.PromptAsync(new AuthPrompt.Select("Sign in to Login Test:",
            [
                new AuthSelectOption("browser", "Browser"),
                new AuthSelectOption("device", "Device code"),
                new AuthSelectOption("copy", "Copy code"),
            ]));
            return new Credential.OAuth("refresh", $"access-{method}", 3_600_000);
        };
        var terminal = new ScriptedTerminal(["7", "2"]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            ["--no-browser", ProviderId], LoginOptions(runtime, terminal)));
        Assert.Equal(["Sign in to Login Test:", "  1. Device code", "  2. Copy code"],
            terminal.Lines.Take(3));
        Assert.Contains("Enter a number from 1 to 2.", terminal.Lines);
    }

    [Fact]
    public async Task Login_NoBrowserKeepsOnlySignInMethodWithoutOpening()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = async (interaction, _) =>
        {
            var method = await interaction.PromptAsync(new AuthPrompt.Select("Sign in to Login Test:",
            [
                new AuthSelectOption("browser", "Browser"),
            ]));
            interaction.Notify(new AuthEvent.AuthUrl("https://example.test/authorize"));
            return new Credential.OAuth("refresh", $"access-{method}", 3_600_000);
        };
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId, "--no-browser"], LoginOptions(runtime, terminal)));
        Assert.Empty(terminal.Questions);
        Assert.Empty(terminal.Opened);
    }

    [Fact]
    public async Task Login_ApiKeySetupAsksForSelections()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = async (interaction, _) =>
        {
            var key = await interaction.PromptAsync(new AuthPrompt.Select("Choose credentials:",
            [
                new AuthSelectOption("first", "First"),
                new AuthSelectOption("second", "Second", "Other credentials"),
            ]));
            return new Credential.ApiKey(key);
        };
        var terminal = new ScriptedTerminal(["2"]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId, "--api-key"], LoginOptions(runtime, terminal)));
        Assert.Equal(["Select 1-2: "], terminal.Questions);
        Assert.Contains("  2. Second - Other credentials", terminal.Lines);
    }

    // ---------------------------------------------------------------- orchestration: failures

    [Fact]
    public async Task Login_ReportsCommittedLoginSeparatelyFromSyncFailure()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = (_, _) => Task.FromException<Credential>(new CredentialSynchronizationError(
            ProviderId, CredentialSynchronizationOperation.Login,
            new Credential.ApiKey("saved"), new Exception("snapshot failed")));
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal)));
        Assert.Contains(
            "Logged in to Login Test, but local model state could not be synchronized:", terminal.Errors[0]);
    }

    [Fact]
    public async Task Login_ReportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = DefaultLoginScript;
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal, signal: cts.Token)));
        Assert.Equal(["Login cancelled."], terminal.Errors);
    }

    [Fact]
    public async Task Login_CatalogRefreshTimeoutIsReported()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LoginScript = (_, _) => Task.FromResult<Credential>(new Credential.OAuth("r", "a", 3_600_000));
        runtime.RefreshScript = (_, _) => Task.FromResult(
            new ModelsRefreshResult(true, new Dictionary<string, Exception>()));
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(0, await AuthLoginCommand.RunAsync(
            [ProviderId], LoginOptions(runtime, terminal)));
        Assert.Contains(
            "The Login Test model catalog refresh timed out; pi refreshes it on next start.", terminal.Lines);
    }

    // ---------------------------------------------------------------- Radius MCP planning

    [Fact]
    public void PlanRadiusMcpServer_AvoidsOccupiedNamespaces()
    {
        McpServerEntry Server(string name) => new(
            name, new McpServerConfig(new JsonObject { ["command"] = "original" }), "global");
        IReadOnlyList<McpServerEntry> servers =
        [
            Server("radius"), Server("radius-mcp"), Server("radius-mcp-2"),
        ];
        var plan = AuthLoginLogic.PlanRadiusMcpServer(
            servers, "radius", "/agent/mcp.json", "https://radius.pi.dev/mcp", McpServers.Namespace);
        Assert.NotNull(plan);
        Assert.Equal("radius-mcp-3", plan.Name);
        Assert.Equal("/agent/mcp.json", plan.McpPath);
        Assert.Equal("https://radius.pi.dev/mcp", plan.Config.Url);
        Assert.Equal("radius", plan.Config.AuthProvider);
    }

    [Fact]
    public void PlanRadiusMcpServer_ReturnsNullWhenAlreadyPointingAtLogin()
    {
        McpServerEntry existing = new(
            "radius",
            new McpServerConfig(new JsonObject
            {
                ["url"] = "https://radius.pi.dev/mcp",
                ["auth"] = new JsonObject { ["provider"] = "radius" },
            }),
            "global");
        Assert.Null(AuthLoginLogic.PlanRadiusMcpServer(
            [existing], "radius", "/agent/mcp.json", "https://radius.pi.dev/mcp", McpServers.Namespace));
    }

    [Fact]
    public void PlanRadiusMcpServer_ReplacesOauthWithProviderAuth()
    {
        McpServerEntry existing = new(
            "radius",
            new McpServerConfig(new JsonObject
            {
                ["url"] = "https://radius.pi.dev/mcp/",
                ["oauth"] = new JsonObject { ["clientId"] = "x" },
            }),
            "global");
        var plan = AuthLoginLogic.PlanRadiusMcpServer(
            [existing], "radius", "/agent/mcp.json", "https://radius.pi.dev/mcp", McpServers.Namespace);
        Assert.NotNull(plan);
        Assert.Equal("radius", plan.Name);
        Assert.Equal("radius", plan.Config.AuthProvider);
        Assert.Null(plan.Config.OAuth);
    }

    // ---------------------------------------------------------------- logout

    [Fact]
    public async Task Logout_UsageRejectsMissingOrExtraArgs()
    {
        var runtime = RuntimeWith(BothMethods());
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--provider" }, new[] { "a", "b" } })
        {
            var terminal = new ScriptedTerminal([]);
            Assert.Equal(1, await AuthLogoutCommand.RunAsync(
                args, LogoutOptions(runtime, terminal)));
            Assert.Equal(["Usage: pi auth logout <provider>"], terminal.Errors);
        }
    }

    [Fact]
    public async Task Logout_NoStoredCredentialsSucceeds()
    {
        var runtime = RuntimeWith(BothMethods());
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(0, await AuthLogoutCommand.RunAsync(
            [ProviderId], LogoutOptions(runtime, terminal)));
        Assert.Equal(
            ["No stored credentials for Login Test. Environment variables and models.json config are unchanged."],
            terminal.Lines);
    }

    [Fact]
    public async Task Logout_OauthAndApiKeyMessages()
    {
        foreach (var (type, expected) in new[]
                 {
                     (CredentialKind.OAuth, "Logged out of Login Test."),
                     (CredentialKind.ApiKey,
                         "Removed stored API key for Login Test. Environment variables and models.json config are unchanged."),
                 })
        {
            var runtime = RuntimeWith(BothMethods());
            runtime.Stored.Add(new CredentialInfo(ProviderId, type));
            var terminal = new ScriptedTerminal([]);
            Assert.Equal(0, await AuthLogoutCommand.RunAsync(
                [ProviderId], LogoutOptions(runtime, terminal)));
            Assert.Equal([expected], terminal.Lines);
        }
    }

    [Fact]
    public async Task Logout_UnknownProviderListsStoredIds()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.Stored.Add(new CredentialInfo("z-provider", CredentialKind.ApiKey));
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLogoutCommand.RunAsync(
            ["nope"], LogoutOptions(runtime, terminal)));
        Assert.Equal("Unknown provider \"nope\".", terminal.Errors[0]);
        Assert.Equal("Stored credentials: z-provider", terminal.Errors[1]);
    }

    [Fact]
    public async Task Logout_SyncFailureIsReportedSeparately()
    {
        var runtime = RuntimeWith(BothMethods());
        runtime.LogoutScript = (_, _) => Task.FromException(new CredentialSynchronizationError(
            ProviderId, CredentialSynchronizationOperation.Logout, null, new Exception("snapshot failed")));
        runtime.Stored.Add(new CredentialInfo(ProviderId, CredentialKind.OAuth));
        var terminal = new ScriptedTerminal([]);
        Assert.Equal(1, await AuthLogoutCommand.RunAsync(
            [ProviderId], LogoutOptions(runtime, terminal)));
        Assert.Contains(
            "Credentials removed for Login Test, but local model state could not be synchronized:", terminal.Errors[0]);
    }
}
