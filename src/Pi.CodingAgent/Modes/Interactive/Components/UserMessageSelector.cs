// ============================================================================
// User-message selector (fork-from-message picker).
// Port of modes/interactive/components/user-message-selector.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>UserMessageItem</c>: one entry of the fork picker.</summary>
public sealed record UserMessageItem(string Id, string Text, string? Timestamp = null);

/// <summary>
/// Port of the private <c>UserMessageList</c>: a scrollable list of user messages with a cursor.
/// </summary>
public sealed class UserMessageList : IComponent
{
    private const int MaxVisible = 10;

    private readonly IReadOnlyList<UserMessageItem> _messages;
    private int _selectedIndex;

    public UserMessageList(IReadOnlyList<UserMessageItem> messages, string? initialSelectedId = null)
    {
        // Store messages in chronological order (oldest to newest).
        _messages = messages;
        var initialIndex = initialSelectedId is null
            ? -1
            : IndexOf(messages, initialSelectedId);
        // Start with the selected message if provided, else default to the most recent.
        _selectedIndex = initialIndex >= 0 ? initialIndex : Math.Max(0, messages.Count - 1);
    }

    public Action<string>? OnSelect { get; set; }

    public Action? OnCancel { get; set; }

    /// <summary>Index of the cursor row (test seam; TS keeps <c>selectedIndex</c> private).</summary>
    internal int SelectedIndex => _selectedIndex;

    public void Invalidate()
    {
        // No cached state to invalidate currently.
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var lines = new List<string>();

        if (_messages.Count == 0)
        {
            lines.Add(theme.Fg("muted", "  No user messages found"));
            return [.. lines];
        }

        // Calculate visible range with scrolling.
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - MaxVisible / 2, _messages.Count - MaxVisible));
        var endIndex = Math.Min(startIndex + MaxVisible, _messages.Count);

        // Render visible messages (2 lines per message + blank line).
        for (var index = startIndex; index < endIndex; index++)
        {
            var message = _messages[index];
            var isSelected = index == _selectedIndex;

            // Normalize message to a single line.
            var normalizedMessage = message.Text.Replace('\n', ' ').Trim();

            // First line: cursor + message.
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";
            var maxMessageWidth = width - 2; // Account for cursor (2 chars).
            var truncatedMessage = TextLayout.TruncateToWidth(normalizedMessage, maxMessageWidth);
            var messageLine = cursor + (isSelected ? theme.Bold(truncatedMessage) : truncatedMessage);

            lines.Add(messageLine);

            // Second line: metadata (position in history).
            var position = index + 1;
            lines.Add(theme.Fg("muted", $"  Message {position} of {_messages.Count}"));
            lines.Add(""); // Blank line between messages.
        }

        // Add scroll indicator if needed.
        if (startIndex > 0 || endIndex < _messages.Count)
        {
            lines.Add(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_messages.Count})"));
        }

        return [.. lines];
    }

    /// <summary>
    /// Port of <c>UserMessageList.handleInput</c>. Plain public method because
    /// <see cref="IComponent"/>'s <c>HandleInput</c> is a default no-op interface member.
    /// </summary>
    public void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectUp))
        {
            // Go to previous (older) message, wrap to bottom when at top.
            _selectedIndex = _selectedIndex == 0 ? _messages.Count - 1 : _selectedIndex - 1;
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectDown))
        {
            // Go to next (newer) message, wrap to top when at bottom.
            _selectedIndex = _selectedIndex == _messages.Count - 1 ? 0 : _selectedIndex + 1;
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm))
        {
            var selected = _messages[_selectedIndex];
            OnSelect?.Invoke(selected.Id);
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            OnCancel?.Invoke();
        }
    }

    private static int IndexOf(IReadOnlyList<UserMessageItem> messages, string id)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Id == id) return index;
        }
        return -1;
    }
}

/// <summary>Port of <c>user-message-selector.ts</c>: the "Fork from Message" picker.</summary>
public sealed class UserMessageSelectorComponent : Container
{
    /// <summary>TS <c>setTimeout(() =&gt; onCancel(), 100)</c> when there is nothing to pick.</summary>
    public const int AutoCancelDelayMs = 100;

    private readonly UserMessageList _messageList;

    public UserMessageSelectorComponent(
        IReadOnlyList<UserMessageItem> messages,
        Action<string> onSelect,
        Action onCancel,
        string? initialSelectedId = null)
    {
        var theme = ThemeRuntime.Current;

        // Add header
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Bold("Fork from Message"), 1, 0));
        AddChild(new Text(
            theme.Fg("muted", "Select a user message to copy the active path up to that point into a new session"),
            1,
            0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Create message list
        _messageList = new UserMessageList(messages, initialSelectedId)
        {
            OnSelect = onSelect,
            OnCancel = onCancel,
        };

        AddChild(_messageList);

        // Add bottom border
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        // Auto-cancel if no messages. TS uses setTimeout; the port uses a one-shot Task.Delay, so the
        // callback runs on a thread-pool thread (same contract as the Loader animation timer).
        if (messages.Count == 0)
        {
            _ = Task.Delay(AutoCancelDelayMs).ContinueWith(_ => onCancel(), TaskScheduler.Default);
        }
    }

    public UserMessageList GetMessageList() => _messageList;
}
