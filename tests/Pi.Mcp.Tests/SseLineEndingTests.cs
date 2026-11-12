using System.Text;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>上游 1.5.1 #11764：Streamable HTTP SSE 支持 CRLF / LF / 孤立 CR 行尾，以及跨 chunk 拆开的 CRLF。</summary>
public class SseLineEndingTests
{
    private static async Task<List<SseEvent>> ParseAsync(params string[] chunks)
    {
        // 用逐块可读的流模拟网络分片；每个 chunk 一次 ReadAsync。
        using var stream = new ChunkStream(chunks);
        var events = new List<SseEvent>();
        await SseStream.ConsumeAsync(stream, new ConsumeSseOptions { OnEvent = events.Add });
        return events;
    }

    [Fact]
    public async Task ParsesLfLineEndings()
    {
        var events = await ParseAsync("event: message\ndata: hello\n\n");
        var ev = Assert.Single(events);
        Assert.Equal("hello", ev.Data);
        Assert.Equal("message", ev.Event);
    }

    [Fact]
    public async Task ParsesCrLfLineEndings()
    {
        var events = await ParseAsync("event: message\r\ndata: hello\r\n\r\n");
        Assert.Equal("hello", Assert.Single(events).Data);
    }

    [Fact]
    public async Task ParsesLoneCrLineEndings()
    {
        // 旧实现只按 \n 分行、且只去掉行尾 \r，孤立 CR 会让整块粘在一起、最终 "stream ended without a response"。
        var events = await ParseAsync("event: message\rdata: hello\r\r");
        Assert.Equal("hello", Assert.Single(events).Data);
    }

    [Fact]
    public async Task CrLfSplitAcrossChunksDoesNotDispatchSpuriousEvent()
    {
        // 第一个 chunk 以 CR 结束，第二个 chunk 以 LF 开始：LF 属于同一 CRLF，不应被当成空行分发事件。
        var events = await ParseAsync("data: one\r", "\ndata: two\n\n");
        // 两段 data 在同一事件里以 \n 连接，且只分发一次。
        var ev = Assert.Single(events);
        Assert.Equal("one\ntwo", ev.Data);
    }

    /// <summary>每次 ReadAsync 返回一个预设分片；全部读完后返回 0。</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        public ChunkStream(IEnumerable<string> chunks)
        {
            _chunks = new Queue<byte[]>(chunks.Select(c => Encoding.UTF8.GetBytes(c)));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunks.Count == 0) return ValueTask.FromResult(0);
            var chunk = _chunks.Dequeue();
            var n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_chunks.Count == 0) return Task.FromResult(0);
            var chunk = _chunks.Dequeue();
            var n = Math.Min(chunk.Length, count);
            Array.Copy(chunk, 0, buffer, offset, n);
            return Task.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
