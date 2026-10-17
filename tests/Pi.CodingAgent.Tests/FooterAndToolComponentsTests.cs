// ============================================================================
// Tests for modes/interactive/components/footer.ts, bash-execution.ts and
// tool-execution.ts (4f-3e Batch 2).
//
// Upstream vectors transcribed from test/footer-width.test.ts (11 cases) and
// test/bash-execution-width.test.ts (2 cases); tool-execution has no upstream
// file of its own — test/tool-execution-component.test.ts covers it, and its
// component-level cases are transcribed here (the renderer-inheritance cases
// live in the renderer corpus suites instead).
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Core.Tools.Renderers;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Tests;

// ------------------------------------------------------------------ footer

public sealed class FooterTests : SelectorTestFixture
{
    private sealed class FakeSession : IFooterSessionView
    {
        public List<SessionEntry> EntryList { get; } = [];

        public string? Name { get; init; }

        public string ModelId { get; init; } = "test-model";

        public string Provider { get; init; } = "test";

        public bool Reasoning { get; init; }

        public ThinkingLevel Level { get; init; } = ThinkingLevel.Off;

        public bool Subscription { get; init; }

        public ScopedModel? Routed { get; init; }

        public IReadOnlyList<SessionEntry> Entries => EntryList;

        public int EntryCount => EntryList.Count;

        public string SessionId => "test-session";

        public string? LeafId => null;

        public string? SessionName => Name;

        public string Cwd => "/tmp/project";

        public ModelSpec? Model => new()
        {
            Id = ModelId,
            Name = ModelId,
            Api = "test-api",
            Provider = Provider,
            BaseUrl = "https://example.invalid",
            ContextWindow = 200_000,
            Reasoning = Reasoning,
        };

        public ThinkingLevel ThinkingLevel => Level;

        public ScopedModel? RoutedModel => Routed;

        public bool IsUsingSubscription(string provider) => Subscription;

        public ContextUsage? GetContextUsage() => new() { ContextWindow = 200_000, Percent = 12.3 };
    }

    private sealed class FakeFooterData(int providerCount) : IReadonlyFooterDataProvider
    {
        public string? GetGitBranch() => "main";

        public IReadOnlyDictionary<string, string> GetExtensionStatuses() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        public int GetAvailableProviderCount() => providerCount;

        public Action OnBranchChange(Action callback) => () => { };
    }

    private static Usage Usage(long input, long output, double cost, long cacheRead = 0, long cacheWrite = 0) =>
        new(input, output, cacheRead, cacheWrite)
        {
            Cost = new UsageCost(Total: cost),
        };

