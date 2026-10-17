// ============================================================================
// Component that renders a session selector.
// Port of modes/interactive/components/session-selector.ts
// ============================================================================

using System.Diagnostics;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Modes.Interactive.Components;

// Design differences (porting ledger):
// - Not numbered: the internal string unions (SessionScope "current"|"all", mode "list"|"rename")
//   stay strings per C242; `AbortController` → `CancellationTokenSource` per C231;
//   `setTimeout`/`clearTimeout` → `System.Threading.Timer` + Dispose; `spawnSync("trash")` → a
//   synchronous `Process` run; `Date.getTime()` → `DateTimeOffset.ToUnixTimeMilliseconds()` and the
//   `Math.floor` divisions in `formatSessionDate` stay `Math.Floor` over doubles so negative gaps
//   round the JS way; the control-character scrub regex keeps its JS class verbatim.

internal static partial class SessionSelectorStatics
{
    public static string ShortenPath(string path)
    {
        var home = Paths.GetHomeDirectory();
        if (path.Length == 0) return path;
        return path.StartsWith(home, StringComparison.Ordinal) ? $"~{path[home.Length..]}" : path;
    }

    public static string FormatSessionDate(DateTimeOffset date)
    {
        var now = DateTimeOffset.Now;
        var diffMs = (now - date).TotalMilliseconds;
        var diffMins = (long)Math.Floor(diffMs / 60000);
        var diffHours = (long)Math.Floor(diffMs / 3600000);
        var diffDays = (long)Math.Floor(diffMs / 86400000);

        if (diffMins < 1) return "now";
        if (diffMins < 60) return $"{diffMins}m";
        if (diffHours < 24) return $"{diffHours}h";
        if (diffDays < 7) return $"{diffDays}d";
        if (diffDays < 30) return $"{Math.Floor(diffDays / 7.0)}w";
        if (diffDays < 365) return $"{Math.Floor(diffDays / 30.0)}mo";
        return $"{Math.Floor(diffDays / 365.0)}y";
    }

    public static string? CanonicalizePath(string? path)
    {
        if (path is null) return null;
        return Paths.CanonicalizePath(path);
    }

    /// <summary>JS <c>/[\x00-\x1f\x7f]/g</c> — control characters become spaces.</summary>
    [GeneratedRegex("[\x00-\x1f\x7f]")]
    internal static partial Regex ControlCharacterRegex();
}

/// <summary>A session tree node for hierarchical display.</summary>
internal sealed class SessionInfoTreeNode
{
    public required SessionInfo Session { get; init; }
    public List<SessionInfoTreeNode> Children { get; } = [];
    public long LatestActivity { get; set; }
}

/// <summary>Flattened node for display with tree structure info.</summary>
internal sealed class FlatSessionNode
{
    public required SessionInfo Session { get; init; }
    public required int Depth { get; init; }
    public required bool IsLast { get; init; }

    /// <summary>For each ancestor level, whether there are more siblings after it.</summary>
    public required IReadOnlyList<bool> AncestorContinues { get; init; }
}

