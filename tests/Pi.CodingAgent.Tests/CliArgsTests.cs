// ============================================================================
// Tests for the CLI argument parser (TS `cli/args.ts`), landed with 4g-1.
//
// Mirrors test/args.test.ts case-for-case: every describe block below maps to
// the same block upstream, and every expectation was transcribed from the TS
// bodies (not back-filled from the C# implementation). Extra cases at the end
// pin the TS behaviours args.test.ts leaves implicit:
//   - value flags with no trailing value fall through to the unknown-flag
//     capture (`--provider` alone records `unknownFlags{provider: true}`),
//   - `--` ends option parsing and everything after it is positional,
//   - a bare `-` is an unknown option,
//   - `--list-models` tri-state,
//   - the VALID_THINKING_LEVELS table against the ThinkingLevel enum,
//   - printHelp section layout.
// ============================================================================

using System.Text;
using Pi.Ai.Types;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CliArgsTests
{
    private static object? UnknownFlag(Args args, string name)
        => args.UnknownFlags.TryGetValue(name, out var value) ? value : null;

    private static void AssertUnknownFlagValue(Args args, string name, string expected)
        => Assert.Equal(expected, UnknownFlag(args, name));

    private static void AssertUnknownFlagTrue(Args args, string name)
        => Assert.True(Assert.IsType<bool>(UnknownFlag(args, name)));

    private static AgentSessionRuntimeDiagnostic Error(string message)
        => new() { Type = AgentSessionDiagnosticType.Error, Message = message };

    private static AgentSessionRuntimeDiagnostic Warning(string message)
        => new() { Type = AgentSessionDiagnosticType.Warning, Message = message };

    // ---------------------------------------------------------- --version flag

    [Fact]
    public void ParsesVersionFlag()
    {
        var result = CliArgs.Parse(["--version"]);
        Assert.True(result.Version);
    }

    [Fact]
    public void ParsesVersionShorthand()
    {
        var result = CliArgs.Parse(["-v"]);
        Assert.True(result.Version);
    }

    [Fact]
    public void VersionTakesPrecedenceOverOtherArgs()
    {
        var result = CliArgs.Parse(["--version", "--help", "some message"]);
        Assert.True(result.Version);
        Assert.True(result.Help);
        Assert.Contains("some message", result.Messages);
    }

    // ------------------------------------------------------------- --help flag

    [Fact]
    public void ParsesHelpFlag()
    {
        var result = CliArgs.Parse(["--help"]);
        Assert.True(result.Help);
    }

    [Fact]
    public void ParsesHelpShorthand()
    {
        var result = CliArgs.Parse(["-h"]);
        Assert.True(result.Help);
    }

    // ------------------------------------------------------------- --print flag

    [Fact]
    public void ParsesPrintFlag()
    {
        var result = CliArgs.Parse(["--print"]);
        Assert.True(result.Print);
    }

    [Fact]
    public void ParsesPrintShorthand()
    {
        var result = CliArgs.Parse(["-p"]);
        Assert.True(result.Print);
    }

    [Fact]
    public void ParsesPromptAfterPrintEvenWithYamlFrontmatter()
    {
        var prompt = "---\ntitle: hello\n---\nSay hi.";
        var result = CliArgs.Parse(["-p", prompt]);
        Assert.True(result.Print);
        Assert.Equal([prompt], result.Messages);
        Assert.Empty(result.UnknownFlags);
    }

    [Fact]
    public void DoesNotConsumeOptionsAfterPrintAsPrompts()
    {
        var result = CliArgs.Parse(["-p", "--provider", "openai", "Say hi."]);
        Assert.True(result.Print);
        Assert.Equal("openai", result.Provider);
        Assert.Equal(["Say hi."], result.Messages);
    }

    // ----------------------------------------------------------- --continue flag

    [Fact]
    public void ParsesContinueFlag()
    {
        var result = CliArgs.Parse(["--continue"]);
        Assert.True(result.Continue);
    }

    [Fact]
    public void ParsesContinueShorthand()
    {
        var result = CliArgs.Parse(["-c"]);
        Assert.True(result.Continue);
    }

    // ------------------------------------------------------------- --resume flag

    [Fact]
    public void ParsesResumeFlag()
    {
        var result = CliArgs.Parse(["--resume"]);
        Assert.True(result.Resume);
    }

    [Fact]
    public void ParsesResumeShorthand()
    {
        var result = CliArgs.Parse(["-r"]);
        Assert.True(result.Resume);
    }

    // ------------------------------------------------------- flags with values

    [Fact]
    public void ParsesProvider()
    {
        var result = CliArgs.Parse(["--provider", "openai"]);
        Assert.Equal("openai", result.Provider);
    }

    [Fact]
    public void ParsesModel()
    {
        var result = CliArgs.Parse(["--model", "gpt-4o"]);
        Assert.Equal("gpt-4o", result.Model);
    }

    [Fact]
    public void ParsesApiKey()
    {
        var result = CliArgs.Parse(["--api-key", "sk-test-key"]);
        Assert.Equal("sk-test-key", result.ApiKey);
    }

    [Fact]
    public void ParsesSystemPrompt()
    {
        var result = CliArgs.Parse(["--system-prompt", "You are a helpful assistant"]);
        Assert.Equal("You are a helpful assistant", result.SystemPrompt);
    }

    [Fact]
    public void ParsesAppendSystemPrompt()
    {
        var result = CliArgs.Parse(["--append-system-prompt", "Additional context"]);
        Assert.Equal(["Additional context"], result.AppendSystemPrompt);
    }

    [Fact]
    public void ParsesMultipleAppendSystemPromptFlags()
    {
        var result = CliArgs.Parse(["--append-system-prompt", "Context A", "--append-system-prompt", "Context B"]);
        Assert.Equal(["Context A", "Context B"], result.AppendSystemPrompt);
    }

    [Fact]
    public void ParsesSession()
    {
        var result = CliArgs.Parse(["--session", "/path/to/session.jsonl"]);
        Assert.Equal("/path/to/session.jsonl", result.Session);
    }

    [Fact]
    public void ParsesSessionId()
    {
        var result = CliArgs.Parse(["--session-id", "orchestrated-session"]);
        Assert.Equal("orchestrated-session", result.SessionId);
    }

    [Fact]
    public void ParsesFork()
    {
        var result = CliArgs.Parse(["--fork", "1234abcd"]);
        Assert.Equal("1234abcd", result.Fork);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ParsesExport()
    {
        var result = CliArgs.Parse(["--export", "session.jsonl"]);
        Assert.Equal("session.jsonl", result.Export);
    }

    [Fact]
    public void ParsesThinking()
    {
        var result = CliArgs.Parse(["--thinking", "high"]);
        Assert.Equal(ThinkingLevel.High, result.Thinking);
    }

    [Fact]
    public void ParsesModelsAsCommaSeparatedList()
    {
        var result = CliArgs.Parse(["--models", "gpt-4o,claude-sonnet,gemini-pro"]);
        Assert.Equal(["gpt-4o", "claude-sonnet", "gemini-pro"], result.Models);
    }

    // Issue #10334
    [Fact]
    public void IgnoresEmptyEntriesInModels()
    {
        var result = CliArgs.Parse(["--models", "gpt-4o, ,claude-sonnet,"]);
        Assert.Equal(["gpt-4o", "claude-sonnet"], result.Models);
    }

    // ---------------------------------------------------------------- --mode flag
    // Issue #9045

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("rpc")]
    public void ParsesMode(string mode)
    {
        var result = CliArgs.Parse(["--mode", mode]);
        Assert.Equal(CliModeText.Parse(mode), result.Mode);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("")]
    public void RejectsInvalidModeValue(string mode)
    {
        var result = CliArgs.Parse(["--mode", mode, "--version"]);
        Assert.Null(result.Mode);
        Assert.True(result.Version);
        Assert.Empty(result.Messages);
        Assert.Empty(result.UnknownFlags);
        Assert.Equal(
            [Error($"Invalid mode \"{mode}\". Valid values: text, json, rpc")],
            result.Diagnostics);
    }

    [Fact]
    public void ReportsMissingModeValue()
    {
        var result = CliArgs.Parse(["--mode"]);
        Assert.Null(result.Mode);
        Assert.Empty(result.UnknownFlags);
        Assert.Equal([Error("--mode requires text, json, or rpc")], result.Diagnostics);
    }

    [Fact]
    public void DoesNotConsumeAnotherOptionAsModeValue()
    {
        var result = CliArgs.Parse(["--mode", "--version"]);
        Assert.Null(result.Mode);
        Assert.True(result.Version);
        Assert.Empty(result.UnknownFlags);
        Assert.Equal([Error("--mode requires text, json, or rpc")], result.Diagnostics);
    }

    [Fact]
    public void ReportsInvalidModeValueAfterAValidOne()
    {
        var result = CliArgs.Parse(["--mode", "json", "--mode", "yaml"]);
        Assert.Equal(
            [Error("Invalid mode \"yaml\". Valid values: text, json, rpc")],
            result.Diagnostics);
    }

    // ---------------------------------------------------------------- --name flag

    [Fact]
    public void ParsesNameFlagWithValue()
    {
        var result = CliArgs.Parse(["--name", "my-session"]);
        Assert.Equal("my-session", result.Name);
    }

    [Fact]
    public void ParsesNameShorthand()
    {
        var result = CliArgs.Parse(["-n", "quick-session"]);
        Assert.Equal("quick-session", result.Name);
    }

    [Fact]
    public void PreservesEmptyNameValuesForMainValidation()
    {
        var result = CliArgs.Parse(["--name", ""]);
        Assert.Equal("", result.Name);
    }

    [Fact]
    public void NormalizesDisplayNamesAndRejectsWhitespaceOnlyValues()
    {
        Assert.Equal("named session", CliArgs.NormalizeSessionName("  named session  "));
        Assert.Null(CliArgs.NormalizeSessionName("   "));
    }

    [Fact]
    public void ReportsMissingNameValue()
    {
        var result = CliArgs.Parse(["--name"]);
        Assert.Equal([Error("--name requires a value")], result.Diagnostics);
    }

    [Fact]
    public void NameWorksAlongsideOtherFlags()
    {
        var result = CliArgs.Parse(["--name", "named-run", "--print", "--model", "gpt-4o", "hello"]);
        Assert.Equal("named-run", result.Name);
        Assert.True(result.Print);
        Assert.Equal("gpt-4o", result.Model);
        Assert.Equal(["hello"], result.Messages);
    }

    // ------------------------------------------------------------ --no-session flag

    [Fact]
    public void ParsesNoSessionFlag()
    {
        var result = CliArgs.Parse(["--no-session"]);
        Assert.True(result.NoSession);
    }

    [Fact]
    public void PreservesCustomSessionIdsForNonPersistingCommands()
    {
        var withHelp = CliArgs.Parse(["--session-id", "ephemeral-id", "--help"]);
        Assert.Equal("ephemeral-id", withHelp.SessionId);
        Assert.True(withHelp.Help);

        var withListModels = CliArgs.Parse(["--session-id", "ephemeral-id", "--list-models"]);
        Assert.Equal("ephemeral-id", withListModels.SessionId);
        Assert.True(withListModels.ListModels);

        var withNoSession = CliArgs.Parse(["--session-id", "ephemeral-id", "--no-session"]);
        Assert.Equal("ephemeral-id", withNoSession.SessionId);
        Assert.True(withNoSession.NoSession);
    }

    // ------------------------------------------------------------ --extension flag

    [Fact]
    public void ParsesSingleExtension()
    {
        var result = CliArgs.Parse(["--extension", "./my-extension.ts"]);
        Assert.Equal(["./my-extension.ts"], result.Extensions);
    }

    [Fact]
    public void ParsesExtensionShorthand()
    {
        var result = CliArgs.Parse(["-e", "./my-extension.ts"]);
        Assert.Equal(["./my-extension.ts"], result.Extensions);
    }

    [Fact]
    public void ParsesMultipleExtensionFlags()
    {
        var result = CliArgs.Parse(["--extension", "./ext1.ts", "-e", "./ext2.ts"]);
        Assert.Equal(["./ext1.ts", "./ext2.ts"], result.Extensions);
    }

    // --------------------------------------------------------- --no-extensions flag

    [Fact]
    public void ParsesNoExtensionsFlag()
    {
        var result = CliArgs.Parse(["--no-extensions"]);
        Assert.True(result.NoExtensions);
    }

    [Fact]
    public void ParsesNoExtensionsWithExplicitExtensionFlags()
    {
        var result = CliArgs.Parse(["--no-extensions", "-e", "foo.ts", "-e", "bar.ts"]);
        Assert.True(result.NoExtensions);
        Assert.Equal(["foo.ts", "bar.ts"], result.Extensions);
    }

    // ---------------------------------------------------------------- --no-mcp flag

    [Fact]
    public void ParsesNoMcpFlag()
    {
        var result = CliArgs.Parse(["--no-mcp"]);
        Assert.True(result.NoMcp);
        Assert.Empty(result.UnknownFlags);
    }

    // ---------------------------------------------------------------- --skill flag

    [Fact]
    public void ParsesSingleSkill()
    {
        var result = CliArgs.Parse(["--skill", "./skill-dir"]);
        Assert.Equal(["./skill-dir"], result.Skills);
    }

    [Fact]
    public void ParsesMultipleSkillFlags()
    {
        var result = CliArgs.Parse(["--skill", "./skill-a", "--skill", "./skill-b"]);
        Assert.Equal(["./skill-a", "./skill-b"], result.Skills);
    }

    // -------------------------------------------------------- --prompt-template flag

    [Fact]
    public void ParsesSinglePromptTemplate()
    {
        var result = CliArgs.Parse(["--prompt-template", "./prompts"]);
        Assert.Equal(["./prompts"], result.PromptTemplates);
    }

    [Fact]
    public void ParsesMultiplePromptTemplateFlags()
    {
        var result = CliArgs.Parse(["--prompt-template", "./one", "--prompt-template", "./two"]);
        Assert.Equal(["./one", "./two"], result.PromptTemplates);
    }

    // ---------------------------------------------------------------- --theme flag

    [Fact]
    public void ParsesSingleTheme()
    {
        var result = CliArgs.Parse(["--theme", "./theme.json"]);
        Assert.Equal(["./theme.json"], result.Themes);
    }

    [Fact]
    public void ParsesMultipleThemeFlags()
    {
        var result = CliArgs.Parse(["--theme", "./dark.json", "--theme", "./light.json"]);
        Assert.Equal(["./dark.json", "./light.json"], result.Themes);
    }

    // ------------------------------------------------------------- --use-theme flag

    [Fact]
    public void ParsesUseTheme()
    {
        var result = CliArgs.Parse(["--use-theme", "light"]);
        Assert.Equal("light", result.UseTheme);
    }

    [Fact]
    public void ReportsWhenThemeNameValueIsMissing()
    {
        var result = CliArgs.Parse(["--use-theme", "--print"]);
        Assert.Null(result.UseTheme);
        Assert.True(result.Print);
        Assert.Equal([Error("--use-theme requires a theme name")], result.Diagnostics);
    }

    // ------------------------------------------------------------ --no-skills flag

    [Fact]
    public void ParsesNoSkillsFlag()
    {
        var result = CliArgs.Parse(["--no-skills"]);
        Assert.True(result.NoSkills);
    }

    // -------------------------------------------------- --no-prompt-templates flag

    [Fact]
    public void ParsesNoPromptTemplatesFlag()
    {
        var result = CliArgs.Parse(["--no-prompt-templates"]);
        Assert.True(result.NoPromptTemplates);
    }

    // -------------------------------------------------------------- --no-themes flag

    [Fact]
    public void ParsesNoThemesFlag()
    {
        var result = CliArgs.Parse(["--no-themes"]);
        Assert.True(result.NoThemes);
    }

    // ------------------------------------------------------ --no-context-files flag

    [Fact]
    public void ParsesNoContextFilesFlag()
    {
        var result = CliArgs.Parse(["--no-context-files"]);
        Assert.True(result.NoContextFiles);
    }

    [Fact]
    public void ParsesNoContextFilesShorthand()
    {
        var result = CliArgs.Parse(["-nc"]);
        Assert.True(result.NoContextFiles);
    }

    // -------------------------------------------------------- project approval flags

    [Fact]
    public void ParsesApprove()
    {
        var result = CliArgs.Parse(["--approve"]);
        Assert.True(result.ProjectTrustOverride);
    }

    [Fact]
    public void ParsesApproveShorthand()
    {
        var result = CliArgs.Parse(["-a"]);
        Assert.True(result.ProjectTrustOverride);
    }

    [Fact]
    public void ParsesNoApprove()
    {
        var result = CliArgs.Parse(["--no-approve"]);
        Assert.False(result.ProjectTrustOverride);
    }

    [Fact]
    public void ParsesNoApproveShorthand()
    {
        var result = CliArgs.Parse(["-na"]);
        Assert.False(result.ProjectTrustOverride);
    }

    // --------------------------------------------------------------- --verbose flag

    [Fact]
    public void ParsesVerboseFlag()
    {
        var result = CliArgs.Parse(["--verbose"]);
        Assert.True(result.Verbose);
    }

    // --------------------------------------------------------------- --offline flag

    [Fact]
    public void ParsesOfflineFlag()
    {
        var result = CliArgs.Parse(["--offline"]);
        Assert.True(result.Offline);
    }

    // -------------------------------------------------------------- --tui-mode flag

    [Theory]
    [InlineData("regular")]
    [InlineData("fullscreen")]
    public void ParsesTuiMode(string mode)
    {
        var result = CliArgs.Parse(["--tui-mode", mode]);
        Assert.Equal(mode == "regular" ? Pi.Tui.TuiMode.Regular : Pi.Tui.TuiMode.Fullscreen, result.TuiMode);
    }

    [Fact]
    public void RejectsInvalidTuiModes()
    {
        var result = CliArgs.Parse(["--tui-mode", "other"]);
        Assert.Equal(
            [Error("Invalid TUI mode \"other\". Valid values: regular, fullscreen")],
            result.Diagnostics);
    }

    [Fact]
    public void RequiresATuiMode()
    {
        var result = CliArgs.Parse(["--tui-mode"]);
        Assert.Equal([Error("--tui-mode requires regular or fullscreen")], result.Diagnostics);
    }

    [Fact]
    public void DoesNotRecognizeTheOldUiModeFlag()
    {
        var result = CliArgs.Parse(["--ui-mode", "fullscreen"]);
        Assert.Null(result.TuiMode);
        AssertUnknownFlagValue(result, "ui-mode", "fullscreen");
    }

    // ------------------------------------------------------------------ tool flags

    [Fact]
    public void ParsesNoToolsFlag()
    {
        var result = CliArgs.Parse(["--no-tools"]);
        Assert.True(result.NoTools);
    }

    [Fact]
    public void ParsesNoToolsShorthand()
    {
        var result = CliArgs.Parse(["-nt"]);
        Assert.True(result.NoTools);
    }

    [Fact]
    public void ParsesNoBuiltinToolsFlag()
    {
        var result = CliArgs.Parse(["--no-builtin-tools"]);
        Assert.True(result.NoBuiltinTools);
    }

    [Fact]
    public void ParsesNoBuiltinToolsShorthand()
    {
        var result = CliArgs.Parse(["-nbt"]);
        Assert.True(result.NoBuiltinTools);
    }

    [Fact]
    public void ParsesToolsFlag()
    {
        var result = CliArgs.Parse(["--tools", "read,bash"]);
        Assert.Equal(["read", "bash"], result.Tools);
    }

    [Fact]
    public void ParsesToolsShorthand()
    {
        var result = CliArgs.Parse(["-t", "read,bash"]);
        Assert.Equal(["read", "bash"], result.Tools);
    }

    [Fact]
    public void ParsesToolModifiers()
    {
        var result = CliArgs.Parse(["-t", "+codemode,-write"]);
        Assert.Equal(["+codemode", "-write"], result.Tools);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RejectsToolNamesMixedWithModifiers()
    {
        var result = CliArgs.Parse(["--tools", "read,+codemode"]);
        Assert.Null(result.Tools);
        Assert.Equal(
            [Error("--tools: tool names cannot be mixed with +name or -name entries")],
            result.Diagnostics);
    }

    [Fact]
    public void RejectsPatternsInToolModifiers()
    {
        var result = CliArgs.Parse(["-t", "+mcp__radius__*"]);
        Assert.Null(result.Tools);
        Assert.Equal(
            [Error("-t: +name and -name entries take exact tool names, not patterns: +mcp__radius__*")],
            result.Diagnostics);
    }

    [Fact]
    public void ParsesExcludeToolsFlag()
    {
        var result = CliArgs.Parse(["--exclude-tools", "read,bash"]);
        Assert.Equal(["read", "bash"], result.ExcludeTools);
    }

    [Fact]
    public void ParsesExcludeToolsShorthand()
    {
        var result = CliArgs.Parse(["-xt", "read,bash"]);
        Assert.Equal(["read", "bash"], result.ExcludeTools);
    }

    [Fact]
    public void ParsesNoToolsWithExplicitToolsFlags()
    {
        var result = CliArgs.Parse(["--no-tools", "--tools", "read,bash"]);
        Assert.True(result.NoTools);
        Assert.Equal(["read", "bash"], result.Tools);
    }

    [Fact]
    public void ParsesNoBuiltinToolsWithExplicitToolsFlags()
    {
        var result = CliArgs.Parse(["--no-builtin-tools", "--tools", "read,bash"]);
        Assert.True(result.NoBuiltinTools);
        Assert.Equal(["read", "bash"], result.Tools);
    }

    // ------------------------------------------------------ messages and file args

    [Fact]
    public void ParsesPlainTextMessages()
    {
        var result = CliArgs.Parse(["hello", "world"]);
        Assert.Equal(["hello", "world"], result.Messages);
    }

    [Fact]
    public void ParsesFileArguments()
    {
        var result = CliArgs.Parse(["@README.md", "@src/main.ts"]);
        Assert.Equal(["README.md", "src/main.ts"], result.FileArgs);
    }

    [Fact]
    public void ParsesMixedMessagesAndFileArgs()
    {
        var result = CliArgs.Parse(["@file.txt", "explain this", "@image.png"]);
        Assert.Equal(["file.txt", "image.png"], result.FileArgs);
        Assert.Equal(["explain this"], result.Messages);
    }

    [Fact]
    public void CapturesUnknownLongFlagsWithStringValues()
    {
        var result = CliArgs.Parse(["--unknown-flag", "message"]);
        Assert.Empty(result.Messages);
        AssertUnknownFlagValue(result, "unknown-flag", "message");
    }

    [Fact]
    public void CapturesUnknownBooleanLongFlags()
    {
        var result = CliArgs.Parse(["--unknown-flag"]);
        AssertUnknownFlagTrue(result, "unknown-flag");
    }

    [Fact]
    public void CapturesUnknownLongFlagsWithEqualsSyntax()
    {
        var result = CliArgs.Parse(["--unknown-flag=value"]);
        AssertUnknownFlagValue(result, "unknown-flag", "value");
    }

    // ---------------------------------------------------------- complex combinations

    [Fact]
    public void ParsesMultipleFlagsTogether()
    {
        var result = CliArgs.Parse(
        [
            "--provider", "anthropic",
            "--model", "claude-sonnet",
            "--print",
            "--thinking", "high",
            "@prompt.md",
            "Do the task",
        ]);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("claude-sonnet", result.Model);
        Assert.True(result.Print);
        Assert.Equal(ThinkingLevel.High, result.Thinking);
        Assert.Equal(["prompt.md"], result.FileArgs);
        Assert.Equal(["Do the task"], result.Messages);
    }

    // ======================================================================
    // TS behaviours args.test.ts leaves implicit — pinned here so the port
    // cannot silently "fix" them.
    // ======================================================================

    [Theory]
    [InlineData("--provider")]
    [InlineData("--model")]
    [InlineData("--api-key")]
    [InlineData("--session")]
    [InlineData("--fork")]
    [InlineData("--session-dir")]
    [InlineData("--export")]
    [InlineData("--append-system-prompt")]
    [InlineData("--tools")]
    [InlineData("--exclude-tools")]
    [InlineData("--extension")]
    [InlineData("--skill")]
    [InlineData("--prompt-template")]
    [InlineData("--theme")]
    public void ValueFlagWithoutTrailingValueFallsThroughToUnknownFlagCapture(string flag)
    {
        // TS guards value flags with `i + 1 < args.length`, so a trailing flag with
        // no value is NOT diagnosed — it falls to the generic `--flag` capture and
        // records `unknownFlags{<name>: true}`.
        var result = CliArgs.Parse([flag]);
        Assert.Empty(result.Diagnostics);
        AssertUnknownFlagTrue(result, flag[2..]);
    }

    [Fact]
    public void DoubleDashEndsOptionParsing()
    {
        var result = CliArgs.Parse(["--", "-p", "@f.txt", "msg"]);
        Assert.Null(result.Print);
        Assert.Equal(["-p", "msg"], result.Messages);
        Assert.Equal(["f.txt"], result.FileArgs);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.UnknownFlags);
    }

    [Fact]
    public void BareDashIsAnUnknownOption()
    {
        var result = CliArgs.Parse(["-"]);
        Assert.Equal([Error("Unknown option: -")], result.Diagnostics);
    }

    [Fact]
    public void EmptyStringIsAMessage()
    {
        var result = CliArgs.Parse([""]);
        Assert.Equal([""], result.Messages);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ListModelsWithoutSearchTerm()
    {
        var result = CliArgs.Parse(["--list-models"]);
        Assert.True(result.ListModels);
        Assert.Null(result.ListModelsSearch);
    }

    [Fact]
    public void ListModelsWithSearchTerm()
    {
        var result = CliArgs.Parse(["--list-models", "sonnet"]);
        Assert.True(result.ListModels);
        Assert.Equal("sonnet", result.ListModelsSearch);
    }

    [Fact]
    public void ListModelsDoesNotConsumeFlagsOrFileArgs()
    {
        var result = CliArgs.Parse(["--list-models", "--print"]);
        Assert.True(result.ListModels);
        Assert.Null(result.ListModelsSearch);
        Assert.True(result.Print);

        var withFileArg = CliArgs.Parse(["--list-models", "@models.txt"]);
        Assert.True(withFileArg.ListModels);
        Assert.Null(withFileArg.ListModelsSearch);
        Assert.Equal(["models.txt"], withFileArg.FileArgs);
    }

    [Fact]
    public void ListModelsWithEmptySearchTerm()
    {
        // TS records `""` (a string, hence a search pattern); main.ts reads it back
        // with `typeof === "string"`, so the empty string must survive as a search.
        var result = CliArgs.Parse(["--list-models", ""]);
        Assert.True(result.ListModels);
        Assert.NotNull(result.ListModelsSearch);
        Assert.Equal("", result.ListModelsSearch);
    }

    [Fact]
    public void TripleDashPrefixedPromptIsConsumedAfterPrint()
    {
        // `next.startsWith("-")` is true but `next.startsWith("---")` short-circuits it,
        // so the `---` prompt is consumed and the following `-p` is parsed as the flag.
        var result = CliArgs.Parse(["-p", "---", "-p"]);
        Assert.Equal(["---"], result.Messages);
        Assert.True(result.Print);
    }

    [Fact]
    public void UnknownLongFlagDoesNotConsumeFileArgs()
    {
        var result = CliArgs.Parse(["--unknown-flag", "@file.txt", "msg"]);
        AssertUnknownFlagTrue(result, "unknown-flag");
        Assert.Equal(["file.txt"], result.FileArgs);
        Assert.Equal(["msg"], result.Messages);
    }

    [Fact]
    public void UnknownLongFlagWithEmptyEqualsValue()
    {
        var result = CliArgs.Parse(["--unknown-flag="]);
        AssertUnknownFlagValue(result, "unknown-flag", "");
    }

    [Fact]
    public void InvalidThinkingLevelWarnsWithEveryValidLevel()
    {
        var result = CliArgs.Parse(["--thinking", "ultra"]);
        Assert.Null(result.Thinking);
        Assert.Equal(
            [Warning(
                "Invalid thinking level \"ultra\". Valid values: off, minimal, low, medium, high, xhigh, max")],
            result.Diagnostics);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void IsValidThinkingLevelMatchesEnum(string level)
    {
        Assert.True(CliArgs.IsValidThinkingLevel(level));
        Assert.NotNull(ThinkingLevels.Parse(level));
    }

    [Fact]
    public void IsValidThinkingLevelRejectsUnknownValues()
    {
        Assert.False(CliArgs.IsValidThinkingLevel(""));
        Assert.False(CliArgs.IsValidThinkingLevel("Off"));
        Assert.False(CliArgs.IsValidThinkingLevel("ultra"));
    }

    [Fact]
    public void ValidThinkingLevelsTableMatchesEnum()
    {
        // Table-driven constant vs. enum: every entry must round-trip through the
        // shared mapping, and the table must cover the whole enum.
        var fromEnum = Enum.GetValues<ThinkingLevel>().Select(ThinkingLevels.ToText).ToArray();
        Assert.Equal(fromEnum, CliArgs.ValidThinkingLevels);
        Assert.All(CliArgs.ValidThinkingLevels, level => Assert.NotNull(ThinkingLevels.Parse(level)));
    }

    // -------------------------------------------------------------------- printHelp

    [Fact]
    public void PrintHelpWritesEverySection()
    {
        var previousOverride = Chalk.EnabledOverride;
        Chalk.EnabledOverride = false;
        var originalOut = Console.Out;
        var buffer = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(buffer));
            CliArgs.PrintHelp();
        }
        finally
        {
            Console.SetOut(originalOut);
            Chalk.EnabledOverride = previousOverride;
        }

        var text = buffer.ToString();
        Assert.Contains("AI coding assistant with read, bash, edit, write tools", text);
        Assert.Contains("Usage:", text);
        Assert.Contains("Commands:", text);
        Assert.Contains("Options:", text);
        Assert.Contains("Examples:", text);
        Assert.Contains("Environment Variables:", text);
        Assert.Contains("Built-in Tool Names:", text);
        // Config-derived interpolations resolve through the Config singletons.
        Assert.Contains($"  {Config.AppName} [options] [--] [@files...] [messages...]", text);
        Assert.Contains($"{Config.EnvAgentDir.PadRight(32)} - Config directory", text);
        Assert.Contains($"{Config.EnvSessionDir.PadRight(32)} - Session storage directory", text);
        Assert.Contains($"~/{Config.ConfigDirName}/agent/sessions/--path--/session.jsonl", text);
        // console.log's trailing newline is part of the contract.
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void PrintHelpIncludesExtensionFlags()
    {
        var previousOverride = Chalk.EnabledOverride;
        Chalk.EnabledOverride = false;
        var originalOut = Console.Out;
        var buffer = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(buffer));
            CliArgs.PrintHelp(
            [
                new ExtensionFlag
                {
                    Name = "plan",
                    Type = "boolean",
                    ExtensionPath = "/ext/plan.ts",
                },
                new ExtensionFlag
                {
                    Name = "target",
                    Type = "string",
                    Description = "Where to aim",
                    ExtensionPath = "/ext/aim.ts",
                },
            ]);
        }
        finally
        {
            Console.SetOut(originalOut);
            Chalk.EnabledOverride = previousOverride;
        }

        var text = buffer.ToString();
        Assert.Contains("Extension CLI Flags:", text);
        Assert.Contains("  --plan".PadRight(30) + "Registered by /ext/plan.ts", text);
        Assert.Contains("  --target <value>".PadRight(30) + "Where to aim", text);
    }

    [Fact]
    public void PrintHelpWithoutExtensionFlagsHasNoExtensionSection()
    {
        var previousOverride = Chalk.EnabledOverride;
        Chalk.EnabledOverride = false;
        var originalOut = Console.Out;
        var buffer = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(buffer));
            CliArgs.PrintHelp();
        }
        finally
        {
            Console.SetOut(originalOut);
            Chalk.EnabledOverride = previousOverride;
        }

        Assert.DoesNotContain("Extension CLI Flags:", buffer.ToString());
    }
}
