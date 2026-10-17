// ============================================================================
// Lifecycle helpers for InteractiveMode.
// Port of interactive-mode.ts's module-level formatResumeCommand (L341-358), the tmux keyboard
// decision inside checkTmuxKeyboardSetup (L1287-1305) and the changelog gating inside
// getChangelogForDisplay (L1324-1350).
//
// The session / file-system reads stay with the caller (4f-4m); these are the decisions taken on
// their results.
// ============================================================================

using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the lifecycle helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeLifecycle
{
    /// <summary>
    /// TS <c>formatResumeCommand(sessionManager)</c>: the command that resumes this session from a
    /// fresh shell, used by the crash report and the install-change warning. Every gate is
    /// independent: not a TTY, not persisted, or a session file that no longer exists each yield
    /// <see langword="null"/>; a custom session dir is quoted with
    /// <see cref="InteractiveModeSupport.QuoteIfNeeded"/>.
    /// </summary>
    public static string? FormatResumeCommand(
        bool isTty,
        bool isPersisted,
        string? sessionFile,
        bool sessionFileExists,
        bool usesDefaultSessionDir,
        string sessionDir,
        string sessionId,
        string appName)
    {
        if (!isTty || !isPersisted)
        {
            return null;
        }

        if (string.IsNullOrEmpty(sessionFile) || !sessionFileExists)
        {
            return null;
        }

        var args = new List<string> { appName };
        if (!usesDefaultSessionDir)
        {
            args.Add("--session-dir");
            args.Add(InteractiveModeSupport.QuoteIfNeeded(sessionDir));
        }

        args.Add("--session");
        args.Add(sessionId);
        return string.Join(" ", args);
    }

    /// <summary>
    /// TS <c>checkTmuxKeyboardSetup</c>'s decision, taken on the two <c>tmux show</c> results.
    /// <paramref name="extendedKeys"/> of <see langword="null"/> means tmux could not be queried at
    /// all (timeout, sandbox, …), which deliberately stays silent.
    /// </summary>
    public static string? ResolveTmuxKeyboardWarning(string? extendedKeys, string? extendedKeysFormat)
    {
        // If we couldn't query tmux (timeout, sandbox, etc.), don't warn.
        if (extendedKeys is null)
        {
            return null;
        }

        if (extendedKeys is not ("on" or "always"))
        {
            return "tmux extended-keys is off. Modified Enter keys may not work. Add `set -g extended-keys on` to ~/.tmux.conf and restart tmux.";
        }

        if (extendedKeysFormat == "xterm")
        {
            return "tmux extended-keys-format is xterm. Pi works best with csi-u. Add `set -g extended-keys-format csi-u` to ~/.tmux.conf and restart tmux.";
        }

        return null;
    }

    /// <summary>
    /// TS <c>getChangelogForDisplay</c>'s first gate: resumed sessions (any message already in the
    /// transcript) never show the changelog.
    /// </summary>
    public static bool ShouldSkipChangelog(int messageCount) => messageCount > 0;

    /// <summary>
    /// TS <c>getChangelogForDisplay</c>'s fresh-install branch: no recorded version means the
    /// version gets recorded and telemetry fires, but nothing is shown — even when the file has
    /// entries.
    /// </summary>
    public static bool IsFreshInstall(string? lastChangelogVersion) => string.IsNullOrEmpty(lastChangelogVersion);
}