internal static class SessionTree
{
    /// <summary>
    /// Build a tree structure from sessions based on parentSessionPath.
    /// Returns root nodes sorted by modified date (descending).
    /// </summary>
    public static List<SessionInfoTreeNode> BuildSessionTree(IReadOnlyList<SessionInfo> sessions)
    {
        var byPath = new Dictionary<string, SessionInfoTreeNode>(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            var sessionPath = SessionSelectorStatics.CanonicalizePath(session.Path) ?? session.Path;
            byPath[sessionPath] = new SessionInfoTreeNode
            {
                Session = session,
                LatestActivity = session.Modified.ToUnixTimeMilliseconds(),
            };
        }

        var roots = new List<SessionInfoTreeNode>();

        foreach (var session in sessions)
        {
            var sessionPath = SessionSelectorStatics.CanonicalizePath(session.Path) ?? session.Path;
            var node = byPath[sessionPath];
            var parentPath = SessionSelectorStatics.CanonicalizePath(session.ParentSessionPath);

            if (parentPath is not null && byPath.TryGetValue(parentPath, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        long UpdateLatestActivity(SessionInfoTreeNode node)
        {
            var latestActivity = node.Session.Modified.ToUnixTimeMilliseconds();
            foreach (var child in node.Children)
            {
                latestActivity = Math.Max(latestActivity, UpdateLatestActivity(child));
            }
            node.LatestActivity = latestActivity;
            return latestActivity;
        }

        foreach (var root in roots)
        {
            UpdateLatestActivity(root);
        }

        // Sort children and roots by latest activity in each subtree (descending)
        void SortNodes(List<SessionInfoTreeNode> nodes)
        {
            // TS Array.prototype.sort is stable; LINQ OrderBy(Descending) keeps that property.
            var ordered = nodes.OrderByDescending(n => n.LatestActivity).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                nodes[i] = ordered[i];
            }
            foreach (var node in nodes)
            {
                SortNodes(node.Children);
            }
        }
        SortNodes(roots);

        return roots;
    }

    /// <summary>Flatten tree into display list with tree structure metadata.</summary>
    public static List<FlatSessionNode> FlattenSessionTree(List<SessionInfoTreeNode> roots)
    {
        var result = new List<FlatSessionNode>();

        void Walk(SessionInfoTreeNode node, int depth, IReadOnlyList<bool> ancestorContinues, bool isLast)
        {
            result.Add(new FlatSessionNode
            {
                Session = node.Session,
                Depth = depth,
                IsLast = isLast,
                AncestorContinues = ancestorContinues,
            });

            for (var i = 0; i < node.Children.Count; i++)
            {
                var childIsLast = i == node.Children.Count - 1;
                // Only show continuation line for non-root ancestors
                var continues = depth > 0 && !isLast;
                Walk(node.Children[i], depth + 1, [.. ancestorContinues, continues], childIsLast);
            }
        }

        for (var i = 0; i < roots.Count; i++)
        {
            Walk(roots[i], 0, [], i == roots.Count - 1);
        }

        return result;
    }
}

internal sealed class SessionSelectorHeader : IComponent
{
    private string _scope;
    private SortMode _sortMode;
    private NameFilter _nameFilter;
    private readonly Action _requestRender;
    private bool _loading;
    private (int Loaded, int Total)? _loadProgress;
    private bool _showPath;
    private string? _confirmingDeletePath;
    private (bool IsError, string Message)? _statusMessage;
    private Timer? _statusTimeout;
    private bool _showRenameHint;

    public SessionSelectorHeader(string scope, SortMode sortMode, NameFilter nameFilter, Action requestRender)
    {
        _scope = scope;
        _sortMode = sortMode;
        _nameFilter = nameFilter;
        _requestRender = requestRender;
    }

    public void SetScope(string scope) => _scope = scope;

    public void SetSortMode(SortMode sortMode) => _sortMode = sortMode;

    public void SetNameFilter(NameFilter nameFilter) => _nameFilter = nameFilter;

    public void SetLoading(bool loading)
    {
        _loading = loading;
        // Progress is scoped to the current load; clear whenever the loading state is set
        _loadProgress = null;
    }

    public void SetProgress(int loaded, int total) => _loadProgress = (loaded, total);

    public void SetShowPath(bool showPath) => _showPath = showPath;

    public void SetShowRenameHint(bool show) => _showRenameHint = show;

    public void SetConfirmingDeletePath(string? path) => _confirmingDeletePath = path;

    private void ClearStatusTimeout()
    {
        _statusTimeout?.Dispose();
        _statusTimeout = null;
    }

    public void SetStatusMessage((bool IsError, string Message)? message, int? autoHideMs = null)
    {
        ClearStatusTimeout();
        _statusMessage = message;
        if (message is null || autoHideMs is not { } hideMs || hideMs == 0) return;

        _statusTimeout = new Timer(_ =>
        {
            _statusMessage = null;
            _statusTimeout?.Dispose();
            _statusTimeout = null;
            _requestRender();
        }, null, hideMs, Timeout.Infinite);
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var title = _scope == "current" ? "Resume Session (Current Folder)" : "Resume Session (All)";
        var leftText = theme.Bold(title);

        var sortLabel = _sortMode == SortMode.Threaded ? "Threaded" : _sortMode == SortMode.Recent ? "Recent" : "Fuzzy";
        var sortText = theme.Fg("muted", "Sort: ") + theme.Fg("accent", sortLabel);

        var nameLabel = _nameFilter == NameFilter.All ? "All" : "Named";
        var nameText = theme.Fg("muted", "Name: ") + theme.Fg("accent", nameLabel);

        string scopeText;
        if (_loading)
        {
            var progressText = _loadProgress is { } progress ? $"{progress.Loaded}/{progress.Total}" : "...";
            scopeText = $"{theme.Fg("muted", "○ Current Folder | ")}{theme.Fg("accent", $"Loading {progressText}")}";
        }
        else if (_scope == "current")
        {
            scopeText = $"{theme.Fg("accent", "◉ Current Folder")}{theme.Fg("muted", " | ○ All")}";
        }
        else
        {
            scopeText = $"{theme.Fg("muted", "○ Current Folder | ")}{theme.Fg("accent", "◉ All")}";
        }

        var rightText = TextLayout.TruncateToWidth($"{scopeText}  {nameText}  {sortText}", width, "");
        var availableLeft = Math.Max(0, width - UnicodeWidth.VisibleWidth(rightText) - 1);
        var left = TextLayout.TruncateToWidth(leftText, availableLeft, "");
        var spacing = Math.Max(0, width - UnicodeWidth.VisibleWidth(left) - UnicodeWidth.VisibleWidth(rightText));

        // Build hint lines - changes based on state (all branches truncate to width)
        string hintLine1;
        string hintLine2;
        if (_confirmingDeletePath is not null)
        {
            var confirmHint =
                $"Delete session? {KeybindingHints.KeyHint("tui.select.confirm", "confirm")} · {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}";
            hintLine1 = theme.Fg("error", TextLayout.TruncateToWidth(confirmHint, width, "…"));
            hintLine2 = "";
        }
        else if (_statusMessage is { } status)
        {
            var color = status.IsError ? "error" : "accent";
            hintLine1 = theme.Fg(color, TextLayout.TruncateToWidth(status.Message, width, "…"));
            hintLine2 = "";
        }
        else
        {
            var pathState = _showPath ? "(on)" : "(off)";
            var sep = theme.Fg("muted", " · ");
            var hint1 =
                KeybindingHints.KeyHint("tui.input.tab", "scope") + sep +
                theme.Fg("muted", "re:<pattern> regex · \"phrase\" exact");
            var hint2Parts = new List<string>
            {
                KeybindingHints.KeyHint("app.session.toggleSort", "sort"),
                KeybindingHints.KeyHint("app.session.toggleNamedFilter", "named"),
                KeybindingHints.KeyHint("app.session.delete", "delete"),
                KeybindingHints.KeyHint("app.session.togglePath", $"path {pathState}"),
            };
            if (_showRenameHint)
            {
                hint2Parts.Add(KeybindingHints.KeyHint("app.session.rename", "rename"));
            }
            var hint2 = string.Join(sep, hint2Parts);
            hintLine1 = TextLayout.TruncateToWidth(hint1, width, "…");
            hintLine2 = TextLayout.TruncateToWidth(hint2, width, "…");
        }

        return [$"{left}{new string(' ', spacing)}{rightText}", hintLine1, hintLine2];
    }
}

/// <summary>Custom session list component with multi-line items and search.</summary>
internal sealed class SessionList : IComponent, IFocusable
{
    public string? GetSelectedSessionPath()
    {
        var selected = _filteredSessions.ElementAtOrDefault(_selectedIndex);
        return selected?.Session.Path;
    }

    private IReadOnlyList<SessionInfo> _allSessions = [];
    private List<FlatSessionNode> _filteredSessions = [];
    private int _selectedIndex;
    private bool _selectionTouched;
    private readonly Input _searchInput = new();
    private bool _showCwd;
    private SortMode _sortMode;
    private NameFilter _nameFilter;
    private readonly KeybindingsManager _keybindings;
    private bool _showPath;
    private string? _confirmingDeletePath;
    private readonly string? _currentSessionCanonicalPath;

    public Action<string>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action? OnExit { get; set; }
    public Action? OnToggleScope { get; set; }
    public Action? OnToggleSort { get; set; }
    public Action? OnToggleNameFilter { get; set; }
    public Action<bool>? OnTogglePath { get; set; }
    public Action<string?>? OnDeleteConfirmationChange { get; set; }
    public Func<string, Task>? OnDeleteSession { get; set; }
    public Action<string>? OnRenameSession { get; set; }
    public Action<string>? OnError { get; set; }

    private const int MaxVisible = 10; // Max sessions visible (one line each)

    // Focusable implementation - propagate to searchInput for IME cursor positioning
    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    public SessionList(
        IReadOnlyList<SessionInfo> sessions,
        bool showCwd,
        SortMode sortMode,
        NameFilter nameFilter,
        KeybindingsManager keybindings,
        string? currentSessionFilePath = null)
    {
        _allSessions = sessions;
        _showCwd = showCwd;
        _sortMode = sortMode;
        _nameFilter = nameFilter;
        _keybindings = keybindings;
        _currentSessionCanonicalPath = SessionSelectorStatics.CanonicalizePath(currentSessionFilePath);
        FilterSessions("");

        // Handle Enter in search input - select current item
        _searchInput.OnSubmit = _ =>
        {
            var selected = _filteredSessions.ElementAtOrDefault(_selectedIndex);
            if (selected is not null)
            {
                OnSelect?.Invoke(selected.Session.Path);
            }
        };
    }

    public void SetSortMode(SortMode sortMode)
    {
        _sortMode = sortMode;
        FilterSessions(_searchInput.GetValue());
    }

    public void SetNameFilter(NameFilter nameFilter)
    {
        _nameFilter = nameFilter;
        FilterSessions(_searchInput.GetValue());
    }

    public void SetSessions(IReadOnlyList<SessionInfo> sessions, bool showCwd)
    {
        var selectedPath = _selectionTouched ? GetSelectedSessionPath() : null;
        _allSessions = sessions;
        _showCwd = showCwd;
        FilterSessions(_searchInput.GetValue());
        if (!_selectionTouched)
        {
            _selectedIndex = 0;
        }
        else if (selectedPath is not null)
        {
            var selectedIndex = _filteredSessions.FindIndex(node => node.Session.Path == selectedPath);
            if (selectedIndex >= 0) _selectedIndex = selectedIndex;
        }
    }

    private void FilterSessions(string query)
    {
        var trimmed = query.Trim();
        IReadOnlyList<SessionInfo> nameFiltered;
        if (_nameFilter == NameFilter.All)
        {
            nameFiltered = _allSessions;
        }
        else
        {
            nameFiltered = [.. _allSessions.Where(session => SessionSelectorSearch.HasSessionName(session))];
        }

        if (_sortMode == SortMode.Threaded && trimmed.Length == 0)
        {
            // Threaded mode without search: show tree structure
            var roots = SessionTree.BuildSessionTree(nameFiltered);
            _filteredSessions = SessionTree.FlattenSessionTree(roots);
        }
        else
        {
            // Other modes or with search: flat list
            var filtered = SessionSelectorSearch.FilterAndSortSessions(nameFiltered, query, _sortMode, NameFilter.All);
            _filteredSessions = [.. filtered.Select(session => new FlatSessionNode
            {
                Session = session,
                Depth = 0,
                IsLast = true,
                AncestorContinues = [],
            })];
        }
        _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _filteredSessions.Count - 1));
    }

