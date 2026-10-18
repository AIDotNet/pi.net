using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the session worker entry half (4g-3d-3b): the event JSON encoders, the control
/// handshake against a real coordinator, the command dispatch, the shutdown teardown, and the
/// entry argument validation.
/// </summary>
public class SessionWorkerEntryTests
{
    private const string ControlAddressEnv = SessionWorkerEnv.ControlAddress;
    private const string Token = "test-token";
    private const string SessionKey = "session-key";
    private const string PeerId = "peer-1";

    private static string EncodeSessionKey(string value)
        => new(Base64Url.EncodeToChars(Encoding.UTF8.GetBytes(value)));

    private static Dictionary<string, string?> WorkerEnv(string controlPath)
        => new()
        {
            [SessionWorkerEnv.ControlAddress] = controlPath,
            [SessionWorkerEnv.ControlToken] = Token,
            [SessionWorkerEnv.SessionKey] = EncodeSessionKey(SessionKey),
            [SessionWorkerEnv.PeerId] = PeerId,
        };

    private static SessionWorkerMetadata Metadata() => new("session-1", 1, "/work", "/sessions/session-1.jsonl");

    // ---------------------------------------------------------------- event JSON

    [Theory]
    [InlineData("worker_ready")]
    [InlineData("worker_failed")]
    [InlineData("demand_applied")]
    [InlineData("demand_rejected")]
    [InlineData("operation_response")]
    [InlineData("service_update")]
    public void EventJson_RoundTripsThroughTheParser(string kind)
    {
        var scope = new WorkerServiceScope("conn-1", "attach-1");
        var message = kind switch
        {
            "worker_ready" => (SessionWorkerEvent)new SessionWorkerEvent.WorkerReady(
                Token, SessionKey, "session-1", 4242, Metadata(), ["/a", "/b"]),
            "worker_failed" => new SessionWorkerEvent.WorkerFailed(Token, SessionKey, "boom"),
            "demand_applied" => new SessionWorkerEvent.DemandApplied(
                Token, SessionKey, "req-1", "attach-1", true),
            "demand_rejected" => new SessionWorkerEvent.DemandRejected(
                Token, SessionKey, "req-1", "no such attachment"),
            "operation_response" => new SessionWorkerEvent.OperationResponse(
                Token, SessionKey,
                new WorkerOperationResponse.Result("req-1", scope, JsonValue.Create(7))),
            _ => new SessionWorkerEvent.ServiceUpdate(
                Token, SessionKey, scope, "sub-1", JsonNode.Parse("""{"any":[1,"x",null]}""")),
        };

        var json = SessionWorkerEventJson.ToJson(message);
        json["token"] = Token;
        json["sessionKey"] = SessionKey;

        // Over the wire the JSON is serialized and re-parsed, so do exactly that here.
        var parsed = SessionWorkerProtocol.TryParseEvent(JsonNode.Parse(json.ToJsonString()));
        Assert.True(SameEvent(message, parsed),
            $"round trip lost data: {json.ToJsonString()} parsed as {parsed}");
    }

