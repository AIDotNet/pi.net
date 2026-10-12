using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9e：<c>core/bug-report.ts</c> 中 <c>generateBugReportSummary</c> 的纯决策逻辑。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\bug-report.ts</c>
/// 的 <c>BUG_SUMMARY_*</c> / <c>selectMessages</c> / <c>generateBugReportSummary</c>）。
/// </summary>
public sealed class BugReportSummaryTests
{
    // ---------------------------------------------------------------------
    // 提示词常量
    // ---------------------------------------------------------------------

    [Fact]
    public void PromptConstants_AreLfNormalized()
    {
        // 本机 core.autocrlf=true，原始字符串字面量会带 CRLF；
        // 两个常量都经 ReplaceLineEndings("\n") 归一，多行内容不能含 \r。
        Assert.DoesNotContain('\r', BugReport.BugSummarySystemPrompt);
        Assert.DoesNotContain('\r', BugReport.BugSummaryInstructions);

        Assert.StartsWith("You are helping a user file a bug report about pi", BugReport.BugSummarySystemPrompt);
        Assert.EndsWith("ONLY output the report.", BugReport.BugSummarySystemPrompt);
        Assert.Contains("Do NOT continue the conversation.", BugReport.BugSummarySystemPrompt);

        Assert.StartsWith("Write the bug report in Markdown with these sections:", BugReport.BugSummaryInstructions);
        Assert.EndsWith("Keep the report factual and concise.", BugReport.BugSummaryInstructions);
        // 四个小节标题逐字。
        Assert.Contains("## What the user was doing", BugReport.BugSummaryInstructions);
        Assert.Contains("## What went wrong", BugReport.BugSummaryInstructions);
        Assert.Contains("## Steps to reproduce", BugReport.BugSummaryInstructions);
        Assert.Contains("## Relevant details", BugReport.BugSummaryInstructions);
    }

    // ---------------------------------------------------------------------
    // 预算
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveBugSummaryTokenBudget_UsesSixtyPercentOfContextWindow()
    {
        Assert.Equal(120_000, BugReport.ResolveBugSummaryTokenBudget(200_000));
        // 窗口为 0 / 负数 → 兜底 128_000。
        Assert.Equal(76_800, BugReport.ResolveBugSummaryTokenBudget(0));
        Assert.Equal(76_800, BugReport.ResolveBugSummaryTokenBudget(-5));
        // Math.floor 向下取整（100_001 * 0.6 = 60000.6 → 60000）。
        Assert.Equal(60_000, BugReport.ResolveBugSummaryTokenBudget(100_001));
    }

    [Fact]
    public void ResolveBugSummaryMaxTokens_CapsAt4096()
    {
        // 模型上限更大 → 取 4096。
        Assert.Equal(4096, BugReport.ResolveBugSummaryMaxTokens(8192));
        // 模型上限更小 → 取模型上限。
        Assert.Equal(2048, BugReport.ResolveBugSummaryMaxTokens(2048));
        // 恰好相等。
        Assert.Equal(4096, BugReport.ResolveBugSummaryMaxTokens(4096));
        // 非正值 → 4096（与 TS 的 Infinity 取小）。
        Assert.Equal(4096, BugReport.ResolveBugSummaryMaxTokens(0));
        Assert.Equal(4096, BugReport.ResolveBugSummaryMaxTokens(-1));
    }

    // ---------------------------------------------------------------------
    // 选片
    // ---------------------------------------------------------------------

    [Fact]
    public void SelectBugSummaryMessages_KeepsWholeListWhenBudgetIsAmple()
    {
        // 用消息自身的数字当 token 成本。
        IReadOnlyList<int> messages = [10, 20, 30];

        var selected = BugReport.SelectBugSummaryMessages(messages, tokenBudget: 1000, static x => x);

        Assert.Equal([10, 20, 30], selected);
    }

    [Fact]
    public void SelectBugSummaryMessages_TrimsFromTheFront()
    {
        // 30 + 20 = 50 ≤ 50，再加 10 会到 60 > 50 → 从最旧的一端丢。
        var selected = BugReport.SelectBugSummaryMessages<int>([10, 20, 30], tokenBudget: 50, static x => x);

        Assert.Equal([20, 30], selected);
    }

    [Fact]
    public void SelectBugSummaryMessages_AlwaysKeepsAtLeastTheLastMessage()
    {
        // 最后一条无条件收录（TS 的 `selected.length > 0` 前置）。
        var single = BugReport.SelectBugSummaryMessages<int>([100], tokenBudget: 1, static x => x);
        Assert.Equal([100], single);

        var lastOnly = BugReport.SelectBugSummaryMessages<int>([5, 100], tokenBudget: 1, static x => x);
        Assert.Equal([100], lastOnly);
    }

    [Fact]
    public void SelectBugSummaryMessages_BreaksAtFirstOverflowRatherThanSkipping()
    {
        // 一旦越界就 break（不跳过继续往前找），所以更早的小消息不会被补进来。
        var selected = BugReport.SelectBugSummaryMessages<int>([1, 100, 30], tokenBudget: 40, static x => x);

        // 30 → 100（40-30=10 < 100）→ break，前面的 1 不参与。
        Assert.Equal([30], selected);
    }

    [Fact]
    public void SelectBugSummaryMessages_EmptyInputGivesEmptyList()
    {
        Assert.Empty(BugReport.SelectBugSummaryMessages<int>([], tokenBudget: 100, static x => x));
    }

