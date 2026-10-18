// ============================================================================
// Editor-submit routing for InteractiveMode.
// Port of interactive-mode.ts's setupEditorSubmitHandler (L3179-3375): the built-in slash-command
// chain, the bash `!`/`!!` branch and the extension-command name probe (L4750-4757).
//
// In TS this is a 200-line if-chain inside an onSubmit closure. The routing decisions are extracted
// here so the command table and its slicing rules can be tested without an editor; the class (4f-4m)
// keeps the closure and calls the per-command handlers.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>The built-in commands the submit handler routes to. TS <c>/command</c> branches.</summary>
public enum SubmitCommand
{
    Settings,
    ScopedModels,
    Model,
    Thinking,
    Export,
    Import,
    Share,
    Bug,
    Copy,
    Name,
    Session,
    Changelog,
    Hotkeys,
    Fork,
    Clone,
    Tree,
    Trust,
    Login,
    Logout,
    New,
    Compact,
    Reload,
    Debug,
    ArminSaysHi,
    DementedElves,
    Resume,
    Quit,
}

/// <summary>
/// One routed submit: the command, the extracted argument (only for commands that take one, and
/// <see langword="null"/> when it is blank, mirroring TS's <c>undefined</c>), and the full text as
/// the handlers that want the raw line receive it.
/// </summary>
public sealed record SubmitRoute(SubmitCommand Command, string? Argument, string FullText);

/// <summary>Port of the submit-routing decisions of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeSubmit
{
    /// <summary>TS <c>"A bash command is already running. Press Esc to cancel it first."</c>.</summary>
    public const string BashBusyWarning = "A bash command is already running. Press Esc to cancel it first.";

    /// <summary>
    /// The five commands that slice a search term off their prefix. All of them match the bare
    /// command or the command followed by a space, and the slice lengths are the observable
    /// contract: <c>/bug</c> slices 4 (the command itself), the others slice their <c>"name "</c>
    /// prefix including the trailing space.
    /// </summary>
    private static readonly (string Prefix, int Slice, SubmitCommand Command)[] ArgumentCommands =
    [
        ("/bug", 4, SubmitCommand.Bug),
        ("/model", 7, SubmitCommand.Model),
        ("/thinking", 10, SubmitCommand.Thinking),
        ("/login", 7, SubmitCommand.Login),
        ("/compact", 9, SubmitCommand.Compact),
    ];

    /// <summary>Commands matched as an exact string only.</summary>
    private static readonly IReadOnlyDictionary<string, SubmitCommand> ExactCommands =
        new Dictionary<string, SubmitCommand>(StringComparer.Ordinal)
        {
            ["/settings"] = SubmitCommand.Settings,
            ["/scoped-models"] = SubmitCommand.ScopedModels,
            ["/share"] = SubmitCommand.Share,
            ["/copy"] = SubmitCommand.Copy,
            ["/session"] = SubmitCommand.Session,
            ["/changelog"] = SubmitCommand.Changelog,
            ["/hotkeys"] = SubmitCommand.Hotkeys,
            ["/fork"] = SubmitCommand.Fork,
            ["/clone"] = SubmitCommand.Clone,
            ["/tree"] = SubmitCommand.Tree,
            ["/trust"] = SubmitCommand.Trust,
            ["/logout"] = SubmitCommand.Logout,
            ["/new"] = SubmitCommand.New,
            ["/reload"] = SubmitCommand.Reload,
            ["/debug"] = SubmitCommand.Debug,
            ["/arminsayshi"] = SubmitCommand.ArminSaysHi,
            ["/dementedelves"] = SubmitCommand.DementedElves,
            ["/resume"] = SubmitCommand.Resume,
            ["/quit"] = SubmitCommand.Quit,
        };

    /// <summary>Commands matched as <c>/name</c> or <c>/name …</c>, whose handler gets the full line.</summary>
    private static readonly (string Prefix, SubmitCommand Command)[] FullLineCommands =
    [
        ("/export", SubmitCommand.Export),
        ("/import", SubmitCommand.Import),
        ("/name", SubmitCommand.Name),
    ];

    /// <summary>
    /// Route a trimmed submit line. Returns <see langword="null"/> when it is not a built-in command
    /// (extension commands, bash lines and plain messages fall through to their own branches).
    /// </summary>
    public static SubmitRoute? Route(string text)
    {
        foreach (var (prefix, slice, command) in ArgumentCommands)
        {
            if (text == prefix || text.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return new SubmitRoute(command, SliceArgument(text, slice), text);
            }
        }

        if (ExactCommands.TryGetValue(text, out var exact))
        {
            return new SubmitRoute(exact, null, text);
        }

        foreach (var (prefix, command) in FullLineCommands)
        {
            if (text == prefix || text.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return new SubmitRoute(command, null, text);
            }
        }

        return null;
    }

    /// <summary>
    /// TS's <c>text.slice(n).trim() || undefined</c>: the blank slice becomes
    /// <see langword="null"/>, so the handler receives no argument rather than <c>""</c>.
    /// </summary>
    private static string? SliceArgument(string text, int slice)
    {
        var argument = slice < text.Length ? text[slice..].Trim() : "";
        return argument.Length > 0 ? argument : null;
    }

    /// <summary>
    /// TS's bash branch: <c>!</c> runs a normal command, <c>!!</c> one excluded from context. A bare
    /// <c>!</c> or <c>!!</c> (empty command) is <em>not</em> a bash line — it falls through to normal
    /// submission, unlike every other prefix in the handler.
    /// </summary>
    public static (string? Command, bool Excluded) ParseBashCommand(string text)
    {
        if (!text.StartsWith('!'))
        {
            return (null, false);
        }

        var excluded = text.StartsWith("!!");
        var command = (excluded ? text[2..] : text[1..]).Trim();
        return command.Length > 0 ? (command, excluded) : (null, false);
    }

    /// <summary>
    /// TS <c>isExtensionCommand</c>'s name probe: everything before the first space, without the
    /// leading slash. Whether the extension runner has that command stays with the caller.
    /// </summary>
    public static string GetSlashCommandName(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        return spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
    }
}

