// ============================================================================
// Module-level half of modes/interactive/interactive-mode.ts.
//
// interactive-mode.ts declares a handful of local types, two small components and a set of pure
// helpers above the InteractiveMode class. They are ported here so the class batches (4f-4d and
// later) only have to add members.
// ============================================================================

using System.Net.Sockets;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Components that can be expanded/collapsed. TS <c>Expandable</c>.</summary>
public interface IExpandable
{
    void SetExpanded(bool expanded);
}

/// <summary>
/// A queued message that could not be sent while compaction was running.
/// TS <c>CompactionQueuedMessage</c>.
/// </summary>
public sealed record CompactionQueuedMessage
{
    /// <summary>TS <c>"steer"</c> or <c>"followUp"</c>; see <see cref="CompactionQueueModes"/>.</summary>
    public required string Mode { get; init; }

    public required string Text { get; init; }
}

/// <summary>TS <c>"steer" | "followUp"</c>.</summary>
public static class CompactionQueueModes
{
    /// <summary>Interrupt the current turn with this message.</summary>
    public const string Steer = "steer";

    /// <summary>Send after the current turn finishes.</summary>
    public const string FollowUp = "followUp";
}

/// <summary>
/// Text that renders one of two strings depending on an expand state. TS <c>ExpandableText</c>.
///
/// TS keeps the state in a boxed object so the closures passed to <c>ThemedText</c> see later
/// changes; the field serves the same purpose here.
/// </summary>
public class ExpandableText : ThemedText, IExpandable
{
    private readonly ExpandedState _state;

    public ExpandableText(
        Func<string> getCollapsedText,
        Func<string> getExpandedText,
        bool expanded = false,
        int paddingX = 0,
        int paddingY = 0)
        : this(new ExpandedState { Expanded = expanded }, getCollapsedText, getExpandedText, paddingX, paddingY)
    {
    }

    // TS boxes the state in an object literal so the closures handed to ThemedText observe later
    // changes; a constructor initializer cannot read `this`, so the box is threaded through a
    // private overload the same way.
    private ExpandableText(
        ExpandedState state,
        Func<string> getCollapsedText,
        Func<string> getExpandedText,
        int paddingX,
        int paddingY)
        : base(() => state.Expanded ? getExpandedText() : getCollapsedText(), paddingX, paddingY)
    {
        _state = state;
    }

    public void SetExpanded(bool expanded)
    {
        _state.Expanded = expanded;
        Invalidate();
    }

    private sealed class ExpandedState
    {
        public bool Expanded { get; set; }
    }
}

/// <summary>
/// The built-in header. Clicking its logo (the first two lines, after one column of padding) plays an
/// easter egg. TS <c>BuiltInHeader</c>.
/// </summary>
public sealed class BuiltInHeader : ExpandableText
{
    /// <summary>Invoked with the logo cell the click landed on, in screen coordinates.</summary>
    public Action<int, int>? OnLogoClick { get; set; }

    public BuiltInHeader(
        Func<string> getCollapsedText,
        Func<string> getExpandedText,
        bool expanded = false,
        int paddingX = 0,
        int paddingY = 0)
        : base(getCollapsedText, getExpandedText, expanded, paddingX, paddingY)
    {
    }

    public override TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        // The logo occupies columns 1-4 of rows 0-1, i.e. the first two lines after one column of
        // padding. Anything else is not a logo click.
        if (@event.Type != TuiMouseEventType.Click ||
            @event.Y > 1 ||
            @event.X < 1 ||
            @event.X > 4 ||
            OnLogoClick is null)
        {
            return null;
        }

        OnLogoClick(@event.ScreenX - @event.X + 1, @event.ScreenY - @event.Y);
        return new TuiMouseEventResult { Handled = true };
    }
}

/// <summary>Port of the module-level helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeSupport
{
    /// <summary>Shown once per session when Anthropic subscription auth is in use.</summary>
    public const string AnthropicSubscriptionAuthWarning =
        "Anthropic subscription auth is active. Third-party harness usage draws from extra usage and is billed per token, not your Claude plan limits. Manage extra usage at https://claude.ai/settings/usage. Disable this warning in /settings.";

    /// <summary>Intro line of the Radius login dialog.</summary>
    public const string RadiusLoginIntro =
        "Radius is a service crafted for Pi by the builders of Pi, Earendil Works";

    /// <summary>
    /// TS <c>quoteIfNeeded</c>: leave a plain path alone, otherwise wrap it in single quotes and
    /// escape embedded quotes the POSIX way (<c>'\''</c>).
    /// </summary>
    public static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && !ContainsQuotingCharacter(value))
        {
            return value;
        }

        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static bool ContainsQuotingCharacter(string value)
    {
        foreach (var character in value)
        {
            // TS: /[^a-zA-Z0-9_\-./~:@]/
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('_' or '-' or '.' or '/' or '~' or ':' or '@'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// TS <c>formatCrashExtensionHint(extensionMatches)</c>: name the loaded extensions a crash stack
    /// frame came from, or <see langword="null"/> when there are none to name.
    /// </summary>
    public static string? FormatCrashExtensionHint(IReadOnlyList<string?>? extensionMatches)
    {
        var matches = extensionMatches is null
            ? []
            : extensionMatches
                .Where(match => !string.IsNullOrEmpty(match))
                .Select(match => match!)
                .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var quoted = matches.Select(match => $"`{match}`").ToList();
        var labels = quoted.Count switch
        {
            1 => quoted[0],
            2 => $"{quoted[0]} and {quoted[1]}",
            _ => $"{string.Join(", ", quoted.Take(quoted.Count - 1))}, and {quoted[^1]}",
        };
        var noun = matches.Count == 1 ? "extension" : "extensions";
        var pronoun = matches.Count == 1 ? "it" : "them";
        return $"A stack frame came from loaded {noun} {labels}, which may be involved. Try disabling {pronoun} with `{Config.AppName} config`, or run `{Config.AppName} -ne` to confirm.";
    }

    /// <summary>TS <c>isAnthropicSubscriptionAuthKey(apiKey)</c>.</summary>
    public static bool IsAnthropicSubscriptionAuthKey(string? apiKey) =>
        apiKey is not null && apiKey.StartsWith("sk-ant-oat", StringComparison.Ordinal);

    /// <summary>TS <c>llamaCppPostLoginGuidance(actionLabel, loadedModelCount)</c>.</summary>
    public static string LlamaCppPostLoginGuidance(string actionLabel, int loadedModelCount) =>
        loadedModelCount == 0
            ? $"{actionLabel}. No llama.cpp models are loaded. Use /llama to load a model, then /model to select it."
            : $"{actionLabel}. Use /model to select a loaded llama.cpp model, or /llama to manage models.";

    /// <summary>
    /// TS <c>isDeadTerminalError(error)</c>: the tty is gone, so writing to it is pointless.
    ///
    /// TS checks the Node errno codes <c>EIO</c>, <c>EPIPE</c>, <c>ENOTCONN</c> and <c>ENOTTY</c>.
    /// .NET folds the tty failures into <see cref="IOException"/> and does not expose errno, so the
    /// mapping is by exception type (C263): any <see cref="IOException"/> counts (EIO/EPIPE/ENOTTY),
    /// a disconnected <see cref="SocketException"/> covers ENOTCONN, and a disposed stream is the
    /// same situation one layer up.
    /// </summary>
    public static bool IsDeadTerminalError(Exception? error) => error switch
    {
        null => false,
        IOException => true,
        ObjectDisposedException => true,
        SocketException socketError => socketError.SocketErrorCode is
            SocketError.NotConnected or
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.Shutdown,
        _ => false,
    };
}
