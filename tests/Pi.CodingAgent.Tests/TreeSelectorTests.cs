// ============================================================================
// Tests for modes/interactive/components/tree-selector.ts
//
// Vectors transcribed from TS test/tree-selector.test.ts (14 upstream cases,
// some covering several assertions). Upstream is the only component in the
// selector group with a dedicated test file.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// `KeybindingsManager` exists in both Pi.Tui (the TUI table) and Pi.CodingAgent.Core (the app
// table). The app one is the type the TS tests construct, so it gets the bare name via an alias.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

public sealed class TreeSelectorTests : SelectorTestFixture
{
    // Key escape sequences (same names as the upstream suite).
    private const string Up = "\x1b[A";
    private const string Down = "\x1b[B";
    private const string CtrlLeft = "\x1b[1;5D";
    private const string CtrlRight = "\x1b[1;5C";
    private const string AltLeft = "\x1b[1;3D";
    private const string AltRight = "\x1b[1;3C";

    // -------------------------------------------------------------- fixtures

    private static string IsoNow => DateTimeOffset.Now.ToString("o");

    private static SessionMessageEntry UserMessage(string id, string? parentId, string content) =>
        new()
        {
            Id = id,
            ParentId = parentId,
            Timestamp = IsoNow,
            Message = new UserMessage(content, DateTimeOffset.Now.ToUnixTimeMilliseconds()),
        };

    private static SessionMessageEntry AssistantMessage(string id, string? parentId, string text) =>
        new()
        {
            Id = id,
            ParentId = parentId,
            Timestamp = IsoNow,
            Message = new AssistantMessage([new TextContent(text)], StopReason.Stop),
        };

    // Filtered out in default mode (assistant message with only a tool call).
    private static SessionMessageEntry ToolCallOnlyAssistant(string id, string? parentId) =>
        new()
        {
            Id = id,
            ParentId = parentId,
            Timestamp = IsoNow,
            Message = new AssistantMessage(
                [new ToolCallContent($"tc-{id}", "read", new Dictionary<string, object> { ["path"] = "test.ts" })],
                StopReason.ToolUse),
        };

    private static ModelChangeEntry ModelChange(string id, string? parentId) =>
        new() { Id = id, ParentId = parentId, Timestamp = IsoNow, Provider = "anthropic", ModelId = "claude-sonnet-4" };

    private static ContextEditEntry ContextEdit(string id, string? parentId, string targetId) =>
        new() { Id = id, ParentId = parentId, Timestamp = IsoNow, TargetId = targetId, Replacement = null };

    private static ThinkingLevelChangeEntry ThinkingChange(string id, string? parentId) =>
        new() { Id = id, ParentId = parentId, Timestamp = IsoNow, ThinkingLevel = "high" };

    /// <summary>Mirror of the TS tests' <c>buildTree</c>: attach by <c>parentId</c>, orphans dropped.</summary>
    private static List<SessionTreeNode> BuildTree(params SessionEntry[] entries)
    {
        var childLists = entries.Select(_ => new List<SessionTreeNode>()).ToList();
        var nodes = entries.Select((e, i) => new SessionTreeNode { Entry = e, Children = childLists[i] }).ToList();
        var byId = new Dictionary<string, SessionTreeNode>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (entries[i].Id is { } id) byId[id] = nodes[i];
        }