public partial class InteractiveMode
{
    /// <summary>
    /// TS <c>setupEditorSubmitHandler</c> (L3179-3375)：提交 if-chain 的闭包本体。
    /// </summary>
    /// <remarks>
    /// 分支顺序、各分支清空编辑器的时机（await 前后）、bash 的 <c>isBashMode</c> 复位时机都按 TS
    /// 逐条对齐：编辑器多数在调用处理器**之前**清空，export/import/share/copy/name/session/
    /// changelog/hotkeys/fork/tree/trust/logout/debug/armin/demented/resume 则在**之后**。
    /// 路由判定走 <see cref="InteractiveModeSubmit.Route"/>（表驱动，单测覆盖），此处只做分派。
    /// </remarks>
    private void SetupEditorSubmitHandler()
    {
        _defaultEditor.OnSubmit = text =>
        {
            _ = HandleSubmitAsync(text);
        };
    }

    /// <summary>Test seam: install the submit handler without running the full init.</summary>
    internal void InstallSubmitHandlerForTests() => SetupEditorSubmitHandler();

    private async Task HandleSubmitAsync(string rawText)
    {
        var text = rawText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var route = InteractiveModeSubmit.Route(text);
        if (route is not null)
        {
            await DispatchCommandAsync(route).ConfigureAwait(true);
            return;
        }

        // Handle bash command (! for normal, !! for excluded from context)
        var (bashCommand, bashExcluded) = InteractiveModeSubmit.ParseBashCommand(text);
        if (bashCommand is not null)
        {
            if (Session.IsBashRunning)
            {
                ShowWarning(InteractiveModeSubmit.BashBusyWarning);
                _editor.SetText(text);
                return;
            }

            _editor.AddToHistory(text);
            await HandleBashCommandAsync(bashCommand, bashExcluded).ConfigureAwait(true);
            _isBashMode = false;
            UpdateEditorBorderColor();
            return;
        }

        // Queue input during compaction (extension commands execute immediately)
        if (Session.IsCompacting)
        {
            if (IsExtensionCommand(text))
            {
                _editor.AddToHistory(text);
                _editor.SetText("");
                await Session.PromptAsync(text).ConfigureAwait(true);
            }
            else
            {
                QueueCompactionMessage(text, "steer");
            }

            return;
        }

        // If streaming, use prompt() with steer behavior
        if (Session.IsStreaming)
        {
            _editor.AddToHistory(text);
            _editor.SetText("");
            await Session.PromptAsync(text, new PromptOptions { StreamingBehavior = "steer" }).ConfigureAwait(true);
            UpdatePendingMessagesDisplay();
            _ui.RequestRender();
            return;
        }

        // Normal message submission
        // First, move any pending bash components to chat
        FlushPendingBashComponents();

        if (_onInputCallback is not null)
        {
            _onInputCallback(text);
        }
        else
        {
            _pendingUserInputs.Add(text);
        }

        _editor.AddToHistory(text);
    }

