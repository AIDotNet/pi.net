// ============================================================================
// Component that renders a session tree selector for navigation.
// Port of modes/interactive/components/tree-selector.ts
// ============================================================================

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using SessionTreeNode = Pi.CodingAgent.Core.SessionTreeNode;
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Modes.Interactive.Components;

// Design differences (porting ledger):
// - Not numbered: FilterMode stays a string union per C242; AbortController-style timers →
//   System.Threading.Timer; `entry.message.role === ...` becomes type switching over the C#
//   ChatMessage hierarchy; the assistant stop-reason check uses the enum
//   (`StopReason is not Stop and not ToolUse`). ContextEditEntry.Replacement cannot distinguish
//   JSON null (omit) from a missing field in C#, so null always maps to "omit" — the TS writer
//   emits `replacement: null` for omit and the field is required in the TS interface, so the
//   main-line semantics are preserved.

internal static partial class TreeSelectorStatics
{
    public const int TreeGutterWidth = 2;
    public const int MinVisibleAnchorContentWidth = 4;
    public const int MaxVisibleAnchorContentWidth = 20;
    public const int MinAnchorContextWidth = 2;
    public const int MaxAnchorContextWidth = 12;

    /// <summary>JS <c>/\s+/</c> for splitting search tokens.</summary>
    [GeneratedRegex(JsRegex.WhitespaceClass + "+")]
    public static partial Regex WhitespaceRunRegex();
}

/// <summary>Gutter info: position (displayIndent where connector was) and whether to show │.</summary>
internal sealed record GutterInfo(int Position, bool Show);

/// <summary>Flattened tree node for navigation.</summary>
internal sealed class FlatNode
{
    public required SessionTreeNode Node { get; set; }

    /// <summary>Indentation level (each level = 3 chars).</summary>
    public int Indent { get; set; }

    /// <summary>Whether to show connector (├─ or └─) - true if parent has multiple children.</summary>
    public bool ShowConnector { get; set; }

    /// <summary>If showConnector, true = last sibling (└─), false = not last (├─).</summary>
    public bool IsLast { get; set; }

    /// <summary>Gutter info for each ancestor branch point.</summary>
    public IReadOnlyList<GutterInfo> Gutters { get; set; } = [];

    /// <summary>True if this node is a root under a virtual branching root (multiple roots).</summary>
    public bool IsVirtualRootChild { get; set; }
}

internal sealed class HorizontalViewportRow
{
    public required string Gutter { get; init; }
    public required string Body { get; init; }
    public required int AnchorCol { get; init; }
    public required int BodyWidth { get; init; }
    public required bool IsSelected { get; init; }
}

/// <summary>Tool call info for lookup.</summary>
internal sealed record ToolCallInfo(string Name, JsonObject? Arguments);

/// <summary>
/// Render tree rows into a horizontally clipped viewport.
///
/// The tree gutter is always kept visible. The row bodies are shifted left only
/// when the selected row's anchor (the start of its entry text after tree
/// indentation/markers) would otherwise be too far right to see useful content.
/// </summary>
internal static class HorizontalViewport
{
    public static List<string> RenderHorizontalViewport(IReadOnlyList<HorizontalViewportRow> rows, int width)
    {
        var viewportWidth = Math.Max(0, width - TreeSelectorStatics.TreeGutterWidth);
        var maxBodyWidth = rows.Count == 0 ? 0 : rows.Max(row => row.BodyWidth);
        var maxHorizontalScroll = Math.Max(0, maxBodyWidth - viewportWidth);
        var selectedRow = rows.FirstOrDefault(row => row.IsSelected);

        // Only pan horizontally when needed to keep enough selected-row content visible after its anchor.
        var horizontalScroll = 0;
        if (selectedRow is not null && maxHorizontalScroll > 0)
        {
            var minVisibleAnchorContentWidth = Math.Min(
                TreeSelectorStatics.MaxVisibleAnchorContentWidth,
                Math.Max(TreeSelectorStatics.MinVisibleAnchorContentWidth, viewportWidth / 3));
            if (selectedRow.AnchorCol > viewportWidth - minVisibleAnchorContentWidth)
            {
                var anchorContextWidth = Math.Min(
                    TreeSelectorStatics.MaxAnchorContextWidth,
                    Math.Max(TreeSelectorStatics.MinAnchorContextWidth, viewportWidth / 4));
                horizontalScroll = Math.Min(maxHorizontalScroll, selectedRow.AnchorCol - anchorContextWidth);
            }
        }

        // Clip only the body; the fixed-width gutter remains visible as navigation context.
        return rows.Select(row =>
        {
            var line = horizontalScroll > 0
                ? $"{row.Gutter}{TextLayout.SliceByColumn(row.Body, horizontalScroll, viewportWidth, true)}\x1b[0m"
                : row.Gutter + row.Body;
            return TextLayout.TruncateToWidth(line, width, "");
        }).ToList();
    }
}

/// <summary>Filter mode for tree display.</summary>
public static class TreeFilterModes
{
    public const string Default = "default";
    public const string NoTools = "no-tools";
    public const string UserOnly = "user-only";
    public const string LabeledOnly = "labeled-only";
    public const string All = "all";
}

/// <summary>Tree list component with selection and ASCII art visualization.</summary>
internal sealed class TreeList : IComponent
{
    private List<FlatNode> _flatNodes = [];
    private List<FlatNode> _filteredNodes = [];
    private int _selectedIndex;
    private string? _currentLeafId;
    private readonly int _maxVisibleLines;
    private string _filterMode;
    private string _searchQuery = "";
    private readonly Dictionary<string, ToolCallInfo> _toolCallMap = new(StringComparer.Ordinal);
    private bool _multipleRoots;
    private bool _showLabelTimestamps;
    private HashSet<string> _activePathIds = new(StringComparer.Ordinal);
    private Dictionary<string, string?> _visibleParentMap = new(StringComparer.Ordinal);

    /// <summary>
    /// Visible children by parent id. TS keys the map by <c>string | null</c> where null means
    /// "root level"; the C# dictionary maps that to the empty-string key (entry ids are never empty).
    /// </summary>
    private Dictionary<string, List<string>> _visibleChildrenMap = new(StringComparer.Ordinal);
    private string? _lastSelectedId;
    private HashSet<string> _foldedNodes = new(StringComparer.Ordinal);

    public Action<string>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action<string?>? OnCopy { get; set; }
    public Action<string, string?>? OnLabelEdit { get; set; }

    public TreeList(
        IReadOnlyList<SessionTreeNode> tree,
        string? currentLeafId,
        int maxVisibleLines,
        string? initialSelectedId = null,
        string? initialFilterMode = null)
    {
        _currentLeafId = currentLeafId;
        _maxVisibleLines = maxVisibleLines;
        _filterMode = initialFilterMode ?? TreeFilterModes.Default;
        _multipleRoots = tree.Count > 1;
        _flatNodes = FlattenTree(tree);
        BuildActivePath();
        ApplyFilter();

        // Start with initialSelectedId if provided, otherwise current leaf
        var targetId = initialSelectedId ?? currentLeafId;
        _selectedIndex = FindNearestVisibleIndex(targetId);
        _lastSelectedId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id;
    }

