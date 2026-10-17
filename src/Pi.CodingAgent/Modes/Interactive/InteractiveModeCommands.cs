// ============================================================================
// Slash-command helpers for InteractiveMode.
// Port of interactive-mode.ts's getPathCommandArgument (L6511-6538), the reload status message
// (L6478-6481), the import usage error (L6543), the bash failure message (L7043-7045) and
// handleHotkeysCommand's markdown body (L6782-6876).
//
// The handlers' side effects (containers, session calls, runtime-host calls) stay in the class
// (4f-4m); these are the strings and the path-parsing the handlers are built on.
// ============================================================================

using System.Text.RegularExpressions;
using Pi.CodingAgent.Modes.Interactive.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the slash-command helpers of <c>interactive-mode.ts</c>.</summary>
public static partial class InteractiveModeCommands
{
    /// <summary>TS <c>"Usage: /import &lt;path.jsonl&gt;"</c>.</summary>
    public const string ImportUsageError = "Usage: /import <path.jsonl>";

    /// <summary>TS <c>/\s/</c> — the first whitespace ends an unquoted path argument.</summary>
    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// TS <c>getPathCommandArgument(text, command)</c>: the argument of <c>/export</c> /
    /// <c>/import</c>. Quoted arguments keep their inner text verbatim (no escape handling), an
    /// unterminated quote or a missing argument yields <see langword="undefined"/>, and an unquoted
    /// argument ends at the first whitespace.
    /// </summary>
    public static string? GetPathCommandArgument(string text, string command)
    {
        if (text == command)
        {
            return null;
        }

        if (!text.StartsWith($"{command} ", StringComparison.Ordinal))
        {
            return null;
        }

        var argsString = text[(command.Length + 1)..].TrimStart();
        if (argsString.Length == 0)
        {
            return null;
        }

        var firstChar = argsString[0];
        if (firstChar is '"' or '\'')
        {
            var closingQuoteIndex = argsString.IndexOf(firstChar, 1);
            if (closingQuoteIndex < 0)
            {
                return null;
            }

            return argsString[1..closingQuoteIndex];
        }

        var firstWhitespace = WhitespaceRegex().Match(argsString);
        if (!firstWhitespace.Success)
        {
            return argsString;
        }

        return argsString[..firstWhitespace.Index];
    }

    /// <summary>
    /// TS's reload completion status: the saved-trust variant appends its own clause to the same
    /// sentence rather than being a different string.
    /// </summary>
    public static string ReloadedStatusMessage(bool savedImplicitProjectTrust) =>
        savedImplicitProjectTrust
            ? "Reloaded keybindings, extensions, skills, prompts, themes, and context files; saved project trust"
            : "Reloaded keybindings, extensions, skills, prompts, themes, and context files";

    /// <summary>
    /// TS's bash failure notice: a non-Error rejection reads <c>"Unknown error"</c> rather than
    /// <c>""</c>.
    /// </summary>
    public static string BashFailureMessage(string? message) =>
        $"Bash command failed: {(string.IsNullOrEmpty(message) ? "Unknown error" : message)}";

