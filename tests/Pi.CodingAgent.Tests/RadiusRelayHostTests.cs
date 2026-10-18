using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Experimental;
using Pi.Client;
using Pi.Protocol;
using Pi.Server;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the Radius relay session half (4g-3d-2b): the host loop, the accepted byte
/// connections, the client transport, its factory, and the reconnect helper.
/// </summary>
public class RadiusRelayHostTests
{
    private const string ServerId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";
    private const string Gateway = "https://radius.pi.dev";
    private const string ConnectionId = "9c8b7a65-4d3e-4f21-b0a9-1e2d3c4b5a60";

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

    private static RadiusRelayAuthResolver InlineTokenAuth()
        => new(new AuthInput.Token("token"), Gateway) { Env = _ => null };

    private static RadiusRelayAuthResolver OfflineAuth()
        => new RadiusRelayAuthResolver(gateway: Gateway) { Env = _ => "1" };

    /// <summary>A host wired to scripted fake sockets; the handshake inputs are recorded.</summary>
    private sealed class HostRig
    {
        public List<RadiusRelayHostStatus> Statuses { get; } = [];

        public List<FakeRelayWebSocket> Sockets { get; } = [];

        public List<(string Url, string Protocol, string Authorization)> Handshakes { get; } = [];

        public Func<IByteConnection, IByteConnectionHandler>? Accept { get; init; }

        public RecordingByteConnectionHandler Handler { get; private set; } = null!;

        public IByteConnection? Connection { get; private set; }

        public RadiusRelayHost CreateHost(RadiusRelayAuthResolver? auth = null)
        {
            var socketIndex = 0;
            return new RadiusRelayHost(new RadiusRelayHostOptions
            {
                ServerId = ServerId,
                Auth = auth ?? InlineTokenAuth(),
                Accept = connection =>
                {
                    Handler = new RecordingByteConnectionHandler();
                    Connection = connection;
                    return Accept?.Invoke(connection) ?? Handler;
                },
                WebSocketFactory = (url, protocol, authorization) =>
                {
                    Handshakes.Add((url, protocol, authorization));
                    return Sockets[socketIndex++];
                },
                OnStatus = status => Statuses.Add(status),
            });
        }
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Start_ConnectsAndServesUntilTheSocketDrops()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();

        // The loop runs synchronously into OpenAsync's first await, so the handshake subscription
        // is already in place when Start returns.
        host.Start();

        Assert.Equal("wss://radius.pi.dev/v1/session-relays/" + ServerId + "/connect", rig.Handshakes[0].Url);
        Assert.Equal(RadiusRelay.HostSubprotocol, rig.Handshakes[0].Protocol);
        Assert.Equal("Bearer token", rig.Handshakes[0].Authorization);

        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);
        await WaitUntilAsync(() => rig.Statuses.Count(status => status is RadiusRelayHostStatus.Connected) == 1);

        // An orderly close resolves serve; the loop treats it as a drop and retries.
        socket.RaiseClosed(1000, "");
        await WaitUntilAsync(() => rig.Statuses.Any(status => status is RadiusRelayHostStatus.Retrying));
        var retrying = rig.Statuses.OfType<RadiusRelayHostStatus.Retrying>().Single();
        Assert.Equal("Radius relay host disconnected", retrying.Error);

