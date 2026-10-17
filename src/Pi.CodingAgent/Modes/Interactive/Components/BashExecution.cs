using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Tools;
using TuiText = Pi.Tui.Components.Text;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component for displaying bash command execution with streaming output
/// (TS <c>BashExecutionComponent</c>).
/// </summary>
public sealed partial class BashExecutionComponent : Container
{
    /// <summary>Preview line limit when not expanded (matches tool execution behavior).</summary>
    private const int PreviewLines = 20;

    private enum BashStatus
    {
        Running,
        Complete,
        Cancelled,
        Error,
    }

    [GeneratedRegex("\r\n")]
    private static partial Regex CrLf();

    [GeneratedRegex("\r")]
    private static partial Regex Cr();

    private readonly string _command;
    private readonly List<string> _outputLines = [];
    private BashStatus _status = BashStatus.Running;
    private int? _exitCode;
    private readonly Loader _loader;
    private Truncate.TruncationResult? _truncationResult;
    private string? _fullOutputPath;
    private bool _expanded;
    private readonly Container _contentContainer;
    /// <summary><c>dim</c> marks <c>!!</c> commands, whose output is excluded from the model context.</summary>
    private readonly string _colorKey;
    private int _outputPad;

    public BashExecutionComponent(string command, ITui ui, bool excludeFromContext = false, int outputPad = 1)
    {
        _command = command;
        _colorKey = excludeFromContext ? ThemeColors.Dim : ThemeColors.BashMode;
        _outputPad = outputPad;
        var theme = ThemeRuntime.Current;
        var borderColor = (string str) => theme.Fg(_colorKey, str);

        // Add spacer
        AddChild(new Spacer(1));

        // Top border
        AddChild(new DynamicBorder(borderColor));

        // Content container (holds dynamic content between borders)
        _contentContainer = new Container();
        AddChild(_contentContainer);

        _loader = new Loader(
            ui,
            spinner => theme.Fg(_colorKey, spinner),
            text => theme.Fg(ThemeColors.Muted, text),
            $"Running... ({KeybindingHints.KeyText(TuiKeybindingIds.SelectCancel)} to cancel)"); // Plain text for loader

        // Bottom border
        AddChild(new DynamicBorder(borderColor));

        UpdateDisplay();
    }

