using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the Radius relay protocol core (4g-3d-2): the data frame codec, the control message
/// grammar, the gateway URL, the ordered writer, and the WebSocket opening handshake.
/// </summary>
public class RadiusRelayProtocolTests
{
    private const string ConnectionId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";

    // ---------------------------------------------------------------- frames

    [Fact]
    public void DataFrame_RoundTripsTheConnectionIdAndPayload()
    {
        var payload = Encoding.UTF8.GetBytes("hello relay");

        var frame = RadiusRelayFrames.EncodeDataFrame(ConnectionId, payload);
        var parsed = RadiusRelayFrames.ParseDataFrame(frame);

        Assert.NotNull(parsed);
        Assert.Equal(ConnectionId, parsed.Value.ConnectionId);
        Assert.Equal(payload, parsed.Value.Payload);
    }

    [Fact]
    public void DataFrame_HasTheDocumentedHeaderLayout()
    {
        var frame = RadiusRelayFrames.EncodeDataFrame(ConnectionId, [0xff]);

        Assert.Equal(19, frame.Length);
        Assert.Equal(1, frame[0]);
        Assert.Equal(1, frame[1]);
        // UUID 的 16 个字节按序跟在版本/类型之后（无分隔符的十六进制）。
        Assert.Equal(
            Convert.FromHexString(ConnectionId.Replace("-", "")),
            frame.AsSpan(2, 16).ToArray());
        Assert.Equal(0xff, frame[18]);
    }

