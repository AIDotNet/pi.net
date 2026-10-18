using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Port of the <c>main.ts</c> module-level helpers (4g-2). The orchestration itself is exercised
/// end-to-end by <see cref="CliMainEndToEndTests"/>; these cover the pure decisions.
/// </summary>
public class CliMainTests
{
    // ------------------------------------------------------------------ isTruthyEnvFlag

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("yes!", false)]
    [InlineData(" true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTruthyEnvFlagMatchesTs(string? value, bool expected)
    {
        Assert.Equal(expected, Main.IsTruthyEnvFlag(value));
    }

    // ------------------------------------------------------------------ resolveAppMode

    [Fact]
    public void RpcModeWins()
    {
        var parsed = new Args { Mode = CliMode.Rpc };
        Assert.Equal(AppModes.Rpc, Main.ResolveAppMode(parsed, stdinIsTty: true, stdoutIsTty: true));
    }

    [Fact]
    public void JsonModeWins()
    {
        var parsed = new Args { Mode = CliMode.Json };
        Assert.Equal(AppModes.Json, Main.ResolveAppMode(parsed, stdinIsTty: true, stdoutIsTty: true));
    }

    [Fact]
    public void PrintFlagForcesPrintMode()
    {
        var parsed = new Args { Print = true };
        Assert.Equal(AppModes.Print, Main.ResolveAppMode(parsed, stdinIsTty: true, stdoutIsTty: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void NonTtyStreamsFallBackToPrint(bool stdinIsTty, bool stdoutIsTty)
    {
        var parsed = new Args();
        Assert.Equal(AppModes.Print, Main.ResolveAppMode(parsed, stdinIsTty, stdoutIsTty));
    }

    [Fact]
    public void TtyStreamsWithoutFlagsAreInteractive()
    {
        var parsed = new Args();
        Assert.Equal(AppModes.Interactive, Main.ResolveAppMode(parsed, stdinIsTty: true, stdoutIsTty: true));
    }

    [Fact]
    public void TextModeIsNotAFlagSoTtyStaysInteractive()
    {
        // `--mode text` is the default; TS only branches on "rpc" / "json".
        var parsed = new Args { Mode = CliMode.Text };
        Assert.Equal(AppModes.Interactive, Main.ResolveAppMode(parsed, stdinIsTty: true, stdoutIsTty: true));
    }

    // ------------------------------------------------------------------ toPrintOutputMode

    [Theory]
    [InlineData(AppModes.Json, "json")]
    [InlineData(AppModes.Print, "text")]
    [InlineData(AppModes.Rpc, "text")]
    public void ToPrintOutputModeOnlyJsonMapsToJson(string appMode, string expected)
    {
        Assert.Equal(expected, Main.ToPrintOutputMode(appMode));
    }

    // ------------------------------------------------------------------ isPlainRuntimeMetadataCommand

    [Fact]
    public void HelpIsPlainMetadata()
    {
        Assert.True(Main.IsPlainRuntimeMetadataCommand(new Args { Help = true }));
    }

    [Fact]
    public void ListModelsIsPlainMetadata()
    {
        Assert.True(Main.IsPlainRuntimeMetadataCommand(new Args { ListModels = true }));
    }

    [Fact]
    public void ListModelsWithSearchIsPlainMetadata()
    {
        var parsed = new Args { ListModels = true, ListModelsSearch = "claude" };
        Assert.True(Main.IsPlainRuntimeMetadataCommand(parsed));
    }

    [Fact]
    public void PrintDisqualifiesPlainMetadata()
    {
        Assert.False(Main.IsPlainRuntimeMetadataCommand(new Args { Help = true, Print = true }));
    }

    [Fact]
    public void ExplicitModeDisqualifiesPlainMetadata()
    {
        Assert.False(Main.IsPlainRuntimeMetadataCommand(new Args { Help = true, Mode = CliMode.Json }));
    }

    [Fact]
    public void NoFlagsIsNotPlainMetadata()
    {
        Assert.False(Main.IsPlainRuntimeMetadataCommand(new Args()));
    }

    // ------------------------------------------------------------------ resolveCliPaths

    [Fact]
    public void ResolveCliPathsOnlyResolvesLocalPaths()
    {
        var cwd = Path.GetTempPath();
        var resolved = Main.ResolveCliPaths(cwd, ["./local.md", "npm:@foo/bar", "git:github.com/u/r"]);

        Assert.NotNull(resolved);
        Assert.Equal(3, resolved!.Count);
        Assert.Equal(Path.Combine(cwd, "local.md"), resolved[0]);
        Assert.Equal("npm:@foo/bar", resolved[1]);
        Assert.Equal("git:github.com/u/r", resolved[2]);
    }

    [Fact]
    public void ResolveCliPathsPassesNullThrough()
    {
        Assert.Null(Main.ResolveCliPaths(Path.GetTempPath(), null));
    }

    // ------------------------------------------------------------------ validateForkFlags

    [Fact]
    public void ForkAloneIsAccepted()
    {
        var error = new StringWriter();
        Main.ValidateForkFlags(new Args { Fork = "target" }, error, _ => { });
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("session", "Error: --fork cannot be combined with --session")]
    [InlineData("continue", "Error: --fork cannot be combined with --continue")]
    [InlineData("resume", "Error: --fork cannot be combined with --resume")]
    [InlineData("noSession", "Error: --fork cannot be combined with --no-session")]
    public void ForkRejectsConflictingFlags(string conflict, string expectedMessage)
    {
        var parsed = new Args { Fork = "target" };
        switch (conflict)
        {
            case "session": parsed.Session = "s"; break;
            case "continue": parsed.Continue = true; break;
            case "resume": parsed.Resume = true; break;
            default: parsed.NoSession = true; break;
        }

        var error = new StringWriter();
        var codes = new List<int>();
        Main.ValidateForkFlags(parsed, error, codes.Add);

        Assert.Equal(expectedMessage, error.ToString().Trim());
        Assert.Equal([1], codes);
    }

    [Fact]
    public void ForkListsConflictsInTsOrder()
    {
        var parsed = new Args { Fork = "target", Session = "s", Continue = true, Resume = true, NoSession = true };
        var error = new StringWriter();
        Main.ValidateForkFlags(parsed, error, _ => { });

        Assert.Equal(
            "Error: --fork cannot be combined with --session, --continue, --resume, --no-session",
            error.ToString().Trim());
    }

    // ------------------------------------------------------------------ validateSessionIdFlags

    [Fact]
    public void SessionIdAloneIsAccepted()
    {
        var error = new StringWriter();
        var codes = new List<int>();
        Main.ValidateSessionIdFlags(new Args { SessionId = "abc-123" }, error, codes.Add);

        Assert.Equal("", error.ToString());
        Assert.Empty(codes);
    }

    [Fact]
    public void SessionIdRejectsConflictingFlags()
    {
        var parsed = new Args { SessionId = "abc", Session = "s", Continue = true };
        var error = new StringWriter();
        var codes = new List<int>();
        Main.ValidateSessionIdFlags(parsed, error, codes.Add);

        Assert.Equal("Error: --session-id cannot be combined with --session, --continue", error.ToString().Trim());
        Assert.Equal([1], codes);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has/slash")]
    public void SessionIdRejectsInvalidIds(string sessionId)
    {
        var error = new StringWriter();
        var codes = new List<int>();
        Main.ValidateSessionIdFlags(new Args { SessionId = sessionId }, error, codes.Add);

        Assert.StartsWith("Error: ", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([1], codes);
    }

    // ------------------------------------------------------------------ reportDiagnostics

    [Fact]
    public void ReportDiagnosticsPrefixesByType()
    {
        var error = new StringWriter();
        Main.ReportDiagnostics(
            [
                new AgentSessionRuntimeDiagnostic
                {
                    Type = AgentSessionDiagnosticType.Error,
                    Message = "boom",
                },
                new AgentSessionRuntimeDiagnostic
                {
                    Type = AgentSessionDiagnosticType.Warning,
                    Message = "careful",
                },
                new AgentSessionRuntimeDiagnostic
                {
                    Type = AgentSessionDiagnosticType.Info,
                    Message = "fyi",
                },
            ],
            error);

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("Error: boom", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Warning: careful", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("fyi", lines[2], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ resolveSessionPath

    [Fact]
    public async Task PathLikeArgumentsBypassTheSessionIndex()
    {
        var cwd = Path.GetTempPath();
        var resolved = await Main.ResolveSessionPathAsync("./some/session.jsonl", cwd);

        var path = Assert.IsType<ResolvedSession.PathKind>(resolved);
        Assert.Equal(Path.Combine(cwd, "some", "session.jsonl"), path.Path);
    }

    [Fact]
    public async Task BackslashPathsAreAlsoPathLike()
    {
        var cwd = Path.GetTempPath();
        var resolved = await Main.ResolveSessionPathAsync(@"sub\file", cwd);

        Assert.IsType<ResolvedSession.PathKind>(resolved);
    }

    [Fact]
    public async Task UnknownIdIsNotFound()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "pi-main-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            var resolved = await Main.ResolveSessionPathAsync(
                "definitely-not-a-session",
                Path.GetTempPath(),
                sessionDir);

            var notFound = Assert.IsType<ResolvedSession.NotFound>(resolved);
            Assert.Equal("definitely-not-a-session", notFound.Arg);
        }
        finally
        {
            try { Directory.Delete(sessionDir, true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ buildSessionOptions

    [Fact]
    public async Task ProviderWithoutModelIsAnError()
    {
        var (runtime, settings) = await CreateRuntimeAsync();
        var (result, _, diagnostics) = Main.BuildSessionOptions(
            new Args { Provider = "anthropic" },
            [],
            hasExistingSession: false,
            runtime,
            settings);

        Assert.Null(result.Model);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(AgentSessionDiagnosticType.Error, diagnostic.Type);
        Assert.Contains(
            "--provider requires --model (for example: --provider anthropic --model <pattern>)",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitThinkingWinsOverScopedModels()
    {
        var (runtime, settings) = await CreateRuntimeAsync();
        var model = (await runtime.GetAvailableAsync(null)).First();
        var scoped = new List<ScopedModel>
        {
            new() { Model = model, ThinkingLevel = ThinkingLevel.Low },
        };

        var (result, fromModel, _) = Main.BuildSessionOptions(
            new Args { Thinking = ThinkingLevel.High },
            scoped,
            hasExistingSession: true,
            runtime,
            settings);

        Assert.Equal(ThinkingLevel.High, result.ThinkingLevel);
        Assert.False(fromModel);
    }

    [Fact]
    public async Task ScopedModelThinkingIsUsedWhenCliDidNotSetIt()
    {
        var (runtime, settings) = await CreateRuntimeAsync();
        var model = (await runtime.GetAvailableAsync(null)).First();
        var scoped = new List<ScopedModel>
        {
            new() { Model = model, ThinkingLevel = ThinkingLevel.Low },
        };

        var (result, fromModel, _) = Main.BuildSessionOptions(
            new Args(),
            scoped,
            // The scoped-model fallback only applies to a fresh session; an existing one keeps its
            // recorded thinking level.
            hasExistingSession: false,
            runtime,
            settings);

        Assert.Equal(ThinkingLevel.Low, result.ThinkingLevel);
        Assert.False(fromModel);
    }

    [Fact]
    public async Task ScopedModelsAreForwardedWithNullThinkingLevelPreserved()
    {
        var (runtime, settings) = await CreateRuntimeAsync();
        var model = (await runtime.GetAvailableAsync(null)).First();
        var scoped = new List<ScopedModel>
        {
            new() { Model = model, ThinkingLevel = null },
            new() { Model = model, ThinkingLevel = ThinkingLevel.Medium },
        };

        var (result, _, _) = Main.BuildSessionOptions(new Args(), scoped, true, runtime, settings);

        Assert.NotNull(result.ScopedModels);
        Assert.Equal(2, result.ScopedModels!.Count);
        Assert.Null(result.ScopedModels[0].ThinkingLevel);
        Assert.Equal(ThinkingLevel.Medium, result.ScopedModels[1].ThinkingLevel);
    }

    [Fact]
    public async Task NoToolsBeatsNoBuiltinTools()
    {
        var (runtime, settings) = await CreateRuntimeAsync();

        var (all, _, _) = Main.BuildSessionOptions(new Args { NoTools = true, NoBuiltinTools = true }, [], true, runtime, settings);
        Assert.Equal(NoToolsMode.All, all.NoTools);

        var (builtin, _, _) = Main.BuildSessionOptions(new Args { NoBuiltinTools = true }, [], true, runtime, settings);
        Assert.Equal(NoToolsMode.Builtin, builtin.NoTools);

        var (none, _, _) = Main.BuildSessionOptions(new Args(), [], true, runtime, settings);
        Assert.Null(none.NoTools);
    }

    [Fact]
    public async Task ToolsAndExcludeToolsAreCopied()
    {
        var (runtime, settings) = await CreateRuntimeAsync();
        var parsed = new Args
        {
            Tools = ["read", "bash"],
            ExcludeTools = ["write"],
        };

        var (result, _, _) = Main.BuildSessionOptions(parsed, [], true, runtime, settings);

        Assert.Equal(["read", "bash"], result.Tools);
        Assert.Equal(["write"], result.ExcludeTools);
        // The CLI lists are copied, so later mutation of Args must not leak into the options.
        parsed.Tools!.Add("edit");
        Assert.Equal(2, result.Tools!.Count);
    }

    // ------------------------------------------------------------------ auth status text

    [Theory]
    [InlineData(AuthCheckStatus.Ready, "ready")]
    [InlineData(AuthCheckStatus.NotReady, "not_ready")]
    [InlineData(AuthCheckStatus.Invalid, "invalid")]
    public void AuthCheckStatusTextMatchesTsUnion(AuthCheckStatus status, string expected)
    {
        Assert.Equal(expected, Main.AuthCheckStatusText(status));
    }

    // ------------------------------------------------------------------ prepareInitialMessage

    [Fact]
    public async Task PrepareInitialMessageUsesStdinWhenNoFileArgs()
    {
        var (message, images) = await Main.PrepareInitialMessageAsync(new Args(), "piped");

        Assert.Equal("piped", message);
        Assert.Null(images);
    }

    [Fact]
    public async Task PrepareInitialMessageReadsFileArgs()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-main-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "hello");
        try
        {
            var parsed = new Args();
            parsed.FileArgs.Add(path);

            var (message, images) = await Main.PrepareInitialMessageAsync(parsed);

            Assert.NotNull(message);
            Assert.Contains("<file name=", message, StringComparison.Ordinal);
            Assert.Contains("hello", message, StringComparison.Ordinal);
            // TS `initialImages?: ImageContent[]` — the key is absent when there is nothing to attach.
            Assert.Null(images);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ 辅助

    private static async Task<(ModelRuntime Runtime, SettingsManager Settings)> CreateRuntimeAsync()
    {
        var credentials = new MemoryCredentialStore();
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            AllowModelNetwork = false,
            RefreshOnCreate = false,
        });
        return (runtime, SettingsManager.InMemory());
    }

    private sealed class MemoryCredentialStore : Pi.Ai.Auth.ICredentialStore
    {
        private readonly Dictionary<string, Pi.Ai.Auth.Credential> _entries = new(StringComparer.Ordinal);

        public Task<Pi.Ai.Auth.Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(providerId, out var credential) ? credential : null);

        public Task<IReadOnlyList<Pi.Ai.Auth.CredentialInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Pi.Ai.Auth.CredentialInfo>>([]);

        public Task<Pi.Ai.Auth.Credential?> ModifyAsync(
            string providerId,
            Func<Pi.Ai.Auth.Credential?, Task<Pi.Ai.Auth.Credential?>> modify,
            CancellationToken cancellationToken = default) => modify(
            _entries.TryGetValue(providerId, out var current) ? current : null);

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