    /// <summary>
    /// TS <c>handleHotkeysCommand</c>'s markdown body: the three fixed sections with the live key
    /// displays interpolated, plus an <c>**Extensions**</c> section when any extension registered a
    /// shortcut. The result is trimmed, exactly like the <c>hotkeys.trim()</c> the Markdown gets.
    /// </summary>
    /// <param name="getEditorKeyDisplay">TS <c>keyDisplayText(action)</c> over the editor bindings.</param>
    /// <param name="getAppKeyDisplay">TS <c>keyDisplayText(action)</c> over the app bindings.</param>
    public static string BuildHotkeysMarkdown(
        Func<string, string> getEditorKeyDisplay,
        Func<string, string> getAppKeyDisplay,
        bool isWindows,
        IReadOnlyList<(string Key, string? Description, string ExtensionPath)> extensionShortcuts)
    {
        string EditorKey(string action) => getEditorKeyDisplay(action);
        string AppKey(string action) => getAppKeyDisplay(action);

        var hotkeys = $"""

                         **Navigation**
                         | Key | Action |
                         |-----|--------|
                         | `{EditorKey("tui.editor.cursorUp")}` / `{EditorKey("tui.editor.cursorDown")}` / `{EditorKey("tui.editor.cursorLeft")}` / `{EditorKey("tui.editor.cursorRight")}` | Move cursor / browse history |
                         | `{EditorKey("tui.editor.cursorWordLeft")}` / `{EditorKey("tui.editor.cursorWordRight")}` | Move by word |
                         | `{EditorKey("tui.editor.cursorLineStart")}` | Start of line |
                         | `{EditorKey("tui.editor.cursorLineEnd")}` | End of line |
                         | `{EditorKey("tui.editor.jumpForward")}` | Jump forward to character |
                         | `{EditorKey("tui.editor.jumpBackward")}` | Jump backward to character |
                         | `{EditorKey("tui.editor.pageUp")}` / `{EditorKey("tui.editor.pageDown")}` | Scroll by page |

                         **Editing**
                         | Key | Action |
                         |-----|--------|
                         | `{EditorKey("tui.input.submit")}` | Send message |
                         | `{EditorKey("tui.input.newLine")}` | New line{(isWindows ? " (Ctrl+Enter on Windows Terminal)" : string.Empty)} |
                         | `{EditorKey("tui.editor.deleteWordBackward")}` | Delete word backwards |
                         | `{EditorKey("tui.editor.deleteWordForward")}` | Delete word forwards |
                         | `{EditorKey("tui.editor.deleteToLineStart")}` | Delete to start of line |
                         | `{EditorKey("tui.editor.deleteToLineEnd")}` | Delete to end of line |
                         | `{EditorKey("tui.editor.yank")}` | Paste the most-recently-deleted text |
                         | `{EditorKey("tui.editor.yankPop")}` | Cycle through the deleted text after pasting |
                         | `{EditorKey("tui.editor.undo")}` | Undo |

                         **Other**
                         | Key | Action |
                         |-----|--------|
                         | `{EditorKey("tui.input.tab")}` | Path completion / accept autocomplete |
                         | `{AppKey("app.interrupt")}` | Cancel autocomplete / abort streaming |
                         | `{AppKey("app.clear")}` | Clear editor (first) / exit (second) |
                         | `{AppKey("app.exit")}` | Exit (when editor is empty) |
                         | `{AppKey("app.suspend")}` | Suspend to background |
                         | `{AppKey("app.thinking.cycle")}` | Cycle thinking level |
                         | `{AppKey("app.model.cycleForward")}` / `{AppKey("app.model.cycleBackward")}` | Cycle models |
                         | `{AppKey("app.model.select")}` | Open model selector |
                         | `{AppKey("app.tools.expand")}` | Toggle tool output expansion |
                         | `{AppKey("app.thinking.toggle")}` | Toggle thinking block visibility |
                         | `{AppKey("app.editor.external")}` | Edit message in external editor |
                         | `{AppKey("app.message.copy")}` | Copy selection or last assistant message |
                         | `{AppKey("app.message.followUp")}` | Queue follow-up message |
                         | `{AppKey("app.message.dequeue")}` | Restore queued messages |
                         | `{AppKey("app.clipboard.pasteImage")}` | Paste files on macOS, images, or text from clipboard |
                         | `/` | Slash commands |
                         | `!` | Run bash command |
                         | `!!` | Run bash command (excluded from context) |
                         """;

        if (extensionShortcuts.Count > 0)
        {
            hotkeys += "\n\n**Extensions**\n| Key | Action |\n|-----|--------|\n";
            foreach (var (key, description, extensionPath) in extensionShortcuts)
            {
                // TS: `shortcut.description ?? shortcut.extensionPath`, capitalised display key.
                var label = description ?? extensionPath;
                var keyDisplay = KeybindingHints.FormatKeyText(key, new KeyTextFormatOptions { Capitalize = true });
                hotkeys += $"| `{keyDisplay}` | {label} |\n";
            }
        }

        return hotkeys.Trim();
    }
}
