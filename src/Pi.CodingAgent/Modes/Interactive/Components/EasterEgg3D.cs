using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using TuiColor = Pi.Tui.Color;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>An RGB triple (TS <c>type Rgb = readonly [number, number, number]</c>).</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);

    public static Rgb operator -(Rgb a, Rgb b) => new(a.R - b.R, a.G - b.G, a.B - b.B);

    public static Rgb operator *(Rgb a, double k) => new(a.R * k, a.G * k, a.B * k);
}

/// <summary>One cell of the screen being dissolved (TS <c>ScreenCell</c>).</summary>
internal sealed class ScreenCell
{
    public required string Text { get; set; }

    /// <summary>1 or 2 for a grapheme, 0 for the second column of a wide grapheme.</summary>
    public required int Width { get; set; }

    public required Rgb Fg { get; set; }

    /// <summary>Null when the cell uses the terminal's default background.</summary>
    public Rgb? Bg { get; set; }

    /// <summary>Seconds until the cell turns into braille dust.</summary>
    public double Delay { get; set; }

    /// <summary>Number of braille dots the glyph turns into.</summary>
    public int Ink { get; set; }

    public long Seed { get; set; }
}

/// <summary>A background star (TS <c>Star</c>).</summary>
internal readonly record struct Star(string Glyph, Rgb Tint, double Strength, double Phase, double Speed);

/// <summary>
/// An axis-aligned block in model space (TS <c>Box</c>; renamed because
/// <see cref="Pi.Tui.Components.Box"/> is the TUI container).
/// </summary>
internal sealed class BlockBox
{
    public required double[] Min { get; init; }

    public required double[] Max { get; init; }

    public required Rgb Color { get; init; }
}

/// <summary>Camera pose (TS <c>Pose</c>). Mutable: the exit path rewrites yaw/pitch/roll.</summary>
internal sealed class Pose
{
    public required double CenterX { get; set; }

    public required double CenterY { get; set; }

    /// <summary>Braille dots per model pixel at depth 0.</summary>
    public required double Scale { get; set; }

    public required double Yaw { get; set; }

    public required double Pitch { get; set; }

    public required double Roll { get; set; }
}

/// <summary>Grid position of a block: column and row in the bitmap, and layer (-1, 0, 1) in depth.</summary>
public readonly record struct Cell3(int X, int Y, int Z);

public readonly record struct ModelBlock(Cell3 Home, Rgb Color);

/// <summary>A bitmap built from one block per foreground pixel (TS <c>Model</c>).</summary>
public sealed class EasterEggModel
{
    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required List<ModelBlock> Blocks { get; init; }

    /// <summary>Camera distance from the model's center, in pixels.</summary>
    public required double CameraDistance { get; init; }

    /// <summary>Farthest distance of any block corner from the center.</summary>
    public required double Radius { get; init; }

    /// <summary>Largest share of the screen width the spinning model covers.</summary>
    public required double WidthShare { get; init; }

    /// <summary>Number of blocks the puzzle moves per step, given a random number from 0 to 1.</summary>
    public required Func<double, int> PuzzleMoves { get; init; }

    /// <summary>
    /// Top-left cell of the model's half-block rendering on screen. The model lifts off from there and
    /// replaces it. Null to grow out of the center.
    /// </summary>
    public (int Column, int Row)? Origin { get; init; }
}

/// <summary>
/// Fullscreen 3D easter eggs (TS <c>easter-egg-3d.ts</c>): the pi logo lifted off the header logo and
/// Armin. Both are bitmaps built from one block per pixel; the current screen dissolves into braille
/// dust while the model spins in the center and its blocks play a sliding puzzle. Leaving plays the
/// same timeline backwards, so the screen reassembles.
/// </summary>
internal static partial class EasterEgg3D
{
    internal const double Depth = 0.7;
    private const double FlyStart = 0.1;
    private const double FlyDuration = 1.3;

    /// <summary>Braille dots per pixel when a model without an origin appears, growing from a speck.</summary>
    private const double StartScale = 0.1;

    private static readonly Rgb Coral = new(228, 138, 122);
    private static readonly Rgb Blue = new(79, 142, 179);
    private static readonly Rgb Yellow = new(234, 182, 93);

    private static readonly string[] PiLogoPixels = ["ccc.", "b.c.", "bb.y", "b..y"];

    private static readonly Dictionary<char, Rgb> PiLogoColors = new()
    {
        ['c'] = Coral,
        ['b'] = Blue,
        ['y'] = Yellow,
    };

    // The sliding puzzle: after the model spun for a while, blocks slide into free neighboring
    // cells, a few at a time. Each cycle shuffles, flies every block back home, and holds briefly.
    private const double PuzzleStart = FlyStart + FlyDuration + 3;
    private const double PuzzleStep = 0.3;
    private const int PuzzleSteps = 12;
    private const double PuzzleReturn = 1;
    private const double PuzzleHold = 0.6;
    internal const double PuzzleCycle = PuzzleStep * PuzzleSteps + PuzzleReturn + PuzzleHold;

