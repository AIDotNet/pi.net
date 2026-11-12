// ============================================================================
// Missing session working directory — port of core/session-cwd.ts (4e-1)
// ============================================================================
//
// A session stores the working directory it was created in. When that directory is gone (the project
// was moved or deleted) resuming must fail with a message that names both paths, rather than silently
// running in the wrong place.

namespace Pi.CodingAgent.Core;

/// <summary>What a session recorded versus where the process actually runs. TS <c>SessionCwdIssue</c>.</summary>
public sealed record SessionCwdIssue(string SessionCwd, string FallbackCwd)
{
    /// <summary>The session file that carried the stale directory, when the session has one.</summary>
    public string? SessionFile { get; init; }
}

/// <summary>The slice of the session manager this module reads. TS <c>SessionCwdSource</c>.</summary>
public interface ISessionCwdSource
{
    /// <summary>TS <c>getCwd()</c>.</summary>
    string GetCwd();

    /// <summary>TS <c>getSessionFile()</c>.</summary>
    string? GetSessionFile();
}

/// <summary>Raised when a session's stored working directory no longer exists. TS <c>MissingSessionCwdError</c>.</summary>
public sealed class MissingSessionCwdException : Exception
{
    /// <summary>The exception name TS sets explicitly on the error object.</summary>
    public const string ErrorName = "MissingSessionCwdError";

    /// <summary>What the session recorded versus the current directory.</summary>
    public SessionCwdIssue Issue { get; }

    /// <summary>Builds the message the same way <see cref="SessionCwd.FormatMissingSessionCwdError"/> does.</summary>
    public MissingSessionCwdException(SessionCwdIssue issue)
        : base(SessionCwd.FormatMissingSessionCwdError(issue))
    {
        Issue = issue;
    }
}

/// <summary>Port of <c>core/session-cwd.ts</c>.</summary>
public static class SessionCwd
{
    /// <summary>
    /// The stale-directory problem, or <see langword="null"/> when there is nothing to report: no
    /// session file, no recorded directory, or the directory still exists.
    /// </summary>
    public static SessionCwdIssue? GetMissingSessionCwdIssue(ISessionCwdSource sessionManager, string fallbackCwd)
    {
        var sessionFile = sessionManager.GetSessionFile();
        if (string.IsNullOrEmpty(sessionFile))
        {
            return null;
        }

        var sessionCwd = sessionManager.GetCwd();
        // TS `existsSync` is true for files and directories alike.
        if (string.IsNullOrEmpty(sessionCwd) || File.Exists(sessionCwd) || Directory.Exists(sessionCwd))
        {
            return null;
        }

        return new SessionCwdIssue(sessionCwd, fallbackCwd) { SessionFile = sessionFile };
    }

    /// <summary>The full error text, used for the thrown exception and for display.</summary>
    public static string FormatMissingSessionCwdError(SessionCwdIssue issue)
    {
        var sessionFile = !string.IsNullOrEmpty(issue.SessionFile) ? $"\nSession file: {issue.SessionFile}" : "";
        return $"Stored session working directory does not exist: {issue.SessionCwd}{sessionFile}"
               + $"\nCurrent working directory: {issue.FallbackCwd}";
    }

    /// <summary>上游 1.5.1 #11975 的一行提示（原多行 confirm 文案被 3 选 1 选择器取代）。</summary>
    public static string FormatMissingSessionCwdPrompt(SessionCwdIssue issue) =>
        $"Session cwd does not exist: {issue.SessionCwd}";

    /// <summary>上游 1.5.1 #11975：会话记录的工作目录丢失时，运行于当前目录一次的两种打开方式。</summary>
    public enum MissingSessionCwdChoice
    {
        /// <summary>在当前目录继续（不记住选择，下次仍询问）。</summary>
        Continue,

        /// <summary>以新会话 ID fork 到当前目录（原会话保留）。</summary>
        Fork,
    }

    /// <summary>一条缺失 cwd 的选择器选项；<see cref="Value"/> 为 null 表示取消。</summary>
    public sealed record MissingSessionCwdOption(string Label, MissingSessionCwdChoice? Value);

    /// <summary>
    /// 上游 1.5.1 #11975 <c>getMissingSessionCwdOptions</c>：continue / fork / cancel 三项，
    /// 标签里点名当前 cwd。逐字对照上游文案。
    /// </summary>
    public static IReadOnlyList<MissingSessionCwdOption> GetMissingSessionCwdOptions(SessionCwdIssue issue) =>
    [
        new MissingSessionCwdOption($"Continue in {issue.FallbackCwd} (asks again next time)", MissingSessionCwdChoice.Continue),
        new MissingSessionCwdOption($"Fork into {issue.FallbackCwd} (new session ID, original kept)", MissingSessionCwdChoice.Fork),
        new MissingSessionCwdOption("Cancel", null),
    ];

    /// <summary>Throws <see cref="MissingSessionCwdException"/> when the recorded directory is gone.</summary>
    public static void AssertSessionCwdExists(ISessionCwdSource sessionManager, string fallbackCwd)
    {
        var issue = GetMissingSessionCwdIssue(sessionManager, fallbackCwd);
        if (issue is not null)
        {
            throw new MissingSessionCwdException(issue);
        }
    }
}
