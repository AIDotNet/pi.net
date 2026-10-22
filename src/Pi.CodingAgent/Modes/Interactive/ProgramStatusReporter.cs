using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>A dialog that blocks the run on the user (port of TS <c>BlockedStatus</c>).</summary>
public sealed record BlockedStatus(ProgramStatusKind Kind, string Message);

/// <summary>
/// Reports interactive-mode state to the terminal (OSC 7501): <c>working</c> during agent runs and
/// compaction, <c>blocked</c> while a dialog waits for the user, then <c>done</c>, <c>error</c> or
/// <c>idle</c> once the run settles. Port of <c>modes/interactive/program-status-reporter.ts</c>.
/// </summary>
/// <remarks>
/// Messages are limited to the session name, dialog titles and the first line of errors; prompts and
/// assistant output are never reported.
/// </remarks>
public sealed class ProgramStatusReporter(Func<ITerminal> getTerminal, Func<string?> getSessionName)
{
    private readonly List<(string Source, BlockedStatus Status)> _blocked = [];

    private bool _runActive;
    private bool _compacting;

    /// <summary>Outcome of the current run, reported once it settles.</summary>
    private ProgramStatus _runResult = new(ProgramStatusState.Done);

    /// <summary>Status while no run is active.</summary>
    private ProgramStatus _restingStatus = new(ProgramStatusState.Idle);

    private string? _lastReport;

    public void HandleEvent(AgentSessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case AgentSessionEvent.AgentStartEvent:
                _runActive = true;
                _runResult = new ProgramStatus(ProgramStatusState.Done);
                break;

            case AgentSessionEvent.MessageEndEvent { Message: AssistantMessage message }:
                // The latest response decides the outcome, so a retried error is replaced by its
                // successful retry.
                _runResult = message.StopReason == StopReason.Error
                    ? new ProgramStatus(ProgramStatusState.Error) { Message = FirstLine(message.ErrorMessage) }
                    : new ProgramStatus(ProgramStatusState.Done);
                break;

            case AgentSessionEvent.CompactionStartEvent:
                _compacting = true;
                break;

            case AgentSessionEvent.CompactionEndEvent compaction:
                _compacting = false;
                if (_runActive)
                {
                    // A failed recovery compaction ends the run unless a later response succeeds.
                    if (compaction.Aborted)
                    {
                        _runResult = new ProgramStatus(ProgramStatusState.Idle);
                    }
                    else if (compaction.ErrorMessage is { } runError)
                    {
                        _runResult = new ProgramStatus(ProgramStatusState.Error) { Message = FirstLine(runError) };
                    }
                }
                else if (compaction.Aborted)
                {
                    _restingStatus = new ProgramStatus(ProgramStatusState.Idle);
                }
                else if (compaction.Reason == "manual")
                {
                    _restingStatus = compaction.ErrorMessage is { } manualError
                        ? new ProgramStatus(ProgramStatusState.Error) { Message = FirstLine(manualError) }
                        : new ProgramStatus(ProgramStatusState.Done);
                }

                break;

            case AgentSessionEvent.AgentSettledEvent settled:
                _runActive = false;
                _restingStatus = settled.Aborted
                    ? new ProgramStatus(ProgramStatusState.Idle)
                    : _runResult;
                break;

            case AgentSessionEvent.SessionInfoChangedEvent:
                // The session name is part of the working and done reports.
                break;

            default:
                return;
        }

        Report();
    }

    /// <summary>
    /// Reports <c>blocked</c> for a dialog until it is cleared with <see langword="null"/>. Reopening a
    /// source replaces it.
    /// </summary>
    public void SetBlocked(string source, BlockedStatus? status)
    {
        _blocked.RemoveAll(entry => entry.Source == source);
        if (status is not null)
        {
            _blocked.Add((source, status));
        }

        Report();
    }

    /// <summary>Forget the previous session's run, for example after switching sessions.</summary>
    public void Reset()
    {
        _runActive = false;
        _compacting = false;
        _runResult = new ProgramStatus(ProgramStatusState.Done);
        _restingStatus = new ProgramStatus(ProgramStatusState.Idle);
        Report();
    }

    public void Report()
    {
        var status = CurrentStatus() with { App = Config.AppName };
        var key = Key(status);
        if (key == _lastReport)
        {
            return;
        }

        _lastReport = key;
        getTerminal().SetProgramStatus(status);
    }

    private ProgramStatus CurrentStatus()
    {
        // The most recent open dialog wins.
        if (_blocked.Count > 0)
        {
            var (_, status) = _blocked[^1];
            return new ProgramStatus(ProgramStatusState.Blocked) { Kind = status.Kind, Message = status.Message };
        }

        if (_compacting)
        {
            return new ProgramStatus(ProgramStatusState.Working) { Message = "Compacting context" };
        }

        var current = _runActive ? new ProgramStatus(ProgramStatusState.Working) : _restingStatus;
        if (current.State is ProgramStatusState.Working or ProgramStatusState.Done)
        {
            return current with { Message = getSessionName() };
        }

        return current;
    }

    /// <summary>TS compares <c>JSON.stringify(status)</c>; a tuple of the wire-visible fields is equivalent.</summary>
    private static string Key(ProgramStatus status) =>
        $"{status.State}|{status.App}|{status.Kind}|{status.Message}";

    private static string FirstLine(string? text)
    {
        var line = text?.Split('\n')[0].TrimEnd('\r').Trim();
        return string.IsNullOrEmpty(line) ? "Error" : line;
    }
}