    [Fact]
    public void DataFrame_CarriesAnEmptyPayload()
    {
        var frame = RadiusRelayFrames.EncodeDataFrame(ConnectionId, []);

        Assert.Equal(RadiusRelayFrames.HeaderBytes, frame.Length);
        var parsed = RadiusRelayFrames.ParseDataFrame(frame);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.Value.Payload);
    }

    [Fact]
    public void DataFrame_RejectsAnInvalidConnectionId()
    {
        foreach (var invalid in new[] { "", "not-a-uuid", ConnectionId.ToUpperInvariant(),
            "3f6d4b2e-4c1a-3f7b-9e2d-8a5c0d1e2f3b", "3f6d4b2e-4c1a-4f7b-7e2d-8a5c0d1e2f3b" })
        {
            Assert.Throws<ArgumentException>(() => RadiusRelayFrames.EncodeDataFrame(invalid, [1]));
        }
    }

    [Fact]
    public void DataFrame_RejectsAnythingThatIsNotAFrame()
    {
        // 太短、错版本、错类型。
        Assert.Null(RadiusRelayFrames.ParseDataFrame(new byte[17]));
        Assert.Null(RadiusRelayFrames.ParseDataFrame([2, 1, .. new byte[16]]));
        Assert.Null(RadiusRelayFrames.ParseDataFrame([1, 2, .. new byte[16]]));
        // 16 个字节重建出的不是 UUIDv4（版本位/变体位不符）。
        Assert.Null(RadiusRelayFrames.ParseDataFrame([1, 1, .. new byte[16]]));
        Assert.Null(RadiusRelayFrames.ParseDataFrame([1, 1, .. Enumerable.Repeat((byte)0xff, 16)]));
    }

    // ---------------------------------------------------------------- control messages

    [Fact]
    public void ControlMessage_ParsesPingAndPong()
    {
        Assert.Equal(new RadiusRelayFrames.HostControlMessage(1, "ping", null, null),
            RadiusRelayFrames.ParseHostControlMessage("""{"version":1,"type":"ping"}"""));
        Assert.Equal(new RadiusRelayFrames.HostControlMessage(1, "pong", null, null),
            RadiusRelayFrames.ParseHostControlMessage("""{"version":1,"type":"pong"}"""));
    }

    [Fact]
    public void ControlMessage_ParsesConnectionOpenAndClose()
    {
        var open = RadiusRelayFrames.ParseHostControlMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}""");
        Assert.Equal(new RadiusRelayFrames.HostControlMessage(1, "connection_open", ConnectionId, null), open);

        var close = RadiusRelayFrames.ParseHostControlMessage(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}","code":1012}""");
        Assert.Equal(new RadiusRelayFrames.HostControlMessage(1, "connection_close", ConnectionId, 1012), close);
    }

    [Fact]
    public void ControlMessage_DropsTheCodeOnConnectionOpen()
    {
        // TS validates a code on connection_open but returns the literal without it.
        var open = RadiusRelayFrames.ParseHostControlMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}","code":4000}""");
        Assert.Equal(new RadiusRelayFrames.HostControlMessage(1, "connection_open", ConnectionId, null), open);
    }

    [Fact]
    public void ControlMessage_ExtraFieldsAreIgnored()
    {
        var message = RadiusRelayFrames.ParseHostControlMessage(
            $$"""{"version":1,"type":"ping","future":true}""");

        Assert.Equal("ping", message.Type);
    }

    [Theory]
    [InlineData("""{"version":1,"type":"connection_open","connection_id":"3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b","code":999}""")]
    [InlineData("""{"version":1,"type":"connection_open","connection_id":"3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b","code":5000}""")]
    [InlineData("""{"version":1,"type":"connection_open","connection_id":"3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b","code":1000.5}""")]
    [InlineData("""{"version":1,"type":"connection_open","connection_id":"3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b","code":"1000"}""")]
    [InlineData("""{"version":1,"type":"connection_open","connection_id":"nope"}""")]
    [InlineData("""{"version":1,"type":"connection_open"}""")]
    [InlineData("""{"version":1,"type":"frobnicate"}""")]
    [InlineData("""{"version":2,"type":"ping"}""")]
    [InlineData("""{"version":"1","type":"ping"}""")]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""["ping"]""")]
    [InlineData(""""ping"""")]
    [InlineData("""not json""")]
    public void ControlMessage_RejectsMalformedInput(string value)
    {
        Assert.Throws<InvalidOperationException>(() => RadiusRelayFrames.ParseHostControlMessage(value));
    }

    [Fact]
    public void ControlMessage_VersionErrorsAreDistinct()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RadiusRelayFrames.ParseHostControlMessage("""{"version":2,"type":"ping"}"""));
        Assert.Contains("Unsupported Radius relay control version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlMessage_EncodesWithTheCodeOnlyWhenPresent()
    {
        Assert.Equal("""{"version":1,"type":"pong"}""",
            RadiusRelayFrames.EncodeHostControlMessage("pong"));
        Assert.Equal($$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}"}""",
            RadiusRelayFrames.EncodeHostControlMessage("connection_close", ConnectionId));
        Assert.Equal($$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}","code":1012}""",
            RadiusRelayFrames.EncodeHostControlMessage("connection_close", ConnectionId, 1012));
    }

    // ---------------------------------------------------------------- url

    [Fact]
    public void WebSocketUrl_SwapsTheSchemeAndKeepsThePath()
    {
        Assert.Equal("wss://radius.pi.dev/v1/session-relays/srv-1/connect",
            RadiusRelay.WebSocketUrl("https://radius.pi.dev", "srv-1"));
        Assert.Equal("ws://127.0.0.1:8787/v1/session-relays/srv-1/connect",
            RadiusRelay.WebSocketUrl("http://127.0.0.1:8787", "srv-1"));
    }

    [Fact]
    public void WebSocketUrl_DropsADefaultPortAndKeepsAnExplicitOne()
    {
        Assert.Equal("wss://radius.pi.dev/v1/session-relays/srv-1/connect",
            RadiusRelay.WebSocketUrl("https://radius.pi.dev:443", "srv-1"));
        Assert.Equal("ws://localhost:3000/v1/session-relays/srv-1/connect",
            RadiusRelay.WebSocketUrl("http://localhost:3000", "srv-1"));
    }

    [Fact]
    public void WebSocketUrl_RejectsAnUnsupportedScheme()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RadiusRelay.WebSocketUrl("ftp://radius.pi.dev", "srv-1"));
        Assert.Contains("Unsupported Radius gateway protocol", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- ordered writer

    [Fact]
    public async Task Writer_SendsInOrder()
    {
        var socket = new FakeRelayWebSocket();
        var writer = new OrderedWebSocketWriter(socket);

        var first = writer.SendAsync("one");
        var second = writer.SendAsync("two");
        var third = writer.SendAsync(RadiusRelayFrames.EncodeDataFrame(ConnectionId, [3]));

        await Task.WhenAll(first, second, third);

        Assert.Equal(["one", "two"], socket.SentText);
        var frame = Assert.Single(socket.Sent);
        Assert.Equal(RadiusRelayFrames.HeaderBytes + 1, frame.Length);
        Assert.Equal(3, frame[^1]);
    }

    [Fact]
    public async Task Writer_RejectsAfterClose()
    {
        var socket = new FakeRelayWebSocket();
        var writer = new OrderedWebSocketWriter(socket);

        writer.Close();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync("one"));
        Assert.Contains("Radius relay WebSocket is closed", error.Message, StringComparison.Ordinal);
        Assert.Empty(socket.SentText);
    }

    [Fact]
    public async Task Writer_RejectsWhileTheSocketIsNotOpen()
    {
        var socket = new FakeRelayWebSocket { ReadyState = 0 };
        var writer = new OrderedWebSocketWriter(socket);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync("one"));
        Assert.Contains("Radius relay WebSocket is closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_CapsThePendingBytes()
    {
        var socket = new FakeRelayWebSocket();
        var writer = new OrderedWebSocketWriter(socket);

        // The frame reaches the socket at once but stays pending in the drain loop (the fake never
        // drains on its own), so the queued bytes sit at the limit and the next frame is rejected.
        var blocked = writer.SendAsync(new byte[RadiusRelay.MaxPendingBytes]);
        await socket.FirstSend;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync([1]));
        Assert.Contains("pending byte limit", error.Message, StringComparison.Ordinal);

        socket.DrainAll();
        await blocked;
    }

    [Fact]
    public async Task Writer_WaitsForTheSocketToDrain()
    {
        var socket = new FakeRelayWebSocket();
        var writer = new OrderedWebSocketWriter(socket);

        var send = writer.SendAsync(new byte[2 * 1024 * 1024]);
        await socket.FirstSend;
        Assert.False(send.IsCompleted);
        Assert.Single(socket.Sent);

        socket.DrainAll();
        await send;
        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task Writer_CopiesThePayloadBeforeQueueing()
    {
        // TS copies ArrayBuffer inputs (value.slice(0)), so later caller mutation cannot corrupt a queued frame.
        var socket = new FakeRelayWebSocket();
        var writer = new OrderedWebSocketWriter(socket);

        var payload = new byte[] { 1, 2, 3 };
        var send = writer.SendAsync(payload);
        payload[0] = 0xff;

        await send;
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(socket.Sent));
    }

    // ---------------------------------------------------------------- opening handshake

    // The factory runs inside the synchronous prefix of OpenAsync, before its first await, so
    // raising an event after the call (rather than inside the factory) is the deterministic
    // stand-in for a real socket firing events from the event loop. TS has the same constraint:
    // undici never fires inside the constructor.

    [Fact]
    public async Task OpenAsync_ResolvesOnOpenWithTheExpectedSubprotocol()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var opened = RadiusRelaySockets.OpenAsync(
            "wss://radius.pi.dev/v1/session-relays/srv-1/connect",
            RadiusRelay.HostSubprotocol,
            "token",
            (_, _, authorization) =>
            {
                Assert.Equal("Bearer token", authorization);
                return socket;
            });

        socket.RaiseOpened();

        var connected = await opened;
        Assert.Same(socket, connected);
        Assert.Equal("arraybuffer", socket.BinaryType);
    }

    [Fact]
    public async Task OpenAsync_RejectsAnUnexpectedSubprotocol()
    {
        var socket = new FakeRelayWebSocket { Protocol = "other.v1" };
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseOpened();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Contains("unexpected WebSocket protocol \"other.v1\"", error.Message, StringComparison.Ordinal);
        Assert.Contains((1000, "Radius relay connection failed"), socket.Closes);
    }

    [Fact]
    public async Task OpenAsync_RejectsACloseBeforeConnecting()
    {
        var socket = new FakeRelayWebSocket();
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseClosed(1006, "going away");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Contains("closed before connecting (1006)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_RejectsAnErrorEvent()
    {
        var socket = new FakeRelayWebSocket();
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseErrored(null, "  handshake refused  ");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Equal("handshake refused", error.Message);
    }

    [Fact]
    public async Task OpenAsync_PrefersTheErrorObjectOverTheMessage()
    {
        var socket = new FakeRelayWebSocket();
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseErrored(new InvalidOperationException("refused"), "fallback");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Equal("refused", error.Message);
    }

    [Fact]
    public async Task OpenAsync_FallsBackWhenTheErrorObjectHasNoMessage()
    {
        var socket = new FakeRelayWebSocket();
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseErrored(new InvalidOperationException("  "), "from the message");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Equal("from the message", error.Message);
    }

    [Fact]
    public async Task OpenAsync_FallsBackToTheGenericMessage()
    {
        var socket = new FakeRelayWebSocket();
        var opened = RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
            (_, _, _) => socket);

        socket.RaiseErrored(null, null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => opened);
        Assert.Equal("Radius WebSocket connection failed", error.Message);
    }

    [Fact]
    public async Task OpenAsync_ThrowsWhenAlreadyCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // TS 的 signal?.throwIfAborted() 同样在进厂前就抛。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
                (_, _, _) => new FakeRelayWebSocket(), cancelled.Token));
    }

    [Fact]
    public async Task OpenAsync_RejectsACancellationDuringTheHandshake()
    {
        using var cancelled = new CancellationTokenSource();
        var socket = new FakeRelayWebSocket();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RadiusRelaySockets.OpenAsync("wss://radius.pi.dev/x", RadiusRelay.HostSubprotocol, "token",
                (_, _, _) =>
                {
                    cancelled.Cancel();
                    return socket;
                }, cancelled.Token));

        Assert.Contains("cancelled", error.Message, StringComparison.Ordinal);
        Assert.Contains((1000, "Radius relay connection failed"), socket.Closes);
    }

    [Fact]
    public async Task DelayAsync_CompletesAndHonoursCancellation()
    {
        await RadiusRelaySockets.DelayAsync(5, CancellationToken.None, unref: true);

        using var cancelled = new CancellationTokenSource();
        var pending = RadiusRelaySockets.DelayAsync(30_000, cancelled.Token, unref: false);
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
    }

}