    /// <summary>
    /// Find the index of the nearest visible entry, walking up the parent chain if needed.
    /// Returns the index in filteredNodes, or the last index as fallback.
    /// </summary>
    private int FindNearestVisibleIndex(string? entryId)
    {
        if (_filteredNodes.Count == 0) return 0;

        // Build a map for parent lookup
        var entryMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in _flatNodes)
        {
            if (flatNode.Node.Entry.Id is { } id) entryMap[id] = flatNode;
        }

        // Build a map of visible entry IDs to their indices in filteredNodes
        var visibleIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _filteredNodes.Count; i++)
        {
            if (_filteredNodes[i].Node.Entry.Id is { } id) visibleIdToIndex[id] = i;
        }

        // Walk from entryId up to root, looking for a visible entry
        var currentId = entryId;
        while (currentId is not null)
        {
            if (visibleIdToIndex.TryGetValue(currentId, out var index)) return index;
            if (!entryMap.TryGetValue(currentId, out var node)) break;
            currentId = node.Node.Entry.ParentId;
        }

        // Fallback: last visible entry
        return _filteredNodes.Count - 1;
    }

    /// <summary>Build the set of entry IDs on the path from root to current leaf.</summary>
    private void BuildActivePath()
    {
        _activePathIds.Clear();
        if (_currentLeafId is null) return;

        // Build a map of id -> entry for parent lookup
        var entryMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in _flatNodes)
        {
            if (flatNode.Node.Entry.Id is { } id) entryMap[id] = flatNode;
        }

        // Walk from leaf to root
        var currentId = (string?)_currentLeafId;
        while (currentId is not null)
        {
            _activePathIds.Add(currentId);
            if (!entryMap.TryGetValue(currentId, out var node)) break;
            currentId = node.Node.Entry.ParentId;
        }
    }

    private List<FlatNode> FlattenTree(IReadOnlyList<SessionTreeNode> roots)
    {
        var result = new List<FlatNode>();
        _toolCallMap.Clear();

        // Indentation rules:
        // - At indent 0: stay at 0 unless parent has >1 children (then +1)
        // - At indent 1: children always go to indent 2 (visual grouping of subtree)
        // - At indent 2+: stay flat for single-child chains, +1 only if parent branches

        // Determine which subtrees contain the active leaf (to sort current branch first).
        // Iterative post-order traversal to avoid stack overflow.
        var containsActive = new Dictionary<SessionTreeNode, bool>();
        var leafId = _currentLeafId;
        {
            // Build list in pre-order, then process in reverse for post-order effect
            var allNodes = new List<SessionTreeNode>();
            var preOrderStack = new Stack<SessionTreeNode>();
            foreach (var root in roots) preOrderStack.Push(root);
            while (preOrderStack.Count > 0)
            {
                var node = preOrderStack.Pop();
                allNodes.Add(node);
                // Push children in reverse so they're processed left-to-right
                for (var i = node.Children.Count - 1; i >= 0; i--)
                {
                    preOrderStack.Push(node.Children[i]);
                }
            }
            // Process in reverse (post-order): children before parents
            for (var i = allNodes.Count - 1; i >= 0; i--)
            {
                var node = allNodes[i];
                var has = leafId is not null && node.Entry.Id == leafId;
                foreach (var child in node.Children)
                {
                    if (containsActive.TryGetValue(child, out var childHas) && childHas)
                    {
                        has = true;
                    }
                }
                containsActive[node] = has;
            }
        }

        // Add roots in reverse order, prioritizing the one containing the active leaf.
        // If multiple roots, treat them as children of a virtual root that branches.
        var multipleRoots = roots.Count > 1;
        // TS Array.prototype.sort is stable; OrderByDescending keeps the tie order.
        var orderedRoots = roots.OrderByDescending(root =>
            containsActive.TryGetValue(root, out var has) && has ? 1 : 0).ToList();
        var stack = new Stack<(SessionTreeNode Node, int Indent, bool JustBranched, bool ShowConnector,
            bool IsLast, IReadOnlyList<GutterInfo> Gutters, bool IsVirtualRootChild)>();
        for (var i = orderedRoots.Count - 1; i >= 0; i--)
        {
            var isLast = i == orderedRoots.Count - 1;
            stack.Push((orderedRoots[i], multipleRoots ? 1 : 0, multipleRoots, multipleRoots, isLast, [],
                multipleRoots));
        }

        while (stack.Count > 0)
        {
            var (node, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();

            // Extract tool calls from assistant messages for later lookup
            var entry = node.Entry;
            if (entry is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                foreach (var block in assistant.Content)
                {
                    if (block is ToolCallContent toolCall)
                    {
                        _toolCallMap[toolCall.Id] =
                            new ToolCallInfo(toolCall.Name, toolCall.Arguments as JsonObject);
                    }
                }
            }

            result.Add(new FlatNode
            {
                Node = node,
                Indent = indent,
                ShowConnector = showConnector,
                IsLast = isLast,
                Gutters = gutters,
                IsVirtualRootChild = isVirtualRootChild,
            });

            var children = node.Children;
            var multipleChildren = children.Count > 1;

            // Order children so the branch containing the active leaf comes first
            var prioritized = new List<SessionTreeNode>();
            var rest = new List<SessionTreeNode>();
            foreach (var child in children)
            {
                if (containsActive.TryGetValue(child, out var childHas) && childHas)
                {
                    prioritized.Add(child);
                }
                else
                {
                    rest.Add(child);
                }
            }
            var orderedChildren = prioritized.Concat(rest).ToList();

            // Calculate child indent
            int childIndent;
            if (multipleChildren)
            {
                // Parent branches: children get +1
                childIndent = indent + 1;
            }
            else if (justBranched && indent > 0)
            {
                // First generation after a branch: +1 for visual grouping
                childIndent = indent + 1;
            }
            else
            {
                // Single-child chain: stay flat
                childIndent = indent;
            }

            // Build gutters for children.
            // If this node showed a connector, add a gutter entry for descendants.
            // Only add gutter if connector is actually displayed (not suppressed for virtual root children).
            var connectorDisplayed = showConnector && !isVirtualRootChild;
            var currentDisplayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            var childGutters = connectorDisplayed
                ? gutters.Append(new GutterInfo(connectorPosition, !isLast)).ToList()
                : gutters;

            // Add children in reverse order
            for (var i = orderedChildren.Count - 1; i >= 0; i--)
            {
                var childIsLast = i == orderedChildren.Count - 1;
                stack.Push((orderedChildren[i], childIndent, multipleChildren, multipleChildren, childIsLast,
                    childGutters, false));
            }
        }

        return result;
    }

    private void ApplyFilter()
    {
        // Update lastSelectedId only when we have a valid selection (non-empty list).
        // This preserves the selection when switching through empty filter results.
        if (_filteredNodes.Count > 0)
        {
            _lastSelectedId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id ?? _lastSelectedId;
        }

        var searchTokens = TreeSelectorStatics.WhitespaceRunRegex()
            .Replace(JsString.ToLowerCase(_searchQuery), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _filteredNodes = _flatNodes.Where(flatNode =>
        {
            var entry = flatNode.Node.Entry;
            if (entry is UsageEntry) return false;
            var isCurrentLeaf = entry.Id == _currentLeafId;

            // Skip assistant messages with only tool calls (no text) unless error/aborted.
            // Always show current leaf so active position is visible.
            if (entry is SessionMessageEntry { Message: AssistantMessage assistant } && !isCurrentLeaf)
            {
                var hasText = HasTextContent(assistant.Content);
                var isErrorOrAborted = assistant.StopReason is not (StopReason.Stop or StopReason.ToolUse);
                // Only hide if no text AND not an error/aborted message
                if (!hasText && !isErrorOrAborted)
                {
                    return false;
                }
            }

            // Apply filter mode
            var passesFilter = true;
            // Entry types hidden in default view (settings/bookkeeping)
            var isSettingsEntry =
                entry is LabelEntry or ContextEditEntry or CustomEntry or ModelChangeEntry
                    or ThinkingLevelChangeEntry or SessionInfoEntry;

            switch (_filterMode)
            {
                case TreeFilterModes.UserOnly:
                    // Just user messages
                    passesFilter = entry is SessionMessageEntry { Message: UserMessage };
                    break;
                case TreeFilterModes.NoTools:
                    // Default minus tool results
                    passesFilter = !isSettingsEntry &&
                                   entry is not SessionMessageEntry { Message: ToolResultMessage };
                    break;
                case TreeFilterModes.LabeledOnly:
                    // Just labeled entries
                    passesFilter = flatNode.Node.Label is not null;
                    break;
                case TreeFilterModes.All:
                    // Show everything
                    passesFilter = true;
                    break;
                default:
                    // Default mode: hide settings/bookkeeping entries
                    passesFilter = !isSettingsEntry;
                    break;
            }

            if (!passesFilter) return false;

            // Apply search filter
            if (searchTokens.Length > 0)
            {
                var nodeText = JsString.ToLowerCase(GetSearchableText(flatNode.Node));
                return searchTokens.All(token => nodeText.Contains(token, StringComparison.Ordinal));
            }

            return true;
        }).ToList();

        // Filter out descendants of folded nodes.
        if (_foldedNodes.Count > 0)
        {
            var skipSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var flatNode in _flatNodes)
            {
                var id = flatNode.Node.Entry.Id;
                var parentId = flatNode.Node.Entry.ParentId;
                if (id is null) continue;
                if (parentId is not null && (_foldedNodes.Contains(parentId) || skipSet.Contains(parentId)))
                {
                    skipSet.Add(id);
                }
            }
            _filteredNodes = _filteredNodes.Where(flatNode =>
                flatNode.Node.Entry.Id is not { } id || !skipSet.Contains(id)).ToList();
        }

        // Recalculate visual structure (indent, connectors, gutters) based on visible tree
        RecalculateVisualStructure();

        // Try to preserve cursor on the same node, or find nearest visible ancestor
        if (_lastSelectedId is not null)
        {
            _selectedIndex = FindNearestVisibleIndex(_lastSelectedId);
        }
        else if (_selectedIndex >= _filteredNodes.Count)
        {
            // Clamp index if out of bounds
            _selectedIndex = Math.Max(0, _filteredNodes.Count - 1);
        }

        // Update lastSelectedId to the actual selection (may have changed due to parent walk)
        if (_filteredNodes.Count > 0)
        {
            _lastSelectedId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id ?? _lastSelectedId;
        }
    }

    /// <summary>
    /// Recompute indentation/connectors for the filtered view.
    ///
    /// Filtering can hide intermediate entries; descendants attach to the nearest visible ancestor.
    /// Keep indentation semantics aligned with flattenTree() so single-child chains don't drift right.
    /// </summary>
    private void RecalculateVisualStructure()
    {
        if (_filteredNodes.Count == 0) return;

        var visibleIds = new HashSet<string>(
            _filteredNodes.Select(n => n.Node.Entry.Id).Where(id => id is not null).Select(id => id!), StringComparer.Ordinal);

        // Build entry map for efficient parent lookup (using full tree)
        var entryMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in _flatNodes)
        {
            if (flatNode.Node.Entry.Id is { } id) entryMap[id] = flatNode;
        }

        string? FindVisibleAncestor(string nodeId)
        {
            var currentId = entryMap.TryGetValue(nodeId, out var node) ? node.Node.Entry.ParentId : null;
            while (currentId is not null)
            {
                if (visibleIds.Contains(currentId))
                {
                    return currentId;
                }
                currentId = entryMap.TryGetValue(currentId, out var ancestor) ? ancestor.Node.Entry.ParentId : null;
            }
            return null;
        }

        // Build visible tree structure:
        // - visibleParent: nodeId → nearest visible ancestor (or null for roots)
        // - visibleChildren: parentId → list of visible children (in filteredNodes order);
        //   the root bucket lives under the empty-string key (TS uses a null key).
        var visibleParent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var visibleChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        visibleChildren[""] = []; // root-level nodes

        foreach (var flatNode in _filteredNodes)
        {
            var nodeId = flatNode.Node.Entry.Id;
            if (nodeId is null) continue;
            var ancestorId = FindVisibleAncestor(nodeId);
            visibleParent[nodeId] = ancestorId;
            var bucketKey = ancestorId ?? "";

            if (!visibleChildren.TryGetValue(bucketKey, out var childrenList))
            {
                childrenList = [];
                visibleChildren[bucketKey] = childrenList;
            }
            childrenList.Add(nodeId);
        }

        // Update multipleRoots based on visible roots
        var visibleRootIds = visibleChildren[""];
        _multipleRoots = visibleRootIds.Count > 1;

        // Build a map for quick lookup: nodeId → FlatNode
        var filteredNodeMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in _filteredNodes)
        {
            if (flatNode.Node.Entry.Id is { } id) filteredNodeMap[id] = flatNode;
        }

        // DFS over the visible tree using flattenTree() indentation semantics
        var stack = new Stack<(string NodeId, int Indent, bool JustBranched, bool ShowConnector,
            bool IsLast, IReadOnlyList<GutterInfo> Gutters, bool IsVirtualRootChild)>();

        // Add visible roots in reverse order (to process in forward order via stack)
        for (var i = visibleRootIds.Count - 1; i >= 0; i--)
        {
            var isLast = i == visibleRootIds.Count - 1;
            stack.Push((visibleRootIds[i], _multipleRoots ? 1 : 0, _multipleRoots, _multipleRoots, isLast, [],
                _multipleRoots));
        }

        while (stack.Count > 0)
        {
            var (nodeId, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();

            if (!filteredNodeMap.TryGetValue(nodeId, out var flatNode)) continue;

            // Update this node's visual properties
            flatNode.Indent = indent;
            flatNode.ShowConnector = showConnector;
            flatNode.IsLast = isLast;
            flatNode.Gutters = gutters;
            flatNode.IsVirtualRootChild = isVirtualRootChild;

            // Get visible children of this node
            var children = visibleChildren.GetValueOrDefault(nodeId) ?? [];
            var multipleChildren = children.Count > 1;

            // Child indent follows flattenTree(): branch points (and first generation after a branch) shift +1
            int childIndent;
            if (multipleChildren)
            {
                childIndent = indent + 1;
            }
            else if (justBranched && indent > 0)
            {
                childIndent = indent + 1;
            }
            else
            {
                childIndent = indent;
            }

            // Child gutters follow flattenTree() connector/gutter rules
            var connectorDisplayed = showConnector && !isVirtualRootChild;
            var currentDisplayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            var childGutters = connectorDisplayed
                ? gutters.Append(new GutterInfo(connectorPosition, !isLast)).ToList()
                : gutters;

            // Add children in reverse order (to process in forward order via stack)
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var childIsLast = i == children.Count - 1;
                stack.Push((children[i], childIndent, multipleChildren, multipleChildren, childIsLast,
                    childGutters, false));
            }
        }

        // Store visible tree maps for ancestor/descendant lookups in navigation
        _visibleParentMap = visibleParent;
        _visibleChildrenMap = visibleChildren;
    }

    /// <summary>Get searchable text content from a node.</summary>
    private string GetSearchableText(SessionTreeNode node)
    {
        var entry = node.Entry;
        var parts = new List<string>();

        if (node.Label is { } label)
        {
            parts.Add(label);
        }

        switch (entry)
        {
            case SessionMessageEntry messageEntry:
            {
                var msg = messageEntry.Message;
                switch (msg)
                {
                    case UserMessage user:
                        parts.Add("user");
                        parts.Add(ExtractContent(user.Content));
                        break;
                    case AssistantMessage assistant:
                        parts.Add("assistant");
                        parts.Add(ExtractContent(assistant.Content));
                        break;
                    case ToolResultMessage toolResult:
                        parts.Add("toolResult");
                        break;
                    case BashExecutionMessage bash:
                        parts.Add("bashExecution");
                        parts.Add(bash.Command);
                        break;
                    default:
                        parts.Add(RoleName(msg));
                        break;
                }
                break;
            }
            case CustomMessageEntry customMessage:
                parts.Add(customMessage.CustomType);
                parts.Add(ExtractContent(customMessage.Content));
                break;
            case CompactionEntry:
                parts.Add("compaction");
                break;
            case BranchSummaryEntry branchSummary:
                parts.Add("branch summary");
                parts.Add(branchSummary.Summary);
                break;
            case SessionInfoEntry sessionInfo:
                parts.Add("title");
                if (sessionInfo.Name is { } name) parts.Add(name);
                break;
            case ModelChangeEntry modelChange:
                parts.Add("model");
                parts.Add(modelChange.ModelId);
                break;
            case ThinkingLevelChangeEntry thinkingChange:
                parts.Add("thinking");
                parts.Add(thinkingChange.ThinkingLevel);
                break;
            case CustomEntry custom:
                parts.Add("custom");
                parts.Add(custom.CustomType);
                break;
            case ContextEditEntry contextEdit:
                parts.Add("context edit");
                parts.Add(contextEdit.Replacement is null ? "omit" : "replace");
                parts.Add(contextEdit.TargetId);
                break;
            case LabelEntry labelEntry:
                parts.Add("label");
                parts.Add(labelEntry.Label ?? "");
                break;
        }

        return string.Join(" ", parts);
    }

    /// <summary>TS <c>msg.role</c> as a display/search token for custom roles.</summary>
    private static string RoleName(ChatMessage msg) => msg switch
    {
        SystemMessage => "system",
        _ => msg.GetType().Name,
    };

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var lines = new List<string>();

        if (_filteredNodes.Count == 0)
        {
            lines.Add(TextLayout.TruncateToWidth(theme.Fg("muted", "  No entries found"), width));
            lines.Add(TextLayout.TruncateToWidth(theme.Fg("muted", $"  (0/0){GetStatusLabels()}"), width));
            return [.. lines];
        }

        var startIndex = Math.Max(
            0,
            Math.Min(
                _selectedIndex - (_maxVisibleLines / 2),
                _filteredNodes.Count - _maxVisibleLines));
        var endIndex = Math.Min(startIndex + _maxVisibleLines, _filteredNodes.Count);

        var renderedRows = new List<HorizontalViewportRow>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var flatNode = _filteredNodes[i];
            var entry = flatNode.Node.Entry;
            var isSelected = i == _selectedIndex;

            // Build line: cursor + prefix + path marker + label + content
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";

            // If multiple roots, shift display (roots at 0, not 1)
            var displayIndent = _multipleRoots ? Math.Max(0, flatNode.Indent - 1) : flatNode.Indent;

            // Build prefix with gutters at their correct positions.
            // Each gutter has a position (displayIndent where its connector was shown).
            var connector =
                flatNode.ShowConnector && !flatNode.IsVirtualRootChild ? (flatNode.IsLast ? "└─ " : "├─ ") : "";
            var connectorPosition = connector.Length > 0 ? displayIndent - 1 : -1;

            // Build prefix char by char, placing gutters and connector at their positions
            var totalChars = displayIndent * 3;
            var prefixChars = new List<string>();
            var isFolded = _foldedNodes.Contains(entry.Id!);
            for (var c = 0; c < totalChars; c++)
            {
                var level = c / 3;
                var posInLevel = c % 3;

                // Check if there's a gutter at this level
                var gutter = flatNode.Gutters.FirstOrDefault(g => g.Position == level);
                if (gutter is not null)
                {
                    if (posInLevel == 0)
                    {
                        prefixChars.Add(gutter.Show ? "│" : " ");
                    }
                    else
                    {
                        prefixChars.Add(" ");
                    }
                }
                else if (connector.Length > 0 && level == connectorPosition)
                {
                    // Connector at this level, with fold indicator
                    if (posInLevel == 0)
                    {
                        prefixChars.Add(flatNode.IsLast ? "└" : "├");
                    }
                    else if (posInLevel == 1)
                    {
                        var foldable = IsFoldable(entry.Id!);
                        prefixChars.Add(isFolded ? "⊞" : foldable ? "⊟" : "─");
                    }
                    else
                    {
                        prefixChars.Add(" ");
                    }
                }
                else
                {
                    prefixChars.Add(" ");
                }
            }
            var prefix = string.Join("", prefixChars);

            // Fold marker for nodes without connectors (roots)
            var showsFoldInConnector = flatNode.ShowConnector && !flatNode.IsVirtualRootChild;
            var foldMarker = isFolded && !showsFoldInConnector ? theme.Fg("accent", "⊞ ") : "";

            // Active path marker - shown right before the entry text
            var isOnActivePath = entry.Id is not null && _activePathIds.Contains(entry.Id);
            var pathMarker = isOnActivePath ? theme.Fg("accent", "• ") : "";

            var labelText = flatNode.Node.Label is not null ? theme.Fg("warning", $"[{flatNode.Node.Label}] ") : "";
            var labelTimestamp =
                _showLabelTimestamps && flatNode.Node.Label is not null && flatNode.Node.LabelTimestamp is not null
                    ? theme.Fg("muted", $"{FormatLabelTimestamp(flatNode.Node.LabelTimestamp)} ")
                    : "";
            var content = GetEntryDisplayText(flatNode.Node, isSelected);
            var prefixPart = theme.Fg("dim", prefix) + foldMarker + pathMarker;
            var anchorCol = UnicodeWidth.VisibleWidth(prefixPart);
            var rowGutter = cursor;
            var body = prefixPart + labelText + labelTimestamp + content;
            if (isSelected)
            {
                rowGutter = theme.Bg("selectedBg", rowGutter);
                body = theme.Bg("selectedBg", body);
            }
            renderedRows.Add(new HorizontalViewportRow
            {
                Gutter = rowGutter,
                Body = body,
                AnchorCol = anchorCol,
                BodyWidth = UnicodeWidth.VisibleWidth(body),
                IsSelected = isSelected,
            });
        }

        lines.AddRange(HorizontalViewport.RenderHorizontalViewport(renderedRows, width));
        lines.Add(TextLayout.TruncateToWidth(
            theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredNodes.Count}){GetStatusLabels()}"),
            width));

        return [.. lines];
    }

    private string GetEntryDisplayText(SessionTreeNode node, bool isSelected)
    {
        var theme = ThemeRuntime.Current;
        var entry = node.Entry;
        string result;

        static string Normalize(string s) =>
            JsString.Trim(Regex.Replace(s, "[\n\t]", " "));

        switch (entry)
        {
            case SessionMessageEntry messageEntry:
            {
                var msg = messageEntry.Message;
                switch (msg)
                {
                    case UserMessage user:
                    {
                        var content = Normalize(ExtractContent(user.Content));
                        result = theme.Fg("accent", "user: ") + content;
                        break;
                    }
                    case AssistantMessage assistant:
                    {
                        var textContent = Normalize(ExtractContent(assistant.Content));
                        if (textContent.Length > 0)
                        {
                            result = theme.Fg("success", "assistant: ") + textContent;
                        }
                        else if (assistant.StopReason == StopReason.Aborted)
                        {
                            result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(aborted)");
                        }
                        else if (assistant.ErrorMessage is { } errorMessage)
                        {
                            var errMsg = Normalize(errorMessage);
                            if (errMsg.Length > 80) errMsg = errMsg[..80];
                            result = theme.Fg("success", "assistant: ") + theme.Fg("error", errMsg);
                        }
                        else
                        {
                            result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(no content)");
                        }
                        break;
                    }
                    case ToolResultMessage toolResult:
                    {
                        var toolCall = toolResult.ToolCallId is { } callId && _toolCallMap.TryGetValue(callId, out var info)
                            ? info
                            : null;
                        if (toolCall is not null)
                        {
                            result = theme.Fg("muted", FormatToolCall(toolCall.Name, toolCall.Arguments));
                        }
                        else
                        {
                            result = theme.Fg("muted", $"[{toolResult.ToolName ?? "tool"}]");
                        }
                        break;
                    }
                    case BashExecutionMessage bash:
                        result = theme.Fg("dim", $"[bash]: {Normalize(bash.Command)}");
                        break;
                    default:
                        result = theme.Fg("dim", $"[{RoleName(msg)}]");
                        break;
                }
                break;
            }
            case CustomMessageEntry customMessage:
            {
                var content = ExtractContent(customMessage.Content);
                result = theme.Fg("customMessageLabel", $"[{customMessage.CustomType}]: ") + Normalize(content);
                break;
            }
            case CompactionEntry compaction:
            {
                var tokens = (long)Math.Round(compaction.TokensBefore / 1000.0);
                result = theme.Fg("borderAccent", $"[compaction: {tokens}k tokens]");
                break;
            }
            case BranchSummaryEntry branchSummary:
                result = theme.Fg("warning", "[branch summary]: ") + Normalize(branchSummary.Summary);
                break;
            case ModelChangeEntry modelChange:
                result = theme.Fg("dim", $"[model: {modelChange.ModelId}]");
                break;
            case ThinkingLevelChangeEntry thinkingChange:
                result = theme.Fg("dim", $"[thinking: {thinkingChange.ThinkingLevel}]");
                break;
            case CustomEntry custom:
                result = theme.Fg("dim", $"[custom: {custom.CustomType}]");
                break;
            case ContextEditEntry contextEdit:
            {
                var omitOrReplace = contextEdit.Replacement is null ? "omit" : "replace";
                result = theme.Fg("dim", $"[context {omitOrReplace}: {contextEdit.TargetId}]");
                break;
            }
            case LabelEntry labelEntry:
                result = theme.Fg("dim", $"[label: {labelEntry.Label ?? "(cleared)"}]");
                break;
            case SessionInfoEntry sessionInfo:
                result = sessionInfo.Name is { } name
                    ? theme.Fg("dim", "[title: ") + theme.Fg("dim", name) + theme.Fg("dim", "]")
                    : theme.Fg("dim", "[title: ") + theme.Italic(theme.Fg("dim", "empty")) + theme.Fg("dim", "]");
                break;
            default:
                result = "";
                break;
        }

        return isSelected ? theme.Bold(result) : result;
    }

    private static string FormatLabelTimestamp(string timestamp)
    {
        var date = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal).ToLocalTime();
        var now = DateTimeOffset.Now;
        var hours = date.Hour.ToString().PadLeft(2, '0');
        var minutes = date.Minute.ToString().PadLeft(2, '0');
        var time = $"{hours}:{minutes}";

        if (date.Year == now.Year && date.Month == now.Month && date.Day == now.Day)
        {
            return time;
        }

        var month = date.Month;
        var day = date.Day;
        if (date.Year == now.Year)
        {
            return $"{month}/{day} {time}";
        }

        var year = date.Year.ToString()[^2..];
        return $"{year}/{month}/{day} {time}";
    }

    private string ExtractContent(MessageContent? content)
    {
        var full = ExtractFullContent(content);
        return full[..Math.Min(200, full.Length)];
    }

    private string ExtractContent(IReadOnlyList<ContentBlock>? content)
    {
        var full = ExtractFullContent(content);
        return full[..Math.Min(200, full.Length)];
    }

    private string ExtractFullContent(MessageContent? content)
    {
        if (content is null) return "";
        return content switch
        {
            MessageContent.Text text => text.Value,
            MessageContent.Blocks blocks => ExtractFullContent(blocks.Value),
            _ => "",
        };
    }

    private static string ExtractFullContent(IReadOnlyList<ContentBlock>? content)
    {
        if (content is null) return "";

        var result = "";
        foreach (var block in content)
        {
            if (block is TextContent text)
            {
                result += text.Text;
            }
        }
        return result;
    }

    private string? GetEntryCopyText(SessionTreeNode node)
    {
        var entry = node.Entry;
        string? text = null;

        switch (entry)
        {
            case SessionMessageEntry messageEntry:
            {
                var msg = messageEntry.Message;
                switch (msg)
                {
                    case BashExecutionMessage bash:
                        text = bash.Command;
                        break;
                    case UserMessage user:
                        text = ExtractFullContent(user.Content);
                        break;
                    case AssistantMessage assistant:
                        text = ExtractFullContent(assistant.Content);
                        if (text.Length == 0 && assistant.ErrorMessage is { } errorMessage)
                        {
                            text = errorMessage;
                        }
                        break;
                    case ToolResultMessage toolResult:
                        text = ExtractFullContent(toolResult.Content);
                        break;
                }
                break;
            }
            case CustomMessageEntry customMessage:
                text = ExtractFullContent(customMessage.Content);
                break;
            case CompactionEntry compaction:
                text = compaction.Summary;
                break;
            case BranchSummaryEntry branchSummary:
                text = branchSummary.Summary;
                break;
        }

        return JsString.Trim(text ?? "").Length > 0 ? text : null;
    }

    private static bool HasTextContent(MessageContent? content)
    {
        if (content is null) return false;
        return content switch
        {
            MessageContent.Text text => JsString.Trim(text.Value).Length > 0,
            MessageContent.Blocks blocks => HasTextContent(blocks.Value),
            _ => false,
        };
    }

    private static bool HasTextContent(IReadOnlyList<ContentBlock>? content)
    {
        if (content is null) return false;
        foreach (var block in content)
        {
            if (block is TextContent { Text: { } text } && JsString.Trim(text).Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatToolCall(string name, JsonObject? args)
    {
        static string ShortenPath(string p)
        {
            var home =
                Environment.GetEnvironmentVariable("HOME") ??
                Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            if (home.Length > 0 && p.StartsWith(home, StringComparison.Ordinal)) return $"~{p[home.Length..]}";
            return p;
        }

        static string Arg(JsonObject? args, string key)
        {
            if (args is null) return "";
            return args[key] is JsonValue { } value && value.TryGetValue<string>(out var s) ? s : "";
        }

        switch (name)
        {
            case "read":
            {
                var path = ShortenPath(Arg(args, "path") is { } p1 && p1.Length > 0 ? p1 : Arg(args, "file_path"));
                var offset = args?["offset"]?.GetValue<int>();
                var limit = args?["limit"]?.GetValue<int>();
                var display = path;
                if (offset.HasValue || limit.HasValue)
                {
                    var start = offset ?? 1;
                    var end = limit.HasValue ? $"{start + limit.Value - 1}" : "";
                    display += $":{start}{(end.Length > 0 ? $"-{end}" : "")}";
                }
                return $"[read: {display}]";
            }
            case "write":
            {
                var path = ShortenPath(Arg(args, "path") is { } p2 && p2.Length > 0 ? p2 : Arg(args, "file_path"));
                return $"[write: {path}]";
            }
            case "edit":
            {
                var path = ShortenPath(Arg(args, "path") is { } p3 && p3.Length > 0 ? p3 : Arg(args, "file_path"));
                return $"[edit: {path}]";
            }
            case "bash":
            {
                var rawCmd = Arg(args, "command");
                var cmd = JsString.Trim(Regex.Replace(rawCmd, "[\n\t]", " "));
                if (cmd.Length > 50) cmd = cmd[..50];
                return $"[bash: {cmd}{(rawCmd.Length > 50 ? "..." : "")}]";
            }
            case "grep":
            {
                var pattern = Arg(args, "pattern");
                var path = ShortenPath(Arg(args, "path") is { } p4 && p4.Length > 0 ? p4 : ".");
                return $"[grep: /{pattern}/ in {path}]";
            }
            case "find":
            {
                var pattern = Arg(args, "pattern");
                var path = ShortenPath(Arg(args, "path") is { } p5 && p5.Length > 0 ? p5 : ".");
                return $"[find: {pattern} in {path}]";
            }
            case "ls":
            {
                var path = ShortenPath(Arg(args, "path") is { } p6 && p6.Length > 0 ? p6 : ".");
                return $"[ls: {path}]";
            }
            default:
            {
                // Custom tool - show name and truncated JSON args
                var argsJson = args?.ToJsonString() ?? "{}";
                var argsStr = argsJson.Length > 40 ? argsJson[..40] : argsJson;
                return $"[{name}: {argsStr}{(argsJson.Length > 40 ? "..." : "")}]";
            }
        }
    }

    public string GetSearchQuery() => _searchQuery;

    public SessionTreeNode? GetSelectedNode() => _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node;

    public void CopySelected()
    {
        var node = GetSelectedNode();
        OnCopy?.Invoke(node is not null ? GetEntryCopyText(node) : null);
    }

    public void UpdateNodeLabel(string entryId, string? label, string? labelTimestamp = null)
    {
        foreach (var flatNode in _flatNodes)
        {
            if (flatNode.Node.Entry.Id == entryId)
            {
                var updated = flatNode.Node with
                {
                    Label = label,
                    LabelTimestamp = label is not null
                        ? labelTimestamp ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                        : null,
                };
                flatNode.Node = updated;
                break;
            }
        }
    }

    private string GetStatusLabels()
    {
        var labels = "";
        switch (_filterMode)
        {
            case TreeFilterModes.NoTools:
                labels += " [no-tools]";
                break;
            case TreeFilterModes.UserOnly:
                labels += " [user]";
                break;
            case TreeFilterModes.LabeledOnly:
                labels += " [labeled]";
                break;
            case TreeFilterModes.All:
                labels += " [all]";
                break;
        }
        if (_showLabelTimestamps)
        {
            labels += " [+label time]";
        }
        return labels;
    }

    public void HandleInput(string keyData)
    {
        var kb = GlobalKeybindings.Get();
        if (kb.Matches(keyData, TuiKeybindingIds.SelectUp))
        {
            _selectedIndex = _selectedIndex == 0 ? _filteredNodes.Count - 1 : _selectedIndex - 1;
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectDown))
        {
            _selectedIndex = _selectedIndex == _filteredNodes.Count - 1 ? 0 : _selectedIndex + 1;
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFoldOrUp))
        {
            var currentId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id;
            if (currentId is not null && IsFoldable(currentId) && !_foldedNodes.Contains(currentId))
            {
                _foldedNodes.Add(currentId);
                ApplyFilter();
            }
            else
            {
                _selectedIndex = FindBranchSegmentStart("up");
            }
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeUnfoldOrDown))
        {
            var currentId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id;
            if (currentId is not null && _foldedNodes.Contains(currentId))
            {
                _foldedNodes.Remove(currentId);
                ApplyFilter();
            }
            else
            {
                _selectedIndex = FindBranchSegmentStart("down");
            }
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.EditorCursorLeft) ||
                 kb.Matches(keyData, TuiKeybindingIds.SelectPageUp))
        {
            // Page up
            _selectedIndex = Math.Max(0, _selectedIndex - _maxVisibleLines);
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.EditorCursorRight) ||
                 kb.Matches(keyData, TuiKeybindingIds.SelectPageDown))
        {
            // Page down
            _selectedIndex = Math.Min(_filteredNodes.Count - 1, _selectedIndex + _maxVisibleLines);
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectConfirm))
        {
            var selected = _filteredNodes.ElementAtOrDefault(_selectedIndex);
            if (selected is not null)
            {
                OnSelect?.Invoke(selected.Node.Entry.Id!);
            }
        }
        else if (kb.Matches(keyData, AppKeybindings.AppMessageCopy))
        {
            CopySelected();
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectCancel))
        {
            if (_searchQuery.Length > 0)
            {
                _searchQuery = "";
                _foldedNodes.Clear();
                ApplyFilter();
            }
            else
            {
                OnCancel?.Invoke();
            }
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterDefault))
        {
            // Direct filter: default
            _filterMode = TreeFilterModes.Default;
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterNoTools))
        {
            // Toggle filter: no-tools ↔ default
            _filterMode = _filterMode == TreeFilterModes.NoTools ? TreeFilterModes.Default : TreeFilterModes.NoTools;
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterUserOnly))
        {
            // Toggle filter: user-only ↔ default
            _filterMode = _filterMode == TreeFilterModes.UserOnly ? TreeFilterModes.Default : TreeFilterModes.UserOnly;
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterLabeledOnly))
        {
            // Toggle filter: labeled-only ↔ default
            _filterMode = _filterMode == TreeFilterModes.LabeledOnly
                ? TreeFilterModes.Default
                : TreeFilterModes.LabeledOnly;
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterAll))
        {
            // Toggle filter: all ↔ default
            _filterMode = _filterMode == TreeFilterModes.All ? TreeFilterModes.Default : TreeFilterModes.All;
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterCycleBackward))
        {
            // Cycle filter backwards
            var modes = new[]
            {
                TreeFilterModes.Default, TreeFilterModes.NoTools, TreeFilterModes.UserOnly,
                TreeFilterModes.LabeledOnly, TreeFilterModes.All,
            };
            var currentIndex = Array.IndexOf(modes, _filterMode);
            _filterMode = modes[(currentIndex - 1 + modes.Length) % modes.Length];
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeFilterCycleForward))
        {
            // Cycle filter forwards: default → no-tools → user-only → labeled-only → all → default
            var modes = new[]
            {
                TreeFilterModes.Default, TreeFilterModes.NoTools, TreeFilterModes.UserOnly,
                TreeFilterModes.LabeledOnly, TreeFilterModes.All,
            };
            var currentIndex = Array.IndexOf(modes, _filterMode);
            _filterMode = modes[(currentIndex + 1) % modes.Length];
            _foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.EditorDeleteCharBackward))
        {
            if (_searchQuery.Length > 0)
            {
                _searchQuery = _searchQuery[..^1];
                _foldedNodes.Clear();
                ApplyFilter();
            }
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeEditLabel))
        {
            var selected = _filteredNodes.ElementAtOrDefault(_selectedIndex);
            if (selected is not null)
            {
                OnLabelEdit?.Invoke(selected.Node.Entry.Id!, selected.Node.Label);
            }
        }
        else if (kb.Matches(keyData, AppKeybindings.AppTreeToggleLabelTimestamp))
        {
            _showLabelTimestamps = !_showLabelTimestamps;
        }
        else
        {
            var hasControlChars = keyData.Any(ch =>
            {
                var code = (int)ch;
                return code < 32 || code == 0x7f || (code >= 0x80 && code <= 0x9f);
            });
            if (!hasControlChars && keyData.Length > 0)
            {
                _searchQuery += keyData;
                _foldedNodes.Clear();
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// Whether a node can be folded. A node is foldable if it has visible children
    /// and is either a root (no visible parent) or a segment start (visible parent
    /// has multiple visible children).
    /// </summary>
    private bool IsFoldable(string entryId)
    {
        var children = _visibleChildrenMap.GetValueOrDefault(entryId);
        if (children is null || children.Count == 0) return false;
        var parentId = _visibleParentMap.GetValueOrDefault(entryId);
        if (parentId is null) return true;
        var siblings = _visibleChildrenMap.GetValueOrDefault(parentId);
        return siblings is not null && siblings.Count > 1;
    }

    /// <summary>
    /// Find the index of the next branch segment start in the given direction.
    /// A segment start is the first child of a branch point.
    ///
    /// "up" walks the visible parent chain; "down" walks visible children
    /// (always following the first child).
    /// </summary>
    private int FindBranchSegmentStart(string direction)
    {
        var selectedId = _filteredNodes.ElementAtOrDefault(_selectedIndex)?.Node.Entry.Id;
        if (selectedId is null) return _selectedIndex;

        var indexByEntryId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _filteredNodes.Count; i++)
        {
            if (_filteredNodes[i].Node.Entry.Id is { } id) indexByEntryId[id] = i;
        }
        var currentId = selectedId;
        if (direction == "down")
        {
            while (true)
            {
                var children = _visibleChildrenMap.GetValueOrDefault(currentId) ?? [];
                if (children.Count == 0) return indexByEntryId[currentId];
                if (children.Count > 1) return indexByEntryId[children[0]];
                currentId = children[0];
            }
        }

        // direction === "up"
        while (true)
        {
            var parentId = _visibleParentMap.GetValueOrDefault(currentId);
            if (parentId is null) return indexByEntryId[currentId];
            var children = _visibleChildrenMap.GetValueOrDefault(parentId) ?? [];
            if (children.Count > 1)
            {
                var segmentStart = indexByEntryId[currentId];
                if (segmentStart < _selectedIndex)
                {
                    return segmentStart;
                }
            }
            currentId = parentId;
        }
    }
}