    private static SessionEntry AssistantEntry(Usage usage) => new SessionMessageEntry
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = "",
        Message = new AssistantMessage([], UsageStats: usage),
    };

    private static string[] RenderLines(FooterComponent footer, int width) =>
        [.. footer.Render(width).Select(AnsiText.StripAnsi)];

    [Fact]
    public void FormatCwdDoesNotAbbreviateSiblingPathsThatShareTheHomePrefix()
    {
        Assert.Equal("/home/user2", FooterFormatting.FormatCwdForFooter("/home/user2", "/home/user"));
    }

    [Fact]
    public void FormatCwdAbbreviatesTheHomeDirectoryAndDescendants()
    {
        Assert.Equal("~", FooterFormatting.FormatCwdForFooter("/home/user", "/home/user"));
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"~{sep}project", FooterFormatting.FormatCwdForFooter("/home/user/project", "/home/user"));
    }

    [Fact]
    public void FormatTokensUsesCompactBuckets()
    {
        Assert.Equal("999", FooterFormatting.FormatTokens(999));
        Assert.Equal("1.0k", FooterFormatting.FormatTokens(1000));
        Assert.Equal("1.5k", FooterFormatting.FormatTokens(1500));
        // The <10000 bucket is `(count / 1000).toFixed(1)`: 9999 rounds up to "10.0k" while 9950
        // stays "9.9k" because 9.95 is below the halfway point in binary floating point — JS's
        // toFixed semantics, which JsNumber.ToFixed reproduces.
        Assert.Equal("10.0k", FooterFormatting.FormatTokens(9999));
        Assert.Equal("9.9k", FooterFormatting.FormatTokens(9950));
        // The >=10000 bucket rounds with Math.round (half away from zero).
        Assert.Equal("12k", FooterFormatting.FormatTokens(12_345));
        Assert.Equal("1.0M", FooterFormatting.FormatTokens(1_000_000));
        Assert.Equal("11M", FooterFormatting.FormatTokens(10_500_000));
    }

    [Fact]
    public void KeepsAllLinesWithinWidthForWideSessionNames()
    {
        const int width = 93;
        var session = new FakeSession { Name = string.Concat(Enumerable.Repeat("한글", 30)) };
        var footer = new FooterComponent(session, new FakeFooterData(1));

        foreach (var line in footer.Render(width))
        {
            Assert.True(UnicodeWidth.VisibleWidth(line) <= width);
        }
    }

    [Fact]
    public void KeepsStatsLineWithinWidthForWideModelAndProviderNames()
    {
        const int width = 60;
        var session = new FakeSession
        {
            Name = "",
            ModelId = string.Concat(Enumerable.Repeat("模", 30)),
            Provider = "공급자",
            Reasoning = true,
            Level = ThinkingLevel.High,
        };
        session.EntryList.Add(AssistantEntry(Usage(12_345, 6_789, 1.234)));
        var footer = new FooterComponent(session, new FakeFooterData(2));

        foreach (var line in footer.Render(width))
        {
            Assert.True(UnicodeWidth.VisibleWidth(line) <= width);
        }
    }

    [Fact]
    public void ShowsThePhysicalModelAVirtualModelRoutedTo()
    {
        var session = new FakeSession
        {
            Name = "",
            ModelId = "auto",
            Reasoning = true,
            Level = ThinkingLevel.High,
            Routed = new ScopedModel
            {
                Model = new ModelSpec
                {
                    Id = "gpt-5.6-luna",
                    Name = "gpt-5.6-luna",
                    Api = "test-api",
                    Provider = "test",
                    BaseUrl = "https://example.invalid",
                    ContextWindow = 200_000,
                },
                ThinkingLevel = ThinkingLevel.Medium,
            },
        };
        var footer = new FooterComponent(session, new FakeFooterData(1));

        var statsLine = RenderLines(footer, 120)[1];
        Assert.Contains("auto • high → gpt-5.6-luna • medium", statsLine);
    }

    [Fact]
    public void IncludesSummaryAndToolResultUsageInTheTotalCost()
    {
        var session = new FakeSession { Name = "" };
        session.EntryList.Add(AssistantEntry(Usage(100, 10, 0.5)));
        session.EntryList.Add(new BranchSummaryEntry
        {
            Id = "b1",
            Timestamp = "",
            FromId = "a0",
            Summary = "s",
            UsageStats = Usage(20, 5, 0.25),
        });
        session.EntryList.Add(new CompactionEntry
        {
            Id = "c1",
            Timestamp = "",
            Summary = "s",
            TokensBefore = 1000,
            UsageStats = Usage(5, 2, 0.125),
        });
        session.EntryList.Add(new SessionMessageEntry
        {
            Id = "t1",
            Timestamp = "",
            Message = new ToolResultMessage("call-1", "tool", [])
            {
                UsageStats = Usage(15, 3, 0.375),
            },
        });
        var footer = new FooterComponent(session, new FakeFooterData(1));

        Assert.Contains("$1.250", RenderLines(footer, 120)[1]);
    }

    [Fact]
    public void UpdatesCachedUsageTotalsAfterAnEntryIsAppended()
    {
        var usage = Usage(10, 1, 0.5);
        var session = new FakeSession { Name = "" };
        session.EntryList.Add(AssistantEntry(usage));
        var footer = new FooterComponent(session, new FakeFooterData(1));
        Assert.Contains("$0.500", RenderLines(footer, 120)[1]);

        session.EntryList.Add(AssistantEntry(usage));
        Assert.Contains("$1.000", RenderLines(footer, 120)[1]);
    }

    [Fact]
    public void ShowsTheLatestCacheHitRateWhenCacheUsageIsPresent()
    {
        var session = new FakeSession { Name = "" };
        session.EntryList.Add(AssistantEntry(Usage(100, 10, 0.001, cacheRead: 50, cacheWrite: 50)));
        var footer = new FooterComponent(session, new FakeFooterData(1));

        Assert.Contains("CH25.0%", RenderLines(footer, 120)[1]);
    }

    [Fact]
    public void MarksKimiCodingCostsAsSubscriptionEstimates()
    {
        var session = new FakeSession { Name = "", Provider = "kimi-coding" };
        session.EntryList.Add(AssistantEntry(Usage(100, 10, 1.234)));
        var footer = new FooterComponent(session, new FakeFooterData(1));

        Assert.Contains("$1.234 (sub)", RenderLines(footer, 120)[1]);
    }

    [Fact]
    public void MarksExplicitlyIdentifiedSubscriptionAuth()
    {
        var session = new FakeSession { Name = "", Provider = "anthropic", Subscription = true };
        var footer = new FooterComponent(session, new FakeFooterData(1));

        Assert.Contains("$0.000 (sub)", RenderLines(footer, 120)[1]);
    }

    [Fact]
    public void DoesNotMarkGenericOAuthSignInAsSubscription()
    {
        var session = new FakeSession { Name = "", Provider = "openrouter" };
        session.EntryList.Add(AssistantEntry(Usage(100, 10, 1.234)));
        var footer = new FooterComponent(session, new FakeFooterData(1));

        var stats = RenderLines(footer, 120)[1];
        Assert.Contains("$1.234", stats);
        Assert.DoesNotContain("(sub)", stats);
    }

    [Fact]
    public void SanitizesExtensionStatusesAndSortsThemByKey()
    {
        var session = new FakeSession { Name = "" };
        var footer = new FooterComponent(session, new FakeFooterData(1));
        // SanitizeStatusText collapses whitespace runs (newlines/tabs included).
        Assert.Equal("a b c", FooterFormatting.SanitizeStatusText("  a\n\tb   c  "));
    }
}