        await host.CloseAsync();
        await host.CloseAsync();
    }

    [Fact]
    public async Task Start_ReportsNotAuthenticatedWhileAuthIsMissing()
    {
        var rig = new HostRig();
        var host = rig.CreateHost(OfflineAuth());

        host.Start();
        await WaitUntilAsync(() => rig.Statuses.Count > 0);

        var status = Assert.Single(rig.Statuses);
        Assert.IsType<RadiusRelayHostStatus.NotAuthenticated>(status);
        Assert.Empty(rig.Handshakes);

        await host.CloseAsync();
    }

    [Fact]
    public async Task Start_RetriesAfterADropWithBackoff()
    {
        var rig = new HostRig();
        var first = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var second = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(first);
        rig.Sockets.Add(second);
        var host = rig.CreateHost();

        host.Start();
        first.RaiseOpened();
        await WaitUntilAsync(() => rig.Statuses.Count(status => status is RadiusRelayHostStatus.Connected) == 1);

        first.RaiseClosed(1006, "boom");
        await WaitUntilAsync(() => rig.Statuses.Count(status => status is RadiusRelayHostStatus.Connecting) == 2);

        // The first backoff is one second; the second connecting proves the loop came around.
        second.RaiseOpened();
        await WaitUntilAsync(() => second.Serving);
        await WaitUntilAsync(() => rig.Statuses.Count(status => status is RadiusRelayHostStatus.Connected) == 2);
        Assert.Equal(2, rig.Handshakes.Count);

        await host.CloseAsync();
    }

    // ---------------------------------------------------------------- serving

    [Fact]
    public async Task Serve_RoutesDataFramesToTheAcceptedConnection()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        var handler = rig.Handler;
        Assert.Empty(handler.Data);

        socket.RaiseMessage(RadiusRelayMessage.BinaryMessage(
            RadiusRelayFrames.EncodeDataFrame(ConnectionId, [1, 2, 3])));

        var chunk = Assert.Single(handler.Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunk);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_AnswersAnUnknownConnectionWithAClose()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.BinaryMessage(
            RadiusRelayFrames.EncodeDataFrame(ConnectionId, [7])));
        await WaitUntilAsync(() => socket.SentText.Count > 0);

        Assert.Equal(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}","code":1000}""",
            Assert.Single(socket.SentText));
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_PingIsAnsweredWithAPong()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage("""{"version":1,"type":"ping"}"""));
        await WaitUntilAsync(() => socket.SentText.Count > 0);

        Assert.Equal("""{"version":1,"type":"pong"}""", Assert.Single(socket.SentText));
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_RemoteCloseNotifiesTheHandler()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        var handler = rig.Handler;

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}"}"""));
        await WaitUntilAsync(() => handler.CloseCount == 1);

        Assert.Empty(handler.Errors);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_RejectsAReusedConnectionId()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));

        await WaitUntilAsync(() => rig.Statuses.Any(status => status is RadiusRelayHostStatus.Retrying));
        Assert.Contains((4000, "Radius relay protocol error"), socket.Closes);
        var retrying = Assert.Single(rig.Statuses.OfType<RadiusRelayHostStatus.Retrying>());
        Assert.Equal("Radius relay reused a connection ID", retrying.Error);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_RejectsANonBinaryDataMessage()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage("not json"));

        await WaitUntilAsync(() => rig.Statuses.Any(status => status is RadiusRelayHostStatus.Retrying));
        Assert.Contains((4000, "Radius relay protocol error"), socket.Closes);
        var retrying = Assert.Single(rig.Statuses.OfType<RadiusRelayHostStatus.Retrying>());
        Assert.Equal("Invalid Radius relay control message", retrying.Error);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_ServerCloseSendsTheFinalChunkAndAClose()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));

        var connection = Assert.IsType<RelayServerByteConnection>(rig.Connection);
        await connection.CloseAsync([9, 9]);

        var frame = Assert.Single(socket.Sent);
        var parsed = RadiusRelayFrames.ParseDataFrame(frame);
        Assert.NotNull(parsed);
        Assert.Equal(ConnectionId, parsed.Value.ConnectionId);
        Assert.Equal(new byte[] { 9, 9 }, parsed.Value.Payload);
        Assert.Equal(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}","code":1000}""",
            Assert.Single(socket.SentText));
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_SendOnAClosedConnectionRejects()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}"}"""));
        await WaitUntilAsync(() => rig.Connection!.Closed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Connection!.SendAsync([1]));
        Assert.Equal("Radius relay connection is closed", error.Message);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_ClosesConnectionsWithAnErrorWhenTheSocketDrops()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        var handler = rig.Handler;

        socket.RaiseClosed(1006, "boom");
        await WaitUntilAsync(() => handler.Errors.Count > 0);

        var error = Assert.Single(handler.Errors);
        Assert.Equal("Radius relay host closed (1006: boom)", error.Message);
        Assert.Equal(0, handler.CloseCount);
        await host.CloseAsync();
    }

    [Fact]
    public async Task Serve_OrderlyCloseNotifiesHandlersWithOnClose()
    {
        var rig = new HostRig();
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        var handler = rig.Handler;

        socket.RaiseClosed(1000, "");
        await WaitUntilAsync(() => handler.CloseCount == 1);

        Assert.Empty(handler.Errors);
        await host.CloseAsync();
    }

    [Fact]
    public async Task OpenConnection_RefusedSynchronouslyGetsAProtocolErrorClose()
    {
        var rig = new HostRig
        {
            Accept = connection =>
            {
                _ = connection.CloseAsync();
                return new RecordingByteConnectionHandler();
            },
        };
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        rig.Sockets.Add(socket);
        var host = rig.CreateHost();
        host.Start();
        socket.RaiseOpened();
        await WaitUntilAsync(() => socket.Serving);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage(
            $$"""{"version":1,"type":"connection_open","connection_id":"{{ConnectionId}}"}"""));
        await WaitUntilAsync(() => socket.SentText.Count > 0);

        Assert.Equal(
            $$"""{"version":1,"type":"connection_close","connection_id":"{{ConnectionId}}","code":1012}""",
            Assert.Single(socket.SentText));
        await host.CloseAsync();
    }
}

