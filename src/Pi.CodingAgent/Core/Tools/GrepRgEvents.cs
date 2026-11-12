using System.Text;
using System.Text.Json;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// 一条 ripgrep <c>--json</c> 事件（<c>match</c> 或 <c>context</c>）。
/// 上游 v1.5.2 <c>core/tools/grep.ts</c>（#9276）把上下文行改由 rg 的 <c>--context</c> 事件直接给出，
/// 不再整文件读回内存。
/// </summary>
public sealed record GrepRgEvent(bool IsMatch, string FilePath, int LineNumber, string LineText);

/// <summary>
/// 纯叶子：把 ripgrep 的 <c>match</c>/<c>context</c> 事件流转成 grep 输出行与 details。
/// 逐条对照上游 v1.5.2 <c>grep.ts</c> 的 <c>pushLine</c> / limit 逻辑：
/// <list type="bullet">
/// <item>上下文行用 <c>path-line- text</c>、匹配行用 <c>path:line: text</c>；只剥离行尾终止符，
///       行内孤立 CR 属于内容（#9276 修 UTF-16 / 孤立 CR / 大文件 OOM）。</item>
/// <item>只有「又出现一条 match」才证明 limit 截断了结果——恰好 limit 条时不再误报 limit 提示
///       （#9276 / #11423）。limit 命中后仍保留最后一条 match 的尾部上下文窗口。</item>
/// <item>超出字节上限后停止存储但继续计数（<c>outputBytes</c>），交给 TruncateHead 做最终截断。</item>
/// </list>
/// </summary>
internal sealed class GrepRgEventProcessor
{
    private readonly Func<string, string> _formatPath;
    private readonly int _contextValue;
    private readonly int _effectiveLimit;
    private readonly int _maxBytes;

    private readonly List<string> _outputLines = [];
    private long _outputBytes;
    private int _outputLineCount;
    private string? _lastFile;
    private bool _limitHit;
    private int _trailingContextEnd;
    private int _matchCount;
    private bool _matchLimitReached;
    private bool _linesTruncated;
    private bool _done;

    public GrepRgEventProcessor(
        Func<string, string> formatPath,
        int contextValue,
        int effectiveLimit,
        int maxBytes)
    {
        _formatPath = formatPath;
        _contextValue = contextValue;
        _effectiveLimit = effectiveLimit;
        _maxBytes = maxBytes;
    }

    public int MatchCount => _matchCount;
    public bool MatchLimitReached => _matchLimitReached;
    public bool LinesTruncated => _linesTruncated;
    public int OutputLineCount => _outputLineCount;
    public long OutputBytes => _outputBytes;
    public bool Done => _done;
    public IReadOnlyList<string> OutputLines => _outputLines;

    /// <summary>解码 rg 的 <c>{text}</c> 或 <c>{bytes}</c>（base64）文本字段；也容忍裸字符串。</summary>
    internal static string DecodeRgText(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String)
        {
            return data.GetString() ?? string.Empty;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }

