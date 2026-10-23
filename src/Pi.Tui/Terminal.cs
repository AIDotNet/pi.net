using System.Runtime.InteropServices;
using System.Text;

namespace Pi.Tui;

/// <summary>Minimal terminal interface for TUI (port of <c>terminal.ts</c>'s <c>Terminal</c>).</summary>
public interface ITerminal
{
    /// <summary>Start the terminal with input and resize handlers.</summary>
    void Start(Action<string> onInput, Action onResize);

    /// <summary>Stop the terminal and restore state.</summary>
    void Stop();

    /// <summary>Drain stdin before exiting so late key releases do not leak to the parent shell.</summary>
    Task DrainInputAsync(int maxMs = 1000, int idleMs = 50);

    /// <summary>Write output to the terminal.</summary>
    void Write(string data);

    int Columns { get; }

    int Rows { get; }

    /// <summary>Whether the Kitty keyboard protocol is active.</summary>
    bool KittyProtocolActive { get; }

    /// <summary>Move the cursor up (negative) or down (positive) by N lines.</summary>
    void MoveBy(int lines);

    void HideCursor();

    void ShowCursor();

    void ClearLine();

    void ClearFromCursor();

    void ClearScreen();

    void SetTitle(string title);

    /// <summary>Indeterminate progress indicator (OSC 9;4).</summary>
    void SetProgress(bool active);

    /// <summary>
    /// Report what the program is doing (OSC 7501). Sent only to terminals that support it; the latest
    /// status is re-sent when support is confirmed or the terminal restarts.
    /// </summary>
    void SetProgramStatus(ProgramStatus status)
    {
        // Terminals that cannot report status ignore it; fakes need not implement this.
    }

    /// <summary>
    /// Synchronously restore terminal modes from a process exit handler when <see cref="Stop"/> did not
    /// run. Writes <paramref name="before"/>, then this terminal's own mode resets, then
    /// <paramref name="after"/>.
    /// </summary>
    void RestoreOnExit(string before, string after)
    {
        // Fakes that never touch the real terminal need no reset.
    }
}

/// <summary>
/// Real terminal using <see cref="Console"/> standard input/output.
///
/// Deviation from TS: Node exposes <c>process.stdin.setRawMode</c>; .NET has no cross-platform raw
/// mode API. On Unix this port shells out to <c>stty raw -echo</c> (restoring <c>stty sane</c> on
/// stop); on Windows it enables virtual-terminal input via P/Invoke. Input is read on a background
/// thread with <see cref="Console.ReadKey(bool)"/> and mapped to the escape sequences the rest of the
/// framework expects.
/// </summary>
public sealed class ProcessTerminal : ITerminal
{
    private const string ProgressActive = "\x1b]9;4;3\x07";
    private const string ProgressClear = "\x1b]9;4;0\x07";
    private const int ProgressKeepAliveMs = 1000;

    private const string BracketedPasteStart = "\x1b[200~";
    private const string BracketedPasteEnd = "\x1b[201~";

    /// <summary>TS <c>DEFAULT_ESCAPE_TIMEOUT_MS</c>: the reassembly window for a lone ESC.</summary>
    private const int DefaultEscapeTimeoutMs = 10;

    /// <summary>TS <c>DEFAULT_SSH_ESCAPE_TIMEOUT_MS</c>: high-latency transports need a wider window.</summary>
    private const int SshEscapeTimeoutMs = 100;

    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private StdinBuffer? _stdinBuffer;
    private Timer? _progressTimer;
    private bool _started;
    private bool _rawModeSet;

    /// <summary>Latest program status, kept across <see cref="Stop"/> so <see cref="Start"/> can report it again.</summary>
    private ProgramStatus? _programStatus;

    /// <summary>Whether the terminal confirmed OSC 7501 support since the last start, or <c>PI_PROGRAM_STATUS=1</c>.</summary>
    private bool _programStatusSupported;

