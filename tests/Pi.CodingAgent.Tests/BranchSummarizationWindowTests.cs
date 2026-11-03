using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Branch summarization fixes from the 1.5.0 window: the session's thinking level reaches the summary
/// request (TS <c>269afc66a</c>) and context edits are applied to the summarized branch
/// (TS <c>d0fe21308</c>).
/// </summary>
public sealed class BranchSummarizationWindowTests
{
    private static UserMessage UserText(string text) => new(MessageContent.From(text), 1);

    private static ModelSpec ReasoningModel() => new()
    {
        Id = "gpt-test",
        Name = "GPT Test",
        Api = "openai-responses",
        Provider = "openai",
        BaseUrl = "https://example.invalid",
        ContextWindow = 128_000,
        Reasoning = true,
    };

    private static ModelSpec NonReasoningModel() => new()
    {
        Id = "plain-test",
        Name = "Plain Test",
        Api = "openai-completions",
        Provider = "openai",
        BaseUrl = "https://example.invalid",
        ContextWindow = 128_000,
        Reasoning = false,
    };

    private static StreamFn CapturingStream(Action<SimpleStreamOptions?> onCall) => (_, _, options, _) =>
    {
        onCall(options);
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage(
            [new TextContent("Summary")],
            StopReason.Stop,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub");
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
        stream.End(message);
        return Task.FromResult<IAssistantMessageEventStream>(stream);
    };

    private static bool HasReasoning(SimpleStreamOptions? options) =>
        options is not null && options.Reasoning is not null;

    [Fact]
    public async Task GenerateBranchSummary_ForwardsThinkingLevelForReasoningModels()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry { Id = "a", Message = UserText("hello"), Timestamp = "2026-01-01T00:00:00.000Z" },
        };

        SimpleStreamOptions? captured = null;
        await BranchSummarization.GenerateBranchSummaryAsync(
            entries,
            new GenerateBranchSummaryOptions
            {
                Model = ReasoningModel(),
                ThinkingLevel = ThinkingLevel.XHigh,
                Signal = CancellationToken.None,
                StreamFn = CapturingStream(options => captured = options),
            });

        Assert.NotNull(captured);
        Assert.Equal("xhigh", captured!.Reasoning);
    }

    [Fact]
    public async Task GenerateBranchSummary_OmitsReasoningWhenThinkingIsOff()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry { Id = "a", Message = UserText("hello"), Timestamp = "2026-01-01T00:00:00.000Z" },
        };

        SimpleStreamOptions? captured = null;
        await BranchSummarization.GenerateBranchSummaryAsync(
            entries,
            new GenerateBranchSummaryOptions
            {
                Model = ReasoningModel(),
                ThinkingLevel = ThinkingLevel.Off,
                Signal = CancellationToken.None,
                StreamFn = CapturingStream(options => captured = options),
            });

        Assert.NotNull(captured);
        Assert.False(HasReasoning(captured));
    }

    [Fact]
    public async Task GenerateBranchSummary_OmitsReasoningForNonReasoningModels()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry { Id = "a", Message = UserText("hello"), Timestamp = "2026-01-01T00:00:00.000Z" },
        };

        SimpleStreamOptions? captured = null;
        await BranchSummarization.GenerateBranchSummaryAsync(
            entries,
            new GenerateBranchSummaryOptions
            {
                Model = NonReasoningModel(),
                ThinkingLevel = ThinkingLevel.High,
                Signal = CancellationToken.None,
                StreamFn = CapturingStream(options => captured = options),
            });

        Assert.NotNull(captured);
        Assert.False(HasReasoning(captured));
    }

    // ---- context edits ------------------------------------------------------------------

    [Fact]
    public void PrepareBranchEntries_HidesAnEditedAwayMessage()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry { Id = "a", Message = UserText("secret"), Timestamp = "2026-01-01T00:00:00.000Z" },
            new ContextEditEntry
            {
                Id = "edit-a",
                TargetId = "a",
                Replacement = null,
                Timestamp = "2026-01-01T00:00:01.000Z",
            },
        };

        var preparation = BranchSummarization.PrepareBranchEntries(entries);

        // The edit hides the target, so nothing is summarized.
        Assert.Empty(preparation.Messages);
    }

    [Fact]
    public void PrepareBranchEntries_UsesTheLatestEditPerTarget()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry { Id = "a", Message = UserText("secret"), Timestamp = "2026-01-01T00:00:00.000Z" },
            new ContextEditEntry
            {
                Id = "edit-a-1",
                TargetId = "a",
                Replacement = new ContextEditReplacement { Content = JsonValue.Create("first") },
                Timestamp = "2026-01-01T00:00:01.000Z",
            },
            new ContextEditEntry
            {
                Id = "edit-a-2",
                TargetId = "a",
                Replacement = new ContextEditReplacement { Content = JsonValue.Create("second") },
                Timestamp = "2026-01-01T00:00:02.000Z",
            },
        };

        var preparation = BranchSummarization.PrepareBranchEntries(entries);

        var message = Assert.IsType<UserMessage>(Assert.Single(preparation.Messages));
        Assert.Equal("second", Assert.IsType<MessageContent.Text>(message.Content).Value);
    }
}
