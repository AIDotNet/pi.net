// ============================================================================
// Tests for modes/interactive/components/session-selector.ts
//
// The upstream suite has no dedicated session-selector test file, so every case
// here is derived from the source behaviour (same approach as Batches E-3/F-3).
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SessionSelectorTests : SelectorTestFixture
{
    private static SessionInfo Session(
        string path,
        string? name = null,
        string? firstMessage = null,
        int? ageMs = null,
        string? parentPath = null,
        int messageCount = 3,
        string cwd = @"C:\work") =>
        new()
        {
            Path = path,
            Id = path,
            Cwd = cwd,
            Name = name,
            ParentSessionPath = parentPath,
            Created = DateTimeOffset.Now.AddHours(-2),
            Modified = DateTimeOffset.Now.AddMilliseconds(-(ageMs ?? 60_000)),
            MessageCount = messageCount,
            FirstMessage = firstMessage ?? "hello world",
            AllMessagesText = "hello world",
        };

    private static SessionSelectorComponent CreateSelector(
        SessionsLoader? currentLoader = null,
        SessionsLoader? allLoader = null,
        SessionSelectorComponent.Options? options = null,
        string? currentSessionFilePath = null,
        IReadOnlyList<SessionInfo>? currentSessions = null,
        IReadOnlyList<SessionInfo>? allSessions = null)
    {
        currentLoader ??= (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>(currentSessions ?? []);
        allLoader ??= (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>(allSessions ?? []);
        return new SessionSelectorComponent(
            currentLoader, allLoader,
            onSelect: _ => { }, onCancel: () => { }, onExit: () => { }, requestRender: () => { },
            options: options, currentSessionFilePath: currentSessionFilePath);
    }

    // -------------------------------------------------------------- pure helpers

    [Fact]
    public void FormatSessionDateBucketsByAge()
    {
        Assert.Equal("now", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now));
        Assert.Equal("5m", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddMinutes(-5)));
        Assert.Equal("3h", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddHours(-3)));
        Assert.Equal("2d", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddDays(-2)));
        Assert.Equal("2w", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddDays(-15)));
        Assert.Equal("2mo", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddDays(-70)));
        Assert.Equal("2y", SessionSelectorStatics.FormatSessionDate(DateTimeOffset.Now.AddDays(-800)));
    }

    [Fact]
    public void ShortenPathCollapsesHome()
    {
        var home = Pi.CodingAgent.Utils.Paths.GetHomeDirectory();
        Assert.Equal("~", SessionSelectorStatics.ShortenPath(home));
        // The TS slice keeps the original separator, so on Windows this is "~\sub".
        var joined = System.IO.Path.Combine(home, "sub");
        Assert.Equal($"~{joined[home.Length..]}", SessionSelectorStatics.ShortenPath(joined));
        Assert.Equal(@"C:\abs", SessionSelectorStatics.ShortenPath(@"C:\abs"));
    }

    // -------------------------------------------------------------- tree building

    [Fact]
    public void BuildSessionTreeAttachesChildrenToExistingParentsOnly()
    {
        var parent = Session(@"C:\s\parent.jsonl");
        var child = Session(@"C:\s\child.jsonl", parentPath: @"C:\s\parent.jsonl");
        var orphan = Session(@"C:\s\orphan.jsonl", parentPath: @"C:\s\missing.jsonl");
        var sessions = new[] { child, orphan, parent }; // child listed first on purpose

        var roots = SessionTree.BuildSessionTree(sessions);

        Assert.Equal(2, roots.Count); // parent + orphan (the missing parent makes the orphan a root)
        var parentNode = roots.Single(n => n.Session.Path == parent.Path);
        Assert.Single(parentNode.Children);
        Assert.Equal(child.Path, parentNode.Children[0].Session.Path);
    }

    [Fact]
    public void FlattenSessionTreeProducesDepthLastAndContinuationMetadata()
    {
        var parent = Session(@"C:\s\p.jsonl", firstMessage: "parent msg");
        var childA = Session(@"C:\s\ca.jsonl", parentPath: @"C:\s\p.jsonl", firstMessage: "childA msg");
        var childB = Session(@"C:\s\cb.jsonl", parentPath: @"C:\s\p.jsonl", firstMessage: "childB msg");
        var grandchild = Session(@"C:\s\g.jsonl", parentPath: @"C:\s\ca.jsonl", firstMessage: "grandchild msg");

        var roots = SessionTree.BuildSessionTree(new[] { parent, childA, childB, grandchild });
        var flat = SessionTree.FlattenSessionTree(roots);

        Assert.Equal([@"C:\s\p.jsonl", @"C:\s\ca.jsonl", @"C:\s\g.jsonl", @"C:\s\cb.jsonl"],
            [.. flat.Select(n => n.Session.Path)]);
        Assert.Equal([0, 1, 2, 1], [.. flat.Select(n => n.Depth)]);
        // p is the only root (last); ca is the first of two children; g is an only child; cb is the last child.
        Assert.Equal([true, false, true, true], [.. flat.Select(n => n.IsLast)]);
        // Ancestor continuation flags: ca's comes from the root walk (depth 0 → false);
        // g's comes from ca's walk (ca not last → true); cb's from the root walk (false).
        Assert.Equal([false, false, true, false], [.. flat.Select(n =>
            n.Depth == 0 ? false : n.AncestorContinues[^1])]);
        // Rendered prefixes: root none; ca "   ├─ "; g "   │  └─ "; cb "   └─ ".
        // The render leads with the search input row and a blank row, so sessions start at [2].
        var rendered = RenderLines(new[] { parent, childA, childB, grandchild }, 200);
        Assert.Contains("├─ childA msg", rendered[3], StringComparison.Ordinal);
        Assert.Contains("│  └─ grandchild msg", rendered[4], StringComparison.Ordinal);
        Assert.Contains("└─ childB msg", rendered[5], StringComparison.Ordinal);
    }

    /// <summary>Render the flat tree rows for the given sessions through a real SessionList.</summary>
    private static string[] RenderLines(IReadOnlyList<SessionInfo> sessions, int width)
    {
        var selector = CreateSelector(currentSessions: sessions);
        return RenderLines(selector.GetSessionList(), width);
    }

    // ---------------------------------------------------------------- rendering

    [Fact]
    public void RenderShowsSessionRowsWithCountsAndAges()
    {
        var selector = CreateSelector(currentSessions: new[]
        {
            Session(@"C:\s\a.jsonl", name: "alpha session", ageMs: 120_000, messageCount: 7),
        });
        var text = RenderText(selector.GetSessionList(), 120);
        Assert.Contains("alpha session", text, StringComparison.Ordinal);
        Assert.Contains("7 2m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderColorsNamedSessionsWithWarning()
    {
        var selector = CreateSelector(
            currentSessions: new[] { Session(@"C:\s\a.jsonl", name: "named one") });
        var raw = string.Join("\n", ((IComponent)selector.GetSessionList()).Render(120));
        // The fixture theme runs in truecolor, so the warning hue is an SGR 38;2 sequence.
        Assert.Contains("named one", raw, StringComparison.Ordinal);
        Assert.Contains("\x1b[38;2;", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderShowsEmptyStateMessages()
    {
        // named filter + current folder
        var selector = CreateSelector(currentSessions: []);
        var list = selector.GetSessionList();
        list.SetNameFilter(NameFilter.Named);
        Assert.Contains("No named sessions in current folder", RenderText(list, 120), StringComparison.Ordinal);

        // named filter + all scope (showCwd)
        list.SetSessions([], showCwd: true);
        list.SetNameFilter(NameFilter.Named);
        Assert.Contains("No named sessions found", RenderText(list, 120), StringComparison.Ordinal);

        // all scope
        list.SetNameFilter(NameFilter.All);
        list.SetSessions([], showCwd: true);
        Assert.Contains("No sessions found", RenderText(list, 120), StringComparison.Ordinal);

        // current folder scope
        list.SetSessions([], showCwd: false);
        Assert.Contains("No sessions in current folder. Press Tab to view all.", RenderText(list, 120),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- filtering

    [Fact]
    public void SearchFiltersSessions()
    {
        var alpha = Session(@"C:\s\a.jsonl", name: "deploy notes");
        var beta = Session(@"C:\s\b.jsonl", firstMessage: "random chat");
        var selector = CreateSelector(currentSessions: new[] { alpha, beta });
        var list = selector.GetSessionList();

        list.HandleInput("d");
        list.HandleInput("e");
        Assert.Contains("deploy notes", RenderText(list, 120), StringComparison.Ordinal);
        Assert.DoesNotContain("random chat", RenderText(list, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void NamedFilterExcludesUnnamedSessions()
    {
        var named = Session(@"C:\s\a.jsonl", name: "keep me");
        var unnamed = Session(@"C:\s\b.jsonl");
        var selector = CreateSelector(currentSessions: new[] { named, unnamed });
        var list = selector.GetSessionList();
        Assert.Contains("keep me", RenderText(list, 120), StringComparison.Ordinal);
        Assert.Contains("hello world", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput("\x0e"); // ctrl+n → app.session.toggleNamedFilter
        var text = RenderText(list, 120);
        Assert.Contains("keep me", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ key map

    [Fact]
    public void TabTogglesScopeAndHeaderFollows()
    {
        var selector = CreateSelector();
        var header = RenderText(selector, 120);
        Assert.Contains("Resume Session (Current Folder)", header, StringComparison.Ordinal);
        Assert.Contains("◉ Current Folder", header, StringComparison.Ordinal);

        selector.GetSessionList().HandleInput("\t");
        var header2 = RenderText(selector, 120);
        Assert.Contains("Resume Session (All)", header2, StringComparison.Ordinal);
        Assert.Contains("◉ All", header2, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlSCyclesSortModes()
    {
        var selector = CreateSelector(currentSessions: new[] { Session(@"C:\s\a.jsonl") });
        var list = selector.GetSessionList();

        Assert.Contains("Threaded", RenderText(selector, 120), StringComparison.Ordinal);
        list.HandleInput("\x13"); // ctrl+s
        Assert.Contains("Recent", RenderText(selector, 120), StringComparison.Ordinal);
        list.HandleInput("\x13");
        Assert.Contains("Fuzzy", RenderText(selector, 120), StringComparison.Ordinal);
        list.HandleInput("\x13");
        Assert.Contains("Threaded", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlPTogglesPathDisplayInRows()
    {
        var selector = CreateSelector(currentSessions: new[] { Session(@"C:\s\a.jsonl") });
        var list = selector.GetSessionList();
        Assert.DoesNotContain(@"C:\s\a.jsonl", RenderText(list, 160), StringComparison.Ordinal);

        list.HandleInput("\x10"); // ctrl+p → app.session.togglePath
        Assert.Contains(@"C:\s\a.jsonl", RenderText(list, 160), StringComparison.Ordinal);
        Assert.Contains("path (on)", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void EnterSelectsAndEscapeCancels()
    {
        var selected = new List<string>();
        var cancelled = 0;
        var selector = new SessionSelectorComponent(
            (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>(
                new[] { Session(@"C:\s\a.jsonl") }),
            (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>([]),
            onSelect: path => selected.Add(path),
            onCancel: () => cancelled++,
            onExit: () => { },
            requestRender: () => { });
        var list = selector.GetSessionList();

        list.HandleInput("\r");
        Assert.Equal([@"C:\s\a.jsonl"], selected);
        list.HandleInput("\x1b");
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void PageDownMovesByMaxVisible()
    {
        var sessions = Enumerable.Range(0, 25)
            .Select(i => Session($@"C:\s\s{i:D2}.jsonl", name: $"session {i:D2}"))
            .ToList();
        var selector = CreateSelector(currentSessions: sessions);
        var list = selector.GetSessionList();

        list.HandleInput("\x1b[6~"); // pageDown jumps 10 rows
        Assert.Equal(@"C:\s\s10.jsonl", list.GetSelectedSessionPath());
    }

    // ------------------------------------------------------------- delete flow

    [Fact]
    public void DeleteConfirmationDeletesViaUnlinkFallbackAndReportsStatus()
    {
        var sessionPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"pi-session-selector-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(sessionPath, "{}");
        var remaining = new[] { Session(sessionPath) };
        var selector = CreateSelector(currentSessions: remaining, allSessions: remaining);
        var list = selector.GetSessionList();

        list.HandleInput("\x04"); // ctrl+d → app.session.delete
        Assert.Contains("Delete session?", RenderText(selector, 120), StringComparison.Ordinal);

        list.HandleInput("\r"); // confirm
        // Deletion runs on the thread pool; wait for the file to disappear.
        for (var i = 0; i < 200 && File.Exists(sessionPath); i++)
        {
            Thread.Sleep(25);
        }
        Assert.False(File.Exists(sessionPath));
        Assert.True(SpinWait.SpinUntil(
            () => RenderText(selector, 120).Contains("Session deleted"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DeleteConfirmationRejectsTheCurrentSession()
    {
        var selector = CreateSelector(
            currentSessions: new[] { Session(@"C:\s\a.jsonl") },
            currentSessionFilePath: @"C:\s\a.jsonl");
        var list = selector.GetSessionList();

        list.HandleInput("\x04"); // ctrl+d
        Assert.Contains(
            "Cannot delete the currently active session",
            RenderText(selector, 120), StringComparison.Ordinal);
        Assert.DoesNotContain("Delete session?", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteConfirmationEscapeCancelsAndInterceptsOtherKeys()
    {
        var selector = CreateSelector(currentSessions: new[] { Session(@"C:\s\a.jsonl") });
        var list = selector.GetSessionList();

        list.HandleInput("\x04"); // ctrl+d → confirmation
        list.HandleInput("x"); // ignored while confirming
        Assert.Contains("Delete session?", RenderText(selector, 120), StringComparison.Ordinal);
        list.HandleInput("\x1b"); // cancel
        Assert.DoesNotContain("Delete session?", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteNoninvasiveForwardsToSearchWhenQueryIsNotEmpty()
    {
        var selector = CreateSelector(currentSessions: new[] { Session(@"C:\s\a.jsonl", name: "alpha") });
        var list = selector.GetSessionList();

        list.HandleInput("a"); // query non-empty
        list.HandleInput("\x08"); // ctrl+backspace → forwarded to the search input
        Assert.DoesNotContain("Delete session?", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.Contains("alpha", RenderText(list, 120), StringComparison.Ordinal); // list still intact
    }

    // --------------------------------------------------------------- rename flow

    [Fact]
    public async Task RenameFlowCallsTheCallbackAndReturnsToTheList()
    {
        var renamed = new List<(string Path, string? Name)>();
        var selector = CreateSelector(
            currentSessions: new[] { Session(@"C:\s\a.jsonl") },
            options: new SessionSelectorComponent.Options
            {
                RenameSession = (path, name) =>
                {
                    renamed.Add((path, name));
                    return Task.CompletedTask;
                },
            });
        var list = selector.GetSessionList();

        list.HandleInput("\x12"); // ctrl+r → app.session.rename
        Assert.Contains("Rename Session", RenderText(selector, 120), StringComparison.Ordinal);

        var inputField = FindRenameInput(selector);
        inputField.SetValue("my new name");
        inputField.HandleInput("\r");

        await Task.Delay(100); // let confirmRename finish
        Assert.Equal([(@"C:\s\a.jsonl", "my new name")], renamed);
        Assert.Contains("Resume Session (Current Folder)", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void RenameFlowEscapeReturnsToTheListWithoutCallingTheCallback()
    {
        var called = 0;
        var selector = CreateSelector(
            currentSessions: new[] { Session(@"C:\s\a.jsonl") },
            options: new SessionSelectorComponent.Options
            {
                RenameSession = (_, _) => { called++; return Task.CompletedTask; },
            });
        var list = selector.GetSessionList();

        list.HandleInput("\x12"); // ctrl+r
        selector.HandleInput("\x1b"); // esc in rename mode
        Assert.Equal(0, called);
        Assert.Contains("Resume Session (Current Folder)", RenderText(selector, 120), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- async loading

    [Fact]
    public void HeaderShowsLoadingProgressAndFinalList()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<SessionInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = new SessionSelectorComponent(
            (onProgress, _) =>
            {
                onProgress?.Invoke(1, 2, new[] { Session(@"C:\s\a.jsonl") });
                return gate.Task;
            },
            (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>([]),
            _ => { }, () => { }, () => { }, () => { });

        // While loading: header shows progress; the partial session list is already visible.
        Assert.Contains("Loading 1/2", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.Contains("hello world", RenderText(selector.GetSessionList(), 120), StringComparison.Ordinal);

        gate.SetResult(new[] { Session(@"C:\s\a.jsonl"), Session(@"C:\s\b.jsonl") });
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var header = RenderText(selector, 120);
            return header.Contains("Resume Session (Current Folder)") && !header.Contains("Loading");
        }, TimeSpan.FromSeconds(5)));
        Assert.Contains("3 1m", RenderText(selector.GetSessionList(), 120), StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderFailureShowsAnErrorStatus()
    {
        var fail = new TaskCompletionSource<IReadOnlyList<SessionInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = new SessionSelectorComponent(
            (_, _) => fail.Task,
            (_, _) => Task.FromResult<IReadOnlyList<SessionInfo>>([]),
            _ => { }, () => { }, () => { }, () => { });

        fail.SetException(new InvalidOperationException("disk exploded"));
        Assert.True(SpinWait.SpinUntil(
            () => RenderText(selector, 120).Contains("Failed to load sessions: disk exploded"),
            TimeSpan.FromSeconds(5)));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The rename input lives inside the rename panel; reach it through the container tree.</summary>
    private static Input FindRenameInput(SessionSelectorComponent selector)
    {
        var input = FindInputIn(selector);
        Assert.NotNull(input);
        return input!;
    }

    private static Input? FindInputIn(IComponent component)
    {
        if (component is Input input) return input;
        if (component is Container container)
        {
            foreach (var child in container.Children)
            {
                var found = FindInputIn(child);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