            if (data.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.String)
            {
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetString() ?? string.Empty));
                }
                catch (FormatException)
                {
                    return string.Empty;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析一行 rg <c>--json</c> 输出为 <see cref="GrepRgEvent"/>；只接受 <c>match</c>/<c>context</c>，
    /// 其余（begin/end/summary）与畸形行返回 false。path/lines 均经 <see cref="DecodeRgText"/> 解码。
    /// </summary>
    internal static bool TryParseRgEvent(string line, out GrepRgEvent e)
    {
        e = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return false;
            }

            var type = typeEl.GetString();
            var isMatch = type == "match";
            if (!isMatch && type != "context")
            {
                return false;
            }

            if (!root.TryGetProperty("data", out var data))
            {
                return false;
            }

            var filePath = data.TryGetProperty("path", out var pathEl) ? DecodeRgText(pathEl) : string.Empty;
            if (filePath.Length == 0)
            {
                return false;
            }

            if (!data.TryGetProperty("line_number", out var lnEl) || !lnEl.TryGetInt32(out var lineNumber))
            {
                return false;
            }

            var lineText = data.TryGetProperty("lines", out var linesEl) ? DecodeRgText(linesEl) : string.Empty;
            e = new GrepRgEvent(isMatch, filePath, lineNumber, lineText);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>处理一个事件；返回 true 表示 limit 已被证明、调用方应停止 rg 子进程（finish）。</summary>
    public bool Process(GrepRgEvent e)
    {
        if (_done)
        {
            return false;
        }

        if (!_limitHit)
        {
            PushLine(e.FilePath, e.LineNumber, e.LineText, e.IsMatch);
            if (!e.IsMatch)
            {
                return false;
            }

            _matchCount++;
            if (_matchCount >= _effectiveLimit)
            {
                _limitHit = true;
                _trailingContextEnd = e.LineNumber + _contextValue;
            }

            return false;
        }

        var inWindow = string.Equals(e.FilePath, _lastFile, StringComparison.Ordinal)
            && e.LineNumber <= _trailingContextEnd;
        // 最后一条 match 尾部上下文里的另一条 match 也按上下文行呈现。
        if (inWindow)
        {
            PushLine(e.FilePath, e.LineNumber, e.LineText, isMatch: false);
        }

        if (e.IsMatch)
        {
            _matchLimitReached = true;
        }

        if (_matchLimitReached && !(inWindow && e.LineNumber < _trailingContextEnd))
        {
            _done = true;
            return true;
        }

        return false;
    }

    private void PushLine(string filePath, int lineNumber, string lineText, bool isMatch)
    {
        _lastFile = filePath;
        var relativePath = _formatPath(filePath);
        // 只剥离行尾终止符：行内孤立 CR 是内容（ripgrep 只按 LF 计行）。
        var sanitized = lineText;
        if (sanitized.EndsWith("\r\n", StringComparison.Ordinal))
        {
            sanitized = sanitized[..^2];
        }
        else if (sanitized.EndsWith('\n') || sanitized.EndsWith('\r'))
        {
            sanitized = sanitized[..^1];
        }

        var (truncatedText, wasTruncated) = Truncate.TruncateLine(sanitized);
        if (wasTruncated)
        {
            _linesTruncated = true;
        }

        var line = isMatch
            ? $"{relativePath}:{lineNumber}: {truncatedText}"
            : $"{relativePath}-{lineNumber}- {truncatedText}";

        // 字节上限后停止存储但继续计数（outputBytes 每行末多算一个换行）。
        if (_outputBytes - 1 <= _maxBytes)
        {
            _outputLines.Add(line);
        }

        _outputBytes += Encoding.UTF8.GetByteCount(line) + 1;
        _outputLineCount++;
    }

    /// <summary>生成最终输出与 details（对照上游 <c>child.on("close")</c> 的收尾）。</summary>
    public (string Output, GrepToolDetails? Details) Finalize()
    {
        if (_matchCount == 0)
        {
            return ("No matches found", null);
        }

        var rawOutput = string.Join("\n", _outputLines);
        var truncation = Truncate.TruncateHead(rawOutput, new Truncate.TruncationOptions
        {
            MaxLines = int.MaxValue,
            MaxBytes = _maxBytes,
        });
        // 上游用计数到的总量覆盖 truncateHead 的 total*（已停止存储的行也要计入）。
        truncation = truncation with
        {
            TotalLines = _outputLineCount,
            TotalBytes = (int)Math.Min(_outputBytes - 1, int.MaxValue),
        };

        var output = truncation.Content;
        var notices = new List<string>();
        if (_matchLimitReached)
        {
            notices.Add($"{_effectiveLimit} matches limit reached. Use limit={_effectiveLimit * 2} for more, or refine pattern");
        }

        if (truncation.Truncated)
        {
            notices.Add($"{Truncate.FormatSize(_maxBytes)} limit reached");
        }

        if (_linesTruncated)
        {
            notices.Add($"Some lines truncated to {Truncate.GrepMaxLineLength} chars. Use read tool to see full lines");
        }

        GrepToolDetails? details = notices.Count > 0
            ? new GrepToolDetails
            {
                Truncation = truncation.Truncated ? truncation : null,
                MatchLimitReached = _matchLimitReached ? _effectiveLimit : null,
                LinesTruncated = _linesTruncated ? true : null,
            }
            : null;
        if (notices.Count > 0)
        {
            output += $"\n\n[{string.Join(". ", notices)}]";
        }

        return (output, details);
    }
}
