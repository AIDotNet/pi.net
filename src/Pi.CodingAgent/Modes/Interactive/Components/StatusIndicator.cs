// ============================================================================
// Status indicators (working / retry / compaction / branch summary) + idle placeholder.
// Port of modes/interactive/components/status-indicator.ts
// ============================================================================

using Pi.CodingAgent.Core.Extensions;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>StatusIndicatorKind</c>.</summary>
public enum StatusIndicatorKind
{
    Working,
    Retry,
    Compaction,
    BranchSummary,
}

/// <summary>
/// A <see cref="Loader"/> with a kind tag and border-context render helpers.
/// Port of <c>StatusIndicator</c>.
/// </summary>
public class StatusIndicator : Loader, IDisposable
{
    public StatusIndicatorKind Kind { get; }

    public StatusIndicator(
        StatusIndicatorKind kind,
        ITui ui,
        Func<string, string> spinnerColorFn,
        Func<string, string> messageColorFn,
        string message,
        WorkingIndicatorOptions? indicator = null)
        : base(ui, spinnerColorFn, messageColorFn, message, ToLoaderIndicator(indicator))
    {
        Kind = kind;
    }

    /// <summary>
    /// TS <c>renderInBorder(width)</c>: the loader's text line (its <c>render</c> prepends one
    /// blank line), left-trimmed of the leading padding space and truncated to the border's
    /// inner width.
    /// </summary>
    public string RenderInBorder(int width)
    {
        var rendered = base.Render(width + 2);
        var line = rendered.Length > 1 ? rendered[1] : "";
        var trimmed = line.StartsWith(' ') ? line[1..].TrimEnd() : line.TrimEnd();
        return TextLayout.TruncateToWidth(trimmed, width, "");
    }

    /// <summary>TS <c>renderSpinnerInBorder(width)</c>.</summary>
    public string RenderSpinnerInBorder(int width) =>
        TextLayout.TruncateToWidth(GetRenderedIndicator(), width, "");

    /// <summary>TS <c>dispose()</c>: stops the spinner animation.</summary>
    public virtual void Dispose() => Stop();

    // WorkingIndicatorOptions (extensions) and LoaderIndicatorOptions (pi-tui) are structurally
    // identical in TS too - the indicator flows from the extension API into the Loader unchanged.
    // Public because InteractiveMode.setWorkingIndicator (TS) forwards the same options object.
    public static LoaderIndicatorOptions? ToLoaderIndicator(WorkingIndicatorOptions? indicator) =>
        indicator is null
            ? null
            : new LoaderIndicatorOptions
            {
                Frames = indicator.Frames is { } frames ? [.. frames] : null,
                IntervalMs = indicator.IntervalMs,
            };
}

/// <summary>TS <c>WorkingStatusIndicator</c>.</summary>
public sealed class WorkingStatusIndicator : StatusIndicator
{
    public WorkingStatusIndicator(
        ITui ui,
        string message,
        WorkingIndicatorOptions? indicator = null,
        Func<string, string>? colorFn = null)
        : base(
            StatusIndicatorKind.Working,
            ui,
            colorFn ?? (text => ThemeRuntime.Current.Fg("accent", text)),
            colorFn ?? (text => ThemeRuntime.Current.Fg("muted", text)),
            message,
            indicator)
    {
    }
}

/// <summary>TS <c>RetryStatusIndicator</c>.</summary>
public sealed class RetryStatusIndicator : StatusIndicator
{    private CountdownTimer? _countdown;

    public RetryStatusIndicator(ITui ui, int attempt, int maxAttempts, int delayMs)
        : base(
            StatusIndicatorKind.Retry,
            ui,
            spinner => ThemeRuntime.Current.Fg("warning", spinner),
            text => ThemeRuntime.Current.Fg("muted", text),
            RetryMessage(attempt, maxAttempts, (int)Math.Ceiling(delayMs / 1000d)))
    {
        _countdown = new CountdownTimer(
            delayMs,
            ui,
            seconds => SetMessage(RetryMessage(attempt, maxAttempts, seconds)),
            () => _countdown = null);
    }

    private static string RetryMessage(int attempt, int maxAttempts, int seconds) =>
        $"Retrying ({attempt}/{maxAttempts}) in {seconds}s... ({KeybindingHints.KeyText("app.interrupt")} to cancel)";

    public override void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
        base.Dispose();
    }
}

/// <summary>TS <c>CompactionStatusReason</c>.</summary>
public enum CompactionStatusReason
{
    Manual,
    Threshold,
    Overflow,
}

/// <summary>TS <c>CompactionStatusIndicator</c>.</summary>
public sealed class CompactionStatusIndicator : StatusIndicator
{
    public CompactionStatusIndicator(ITui ui, CompactionStatusReason reason)
        : base(
            StatusIndicatorKind.Compaction,
            ui,
            spinner => ThemeRuntime.Current.Fg("accent", spinner),
            text => ThemeRuntime.Current.Fg("muted", text),
            reason == CompactionStatusReason.Manual
                ? $"Compacting context... ({KeybindingHints.KeyText("app.interrupt")} to cancel)"
                : $"{(reason == CompactionStatusReason.Overflow ? "Context overflow detected, " : "")}Auto-compacting... ({KeybindingHints.KeyText("app.interrupt")} to cancel)")
    {
    }
}

/// <summary>TS <c>BranchSummaryStatusIndicator</c>.</summary>
public sealed class BranchSummaryStatusIndicator : StatusIndicator
{
    public BranchSummaryStatusIndicator(ITui ui)
        : base(
            StatusIndicatorKind.BranchSummary,
            ui,
            spinner => ThemeRuntime.Current.Fg("accent", spinner),
            text => ThemeRuntime.Current.Fg("muted", text),
            $"Summarizing branch... ({KeybindingHints.KeyText("app.interrupt")} to cancel)")
    {
    }
}

/// <summary>
/// Placeholder component rendered where the status line sits while idle. TS <c>IdleStatus</c>.
/// </summary>
public sealed class IdleStatus : IComponent
{
    public void Invalidate()
    {
        // No cached state to invalidate.
    }

    public string[] Render(int width)
    {
        var emptyLine = new string(' ', width);
        return [emptyLine, emptyLine];
    }
}