    [Fact]
    public void EventJson_OmitsTheCodeWhenTheErrorHasNone()
    {
        var scope = new WorkerServiceScope("conn-1", "attach-1");
        var json = SessionWorkerEventJson.ToJson(new SessionWorkerEvent.OperationResponse(
            Token, SessionKey, new WorkerOperationResponse.Error("req-1", scope, null, "boom")));

        Assert.False(json.AsObject().ContainsKey("code"));
        Assert.Equal("boom", json["response"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void EventJson_OmitsTheResultWhenTheOperationHasNone()
    {
        var scope = new WorkerServiceScope("conn-1", "attach-1");
        var json = SessionWorkerEventJson.ToJson(new SessionWorkerEvent.OperationResponse(
            Token, SessionKey, new WorkerOperationResponse.Result("req-1", scope, null)));

        Assert.False(json["response"]!.AsObject().ContainsKey("result"));
    }

    [Fact]
    public void MetadataJson_RoundTrips()
    {
        var json = SessionWorkerEventJson.MetadataJson(Metadata());
        Assert.Equal(Metadata(), SessionWorkerProtocol.TryParseMetadata(json));
    }

    // ---------------------------------------------------------------- control handshake

    [Fact]
    public async Task ConnectControlAsync_RegistersAsAPeerAndReportsTheServerGeneration()
    {
        var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
        var publicPath = Path.Combine(Path.GetTempPath(), $"pi-worker-{Guid.NewGuid():N}.sock");
        var controlPath = Path.Combine(Path.GetTempPath(), $"pi-worker-ctl-{Guid.NewGuid():N}.sock");
        await process.RunAsync([publicPath, controlPath]);
        try
        {
            var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await server.ConnectAsync();

            var control = await SessionWorkerControl.ConnectControlAsync(_ => WorkerEnv(controlPath)[_]);

            // A server is registered, so its generation is reported to the worker up front.
            Assert.Equal(server.ServerConnectionId, control.InitialServerConnectionId);

            // A routed message from the server reaches the worker's stream.
            await server.SendAsync(PeerId, JsonValue.Create("hello"));
            await foreach (var message in control.Messages.WithCancellation(new CancellationTokenSource(5000).Token))
            {
                var routed = Assert.IsType<CoordinatorInput.Message>(
                    SessionWorkerProtocol.TryParseCoordinatorInput(message));
                Assert.Equal("hello", routed.Payload!.GetValue<string>());
                break;
            }
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConnectControlAsync_RequiresTheLaunchEnvironment()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.ConnectControlAsync(_ => null));
        Assert.Equal("Session worker requires a control address", error.Message);

        var partial = new Dictionary<string, string?>
        {
            [SessionWorkerEnv.ControlAddress] = "/tmp/x",
            [SessionWorkerEnv.ControlToken] = "t",
        };
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.ConnectControlAsync(name => partial.TryGetValue(name, out var value) ? value : null));
        Assert.Equal("Session worker requires a control address", error.Message);

        var noPeer = new Dictionary<string, string?>(WorkerEnv("/tmp/x"))
        {
            [SessionWorkerEnv.PeerId] = null,
        };
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.ConnectControlAsync(name => noPeer.TryGetValue(name, out var value) ? value : null));
        Assert.Equal("Session worker requires a peer ID", error.Message);
    }

    // ---------------------------------------------------------------- command dispatch

    [Fact]
    public async Task ReadCommandsAsync_DispatchesEveryCommand()
    {
        var control = new FakeWorkerControl();
        var shutdowns = 0;
        var discoveries = 0;
        var demands = new List<SessionWorkerCommand.SessionDemand>();
        var operations = new List<SessionWorkerCommand.Operation>();
        var cancels = new List<SessionWorkerCommand.OperationCancel>();
        var connected = new List<string>();
        var disconnected = new List<string>();

        var dispatch = SessionWorkerControl.ReadCommandsAsync(control, new WorkerCommandHandlers
        {
            OnShutdown = () => shutdowns++,
            OnDiscovery = () => discoveries++,
            OnDemand = demand =>
            {
                demands.Add(demand);
                return Task.CompletedTask;
            },
            OnOperation = operation => operations.Add(operation),
            OnOperationCancel = cancel => cancels.Add(cancel),
            OnServerConnected = id => connected.Add(id),
            OnServerDisconnected = id => disconnected.Add(id),
        });

        await control.SendAsync(JsonNode.Parse("""{"type":"server_connected","serverConnectionId":"gen-1"}"""));
        // Commands arrive routed inside a message envelope, as TS's readCommands expects.
        await control.SendAsync(JsonNode.Parse("""{"type":"message","from":"server","payload":{"type":"shutdown"}}"""));
        await control.SendAsync(JsonNode.Parse("""{"type":"message","from":"server","payload":{"type":"discover_workers"}}"""));
        await control.SendAsync(JsonNode.Parse(
            """{"type":"message","from":"server","payload":{"type":"session_demand","serverConnectionId":"gen-1","requestId":"r","attachmentId":"a","attached":true}}"""));
        await control.SendAsync(JsonNode.Parse(
            """{"type":"message","from":"server","payload":{"type":"operation","requestId":"r","scope":{"serverConnectionId":"gen-1","attachmentId":"a"},"call":{"serviceId":"s","member":"m","args":[]}}}"""));
        await control.SendAsync(JsonNode.Parse(
            """{"type":"message","from":"server","payload":{"type":"operation_cancel","requestId":"r","scope":{"serverConnectionId":"gen-1","attachmentId":"a"}}}"""));
        await control.SendAsync(JsonNode.Parse("""{"type":"server_disconnected","serverConnectionId":"gen-1"}"""));

        await WaitUntilAsync(() => disconnected.Count == 1);

        Assert.Equal(["gen-1"], connected);
        Assert.Equal(["gen-1"], disconnected);
        Assert.Equal(1, shutdowns);
        Assert.Equal(1, discoveries);
        var demand = Assert.Single(demands);
        Assert.True(demand.Attached);
        Assert.Equal("a", demand.AttachmentId);
        var operation = Assert.Single(operations);
        Assert.Equal("s", operation.Call.ServiceId);
        var cancel = Assert.Single(cancels);
        Assert.Equal("r", cancel.RequestId);
    }

    [Fact]
    public async Task ReadCommandsAsync_DestroysTheControlOnAnInvalidMessage()
    {
        var control = new FakeWorkerControl();
        var dispatch = SessionWorkerControl.ReadCommandsAsync(control, new WorkerCommandHandlers
        {
            OnShutdown = () => { },
            OnDiscovery = () => { },
            OnDemand = _ => Task.CompletedTask,
            OnOperation = _ => { },
            OnOperationCancel = _ => { },
            OnServerConnected = _ => { },
            OnServerDisconnected = _ => { },
        });

        await control.SendAsync(JsonNode.Parse("""{"type":"nope"}"""));

        await WaitUntilAsync(() => control.Failures.Count == 1);
        Assert.Equal("Coordinator sent an invalid worker message", Assert.Single(control.Failures));
    }

    [Fact]
    public async Task ReadCommandsAsync_IgnoresNonCommandPayloads()
    {
        var control = new FakeWorkerControl();
        var operations = 0;
        var dispatch = SessionWorkerControl.ReadCommandsAsync(control, new WorkerCommandHandlers
        {
            OnShutdown = () => { },
            OnDiscovery = () => { },
            OnDemand = _ => Task.CompletedTask,
            OnOperation = _ => operations++,
            OnOperationCancel = _ => { },
            OnServerConnected = _ => { },
            OnServerDisconnected = _ => { },
        });

        // A routed message whose payload is not a command is skipped, as in TS.
        await control.SendAsync(JsonNode.Parse("""{"type":"message","from":"server","payload":{"type":"nope"}}"""));
        // A late peer_registered is ignored too.
        await control.SendAsync(JsonNode.Parse("""{"type":"peer_registered","peerId":"other"}"""));
        await Task.Delay(50);

        Assert.Equal(0, operations);
        Assert.Empty(control.Failures);
    }

    // ---------------------------------------------------------------- shutdown

    [Fact]
    public async Task CloseResourcesAsync_RethrowsTheSingleFailure()
    {
        var boom = new InvalidOperationException("boom");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.CloseResourcesAsync(null, null, () => throw boom));
        Assert.Same(boom, error);
    }