/// <summary>Tests for the relay client half: the byte transport and its factory.</summary>
public class RadiusClientTransportTests
{
    private const string ServerId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";

    private sealed class RecordingHandlers : IByteTransportHandlers
    {
        public List<byte[]> Data { get; } = [];

        public int CloseCount { get; private set; }

        public List<Exception> Errors { get; } = [];

        public void OnData(byte[] chunk) => Data.Add(chunk);

        public void OnClose() => CloseCount++;

        public void OnError(Exception error) => Errors.Add(error);
    }

    [Fact]
    public async Task SendAsync_WritesTheChunkThroughTheOrderedWriter()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var transport = new RadiusClientByteTransport(socket, new RecordingHandlers());

        await transport.SendAsync([1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(socket.Sent));
    }

    [Fact]
    public async Task SendAsync_RejectsAfterClose()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var transport = new RadiusClientByteTransport(socket, new RecordingHandlers());

        transport.Close();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync([1]));
        Assert.Equal("Radius relay client is closed", error.Message);
    }

    [Fact]
    public void Close_ClosesTheSocketOnce()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var transport = new RadiusClientByteTransport(socket, new RecordingHandlers());

        transport.Close();
        transport.Close();

        Assert.Equal((1000, "Pi client closed"), Assert.Single(socket.Closes));
    }

    [Fact]
    public void OnData_ForwardsBinaryMessages()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var handlers = new RecordingHandlers();
        _ = new RadiusClientByteTransport(socket, handlers);

        socket.RaiseMessage(RadiusRelayMessage.BinaryMessage([4, 5]));

        Assert.Equal(new byte[] { 4, 5 }, Assert.Single(handlers.Data));
    }

    [Fact]
    public void OnData_RejectsTextMessages()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var handlers = new RecordingHandlers();
        _ = new RadiusClientByteTransport(socket, handlers);

        socket.RaiseMessage(RadiusRelayMessage.TextMessage("hello"));

        var error = Assert.Single(handlers.Errors);
        Assert.Equal("Radius relay client received a non-binary message", error.Message);
        Assert.Contains((4001, "Radius relay transport error"), socket.Closes);
    }

    [Fact]
    public void OnClose_ForwardsOnCloseExactlyOnce()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var handlers = new RecordingHandlers();
        var transport = new RadiusClientByteTransport(socket, handlers);

        socket.RaiseClosed(1006, "");
        socket.RaiseClosed(1006, "");

        Assert.Equal(1, handlers.CloseCount);
        Assert.Empty(handlers.Errors);
    }

    [Fact]
    public void OnError_ReportsAndClosesWithATransportError()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol };
        var handlers = new RecordingHandlers();
        var transport = new RadiusClientByteTransport(socket, handlers);

        socket.RaiseErrored(new InvalidOperationException("boom"), null);

        Assert.Equal("boom", Assert.Single(handlers.Errors).Message);
        Assert.Contains((4001, "Radius relay transport error"), socket.Closes);
    }

    [Fact]
    public async Task CreateTransportFactory_OpensWithTheClientSubprotocol()
    {
        var socket = new FakeRelayWebSocket { Protocol = RadiusRelay.ClientSubprotocol };
        var handshakes = new List<(string Url, string Protocol, string Authorization)>();
        var options = new RadiusClientTransportOptions
        {
            ServerId = ServerId,
            Auth = new RadiusRelayAuthResolver(new AuthInput.Token("token"), "https://radius.pi.dev") { Env = _ => null },
            WebSocketFactory = (url, protocol, authorization) =>
            {
                handshakes.Add((url, protocol, authorization));
                return socket;
            },
        };

        // The factory delegate runs synchronously into OpenAsync's first await, so the open
        // subscription is already in place when it returns the pending task.
        var pending = RadiusRelayClients.CreateTransportFactory(options)(new RecordingHandlers());
        socket.RaiseOpened();
        var transport = await pending;

        Assert.IsType<RadiusClientByteTransport>(transport);
        Assert.Equal("wss://radius.pi.dev/v1/session-relays/" + ServerId + "/connect", handshakes[0].Url);
        Assert.Equal(RadiusRelay.ClientSubprotocol, handshakes[0].Protocol);
        Assert.Equal("Bearer token", handshakes[0].Authorization);
    }

    [Fact]
    public async Task CreateTransportFactory_ResolvesAuthAsRequired()
    {
        var options = new RadiusClientTransportOptions
        {
            ServerId = ServerId,
            // Offline mode throws only when required — the factory must ask for it.
            Auth = new RadiusRelayAuthResolver(gateway: "https://radius.pi.dev") { Env = _ => "1" },
            WebSocketFactory = (_, _, _) => new FakeRelayWebSocket { Protocol = RadiusRelay.HostSubprotocol },
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RadiusRelayClients.CreateTransportFactory(options)(new RecordingHandlers()));
        Assert.Equal("Radius relay connections are unavailable in offline mode", error.Message);
    }
}

