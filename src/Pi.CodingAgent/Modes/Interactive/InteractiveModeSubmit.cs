// ============================================================================
// Editor-submit routing for InteractiveMode.
// Port of interactive-mode.ts's setupEditorSubmitHandler (L3179-3375): the built-in slash-command
// chain, the bash `!`/`!!` branch and the extension-command name probe (L4750-4757).
//
// In TS this is a 200-line if-chain inside an onSubmit closure. The routing decisions are extracted
// here so the command table and its slicing rules can be tested without an editor; the class (4f-4m)
// keeps the closure and calls the per-command handlers.
// ============================================================================

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