    /// <summary>The support query was sent and neither a reply nor the DA1 sentinel has arrived yet.</summary>
    private bool _programStatusQueryPending;

    public bool KittyProtocolActive => Keys.IsKittyProtocolActive();

    public int Columns => Math.Max(1, Safe(() => Console.WindowWidth, ParseEnv("COLUMNS", 80)));

    public int Rows => Math.Max(1, Safe(() => Console.WindowHeight, ParseEnv("LINES", 24)));

    private static int ParseEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static int Safe(Func<int> get, int fallback)
    {
        try
        {
            var v = get();
            return v > 0 ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void Start(Action<string> onInput, Action onResize)
    {
        _inputHandler = onInput;
        _resizeHandler = onResize;
        _started = true;

        Console.OutputEncoding = Encoding.UTF8;
        EnableRawMode();

        // Split batched input into individual sequences, exactly like TS's `setupStdinBuffer`.
        // `Console.ReadKey` hands escape sequences over one key at a time, so without this buffer a
        // terminal reply (OSC 10/11/4 colors, OSC 7501 program status, …) arrives as a stream of
        // single characters that `Tui.HandleTerminalInput` cannot recognize and that therefore end up
        // typed into the focused editor.
        _stdinBuffer = CreateStdinBuffer();

        // Bracketed paste mode.
        Write("\x1b[?2004h");

        // OSC 7501 support: a supporting terminal echoes the query before anything else arrives.
        // PI_PROGRAM_STATUS=1 claims support without asking, =0 leaves the protocol alone.
        var programStatusOverride = Environment.GetEnvironmentVariable("PI_PROGRAM_STATUS");
        _programStatusSupported = programStatusOverride == "1";
        _programStatusQueryPending = programStatusOverride is not ("1" or "0");
        if (_programStatusQueryPending)
        {
            Write(ProgramStatusProtocol.Query);
        }

        WriteProgramStatus();

        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_readCts.Token));
    }