    private static readonly Cell3[] PuzzleMoves =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    ];

    internal const double FrameMs = 1000.0 / 30;
    private const double DustDuration = 0.35;
    private const double WaveSpread = 0.55;
    private const double WaveJitter = 0.12;
    internal const double DissolveEnd = FlyStart + WaveSpread + WaveJitter + DustDuration;
    private const double BackgroundFade = 0.5;
    private const double ExitDuration = 1.1;

    // A faint starfield fades in behind the logo once the screen has dissolved.
    private const double StarsStart = 2.5;
    private const double StarsFade = 2;
    private const double StarDensity = 0.018;

    // Star tints: neutral, pale blue, pale gold, and pale rose.
    internal static readonly Rgb[] StarTints =
    [
        new(255, 255, 255),
        new(170, 195, 255),
        new(255, 225, 170),
        new(255, 190, 205),
    ];

    internal const double LogoColorStep = 5;

    // On light backgrounds most of each braille cell shows the bright background, which washes the
    // logo out. There the logo gets a halo blurred over HALO_RADIUS_X columns / HALO_RADIUS_Y rows.
    private const double LightHaloStrength = 0.3;
    internal const int HaloRadiusX = 6;
    internal const int HaloRadiusY = 3;
    internal const int HaloLevels = 32;
    private const int StarAlphaLevels = 8;
    private const int StarFlickerLevels = 3;
    private const double HintFade = 0.5;

    /// <summary>Braille dot bits, indexed by <c>row * 2 + column</c> within the 2x4 cell.</summary>
    internal static readonly byte[] DotBits = [0x01, 0x08, 0x02, 0x10, 0x04, 0x20, 0x40, 0x80];

    internal static readonly string[] Braille = BuildBraille();

    internal static readonly (double X, double Y, double Z) Light = Normalize(-0.45, -0.6, 0.75);

    internal static readonly (double X, double Y, double Z) HalfVector =
        Normalize(Light.X, Light.Y, Light.Z + 1);

    private static string[] BuildBraille()
    {
        var table = new string[256];
        for (var bits = 0; bits < table.Length; bits++)
        {
            table[bits] = ((char)(0x2800 + bits)).ToString();
        }

        return table;
    }

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var length = Hypot(x, y, z);
        return (x / length, y / length, z / length);
    }

    internal static double Hypot(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);

    internal static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>
    /// JS <c>Math.round</c> (half away from zero); C# <see cref="Math.Round(double)"/> rounds halves to
    /// even, which would flip the quantized star/color levels at exact halves.
    /// </summary>
    internal static double JsRound(double value) => Math.Floor(value + 0.5);

    /// <summary>Quintic smoothstep.</summary>
    internal static double Smooth(double value)
    {
        var t = Clamp01(value);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    internal static double EaseOut(double value)
    {
        var t = 1 - Clamp01(value);
        return 1 - t * t * t;
    }

    internal static Rgb Mix(Rgb a, Rgb b, double amount) =>
        new(a.R + (b.R - a.R) * amount, a.G + (b.G - a.G) * amount, a.B + (b.B - a.B) * amount);

    /// <summary>
    /// TS <c>hash(value)</c>. The JS operators coerce to int32 (and <c>Math.imul</c> is a 32-bit
    /// multiply), so the argument is truncated to its low 32 bits first, exactly like <c>^</c>.
    /// </summary>
    internal static double Hash(long value)
    {
        unchecked
        {
            var x = (int)value ^ 0x9e3779b9;
            x *= 0x85ebca6b;
            x ^= (int)((uint)x >> 13);
            x *= 0xc2b2ae35;
            x ^= (int)((uint)x >> 16);
            return (uint)x / 4294967296.0;
        }
    }

    /// <summary>Whether a background is light, by relative luminance.</summary>
    internal static bool IsLight(Rgb color) => 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 128;

    internal static Rgb ToRgb(TuiColor color)
    {
        var rgb = Colors.ColorToRgb(color);
        return new Rgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>Braille dots a glyph turns into, a rough measure of how much ink it has.</summary>
    internal static int GlyphInk(string text, double seed)
    {
        if (text.Trim().Length == 0)
        {
            return 0;
        }

        if (text.Length == 1 && ".,:;'`-_·".Contains(text[0], StringComparison.Ordinal))
        {
            return 2;
        }

        if (text.Length == 1 && text[0] >= '\u2500' && text[0] <= '\u257f')
        {
            return 3;
        }

        return 4 + (int)Math.Floor(seed * 3);
    }

    private static EasterEggModel CreateModel(
        int columns,
        int rows,
        Func<int, int, Rgb?> pixel,
        double cameraDistance,
        double widthShare,
        Func<double, int, int> puzzleMoves,
        (int Column, int Row)? origin)
    {
        var blocks = new List<ModelBlock>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (pixel(column, row) is { } color)
                {
                    blocks.Add(new ModelBlock(new Cell3(column, row, 0), color));
                }
            }
        }

        return new EasterEggModel
        {
            Columns = columns,
            Rows = rows,
            Blocks = blocks,
            CameraDistance = cameraDistance,
            Radius = Hypot(columns / 2.0, rows / 2.0, 1 + Depth / 2),
            WidthShare = widthShare,
            PuzzleMoves = random => puzzleMoves(random, blocks.Count),
            Origin = origin,
        };
    }

    internal static EasterEggModel PiLogoModel(int column, int row) =>
        CreateModel(
            4,
            4,
            // TS indexes a plain object: a '.' lookup yields undefined, i.e. no block.
            (c, r) => PiLogoColors.TryGetValue(PiLogoPixels[r][c], out var color) ? color : null,
            10,
            0.35,
            (random, _) => random < 0.4 ? 2 : 1,
            (column, row));

    internal static EasterEggModel ArminModel(Rgb color) =>
        CreateModel(
            Armin.Width,
            Armin.Height,
            (column, row) => Armin.IsArminPixel(column, row) ? color : null,
            // Far enough that the perspective stays mild for a figure about 36 blocks tall.
            80,
            0.45,
            (random, blockCount) => (int)EasterEgg3D.JsRound(blockCount * (0.03 + random * 0.04)),
            null);

    /// <summary>
    /// Block positions after each step of one shuffle cycle, starting at home. Deterministic per cycle.
    /// </summary>
    internal static List<Cell3[]> ShuffleSteps(EasterEggModel model, int cycle)
    {
        var columns = model.Columns;
        var rows = model.Rows;
        var positions = model.Blocks.Select(block => block.Home).ToArray();
        var steps = new List<Cell3[]> { positions };
        var lastMoved = new HashSet<int>();
        var random = cycle * 7919L + 1;
        double Next() => Hash(random++);
        int Key(Cell3 cell) => ((cell.Z + 1) * rows + cell.Y) * columns + cell.X;

        for (var step = 0; step < PuzzleSteps; step++)
        {
            var occupied = new HashSet<int>(positions.Select(Key));
            var nextPositions = (Cell3[])positions.Clone();
            var moved = new HashSet<int>();
            var moveCount = model.PuzzleMoves(Next());
            for (var move = 0; move < moveCount; move++)
            {
                var candidates = new List<(int Block, Cell3 Target)>();
                for (var block = 0; block < positions.Length; block++)
                {
                    if (moved.Contains(block))
                    {
                        continue;
                    }

                    var current = positions[block];
                    foreach (var (dx, dy, dz) in PuzzleMoves)
                    {
                        var target = new Cell3(current.X + dx, current.Y + dy, current.Z + dz);
                        if (target.X < 0 || target.X >= columns || target.Y < 0 || target.Y >= rows)
                        {
                            continue;
                        }

                        if (target.Z < -1 || target.Z > 1 || occupied.Contains(Key(target)))
                        {
                            continue;
                        }

                        candidates.Add((block, target));
                    }
                }

                // Prefer blocks that did not just move, so the puzzle does not look like one block jittering.
                var fresh = candidates.Where(candidate => !lastMoved.Contains(candidate.Block)).ToList();
                var pool = fresh.Count > 0 ? fresh : candidates;
                if (pool.Count == 0)
                {
                    break;
                }

                var choice = pool[(int)Math.Floor(Next() * pool.Count)];
                occupied.Add(Key(choice.Target));
                nextPositions[choice.Block] = choice.Target;
                moved.Add(choice.Block);
            }

            lastMoved.Clear();
            foreach (var block in moved)
            {
                lastMoved.Add(block);
            }

            positions = nextPositions;
            steps.Add(positions);
        }

        return steps;
    }

    // The spin is locked to the puzzle: one turn per cycle, facing the camera mid-hold.
    private const double SpinRamp = 1.4;
    private const double SpinSpeed = Math.PI * 2 / PuzzleCycle;

    /// <summary>How much the spin slows while facing the camera (and speeds up facing away), 0..1.</summary>
    private const double SpinLinger = 0.6;

    private const double FirstFrontView = PuzzleStart + PuzzleStep * PuzzleSteps + PuzzleReturn + PuzzleHold / 2;

    /// <summary>Uniform spin that accelerates from rest over <see cref="SpinRamp"/>, then turns at <see cref="SpinSpeed"/>.</summary>
    internal static double BaseSpinPhase(double time)
    {
        var elapsed = time - FlyStart;
        if (elapsed <= 0)
        {
            return 0;
        }

        var u = elapsed / SpinRamp;
        // Integral of the quintic smoothstep ramp.
        if (u < 1)
        {
            return SpinSpeed * SpinRamp * (Math.Pow(u, 6) - 3 * Math.Pow(u, 5) + 2.5 * Math.Pow(u, 4));
        }

        return SpinSpeed * (SpinRamp * 0.5 + elapsed - SpinRamp);
    }

    /// <summary>Extra rotation during the flight, so the phase is whole turns at every assembled front view.</summary>
    private static readonly double SpinAlignment =
        Math.PI * 2 - BaseSpinPhase(FirstFrontView) % (Math.PI * 2);

    /// <summary>Spin phase: whole turns exactly when the reassembled logo faces the camera.</summary>
    internal static double SpinPhase(double time) =>
        BaseSpinPhase(time) + SpinAlignment * Smooth((time - FlyStart) / FlyDuration);

    /// <summary>Yaw of the logo; matches the phase at whole turns but lingers there.</summary>
    internal static double SpinAngle(double time)
    {
        var phase = SpinPhase(time);
        return phase - SpinLinger * Math.Sin(phase);
    }

    /// <summary>Row-major 3x3 rotation matrix for yaw (y), then pitch (x), then roll (z).</summary>
    internal static double[] Rotation(double yaw, double pitch, double roll)
    {
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw);
        double sx = Math.Sin(pitch), cx = Math.Cos(pitch);
        double sz = Math.Sin(roll), cz = Math.Cos(roll);
        // Rz * Rx * Ry
        double[] ry = [cy, 0, sy, 0, 1, 0, -sy, 0, cy];
        double[] rx = [1, 0, 0, 0, cx, -sx, 0, sx, cx];
        double[] rz = [cz, -sz, 0, sz, cz, 0, 0, 0, 1];
        return Multiply(rz, Multiply(rx, ry));
    }

    internal static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result[row * 3 + column] =
                    a[row * 3] * b[column] + a[row * 3 + 1] * b[3 + column] + a[row * 3 + 2] * b[6 + column];
            }
        }

        return result;
    }

    /// <summary>
    /// Parse rendered lines into cells with resolved colors. Other escape sequences (OSC, APC, cursor)
    /// are skipped.
    /// </summary>
    internal static List<ScreenCell>[] ParseScreen(
        IReadOnlyList<string> lines,
        int width,
        Rgb foreground,
        Rgb background)
    {
        Rgb Palette(int index) => ToRgb(Colors.Indexed(index));
        var parsed = new List<ScreenCell>[lines.Count];
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var cells = new List<ScreenCell>();
            Rgb? fg = null;
            Rgb? bg = null;
            var dim = false;
            var inverse = false;

            void ApplySgr(string parameters)
            {
                var codes = parameters.Length == 0 ? ["0"] : parameters.Split(';');
                for (var i = 0; i < codes.Length; i++)
                {
                    var parts = codes[i].Split(':')
                        .Select(part => int.TryParse(part, out var value) ? value : int.MinValue)
                        .ToArray();
                    var code = parts.Length > 0 && parts[0] != int.MinValue ? parts[0] : 0;
                    if (code is 38 or 48)
                    {
                        // Extended color, either as `38;5;n` / `38;2;r;g;b` or colon sub-parameters.
                        int[] args;
                        if (parts.Length > 1)
                        {
                            args = parts.Skip(1).Where(part => part != int.MinValue).ToArray();
                            // `38:2::r:g:b` has an empty color space id, dropped above.
                        }
                        else
                        {
                            var kind = i + 1 < codes.Length && int.TryParse(codes[i + 1], out var parsedKind)
                                ? parsedKind
                                : int.MinValue;
                            var count = kind == 5 ? 2 : kind == 2 ? 4 : 1;
                            args = codes.Skip(i + 1).Take(count)
                                .Select(part => int.TryParse(part, out var value) ? value : int.MinValue)
                                .ToArray();
                            i += count;
                        }

                        Rgb? color = null;
                        if (args.Length > 1 && args[0] == 5)
                        {
                            color = Palette(args[1]);
                        }
                        else if (args.Length >= 4 && args[0] == 2)
                        {
                            color = new Rgb(args[1], args[2], args[3]);
                        }

                        if (color is { } resolved)
                        {
                            if (code == 38)
                            {
                                fg = resolved;
                            }
                            else
                            {
                                bg = resolved;
                            }
                        }
                    }
                    else if (code == 0)
                    {
                        fg = null;
                        bg = null;
                        dim = false;
                        inverse = false;
                    }
                    else if (code == 2)
                    {
                        dim = true;
                    }
                    else if (code == 22)
                    {
                        dim = false;
                    }
                    else if (code == 7)
                    {
                        inverse = true;
                    }
                    else if (code == 27)
                    {
                        inverse = false;
                    }
                    else if (code is >= 30 and <= 37)
                    {
                        fg = Palette(code - 30);
                    }
                    else if (code is >= 90 and <= 97)
                    {
                        fg = Palette(code - 90 + 8);
                    }
                    else if (code == 39)
                    {
                        fg = null;
                    }
                    else if (code is >= 40 and <= 47)
                    {
                        bg = Palette(code - 40);
                    }
                    else if (code is >= 100 and <= 107)
                    {
                        bg = Palette(code - 100 + 8);
                    }
                    else if (code == 49)
                    {
                        bg = null;
                    }
                }
            }

            void PushText(string text)
            {
                foreach (var (_, segment) in UnicodeWidth.GraphemesWithIndex(text))
                {
                    var glyphWidth = UnicodeWidth.VisibleWidth(segment);
                    if (glyphWidth == 0 || cells.Count + glyphWidth > width)
                    {
                        continue;
                    }

                    var cellFg = fg ?? foreground;
                    var cellBg = bg;
                    if (inverse)
                    {
                        cellFg = bg ?? background;
                        cellBg = fg ?? foreground;
                    }

                    if (dim)
                    {
                        cellFg = Mix(cellFg, cellBg ?? background, 0.4);
                    }

                    var cell = new ScreenCell
                    {
                        Text = segment,
                        Width = glyphWidth,
                        Fg = cellFg,
                        Bg = cellBg,
                    };
                    cells.Add(cell);
                    if (glyphWidth == 2)
                    {
                        cells.Add(new ScreenCell { Text = "", Width = 0, Fg = cellFg, Bg = cellBg });
                    }
                }
            }

            var index = 0;
            while (index < line.Length)
            {
                var sequenceStart = line.IndexOf('\x1b', index);
                if (sequenceStart == -1)
                {
                    PushText(line[index..]);
                    break;
                }

                if (sequenceStart > index)
                {
                    PushText(line[index..sequenceStart]);
                }

                var kind = sequenceStart + 1 < line.Length ? line[sequenceStart + 1] : '\0';
                if (kind == '[')
                {
                    var end = sequenceStart + 2;
                    while (end < line.Length && (line[end] < 0x40 || line[end] > 0x7e))
                    {
                        end++;
                    }

                    if (end < line.Length && line[end] == 'm')
                    {
                        ApplySgr(line[(sequenceStart + 2)..end]);
                    }

                    index = end + 1;
                }
                else if (kind is ']' or '_' or 'P' or '^')
                {
                    // String sequences end at BEL or ST (ESC \).
                    var bell = line.IndexOf('\x07', sequenceStart + 2);
                    var st = line.IndexOf("\x1b\\", sequenceStart + 2, StringComparison.Ordinal);
                    if (bell == -1 && st == -1)
                    {
                        break;
                    }

                    index = bell != -1 && (st == -1 || bell < st) ? bell + 1 : st + 2;
                }
                else
                {
                    index = sequenceStart + 2;
                }
            }

            parsed[lineIndex] = cells;
        }

        return parsed;
    }
}