/// <summary>Component that displays the current search query.</summary>
internal sealed class SearchLine : IComponent
{
    private readonly TreeList _treeList;

    public SearchLine(TreeList treeList)
    {
        _treeList = treeList;
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var query = _treeList.GetSearchQuery();
        if (query.Length > 0)
        {
            return [TextLayout.TruncateToWidth($"  {theme.Fg("muted", "Type to search:")} {theme.Fg("accent", query)}", width)];
        }
        return [TextLayout.TruncateToWidth($"  {theme.Fg("muted", "Type to search:")}", width)];
    }
}

/// <summary>Component that renders tree help as semantic rows with chunk-aware wrapping.</summary>
internal sealed class TreeHelp : IComponent
{
    private static readonly (string[] Keys, string Label, bool LabelFirst)[] HelpItems =
    [
        (["tui.select.up", "tui.select.down"], "move", false),
        (["tui.editor.cursorLeft", "tui.editor.cursorRight"], "page", false),
        (["app.tree.foldOrUp", "app.tree.unfoldOrDown"], "branch", false),
        (["app.message.copy"], "copy", false),
        (["app.tree.editLabel"], "label", false),
        (["app.tree.toggleLabelTimestamp"], "label time", false),
        ([
            "app.tree.filter.default", "app.tree.filter.noTools", "app.tree.filter.userOnly",
            "app.tree.filter.labeledOnly", "app.tree.filter.all",
        ], "filters", true),
        (["app.tree.filter.cycleForward", "app.tree.filter.cycleBackward"], "cycle", true),
    ];

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var items = HelpItems.Select(item =>
        {
            var text = TreeHelpFormatting.FormatHelpKeys(item.Keys);
            if (text.Length == 0) return item.Label;
            return item.LabelFirst ? $"{item.Label} {text}" : $"{text} {item.Label}";
        });

