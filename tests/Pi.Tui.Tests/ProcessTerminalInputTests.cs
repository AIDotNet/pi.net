using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// The Windows input path feeds <see cref="ProcessTerminal"/> one key at a time, so a terminal reply
/// (OSC 10/11/4 colors, OSC 7501 program status, …) reaches the framework as a stream of single
/// characters. Without the <see cref="StdinBuffer"/> stage those characters are typed into the focused
/// editor instead of being recognized and swallowed. These tests drive the same pipeline the read loop
/// drives, without touching a real console.
/// </summary>
public class ProcessTerminalInputTests
{
    [Fact]
    public void ColorReplyFedKeyByKeyIsReassembledIntoOneSequence()
    {
        var forwarded = new List<string>();
        var terminal = new ProcessTerminal();
        terminal.StartInputPipelineForTests(forwarded.Add, escapeTimeoutMs: 5_000);

        // What Windows Terminal answers to the theme's OSC 10/11 query, delivered one key at a time.
        Feed(terminal, "\x1b]10;rgb:cccc/cccc/cccc\x1b\\");
        Feed(terminal, "\x1b]11;rgb:0e0e/0e0e/0e0e\x07");

        Assert.Equal(["\x1b]10;rgb:cccc/cccc/cccc\x1b\\", "\x1b]11;rgb:0e0e/0e0e/0e0e\x07"], forwarded);
    }

    [Fact]
    public void ProgramStatusReplyIsConsumedInsteadOfForwarded()
    {
        var forwarded = new List<string>();
        var terminal = new ProcessTerminal();
        terminal.StartInputPipelineForTests(forwarded.Add, escapeTimeoutMs: 5_000);

        Feed(terminal, "\x1b]7501;?\x1b\\");
        Feed(terminal, "a");

        Assert.Equal(["a"], forwarded);
    }

    [Fact]
    public void PlainKeysStillArriveOneSequenceEach()
    {
        var forwarded = new List<string>();
        var terminal = new ProcessTerminal();
        terminal.StartInputPipelineForTests(forwarded.Add, escapeTimeoutMs: 5_000);

        Feed(terminal, "ab");
        terminal.FeedKeyForTests(Key(ConsoleKey.Enter, '\r'));
        terminal.FeedKeyForTests(Key(ConsoleKey.UpArrow, '\0'));

        Assert.Equal(["a", "b", "\r", "\x1b[A"], forwarded);
    }

    [Fact]
    public void BracketedPasteIsRewrappedForTheEditor()
    {
        var forwarded = new List<string>();
        var terminal = new ProcessTerminal();
        terminal.StartInputPipelineForTests(forwarded.Add, escapeTimeoutMs: 5_000);

        Feed(terminal, "\x1b[200~hello\x1b[201~");

        Assert.Equal(["\x1b[200~hello\x1b[201~"], forwarded);
    }

    [Fact]
    public void EscapeTimeoutComesFromTheEnvironmentWithAnSshWidening()
    {
        Assert.Equal(250, ProcessTerminal.ResolveEscapeTimeoutMs(Env(("PI_TUI_ESC_TIMEOUT", "250"))));
        Assert.Equal(100, ProcessTerminal.ResolveEscapeTimeoutMs(Env(("SSH_CONNECTION", "1 2 3 4"))));
        Assert.Equal(100, ProcessTerminal.ResolveEscapeTimeoutMs(Env(("SSH_TTY", "/dev/pts/1"))));
        Assert.Equal(10, ProcessTerminal.ResolveEscapeTimeoutMs(Env()));

        // A non-numeric or non-positive override falls through to the SSH / default branch.
        Assert.Equal(10, ProcessTerminal.ResolveEscapeTimeoutMs(Env(("PI_TUI_ESC_TIMEOUT", "abc"))));
        Assert.Equal(10, ProcessTerminal.ResolveEscapeTimeoutMs(Env(("PI_TUI_ESC_TIMEOUT", "0"))));
    }

    private static void Feed(ProcessTerminal terminal, string text)
    {
        foreach (var c in text)
        {
            terminal.FeedKeyForTests(c == '\x1b'
                ? Key(ConsoleKey.Escape, c)
                : Key(ConsoleKey.NoName, c));
        }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c) => new(c, key, false, false, false);

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name =>
        pairs.Where(p => p.Name == name).Select(p => (string?)p.Value).FirstOrDefault();
}