    // ---------------------------------------------------------------------
    // 提示词装配
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildBugSummaryPrompt_NoTruncationNoteWhenAllMessagesShown()
    {
        var prompt = BugReport.BuildBugSummaryPrompt(
            selectedCount: 5, totalCount: 5, conversation: "CONV", hint: null);

        Assert.DoesNotContain("Note: only the last", prompt);
        Assert.StartsWith("<conversation>\nCONV\n</conversation>", prompt);
        Assert.EndsWith(BugReport.BugSummaryInstructions, prompt);
    }

    [Fact]
    public void BuildBugSummaryPrompt_AddsTruncationNoteFirst()
    {
        var prompt = BugReport.BuildBugSummaryPrompt(
            selectedCount: 2, totalCount: 5, conversation: "CONV", hint: null);

        Assert.StartsWith("Note: only the last 2 of 5 messages are shown.", prompt);
        // 四段以空行连接。
        Assert.Contains("messages are shown.\n\n<conversation>", prompt);
    }

    [Fact]
    public void BuildBugSummaryPrompt_AddsUserReportOnlyWhenHintNonEmpty()
    {
        var withHint = BugReport.BuildBugSummaryPrompt(1, 1, "CONV", "it crashed");
        Assert.Contains("<user-report>\nit crashed\n</user-report>", withHint);
        // user-report 在 conversation 之后、说明之前。
        Assert.True(
            withHint.IndexOf("<user-report>", StringComparison.Ordinal)
            < withHint.IndexOf(BugReport.BugSummaryInstructions, StringComparison.Ordinal));

        // null / 空串 / 纯空白都视为缺席（TS 的 `hint ?` 真值判定）。
        Assert.DoesNotContain("<user-report>", BugReport.BuildBugSummaryPrompt(1, 1, "CONV", null));
        Assert.DoesNotContain("<user-report>", BugReport.BuildBugSummaryPrompt(1, 1, "CONV", ""));
    }

    [Fact]
    public void BuildBugSummaryPrompt_SeparatesPartsWithBlankLine()
    {
        var prompt = BugReport.BuildBugSummaryPrompt(2, 5, "CONV", "hint");

        var expected = string.Join("\n\n",
            "Note: only the last 2 of 5 messages are shown.",
            "<conversation>\nCONV\n</conversation>",
            "<user-report>\nhint\n</user-report>",
            BugReport.BugSummaryInstructions);
        Assert.Equal(expected, prompt);
    }

    // ---------------------------------------------------------------------
    // 响应校验
    // ---------------------------------------------------------------------

    [Fact]
    public void ValidateBugSummaryResponse_ReturnsTrimmedText()
    {
        var response = new AssistantMessage([new TextContent("  the report  ")]);

        Assert.Equal("the report", BugReport.ValidateBugSummaryResponse(response));
    }

    [Fact]
    public void ValidateBugSummaryResponse_AbortedWinsOverEmptyContent()
    {
        // 中止的响应内容为空；必须先判中止，否则用户看到的是 "was empty"。
        var response = new AssistantMessage([], StopReason.Aborted);

        var ex = Assert.Throws<InvalidOperationException>(() => BugReport.ValidateBugSummaryResponse(response));
        Assert.Equal("Bug report summary was cancelled", ex.Message);
    }

    [Fact]
    public void ValidateBugSummaryResponse_ErrorAndLengthUseSummarizationFailure()
    {
        var error = new AssistantMessage([], StopReason.Error, ErrorMessage: "boom");
        Assert.Equal(
            "Bug report summary failed: boom",
            Assert.Throws<InvalidOperationException>(() => BugReport.ValidateBugSummaryResponse(error)).Message);

        // errorMessage 缺席 → "Unknown error"。
        var noMessage = new AssistantMessage([], StopReason.Error);
        Assert.Equal(
            "Bug report summary failed: Unknown error",
            Assert.Throws<InvalidOperationException>(() => BugReport.ValidateBugSummaryResponse(noMessage)).Message);

        var length = new AssistantMessage([new TextContent("partial")], StopReason.Length);
        Assert.Equal(
            "Bug report summary failed: generation hit the token cap and the summary is incomplete",
            Assert.Throws<InvalidOperationException>(() => BugReport.ValidateBugSummaryResponse(length)).Message);
    }

    [Fact]
    public void ValidateBugSummaryResponse_RejectsToolCalls()
    {
        var response = new AssistantMessage(
            [new ToolCallContent("id1", "read", new { path = "a.cs" })]);

        var ex = Assert.Throws<InvalidOperationException>(() => BugReport.ValidateBugSummaryResponse(response));
        Assert.Equal("Bug report summary attempted to call a tool", ex.Message);
    }

    [Fact]
    public void ValidateBugSummaryResponse_RejectsEmptyOrWhitespaceText()
    {
        Assert.Equal(
            "Bug report summary was empty",
            Assert.Throws<InvalidOperationException>(
                () => BugReport.ValidateBugSummaryResponse(new AssistantMessage([]))).Message);

        Assert.Equal(
            "Bug report summary was empty",
            Assert.Throws<InvalidOperationException>(
                () => BugReport.ValidateBugSummaryResponse(new AssistantMessage([new TextContent("  \n  ")]))).Message);

        // 只有 thinking 块、没有 text 块 → 也算空。
        Assert.Equal(
            "Bug report summary was empty",
            Assert.Throws<InvalidOperationException>(
                () => BugReport.ValidateBugSummaryResponse(
                    new AssistantMessage([new ThinkingContent("thinking...")]))).Message);
    }
}