/// <summary>A screen-space face of a block (TS <c>Face</c>).</summary>
internal sealed class Face
{
    public required int Axis { get; init; }

    /// <summary>Plane coordinate on <see cref="Axis"/> in object space.</summary>
    public required double Plane { get; init; }

    public required double UMin { get; init; }

    public required double UMax { get; init; }

    public required double VMin { get; init; }

    public required double VMax { get; init; }

    /// <summary>Projected bounds in braille dots, inclusive.</summary>
    public required int MinX { get; init; }

    public required int MinY { get; init; }

    public required int MaxX { get; init; }

    public required int MaxY { get; init; }

    public required double Red { get; init; }

    public required double Green { get; init; }

    public required double Blue { get; init; }
}

/// <summary>
/// Renders the model's blocks into braille cells. Only faces that point at the camera and are not
/// covered by a touching block are drawn. Each face is rasterized over its projected bounds by
/// intersecting each dot's ray with the face's plane, with a depth buffer resolving overlaps.
/// </summary>
internal sealed class BlockRaster(double cameraDistance)
{
    /// <summary>Braille dot bits per cell.</summary>
    public byte[] Bits = [];

    /// <summary>Lit dots per cell.</summary>
    public byte[] Counts = [];

    /// <summary>Summed RGB of the lit dots per cell.</summary>
    public float[] Rgb = [];

    /// <summary>Halo tint strength per cell, 0..1. Only set when a halo was requested.</summary>
    public float[] HaloAmount = [];

    /// <summary>Halo color per cell.</summary>
    public float[] HaloRgb = [];

    private int _width;
    private int _height;

    /// <summary>Ray parameter of the nearest hit per dot; smaller is nearer.</summary>
    private float[] _depth = [];

    /// <summary>Face index + 1 of the nearest hit per dot, 0 for none.</summary>
    private ushort[] _faceIds = [];

    /// <summary>Cells written by the previous frame, cleared before the next one.</summary>
    private (int MinX, int MinY, int MaxX, int MaxY)? _dirty;

    /// <summary>Cells with a halo from the previous frame, cleared before the next one.</summary>
    private (int MinX, int MinY, int MaxX, int MaxY)? _haloDirty;

    private readonly double _cameraDistance = cameraDistance;

    public void Render(
        int width,
        int height,
        Pose pose,
        IReadOnlyList<BlockBox> boxes,
        Rgb background,
        double haloStrength)
    {
        var dotWidth = width * 2;
        var dotHeight = height * 4;
        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            Bits = new byte[width * height];
            Counts = new byte[width * height];
            Rgb = new float[width * height * 3];
            HaloAmount = new float[width * height];
            HaloRgb = new float[width * height * 3];
            _haloDirty = null;
            _depth = new float[dotWidth * dotHeight];
            Array.Fill(_depth, float.PositiveInfinity);
            _faceIds = new ushort[dotWidth * dotHeight];
            _dirty = null;
        }
        else if (_dirty is { } dirty)
        {
            for (var row = dirty.MinY; row <= dirty.MaxY; row++)
            {
                var start = row * width + dirty.MinX;
                var count = dirty.MaxX - dirty.MinX + 1;
                Array.Clear(Bits, start, count);
                Array.Clear(Counts, start, count);
                Array.Clear(Rgb, start * 3, count * 3);
            }

            _dirty = null;
        }

