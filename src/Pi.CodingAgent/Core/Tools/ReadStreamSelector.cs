using System.Runtime.CompilerServices;
using System.Text;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// 上游 v1.5.1 #9654（PR #11750）：read 工具原来把整篇文件读进内存再 split("\n")，
/// 单行读也会把整篇放大三倍（字节数组 / 字符串 / 行数组），大文件 OOM。此纯叶子从流式
/// 字节块里逐行挑选：<b>只存会被 TruncateHead 显示的行，其余仅计数</b>，内存随改动大小而非文件大小增长；
/// 行尾多留一行以让 truncateHead 对「恰好落在空行上的上限」判定与整篇一致；超过上限的超长行只量大小不整行存储。
/// 逐条对照上游 <c>core/tools/read.ts</c> 的 <c>selectLinesFromStream</c>；流式 UTF-8 解码对齐
/// <c>TextDecoder("utf-8", {ignoreBOM:true})</c> 的 <c>decode(chunk,{stream:true})</c> + 末尾 <c>decode()</c>。
/// </summary>
public static class ReadStreamSelector
{
    /// <summary>选出的行与整篇行数 / 截断信息 / 首行字节数。</summary>
    public sealed record SelectedLines(int TotalFileLines, Truncate.TruncationResult Truncation, int FirstLineBytes);

    /// <summary>
    /// 上游 <c>isStreamableRange</c>：非负整数 limit 才能流式；负 limit 保留整篇路径
    /// （其数组切片语义，例如 limit=-1 表示除末行外所有行）。offset/limit 经 ToolArgs.GetInt 已是整数。
    /// </summary>
    public static bool IsStreamableRange(int? offset, int? limit) =>
        limit is null || limit >= 0;

    /// <summary>
    /// 从流式字节块里挑选行（startLine 为 0 基，limit 为 null 表示到文件末尾）。逐块流式解码，
    /// 跨块多字节字符正确拼接；signal 取消时抛 <see cref="OperationCanceledException"/> 并停止消费流。
    /// </summary>
    public static async Task<SelectedLines> SelectLinesAsync(
        IAsyncEnumerable<byte[]> rawChunks,
        int startLine,
        int? limit,
        CancellationToken signal = default)
    {
        var endLine = limit is null ? int.MaxValue : startLine + limit.Value;
        var storedLines = new List<string>();
        long storedBytes = 0;
        var storedPastCaps = false;
        var line = 0;
        var selectedLines = 0;
        long selectedBytes = 0;
        var firstLineBytes = 0;
        var lastLineEmpty = true;
        List<string>? pieces = [];
        long lineBytes = 0;
        var lineHasText = false;

        void AppendToLine(string text, int start, int end)
        {
            lineHasText = true;
            if (pieces is null)
            {
                return;
            }

            var piece = text[start..end];
            // 超过字节上限的行永远不整行存储；首行超限时只量大小（1GB 单行也不会进内存）。
            if (lineBytes <= Truncate.DefaultMaxBytes)
            {
                pieces.Add(piece);
            }

            lineBytes += Encoding.UTF8.GetByteCount(piece);
        }

        void FinishLine()
        {
            if (pieces is not null)
            {
                if (selectedLines == 0)
                {
                    firstLineBytes = (int)Math.Min(lineBytes, int.MaxValue);
                }

                // 上限之后多存一行：truncateHead 会忽略末尾空行，故上限后的那一行必须在场。
                if (storedLines.Count > Truncate.DefaultMaxLines || storedBytes > Truncate.DefaultMaxBytes)
                {
                    storedPastCaps = true;
                }

                var text = string.Concat(pieces);
                storedBytes += Encoding.UTF8.GetByteCount(text) + (storedLines.Count > 0 ? 1 : 0);
                storedLines.Add(text);
            }

            selectedLines++;
            lastLineEmpty = !lineHasText;
            pieces = storedPastCaps ? null : [];
            lineBytes = 0;
            lineHasText = false;
        }

        void Select(string text)
        {
            var position = 0;
            var selectionStart = -1;
            while (true)
            {
                var selected = line >= startLine && line < endLine;
                if (selected && selectionStart == -1)
                {
                    selectionStart = position;
                }

                var newline = text.IndexOf('\n', position);
                var end = newline == -1 ? text.Length : newline;
                if (selected && end > position)
                {
                    AppendToLine(text, position, end);
                }

                if (newline == -1)
                {
                    break;
                }

                if (selected)
                {
                    FinishLine();
                    if (line + 1 == endLine)
                    {
                        selectedBytes += Encoding.UTF8.GetByteCount(text[selectionStart..newline]);
                        selectionStart = -1;
                    }
                }

                line++;
                position = newline + 1;
            }

            if (selectionStart != -1)
            {
                selectedBytes += Encoding.UTF8.GetByteCount(text[selectionStart..]);
            }
        }

        // 非致命流式 UTF-8 解码，跨块多字节字符由 Decoder 内部缓冲（等价 TextDecoder stream:true）。
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        await foreach (var chunk in rawChunks.WithCancellation(signal).ConfigureAwait(false))
        {
            signal.ThrowIfCancellationRequested();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            var count = decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush: false);
            if (count > 0)
            {
                Select(new string(chars, 0, count));
            }
        }

        // 末尾 decode()：吐出被缓冲的未完成序列。
        var tail = new char[8];
        var tailCount = decoder.GetChars([], 0, 0, tail, 0, flush: true);
        if (tailCount > 0)
        {
            Select(new string(tail, 0, tailCount));
        }

        // 流末尾把最后一行（无换行）也结束掉。
        if (line >= startLine && line < endLine)
        {
            FinishLine();
        }

        var truncation = Truncate.TruncateHead(string.Join("\n", storedLines));
        // truncateHead 对「选中的行拼成的文本」计数会丢末尾空行，用流式计数覆盖 total*。
        truncation = truncation with
        {
            TotalLines = selectedBytes == 0 ? 0 : selectedLines - (lastLineEmpty ? 1 : 0),
            TotalBytes = (int)Math.Min(selectedBytes, int.MaxValue),
        };

        return new SelectedLines(line + 1, truncation, firstLineBytes);
    }

    /// <summary>把同步字节块序列包装成 <see cref="IAsyncEnumerable{T}"/>，供测试与本地文件流使用。</summary>
    public static async IAsyncEnumerable<byte[]> AsAsync(
        IEnumerable<byte[]> chunks,
        [EnumeratorCancellation] CancellationToken signal = default)
    {
        foreach (var chunk in chunks)
        {
            signal.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }
}