    /// <summary>Whether the output is expanded (full output) or collapsed (preview only).</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public void SetOutputPad(int outputPad)
    {
        _outputPad = outputPad;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void AppendOutput(string chunk)
    {
        // Strip ANSI codes and normalize line endings
        // Note: binary data is already sanitized in the renderer's executeBashCommand
        var clean = Cr().Replace(CrLf().Replace(AnsiText.StripAnsi(chunk), "\n"), "\n");

        // Append to output lines
        var newLines = clean.Split('\n');
        if (_outputLines.Count > 0 && newLines.Length > 0)
        {
            // Append first chunk to last line (incomplete line continuation)
            _outputLines[^1] += newLines[0];
            _outputLines.AddRange(newLines.Skip(1));
        }
        else
        {
            _outputLines.AddRange(newLines);
        }

        UpdateDisplay();
    }

    public void SetComplete(
        int? exitCode,
        bool cancelled,
        Truncate.TruncationResult? truncationResult = null,
        string? fullOutputPath = null)
    {
        _exitCode = exitCode;
        _status = cancelled
            ? BashStatus.Cancelled
            : exitCode is not null and not 0
                ? BashStatus.Error
                : BashStatus.Complete;
        _truncationResult = truncationResult;
        _fullOutputPath = fullOutputPath;

        // Stop loader
        _loader.Stop();

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var theme = ThemeRuntime.Current;

        // Apply truncation for LLM context limits (same limits as the bash tool)
        var fullOutput = string.Join("\n", _outputLines);
        var contextTruncation = Truncate.TruncateTail(fullOutput, new Truncate.TruncationOptions
        {
            MaxLines = Truncate.DefaultMaxLines,
            MaxBytes = Truncate.DefaultMaxBytes,
        });

        // Lines to potentially display (after context truncation)
        var availableLines = contextTruncation.Content.Length > 0
            ? contextTruncation.Content.Split('\n')
            : [];

        // Apply preview truncation based on expanded state
        var previewLogicalLines = availableLines.Length > PreviewLines
            ? availableLines[^PreviewLines..]
            : availableLines;
        var hiddenLineCount = availableLines.Length - previewLogicalLines.Length;

        // Rebuild content container
        _contentContainer.Clear();

        // Command header
        _contentContainer.AddChild(new TuiText(
            theme.Fg(_colorKey, theme.Bold($"$ {_command}")), _outputPad, 0));

        // Output
        if (availableLines.Length > 0)
        {
            if (_expanded)
            {
                // Show all lines
                var displayText = string.Join("\n", availableLines.Select(line => theme.Fg(ThemeColors.Muted, line)));
                _contentContainer.AddChild(new TuiText($"\n{displayText}", _outputPad, 0));
            }
            else
            {
                // Shared visual truncation utility with width-aware caching
                var styledOutput = string.Join("\n", previewLogicalLines.Select(line => theme.Fg(ThemeColors.Muted, line)));
                var styledInput = $"\n{styledOutput}";
                _contentContainer.AddChild(new CachedVisualLines(styledInput, PreviewLines, _outputPad));
            }
        }

        // Loader or status
        if (_status == BashStatus.Running)
        {
            _contentContainer.AddChild(_loader);
        }
        else
        {
            var statusParts = new List<string>();

            // Show how many lines are hidden (collapsed preview)
            if (hiddenLineCount > 0)
            {
                if (_expanded)
                {
                    statusParts.Add(
                        $"{theme.Fg(ThemeColors.Muted, "(")}{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to collapse")}{theme.Fg(ThemeColors.Muted, ")")}");
                }
                else
                {
                    statusParts.Add(
                        $"{theme.Fg(ThemeColors.Muted, $"... {hiddenLineCount} more lines (")}{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}{theme.Fg(ThemeColors.Muted, ")")}");
                }
            }

            if (_status == BashStatus.Cancelled)
            {
                statusParts.Add(theme.Fg(ThemeColors.Warning, "(cancelled)"));
            }
            else if (_status == BashStatus.Error)
            {
                statusParts.Add(theme.Fg(ThemeColors.Error, $"(exit {_exitCode})"));
            }

            // Truncation warning (context truncation, not preview truncation)
            var wasTruncated = _truncationResult?.Truncated == true || contextTruncation.Truncated;
            if (wasTruncated && _fullOutputPath is not null)
            {
                statusParts.Add(theme.Fg(ThemeColors.Warning, $"Output truncated. Full output: {_fullOutputPath}"));
            }

            if (statusParts.Count > 0)
            {
                _contentContainer.AddChild(new TuiText($"\n{string.Join("\n", statusParts)}", _outputPad, 0));
            }
        }
    }

    /// <summary>The raw output for creating a BashExecutionMessage.</summary>
    public string GetOutput() => string.Join("\n", _outputLines);

    /// <summary>The command that was executed.</summary>
    public string GetCommand() => _command;

    /// <summary>
    /// The TS inline renderer object: visual-line truncation cached per width.
    /// </summary>
    private sealed class CachedVisualLines : IComponent
    {
        private readonly string _styledInput;
        private readonly int _maxVisualLines;
        private readonly int _outputPad;
        private int? _cachedWidth;
        private string[]? _cachedLines;

        public CachedVisualLines(string styledInput, int maxVisualLines, int outputPad)
        {
            _styledInput = styledInput;
            _maxVisualLines = maxVisualLines;
            _outputPad = outputPad;
        }

        public string[] Render(int width)
        {
            if (_cachedLines is null || _cachedWidth != width)
            {
                var result = VisualTruncate.TruncateToVisualLines(_styledInput, _maxVisualLines, width, _outputPad);
                _cachedLines = [.. result.VisualLines];
                _cachedWidth = width;
            }

            return _cachedLines ?? [];
        }

        public void Invalidate()
        {
            _cachedWidth = null;
            _cachedLines = null;
        }
    }
}
