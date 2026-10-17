// ============================================================================
// Tests for the submit routing of interactive-mode.ts (4f-4i).
//
// Upstream has no test file for setupEditorSubmitHandler. The vectors below re-derive every branch
// of the 200-line if-chain: the exact commands, the five argument-slicing commands (with their
// slice lengths), the three full-line commands, the `!`/`!!` bash branch and the name probe.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeSubmitTests
{
    // ---------------------------------------------------------------- argument commands

    [Theory]
    [InlineData("/model", null)]
    [InlineData("/model ", null)]        // a blank slice becomes undefined upstream
    [InlineData("/model gpt-5", "gpt-5")]
    [InlineData("/model   gpt-5  ", "gpt-5")]
    [InlineData("/thinking", null)]
    [InlineData("/thinking high", "high")]
    [InlineData("/login", null)]
    [InlineData("/login anthropic", "anthropic")]
    [InlineData("/compact", null)]
    [InlineData("/compact focus on tests", "focus on tests")]
    [InlineData("/bug", null)]
    [InlineData("/bug something broke", "something broke")]
    public void ArgumentCommandsExtractTheirSearchTerm(string text, string? expected)
    {
        var route = InteractiveModeSubmit.Route(text);

        Assert.NotNull(route);
        Assert.Equal(expected, route!.Argument);
    }

    [Fact]
    public void TheBugSliceIsFourCharactersSoTheCommandItselfIsNotPartOfTheArgument()
    {
        // TS slices "/bug".length (4), not "/bug ".length (5): "/bug hi" -> " hi" -> "hi".
        Assert.Equal("hi", InteractiveModeSubmit.Route("/bug hi")!.Argument);
    }

    [Fact]
    public void NearMissesAreNotCommands()
    {
        Assert.Null(InteractiveModeSubmit.Route("/models"));
        Assert.Null(InteractiveModeSubmit.Route("/modelfoo"));
        Assert.Null(InteractiveModeSubmit.Route("/thinkingx"));
        Assert.Null(InteractiveModeSubmit.Route("/loginx"));
        Assert.Null(InteractiveModeSubmit.Route("/compactx"));
        Assert.Null(InteractiveModeSubmit.Route("/buggy"));
    }

    // ---------------------------------------------------------------- exact commands

    [Theory]
    [InlineData("/settings", SubmitCommand.Settings)]
    [InlineData("/scoped-models", SubmitCommand.ScopedModels)]
    [InlineData("/share", SubmitCommand.Share)]
    [InlineData("/copy", SubmitCommand.Copy)]
    [InlineData("/session", SubmitCommand.Session)]
    [InlineData("/changelog", SubmitCommand.Changelog)]
    [InlineData("/hotkeys", SubmitCommand.Hotkeys)]
    [InlineData("/fork", SubmitCommand.Fork)]
    [InlineData("/clone", SubmitCommand.Clone)]
    [InlineData("/tree", SubmitCommand.Tree)]
    [InlineData("/trust", SubmitCommand.Trust)]
    [InlineData("/logout", SubmitCommand.Logout)]
    [InlineData("/new", SubmitCommand.New)]
    [InlineData("/reload", SubmitCommand.Reload)]
    [InlineData("/debug", SubmitCommand.Debug)]
    [InlineData("/arminsayshi", SubmitCommand.ArminSaysHi)]
    [InlineData("/dementedelves", SubmitCommand.DementedElves)]
    [InlineData("/resume", SubmitCommand.Resume)]
    [InlineData("/quit", SubmitCommand.Quit)]
    public void ExactCommandsRouteWithoutAnArgument(string text, SubmitCommand command)
    {
        var route = InteractiveModeSubmit.Route(text);

        Assert.NotNull(route);
        Assert.Equal(command, route!.Command);
        Assert.Null(route.Argument);
    }

    [Fact]
    public void ExactCommandsDoNotSwallowArguments()
    {
        // TS matches these as `text === "/settings"` only, so "/settings x" is not a command.
        Assert.Null(InteractiveModeSubmit.Route("/settings x"));
        Assert.Null(InteractiveModeSubmit.Route("/quit now"));
    }

    // ---------------------------------------------------------------- full-line commands

    [Theory]
    [InlineData("/export", SubmitCommand.Export)]
    [InlineData("/export /tmp/out.html", SubmitCommand.Export)]
    [InlineData("/import", SubmitCommand.Import)]
    [InlineData("/import session.jsonl", SubmitCommand.Import)]
    [InlineData("/name", SubmitCommand.Name)]
    [InlineData("/name my session", SubmitCommand.Name)]
    public void FullLineCommandsKeepTheWholeText(string text, SubmitCommand command)
    {
        var route = InteractiveModeSubmit.Route(text);

        Assert.NotNull(route);
        Assert.Equal(command, route!.Command);
        Assert.Equal(text, route.FullText); // the handler receives the raw trimmed line
    }

    [Fact]
    public void NearMissesOfFullLineCommandsAreNotCommands()
    {
        Assert.Null(InteractiveModeSubmit.Route("/exports"));
        Assert.Null(InteractiveModeSubmit.Route("/named"));
        Assert.Null(InteractiveModeSubmit.Route("/imports"));
    }

    // ---------------------------------------------------------------- not commands

    [Fact]
    public void PlainMessagesAndExtensionCommandsDoNotRoute()
    {
        Assert.Null(InteractiveModeSubmit.Route("hello"));
        Assert.Null(InteractiveModeSubmit.Route("/deploy"));       // extension command
        Assert.Null(InteractiveModeSubmit.Route("/deploy prod"));  // extension command with args
        Assert.Null(InteractiveModeSubmit.Route("!ls"));
        Assert.Null(InteractiveModeSubmit.Route(""));
    }

    // ---------------------------------------------------------------- bash branch

    [Theory]
    [InlineData("!ls -la", "ls -la", false)]
    [InlineData("!!ls -la", "ls -la", true)]
    [InlineData("!  git status  ", "git status", false)]
    [InlineData("!!git status", "git status", true)]
    [InlineData("!", null, false)]      // a bare `!` falls through to normal submission
    [InlineData("!!", null, false)]
    [InlineData("!!!foo", "!foo", true)] // TS strips exactly two characters for `!!`
    [InlineData("hello", null, false)]
    public void BashLinesAreStrippedOfTheirExclamationMarks(string text, string? command, bool excluded)
    {
        Assert.Equal((command, excluded), InteractiveModeSubmit.ParseBashCommand(text));
    }

    // ---------------------------------------------------------------- extension name probe

    [Theory]
    [InlineData("/deploy", "deploy")]
    [InlineData("/deploy prod --dry", "deploy")]
    [InlineData("/model", "model")]
    [InlineData("/a", "a")]
    public void TheCommandNameIsEverythingBeforeTheFirstSpace(string text, string expected)
    {
        Assert.Equal(expected, InteractiveModeSubmit.GetSlashCommandName(text));
    }

    // ---------------------------------------------------------------- busy warning

    [Fact]
    public void TheBusyBashWarningTellsTheUserToCancelFirst()
    {
        Assert.Equal(
            "A bash command is already running. Press Esc to cancel it first.",
            InteractiveModeSubmit.BashBusyWarning);
    }
}