        if (_haloDirty is { } haloDirty)
        {
            for (var row = haloDirty.MinY; row <= haloDirty.MaxY; row++)
            {
                var start = row * width + haloDirty.MinX;
                var count = haloDirty.MaxX - haloDirty.MinX + 1;
                Array.Clear(HaloAmount, start, count);
                Array.Clear(HaloRgb, start * 3, count * 3);
            }

            _haloDirty = null;
        }

        var m = EasterEgg3D.Rotation(pose.Yaw, pose.Pitch, pose.Roll);
        var centerX = pose.CenterX;
        var centerY = pose.CenterY;
        var scale = pose.Scale;
        // The camera sits at (0, 0, cameraDistance) in camera space; object space is the transpose rotation.
        double[] origin = [m[6] * _cameraDistance, m[7] * _cameraDistance, m[8] * _cameraDistance];
        var light = EasterEgg3D.IsLight(background);
        var faces = VisibleFaces(m, origin, pose, boxes, dotWidth, dotHeight, light);
        if (faces.Count == 0)
        {
            return;
        }

        var minX = dotWidth;
        var minY = dotHeight;
        var maxX = -1;
        var maxY = -1;
        foreach (var face in faces)
        {
            minX = Math.Min(minX, face.MinX);
            minY = Math.Min(minY, face.MinY);
            maxX = Math.Max(maxX, face.MaxX);
            maxY = Math.Max(maxY, face.MaxY);
        }

        if (maxX < minX || maxY < minY)
        {
            return;
        }