    private void SetConfirmingDeletePath(string? path)
    {
        _confirmingDeletePath = path;
        OnDeleteConfirmationChange?.Invoke(path);
    }

    private void StartDeleteConfirmationForSelectedSession()
    {
        var selected = _filteredSessions.ElementAtOrDefault(_selectedIndex);
        if (selected is null) return;

        // Prevent deleting current session
        if (IsCurrentSessionPath(selected.Session.Path))
        {
            OnError?.Invoke("Cannot delete the currently active session");
            return;
        }

        SetConfirmingDeletePath(selected.Session.Path);
    }

    private bool IsCurrentSessionPath(string path)
    {
        if (_currentSessionCanonicalPath is null) return false;
        return (SessionSelectorStatics.CanonicalizePath(path) ?? path) == _currentSessionCanonicalPath;
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var lines = new List<string>();

        // Render search input
        lines.AddRange(_searchInput.Render(width));
        lines.Add(""); // Blank line after search

        if (_filteredSessions.Count == 0)
        {
            string emptyMessage;
            if (_nameFilter == NameFilter.Named)
            {
                var toggleKey = KeybindingHints.KeyText("app.session.toggleNamedFilter");
                if (_showCwd)
                {
                    emptyMessage = $"  No named sessions found. Press {toggleKey} to show all.";
                }
                else
                {
                    emptyMessage = $"  No named sessions in current folder. Press {toggleKey} to show all, or Tab to view all.";
                }
            }
            else if (_showCwd)
            {
                // "All" scope - no sessions anywhere that match filter
                emptyMessage = "  No sessions found";
            }
            else
            {
                // "Current folder" scope - hint to try "all"
                emptyMessage = "  No sessions in current folder. Press Tab to view all.";
            }
            lines.Add(theme.Fg("muted", TextLayout.TruncateToWidth(emptyMessage, width, "…")));
            return [.. lines];
        }

        // Calculate visible range with scrolling
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (MaxVisible / 2), _filteredSessions.Count - MaxVisible));
        var endIndex = Math.Min(startIndex + MaxVisible, _filteredSessions.Count);

        // Render visible sessions (one line each with tree structure)
        for (var i = startIndex; i < endIndex; i++)
        {
            var node = _filteredSessions[i];
            var session = node.Session;
            var isSelected = i == _selectedIndex;
            var isConfirmingDelete = session.Path == _confirmingDeletePath;
            var isCurrent = IsCurrentSessionPath(session.Path);

            // Build tree prefix
            var prefix = BuildTreePrefix(node);

            // Session display text (name or first message)
            var hasName = !string.IsNullOrEmpty(session.Name);
            var displayText = session.Name ?? session.FirstMessage;
            var normalizedMessage =
                JsString.Trim(SessionSelectorStatics.ControlCharacterRegex().Replace(displayText, " "));

            // Right side: message count and age
            var age = SessionSelectorStatics.FormatSessionDate(session.Modified);
            var msgCount = session.MessageCount.ToString();
            var rightPart = $"{msgCount} {age}";
            if (_showCwd && session.Cwd.Length > 0)
            {
                rightPart = $"{SessionSelectorStatics.ShortenPath(session.Cwd)} {rightPart}";
            }
            if (_showPath)
            {
                rightPart = $"{SessionSelectorStatics.ShortenPath(session.Path)} {rightPart}";
            }

            // Cursor
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";

            // Calculate available width for message
            var prefixWidth = UnicodeWidth.VisibleWidth(prefix);
            var rightWidth = UnicodeWidth.VisibleWidth(rightPart) + 2; // +2 for spacing
            var availableForMsg = width - 2 - prefixWidth - rightWidth; // -2 for cursor

            var truncatedMsg = TextLayout.TruncateToWidth(normalizedMessage, Math.Max(10, availableForMsg), "…");

            // Style message
            string? messageColor = null;
            if (isConfirmingDelete)
            {
                messageColor = "error";
            }
            else if (isCurrent)
            {
                messageColor = "accent";
            }
            else if (hasName)
            {
                messageColor = "warning";
            }
            var styledMsg = messageColor is not null ? theme.Fg(messageColor, truncatedMsg) : truncatedMsg;
            if (isSelected)
            {
                styledMsg = theme.Bold(styledMsg);
            }

            // Build line
            var leftPart = cursor + theme.Fg("dim", prefix) + styledMsg;
            var leftWidth = UnicodeWidth.VisibleWidth(leftPart);
            var spacing = Math.Max(1, width - leftWidth - UnicodeWidth.VisibleWidth(rightPart));
            var styledRight = theme.Fg(isConfirmingDelete ? "error" : "dim", rightPart);

            var line = leftPart + new string(' ', spacing) + styledRight;
            if (isSelected)
            {
                line = theme.Bg("selectedBg", line);
            }
            lines.Add(TextLayout.TruncateToWidth(line, width));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < _filteredSessions.Count)
        {
            var scrollText = $"  ({_selectedIndex + 1}/{_filteredSessions.Count})";
            var scrollInfo = theme.Fg("muted", TextLayout.TruncateToWidth(scrollText, width, ""));
            lines.Add(scrollInfo);
        }

        return [.. lines];
    }

    private static string BuildTreePrefix(FlatSessionNode node)
    {
        if (node.Depth == 0)
        {
            return "";
        }

        var parts = node.AncestorContinues.Select(continues => continues ? "│  " : "   ");
        var branch = node.IsLast ? "└─ " : "├─ ";
        return string.Join("", parts) + branch;
    }

    public void HandleInput(string keyData)
    {
        var kb = GlobalKeybindings.Get();

        // Handle delete confirmation state first - intercept all keys
        if (_confirmingDeletePath is not null)
        {
            if (kb.Matches(keyData, TuiKeybindingIds.SelectConfirm))
            {
                var pathToDelete = _confirmingDeletePath;
                SetConfirmingDeletePath(null);
                _ = OnDeleteSession?.Invoke(pathToDelete);
                return;
            }
            if (kb.Matches(keyData, TuiKeybindingIds.SelectCancel))
            {
                SetConfirmingDeletePath(null);
                return;
            }
            // Ignore all other keys while confirming
            return;
        }

        if (kb.Matches(keyData, TuiKeybindingIds.InputTab))
        {
            OnToggleScope?.Invoke();
            return;
        }

        if (kb.Matches(keyData, AppKeybindings.AppSessionToggleSort))
        {
            OnToggleSort?.Invoke();
            return;
        }

        if (_keybindings.Matches(keyData, AppKeybindings.AppSessionToggleNamedFilter))
        {
            OnToggleNameFilter?.Invoke();
            return;
        }

        // Ctrl+P: toggle path display
        if (kb.Matches(keyData, AppKeybindings.AppSessionTogglePath))
        {
            _showPath = !_showPath;
            OnTogglePath?.Invoke(_showPath);
            return;
        }

        // Ctrl+D: initiate delete confirmation (useful on terminals that don't distinguish Ctrl+Backspace from Backspace)
        if (kb.Matches(keyData, AppKeybindings.AppSessionDelete))
        {
            StartDeleteConfirmationForSelectedSession();
            return;
        }

        // Rename selected session
        if (kb.Matches(keyData, AppKeybindings.AppSessionRename))
        {
            var selected = _filteredSessions.ElementAtOrDefault(_selectedIndex);
            if (selected is not null)
            {
                OnRenameSession?.Invoke(selected.Session.Path);
            }
            return;
        }

        // Ctrl+Backspace: non-invasive convenience alias for delete
        // Only triggers deletion when the query is empty; otherwise it is forwarded to the input
        if (kb.Matches(keyData, AppKeybindings.AppSessionDeleteNoninvasive))
        {
            if (_searchInput.GetValue().Length > 0)
            {
                _searchInput.HandleInput(keyData);
                FilterSessions(_searchInput.GetValue());
                return;
            }

            StartDeleteConfirmationForSelectedSession();
            return;
        }

        _selectionTouched = true;
        // Up arrow
        if (kb.Matches(keyData, TuiKeybindingIds.SelectUp))
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
        }
        // Down arrow
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectDown))
        {
            _selectedIndex = Math.Min(_filteredSessions.Count - 1, _selectedIndex + 1);
        }
        // Page up - jump up by maxVisible items
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectPageUp))
        {
            _selectedIndex = Math.Max(0, _selectedIndex - MaxVisible);
        }
        // Page down - jump down by maxVisible items
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectPageDown))
        {
            _selectedIndex = Math.Min(_filteredSessions.Count - 1, _selectedIndex + MaxVisible);
        }
        // Enter
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectConfirm))
        {
            var selected = _filteredSessions.ElementAtOrDefault(_selectedIndex);
            if (selected is not null)
            {
                OnSelect?.Invoke(selected.Session.Path);
            }
        }
        // Escape - cancel
        else if (kb.Matches(keyData, TuiKeybindingIds.SelectCancel))
        {
            OnCancel?.Invoke();
        }
        // Pass everything else to search input
        else
        {
            _searchInput.HandleInput(keyData);
            FilterSessions(_searchInput.GetValue());
        }
    }
}

