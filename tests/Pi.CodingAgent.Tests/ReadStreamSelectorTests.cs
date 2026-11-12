using System.Text;
using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 v1.5.1 #9654（PR #11750）read 流式选行的回归用例，逐字对照
/// <c>test/read-streaming.test.ts</c>。以不同块大小（1 / 4096 字节）喂入原始字节块，
/// 校验流式解码（跨块多字节 / BOM / 无效字节）、offset/limit、恰好落在空行上的上限判定，
/// 以及「超过上限的行不整行存储」。去掉修复（整篇 split）时这些用例的内存/输出语义不成立。
/// </summary>
public sealed class ReadStreamSelectorTests
{
    private static async Task<ReadStreamSelector.SelectedLines> SelectAsync(
        string text, int startLine, int? limit, int chunkSize = 4096)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var chunks = new List<byte[]>();
        for (var i = 0; i < bytes.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, bytes.Length - i);
            var chunk = new byte[len];
            Buffer.BlockCopy(bytes, i, chunk, 0, len);
            chunks.Add(chunk);
        }

        if (chunks.Count == 0)
        {
            chunks.Add([]); // 空文件也给一个空块
        }

        return await ReadStreamSelector
            .SelectLinesAsync(ReadStreamSelector.AsAsync(chunks), startLine, limit);
    }

    private static string Expected(string text, int startLine, int? limit)
    {
        var allLines = text.Split('\n');
        var endLine = limit is null ? allLines.Length : Math.Min(startLine + limit.Value, allLines.Length);
        return Truncate.TruncateHead(string.Join("\n", allLines[startLine..endLine])).Content;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task ChunkedDecoding_MatchesWholeFileOffsetLimit(int chunkSize)
    {
        // 对照上游 "gives the same output as the whole-file algorithm"：多种文本 / offset / limit / 块大小。
        var cases = new[]
        {
            "",
            "hello",
            "a\nb\nc",
            "a\nb\nc\n",
            "a\r\nb\r\nc\r\n",           // CRLF
            "\uFEFFbefore\nneedle 中文\nafter\n", // UTF-8 BOM + 多字节
            "one\rtwo needle\r\nnext\r\n",        // 孤立 CR 作为行内内容
            string.Join("\n", Enumerable.Range(1, 2001).Select(i => $"line {i}")),
        };

        foreach (var text in cases)
        {
            var total = text.Split('\n').Length;
            foreach (var offset in new int?[] { null, 1, 2, 3, total, total + 1 })
            {
                var startLine = offset is > 0 ? offset.Value - 1 : 0;
                if (startLine >= total && offset is > 0 && offset > total)
                {
                    // 越界 offset 由工具层守卫（不在纯叶子职责内），这里只测合法 startLine。
                    continue;
                }

                foreach (var limit in new int?[] { null, 0, 1, total })
                {
                    var selected = await SelectAsync(text, startLine, limit, chunkSize);
                    Assert.Equal(Expected(text, startLine, limit), selected.Truncation.Content);
                    Assert.Equal(total, selected.TotalFileLines);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)] // 把多字节字符从中间劈开
    [InlineData(4096)]
    public async Task MultiByteCharsSplitAcrossChunks_DecodeOnce(int chunkSize)
    {
        // "€😀" 是 3+4 字节的 UTF-8；1/3 字节块会把字符劈到两块，解码器必须跨块拼接。
        var text = "a€😀b\nc€😀d\n";
        var selected = await SelectAsync(text, 0, null, chunkSize);
        Assert.Equal(Expected(text, 0, null), selected.Truncation.Content);
        Assert.Equal(3, selected.TotalFileLines);
    }

    [Fact]
    public async Task FirstLineOverByteCap_ReportsFirstLineBytes_NotStored()
    {
        // 上游 "first line over 50KB"：首行超限只量大小，不整行存储；FirstLineExceedsLimit=true。
        var big = new string('é', Truncate.DefaultMaxBytes / 2 + 1);
        var text = big + "\nsecond";
        var selected = await SelectAsync(text, 0, null, 1024);
        Assert.True(selected.Truncation.FirstLineExceedsLimit);
        // firstLineBytes 是首行字节数（é 为 2 字节），且 storedLines 不含整行（truncation.Content 为空）。
        Assert.Equal(Encoding.UTF8.GetByteCount(big), selected.FirstLineBytes);
        Assert.Equal("", selected.Truncation.Content);
    }

    [Fact]
    public async Task TrailingEmptyLineAtCap_StillReportsTruncatedByLines()
    {
        // 上游：2001 行后接空行会被 truncateHead 忽略末尾空行；多存一行的规则让 totalLines 判定与整篇一致。
        var text = string.Join("\n", Enumerable.Range(1, 2001).Select(i => $"line {i}")) + "\n\nmore";
        var selected = await SelectAsync(text, 0, null);
        // 与整篇算法一致：按行截断，truncatedBy=lines。
        Assert.True(selected.Truncation.Truncated);
        Assert.Equal(TruncatedBy.Lines, selected.Truncation.TruncatedBy);
        Assert.Equal(Expected(text, 0, null), selected.Truncation.Content);
    }

    [Fact]
    public async Task AbortBetweenChunks_StopsConsumingStream()
    {
        var gate = new TaskCompletionSource();
        var pulled = 0;
        async IAsyncEnumerable<byte[]> SlowChunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 5; i++)
            {
                await gate.Task.ConfigureAwait(false);
                pulled++;
                ct.ThrowIfCancellationRequested();
                yield return Encoding.UTF8.GetBytes("line\n");
            }
        }

        using var cts = new CancellationTokenSource();
        var task = ReadStreamSelector.SelectLinesAsync(SlowChunks(cts.Token), 0, null, cts.Token);
        cts.Cancel();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(pulled <= 1, "取消后不应继续拉取后续块");
    }

    [Fact]
    public void IsStreamableRange_NegativeLimitFallsBackToWholeFile()
    {
        Assert.True(ReadStreamSelector.IsStreamableRange(null, null));
        Assert.True(ReadStreamSelector.IsStreamableRange(1, 5));
        Assert.False(ReadStreamSelector.IsStreamableRange(1, -1)); // limit:-1 走整篇数组切片
    }
}
