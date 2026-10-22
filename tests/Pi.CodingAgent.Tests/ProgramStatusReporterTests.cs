using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// The interactive-mode OSC 7501 reporter (port of <c>modes/interactive/program-status-reporter.ts</c>,
/// upstream 1.1.0 / #10607).
/// </summary>
public class ProgramStatusReporterTests
{
    private sealed class Harness
    {
        public Harness(string? sessionName = "fix the bug")
        {
            SessionName = sessionName;
            Reporter = new ProgramStatusReporter(() => Terminal, () => SessionName);
        }

        public StringTerminal Terminal { get; } = new();

        public string? SessionName { get; set; }

        public ProgramStatusReporter Reporter { get; }

        public ProgramStatus Last => Terminal.ProgramStatuses[^1];

        public int Count => Terminal.ProgramStatuses.Count;
    }

    private static AssistantMessage Assistant(StopReason stopReason = StopReason.Stop, string? error = null) =>
        new([], stopReason, error);

    [Fact]
    public void StartupReportsIdleWithTheAppName()
    {
        var harness = new Harness();

        harness.Reporter.Report();

        Assert.Equal(ProgramStatusState.Idle, harness.Last.State);
        Assert.Equal(Config.AppName, harness.Last.App);
        Assert.Null(harness.Last.Message);
    }

    [Fact]
    public void AgentRunReportsWorkingWithTheSessionNameThenDone()
    {
        var harness = new Harness("tidy the parser");

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);
        Assert.Equal("tidy the parser", harness.Last.Message);