internal static class SessionFileDeletion
{
    /// <summary>Result payload of <see cref="DeleteSessionFileAsync"/> (TS inline object).</summary>
    public sealed record DeleteResult(bool Ok, string Method, string? Error = null);

    /// <summary>
    /// Delete a session file, trying the `trash` CLI first, then falling back to unlink.
    /// </summary>
    public static async Task<DeleteResult> DeleteSessionFileAsync(string sessionPath)
    {
        // Try `trash` first (if installed)
        var trashArgs = sessionPath.StartsWith('-')
            ? (IReadOnlyList<string>)["--", sessionPath]
            : (IReadOnlyList<string>)[sessionPath];
        var (status, stderrText, spawnError) = RunTrash(trashArgs);

        string? GetTrashErrorHint()
        {
            var parts = new List<string>();
            if (spawnError is not null)
            {
                parts.Add(spawnError);
            }
            var stderr = stderrText?.Trim();
            if (!string.IsNullOrEmpty(stderr))
            {
                parts.Add(stderr.Split('\n')[0]);
            }
            if (parts.Count == 0) return null;
            var joined = string.Join(" · ", parts);
            return $"trash: {(joined.Length > 200 ? joined[..200] : joined)}";
        }

        // If trash reports success, or the file is gone afterwards, treat it as successful
        if (status == 0 || !File.Exists(sessionPath))
        {
            return new DeleteResult(true, "trash");
        }

        // Fallback to permanent deletion
        try
        {
            File.Delete(sessionPath);
            return new DeleteResult(true, "unlink");
        }
        catch (Exception err)
        {
            var unlinkError = err.Message;
            var trashErrorHint = GetTrashErrorHint();
            var error = trashErrorHint is not null ? $"{unlinkError} ({trashErrorHint})" : unlinkError;
            return new DeleteResult(false, "unlink", error);
        }
    }

