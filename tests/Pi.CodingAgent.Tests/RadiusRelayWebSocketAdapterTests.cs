using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Integration tests for <see cref="ClientWebSocketRelaySocket"/>, the production relay socket.
/// They speak to a minimal hand-rolled WebSocket server (a TcpListener that performs the RFC 6455
/// handshake and frames by hand) because the test project has no ASP.NET Core WebSocket host.
/// </summary>
public class RadiusRelayWebSocketAdapterTests : IAsyncDisposable
{
    private const string GatewayHost = "127.0.0.1";
    private const string ServerId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private MiniWebSocketServer? _server;

    private Task<MiniWebSocketServer> StartServerAsync(string protocol)
    {
        var server = new MiniWebSocketServer(protocol);
        _server = server;
        return Task.FromResult(server);
    }

    /// <summary>One message, captured once; a completed TCS ignores later results.</summary>
    private static Task<RadiusRelayMessage> SubscribeOnce(IRadiusRelayWebSocket socket)
    {
        var received = new TaskCompletionSource<RadiusRelayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.MessageReceived += message => received.TrySetResult(message);
        return received.Task;
    }

    /// <summary>The gateway origin the production code would hand the socket factory.</summary>
    private static string RelayUrl(MiniWebSocketServer server)
        => RadiusRelay.WebSocketUrl($"http://{GatewayHost}:{server.Port}", ServerId);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was never met");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task OpenAsync_NegotiatesTheSubprotocolAndSendsTheBearerHeader()
    {
        var server = await StartServerAsync(RadiusRelay.HostSubprotocol);

        var socket = await RadiusRelaySockets.OpenAsync(
            RelayUrl(server),
            RadiusRelay.HostSubprotocol,
            "token",
            RadiusRelaySockets.DefaultWebSocketFactory);

        Assert.Equal(RadiusRelay.HostSubprotocol, socket.Protocol);
        Assert.Equal(1, socket.ReadyState);
        Assert.Contains("authorization: Bearer token", server.RequestHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Messages_RoundTripAsTextAndBinary()
    {
        var server = await StartServerAsync(RadiusRelay.HostSubprotocol);

        var socket = await RadiusRelaySockets.OpenAsync(
            RelayUrl(server),
            RadiusRelay.HostSubprotocol,
            "token",
            RadiusRelaySockets.DefaultWebSocketFactory);

        var first = SubscribeOnce(socket);
        await server.SendTextAsync("""{"version":1,"type":"pong"}""");
        var text = await first;
        Assert.Equal("""{"version":1,"type":"pong"}""", text.Text);

        var second = SubscribeOnce(socket);
        await server.SendBinaryAsync([1, 2, 3, 4]);
        var binary = await second;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, binary.Binary);

        socket.Send([9, 8]);
        await WaitUntilAsync(() => server.Received.Count > 0);
        Assert.True(server.Received.TryPeek(out var echoed));
        Assert.Equal(new byte[] { 9, 8 }, echoed.Payload);
        Assert.Equal(0x2, echoed.Opcode);
    }

    [Fact]
    public async Task Messages_ReassembleFragmentedFrames()
    {
        var server = await StartServerAsync(RadiusRelay.HostSubprotocol);

        var socket = await RadiusRelaySockets.OpenAsync(
            RelayUrl(server),
            RadiusRelay.HostSubprotocol,
            "token",
            RadiusRelaySockets.DefaultWebSocketFactory);

        var received = SubscribeOnce(socket);
        await server.SendFragmentedBinaryAsync([1, 2, 3], [4, 5, 6]);

        var message = await received;
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, message.Binary);
    }

