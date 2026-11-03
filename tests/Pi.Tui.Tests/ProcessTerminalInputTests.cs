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

    /// <summary>
    /// 本机按着 Shift 时把 CR / ESC+CR 升级成 Kitty 的 shift+enter（`73dd066ee` + `86dc686cf`）：
    /// Apple Terminal 与 Windows 在没开 Kitty 协议时就是这么发的，否则会被当成 alt+enter 排进 follow-up。
    /// 判定只在「该判定」且「Shift 确实按着」时生效，其余原样返回。
    /// </summary>
    [Fact]
    public void NormalizeNativeShiftEnterInput_UpgradesCarriageReturnOnlyWhenShiftIsPressed()
    {
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", true, true));
        // 有些 Apple Terminal 配置发 ESC+CR（#11559）。
        Assert.Equal("\x1b[13;2u", ProcessTerminal.NormalizeNativeShiftEnterInput("\x1b\r", true, true));
        // Shift 没按着：保持 CR / alt+enter。
        Assert.Equal("\r", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", true, false));
        Assert.Equal("\x1b\r", ProcessTerminal.NormalizeNativeShiftEnterInput("\x1b\r", true, false));
        // 不该判定的场景（其它平台、其它键）原样返回；谓词已经把「是不是 CR」判掉了。
        Assert.Equal("\r", ProcessTerminal.NormalizeNativeShiftEnterInput("\r", false, true));
        Assert.Equal("a", ProcessTerminal.NormalizeNativeShiftEnterInput("a", false, true));
        // 谓词只认 CR / ESC+CR。
        Assert.False(ProcessTerminal.ShouldDetectNativeShiftEnter("a"));
        Assert.False(ProcessTerminal.ShouldDetectNativeShiftEnter("\x1b[13;2u"));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c) => new(c, key, false, false, false);

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name =>
        pairs.Where(p => p.Name == name).Select(p => (string?)p.Value).FirstOrDefault();
}