    private static (int? Status, string? Stderr, string? Error) RunTrash(IReadOnlyList<string> args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "trash",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.Start();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stderr, null);
        }
        catch (Exception err)
        {
            return (null, null, err.Message);
        }
    }
}

/// <summary>TS <c>SessionsLoader</c>: loads a session list with progress and abort support.</summary>
public delegate Task<IReadOnlyList<SessionInfo>> SessionsLoader(
    SessionListProgress? onProgress = null, CancellationToken signal = default);

/// <summary>
/// Component that renders a session selector (TS <c>SessionSelectorComponent</c>).
/// </summary>
public sealed class SessionSelectorComponent : Container, IFocusable
{
    private readonly bool _canRename;
    private readonly SessionList _sessionList;
    private readonly SessionSelectorHeader _header;
    private readonly KeybindingsManager _keybindings;
    private string _scope = "current";
    private SortMode _sortMode = SortMode.Threaded;
    private NameFilter _nameFilter = NameFilter.All;
    private IReadOnlyList<SessionInfo>? _currentSessions;
    private IReadOnlyList<SessionInfo>? _allSessions;
    private readonly SessionsLoader _currentSessionsLoader;
    private readonly SessionsLoader _allSessionsLoader;
    private readonly Action _requestRender;
    private readonly Func<string, string?, Task>? _renameSession;
    private CancellationTokenSource? _currentLoad;
    private CancellationTokenSource? _allLoad;