    [Fact]
    public async Task Close_ReportsTheServerStatusCodeAndReason()
    {
        var server = await StartServerAsync(RadiusRelay.HostSubprotocol);

        var socket = await RadiusRelaySockets.OpenAsync(
            RelayUrl(server),
            RadiusRelay.HostSubprotocol,
            "token",
            RadiusRelaySockets.DefaultWebSocketFactory);

        var closed = new TaskCompletionSource<(int Code, string Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Closed += (code, reason) => closed.TrySetResult((code, reason));

        await server.SendCloseAsync(1001, "going away");

        var (code, reason) = await closed.Task;
        Assert.Equal(1001, code);
        Assert.Equal("going away", reason);
    }

    [Fact]
    public async Task OpenAsync_RejectsANonWebSocketUrl()
    {
        // undici's WebSocket rejects a non-WebSocket scheme from its constructor; the adapter
        // throws the same way so the failure surfaces out of the factory.
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => RadiusRelaySockets.OpenAsync(
                $"http://{GatewayHost}:1",
                RadiusRelay.HostSubprotocol,
                "token",
                RadiusRelaySockets.DefaultWebSocketFactory));
        Assert.Contains("ws://", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_RejectsWhenTheConnectIsRefused()
    {
        // Nothing listens on the port, so the connect fails deterministically and the handshake
        // promise rejects with the transport error instead of hanging.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var error = await Assert.ThrowsAsync<WebSocketException>(
            () => RadiusRelaySockets.OpenAsync(
                $"ws://{GatewayHost}:{port}",
                RadiusRelay.HostSubprotocol,
                "token",
                RadiusRelaySockets.DefaultWebSocketFactory));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}

/// <summary>A frame in the server→client or client→server direction.</summary>
internal readonly record struct WireFrame(byte[] Payload, int Opcode);

/// <summary>
/// The smallest server that satisfies <see cref="ClientWebSocket"/>: the RFC 6455 handshake, then
/// unmasked frames out and masked frames in. One connection at a time is plenty for these tests.
/// </summary>
internal sealed class MiniWebSocketServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _protocol;
    private readonly ConcurrentQueue<WireFrame> _received = [];
    private readonly Task _loop;

    public MiniWebSocketServer(string protocol)
    {
        // Start before the accept loop is scheduled: AcceptTcpClientAsync throws on a listener
        // that has not been started yet.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _protocol = protocol;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<WireFrame> Received => _received;

    public string RequestHeaders { get; private set; } = "";

    public async Task SendTextAsync(string text) => await SendFrameAsync(Encoding.UTF8.GetBytes(text), 0x1);

    public async Task SendBinaryAsync(byte[] payload) => await SendFrameAsync(payload, 0x2);

    public async Task SendCloseAsync(int code, string reason)
    {
        var payload = new byte[2 + Encoding.UTF8.GetByteCount(reason)];
        payload[0] = (byte)(code >> 8);
        payload[1] = (byte)code;
        Encoding.UTF8.GetBytes(reason, payload.AsSpan(2));
        await SendFrameAsync(payload, 0x8);
    }

    /// <summary>One binary message split across two continuation-free frames.</summary>
    public async Task SendFragmentedBinaryAsync(byte[] first, byte[] second)
    {
        await SendFrameAsync(first, 0x2, fin: false);
        await SendFrameAsync(second, 0x0, fin: true);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // The listener stop surfaces as a cancellation on the accept loop.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            // Publish before the 101 goes out: the client's handshake resolves only after it
            // arrives, so any test send is ordered after this assignment.
            Volatile.Write(ref _current, stream);
            var request = await ReadHeadersAsync(stream);
            RequestHeaders = request;
            var key = ExtractHeader(request, "Sec-WebSocket-Key");
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n" +
                $"Sec-WebSocket-Protocol: {_protocol}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

            while (true)
            {
                var (payload, opcode) = await ReadFrameAsync(stream);
                if (opcode == 0x8)
                {
                    // Echo the close so the client's closing handshake completes.
                    await WriteFrameAsync(stream, payload, 0x8, fin: true);
                    return;
                }

                _received.Enqueue(new WireFrame(payload, opcode));
            }
        }
    }

    private async Task SendFrameAsync(byte[] payload, int opcode, bool fin = true)
    {
        // The server holds one connection for the test's lifetime; the stream is captured by the
        // serve loop, so writes go through the last accepted client.
        var stream = Volatile.Read(ref _current);
        if (stream is null) return;
        await WriteFrameAsync(stream, payload, opcode, fin);
    }

    private NetworkStream? _current;

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, int opcode, bool fin)
    {
        var header = new List<byte> { (byte)((fin ? 0x80 : 0x00) | opcode) };
        if (payload.Length <= 125)
        {
            header.Add((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)payload.Length);
        }
        else
        {
            header.Add(127);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                header.Add((byte)((ulong)payload.Length >> shift));
            }
        }

        header.AddRange(payload);
        await stream.WriteAsync(header.ToArray());
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[1];
        while (!EndsWithHeaders(buffer))
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            buffer.Add(chunk[0]);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static bool EndsWithHeaders(List<byte> buffer)
        => buffer.Count >= 4
            && buffer[^1] == (byte)'\n'
            && buffer[^2] == (byte)'\r'
            && buffer[^3] == (byte)'\n'
            && buffer[^4] == (byte)'\r';

    private static string ExtractHeader(string request, string name)
    {
        foreach (var line in request.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return "";
    }

    private static async Task<WireFrame> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        await ReadExactAsync(stream, header);
        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        var length = header[1] & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactAsync(stream, extended);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactAsync(stream, extended);
            length = 0;
            foreach (var b in extended)
            {
                length = (length << 8) | b;
            }
        }

        byte[]? mask = null;
        if (masked)
        {
            mask = new byte[4];
            await ReadExactAsync(stream, mask);
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload);
        if (mask is not null)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(payload[index] ^ mask[index % 4]);
            }
        }

        return new WireFrame(payload, opcode);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