        var availableWidth = Math.Max(1, width);
        const string indent = "  ";
        const string separator = " · ";
        var lines = new List<string>();
        var currentLine = "";

        foreach (var item in items)
        {
            var candidate = currentLine.Length > 0
                ? $"{currentLine}{separator}{item}"
                : UnicodeWidth.VisibleWidth($"{indent}{item}") <= availableWidth ? $"{indent}{item}" : item;
            if (currentLine.Length == 0 || UnicodeWidth.VisibleWidth(candidate) <= availableWidth)
            {
                currentLine = candidate;
                continue;
            }

            lines.AddRange(TextLayout.WrapTextWithAnsi(currentLine.TrimEnd(), availableWidth));
            currentLine = UnicodeWidth.VisibleWidth($"{indent}{item}") <= availableWidth ? $"{indent}{item}" : item;
        }

        if (currentLine.Length > 0)
        {
            lines.AddRange(TextLayout.WrapTextWithAnsi(currentLine.TrimEnd(), availableWidth));
        }

        return lines.Select(line => theme.Fg("muted", line)).ToArray();
    }
}

internal static class TreeHelpFormatting
{
    internal static string FormatHelpKeys(IReadOnlyList<string> keybindings)
    {
        var keys = new List<string>();
        var kb = GlobalKeybindings.Get();
        foreach (var keybinding in keybindings)
        {
            var allKeys = kb.GetKeys(keybinding);
            if (allKeys.Length > 0) keys.Add(allKeys[0]);
        }
        if (keys.Count == 0) return "";

        // TS 用 \b 词边界正则（\bpageUp\b 等），裸 Replace 会误伤 "pgup" → "pg↑"。
        return KeybindingHints.FormatKeyText(CompactRawKeys(keys))
            .ReplaceRegexWholeWord("pageUp", "pgup")
            .ReplaceRegexWholeWord("pageDown", "pgdn")
            .ReplaceRegexWholeWord("up", "↑")
            .ReplaceRegexWholeWord("down", "↓")
            .ReplaceRegexWholeWord("left", "←")
            .ReplaceRegexWholeWord("right", "→");
    }