        var depth = _depth;
        var faceIds = _faceIds;
        for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            var face = faces[faceIndex];
            var a = face.Axis;
            var u = (a + 1) % 3;
            var v = (a + 2) % 3;
            var originA = origin[a];
            var originU = origin[u];
            var originV = origin[v];
            // The ray direction in object space is linear in the dot position, so it is stepped per dot.
            var stepA = m[a] / scale;
            var stepU = m[u] / scale;
            var stepV = m[v] / scale;
            var sx = (face.MinX + 0.5 - centerX) / scale;
            for (var dotY = face.MinY; dotY <= face.MaxY; dotY++)
            {
                var sy = (dotY + 0.5 - centerY) / scale;
                var directionA = m[a] * sx + m[3 + a] * sy - m[6 + a] * _cameraDistance;
                var directionU = m[u] * sx + m[3 + u] * sy - m[6 + u] * _cameraDistance;
                var directionV = m[v] * sx + m[3 + v] * sy - m[6 + v] * _cameraDistance;
                var index = dotY * dotWidth + face.MinX;
                for (var dotX = face.MinX; dotX <= face.MaxX; dotX++, index++)
                {
                    var t = (face.Plane - originA) / directionA;
                    if (t > 0 && t < depth[index])
                    {
                        var hitU = originU + t * directionU;
                        var hitV = originV + t * directionV;
                        if (hitU >= face.UMin && hitU <= face.UMax && hitV >= face.VMin && hitV <= face.VMax)
                        {
                            depth[index] = (float)t;
                            faceIds[index] = (ushort)(faceIndex + 1);
                        }
                    }

                    directionA += stepA;
                    directionU += stepU;
                    directionV += stepV;
                }
            }
        }

        // Shade the hit dots, pack them into braille cells, and reset the dot buffers for the next frame.
        var bits = Bits;
        var counts = Counts;
        var rgb = Rgb;
        var cellMinX = width;
        var cellMinY = height;
        var cellMaxX = -1;
        var cellMaxY = -1;
        for (var dotY = minY; dotY <= maxY; dotY++)
        {
            var index = dotY * dotWidth + minX;
            for (var dotX = minX; dotX <= maxX; dotX++, index++)
            {
                var id = faceIds[index];
                if (id == 0)
                {
                    continue;
                }

                var face = faces[id - 1];
                // Points farther from the camera fade slightly toward the background for depth.
                var fog = EasterEgg3D.Clamp01(0.15 - _cameraDistance * (1 - depth[index]) * 0.12) *
                          (light ? 0.5 : 1);
                faceIds[index] = 0;
                depth[index] = float.PositiveInfinity;
                var cellX = dotX >> 1;
                var cellY = dotY >> 2;
                var cell = cellY * width + cellX;
                bits[cell] |= EasterEgg3D.DotBits[((dotY & 3) * 2) + (dotX & 1)];
                counts[cell]++;
                rgb[cell * 3] += (float)(face.Red + (background.R - face.Red) * fog);
                rgb[(cell * 3) + 1] += (float)(face.Green + (background.G - face.Green) * fog);
                rgb[(cell * 3) + 2] += (float)(face.Blue + (background.B - face.Blue) * fog);
                if (cellX < cellMinX) cellMinX = cellX;
                if (cellX > cellMaxX) cellMaxX = cellX;
                if (cellY < cellMinY) cellMinY = cellY;
                if (cellY > cellMaxY) cellMaxY = cellY;
            }
        }

        if (cellMaxX >= 0)
        {
            _dirty = (cellMinX, cellMinY, cellMaxX, cellMaxY);
        }

        if (haloStrength > 0)
        {
            RenderHalo(haloStrength);
        }
    }

    /// <summary>
    /// Blur the logo's coverage and color over neighboring cells with a separable tent filter, so the
    /// tint falls off smoothly past the logo's edges.
    /// </summary>
    private void RenderHalo(double strength)
    {
        if (_dirty is not { } cells)
        {
            return;
        }

        var width = _width;
        var height = _height;
        var counts = Counts;
        var rgb = Rgb;
        var minX = Math.Max(0, cells.MinX - EasterEgg3D.HaloRadiusX);
        var maxX = Math.Min(width - 1, cells.MaxX + EasterEgg3D.HaloRadiusX);
        var minY = Math.Max(0, cells.MinY - EasterEgg3D.HaloRadiusY);
        var maxY = Math.Min(height - 1, cells.MaxY + EasterEgg3D.HaloRadiusY);
        var regionWidth = maxX - minX + 1;

        static float[] Tent(int radius)
        {
            var weights = new float[(radius * 2) + 1];
            var total = 0.0;
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = radius + 1 - Math.Abs(i - radius);
                total += weights[i];
            }

            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = (float)(weights[i] / total);
            }

            return weights;
        }

        var weightsX = Tent(EasterEgg3D.HaloRadiusX);
        var weightsY = Tent(EasterEgg3D.HaloRadiusY);

        // Horizontal pass over the rows that contain the logo: coverage and premultiplied color per cell.
        var rows = cells.MaxY - cells.MinY + 1;
        var horizontal = new float[rows * regionWidth * 4];
        for (var y = cells.MinY; y <= cells.MaxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                float coverage = 0;
                float red = 0;
                float green = 0;
                float blue = 0;
                for (var dx = -EasterEgg3D.HaloRadiusX; dx <= EasterEgg3D.HaloRadiusX; dx++)
                {
                    var sourceX = x + dx;
                    if (sourceX < cells.MinX || sourceX > cells.MaxX)
                    {
                        continue;
                    }

                    var source = y * width + sourceX;
                    var count = counts[source];
                    if (count == 0)
                    {
                        continue;
                    }

                    var weight = weightsX[dx + EasterEgg3D.HaloRadiusX] / 8;
                    coverage += count * weight;
                    red += rgb[source * 3] * weight;
                    green += rgb[(source * 3) + 1] * weight;
                    blue += rgb[(source * 3) + 2] * weight;
                }

                var target = (((y - cells.MinY) * regionWidth) + (x - minX)) * 4;
                horizontal[target] = coverage;
                horizontal[target + 1] = red;
                horizontal[target + 2] = green;
                horizontal[target + 3] = blue;
            }
        }

        // Vertical pass into the halo buffers.
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                float coverage = 0;
                float red = 0;
                float green = 0;
                float blue = 0;
                for (var dy = -EasterEgg3D.HaloRadiusY; dy <= EasterEgg3D.HaloRadiusY; dy++)
                {
                    var sourceY = y + dy;
                    if (sourceY < cells.MinY || sourceY > cells.MaxY)
                    {
                        continue;
                    }

                    var weight = weightsY[dy + EasterEgg3D.HaloRadiusY];
                    var source = (((sourceY - cells.MinY) * regionWidth) + (x - minX)) * 4;
                    coverage += horizontal[source] * weight;
                    red += horizontal[source + 1] * weight;
                    green += horizontal[source + 2] * weight;
                    blue += horizontal[source + 3] * weight;
                }

                var target = y * width + x;
                // Cells under the logo keep at least their own coverage, so faces stay solidly tinted.
                var count = counts[target];
                if (count > 0 && count / 8.0 > coverage)
                {
                    coverage = (float)(count / 8.0);
                    red = rgb[target * 3] / count * coverage;
                    green = rgb[(target * 3) + 1] / count * coverage;
                    blue = rgb[(target * 3) + 2] / count * coverage;
                }

                if (coverage <= 0)
                {
                    continue;
                }

                // Quantized so neighboring cells usually share a background and its escape sequence.
                var amount = EasterEgg3D.JsRound(strength * Math.Pow(Math.Min(1, coverage), 0.7) * EasterEgg3D.HaloLevels) /
                             EasterEgg3D.HaloLevels;
                if (amount <= 0)
                {
                    continue;
                }

                HaloAmount[target] = (float)amount;
                HaloRgb[target * 3] =
                    (float)(EasterEgg3D.JsRound(red / coverage / EasterEgg3D.LogoColorStep) * EasterEgg3D.LogoColorStep);
                HaloRgb[(target * 3) + 1] =
                    (float)(EasterEgg3D.JsRound(green / coverage / EasterEgg3D.LogoColorStep) * EasterEgg3D.LogoColorStep);
                HaloRgb[(target * 3) + 2] =
                    (float)(EasterEgg3D.JsRound(blue / coverage / EasterEgg3D.LogoColorStep) * EasterEgg3D.LogoColorStep);
            }
        }

        _haloDirty = (minX, minY, maxX, maxY);
    }

    private List<Face> VisibleFaces(
        double[] m,
        double[] origin,
        Pose pose,
        IReadOnlyList<BlockBox> boxes,
        int dotWidth,
        int dotHeight,
        bool lightBackground)
    {
        var faces = new List<Face>();
        var cameraDistance = _cameraDistance;
        foreach (var box in boxes)
        {
            for (var a = 0; a < 3; a++)
            {
                var u = (a + 1) % 3;
                var v = (a + 2) % 3;
                foreach (var side in new[] { -1, 1 })
                {
                    var plane = side > 0 ? box.Max[a] : box.Min[a];
                    // Back faces point away from the camera.
                    if ((origin[a] - plane) * side <= 0)
                    {
                        continue;
                    }

                    // Faces pressed against a neighboring block are hidden. Positions are exact while blocks rest.
                    var covered = boxes.Any(other =>
                        !ReferenceEquals(other, box) &&
                        (side > 0 ? other.Min[a] : other.Max[a]) == plane &&
                        other.Min[u] <= box.Min[u] &&
                        other.Max[u] >= box.Max[u] &&
                        other.Min[v] <= box.Min[v] &&
                        other.Max[v] >= box.Max[v]);
                    if (covered)
                    {
                        continue;
                    }

                    var minX = double.PositiveInfinity;
                    var minY = double.PositiveInfinity;
                    var maxX = double.NegativeInfinity;
                    var maxY = double.NegativeInfinity;
                    var corner = new double[3];
                    foreach (var cornerU in new[] { box.Min[u], box.Max[u] })
                    {
                        foreach (var cornerV in new[] { box.Min[v], box.Max[v] })
                        {
                            corner[a] = plane;
                            corner[u] = cornerU;
                            corner[v] = cornerV;
                            var x = m[0] * corner[0] + m[1] * corner[1] + m[2] * corner[2];
                            var y = m[3] * corner[0] + m[4] * corner[1] + m[5] * corner[2];
                            var z = m[6] * corner[0] + m[7] * corner[1] + m[8] * corner[2];
                            var perspective = pose.Scale * cameraDistance / (cameraDistance - z);
                            var screenX = pose.CenterX + x * perspective;
                            var screenY = pose.CenterY + y * perspective;
                            minX = Math.Min(minX, screenX);
                            minY = Math.Min(minY, screenY);
                            maxX = Math.Max(maxX, screenX);
                            maxY = Math.Max(maxY, screenY);
                        }
                    }

                    var boundsMinX = Math.Max(0, (int)Math.Floor(minX));
                    var boundsMinY = Math.Max(0, (int)Math.Floor(minY));
                    var boundsMaxX = Math.Min(dotWidth - 1, (int)Math.Ceiling(maxX));
                    var boundsMaxY = Math.Min(dotHeight - 1, (int)Math.Ceiling(maxY));
                    if (boundsMaxX < boundsMinX || boundsMaxY < boundsMinY)
                    {
                        continue;
                    }

                    // Faces are flat, so lighting is computed once per face. The normal in camera space
                    // is a column of the rotation matrix.
                    var nx = m[a] * side;
                    var ny = m[3 + a] * side;
                    var nz = m[6 + a] * side;
                    var diffuse = Math.Max(0, nx * EasterEgg3D.Light.X + ny * EasterEgg3D.Light.Y +
                                              nz * EasterEgg3D.Light.Z);
                    var rim = Math.Max(0, (nx * 0.8) - (nz * 0.3));
                    // On light backgrounds, highlights toward white would vanish, so faces only get
                    // darker than the brand colors there.
                    var specular = lightBackground
                        ? 0
                        : Math.Pow(
                              Math.Max(
                                  0,
                                  nx * EasterEgg3D.HalfVector.X + ny * EasterEgg3D.HalfVector.Y +
                                  nz * EasterEgg3D.HalfVector.Z),
                              24) * 0.6 * 255;
                    var light = lightBackground
                        ? Math.Min(1, 0.55 + (diffuse * 0.45) + (rim * 0.1))
                        : 0.45 + (diffuse * 0.78) + (rim * 0.25);
                    faces.Add(new Face
                    {
                        Axis = a,
                        Plane = plane,
                        UMin = box.Min[u],
                        UMax = box.Max[u],
                        VMin = box.Min[v],
                        VMax = box.Max[v],
                        MinX = boundsMinX,
                        MinY = boundsMinY,
                        MaxX = boundsMaxX,
                        MaxY = boundsMaxY,
                        Red = Math.Min(255, (box.Color.R * light) + specular),
                        Green = Math.Min(255, (box.Color.G * light) + specular),
                        Blue = Math.Min(255, (box.Color.B * light) + specular),
                    });
                }
            }
        }

        return faces;
    }
}

/// <summary>
/// Which easter egg to play (TS <c>export type EasterEgg3d = {kind:"pi-logo"; column; row} |
/// {kind:"armin"}</c>; the union is named <c>EasterEggKind</c> because the module-level helpers
/// already occupy <see cref="EasterEgg3D"/>).
/// </summary>
public abstract record EasterEggKind;

/// <summary>The pi logo lifts off the header logo, whose top-left cell is at column, row.</summary>
public sealed record PiLogoEgg(int Column, int Row) : EasterEggKind;

/// <summary>Armin grows out of a speck at the center.</summary>
public sealed record ArminEgg : EasterEggKind;

/// <summary>Colors the animation fades toward (TS inline <c>{foreground, background}</c>).</summary>
public sealed record EasterEggColors(Rgb Foreground, Rgb Background);

/// <summary>The hint line at the bottom of the animation (TS inline object).</summary>
internal readonly record struct EasterEggHint(
    int Row,
    int Start,
    string Text,
    int KeyLength,
    Rgb KeyColor,
    Rgb Color);

/// <summary>
/// The fullscreen animation itself (TS <c>EasterEgg3dAnimation</c>).
/// </summary>
/// <remarks>
/// TS reads the wall clock through <c>performance.now()</c> and starts a <c>setInterval</c> in the
/// constructor. Both are injected here (internal constructor) so tests can drive frames at fixed
/// times; the public constructor uses a <see cref="Stopwatch"/> and starts the timer.
/// </remarks>
public sealed class EasterEgg3dAnimation : IComponent, IDisposable
{
    private readonly ITui _tui;

