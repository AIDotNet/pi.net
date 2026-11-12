using System.Text;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 上游 1.5.1 #11729：SSE 事件超过 maxMessageBytes 抛错时，必须 cancel（释放）response body，
/// 否则带心跳的长连接会被这个失败请求一直占住。原错误要照常抛出，cancel 失败不能盖掉它。
/// </summary>
public class SseCancelOnErrorTests
{
    [Fact]
    public async Task OversizedEvent_CancelsResponseBody()
    {
        var stream = new TrackingStream("data: " + new string('x', 256) + "\n\n");
        var options = new ConsumeSseOptions
        {
            MaxEventBytes = 64,
            OnEvent = _ => { },
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SseStream.ConsumeAsync(stream, options));
        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stream.Disposed, "response body must be cancelled/disposed when the size limit rejects an event");
    }

    [Fact]
    public async Task NormalStream_IsNotForceCancelledByConsumer()
    {
        // 正常读到 EOF 不应由解析器主动 Dispose（连接归属由调用方 response using 管理）。
        var stream = new TrackingStream("data: hello\n\n");
        var events = new List<SseEvent>();
        await SseStream.ConsumeAsync(stream, new ConsumeSseOptions { OnEvent = events.Add });

        Assert.Equal("hello", Assert.Single(events).Data);
        Assert.False(stream.Disposed);
    }

    /// <summary>记录是否被 Dispose（cancel）的只读流；只吐一个分片后 EOF。</summary>
    private sealed class TrackingStream : Stream
    {
        private readonly byte[] _chunk;
        private int _offset;
        public bool Disposed { get; private set; }

        public TrackingStream(string content) => _chunk = Encoding.UTF8.GetBytes(content);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _chunk.Length) return ValueTask.FromResult(0);
            var n = Math.Min(_chunk.Length - _offset, buffer.Length);
            _chunk.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset >= _chunk.Length) return Task.FromResult(0);
            var n = Math.Min(_chunk.Length - _offset, count);
            Array.Copy(_chunk, _offset, buffer, offset, n);
            _offset += n;
            return Task.FromResult(n);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