// ------------------------------------------------------------------ bash execution

public sealed class BashExecutionTests : SelectorTestFixture
{
    [Fact]
    public void CollapsedPreviewLinesRespectRenderTimeWidth()
    {
        var component = new BashExecutionComponent("pwd", new FakeTui());
        var longLine = new string('x', 150);
        component.AppendOutput($"{longLine}\n{longLine}\n");
        component.SetComplete(0, false);

        foreach (var line in component.Render(80))
        {
            Assert.True(UnicodeWidth.VisibleWidth(line) <= 80);
        }
    }

    [Fact]
    public void RecomputesLinesWhenWidthChangesBetweenRenders()
    {
        var component = new BashExecutionComponent("echo hello", new FakeTui());
        component.AppendOutput(string.Concat(Enumerable.Repeat("abcdefghij", 20)) + "\n");
        component.SetComplete(0, false);

        foreach (var line in component.Render(200))
        {
            Assert.True(UnicodeWidth.VisibleWidth(line) <= 200);
        }

        foreach (var line in component.Render(60))
        {
            Assert.True(UnicodeWidth.VisibleWidth(line) <= 60);
        }
    }

    [Fact]
    public void AppendOutputContinuesAnIncompleteLineAndNormalizesLineEndings()
    {
        var component = new BashExecutionComponent("cmd", new FakeTui());
        component.AppendOutput("abc");
        component.AppendOutput("def\r\ng");
        component.AppendOutput("hi\r\n");

        // The trailing "\n" of the last chunk becomes a new (still empty) logical line, exactly as
        // TS's `outputLines.push(...clean.split("\n"))` does — `split` keeps the empty tail item.
        Assert.Equal("abcdef\nghi\n", component.GetOutput());
    }

    [Fact]
    public void ShowsExitCodeAndCancelledStatus()
    {
        var failed = new BashExecutionComponent("cmd", new FakeTui());
        failed.AppendOutput("x\n");
        failed.SetComplete(2, false);
        Assert.Contains("(exit 2)", string.Join("\n", failed.Render(120).Select(AnsiText.StripAnsi)));

        var cancelled = new BashExecutionComponent("cmd", new FakeTui());
        cancelled.SetComplete(null, true);
        Assert.Contains("(cancelled)", string.Join("\n", cancelled.Render(120).Select(AnsiText.StripAnsi)));
    }