/// <summary>Tests for <see cref="RadiusClientReconnect"/>.</summary>
public class RadiusClientReconnectTests
{
    private static RpcTarget.SessionTarget Session(string sessionId)
        => new(ServerId, sessionId, "attach-1");

    private const string ServerId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";

    private static (RadiusClientReconnect Reconnect, StubReconnectClient Client, List<string> Reattached) Create(
        RpcTarget.SessionTarget? attachment, bool connected)
    {
        var client = new StubReconnectClient { Attachment = attachment };
        if (connected)
        {
            client.Connected = true;
        }

        var reattached = new List<string>();
        var reconnect = new RadiusClientReconnect(client, sessionId =>
        {
            reattached.Add(sessionId);
            return Task.CompletedTask;
        });
        return (reconnect, client, reattached);
    }

    [Fact]
    public async Task Disconnect_ReconnectsAndReattachesTheLastSession()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);

        client.Disconnect("dropped");
        await WaitUntilAsync(() => reattached.Count > 0);

        Assert.Equal(1, client.ReconnectCalls);
        Assert.Equal(["session-1"], reattached);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task AttachmentChange_TracksTheNewSession()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);

        client.RaiseAttachment(Session("session-2"));
        client.Disconnect("dropped");
        await WaitUntilAsync(() => reattached.Count > 0);

        Assert.Equal(["session-2"], reattached);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task Detach_WhileConnected_ClearsTheDesiredSession()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);

        client.RaiseAttachment(null);
        client.Disconnect("dropped");
        await WaitUntilAsync(() => client.ReconnectCalls > 0);

        Assert.Empty(reattached);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task Detach_WhileDisconnected_KeepsTheDesiredSession()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);

        client.Disconnect("dropped");
        client.RaiseAttachment(null);
        await WaitUntilAsync(() => reattached.Count > 0);

        Assert.Equal(["session-1"], reattached);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_RetriesWithBackoffAfterAFailure()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);
        client.ReconnectError = new InvalidOperationException("handshake refused");

        client.Disconnect("dropped");

        // The first attempt fails after the initial backoff; the second doubles the wait.
        await WaitUntilAsync(() => client.ReconnectCalls == 2);
        Assert.Empty(reattached);

        client.ReconnectError = null;
        await WaitUntilAsync(() => client.ReconnectCalls == 3 && reattached.Count > 0);

        Assert.Equal(["session-1"], reattached);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_DisconnectsWhenTheFailureLeavesItConnected()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);
        client.ReconnectError = new InvalidOperationException("handshake refused");
        client.StayConnectedOnFailure = true;

        client.Disconnect("dropped");
        await WaitUntilAsync(() => client.DisconnectReasons.Count > 1);

        Assert.Contains("handshake refused", client.DisconnectReasons);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectLoop_RunsOnceAtATime()
    {
        var (reconnect, client, reattached) = Create(Session("session-1"), connected: true);

        client.Disconnect("dropped");
        client.Disconnect("dropped again");
        await WaitUntilAsync(() => reattached.Count > 0);

        Assert.Equal(1, client.ReconnectCalls);
        await reconnect.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_StopsTheLoopAndDisconnects()
    {
        var (reconnect, client, _) = Create(Session("session-1"), connected: true);

        await reconnect.DisposeAsync();

        Assert.Equal(0, client.ReconnectCalls);
        Assert.Contains("Radius reconnect stopped", client.DisconnectReasons);

        // Later disconnects no longer start a loop.
        client.Disconnect("after dispose");
        await Task.Delay(50);
        Assert.Equal(0, client.ReconnectCalls);
    }

    [Fact]
    public async Task Dispose_WhileDisconnected_DoesNotDisconnectAgain()
    {
        var (reconnect, client, _) = Create(Session("session-1"), connected: true);

        client.Disconnect("dropped");
        await reconnect.DisposeAsync();

        Assert.DoesNotContain("Radius reconnect stopped", client.DisconnectReasons);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was never met");
            }

            await Task.Delay(5);
        }
    }
}