        var roots = new List<SessionTreeNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Entry.ParentId is null) roots.Add(node);
            else if (node.Entry.ParentId is { } pid && byId.TryGetValue(pid, out var parent))
                childLists[nodes.IndexOf(parent)].Add(node);
        }

        return roots;
    }

    private static TreeSelectorComponent NewSelector(
        List<SessionTreeNode> tree,
        string? currentLeafId,
        string? initialFilterMode = null) =>
        new(tree, currentLeafId, 24, _ => { }, () => { }, initialFilterMode: initialFilterMode);

    private static string? SelectedId(TreeSelectorComponent selector) =>
        selector.GetTreeList().GetSelectedNode()?.Entry.Id;

    private static string StrippedRender(IComponent component, int width) =>
        string.Join("\n", component.Render(width).Select(AnsiText.StripAnsi));

    /// <summary>The branching tree from the upstream folding describe block.</summary>
    private static List<SessionTreeNode> BuildBranchingTree() => BuildTree(
        UserMessage("user-1", null, "first message"),
        AssistantMessage("asst-1", "user-1", "response 1"),
        UserMessage("user-2", "asst-1", "second message"),
        AssistantMessage("asst-2", "user-2", "response 2"),
        // Branch A (active)
        UserMessage("user-3a", "asst-2", "branch A start"),
        AssistantMessage("asst-3a", "user-3a", "branch A response"),
        UserMessage("user-4a", "asst-3a", "branch A deep"),
        AssistantMessage("asst-4a", "user-4a", "branch A leaf"),
        // Branch B
        UserMessage("user-3b", "asst-2", "branch B start"),
        AssistantMessage("asst-3b", "user-3b", "branch B response"),
        UserMessage("user-4b", "asst-3b", "branch B deep"));

    // -------------------------------------------------- initial selection with metadata entries

    [Fact]
    public void FocusesNearestVisibleAncestor_WhenCurrentLeafIsModelChangeWithSiblingBranch()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "active branch"), // Active branch
            ModelChange("model-1", "user-2"),                  // Current leaf (metadata)
            UserMessage("user-3", "asst-1", "sibling branch")); // Sibling branch

        var selector = NewSelector(tree, "model-1");

        // Should focus on user-2 (parent of model-1), not user-3 (last item).
        Assert.Equal("user-2", SelectedId(selector));
    }

    [Fact]
    public void HidesContextEditsByDefault_AndLabelsThemInAllMode()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            ContextEdit("edit-1", "asst-1", "asst-1"));

        var defaultSelector = NewSelector(tree, "edit-1");
        Assert.Equal("asst-1", SelectedId(defaultSelector));

        var allSelector = NewSelector(tree, "edit-1", initialFilterMode: "all");
        var rendered = StrippedRender(allSelector.GetTreeList(), 200);
        Assert.Contains("[context omit: asst-1]", rendered);
    }

    [Fact]
    public void FocusesNearestVisibleAncestor_WhenCurrentLeafIsThinkingLevelChange()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "active branch"),
            ThinkingChange("thinking-1", "user-2"),
            UserMessage("user-3", "asst-1", "sibling branch"));

        var selector = NewSelector(tree, "thinking-1");

        Assert.Equal("user-2", SelectedId(selector));
    }

    // -------------------------------------------------- filter switching with parent traversal

    [Fact]
    public void SwitchesToNearestVisibleUserMessage_WhenChangingToUserOnlyFilter()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "active branch"),
            AssistantMessage("asst-2", "user-2", "response"),
            UserMessage("user-3", "asst-1", "sibling branch"));

        var selector = NewSelector(tree, "asst-2");
        Assert.Equal("asst-2", SelectedId(selector));

        // Simulate Ctrl+U (user-only filter). Should land on user-2 (the parent), not user-3.
        selector.HandleInput("\x15");
        Assert.Equal("user-2", SelectedId(selector));
    }

    [Fact]
    public void ReturnsToNearestVisibleAncestor_WhenSwitchingBackToDefaultFilter()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "active branch"),
            AssistantMessage("asst-2", "user-2", "response"),
            UserMessage("user-3", "asst-1", "sibling branch"));

        var selector = NewSelector(tree, "asst-2");
        Assert.Equal("asst-2", SelectedId(selector));

        selector.HandleInput("\x15"); // Ctrl+U
        Assert.Equal("user-2", SelectedId(selector));

        // Switch back to default — stays on user-2 (navigated there via parent traversal).
        selector.HandleInput("\x04"); // Ctrl+D
        Assert.Equal("user-2", SelectedId(selector));
    }

    // -------------------------------------------------- help

    [Fact]
    public void RendersSemanticHelpRows_WithoutTruncatingNarrowTerminalControls()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"));
        var selector = NewSelector(tree, "asst-1");

        var plainLines = selector.Render(30).Select(AnsiText.StripAnsi).ToArray();
        var plain = string.Join("\n", plainLines);
        Assert.Contains("branch", plain);
        Assert.Contains("copy", plain);
        Assert.Contains("filters", plain);
        Assert.Contains("cycle", plain);
        Assert.Contains("label time", plain);
        Assert.DoesNotContain("...", plain);
        Assert.All(plainLines, line => Assert.True(UnicodeWidth.VisibleWidth(line) <= 30, $"'{line}' exceeds 30"));
    }

    // -------------------------------------------------- copy

    [Fact]
    public void CopiesTheFullSelectedMessage_WithCtrlX()
    {
        var message = $"{string.Concat(Enumerable.Repeat("long message ", 30))}\nsecond line";
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", message));
        var selector = NewSelector(tree, "asst-1");
        string? copied = null;
        selector.OnCopy = text => copied = text;

        selector.HandleInput("\x18"); // Ctrl+X

        Assert.Equal(message, copied);
    }

    // -------------------------------------------------- label timestamps

    [Fact]
    public void TogglesLabelTimestamps_ForLabeledNodes()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"));
        // TS mutates tree[0].label in place; the C# node is an immutable record.
        var stamp = new DateTimeOffset(2026, 3, 28, 14, 32, 0, DateTimeOffset.Now.Offset);
        tree[0] = tree[0] with { Label = "checkpoint", LabelTimestamp = stamp.ToString("o") };

        var selector = NewSelector(tree, "asst-1");
        var list = selector.GetTreeList();

        var render = string.Join("\n", list.Render(200).Select(AnsiText.StripAnsi));
        Assert.Contains("[checkpoint]", render);
        Assert.DoesNotContain("3/28 14:32", render);
        Assert.DoesNotContain("[+label time]", render);

        selector.HandleInput("T");

        render = string.Join("\n", list.Render(200).Select(AnsiText.StripAnsi));
        Assert.Contains("3/28 14:32", render);
        Assert.Contains("[+label time]", render);
    }

    // -------------------------------------------------- empty filter preservation

    [Fact]
    public void PreservesSelection_WhenSwitchingToEmptyLabeledFilterAndBack()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "bye"),
            AssistantMessage("asst-2", "user-2", "goodbye"));

        var selector = NewSelector(tree, "asst-2");
        Assert.Equal("asst-2", SelectedId(selector));

        // Switch to labeled-only filter (no labels exist, so empty result).
        selector.HandleInput("\x0c"); // Ctrl+L
        Assert.Null(SelectedId(selector));

        // Switch back to default — restores asst-2.
        selector.HandleInput("\x04"); // Ctrl+D
        Assert.Equal("asst-2", SelectedId(selector));
    }

    [Fact]
    public void PreservesSelection_ThroughMultipleEmptyFilterSwitches()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            AssistantMessage("asst-1", "user-1", "hi"));

        var selector = NewSelector(tree, "asst-1");
        Assert.Equal("asst-1", SelectedId(selector));

        selector.HandleInput("\x0c"); // Ctrl+L -> labeled-only (empty)
        Assert.Null(SelectedId(selector));

        selector.HandleInput("\x0c"); // Ctrl+L -> default (toggle back)
        Assert.Equal("asst-1", SelectedId(selector));

        selector.HandleInput("\x0c"); // Ctrl+L -> labeled-only again
        Assert.Null(SelectedId(selector));

        selector.HandleInput("\x04"); // Ctrl+D
        Assert.Equal("asst-1", SelectedId(selector));
    }

    // -------------------------------------------------- branch navigation and folding

    [Fact]
    public void CtrlRightUnfoldsFoldedNode_ThenDoesSegmentJumpWhenUnfolded()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");
        var list = selector.GetTreeList();

        selector.HandleInput(CtrlLeft); // asst-4a → user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(Down); // user-3a → user-3b (children hidden)
        Assert.Equal("user-3b", SelectedId(selector));

        selector.HandleInput(Up); // user-3b → user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(CtrlRight); // unfold user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(Down); // user-3a → asst-3a (children restored)
        Assert.Equal("asst-3a", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // asst-3a → user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(CtrlRight); // user-3a → asst-4a (segment jump to leaf)
        Assert.Equal("asst-4a", SelectedId(selector));
    }

    [Fact]
    public void AltLeftRight_AreAliasesForFoldAndUnfoldNavigation()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");

        selector.HandleInput(AltLeft); // asst-4a → user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(AltLeft); // fold user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(AltRight); // unfold user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(AltRight); // user-3a → asst-4a
        Assert.Equal("asst-4a", SelectedId(selector));
    }

    [Fact]
    public void FoldingRootHidesEntireSubtree_NestedFoldPreservedOnUnfold()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");

        selector.HandleInput(CtrlLeft); // asst-4a → user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-3a
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // user-3a (folded) → user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(Down); // wrap (only visible node)
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(CtrlRight); // unfold user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(CtrlRight); // user-1 → user-3a (segment jump, user-3a still folded)
        Assert.Equal("user-3a", SelectedId(selector));

        selector.HandleInput(Down); // user-3a → user-3b (user-3a still folded)
        Assert.Equal("user-3b", SelectedId(selector));
    }

    [Fact]
    public void FoldAndNavigate_OnNonActiveBranch()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");

        // Navigate down to user-3b (branch B).
        var found = false;
        for (var i = 0; i < 20; i++)
        {
            selector.HandleInput(Down);
            if (SelectedId(selector) == "user-3b")
            {
                found = true;
                break;
            }
        }
        Assert.True(found);

        selector.HandleInput(CtrlRight); // user-3b → user-4b (segment jump to leaf)
        Assert.Equal("user-4b", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // user-4b → user-3b
        Assert.Equal("user-3b", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-3b
        Assert.Equal("user-3b", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // user-3b (folded) → user-1
        Assert.Equal("user-1", SelectedId(selector));
    }

    [Fact]
    public void FoldAndNavigate_WithMultipleRoots()
    {
        var tree = BuildTree(
            UserMessage("user-1", null, "first root"),
            AssistantMessage("asst-1", "user-1", "response 1"),
            UserMessage("user-2", null, "second root"),
            AssistantMessage("asst-2", "user-2", "response 2"));
        var selector = NewSelector(tree, "asst-1");

        Assert.Equal("asst-1", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // asst-1 → user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(Down); // user-1 → user-2 (children hidden)
        Assert.Equal("user-2", SelectedId(selector));

        selector.HandleInput(CtrlRight); // user-2 → asst-2 (segment jump to leaf)
        Assert.Equal("asst-2", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // asst-2 → user-2
        Assert.Equal("user-2", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-2
        Assert.Equal("user-2", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // user-2 (folded, root) → stays on user-2
        Assert.Equal("user-2", SelectedId(selector));
    }

    [Fact]
    public void FoldingRootHidesDescendants_EvenWhenIntermediateNodesAreFilteredOut()
    {
        // user-1 → toolCallOnly-1 (filtered out) → user-2 → asst-2
        var tree = BuildTree(
            UserMessage("user-1", null, "hello"),
            ToolCallOnlyAssistant("tool-asst-1", "user-1"),
            UserMessage("user-2", "tool-asst-1", "follow up"),
            AssistantMessage("asst-2", "user-2", "response"));
        var selector = NewSelector(tree, "asst-2");

        selector.HandleInput(CtrlLeft); // asst-2 → user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(CtrlLeft); // fold user-1
        Assert.Equal("user-1", SelectedId(selector));

        selector.HandleInput(Down); // wrap (only visible node)
        Assert.Equal("user-1", SelectedId(selector));
    }

    [Fact]
    public void SearchResetsFoldState()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");
        var list = selector.GetTreeList();

        selector.HandleInput(CtrlLeft); // asst-4a → user-3a
        selector.HandleInput(CtrlLeft); // fold user-3a

        selector.HandleInput(Down); // user-3a → user-3b (children hidden)
        Assert.Equal("user-3b", SelectedId(selector));

        selector.HandleInput("b"); // search resets folds
        selector.HandleInput("\x1b"); // clear search

        // Navigate to user-3a to verify the fold was reset.
        var currentId = "";
        for (var i = 0; i < 20; i++)
        {
            selector.HandleInput(Down);
            currentId = SelectedId(selector) ?? "";
            if (currentId == "user-3a") break;
        }
        Assert.Equal("user-3a", currentId);

        selector.HandleInput(Down); // user-3a → asst-3a (not user-3b)
        Assert.Equal("asst-3a", SelectedId(selector));
    }

    [Fact]
    public void FilterModeChangeResetsFoldState()
    {
        var tree = BuildBranchingTree();
        var selector = NewSelector(tree, "asst-4a");

        selector.HandleInput(CtrlLeft); // asst-4a → user-3a
        selector.HandleInput(CtrlLeft); // fold user-3a

        selector.HandleInput("\x15"); // Ctrl+U: user-only filter resets folds
        selector.HandleInput("\x04"); // Ctrl+D: back to default

        // Navigate to user-3a to verify the fold was reset.
        var currentId = "";
        for (var i = 0; i < 20; i++)
        {
            selector.HandleInput(Down);
            currentId = SelectedId(selector) ?? "";
            if (currentId == "user-3a") break;
        }
        Assert.Equal("user-3a", currentId);

        selector.HandleInput(Down); // user-3a → asst-3a (not user-3b)
        Assert.Equal("asst-3a", SelectedId(selector));
    }
}