    private string _mode = "list";
    private readonly Input _renameInput = new();
    private string? _renameTargetPath;

    // Focusable implementation - propagate to sessionList for IME cursor positioning
    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _sessionList.Focused = value;
            _renameInput.Focused = value;
            if (value && _mode == "rename")
            {
                _renameInput.Focused = true;
            }
        }
    }

    private void BuildBaseLayout(IComponent content, bool showHeader = true)
    {
        Clear();
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => ThemeRuntime.Current.Fg("accent", s)));
        AddChild(new Spacer(1));
        if (showHeader)
        {
            AddChild(_header);
            AddChild(new Spacer(1));
        }
        AddChild(content);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => ThemeRuntime.Current.Fg("accent", s)));
    }

    /// <summary>TS <c>options</c> parameter.</summary>
    public sealed record Options
    {
        public Func<string, string?, Task>? RenameSession { get; init; }
        public bool? ShowRenameHint { get; init; }
        public KeybindingsManager? Keybindings { get; init; }
    }

    public SessionSelectorComponent(
        SessionsLoader currentSessionsLoader,
        SessionsLoader allSessionsLoader,
        Action<string> onSelect,
        Action onCancel,
        Action onExit,
        Action requestRender,
        Options? options = null,
        string? currentSessionFilePath = null)
    {
        _keybindings = options?.Keybindings ?? new KeybindingsManager();
        _currentSessionsLoader = currentSessionsLoader;
        _allSessionsLoader = allSessionsLoader;
        _requestRender = requestRender;
        _header = new SessionSelectorHeader(_scope, _sortMode, _nameFilter, _requestRender);
        var renameSession = options?.RenameSession;
        _renameSession = renameSession;
        _canRename = renameSession is not null;
        _header.SetShowRenameHint(options?.ShowRenameHint ?? _canRename);

        // Create session list (starts empty, will be populated after load)
        _sessionList = new SessionList(
            [],
            false,
            _sortMode,
            _nameFilter,
            _keybindings,
            currentSessionFilePath);

        BuildBaseLayout(_sessionList);

        _renameInput.OnSubmit = value => _ = ConfirmRename(value);

        // Ensure header status timeouts are cleared when leaving the selector
        void ClearStatusMessage() => _header.SetStatusMessage(null);
        _sessionList.OnSelect = sessionPath =>
        {
            ClearStatusMessage();
            CancelLoads();
            onSelect(sessionPath);
        };
        _sessionList.OnCancel = () =>
        {
            ClearStatusMessage();
            CancelLoads();
            onCancel();
        };
        _sessionList.OnExit = () =>
        {
            ClearStatusMessage();
            CancelLoads();
            onExit();
        };
        _sessionList.OnToggleScope = ToggleScope;
        _sessionList.OnToggleSort = ToggleSortMode;
        _sessionList.OnToggleNameFilter = ToggleNameFilter;
        _sessionList.OnRenameSession = sessionPath =>
        {
            if (renameSession is null) return;
            if ((_scope == "current" ? _currentLoad : _allLoad) is not null) return;

            var sessions = _scope == "all" ? (_allSessions ?? []) : (_currentSessions ?? []);
            var session = sessions.FirstOrDefault(s => s.Path == sessionPath);
            EnterRenameMode(sessionPath, session?.Name);
        };

        // Sync list events to header
        _sessionList.OnTogglePath = showPath =>
        {
            _header.SetShowPath(showPath);
            _requestRender();
        };
        _sessionList.OnDeleteConfirmationChange = path =>
        {
            _header.SetConfirmingDeletePath(path);
            _requestRender();
        };
        _sessionList.OnError = msg =>
        {
            _header.SetStatusMessage((true, msg), 3000);
            _requestRender();
        };

        // Handle session deletion
        _sessionList.OnDeleteSession = async sessionPath =>
        {
            var result = await SessionFileDeletion.DeleteSessionFileAsync(sessionPath);

            if (result.Ok)
            {
                if (_currentSessions is not null)
                {
                    _currentSessions = [.. _currentSessions.Where(s => s.Path != sessionPath)];
                }
                if (_allSessions is not null)
                {
                    _allSessions = [.. _allSessions.Where(s => s.Path != sessionPath)];
                }

                var sessions = _scope == "all" ? (_allSessions ?? []) : (_currentSessions ?? []);
                var showCwd = _scope == "all";
                _sessionList.SetSessions(sessions, showCwd);

                var msg = result.Method == "trash" ? "Session moved to trash" : "Session deleted";
                _header.SetStatusMessage((false, msg), 2000);
                await RefreshSessionsAfterMutation();
            }
            else
            {
                var errorMessage = result.Error ?? "Unknown error";
                _header.SetStatusMessage((true, $"Failed to delete: {errorMessage}"), 3000);
            }

            _requestRender();
        };

        // Start loading current sessions immediately
        _ = LoadScope("current");
    }

    public override void HandleInput(string data)
    {
        if (_mode == "rename")
        {
            var kb = GlobalKeybindings.Get();
            if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
            {
                ExitRenameMode();
                return;
            }
            _renameInput.HandleInput(data);
            return;
        }

        _sessionList.HandleInput(data);
    }

    private void CancelLoads()
    {
        if (_currentLoad is not null)
        {
            _currentLoad.Cancel();
            _currentLoad.Dispose();
            _currentLoad = null;
            _currentSessions = null;
        }
        if (_allLoad is not null)
        {
            _allLoad.Cancel();
            _allLoad.Dispose();
            _allLoad = null;
            _allSessions = null;
        }
    }

    private void EnterRenameMode(string sessionPath, string? currentName)
    {
        _mode = "rename";
        _renameTargetPath = sessionPath;
        _renameInput.SetValue(currentName ?? "");
        _renameInput.Focused = true;

        var panel = new Container();
        panel.AddChild(new TuiText(ThemeRuntime.Current.Bold("Rename Session"), 1, 0));
        panel.AddChild(new Spacer(1));
        panel.AddChild(_renameInput);
        panel.AddChild(new Spacer(1));
        panel.AddChild(new TuiText(
            ThemeRuntime.Current.Fg(
                "muted",
                $"{KeybindingHints.KeyText("tui.select.confirm")} to save · {KeybindingHints.KeyText("tui.select.cancel")} to cancel"),
            1,
            0));

        BuildBaseLayout(panel, showHeader: false);
        _requestRender();
    }

    private void ExitRenameMode()
    {
        _mode = "list";
        _renameTargetPath = null;

        BuildBaseLayout(_sessionList);

        _requestRender();
    }

    private async Task ConfirmRename(string value)
    {
        var next = value.Trim();
        if (next.Length == 0) return;
        var target = _renameTargetPath;
        if (target is null)
        {
            ExitRenameMode();
            return;
        }

        // Find current name for callback
        var renameSession = _renameSession;
        if (renameSession is null)
        {
            ExitRenameMode();
            return;
        }

        try
        {
            await renameSession(target, next);
            await RefreshSessionsAfterMutation();
        }
        finally
        {
            ExitRenameMode();
        }
    }

    private async Task LoadScope(string scope)
    {
        if ((scope == "current" ? _currentLoad : _allLoad) is not null) return;

        var showCwd = scope == "all";
        var controller = new CancellationTokenSource();
        if (scope == "current")
        {
            _currentLoad = controller;
        }
        else
        {
            _allLoad = controller;
        }
        _header.SetScope(scope);
        _header.SetLoading(true);
        _requestRender();

        var isActive = () => (scope == "current" ? _currentLoad : _allLoad) == controller;
        void OnProgress(int loaded, int total, IReadOnlyList<SessionInfo>? partialSessions)
        {
            if (!isActive()) return;
            if (partialSessions is not null)
            {
                var sessions = partialSessions;
                if (scope == "current")
                {
                    _currentSessions = sessions;
                }
                else
                {
                    _allSessions = sessions;
                }
                if (scope == _scope) _sessionList.SetSessions(sessions, showCwd);
            }
            if (scope != _scope) return;
            _header.SetProgress(loaded, total);
            _requestRender();
        }

        try
        {
            var sessions = await (scope == "current"
                ? _currentSessionsLoader(OnProgress, controller.Token)
                : _allSessionsLoader(OnProgress, controller.Token));
            if (!isActive()) return;

            if (scope == "current")
            {
                _currentSessions = sessions;
                _currentLoad = null;
            }
            else
            {
                _allSessions = sessions;
                _allLoad = null;
            }
            controller.Dispose();

            if (scope != _scope) return;
            _header.SetLoading(false);
            _sessionList.SetSessions(sessions, showCwd);
            _requestRender();
        }
        catch (OperationCanceledException)
        {
            if (!isActive()) return;
            ResetLoad(scope);
            controller.Dispose();
        }
        catch (Exception err)
        {
            if (!isActive()) return;
            ResetLoad(scope);
            controller.Dispose();
            if (scope != _scope) return;

            _header.SetLoading(false);
            _header.SetStatusMessage((true, $"Failed to load sessions: {err.Message}"), 4000);
            _sessionList.SetSessions([], showCwd);
            _requestRender();
        }
    }

    private void ResetLoad(string scope)
    {
        if (scope == "current")
        {
            _currentLoad = null;
            _currentSessions = null;
        }
        else
        {
            _allLoad = null;
            _allSessions = null;
        }
    }

    private void ToggleSortMode()
    {
        // Cycle: threaded -> recent -> relevance -> threaded
        _sortMode = _sortMode == SortMode.Threaded ? SortMode.Recent
            : _sortMode == SortMode.Recent ? SortMode.Relevance
            : SortMode.Threaded;
        _header.SetSortMode(_sortMode);
        _sessionList.SetSortMode(_sortMode);
        _requestRender();
    }

    private void ToggleNameFilter()
    {
        _nameFilter = _nameFilter == NameFilter.All ? NameFilter.Named : NameFilter.All;
        _header.SetNameFilter(_nameFilter);
        _sessionList.SetNameFilter(_nameFilter);
        _requestRender();
    }

    private async Task RefreshSessionsAfterMutation()
    {
        CancelLoads();
        _currentSessions = null;
        _allSessions = null;
        await LoadScope(_scope);
    }

    private void ToggleScope()
    {
        _scope = _scope == "current" ? "all" : "current";
        var sessions = _scope == "current" ? _currentSessions : _allSessions;
        var loading = (_scope == "current" ? _currentLoad : _allLoad) is not null;
        _header.SetScope(_scope);
        _header.SetLoading(loading);
        _sessionList.SetSessions(sessions ?? (IReadOnlyList<SessionInfo>)[], _scope == "all");
        _requestRender();
        if (sessions is null && !loading) _ = LoadScope(_scope);
    }

    internal SessionList GetSessionList() => _sessionList;
}
