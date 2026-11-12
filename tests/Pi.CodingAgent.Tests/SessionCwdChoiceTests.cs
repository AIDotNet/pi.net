using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 v1.5.1 #11975（PR 4ea883792）：会话记录的工作目录丢失时，从「只丢错误 / 单次 confirm
/// 继续」升级为 3 选 1（continue / fork / cancel），并把 prompt 改为一行 `Session cwd does not exist: X`。
/// 逐字对照上游 core/session-cwd.ts 与 interactive-mode-resume-missing-cwd.test.ts 的选项文案。
/// 去掉修复（旧 confirm / 两标签）时这些用例失败。
/// </summary>
public sealed class SessionCwdChoiceTests
{
    private static SessionCwdIssue Issue() => new("/gone/proj", "/now/here");

    [Fact]
    public void FormatMissingSessionCwdPrompt_IsOneLineAndNamesSessionCwd()
    {
        Assert.Equal(
            "Session cwd does not exist: /gone/proj",
            SessionCwd.FormatMissingSessionCwdPrompt(Issue()));
    }

    [Fact]
    public void GetMissingSessionCwdOptions_ThreeChoicesWithExactLabels()
    {
        var options = SessionCwd.GetMissingSessionCwdOptions(Issue());
        Assert.Equal(3, options.Count);
        Assert.Equal(
            "Continue in /now/here (asks again next time)", options[0].Label);
        Assert.Equal(
            SessionCwd.MissingSessionCwdChoice.Continue, options[0].Value);
        Assert.Equal(
            "Fork into /now/here (new session ID, original kept)", options[1].Label);
        Assert.Equal(SessionCwd.MissingSessionCwdChoice.Fork, options[1].Value);
        Assert.Equal("Cancel", options[2].Label);
        Assert.Null(options[2].Value);
    }

    [Fact]
    public void GetMissingSessionCwdOptions_ValuesAreEnumChoices()
    {
        var options = SessionCwd.GetMissingSessionCwdOptions(Issue());
        Assert.Equal(SessionCwd.MissingSessionCwdChoice.Continue, options[0].Value);
        Assert.Equal(SessionCwd.MissingSessionCwdChoice.Fork, options[1].Value);
        Assert.Null(options[2].Value);
    }

    [Fact]
    public void FormatMissingSessionCwdError_IsUnchanged()
    {
        // #11975 只改了 prompt 与选项，error 文案不变。
        var issue = new SessionCwdIssue("/gone/proj", "/now/here") { SessionFile = "/s/x.jsonl" };
        Assert.Equal(
            "Stored session working directory does not exist: /gone/proj\nSession file: /s/x.jsonl\nCurrent working directory: /now/here",
            SessionCwd.FormatMissingSessionCwdError(issue));
    }
}