    /// <summary>TS if-chain 的逐命令分派；清空编辑器的时机按 TS 原样保留。</summary>
    private async Task DispatchCommandAsync(SubmitRoute route)
    {
        var text = route.FullText;
        switch (route.Command)
        {
            case SubmitCommand.Settings:
                ShowSettingsSelector();
                _editor.SetText("");
                return;
            case SubmitCommand.ScopedModels:
                _editor.SetText("");
                ShowModelsSelector();
                return;
            case SubmitCommand.Model:
                _editor.SetText("");
                await HandleModelCommandAsync(route.Argument).ConfigureAwait(true);
                return;
            case SubmitCommand.Thinking:
                _editor.SetText("");
                HandleThinkingCommand(route.Argument);
                return;
            case SubmitCommand.Export:
                await HandleExportCommandAsync(text).ConfigureAwait(true);
                _editor.SetText("");
                return;
            case SubmitCommand.Import:
                await HandleImportCommandAsync(text).ConfigureAwait(true);
                _editor.SetText("");
                return;
            case SubmitCommand.Share:
                await HandleShareCommandAsync().ConfigureAwait(true);
                _editor.SetText("");
                return;
            case SubmitCommand.Bug:
                _editor.SetText("");
                await HandleBugCommandAsync(route.Argument).ConfigureAwait(true);
                return;
            case SubmitCommand.Copy:
                await HandleCopyCommandAsync().ConfigureAwait(true);
                _editor.SetText("");
                return;
            case SubmitCommand.Name:
                HandleNameCommand(text);
                _editor.SetText("");
                return;
            case SubmitCommand.Session:
                HandleSessionCommand();
                _editor.SetText("");
                return;
            case SubmitCommand.Changelog:
                HandleChangelogCommand();
                _editor.SetText("");
                return;
            case SubmitCommand.Hotkeys:
                HandleHotkeysCommand();
                _editor.SetText("");
                return;
            case SubmitCommand.Fork:
                ShowUserMessageSelector();
                _editor.SetText("");
                return;
            case SubmitCommand.Clone:
                _editor.SetText("");
                await HandleCloneCommandAsync().ConfigureAwait(true);
                return;
            case SubmitCommand.Tree:
                ShowTreeSelector();
                _editor.SetText("");
                return;
            case SubmitCommand.Trust:
                ShowTrustSelector();
                _editor.SetText("");
                return;
            case SubmitCommand.Login:
                _editor.SetText("");
                await HandleLoginCommandAsync(route.Argument).ConfigureAwait(true);
                return;
            case SubmitCommand.Logout:
                HandleLogoutCommand();
                _editor.SetText("");
                return;
            case SubmitCommand.New:
                _editor.SetText("");
                await HandleClearCommandAsync().ConfigureAwait(true);
                return;
            case SubmitCommand.Compact:
                _editor.SetText("");
                await HandleCompactCommandAsync(route.Argument).ConfigureAwait(true);
                return;
            case SubmitCommand.Reload:
                _editor.SetText("");
                await HandleReloadCommandAsync().ConfigureAwait(true);
                return;
            case SubmitCommand.Debug:
                HandleDebugCommand();
                _editor.SetText("");
                return;
            case SubmitCommand.ArminSaysHi:
                HandleArminSaysHi();
                _editor.SetText("");
                return;
            case SubmitCommand.DementedElves:
                HandleDementedDelves();
                _editor.SetText("");
                return;
            case SubmitCommand.Resume:
                ShowSessionSelector();
                _editor.SetText("");
                return;
            case SubmitCommand.Quit:
                _editor.SetText("");
                _ = ShutdownAsync();
                return;
            default:
                return;
        }
    }
}