    private static string CompactRawKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count == 1) return keys[0];

        var parts = keys.Select(key =>
        {
            var separatorIndex = key.LastIndexOf('+');
            return separatorIndex == -1
                ? (Prefix: "", Suffix: key)
                : (Prefix: key[..(separatorIndex + 1)], Suffix: key[(separatorIndex + 1)..]);
        }).ToList();
        var prefix = parts[0].Prefix;
        return prefix.Length > 0 && parts.All(part => part.Prefix == prefix)
            ? $"{prefix}{string.Join("/", parts.Select(part => part.Suffix))}"
            : string.Join("/", keys);
    }

    /// <summary>对应 TS 的 <c>replace(/\b…\b/g, …)</c>：只在完整词边界处替换。</summary>
    private static string ReplaceRegexWholeWord(this string input, string word, string replacement) =>
        Regex.Replace(input, $@"\b{Regex.Escape(word)}\b", replacement);
}

/// <summary>Label input component shown when editing a label.</summary>
internal sealed class LabelInput : IComponent, IFocusable
{
    private readonly Input _input = new();
    private readonly string _entryId;

    public Action<string, string?>? OnSubmit { get; set; }
    public Action? OnCancel { get; set; }

    // Focusable implementation - propagate to input for IME cursor positioning
    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    public LabelInput(string entryId, string? currentLabel)
    {
        _entryId = entryId;
        if (currentLabel is not null)
        {
            _input.SetValue(currentLabel);
        }
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var lines = new List<string>();
        const string indent = "  ";
        var availableWidth = width - indent.Length;
        lines.Add(TextLayout.TruncateToWidth($"{indent}{theme.Fg("muted", "Label (empty to remove):")}", width));
        lines.AddRange(_input.Render(availableWidth).Select(line =>
            TextLayout.TruncateToWidth($"{indent}{line}", width)));
        lines.Add(TextLayout.TruncateToWidth(
            $"{indent}{KeybindingHints.KeyHint("tui.select.confirm", "save")}  {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}",
            width));
        return [.. lines];
    }