    /// <summary>The screen to dissolve, as rendered lines.</summary>
    private readonly IReadOnlyList<string> _screen;

    private readonly EasterEggModel _model;
    private readonly Rgb _foreground;
    private readonly Rgb _background;
    private readonly Action _onDone;
    private readonly Func<double> _clock;
    private readonly double _startTime;
    private double _lastRender;
    private Timer? _timer;
    private ExitState? _exit;
    private (int Cycle, List<Cell3[]> Steps)? _shuffle;
    private int _screenWidth = -1;
    private int _screenHeight = -1;

    /// <summary>Star per cell index, or null.</summary>
    private Star?[] _stars = [];

    private List<ScreenCell>[] _cells = [];
    private readonly Dictionary<int, string> _ansiCache = [];
    private readonly BlockRaster _raster;

    private sealed class ExitState
    {
        public required double Start { get; init; }

        public required double Time { get; init; }

        public required double Yaw { get; init; }

        public required double TargetYaw { get; init; }

        public required (double X, double Y, double Z)[] Offsets { get; init; }
    }

    public EasterEgg3dAnimation(
        ITui tui,
        IReadOnlyList<string> screen,
        EasterEggModel model,
        EasterEggColors colors,
        Action onDone)
        : this(tui, screen, model, colors, onDone, StartStopwatch(), startTimer: true)
    {
    }

    /// <summary>Test seam: fixed clock and no timer.</summary>
    internal EasterEgg3dAnimation(
        ITui tui,
        IReadOnlyList<string> screen,
        EasterEggModel model,
        EasterEggColors colors,
        Action onDone,
        Func<double> clock,
        bool startTimer)
    {
        _tui = tui;
        _screen = screen;
        _model = model;
        _raster = new BlockRaster(model.CameraDistance);
        _foreground = colors.Foreground;
        _background = colors.Background;
        _onDone = onDone;
        _clock = clock;
        _startTime = clock();
        _lastRender = _startTime;

        if (startTimer)
        {
            _timer = new Timer(
                _ =>
                {
                    // Also stop when no longer rendered, e.g. when pi hides all overlays on exit.
                    if ((_exit is not null && ExitProgress() >= 1) || _clock() - _lastRender > 1000)
                    {
                        Finish();
                    }
                    else
                    {
                        _tui.RequestRender();
                    }
                },
                null,
                (int)EasterEgg3D.FrameMs,
                (int)EasterEgg3D.FrameMs);
        }
    }

    private static Func<double> StartStopwatch()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Play the exit animation. A second call skips it.</summary>
    public void Close()
    {
        if (_exit is not null)
        {
            Finish();
            return;
        }

        var time = Elapsed();
        var yaw = EasterEgg3D.SpinAngle(time);
        const double turn = Math.PI * 2;
        _exit = new ExitState
        {
            Start = _clock(),
            Time = time,
            Yaw = yaw,
            TargetYaw = Math.Ceiling(yaw / turn) * turn,
            Offsets = BlockOffsets(time),
        };
    }