    [Fact]
    public void ShowsTruncationWarningWithTheFullOutputPath()
    {
        var component = new BashExecutionComponent("cmd", new FakeTui());
        component.AppendOutput("x\n");
        // TS only warns when a truncation actually happened (`truncationResult?.truncated ||
        // contextTruncation.truncated`); a path alone is not enough.
        component.SetComplete(
            0,
            false,
            new Truncate.TruncationResult
            {
                Content = "x",
                Truncated = true,
                TruncatedBy = Pi.CodingAgent.Core.Tools.TruncatedBy.Lines,
                TotalLines = 5000,
                TotalBytes = 5000,
                OutputLines = 1,
                OutputBytes = 1,
                LastLinePartial = false,
                FirstLineExceedsLimit = false,
                MaxLines = Truncate.DefaultMaxLines,
                MaxBytes = Truncate.DefaultMaxBytes,
            },
            @"C:\tmp\full.txt");

        var rendered = string.Join("\n", component.Render(200).Select(AnsiText.StripAnsi));
        Assert.Contains("Output truncated. Full output: C:\\tmp\\full.txt", rendered);
    }

    [Fact]
    public void CollapsedPreviewCountsHiddenLinesAndExpandedShowsAll()
    {
        var component = new BashExecutionComponent("cmd", new FakeTui());
        for (var i = 1; i <= 25; i++)
        {
            component.AppendOutput($"line-{i:00}\n");
        }

        component.SetComplete(0, false);
        var collapsed = string.Join("\n", component.Render(200).Select(AnsiText.StripAnsi));
        // 25 "line-NN\n" chunks produce 26 logical lines (the final "\n" leaves an empty tail line,
        // as TS's split does); the preview keeps the last 20, so 6 are hidden.
        Assert.Contains("... 6 more lines", collapsed);
        Assert.Contains("to expand", collapsed);
        Assert.Contains("line-25", collapsed);
        Assert.DoesNotContain("line-01", collapsed);

        component.SetExpanded(true);
        var expanded = string.Join("\n", component.Render(200).Select(AnsiText.StripAnsi));
        Assert.Contains("line-01", expanded);
        Assert.DoesNotContain("more lines", expanded);
    }
}

// ------------------------------------------------------------------ tool execution