    private void ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ConsoleKeyInfo keyInfo;
            try
            {
                if (Console.IsInputRedirected)
                {
                    var read = Console.In.Read();
                    if (read < 0)
                    {
                        break;
                    }
                    // A lone surrogate would throw in `ConvertFromUtf32`; feed the raw UTF-16 code unit
                    // instead and let the buffer reassemble it.
                    _stdinBuffer?.Process(((char)read).ToString());
                    continue;
                }
                keyInfo = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            FeedKey(keyInfo);
        }
    }

    /// <summary>
    /// Port of the <c>stdinBuffer.on("data")</c> / <c>on("paste")</c> wiring.
    /// </summary>
    private StdinBuffer CreateStdinBuffer(int? escapeTimeoutMs = null)
    {
        var buffer = new StdinBuffer(new StdinBufferOptions
        {
            EscapeTimeout = escapeTimeoutMs ?? ResolveEscapeTimeoutMs(),
        });
        buffer.Data += ForwardInputSequence;
        // Re-wrap paste content with bracketed paste markers for the existing editor handling; TS does
        // the same in `stdinBuffer.on("paste")`.
        buffer.Paste += content => _inputHandler?.Invoke($"{BracketedPasteStart}{content}{BracketedPasteEnd}");
        return buffer;
    }

    /// <summary>Decode one console key event and feed it through the sequence buffer.</summary>
    private void FeedKey(ConsoleKeyInfo keyInfo)
    {
        var sequence = MapKey(keyInfo);
        if (sequence is not null)
        {
            _stdinBuffer?.Process(sequence);
        }
    }

    /// <summary>
    /// Port of the <c>stdinBuffer.on("data")</c> handler: swallow terminal replies, forward the rest.
    /// </summary>
    private void ForwardInputSequence(string sequence)
    {
        if (ProgramStatusProtocol.IsReply(sequence))
        {
            if (_programStatusQueryPending)
            {
                _programStatusQueryPending = false;
                _programStatusSupported = true;
                WriteProgramStatus();
            }
            return;
        }

        if (_programStatusQueryPending && sequence.StartsWith("\x1b[?", StringComparison.Ordinal))
        {
            // DA1: the terminal does not implement OSC 7501 (or answers it later than DA1).
            _programStatusQueryPending = false;
            return;
        }

        _inputHandler?.Invoke(sequence);
    }

    /// <summary>Port of <c>resolveEscapeTimeoutMs(env)</c>.</summary>
    internal static int ResolveEscapeTimeoutMs(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var configured = getEnv("PI_TUI_ESC_TIMEOUT");
        if (int.TryParse(configured, out var value) && value > 0)
        {
            return value;
        }

        var ssh = getEnv("SSH_CONNECTION");
        if (string.IsNullOrEmpty(ssh))
        {
            ssh = getEnv("SSH_TTY");
        }
        return string.IsNullOrEmpty(ssh) ? DefaultEscapeTimeoutMs : SshEscapeTimeoutMs;
    }

    /// <summary>
    /// Test seam: install the input pipeline without touching the real console. Mirrors what
    /// <see cref="Start"/> does between <c>EnableRawMode</c> and the program-status query.
    /// </summary>
    internal void StartInputPipelineForTests(Action<string> onInput, int? escapeTimeoutMs = null)
    {
        _inputHandler = onInput;
        _stdinBuffer?.Dispose();
        _stdinBuffer = CreateStdinBuffer(escapeTimeoutMs);
    }

    /// <summary>Test seam: feed one decoded console key event, exactly as <see cref="ReadLoop"/> does.</summary>
    internal void FeedKeyForTests(ConsoleKeyInfo keyInfo) => FeedKey(keyInfo);

    /// <summary>Map a <see cref="ConsoleKeyInfo"/> to the terminal escape sequence the framework expects.</summary>
    internal static string? MapKey(ConsoleKeyInfo key)
    {
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return alt ? "\x1bp" : shift ? "\x1b[a" : ctrl ? "\x1bOa" : "\x1b[A";
            case ConsoleKey.DownArrow: return alt ? "\x1bn" : shift ? "\x1b[b" : ctrl ? "\x1bOb" : "\x1b[B";
            case ConsoleKey.RightArrow: return alt ? "\x1bf" : shift ? "\x1b[c" : ctrl ? "\x1bOc" : "\x1b[C";
            case ConsoleKey.LeftArrow: return alt ? "\x1bb" : shift ? "\x1b[d" : ctrl ? "\x1bOd" : "\x1b[D";
            case ConsoleKey.Home: return "\x1b[H";
            case ConsoleKey.End: return "\x1b[F";
            case ConsoleKey.PageUp: return "\x1b[5~";
            case ConsoleKey.PageDown: return "\x1b[6~";
            case ConsoleKey.Insert: return "\x1b[2~";
            case ConsoleKey.Delete: return "\x1b[3~";
            case ConsoleKey.Escape: return "\x1b";
            case ConsoleKey.Enter: return alt ? "\x1b\r" : shift ? "\x1b[13;2u" : "\r";
            case ConsoleKey.Tab: return shift ? "\x1b[Z" : "\t";
            case ConsoleKey.Backspace: return alt ? "\x1b\x7f" : "\x7f";
            case ConsoleKey.Spacebar: return ctrl ? "\x00" : alt ? "\x1b " : " ";
            case ConsoleKey.F1: return "\x1bOP";
            case ConsoleKey.F2: return "\x1bOQ";
            case ConsoleKey.F3: return "\x1bOR";
            case ConsoleKey.F4: return "\x1bOS";
            case ConsoleKey.F5: return "\x1b[15~";
            case ConsoleKey.F6: return "\x1b[17~";
            case ConsoleKey.F7: return "\x1b[18~";
            case ConsoleKey.F8: return "\x1b[19~";
            case ConsoleKey.F9: return "\x1b[20~";
            case ConsoleKey.F10: return "\x1b[21~";
            case ConsoleKey.F11: return "\x1b[23~";
            case ConsoleKey.F12: return "\x1b[24~";
        }

        if (key.KeyChar == '\0')
        {
            return null;
        }

        if (ctrl && key.KeyChar is >= (char)1 and <= (char)26)
        {
            return alt ? $"\x1b{key.KeyChar}" : key.KeyChar.ToString();
        }

        var text = key.KeyChar.ToString();
        if (alt)
        {
            return $"\x1b{text}";
        }
        return text;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;

        if (_progressTimer is not null)
        {
            _progressTimer.Dispose();
            _progressTimer = null;
            Write(ProgressClear);
        }

        Write("\x1b[?2004l");

        // Remove the status while stopped, for example after exit or while suspended. Start() reports it again.
        if (_programStatusSupported && _programStatus is not null)
        {
            Write(ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Clear)));
        }

        _programStatusSupported = false;
        _programStatusQueryPending = false;

        try
        {
            _readCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        // Drop the reassembly buffer (and its flush timer) with the read loop that feeds it.
        _stdinBuffer?.Dispose();
        _stdinBuffer = null;

        RestoreRawMode();
        _inputHandler = null;
        _resizeHandler = null;
    }

    /// <summary>
    /// Last-resort restore for exit paths that skip <see cref="Stop"/>, e.g. a direct
    /// <c>Environment.Exit</c> (#11170). Hard kills and native aborts run no managed code and cannot be
    /// covered.
    /// </summary>
    public void RestoreOnExit(string before, string after)
    {
        var sequence = new StringBuilder(before);
        if (_progressTimer is not null)
        {
            sequence.Append(ProgressClear);
        }
        sequence.Append("\x1b[?2004l");
        if (Keys.IsKittyProtocolActive())
        {
            sequence.Append("\x1b[<u");
        }
        sequence.Append(after);
        try
        {
            // Write() flushes synchronously, which the exit path needs: a buffered write would be lost.
            Write(sequence.ToString());
        }
        catch
        {
            // The terminal may already be gone.
        }
    }

    public async Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        _inputHandler = null;
        var deadline = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(Math.Min(idleMs, Math.Max(0, (int)(deadline - Environment.TickCount64)))).ConfigureAwait(false);
            if (Environment.TickCount64 >= deadline)
            {
                break;
            }
        }
    }

    public void Write(string data)
    {
        Console.Out.Write(data);
        Console.Out.Flush();
    }

    public void MoveBy(int lines)
    {
        if (lines > 0)
        {
            Write($"\x1b[{lines}B");
        }
        else if (lines < 0)
        {
            Write($"\x1b[{-lines}A");
        }
    }

    public void HideCursor() => Write("\x1b[?25l");

    public void ShowCursor() => Write("\x1b[?25h");

    public void ClearLine() => Write("\x1b[K");

    public void ClearFromCursor() => Write("\x1b[J");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void SetTitle(string title) => Write($"\x1b]0;{title}\x07");

    public void SetProgress(bool active)
    {
        if (active)
        {
            Write(ProgressActive);
            _progressTimer ??= new Timer(_ => Write(ProgressActive), null, ProgressKeepAliveMs, ProgressKeepAliveMs);
        }
        else
        {
            _progressTimer?.Dispose();
            _progressTimer = null;
            Write(ProgressClear);
        }
    }

    public void SetProgramStatus(ProgramStatus status)
    {
        _programStatus = status.State == ProgramStatusState.Clear ? null : status;
        if (_programStatusSupported)
        {
            Write(ProgramStatusProtocol.Format(status));
        }
    }

    private void WriteProgramStatus()
    {
        if (_programStatusSupported && _programStatus is not null)
        {
            Write(ProgramStatusProtocol.Format(_programStatus));
        }
    }

    // ------------------------------------------------------------------
    // Raw mode (best effort)
    // ------------------------------------------------------------------

    private void EnableRawMode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            EnableWindowsVirtualTerminalInput();
            return;
        }
        try
        {
            RunStty("raw -echo");
            _rawModeSet = true;
        }
        catch
        {
            // Best effort; interactive input may not work without raw mode.
        }
    }

    private void RestoreRawMode()
    {
        if (_rawModeSet)
        {
            try
            {
                RunStty("sane");
            }
            catch
            {
                // ignore
            }
            _rawModeSet = false;
        }
    }

    private static void RunStty(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stty", args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(2000);
    }

    private void EnableWindowsVirtualTerminalInput()
    {
        try
        {
            const int StdInputHandle = -10;
            const uint EnableVirtualTerminalInput = 0x0200;
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return;
            }
            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, mode | EnableVirtualTerminalInput);
            }
        }
        catch
        {
            // Native helper not available.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

/// <summary>
/// In-memory terminal used by tests and headless rendering. Records all writes and lets tests push
/// input synchronously. Not present in the TS source; it fills the role of the test harnesses that
/// inject a fake <c>Terminal</c> in vitest.
/// </summary>
public sealed class StringTerminal : ITerminal
{
    private readonly StringBuilder _output = new();
    private Action<string>? _inputHandler;

    public StringTerminal(int columns = 80, int rows = 24)
    {
        Columns = columns;
        Rows = rows;
    }

    public int Columns { get; set; }

    public int Rows { get; set; }

    public bool KittyProtocolActive { get; set; }

    public bool Started { get; private set; }

    public string Output => _output.ToString();

    public void ClearOutput() => _output.Clear();

    public void Start(Action<string> onInput, Action onResize)
    {
        _inputHandler = onInput;
        Started = true;
    }

    public void Stop() => Started = false;

    public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        _inputHandler = null;
        return Task.CompletedTask;
    }

    public void Write(string data) => _output.Append(data);

    /// <summary>Simulate terminal input arriving.</summary>
    public void SendInput(string data) => _inputHandler?.Invoke(data);

    /// <summary>Simulate a terminal resize.</summary>
    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public void MoveBy(int lines) => Write(lines > 0 ? $"\x1b[{lines}B" : lines < 0 ? $"\x1b[{-lines}A" : "");

    public void HideCursor() => Write("\x1b[?25l");

    public void ShowCursor() => Write("\x1b[?25h");

    public void ClearLine() => Write("\x1b[K");

    public void ClearFromCursor() => Write("\x1b[J");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void SetTitle(string title) => Write($"\x1b]0;{title}\x07");

    public void SetProgress(bool active) => Write(active ? "\x1b]9;4;3\x07" : "\x1b]9;4;0\x07");

    /// <summary>Every status reported through <see cref="SetProgramStatus"/>, in order.</summary>
    public List<ProgramStatus> ProgramStatuses { get; } = [];

    /// <summary>Records the status and writes it, i.e. behaves like a terminal that supports OSC 7501.</summary>
    public void SetProgramStatus(ProgramStatus status)
    {
        ProgramStatuses.Add(status);
        Write(ProgramStatusProtocol.Format(status));
    }

    /// <summary>Every exit-path restore, in order.</summary>
    public List<string> ExitRestores { get; } = [];

    public void RestoreOnExit(string before, string after)
    {
        ExitRestores.Add(before + "\x1b[?2004l" + after);
        Write(before + "\x1b[?2004l" + after);
    }
}

/// <summary>Session-level terminal helpers (port of <c>terminal.ts</c> free functions).</summary>
public static class TerminalSession
{
    /// <summary>
    /// Whether the current session is Apple Terminal. TS <c>isAppleTerminalSession()</c>:
    /// <c>process.platform === "darwin" &amp;&amp; process.env.TERM_PROGRAM === "Apple_Terminal"</c>.
    /// </summary>
    public static bool IsAppleTerminalSession() =>
        OperatingSystem.IsMacOS()
        && Environment.GetEnvironmentVariable("TERM_PROGRAM") == "Apple_Terminal";
}