    [Fact]
    public async Task CloseResourcesAsync_AggregatesSeveralFailures()
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var error = await Assert.ThrowsAsync<AggregateException>(
            () => SessionWorkerControl.CloseResourcesAsync(
                null,
                new SessionWorkerServices
                {
                    Invoke = (_, _, _) => Task.FromResult<object?>(null),
                    RemoveSubscriptions = _ => { },
                    Dispose = () => throw first,
                },
                () => throw second));
        // .NET appends the inner messages to the aggregate's message, as elsewhere in the port.
        Assert.StartsWith("Session worker cleanup failed", error.Message, StringComparison.Ordinal);
        Assert.Contains(first, error.InnerExceptions);
        Assert.Contains(second, error.InnerExceptions);
    }

    [Fact]
    public async Task CloseResourcesAsync_SucceedsWhenEverythingReleases()
    {
        var released = 0;
        await SessionWorkerControl.CloseResourcesAsync(null, null, () =>
        {
            released++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, released);
    }

    // ---------------------------------------------------------------- entry validation

    [Fact]
    public async Task RunSessionWorkerWithHarnessAsync_RequiresOneOptionsArgument()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync([], (_, _) => throw new InvalidOperationException("unreachable")));
        Assert.Equal("Session worker requires one options argument", error.Message);

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync(
                ["{}", "{}"], (_, _) => throw new InvalidOperationException("unreachable")));
        Assert.Equal("Session worker requires one options argument", error.Message);
    }

    [Fact]
    public async Task RunSessionWorkerWithHarnessAsync_RejectsInvalidOptions()
    {
        // Absolute on every platform: Windows drive paths cannot be spelled inside a JSON
        // string literal (a lone backslash is not a valid JSON escape).
        var valid = JsonSerializer.Serialize(new
        {
            sessionDir = Path.Combine(Path.GetTempPath(), "s"),
            metadata = new
            {
                id = "s",
                createdAt = 1,
                cwd = Path.Combine(Path.GetTempPath(), "w"),
                path = Path.Combine(Path.GetTempPath(), "p"),
            },
            pluginManifestPaths = Array.Empty<string>(),
        });

        // Not JSON at all.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync(["not json"], (_, _) => null!));
        Assert.Equal("Session worker received invalid options", error.Message);

        // Schema violations.
        foreach (var bad in new[]
        {
            """{"sessionDir":"","metadata":{"id":"s","createdAt":1,"cwd":"/w","path":"/p"},"pluginManifestPaths":[]}""",
            """{"sessionDir":"/s","pluginManifestPaths":[]}""",
            """{"sessionDir":"/s","metadata":{"id":"s","createdAt":1,"cwd":"/w","path":"/p"},"pluginManifestPaths":["" ]}""",
        })
        {
            error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync([bad], (_, _) => null!));
            Assert.Equal("Session worker received invalid options", error.Message);
        }

        // Relative paths are not absolute.
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync(
                ["""{"sessionDir":"s","metadata":{"id":"s","createdAt":1,"cwd":"/w","path":"/p"},"pluginManifestPaths":[]}"""],
                (_, _) => null!));
        Assert.Equal("Session worker received invalid options", error.Message);

        // A provider without a model is rejected.
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync(
                ["""{"sessionDir":"/s","metadata":{"id":"s","createdAt":1,"cwd":"/w","path":"/p"},"provider":"openai","pluginManifestPaths":[]}"""],
                (_, _) => null!));
        Assert.Equal("Session worker received invalid options", error.Message);

        // The valid shape passes validation; TS (and the port) connect the control before the
        // harness factory runs, so a missing launch environment fails first and the factory is
        // never reached.
        var reached = false;
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionWorkerControl.RunSessionWorkerWithHarnessAsync([valid], (_, _) =>
            {
                reached = true;
                throw new InvalidOperationException("harness refused");
            }));
        Assert.False(reached);
        Assert.Equal("Session worker requires a control address", error.Message);
    }

    [Fact]
    public void DecodeSessionKey_DecodesBase64Url()
    {
        Assert.Equal(SessionKey, SessionWorkerControl.DecodeSessionKey(EncodeSessionKey(SessionKey)));
        Assert.Equal("a?b>c", SessionWorkerControl.DecodeSessionKey(EncodeSessionKey("a?b>c")));
    }

    // ---------------------------------------------------------------- wire update JSON

    [Fact]
    public void WireUpdateJson_RoundTripsThroughTheParser()
    {
        var address = new ServiceInstanceAddress("key-1", 2);
        var updates = new WireServiceProviderUpdate[]
        {
            new WireServiceProviderUpdate.StateUpdate(address, "state", 3,
                [new List<object?> { "s", new List<object?> { "a" }, 1 }]),
            new WireServiceProviderUpdate.StateUpdate(null, "state", 1, []),
            new WireServiceProviderUpdate.Unavailable(),
            new WireServiceProviderUpdate.Reset(new WireServiceSubscriptionSnapshot(
                "svc", ServiceMode.Singleton,
                [new WireServiceInstanceSnapshot(address, [new WireServiceMemberSnapshot.Method("m")])])),
            new WireServiceProviderUpdate.Replaced(new WireServiceInstanceSnapshot(
                null, [new WireServiceMemberSnapshot.State("s", 0, [new List<object?> { "r", null }])])),
            new WireServiceProviderUpdate.Spawned(new WireServiceInstanceSnapshot(address, [])),
            new WireServiceProviderUpdate.Closed(address),
        };

        foreach (var update in updates)
        {
            var json = ServiceUpdateWireJson.ToJson(update);
            var parsed = ServiceWire.ParseServiceProviderUpdate(ToClr(json));
            // Records holding nested lists compare those by reference; the re-encoded wire JSON
            // is the shape that has to survive the round trip.
            Assert.True(JsonNode.DeepEquals(json, ServiceUpdateWireJson.ToJson(parsed)),
                $"round trip lost data: {json.ToJsonString()}");
        }
    }

    [Fact]
    public void WireUpdateJson_OmitsTheInstanceWhenThereIsNone()
    {
        var json = ServiceUpdateWireJson.ToJson(
            new WireServiceProviderUpdate.StateUpdate(null, "state", 1, []));
        Assert.False(json.AsObject().ContainsKey("instance"));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Record equality compares <see cref="JsonNode"/> members by reference, so the events that
    /// carry one need a structural comparison — the same check TS's deepStrictEqual performs.
    /// </summary>
    private static bool SameEvent(SessionWorkerEvent? left, SessionWorkerEvent? right) => (left, right) switch
    {
        (null, null) => true,
        (SessionWorkerEvent.WorkerReady a, SessionWorkerEvent.WorkerReady b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && a.SessionId == b.SessionId && a.Pid == b.Pid
            && a.Metadata == b.Metadata && a.PluginManifestPaths.SequenceEqual(b.PluginManifestPaths),
        (SessionWorkerEvent.WorkerFailed a, SessionWorkerEvent.WorkerFailed b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && a.Message == b.Message,
        (SessionWorkerEvent.DemandApplied a, SessionWorkerEvent.DemandApplied b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && a.RequestId == b.RequestId
            && a.AttachmentId == b.AttachmentId && a.Attached == b.Attached,
        (SessionWorkerEvent.DemandRejected a, SessionWorkerEvent.DemandRejected b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && a.RequestId == b.RequestId && a.Message == b.Message,
        (SessionWorkerEvent.OperationResponse a, SessionWorkerEvent.OperationResponse b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && SameResponse(a.Response, b.Response),
        (SessionWorkerEvent.ServiceUpdate a, SessionWorkerEvent.ServiceUpdate b) =>
            a.Token == b.Token && a.SessionKey == b.SessionKey && a.Scope == b.Scope
            && a.SubscriptionId == b.SubscriptionId && JsonNode.DeepEquals(a.Update, b.Update),
        _ => false,
    };

    private static bool SameResponse(WorkerOperationResponse left, WorkerOperationResponse right) => (left, right) switch
    {
        (WorkerOperationResponse.Result a, WorkerOperationResponse.Result b) =>
            a.RequestId == b.RequestId && a.Scope == b.Scope && JsonNode.DeepEquals(a.Value, b.Value),
        (WorkerOperationResponse.Error a, WorkerOperationResponse.Error b) =>
            a.RequestId == b.RequestId && a.Scope == b.Scope && a.Code == b.Code && a.Message == b.Message,
        _ => false,
    };

    /// <summary>
    /// The wire parsers read plain CLR values, as the JSON-line channel produces on the TS side;
    /// <see cref="JsonSerializer"/>'s <c>object?</c> projection would hand them <see cref="JsonElement"/>s.
    /// </summary>
    private static object? ToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonObject json => json.ToDictionary(
            pair => pair.Key, pair => ToClr(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(ToClr).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            // The conditional's natural type would be double (long converts to it); the cast
            // keeps the integral as a long, which is what the wire parsers accept.
            JsonValueKind.Number => value.TryGetValue<long>(out var integer)
                ? (object)integer
                : value.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was never met");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>A control the test drives: messages in, sends and failures recorded.</summary>
    private sealed class FakeWorkerControl : IWorkerControl
    {
        private readonly Channel<JsonNode?> _messages = Channel.CreateUnbounded<JsonNode?>();

        public string? InitialServerConnectionId => "gen-1";

        public IAsyncEnumerable<JsonNode?> Messages => _messages.Reader.ReadAllAsync();

        public List<string> Failures { get; } = [];

        public List<JsonObject> Sent { get; } = [];

        public event Action<Exception?>? Closed;

        public Task SendAsync(SessionWorkerEvent message)
        {
            var payload = SessionWorkerEventJson.ToJson(message);
            Sent.Add(payload);
            return Task.CompletedTask;
        }

        public void Fail(string message)
        {
            Failures.Add(message);
            // The real control destroys the socket, which raises Closed; mirror that here.
            Closed?.Invoke(new InvalidOperationException(message));
        }

        public Task SendAsync(JsonNode? message) => _messages.Writer.WriteAsync(message).AsTask();
    }
}