    public void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel) || keybindings.Matches(data, AppKeybindings.AppClear))
        {
            Close();
        }
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        if (@event.Type == TuiMouseEventType.Click)
        {
            Close();
        }

        return new TuiMouseEventResult { Handled = true, Render = false };
    }

    public void Invalidate()
    {
        _screenWidth = -1;
        _screenHeight = -1;
        _ansiCache.Clear();
    }

    public string[] Render(int width)
    {
        var height = Math.Max(1, _tui.Terminal.Rows);
        var foreground = _foreground;
        var background = _background;
        _lastRender = _clock();
        if (width != _screenWidth || height != _screenHeight)
        {
            _screenWidth = width;
            _screenHeight = height;
            _cells = PrepareCells(width, foreground);
            _stars = PrepareStars(width, height);
        }

        // Dissolve time runs forward on entry and backward on exit, so the screen reassembles in reverse.
        double dissolveTime;
        Pose pose;
        double hintAlpha;
        double starAlpha;
        (double X, double Y, double Z)[] offsets;
        if (_exit is { } exit)
        {
            var progress = ExitProgress();
            var landing = EasterEgg3D.Clamp01(progress / 0.8);
            var settle = 1 - EasterEgg3D.Smooth(landing);
            dissolveTime = Math.Min(exit.Time, EasterEgg3D.DissolveEnd) * (1 - progress);
            pose = PoseAt(width, height, FlyProgress(exit.Time) * settle, exit.Time);
            pose.Yaw = exit.Yaw + ((exit.TargetYaw - exit.Yaw) * EasterEgg3D.EaseOut(landing));
            pose.Pitch *= settle;
            pose.Roll *= settle;
            hintAlpha = HintAlpha(exit.Time) * (1 - EasterEgg3D.Smooth(progress / 0.2));
            starAlpha = StarAlpha(exit.Time) * (1 - EasterEgg3D.Smooth(progress / 0.3));
            // Blocks are home well before the logo lands.
            var gather = 1 - EasterEgg3D.Smooth(progress / 0.5);
            offsets = exit.Offsets.Select(offset =>
                (offset.X * gather, offset.Y * gather, offset.Z * gather)).ToArray();
        }
        else
        {
            var time = Elapsed();
            dissolveTime = time;
            pose = PoseAt(width, height, FlyProgress(time), time);
            hintAlpha = HintAlpha(time);
            starAlpha = StarAlpha(time);
            offsets = BlockOffsets(time);
        }

        var columns = _model.Columns;
        var rows = _model.Rows;
        var boxes = new List<BlockBox>(_model.Blocks.Count);
        for (var index = 0; index < _model.Blocks.Count; index++)
        {
            var block = _model.Blocks[index];
            var (dx, dy, dz) = offsets[index];
            var x = block.Home.X - (columns / 2.0) + dx;
            var y = block.Home.Y - (rows / 2.0) + dy;
            boxes.Add(new BlockBox
            {
                Min = [x, y, dz - (EasterEgg3D.Depth / 2)],
                Max = [x + 1, y + 1, dz + (EasterEgg3D.Depth / 2)],
                Color = block.Color,
            });
        }

        var raster = _raster;
        raster.Render(
            width,
            height,
            pose,
            boxes,
            background,
            EasterEgg3D.IsLight(background) ? 0.3 : 0);

        Rgb? Halo(int index, Rgb? baseColor)
        {
            var amount = raster.HaloAmount[index];
            if (amount <= 0)
            {
                return baseColor;
            }

            var color = raster.HaloRgb;
            return EasterEgg3D.Mix(
                baseColor ?? background,
                new Rgb(color[index * 3], color[(index * 3) + 1], color[(index * 3) + 2]),
                amount);
        }

        var backgroundFade = EasterEgg3D.Smooth(dissolveTime / 0.5);
        var textFade = EasterEgg3D.Smooth(dissolveTime / 0.6) * 0.3;
        // Once the screen has fully dissolved, the text layer is empty and can be skipped.
        var textActive = dissolveTime < EasterEgg3D.DissolveEnd || backgroundFade < 1;
        var hint = Hint(width, height, hintAlpha, background);
        var frame = Math.Floor(dissolveTime * 14);
        var now = Elapsed();
        // Star brightness is quantized, so a row only changes when one of its stars steps to another level.
        var starLevel = EasterEgg3D.JsRound(starAlpha * 8) / 8;
        (string Text, Rgb Fg)? Starfield(int index)
        {
            if (starLevel <= 0)
            {
                return null;
            }

            if (_stars[index] is not { } star)
            {
                return null;
            }

            var flicker = EasterEgg3D.JsRound((0.5 + (0.5 * Math.Sin((now * star.Speed) + star.Phase))) * 3) / 3;
            return (
                star.Glyph,
                EasterEgg3D.Mix(
                    background,
                    EasterEgg3D.Mix(foreground, star.Tint, 0.5),
                    star.Strength * (0.7 + (0.3 * flicker)) * starLevel));
        }

        var lines = new List<string>(height);
        for (var row = 0; row < height; row++)
        {
            var cells = row < _cells.Length ? _cells[row] : [];
            var line = new StringBuilder();
            var currentFg = -1;
            var currentBg = -1;

            void Emit(string text, Rgb? fg, Rgb? bg)
            {
                var fgKey = fg is { } fgValue ? Pack(fgValue) : -1;
                var bgKey = bg is { } bgValue ? Pack(bgValue) : -1;
                if (fgKey != currentFg && text != " ")
                {
                    line.Append(fg is not null ? Ansi(fgKey, false) : "\x1b[39m");
                    currentFg = fgKey;
                }

                if (bgKey != currentBg)
                {
                    line.Append(bg is not null ? Ansi(bgKey, true) : "\x1b[49m");
                    currentBg = bgKey;
                }

                line.Append(text);
            }

            for (var column = 0; column < width; column++)
            {
                var index = (row * width) + column;
                var cell = textActive && column < cells.Count ? cells[column] : null;
                var cellBg = Halo(
                    index,
                    cell?.Bg is { } cellBackground && backgroundFade < 1
                        ? EasterEgg3D.Mix(cellBackground, background, backgroundFade)
                        : null);
                if (hint is { } hintValue &&
                    row == hintValue.Row &&
                    column >= hintValue.Start &&
                    column < hintValue.Start + hintValue.Text.Length)
                {
                    var offset = column - hintValue.Start;
                    Emit(
                        hintValue.Text[offset].ToString(),
                        offset < hintValue.KeyLength ? hintValue.KeyColor : hintValue.Color,
                        cellBg);
                    continue;
                }

                var dots = raster.Bits[index];
                if (dots != 0)
                {
                    // Quantized so neighboring cells on one face usually share a color and its escape sequence.
                    var step = EasterEgg3D.LogoColorStep * raster.Counts[index];
                    var rgb = raster.Rgb;
                    var color = new Rgb(
                        EasterEgg3D.JsRound(rgb[index * 3] / step) * EasterEgg3D.LogoColorStep,
                        EasterEgg3D.JsRound(rgb[(index * 3) + 1] / step) * EasterEgg3D.LogoColorStep,
                        EasterEgg3D.JsRound(rgb[(index * 3) + 2] / step) * EasterEgg3D.LogoColorStep);
                    Emit(EasterEgg3D.Braille[dots], color, cellBg);
                    continue;
                }

                if (cell is null)
                {
                    var star = Starfield(index);
                    Emit(star?.Text ?? " ", star?.Fg, cellBg);
                    continue;
                }

                var dust = (dissolveTime - cell.Delay) / 0.35;
                if (dust < 0 && cell.Width == 2 && (index + 1 >= raster.Bits.Length || raster.Bits[index + 1] == 0))
                {
                    Emit(cell.Text, EasterEgg3D.Mix(cell.Fg, background, textFade), cellBg);
                    column++;
                    continue;
                }

                if (dust < 0 && cell.Width == 1)
                {
                    Emit(cell.Text, EasterEgg3D.Mix(cell.Fg, background, textFade), cellBg);
                    continue;
                }

                var dotCount = dust < 0 ? cell.Ink : (int)EasterEgg3D.JsRound(cell.Ink * (1 - EasterEgg3D.Clamp01(dust)));
                if (dotCount <= 0)
                {
                    var star = Starfield(index);
                    Emit(star?.Text ?? " ", star?.Fg, cellBg);
                    continue;
                }

                // Pick `dotCount` distinct dots; the choice changes a few times per second so the dust shimmers.
                var bits = 0;
                var placed = 0;
                for (var attempt = 0; placed < dotCount && attempt < 32; attempt++)
                {
                    var bit = EasterEgg3D.DotBits[
                        (int)Math.Floor(EasterEgg3D.Hash((cell.Seed * 7919) + ((long)frame * 131) + attempt) * 8)];
                    if ((bits & bit) != 0)
                    {
                        continue;
                    }

                    bits |= bit;
                    placed++;
                }

                var fade = textFade + ((1 - textFade) * Math.Pow(EasterEgg3D.Clamp01(dust), 0.8));
                Emit(EasterEgg3D.Braille[bits], EasterEgg3D.Mix(cell.Fg, background, fade), cellBg);
            }

            lines.Add(line + "\x1b[0m");
        }

        return [.. lines];
    }

    public void Dispose()
    {
        if (_timer is { } timer)
        {
            timer.Dispose();
            _timer = null;
        }
    }

    private double Elapsed() => (_clock() - _startTime) / 1000;

    private double ExitProgress() => _exit is { } exit
        ? EasterEgg3D.Clamp01((_clock() - exit.Start) / 1000 / 1.1)
        : 0;

    private void Finish()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Dispose();
        _timer = null;
        _onDone();
    }

    /// <summary>Each block's displacement from its place in the model at <paramref name="time"/>, in grid units.</summary>
    private (double X, double Y, double Z)[] BlockOffsets(double time)
    {
        var blocks = _model.Blocks;
        (double X, double Y, double Z)[] Home() => blocks.Select(_ => (0.0, 0.0, 0.0)).ToArray();
        if (time < 0.1 + 1.3 + 3)
        {
            return Home();
        }

        var cycle = (int)Math.Floor((time - (0.1 + 1.3 + 3)) / EasterEgg3D.PuzzleCycle);
        var local = time - (0.1 + 1.3 + 3) - (cycle * EasterEgg3D.PuzzleCycle);
        if (_shuffle?.Cycle != cycle)
        {
            _shuffle = (cycle, EasterEgg3D.ShuffleSteps(_model, cycle));
        }

        var steps = _shuffle.Value.Steps;
        (double X, double Y, double Z) Offset(Cell3 position, int index)
        {
            var home = blocks[index].Home;
            return (position.X - home.X, position.Y - home.Y, position.Z - home.Z);
        }

        const double shuffleEnd = 0.3 * 12;
        if (local < shuffleEnd)
        {
            var step = (int)Math.Floor(local / 0.3);
            var progress = EasterEgg3D.Smooth((local - (step * 0.3)) / 0.3);
            var from = steps[step];
            var to = steps[step + 1];
            var result = new (double X, double Y, double Z)[blocks.Count];
            for (var index = 0; index < blocks.Count; index++)
            {
                var a = Offset(from[index], index);
                var b = Offset(to[index], index);
                result[index] = (
                    a.X + ((b.X - a.X) * progress),
                    a.Y + ((b.Y - a.Y) * progress),
                    a.Z + ((b.Z - a.Z) * progress));
            }

            return result;
        }

        if (local < shuffleEnd + 1)
        {
            // All blocks fly home at once. Alternating arcs in depth keep them from passing through each other.
            var u = (local - shuffleEnd) / 1;
            var remaining = 1 - EasterEgg3D.Smooth(u);
            var arc = Math.Sin(Math.PI * EasterEgg3D.Clamp01(u)) * 0.8;
            var last = steps[12];
            var result = new (double X, double Y, double Z)[blocks.Count];
            for (var index = 0; index < blocks.Count; index++)
            {
                var (x, y, z) = Offset(last[index], index);
                result[index] = (x * remaining, y * remaining, z * remaining + (index % 2 == 0 ? arc : -arc));
            }

            return result;
        }

        return Home();
    }

    private static double FlyProgress(double time) => EasterEgg3D.Smooth((time - 0.1) / 1.3);

    private static double StarAlpha(double time) => EasterEgg3D.Smooth((time - 2.5) / 2);

    private static double HintAlpha(double time) => EasterEgg3D.Smooth((time - 0.1 - 1.3) / 0.5);

    private Pose PoseAt(int width, int height, double progress, double time)
    {
        var columns = _model.Columns;
        var rows = _model.Rows;
        var cameraDistance = _model.CameraDistance;
        var radius = _model.Radius;
        var widthShare = _model.WidthShare;
        var origin = _model.Origin;
        var reach = radius * (cameraDistance / (cameraDistance - radius));
        // Lifting off, the front face must cover exactly the half-block cells (2x2 dots per pixel)
        // despite the perspective.
        var startScale = origin is not null
            ? 2 * (cameraDistance - (EasterEgg3D.Depth / 2)) / cameraDistance
            : 0.1;
        var endScale = Math.Max(
            startScale,
            Math.Min(width * 2 * widthShare, ((height * 4) - 8) * 0.48) / reach);
        double endX = width;
        double endY = (height * 2) - 2;
        double startX = origin is { } originValue ? (originValue.Column * 2) + columns : endX;
        double startY = origin is { } originRow ? (originRow.Row * 4) + rows : endY;
        var phase = EasterEgg3D.SpinPhase(time);
        return new Pose
        {
            CenterX = startX + ((endX - startX) * progress),
            CenterY = startY + ((endY - startY) * progress),
            // Interpolate the zoom geometrically so it feels uniform.
            Scale = startScale * Math.Pow(endScale / startScale, progress),
            Yaw = EasterEgg3D.SpinAngle(time),
            // Tilts are zero at whole turns, so the camera looks straight at the front of the logo.
            Pitch = 0.3 * Math.Sin(phase) * progress,
            Roll = 0.06 * Math.Sin(2 * phase) * progress,
        };
    }

    private EasterEggHint? Hint(int width, int height, double alpha, Rgb background)
    {
        if (alpha <= 0)
        {
            return null;
        }

        var keys = GlobalKeybindings.Get().GetKeys(TuiKeybindingIds.SelectCancel);
        var key = KeybindingHints.FormatKeyText(keys.Length > 0 ? keys[0] : "escape");
        var text = $"{key} to return";
        if (text.Length > width)
        {
            return null;
        }

        var theme = ThemeRuntime.Current;
        return new EasterEggHint(
            height - 2,
            (int)Math.Floor((width - text.Length) / 2.0),
            text,
            key.Length,
            EasterEgg3D.Mix(background, EasterEgg3D.ToRgb(theme.Colors[ThemeColors.Muted]), alpha),
            EasterEgg3D.Mix(background, EasterEgg3D.ToRgb(theme.Colors[ThemeColors.Dim]), alpha));
    }

    /// <summary>A sparse, deterministic starfield: one braille dot in about 1.8% of all cells.</summary>
    private Star?[] PrepareStars(int width, int height)
    {
        var stars = new Star?[width * height];
        for (var index = 0; index < stars.Length; index++)
        {
            if (EasterEgg3D.Hash((index * 3L) + 0x51ed) >= 0.018)
            {
                continue;
            }

            double Random(long salt) => EasterEgg3D.Hash((index * 7L) + (salt * 0x9e37));
            stars[index] = new Star(
                EasterEgg3D.Braille[EasterEgg3D.DotBits[(int)Math.Floor(Random(1) * 8)]],
                EasterEgg3D.StarTints[(int)Math.Floor(Random(2) * EasterEgg3D.StarTints.Length)],
                0.1 + (Random(3) * 0.12),
                Random(4) * Math.PI * 2,
                0.4 + (Random(5) * 0.8));
        }

        return stars;
    }

    private List<ScreenCell>[] PrepareCells(int width, Rgb foreground)
    {
        var cells = EasterEgg3D.ParseScreen(_screen, width, foreground, _background);
        var height = Math.Max(1, _tui.Terminal.Rows);
        var columns = _model.Columns;
        var rows = _model.Rows;
        var origin = _model.Origin;
        // The dust spreads out from where the model starts.
        var centerX = origin is { } originValue ? originValue.Column + (columns / 2.0) : width / 2.0;
        var centerY = origin is { } originRow ? originRow.Row + (rows / 4.0) : height / 2.0;
        var farthest = EasterEgg3D.Hypot(
            Math.Max(centerX, width - centerX),
            Math.Max(centerY, height - centerY) * 2,
            0);
        for (var row = 0; row < cells.Length; row++)
        {
            var line = cells[row];
            for (var column = 0; column < line.Count; column++)
            {
                var cell = line[column];
                // Wide graphemes share their first column's timing so both halves change together.
                var owner = cell.Width == 0 ? line[column - 1] : cell;
                var seed = EasterEgg3D.Hash((row * 65_537L) + column);
                if (cell.Width != 0)
                {
                    var distance = EasterEgg3D.Hypot(column - centerX, (row - centerY) * 2, 0) / farthest;
                    cell.Delay = 0.1 + (distance * 0.55) + (seed * 0.12);
                }
                else
                {
                    cell.Delay = owner.Delay;
                }

                cell.Seed = (row * 65_537L) + column;
                cell.Ink = EasterEgg3D.GlyphInk(owner.Text, seed);
            }
        }

        // The 3D model replaces its half-block rendering on screen.
        if (origin is { } modelOrigin)
        {
            var lastRow = Math.Min(cells.Length, modelOrigin.Row + (int)Math.Ceiling(rows / 2.0));
            for (var row = modelOrigin.Row; row < lastRow; row++)
            {
                var line = cells[row];
                var lastColumn = Math.Min(line.Count, modelOrigin.Column + columns);
                for (var column = modelOrigin.Column; column < lastColumn; column++)
                {
                    var cell = line[column];
                    cell.Text = " ";
                    cell.Width = 1;
                    cell.Ink = 0;
                    cell.Bg = null;
                }
            }
        }

        return cells;
    }

    private static int Pack(Rgb color) =>
        ((int)EasterEgg3D.JsRound(color.R) << 16) | ((int)EasterEgg3D.JsRound(color.G) << 8) | (int)EasterEgg3D.JsRound(color.B);

    private string Ansi(int key, bool isBackground)
    {
        var cacheKey = (key * 2) + (isBackground ? 1 : 0);
        if (_ansiCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var color = Colors.Rgb((key >> 16) & 255, (key >> 8) & 255, key & 255);
        var mode = ThemeRuntime.Current.GetColorMode();
        var value = isBackground ? Colors.BackgroundAnsi(color, mode) : Colors.ForegroundAnsi(color, mode);
        _ansiCache[cacheKey] = value;
        return value;
    }
}

