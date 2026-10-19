using System.Net.Sockets;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Client;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;
using Context = Pi.Chord.Context.Context;
using ClientConnectionState = Pi.Client.ConnectionState;
using DeltaOp = Pi.Chord.Delta.DeltaOp;
using DeltaPath = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;

namespace Pi.Server.Tests;

/// <summary>server.ts + client.ts 端到端：真实 Unix 域套接字上的握手、请求与断开。</summary>
public class ServerClientEndToEndTests
{
    private static readonly ServiceInstanceAddress Instance = new("inst-a", 1L);

    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f01";

    private sealed record Metadata(string Id) : ISessionMetadata;

    private sealed class EchoServerServiceHost : IRoutedServerServiceHost
    {
        public Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
            Context context)
            => Task.FromResult<IRoutedServerServiceAttachment>(new EchoAttachment());
    }

    private sealed class EchoAttachment : IRoutedServerServiceAttachment
    {
        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
            => Task.FromResult<object?>($"{call.ServiceId}.{call.Member}:{call.Args.Count}");

        public Task ReleaseAsync(Context context) => Task.CompletedTask;
    }

    private sealed class EchoHost : IServerHost<Metadata>
    {
        public IRoutedServerServiceHost ServerServices { get; } = new EchoServerServiceHost();

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
            => Task.FromResult(new Metadata(sessionId));

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// An attachment that answers a subscribe with the wire snapshot value tree — the shape the
    /// session-worker manager's attachment hands the server — and keeps the publish delegate so
    /// the test can push an update after the response.
    /// </summary>
    private sealed class SubscribingServerServiceHost : IRoutedServerServiceHost
    {
        public SubscribingAttachment Attachment { get; } = new();

        public Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
            Context context)
            => Task.FromResult<IRoutedServerServiceAttachment>(Attachment);
    }

    private sealed class SubscribingAttachment : IRoutedServerServiceAttachment
    {
        public string? SubscriptionId { get; private set; }

        public ServiceUpdatePublisher? Publish { get; private set; }

        public List<ServiceCall> Calls { get; } = [];

        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
        {
            Publish = publish;
            Calls.Add(call);
            if (ServiceWire.DecodeServiceControlCall(call) is not ServiceControlCall.Subscribe subscribe)
            {
                return Task.FromResult<object?>(null);
            }

            SubscriptionId = subscribe.SubscriptionId;
            var decoded = new ServiceSubscriptionSnapshot("fs", ServiceMode.Singleton,
            [
                new ServiceInstanceSnapshot(Instance,
                [
                    new ServiceMemberSnapshot.State("doc", 1L,
                        [new DeltaOp.Replace(new Dictionary<string, object?> { ["n"] = 1L })]),
                ]),
            ]);
            return Task.FromResult<object?>(ServiceWire.ToValue(
                new ServiceStateEncoder().EncodeSnapshot(decoded)));
        }

        public Task ReleaseAsync(Context context) => Task.CompletedTask;
    }

    private sealed class SubscribingHost : IServerHost<Metadata>
    {
        public SubscribingServerServiceHost Services { get; } = new();

        public IRoutedServerServiceHost ServerServices => Services;

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
            => Task.FromResult(new Metadata(sessionId));

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => throw new NotSupportedException();
    }

    private static string TempSocketPath()
        => Path.Combine(Path.GetTempPath(), $"pi-{Guid.NewGuid():N}"[..11] + ".sock");

    private static string TempSocketDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pd-{Guid.NewGuid():N}"[..11]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ClientOptions ClientOptionsFor(string path, string serverId = ServerId) => new()
    {
        ServerId = serverId,
        TransportFactory = UnixTransport.CreateUnixTransportFactory(new UnixTransportOptions { Path = path }),
    };

    [Fact]
    public async Task HandshakesAndRoutesRequestsOverUnixSocket()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();

            var states = new List<ClientConnectionState>();
            await using var client = new Pi.Client.Client(ClientOptionsFor(path));
            client.OnConnectionStateChange(change => states.Add(change.State));

            var hello = await client.ConnectAsync();
            Assert.Equal(ServerId, hello.ServerId);
            Assert.Equal(ServerId, client.Hello!.ServerId);
            Assert.True(client.Connected);
            Assert.Equal([ClientConnectionState.Connecting, ClientConnectionState.Connected], states);

            // 服务端域调用经真实 socket 往返。
            var result = await client.RequestAsync(
                new RpcTarget.ServerTarget(ServerId), new ServiceCall("echoer", "echo", ["a", "b"]));
            Assert.Equal("echoer.echo:2", result);

            client.Disconnect();
            Assert.Equal(ClientConnectionState.Disconnected, client.ConnectionState);
            Assert.False(client.Connected);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsServerIdMismatchAndDisposedClient()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();

            await using (var wrong = new Pi.Client.Client(ClientOptionsFor(
                path, "11111111-1111-4111-8111-111111111111")))
            {
                var error = await Assert.ThrowsAsync<ProtocolValidationError>(() => wrong.ConnectAsync());
                Assert.Contains("does not match", error.Message);
            }

            var client = new Pi.Client.Client(ClientOptionsFor(path));
            await client.ConnectAsync();
            await client.DisposeAsync();
            Assert.True(client.Disposed);
            await Assert.ThrowsAsync<ClientDisposedError>(() => client.ConnectAsync());
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SendsLargeRequestFramesWithoutTruncation()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();
            await using var client = new Pi.Client.Client(ClientOptionsFor(path));
            await client.ConnectAsync();

            var payload = new string('x', 4 * 1024 * 1024);
            var result = await AwaitWithTimeoutAsync(
                client.RequestAsync(new RpcTarget.ServerTarget(ServerId),
                    new ServiceCall("echoer", "echo", [payload])),
                15_000);
            Assert.Equal("echoer.echo:1", result);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DiscoveryReturnsEmptyForMissingDirectoryButPropagatesOtherFilesystemErrors()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"pd-{Guid.NewGuid():N}"[..11]);
        var missingRoutes = await UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
        {
            Directory = missingDirectory,
        });
        Assert.Empty(missingRoutes);

        var file = Path.Combine(Path.GetTempPath(), $"pd-{Guid.NewGuid():N}"[..11]);
        File.WriteAllText(file, "not a directory");
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(async () =>
                await UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions { Directory = file }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task DiscoveryTimesOutUnresponsiveSocketsWithoutDeletingThem()
    {
        var directory = TempSocketDirectory();
        var path = Path.Combine(directory, $"{ServerId}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Socket? peer = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var accept = listener.AcceptAsync();
            var discovery = UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
            {
                Directory = directory,
                TimeoutMs = 50,
            });
            peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await discovery.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(path));
        }
        finally
        {
            peer?.Dispose();
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveryIgnoresServerIdMismatches()
    {
        const string reportedServerId = "11111111-1111-4111-8111-111111111111";
        var directory = TempSocketDirectory();
        var path = Path.Combine(directory, $"{ServerId}.sock");
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = reportedServerId,
        });
        try
        {
            await server.StartAsync();
            var routes = await UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
            {
                Directory = directory,
            });
            Assert.Empty(routes);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverySuppressesVersionErrorsButPropagatesOtherHandshakeErrors()
    {
        Assert.Empty(await DiscoverWithHelloErrorAsync("version"));
        var error = await Assert.ThrowsAsync<Pi.Client.ServerError>(async () =>
            await DiscoverWithHelloErrorAsync("invalid_request"));
        Assert.Equal("invalid_request", error.Code);
    }

    private static async Task<IReadOnlyList<UnixServerRoute>> DiscoverWithHelloErrorAsync(string code)
    {
        var directory = TempSocketDirectory();
        var path = Path.Combine(directory, $"{ServerId}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Socket? peer = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var accept = listener.AcceptAsync();
            var discovery = UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
            {
                Directory = directory,
                TimeoutMs = 5_000,
            });
            peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));

            var decoder = new ProtocolCodec.ClientMessageDecoder();
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var helloReceived = false;
            while (!helloReceived)
            {
                var read = await peer.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, timeout.Token);
                if (read == 0) throw new IOException("Unix client closed before sending hello");
                helloReceived = decoder.Push(buffer[..read]).Any(message => message is ClientMessage.Hello);
            }

            var response = ProtocolCodec.EncodeServerMessage(
                new ServerMessage.HelloError(new ProtocolError(code, $"Synthetic {code}")));
            await peer.SendAsync(response.AsMemory(), SocketFlags.None, timeout.Token);
            return await discovery.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            peer?.Dispose();
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SubscribeService_RoundTripsTheSnapshotAndUpdatesOverTheWire()
    {
        var path = TempSocketPath();
        var host = new SubscribingHost();
        var server = new Server<Metadata>(host, new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();

            var delivered = new TaskCompletionSource<ServiceProviderUpdate>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var client = new Pi.Client.Client(ClientOptionsFor(path));
            await client.ConnectAsync();

            var subscription = await client.SubscribeServiceAsync(
                new RpcTarget.ServerTarget(ServerId), "fs", ServiceMode.Singleton,
                update =>
                {
                    delivered.TrySetResult(update);
                    return Task.CompletedTask;
                });

            // The snapshot crossed as the wire value tree and decodes on the client side.
            var state = Assert.IsType<ServiceMemberSnapshot.State>(
                Assert.Single(Assert.Single(subscription.Snapshot.Instances).Members));
            Assert.Equal("doc", state.Name);
            Assert.Equal(1L, state.Sequence);
            var replace = Assert.IsType<DeltaOp.Replace>(Assert.Single(state.Ops));
            Assert.Equal(new Dictionary<string, object?> { ["n"] = 1L }, replace.Value);

            // An update the server publishes after the response: encoded per subscription, sent
            // as its wire value tree, decoded back on the client.
            subscription.Start();
            await host.Services.Attachment.Publish!(
                host.Services.Attachment.SubscriptionId!,
                new ServiceProviderUpdate.StateUpdate(Instance, "doc", 2L,
                    [new DeltaOp.Set(new DeltaPath([Seg.Key("n")]), 5L)]),
                Context.Background);

            var update = await AwaitWithTimeoutAsync(delivered.Task);
            var stateUpdate = Assert.IsType<ServiceProviderUpdate.StateUpdate>(update);
            Assert.Equal("doc", stateUpdate.Member);
            Assert.Equal(2L, stateUpdate.Sequence);
            var set = Assert.IsType<DeltaOp.Set>(Assert.Single(stateUpdate.Ops));
            Assert.Equal(new DeltaPath([Seg.Key("n")]), set.Path);
            Assert.Equal(5L, set.Value);

            // Disposing the subscription unsubscribes over the same control channel.
            await subscription.DisposeAsync();
            var unsubscribe = Assert.IsType<ServiceControlCall.Unsubscribe>(
                ServiceWire.DecodeServiceControlCall(host.Services.Attachment.Calls[^1]));
            Assert.Equal(subscription.Id, unsubscribe.SubscriptionId);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, int milliseconds = 5_000)
    {
        var winner = await Task.WhenAny(task, Task.Delay(milliseconds));
        if (!ReferenceEquals(winner, task))
        {
            throw new TimeoutException("the awaited task never completed");
        }

        return await task;
    }

    [Fact]
    public async Task DiscoversRunningServerAndSkipsStaleSockets()
    {
        // 发现按「<serverId>.sock」约定寻址。
        var path = UnixSocketAddress.GetUnixSocketPath(ServerId, Path.GetTempPath());
        var stalePath = UnixSocketAddress.GetUnixSocketPath(
            Guid.NewGuid().ToString(), Path.GetTempPath());
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();
            // 陈旧 socket：写一个普通文件，探测应跳过而非报错。
            File.WriteAllText(stalePath, "stale");

            var directory = Path.GetTempPath();
            var routes = await UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
            {
                Directory = directory,
                TimeoutMs = 2_000,
            });
            Assert.Contains(routes, route => route.ServerId == ServerId && route.Path == path);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(stalePath)) File.Delete(stalePath);
        }
    }
}
