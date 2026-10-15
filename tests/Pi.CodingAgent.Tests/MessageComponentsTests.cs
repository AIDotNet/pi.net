// ============================================================================
// Tests for modes/interactive/components message components
// (assistant-message / user-message / custom-message / skill-invocation /
//  branch-summary / compaction-summary / markdown-transform)
// Vectors transcribed from TS test/assistant-message.test.ts,
// test/user-message.test.ts, test/custom-message.test.ts,
// test/collapsible-message-components.test.ts
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class MessageComponentsTests : IDisposable
{
    private const string Osc133ZoneStart = "\x1b]133;A\x07";
    private const string Osc133ZoneEnd = "\x1b]133;B\x07";
    private const string Osc133ZoneFinal = "\x1b]133;C\x07";
    private const string BgReset = "\x1b[49m";

    private readonly bool? _previousChalk = Chalk.EnabledOverride;

    public MessageComponentsTests()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    private static AssistantMessage CreateAssistantMessage(
        IReadOnlyList<ContentBlock> content,
        StopReason stopReason = StopReason.Stop) =>
        new(content, stopReason, null, null, "gpt-4o-mini", "openai-responses", "openai");

    private static string Text(object component, int width) =>
        AnsiText.StripAnsi(string.Join("\n", ((IComponent)component).Render(width)));

    private static TuiMouseEvent ClickAt(int x, int y, int width, int height) => new()
    {
        Type = TuiMouseEventType.Click,
        Button = TuiMouseButton.Left,
        X = x,
        Y = y,
        ScreenX = x,
        ScreenY = y,
        Width = width,
        Height = height,
        ClickCount = 1,
    };

    private static int RowOf(IComponent component, int width, string marker)
    {
        var lines = component.Render(width);
        for (var i = 0; i < lines.Length; i++)
        {
            if (AnsiText.StripAnsi(lines[i]).Contains(marker, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------- AssistantMessageComponent

    [Fact]
    public void AssistantMessage_AddsOsc133ZoneMarkersWithoutToolCalls()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage([new TextContent("hello")]));

        var lines = component.Render(40);

        Assert.NotEmpty(lines);
        Assert.Contains(Osc133ZoneStart, lines[0], StringComparison.Ordinal);
        Assert.StartsWith(Osc133ZoneEnd + Osc133ZoneFinal, lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantMessage_OmitsOsc133ZoneMarkersWithToolCalls()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage(
            [
                new TextContent("calling tool"),
                new ToolCallContent("tool-1", "read", new { path = "file.txt" }),
            ]));

        var rendered = string.Join("\n", component.Render(60));

        Assert.DoesNotContain(Osc133ZoneStart, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Osc133ZoneEnd, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Osc133ZoneFinal, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantMessage_RendersLengthStopsWithNeutralTruncationWording()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage([new ThinkingContent("private reasoning")], StopReason.Length),
            hideThinkingBlock: true);

        var rendered = Text(component, 80);

        Assert.Contains("Thinking...", rendered, StringComparison.Ordinal);
        Assert.Contains("Response was truncated before completion.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantMessage_CoalescesAdjacentThinkingBlocksIntoOneHiddenLabel()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage(
            [
                new ThinkingContent("first thought"),
                new ThinkingContent(""),
                new ThinkingContent("second thought"),
                new TextContent("answer"),
            ]),
            hideThinkingBlock: true);

        var rendered = Text(component, 80);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rendered, @"Thinking\.\.\."));
        Assert.Contains("answer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantMessage_CollapsesIndividualThinkingRunsWhenClicked()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage(
            [
                new ThinkingContent("first reasoning"),
                new TextContent("answer"),
                new ThinkingContent("second reasoning"),
            ]));
        const int width = 80;
        var firstThinkingRow = RowOf(component, width, "first reasoning");
        Assert.True(firstThinkingRow >= 0);

        var lines = component.Render(width);
        var result = component.HandleMouse(ClickAt(1, firstThinkingRow, width, lines.Length));

        Assert.True(result?.Handled == true);

        var collapsed = Text(component, width);
        Assert.DoesNotContain("first reasoning", collapsed, StringComparison.Ordinal);
        Assert.Contains("Thinking...", collapsed, StringComparison.Ordinal);
        Assert.Contains("second reasoning", collapsed, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantMessage_UsesConfiguredOutputPadding()
    {
        var component = new AssistantMessageComponent(
            CreateAssistantMessage(
            [
                new TextContent("hello"),
                new ThinkingContent("reasoning"),
            ]));

        var lines = component.Render(80).Select(AnsiText.StripAnsi).ToArray();
        Assert.Contains(lines, line => line.Contains(" hello", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(" reasoning", StringComparison.Ordinal));

        component.SetOutputPad(0);
        var updated = component.Render(80).Select(AnsiText.StripAnsi).ToArray();
        Assert.Contains(updated, line => line.StartsWith("hello", StringComparison.Ordinal));
        Assert.Contains(updated, line => line.StartsWith("reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public void AssistantMessage_ChainsMarkdownTransformersInRegistrationOrder()
    {
        var calls = new List<string>();
        var component = new AssistantMessageComponent(
            CreateAssistantMessage([new TextContent("The result is $x^2$.")]),
            false,
            null,
            "Thinking...",
            1,
            [
                (markdown, context) =>
                {
                    calls.Add("formula");
                    Assert.Equal(new MarkdownTransformContext
                    {
                        MessageType = "assistant",
                        IsStreaming = false,
                        AvailableWidth = 78,
                    }, context);
                    return markdown.Replace("$x^2$", "x²");
                },
                // TS declares one-arg transformers (JS allows fewer formal parameters); C# delegates
                // always take the full signature, so the suffix transformer ignores the context.
                (markdown, _) =>
                {
                    calls.Add("suffix");
                    return $"{markdown} Done.";
                },
            ]);

        Assert.Contains("The result is x². Done.", Text(component, 80), StringComparison.Ordinal);
        Assert.Equal(["formula", "suffix"], calls);
    }

    [Fact]
    public void AssistantMessage_IdentifiesPartialMarkdownAsStreaming()
    {
        var streamingStates = new List<bool>();
        var message = CreateAssistantMessage([new TextContent("partial")]);
        var component = new AssistantMessageComponent(
            null,
            false,
            null,
            "Thinking...",
            1,
            [
                (markdown, context) =>
                {
                    streamingStates.Add(context.IsStreaming);
                    return context.IsStreaming ? markdown : $"{markdown} transformed";
                },
            ]);

        component.UpdateContent(message, true);
        Assert.DoesNotContain("transformed", Text(component, 80), StringComparison.Ordinal);

        component.UpdateContent(message, false);
        Assert.Contains("partial transformed", Text(component, 80), StringComparison.Ordinal);
        Assert.Equal([true, false], streamingStates);
    }

    [Fact]
    public void AssistantMessage_ReappliesTransformersWhenWidthChanges()
    {
        var availableWidths = new List<int>();
        var component = new AssistantMessageComponent(
            CreateAssistantMessage([new TextContent("answer")]),
            false,
            null,
            "Thinking...",
            1,
            [
                (markdown, context) =>
                {
                    availableWidths.Add(context.AvailableWidth);
                    return $"{markdown} ({context.AvailableWidth})";
                },
            ]);

        Assert.Contains("answer (78)", Text(component, 80), StringComparison.Ordinal);
        component.Render(80);
        Assert.Contains("answer (58)", Text(component, 60), StringComparison.Ordinal);
        Assert.Equal([78, 58], availableWidths);
    }

    [Fact]
    public void AssistantMessage_ContinuesTransformerChainWhenATransformerThrows()
    {
        var calls = new List<string>();
        var component = new AssistantMessageComponent(
            CreateAssistantMessage([new TextContent("still visible")]),
            false,
            null,
            "Thinking...",
            1,
            [
                (markdown, _) =>
                {
                    calls.Add("first");
                    return markdown.Replace("still", "remains");
                },
                (_, _) =>
                {
                    calls.Add("throw");
                    throw new InvalidOperationException("broken transformer");
                },
                (markdown, _) =>
                {
                    calls.Add("last");
                    return $"{markdown} after error";
                },
            ]);

        Assert.Contains("remains visible after error", Text(component, 80), StringComparison.Ordinal);
        Assert.Equal(["first", "throw", "last"], calls);
    }

    [Fact]
    public void AssistantMessage_TransformsTextAndThinkingWithoutMutatingTheMessage()
    {
        var message = CreateAssistantMessage(
            [
                new TextContent("answer"),
                new ThinkingContent("reasoning"),
            ]);
        var component = new AssistantMessageComponent(
            message,
            false,
            null,
            "Thinking...",
            1,
            [
                (markdown, context) => $"{context.MessageType}:{markdown}",
            ]);

        var rendered = Text(component, 80);
        Assert.Contains("assistant:answer", rendered, StringComparison.Ordinal);
        Assert.Contains("assistant-thinking:reasoning", rendered, StringComparison.Ordinal);
        Assert.Equal(
            [new TextContent("answer"), new ThinkingContent("reasoning")],
            message.Content);
    }

    // ------------------------------------------------------------- UserMessageComponent

    [Fact]
    public void UserMessage_KeepsHeightStableWithClosingZoneMarkers()
    {
        var component = new UserMessageComponent("hello");

        var lines = component.Render(20);

        Assert.Equal(3, lines.Length);
        Assert.Contains(Osc133ZoneStart, lines[0], StringComparison.Ordinal);
        Assert.EndsWith(BgReset, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Osc133ZoneEnd, lines[0], StringComparison.Ordinal);
        Assert.Contains("hello", lines[1], StringComparison.Ordinal);
        Assert.StartsWith(Osc133ZoneEnd + Osc133ZoneFinal, lines[2], StringComparison.Ordinal);
        Assert.EndsWith(BgReset, lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_ChainsTransformersWithUserContext()
    {
        var calls = new List<string>();
        var component = new UserMessageComponent(
            "The input is $x^2$.",
            null,
            1,
            [
                (markdown, context) =>
                {
                    calls.Add("formula");
                    Assert.Equal(new MarkdownTransformContext
                    {
                        MessageType = "user",
                        IsStreaming = false,
                        AvailableWidth = 78,
                    }, context);
                    return markdown.Replace("$x^2$", "x²");
                },
                (markdown, _) =>
                {
                    calls.Add("suffix");
                    return $"{markdown} Done.";
                },
            ]);

        Assert.Contains("The input is x². Done.", Text(component, 80), StringComparison.Ordinal);
        Assert.Equal(["formula", "suffix"], calls);
    }

    [Fact]
    public void UserMessage_ReappliesTransformersWhenInvalidated()
    {
        var suffix = "before";
        var component = new UserMessageComponent(
            "Message",
            null,
            1,
            [(markdown, _) => $"{markdown} {suffix}"]);

        Assert.Contains("Message before", Text(component, 80), StringComparison.Ordinal);

        suffix = "after";
        component.Invalidate();

        Assert.Contains("Message after", Text(component, 80), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- CustomMessageComponent

    [Fact]
    public void CustomMessage_ProvidesOutputPaddingToCustomRenderers()
    {
        var optionsSeen = new List<MessageRenderOptions>();
        MessageRenderer renderer = (_, options, _) =>
        {
            optionsSeen.Add(options);
            return new Pi.Tui.Components.Text("custom", options.OutputPad, 0);
        };
        var message = new CustomMessage("test", MessageContent.From("custom"), true, 0);

        var component = new CustomMessageComponent(message, renderer, null, 1);

        Assert.Equal(new MessageRenderOptions { Expanded = false, OutputPad = 1 }, optionsSeen[0]);
        Assert.Contains(
            component.Render(40).Select(AnsiText.StripAnsi),
            line => line.StartsWith(" custom", StringComparison.Ordinal));

        component.SetOutputPad(0);

        Assert.Equal(new MessageRenderOptions { Expanded = false, OutputPad = 0 }, optionsSeen[^1]);
        Assert.Contains(
            component.Render(40).Select(AnsiText.StripAnsi),
            line => line.StartsWith("custom", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- collapsible messages

    [Fact]
    public void CompactionSummaryMessage_TogglesWhenClicked()
    {
        var component = new CompactionSummaryMessageComponent(
            new CompactionSummaryMessage("compaction details", 1234, 0));

        Assert.DoesNotContain("compaction details", Text(component, 80), StringComparison.Ordinal);
        Assert.Contains("Compacted from 1,234 tokens (", Text(component, 80), StringComparison.Ordinal);

        var row = RowOf(component, 80, "[compaction]");
        Assert.True(row >= 0);
        var lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.Contains("compaction details", Text(component, 80), StringComparison.Ordinal);

        row = RowOf(component, 80, "[compaction]");
        lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.DoesNotContain("compaction details", Text(component, 80), StringComparison.Ordinal);
    }

    [Fact]
    public void BranchSummaryMessage_TogglesWhenClicked()
    {
        var component = new BranchSummaryMessageComponent(
            new BranchSummaryMessage("branch details", "entry-1", 0));

        Assert.DoesNotContain("branch details", Text(component, 80), StringComparison.Ordinal);

        var row = RowOf(component, 80, "[branch]");
        Assert.True(row >= 0);
        var lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.Contains("branch details", Text(component, 80), StringComparison.Ordinal);

        row = RowOf(component, 80, "[branch]");
        lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.DoesNotContain("branch details", Text(component, 80), StringComparison.Ordinal);
    }

    [Fact]
    public void SkillInvocationMessage_TogglesWhenClicked()
    {
        var component = new SkillInvocationMessageComponent(
            new Core.ParsedSkillBlock("example-skill", "/tmp/example-skill.md", "skill details", null));

        Assert.DoesNotContain("skill details", Text(component, 80), StringComparison.Ordinal);
        Assert.Contains("example-skill", Text(component, 80), StringComparison.Ordinal);

        var row = RowOf(component, 80, "[skill]");
        Assert.True(row >= 0);
        var lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.Contains("skill details", Text(component, 80), StringComparison.Ordinal);

        row = RowOf(component, 80, "[skill]");
        lines = component.Render(80);
        Assert.True(component.HandleMouse(ClickAt(2, row, 80, lines.Length))?.Handled == true);
        Assert.DoesNotContain("skill details", Text(component, 80), StringComparison.Ordinal);
    }

    [Fact]
    public void CustomMessage_FallsBackToDefaultRenderingWhenRendererThrows()
    {
        MessageRenderer renderer = (_, _, _) => throw new InvalidOperationException("broken renderer");        var message = new CustomMessage("test", MessageContent.From("plain text"), true, 0);

        var component = new CustomMessageComponent(message, renderer);

        var rendered = Text(component, 80);
        Assert.Contains("[test]", rendered, StringComparison.Ordinal);
        Assert.Contains("plain text", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomMessage_JoinsTextBlocksWithNewlines()
    {
        var message = new CustomMessage(
            "test",
            MessageContent.BlocksOf([new TextContent("first"), new TextContent("second")]),
            true,
            0);

        var component = new CustomMessageComponent(message);

        var rendered = Text(component, 80);
        Assert.Contains("first", rendered, StringComparison.Ordinal);
        Assert.Contains("second", rendered, StringComparison.Ordinal);
    }
}