/// <summary>
/// Entry points of <c>easter-egg-3d.lazy.ts</c> plus <c>playEasterEgg3d</c> from the main module.
/// </summary>
/// <remarks>
/// TS splits the module in two so the 1.3k-line animation only loads on first use (<c>import()</c>).
/// C# assemblies are loaded up front, so the split disappears; the guards that matter are kept: only
/// the alternate screen can play, an existing overlay wins, and the screen is captured before playing.
/// </remarks>
public static class EasterEggPlayer
{
    private static bool _playing;

    /// <summary>Plays the 3D pi logo, lifting off the header logo whose top-left cell is at column, row.</summary>
    public static bool PlayPiLogo3d(ITui tui, int column, int row) => Play(tui, new PiLogoEgg(column, row));

    /// <summary>Plays the 3D Armin. Returns false when it cannot play, so the caller can fall back.</summary>
    public static bool PlayArmin3d(ITui tui) => Play(tui, new ArminEgg());

    private static bool Play(ITui tui, EasterEggKind egg)
    {
        if (tui is not TuiAltScreen alt)
        {
            return false;
        }

        if (alt.HasOverlay())
        {
            return true;
        }

        var screen = alt.GetScreenLines();
        _ = PlayAsync(alt, screen, egg);
        return true;
    }

    /// <summary>
    /// Show the animation as a fullscreen overlay until it is dismissed. The overlay takes focus and
    /// mouse input and returns focus when hidden, so the rest of the UI keeps running underneath.
    /// </summary>
    public static async Task PlayAsync(ITui tui, IReadOnlyList<string> screen, EasterEggKind egg)
    {
        if (_playing)
        {
            return;
        }

        _playing = true;
        // Fading needs the terminal's actual default colors; the theme only knows its own.
        var reported = await tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 100 });
        var theme = ThemeRuntime.Current;
        var dark = theme.Appearance == "dark";
        Rgb Tuple(RgbColor? rgb, Rgb fallback) => rgb is { } value ? new Rgb(value.R, value.G, value.B) : fallback;
        var colors = new EasterEggColors(
            Tuple(reported.Foreground, EasterEgg3D.ToRgb(theme.Colors[ThemeColors.Text])),
            Tuple(reported.Background, dark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255)));
        if (tui.HasOverlay())
        {
            _playing = false;
            return;
        }

        var model = egg is ArminEgg
            ? EasterEgg3D.ArminModel(EasterEgg3D.ToRgb(theme.Colors[ThemeColors.Accent]))
            : EasterEgg3D.PiLogoModel(((PiLogoEgg)egg).Column, ((PiLogoEgg)egg).Row);
        IOverlayHandle? overlay = null;
        var animation = new EasterEgg3dAnimation(tui, screen, model, colors, () =>
        {
            _playing = false;
            overlay?.Hide();
        });
        overlay = tui.ShowOverlay(animation, new OverlayOptions
        {
            Anchor = OverlayAnchor.TopLeft,
            Width = SizeValue.Percent(100),
            MaxHeight = SizeValue.Percent(100),
        });
    }
}