        harness.Reporter.HandleEvent(new AgentSessionEvent.MessageEndEvent(Assistant()));
        // Still mid-run: the outcome is only reported once the run settles.
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: false));
        Assert.Equal(ProgramStatusState.Done, harness.Last.State);
        Assert.Equal("tidy the parser", harness.Last.Message);
    }

    [Fact]
    public void AssistantErrorsCarryTheFirstLineAndSurviveUntilSettled()
    {
        var harness = new Harness();

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        harness.Reporter.HandleEvent(new AgentSessionEvent.MessageEndEvent(
            Assistant(StopReason.Error, "rate limited\r\nsecond line")));
        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: false));

        Assert.Equal(ProgramStatusState.Error, harness.Last.State);
        Assert.Equal("rate limited", harness.Last.Message);

        // The latest response decides the outcome: a successful retry replaces the error.
        var retried = new Harness();
        retried.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        retried.Reporter.HandleEvent(new AgentSessionEvent.MessageEndEvent(Assistant(StopReason.Error, "boom")));
        retried.Reporter.HandleEvent(new AgentSessionEvent.MessageEndEvent(Assistant()));
        retried.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: false));
        Assert.Equal(ProgramStatusState.Done, retried.Last.State);
    }

    [Fact]
    public void AbortedSettlesBackToIdle()
    {
        var harness = new Harness();

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: true));

        Assert.Equal(ProgramStatusState.Idle, harness.Last.State);
        Assert.Null(harness.Last.Message);
    }

    [Fact]
    public void CompactionReportsWorkingWithItsOwnMessage()
    {
        var harness = new Harness("long session");

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        harness.Reporter.HandleEvent(new AgentSessionEvent.CompactionStartEvent("threshold"));
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);
        Assert.Equal("Compacting context", harness.Last.Message);

        harness.Reporter.HandleEvent(new AgentSessionEvent.CompactionEndEvent("threshold", null, Aborted: false, WillRetry: false));
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);
        Assert.Equal("long session", harness.Last.Message);

        // A manual compaction outside a run settles to done, or to error when it failed.
        var manual = new Harness();
        manual.Reporter.HandleEvent(new AgentSessionEvent.CompactionStartEvent("manual"));
        manual.Reporter.HandleEvent(new AgentSessionEvent.CompactionEndEvent("manual", null, Aborted: false, WillRetry: false));
        Assert.Equal(ProgramStatusState.Done, manual.Last.State);

        var failed = new Harness();
        failed.Reporter.HandleEvent(new AgentSessionEvent.CompactionStartEvent("manual"));
        failed.Reporter.HandleEvent(new AgentSessionEvent.CompactionEndEvent(
            "manual", null, Aborted: false, WillRetry: false, "compact failed"));
        Assert.Equal(ProgramStatusState.Error, failed.Last.State);
        Assert.Equal("compact failed", failed.Last.Message);
    }

    [Fact]
    public void FailedOrAbortedRecoveryCompactionChangesTheRunOutcome()
    {
        var aborted = new Harness();
        aborted.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        aborted.Reporter.HandleEvent(new AgentSessionEvent.CompactionStartEvent("overflow"));
        aborted.Reporter.HandleEvent(new AgentSessionEvent.CompactionEndEvent("overflow", null, Aborted: true, WillRetry: false));
        aborted.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: false));
        Assert.Equal(ProgramStatusState.Idle, aborted.Last.State);

        var errored = new Harness();
        errored.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        errored.Reporter.HandleEvent(new AgentSessionEvent.CompactionStartEvent("overflow"));
        errored.Reporter.HandleEvent(new AgentSessionEvent.CompactionEndEvent(
            "overflow", null, Aborted: false, WillRetry: false, "overflow again"));
        errored.Reporter.HandleEvent(new AgentSessionEvent.AgentSettledEvent(Aborted: false));
        Assert.Equal(ProgramStatusState.Error, errored.Last.State);
        Assert.Equal("overflow again", errored.Last.Message);
    }

    [Fact]
    public void BlockedDialogsWinOverTheRunStateAndTheLastOneIsReported()
    {
        var harness = new Harness("busy");
        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());

        harness.Reporter.SetBlocked("extension-dialog:1", new BlockedStatus(ProgramStatusKind.Question, "Pick a file"));
        Assert.Equal(ProgramStatusState.Blocked, harness.Last.State);
        Assert.Equal(ProgramStatusKind.Question, harness.Last.Kind);
        Assert.Equal("Pick a file", harness.Last.Message);

        harness.Reporter.SetBlocked("login", new BlockedStatus(ProgramStatusKind.Auth, "Log in to anthropic"));
        Assert.Equal(ProgramStatusKind.Auth, harness.Last.Kind);
        Assert.Equal("Log in to anthropic", harness.Last.Message);

        // Clearing the most recent dialog falls back to the one still open.
        harness.Reporter.SetBlocked("login", null);
        Assert.Equal("Pick a file", harness.Last.Message);

        // Clearing the last one returns to the run state.
        harness.Reporter.SetBlocked("extension-dialog:1", null);
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);
        Assert.Equal("busy", harness.Last.Message);

        // Re-opening an existing source replaces it instead of stacking.
        harness.Reporter.SetBlocked("login", new BlockedStatus(ProgramStatusKind.Auth, "Log in to openai"));
        harness.Reporter.SetBlocked("login", new BlockedStatus(ProgramStatusKind.Auth, "Log in to xai"));
        harness.Reporter.SetBlocked("login", null);
        Assert.Equal(ProgramStatusState.Working, harness.Last.State);
    }

    [Fact]
    public void IdenticalStatusesAreNotReportedTwiceAndResetReturnsToIdle()
    {
        var harness = new Harness();
        harness.Reporter.Report();
        var afterFirst = harness.Count;
        harness.Reporter.Report();
        Assert.Equal(afterFirst, harness.Count);

        harness.Reporter.HandleEvent(new AgentSessionEvent.AgentStartEvent());
        harness.Reporter.Reset();
        Assert.Equal(ProgramStatusState.Idle, harness.Last.State);
        Assert.Null(harness.Last.Message);
    }

    [Fact]
    public void UnrelatedEventsDoNotReport()
    {
        var harness = new Harness();
        harness.Reporter.Report();
        var count = harness.Count;

        harness.Reporter.HandleEvent(new AgentSessionEvent.SessionInfoChangedEvent("renamed"));
        harness.Reporter.HandleEvent(new AgentSessionEvent.ThinkingLevelChangedEvent(ThinkingLevel.Low));

        Assert.Equal(count, harness.Count);
    }
}