public sealed class ToolExecutionComponentTests : SelectorTestFixture
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static ToolExecutionComponent Create(
        string toolName,
        ToolRenderers? renderers = null,
        IReadOnlyDictionary<string, object?>? args = null) =>
        new(toolName, "tool-1", args ?? NoArgs, null, renderers, new FakeTui(), Environment.CurrentDirectory);

    private static string Stripped(ToolExecutionComponent component, int width = 120) =>
        string.Join("\n", component.Render(width).Select(AnsiText.StripAnsi));

    [Fact]
    public void StacksCustomCallAndResultRenderers()
    {
        var renderers = new ToolRenderers
        {
            RenderCall = (_, _, _) => new TuiText("custom call", 0, 0),
            RenderResult = (_, _, _, _) => new TuiText("custom result", 0, 0),
        };
        var component = Create("custom_tool", renderers);

        Assert.Contains("custom call", Stripped(component));

        component.UpdateResult(new AgentToolResult([new TextContent("done")]));
        var rendered = Stripped(component);
        Assert.Contains("custom call", rendered);
        Assert.Contains("custom result", rendered);
    }

    [Fact]
    public void SelfRenderedEmptyRowsTakeNoLayoutSpace()
    {
        var renderers = new ToolRenderers
        {
            RenderShell = "self",
            RenderCall = (_, _, _) => new TuiText("", 0, 0),
            RenderResult = (_, _, _, _) => new TuiText("", 0, 0),
        };
        var component = Create("custom_tool", renderers);
        Assert.Empty(component.Render(120));

        component.UpdateResult(new AgentToolResult([]));
        Assert.Empty(component.Render(120));
    }

    [Fact]
    public void FallbackRenderingShowsTheToolNameAndLegacyFilePathVariable()
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["file_path"] = "README.md" };
        var component = Create("read", args: args);

        var rendered = Stripped(component);
        Assert.Contains("read", rendered);
        Assert.Contains("README.md", rendered);
    }

    [Fact]
    public void CollapsesFallbackResultsUntilExpanded()
    {
        var component = Create("custom_tool", new ToolRenderers());
        var output = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"line-{i}"));
        component.UpdateResult(new AgentToolResult([new TextContent(output)]));

        var collapsed = Stripped(component);
        Assert.Contains("line-10", collapsed);
        Assert.DoesNotContain("line-11", collapsed);
        Assert.Contains("5 more lines", collapsed);
        Assert.Contains("to expand", collapsed);

        component.SetExpanded(true);
        var expanded = Stripped(component);
        Assert.Contains("line-15", expanded);
        Assert.DoesNotContain("more lines", expanded);
    }

    [Fact]
    public void SharesRendererStateAcrossCallAndResultSlots()
    {
        var renderers = new ToolRenderers
        {
            RenderCall = (_, _, context) =>
            {
                var state = (Dictionary<string, object?>)context.State!;
                state.TryAdd("token", "shared-token");
                return new TuiText($"custom call {state["token"]}", 0, 0);
            },
            RenderResult = (_, _, _, context) =>
            {
                var state = (Dictionary<string, object?>)context.State!;
                return new TuiText($"custom result {state.GetValueOrDefault("token")}", 0, 0);
            },
        };
        var component = Create("custom_tool", renderers);
        component.UpdateResult(new AgentToolResult([new TextContent("done")]));

        var rendered = Stripped(component);
        Assert.Contains("custom call shared-token", rendered);
        Assert.Contains("custom result shared-token", rendered);
    }

    [Fact]
    public void ExposesArgsInTheResultRenderContext()
    {
        var renderers = new ToolRenderers
        {
            RenderCall = (_, _, _) => new TuiText("call", 0, 0),
            RenderResult = (_, _, _, context) =>
                new TuiText($"arg:{context.Args.GetValueOrDefault("foo")}", 0, 0),
        };
        var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["foo"] = "bar" };
        var component = Create("custom_tool", renderers, args);
        component.UpdateResult(new AgentToolResult([new TextContent("done")]));

        Assert.Contains("arg:bar", Stripped(component));
    }

    [Fact]
    public void ThrowingRendererFallsBackInsteadOfCrashing()
    {
        var renderers = new ToolRenderers
        {
            RenderCall = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var component = Create("custom_tool", renderers);

        // The fallback header carries the tool name.
        Assert.Contains("custom_tool", Stripped(component));
    }

    [Fact]
    public void ExposesDurationOnlyForFinalResults()
    {
        long? seen = -1;
        var renderers = new ToolRenderers
        {
            RenderResult = (_, _, _, context) =>
            {
                seen = context.DurationMs;
                return new TuiText("r", 0, 0);
            },
        };
        var component = Create("custom_tool", renderers);

        component.UpdateResult(new AgentToolResult([new TextContent("partial")]), isPartial: true, durationMs: 4200);
        Assert.Null(seen);

        component.UpdateResult(new AgentToolResult([new TextContent("done")]), isPartial: false, durationMs: 4200);
        Assert.Equal(4200, seen);
    }

    [Fact]
    public void ClickExpandsACollapsedResult()
    {
        var renderers = BuiltInToolRenderers.WithBuiltInRenderers("read", null);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "notes.txt" };
        var component = Create("read", renderers, args);
        component.UpdateResult(new AgentToolResult([new TextContent("hidden content")]));

        const int width = 120;
        var lines = component.Render(width);
        var row = Array.FindIndex(lines, line => AnsiText.StripAnsi(line).Contains("notes.txt"));
        Assert.True(row >= 0);

        var handled = component.HandleMouse(new TuiMouseEvent
        {
            Type = TuiMouseEventType.Click,
            Button = TuiMouseButton.Left,
            X = 2,
            Y = row,
            ScreenX = 2,
            ScreenY = row,
            Width = width,
            Height = lines.Length,
            ClickCount = 1,
        });

        Assert.True(handled?.Handled);
        Assert.Contains("hidden content", Stripped(component, width));
    }

    [Fact]
    public void BuiltInReadRenderersCollapseUntilExpanded()
    {
        var renderers = BuiltInToolRenderers.WithBuiltInRenderers("read", null);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "notes.txt" };
        var component = Create("read", renderers, args);
        component.UpdateResult(new AgentToolResult([new TextContent("hidden content")]));

        var collapsed = Stripped(component);
        Assert.Contains("notes.txt", collapsed);
        Assert.DoesNotContain("hidden content", collapsed);

        component.SetExpanded(true);
        Assert.Contains("hidden content", Stripped(component));
    }

    [Fact]
    public void BuiltInReadRendererTreatsNullOffsetAndLimitAsFullFileRead()
    {
        var renderers = BuiltInToolRenderers.WithBuiltInRenderers("read", null);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = "src/example.ts",
            ["offset"] = null,
            ["limit"] = null,
        };
        var component = Create("read", renderers, args);

        var rendered = Stripped(component);
        Assert.Contains("src/example.ts", rendered);
        Assert.DoesNotContain("src/example.ts:", rendered);
    }
}