    public void HandleInput(string keyData)
    {
        var kb = GlobalKeybindings.Get();
        if (kb.Matches(keyData, TuiKeybindingIds.SelectConfirm))
        {
            var value = JsString.Trim(_input.GetValue());
            OnSubmit?.Invoke(_entryId, value.Length > 0 ? value : null);
        }
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectCancel))
        {
            OnCancel?.Invoke();
        }
        else
        {
            _input.HandleInput(keyData);
        }
    }
}

/// <summary>
/// Component that renders a session tree selector for navigation (TS <c>TreeSelectorComponent</c>).
/// </summary>
public sealed class TreeSelectorComponent : Container, IFocusable
{
    private readonly TreeList _treeList;
    private LabelInput? _labelInput;
    private readonly Container _labelInputContainer;
    private readonly Container _treeContainer;
    private readonly Action<string, string?>? _onLabelChangeCallback;
    private Timer? _emptyTreeCancelTimer;

    public Action<string?>? OnCopy { get; set; }

    // Focusable implementation - propagate to labelInput when active for IME cursor positioning
    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            // Propagate to labelInput when it's active
            if (_labelInput is not null)
            {
                _labelInput.Focused = value;
            }
        }
    }

    public TreeSelectorComponent(
        IReadOnlyList<SessionTreeNode> tree,
        string? currentLeafId,
        int terminalHeight,
        Action<string> onSelect,
        Action onCancel,
        Action<string, string?>? onLabelChange = null,
        string? initialSelectedId = null,
        string? initialFilterMode = null)
    {
        _onLabelChangeCallback = onLabelChange;
        var maxVisibleLines = Math.Max(5, terminalHeight / 2);

        _treeList = new TreeList(tree, currentLeafId, maxVisibleLines, initialSelectedId, initialFilterMode);
        _treeList.OnSelect = onSelect;
        _treeList.OnCancel = onCancel;
        _treeList.OnCopy = text => OnCopy?.Invoke(text);
        _treeList.OnLabelEdit = ShowLabelInput;

        _treeContainer = new Container();
        _treeContainer.AddChild(_treeList);

        _labelInputContainer = new Container();

        var theme = ThemeRuntime.Current;
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new TuiText(theme.Bold("  Session Tree"), 1, 0));
        AddChild(new TreeHelp());
        AddChild(new SearchLine(_treeList));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(_treeContainer);
        AddChild(_labelInputContainer);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        if (tree.Count == 0)
        {
            // TS: setTimeout(() => onCancel(), 100). The timer reference is kept so it is not
            // collected before firing.
            _emptyTreeCancelTimer = new Timer(_ => onCancel(), null, 100, Timeout.Infinite);
        }
    }

    private void ShowLabelInput(string entryId, string? currentLabel)
    {
        _labelInput = new LabelInput(entryId, currentLabel);
        _labelInput.OnSubmit = (id, label) =>
        {
            _treeList.UpdateNodeLabel(id, label);
            _onLabelChangeCallback?.Invoke(id, label);
            HideLabelInput();
        };
        _labelInput.OnCancel = HideLabelInput;

        // Propagate current focused state to the new labelInput
        _labelInput.Focused = _focused;

        _treeContainer.Clear();
        _labelInputContainer.Clear();
        _labelInputContainer.AddChild(_labelInput);
    }

    private void HideLabelInput()
    {
        _labelInput = null;
        _labelInputContainer.Clear();
        _treeContainer.Clear();
        _treeContainer.AddChild(_treeList);
    }

    public override void HandleInput(string keyData)
    {
        if (_labelInput is not null)
        {
            _labelInput.HandleInput(keyData);
        }
        else
        {
            _treeList.HandleInput(keyData);
        }
    }

    internal TreeList GetTreeList() => _treeList;
}
