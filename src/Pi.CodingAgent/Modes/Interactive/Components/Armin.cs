using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Armin says hi! The XBM bitmap and its pure accessors (TS <c>armin.ts</c> module scope:
/// <c>ARMIN_WIDTH</c> / <c>ARMIN_HEIGHT</c> / <c>isArminPixel</c> plus the private
/// <c>getChar</c> / <c>buildFinalGrid</c>).
/// </summary>
/// <remarks>
/// The bitmap is 31x36 pixels, LSB first, 1 = background, 0 = foreground, and is packed as
/// <c>height * bytesPerRow</c> = 36 * 4 = 144 bytes. It is rendered as half blocks, so one cell
/// carries two vertical pixels and the visible grid is 31x18.
/// </remarks>
public static class Armin
{
    public const int Width = 31;

    public const int Height = 36;

    private const int BytesPerRow = (Width + 7) / 8;

    /// <summary>Half-block rendering: one character cell per two vertical pixels.</summary>
    public const int DisplayHeight = (Height + 1) / 2;

    private static readonly byte[] Bits =
    [
        0xff, 0xff, 0xff, 0x7f, 0xff, 0xf0, 0xff, 0x7f, 0xff, 0xed, 0xff, 0x7f, 0xff, 0xdb, 0xff, 0x7f,
        0xff, 0xb7, 0xff, 0x7f, 0xff, 0x77, 0xfe, 0x7f, 0x3f, 0xf8, 0xfe, 0x7f, 0xdf, 0xff, 0xfe, 0x7f,
        0xdf, 0x3f, 0xfc, 0x7f, 0x9f, 0xc3, 0xfb, 0x7f, 0x6f, 0xfc, 0xf4, 0x7f, 0xf7, 0x0f, 0xf7, 0x7f,
        0xf7, 0xff, 0xf7, 0x7f, 0xf7, 0xff, 0xe3, 0x7f, 0xf7, 0x07, 0xe8, 0x7f, 0xef, 0xf8, 0x67, 0x70,
        0x0f, 0xff, 0xbb, 0x6f, 0xf1, 0x00, 0xd0, 0x5b, 0xfd, 0x3f, 0xec, 0x53, 0xc1, 0xff, 0xef, 0x57,
        0x9f, 0xfd, 0xee, 0x5f, 0x9f, 0xfc, 0xae, 0x5f, 0x1f, 0x78, 0xac, 0x5f, 0x3f, 0x00, 0x50, 0x6c,
        0x7f, 0x00, 0xdc, 0x77, 0xff, 0xc0, 0x3f, 0x78, 0xff, 0x01, 0xf8, 0x7f, 0xff, 0x03, 0x9c, 0x78,
        0xff, 0x07, 0x8c, 0x7c, 0xff, 0x0f, 0xce, 0x78, 0xff, 0xff, 0xcf, 0x7f, 0xff, 0xff, 0xcf, 0x78,
        0xff, 0xff, 0xdf, 0x78, 0xff, 0xff, 0xdf, 0x7d, 0xff, 0xff, 0x3f, 0x7e, 0xff, 0xff, 0xff, 0x7f,
    ];

    /// <summary>Get pixel at (x, y): true = foreground, false = background.</summary>
    public static bool IsArminPixel(int x, int y)
    {
        if (y >= Height)
        {
            return false;
        }

        var byteIndex = y * BytesPerRow + (x / 8);
        var bitIndex = x % 8;
        return ((Bits[byteIndex] >> bitIndex) & 1) == 0;
    }

    /// <summary>Get the character for a cell (two vertical pixels packed).</summary>
    internal static char GetChar(int x, int row)
    {
        var upper = IsArminPixel(x, row * 2);
        var lower = IsArminPixel(x, row * 2 + 1);
        if (upper && lower) return '█';
        if (upper) return '▀';
        if (lower) return '▄';
        return ' ';
    }

    /// <summary>Build the final image grid (one char per cell).</summary>
    internal static char[][] BuildFinalGrid()
    {
        var grid = new char[DisplayHeight][];
        for (var row = 0; row < DisplayHeight; row++)
        {
            var line = new char[Width];
            for (var x = 0; x < Width; x++)
            {
                line[x] = GetChar(x, row);
            }

            grid[row] = line;
        }

        return grid;
    }
}

/// <summary>The seven reveal animations (TS <c>type Effect</c>).</summary>
public enum ArminEffect
{
    Typewriter,
    Scanline,
    Rain,
    Fade,
    Crt,
    Glitch,
    Dissolve,
}

/// <summary>
/// Animated XBM art component (TS <c>ArminComponent</c>): the effect draws the bitmap cell by cell,
/// then the animation stops itself.
/// </summary>
/// <remarks>
/// <para>
/// Faithful quirk: the <see cref="ArminEffect.Rain"/> effect never terminates. A column only counts as
/// settled once <c>settled &gt;= DisplayHeight</c>, i.e. once its <em>lowest</em> ink pixel is on row 0;
/// this bitmap has no ink in row 0 at all (columns 0 and 30 are blank for their whole height), so
/// <c>allSettled</c> is always false and the 30fps timer keeps running until <see cref="Dispose"/>.
/// The upstream TS behaves the same way — the port reproduces it rather than silently changing it.
/// </para>
/// </remarks>
public sealed class ArminComponent : IComponent, IDisposable
{
    private const string Message = "ARMIN SAYS HI";

    private static readonly ArminEffect[] Effects =
    [
        ArminEffect.Typewriter,
        ArminEffect.Scanline,
        ArminEffect.Rain,
        ArminEffect.Fade,
        ArminEffect.Crt,
        ArminEffect.Glitch,
        ArminEffect.Dissolve,
    ];

    private readonly ITui _ui;
    private readonly Random _random;
    private readonly ArminEffect _effect;
    private readonly char[][] _finalGrid;
    private readonly object _gate = new();

    private char[][] _currentGrid;
    private EffectState _state;
    private Timer? _interval;
    private string[] _cachedLines = [];
    private int _cachedWidth;
    private int _gridVersion;
    private int _cachedVersion = -1;

    public ArminComponent(ITui ui)
        : this(ui, Effects[Random.Shared.Next(Effects.Length)], Random.Shared, startAnimation: true)
    {
    }

    /// <summary>
    /// Test seam: pins the effect and the random source, and can skip starting the timer (TS picks the
    /// effect with <c>Math.random()</c> and starts a <c>setInterval</c> immediately, which makes both
    /// unreachable from a test).
    /// </summary>
    internal ArminComponent(ITui ui, ArminEffect effect, Random random, bool startAnimation)
    {
        _ui = ui;
        _random = random;
        _effect = effect;
        _finalGrid = Armin.BuildFinalGrid();
        _currentGrid = CreateEmptyGrid();
        _state = InitEffect();

        if (startAnimation)
        {
            StartAnimation();
        }
    }

    /// <summary>The chosen reveal animation (TS <c>private effect</c>, readable at runtime).</summary>
    internal ArminEffect Effect => _effect;

    /// <summary>The grid being drawn (TS <c>private currentGrid</c>).</summary>
    internal char[][] CurrentGrid => _currentGrid;

    /// <summary>The completed grid (TS <c>private finalGrid</c>).</summary>
    internal char[][] FinalGrid => _finalGrid;

    /// <summary>Whether the frame timer is still running.</summary>
    internal bool IsAnimating
    {
        get
        {
            lock (_gate)
            {
                return _interval is not null;
            }
        }
    }

    public void Invalidate()
    {
        _cachedWidth = 0;
    }

    public string[] Render(int width)
    {
        lock (_gate)
        {
            if (width == _cachedWidth && _cachedVersion == _gridVersion)
            {
                return _cachedLines;
            }

            var theme = ThemeRuntime.Current;
            const int padding = 1;
            var availableWidth = width - padding;

            var lines = new List<string>(_currentGrid.Length + 1);
            foreach (var row in _currentGrid)
            {
                // Clip the row to the available width before applying color (JS slice semantics).
                var clipped = SliceJoin(row, 0, availableWidth);
                var padRight = Math.Max(0, width - padding - clipped.Length);
                lines.Add($" {theme.Fg(ThemeColors.Accent, clipped)}{new string(' ', padRight)}");
            }

            // Add "ARMIN SAYS HI" at the end.
            var msgPadRight = Math.Max(0, width - padding - Message.Length);
            lines.Add($" {theme.Fg(ThemeColors.Accent, Message)}{new string(' ', msgPadRight)}");

            _cachedLines = [.. lines];
            _cachedWidth = width;
            _cachedVersion = _gridVersion;
            return _cachedLines;
        }
    }

    /// <summary>Advance the animation by one frame; returns true when the effect is done.</summary>
    internal bool TickEffect() => _effect switch
    {
        ArminEffect.Typewriter => TickTypewriter(),
        ArminEffect.Scanline => TickScanline(),
        ArminEffect.Rain => TickRain(),
        ArminEffect.Fade => TickFade(),
        ArminEffect.Crt => TickCrt(),
        ArminEffect.Glitch => TickGlitch(),
        ArminEffect.Dissolve => TickDissolve(),
        _ => true,
    };

    public void Dispose()
    {
        StopAnimation();
    }

    private char[][] CreateEmptyGrid()
    {
        var grid = new char[Armin.DisplayHeight][];
        for (var row = 0; row < grid.Length; row++)
        {
            grid[row] = EmptyRow();
        }

        return grid;
    }

    private static char[] EmptyRow()
    {
        var row = new char[Armin.Width];
        Array.Fill(row, ' ');
        return row;
    }

    private EffectState InitEffect()
    {
        switch (_effect)
        {
            case ArminEffect.Typewriter:
                return new TypewriterState();

            case ArminEffect.Scanline:
                return new ScanlineState();

            case ArminEffect.Rain:
                // Track the falling position for each column.
                var drops = new Drop[Armin.Width];
                for (var x = 0; x < drops.Length; x++)
                {
                    drops[x] = new Drop { Y = -_random.Next(Armin.DisplayHeight * 2), Settled = 0 };
                }

                return new RainState { Drops = drops };

            case ArminEffect.Fade:
                return new PositionCursorState { Positions = ShuffleAllPositions() };

            case ArminEffect.Crt:
                return new CrtState();

            case ArminEffect.Glitch:
                return new GlitchState { Phase = 0, GlitchFrames = 8 };

            case ArminEffect.Dissolve:
                // Start with random noise.
                var noise = new char[Armin.DisplayHeight][];
                char[] chars = [' ', '░', '▒', '▓', '█', '▀', '▄'];
                for (var row = 0; row < noise.Length; row++)
                {
                    var line = new char[Armin.Width];
                    for (var x = 0; x < line.Length; x++)
                    {
                        line[x] = chars[_random.Next(chars.Length)];
                    }

                    noise[row] = line;
                }

                _currentGrid = noise;
                return new PositionCursorState { Positions = ShuffleAllPositions() };

            default:
                return new TypewriterState();
        }
    }

    /// <summary>All (row, x) cells in row-major order, Fisher-Yates shuffled.</summary>
    private (int Row, int X)[] ShuffleAllPositions()
    {
        var positions = new List<(int Row, int X)>(Armin.DisplayHeight * Armin.Width);
        for (var row = 0; row < Armin.DisplayHeight; row++)
        {
            for (var x = 0; x < Armin.Width; x++)
            {
                positions.Add((row, x));
            }
        }

        var items = positions.ToArray();
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    private void StartAnimation()
    {
        var fps = _effect == ArminEffect.Glitch ? 60 : 30;
        // Node's setInterval truncates a fractional delay to whole milliseconds.
        var period = (int)(1000.0 / fps);
        lock (_gate)
        {
            _interval = new Timer(
                _ =>
                {
                    bool done;
                    lock (_gate)
                    {
                        done = TickEffect();
                        UpdateDisplay();
                    }

                    _ui.RequestRender();
                    if (done)
                    {
                        StopAnimation();
                    }
                },
                null,
                period,
                period);
        }
    }

    private void StopAnimation()
    {
        lock (_gate)
        {
            if (_interval is { } interval)
            {
                interval.Dispose();
                _interval = null;
            }
        }
    }

    private bool TickTypewriter()
    {
        var state = (TypewriterState)_state;
        const int pixelsPerFrame = 3;

        for (var i = 0; i < pixelsPerFrame; i++)
        {
            var row = state.Pos / Armin.Width;
            var x = state.Pos % Armin.Width;
            if (row >= Armin.DisplayHeight)
            {
                return true;
            }

            _currentGrid[row][x] = _finalGrid[row][x];
            state.Pos++;
        }

        return false;
    }

    private bool TickScanline()
    {
        var state = (ScanlineState)_state;
        if (state.Row >= Armin.DisplayHeight)
        {
            return true;
        }

        for (var x = 0; x < Armin.Width; x++)
        {
            _currentGrid[state.Row][x] = _finalGrid[state.Row][x];
        }

        state.Row++;
        return false;
    }

    private bool TickRain()
    {
        var state = (RainState)_state;

        var allSettled = true;
        _currentGrid = CreateEmptyGrid();

        for (var x = 0; x < Armin.Width; x++)
        {
            var drop = state.Drops[x];

            // Draw settled pixels.
            for (var row = Armin.DisplayHeight - 1; row >= Armin.DisplayHeight - drop.Settled; row--)
            {
                if (row >= 0)
                {
                    _currentGrid[row][x] = _finalGrid[row][x];
                }
            }

            // Check whether this column is done.
            if (drop.Settled >= Armin.DisplayHeight)
            {
                continue;
            }

            allSettled = false;

            // Find the target row for this column (lowest non-space pixel above the settled block).
            var targetRow = -1;
            for (var row = Armin.DisplayHeight - 1 - drop.Settled; row >= 0; row--)
            {
                if (_finalGrid[row][x] != ' ')
                {
                    targetRow = row;
                    break;
                }
            }

            // Move the drop down.
            drop.Y++;

            // Draw the falling drop.
            if (drop.Y >= 0 && drop.Y < Armin.DisplayHeight)
            {
                if (targetRow >= 0 && drop.Y >= targetRow)
                {
                    // Settle.
                    drop.Settled = Armin.DisplayHeight - targetRow;
                    drop.Y = -_random.Next(5) - 1;
                }
                else
                {
                    // Still falling.
                    _currentGrid[drop.Y][x] = '▓';
                }
            }
        }

        return allSettled;
    }

    private bool TickFade()
    {
        var state = (PositionCursorState)_state;
        const int pixelsPerFrame = 15;

        for (var i = 0; i < pixelsPerFrame; i++)
        {
            if (state.Idx >= state.Positions.Length)
            {
                return true;
            }

            var (row, x) = state.Positions[state.Idx];
            _currentGrid[row][x] = _finalGrid[row][x];
            state.Idx++;
        }

        return false;
    }

    private bool TickCrt()
    {
        var state = (CrtState)_state;
        var midRow = Armin.DisplayHeight / 2;

        _currentGrid = CreateEmptyGrid();

        // Draw from the middle expanding outward.
        var top = midRow - state.Expansion;
        var bottom = midRow + state.Expansion;

        for (var row = Math.Max(0, top); row <= Math.Min(Armin.DisplayHeight - 1, bottom); row++)
        {
            for (var x = 0; x < Armin.Width; x++)
            {
                _currentGrid[row][x] = _finalGrid[row][x];
            }
        }

        state.Expansion++;
        return state.Expansion > Armin.DisplayHeight;
    }

    private bool TickGlitch()
    {
        var state = (GlitchState)_state;

        if (state.Phase < state.GlitchFrames)
        {
            // Glitch phase: show a corrupted version.
            var corrupted = new char[_finalGrid.Length][];
            for (var i = 0; i < _finalGrid.Length; i++)
            {
                var row = _finalGrid[i];
                var offset = _random.Next(7) - 3;

                // Random horizontal offset.
                if (_random.NextDouble() < 0.3)
                {
                    var shifted = SliceJoin(row, offset, null) + SliceJoin(row, 0, offset);
                    corrupted[i] = SliceTake(shifted, Armin.Width);
                    continue;
                }

                // Random vertical swap.
                if (_random.NextDouble() < 0.2)
                {
                    var swapRow = _random.Next(Armin.DisplayHeight);
                    corrupted[i] = [.. _finalGrid[swapRow]];
                    continue;
                }

                corrupted[i] = [.. row];
            }

            _currentGrid = corrupted;
            state.Phase++;
            return false;
        }

        // Final frame: show the clean image.
        _currentGrid = _finalGrid.Select(row => (char[])row.Clone()).ToArray();
        return true;
    }

    private bool TickDissolve()
    {
        var state = (PositionCursorState)_state;
        const int pixelsPerFrame = 20;

        for (var i = 0; i < pixelsPerFrame; i++)
        {
            if (state.Idx >= state.Positions.Length)
            {
                return true;
            }

            var (row, x) = state.Positions[state.Idx];
            _currentGrid[row][x] = _finalGrid[row][x];
            state.Idx++;
        }

        return false;
    }

    private void UpdateDisplay()
    {
        _gridVersion++;
    }

    /// <summary>
    /// JS <c>array.slice(start, end)</c> joined back into a string. Negative indices count from the
    /// end, which the glitch effect relies on (<c>slice(-1)</c> is the last cell).
    /// </summary>
    private static string SliceJoin(char[] row, int start, int? end)
    {
        var (from, to) = JsSliceRange(row.Length, start, end);
        return to <= from ? "" : new string(row, from, to - from);
    }

    /// <summary>JS <c>string.slice(0, count)</c> — also negative-aware — as a char array.</summary>
    private static char[] SliceTake(string value, int count)
    {
        var (from, to) = JsSliceRange(value.Length, 0, count);
        var result = new char[to - from];
        value.CopyTo(from, result, 0, to - from);
        return result;
    }

    /// <summary>Normalize JS <c>slice</c> bounds: negatives count from the end, results are clamped.</summary>
    private static (int From, int To) JsSliceRange(int length, int start, int? end)
    {
        var from = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        int to;
        if (end is null)
        {
            to = length;
        }
        else
        {
            to = end.Value < 0 ? Math.Max(length + end.Value, 0) : Math.Min(end.Value, length);
        }

        return (from, Math.Max(to, from));
    }

    /// <summary>Per-effect animation state (TS's untyped <c>effectState</c> record).</summary>
    private abstract class EffectState;

    private sealed class TypewriterState : EffectState
    {
        public int Pos;
    }

    private sealed class ScanlineState : EffectState
    {
        public int Row;
    }

    private sealed class Drop
    {
        public int Y;
        public int Settled;
    }

    private sealed class RainState : EffectState
    {
        public required Drop[] Drops;
    }

    /// <summary>Shared by <c>fade</c> and <c>dissolve</c>: TS gives both <c>{ positions, idx }</c>.</summary>
    private sealed class PositionCursorState : EffectState
    {
        public required (int Row, int X)[] Positions;
        public int Idx;
    }

    private sealed class CrtState : EffectState
    {
        public int Expansion;
    }

    private sealed class GlitchState : EffectState
    {
        public int Phase;
        public int GlitchFrames;
    }
}
