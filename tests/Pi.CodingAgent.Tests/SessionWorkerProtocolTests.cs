using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the session worker protocol half (4g-3d-3a): the wire grammar's typebox-mirroring
/// strictness, the helpers, and WorkerLifecycle's demand/retirement reconciliation.
/// </summary>
public class SessionWorkerProtocolTests
{
    private const string ConnectionId = "3f6d4b2e-4c1a-4f7b-9e2d-8a5c0d1e2f3b";
    private const string SessionId = "9c8b7a65-4d3e-4f21-b0a9-1e2d3c4b5a60";

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static SessionWorkerMetadata SampleMetadata()
        => new("session-1", 1700000000000, "/work", "/sessions/session-1.jsonl");

    // ---------------------------------------------------------------- metadata

    [Fact]
    public void Metadata_ParsesAndRejectsUnknownKeys()
    {
        var parsed = SessionWorkerProtocol.TryParseMetadata(Node(
            """{"id":"s1","createdAt":1,"cwd":"/w","path":"/p"}"""));
        Assert.NotNull(parsed);
        Assert.Equal(new SessionWorkerMetadata("s1", 1, "/w", "/p"), parsed);

        // StrictObject rejects the extra key.
        Assert.Null(SessionWorkerProtocol.TryParseMetadata(Node(
            """{"id":"s1","createdAt":1,"cwd":"/w","path":"/p","extra":true}""")));
        Assert.Null(SessionWorkerProtocol.TryParseMetadata(Node("""{"id":"","createdAt":1,"cwd":"","path":""}""")));
        Assert.Null(SessionWorkerProtocol.TryParseMetadata(Node("""{"id":"s1","createdAt":"1","cwd":"","path":""}""")));
        Assert.Null(SessionWorkerProtocol.TryParseMetadata(Node("""{"id":"s1","createdAt":1,"cwd":""}""")));
    }

    [Fact]
    public void Metadata_AcceptsAnyNumberForCreatedAt()
    {
        Assert.NotNull(SessionWorkerProtocol.TryParseMetadata(Node(
            """{"id":"s1","createdAt":1.5,"cwd":"","path":""}""")));
    }

    // ---------------------------------------------------------------- options

    [Fact]
    public void Options_ParsesWithAndWithoutOptionalModel()
    {
        var bare = SessionWorkerProtocol.TryParseOptions(Node(
            $$"""{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"/w","path":"/p"},"pluginManifestPaths":[]}"""));
        Assert.NotNull(bare);
        Assert.Null(bare.Provider);
        Assert.Null(bare.Model);
        Assert.Empty(bare.PluginManifestPaths);

        var full = SessionWorkerProtocol.TryParseOptions(Node(
            $$"""{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"/w","path":"/p"},"provider":"openai","model":"gpt","pluginManifestPaths":["/a","/b"]}"""));
        Assert.NotNull(full);
        Assert.Equal("openai", full.Provider);
        Assert.Equal("gpt", full.Model);
        Assert.Equal(["/a", "/b"], full.PluginManifestPaths);
    }

    [Fact]
    public void Options_RejectsPresentButInvalidOptionals()
    {
        // Type.Optional allows absence, never null.
        Assert.Null(SessionWorkerProtocol.TryParseOptions(Node(
            """{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"provider":null,"pluginManifestPaths":[]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOptions(Node(
            """{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"model":"","pluginManifestPaths":[]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOptions(Node(
            """{"sessionDir":"","metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":[]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOptions(Node(
            """{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":[""]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOptions(Node(
            """{"sessionDir":"/s","metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":[],"extra":1}""")));
    }

    // ---------------------------------------------------------------- scope and calls

    [Fact]
    public void Scope_ParsesAndRejectsUnknownKeys()
    {
        var scope = SessionWorkerProtocol.TryParseScope(Node(
            $$"""{"serverConnectionId":"{{ConnectionId}}","attachmentId":"a-1"}"""));
        Assert.Equal(new WorkerServiceScope(ConnectionId, "a-1"), scope);
        Assert.Null(SessionWorkerProtocol.TryParseScope(Node(
            $$"""{"serverConnectionId":"{{ConnectionId}}","attachmentId":"a-1","extra":1}""")));
        Assert.Null(SessionWorkerProtocol.TryParseScope(Node(
            """{"serverConnectionId":"c","attachmentId":1}""")));
    }

    [Fact]
    public void ServiceCall_ParsesInstanceAndArgs()
    {
        var call = SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"sessions","member":"list","args":[],"instance":{"key":"k","generation":2}}"""));
        Assert.NotNull(call);
        Assert.Equal("sessions", call.ServiceId);
        Assert.Equal("list", call.Member);
        Assert.Equal(new ServiceInstanceAddress("k", 2), call.Instance);

        var withoutInstance = SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"sessions","member":"list","args":[1,"x",null]}"""));
        Assert.NotNull(withoutInstance);
        Assert.Null(withoutInstance.Instance);
        Assert.Equal(3, withoutInstance.Args.Count);
    }

    [Fact]
    public void ServiceCall_RejectsBadInstancesAndArgs()
    {
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"s","member":"m","args":[],"instance":{"key":"k","generation":0}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"s","member":"m","args":[],"instance":{"key":"k","generation":1.5}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"s","member":"m","args":[],"instance":{"key":"k","generation":1,"extra":1}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"","member":"m","args":[]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"s","member":"m","args":{}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseServiceCall(Node(
            """{"serviceId":"s","member":"m","args":[],"instance":null}""")));
    }

    // ---------------------------------------------------------------- operation protocol

    [Fact]
    public void OperationRequest_IsStrict()
    {
        var request = SessionWorkerProtocol.TryParseOperationRequest(Node(
            $$$$"""{"type":"operation","requestId":"r-1","scope":{"serverConnectionId":"{{{{ConnectionId}}}}","attachmentId":"a-1"},"call":{"serviceId":"s","member":"m","args":[]}}"""));
        Assert.NotNull(request);
        Assert.Equal("r-1", request.RequestId);
        Assert.Equal(ConnectionId, request.Scope.ServerConnectionId);

        Assert.Null(SessionWorkerProtocol.TryParseOperationRequest(Node(
            """{"type":"operation","requestId":"","scope":{"serverConnectionId":"c","attachmentId":"a"},"call":{"serviceId":"s","member":"m","args":[]}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOperationRequest(Node(
            """{"type":"operation","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"call":{"serviceId":"s","member":"m","args":[]},"extra":1}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOperationRequest(Node(
            """{"type":"other","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"call":{"serviceId":"s","member":"m","args":[]}}""")));
    }

    [Fact]
    public void OperationResponse_ParsesBothMembers()
    {
        var result = SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_result","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"}}"""));
        var ok = Assert.IsType<WorkerOperationResponse.Result>(result);
        Assert.Null(ok.Value);

        var withValue = Assert.IsType<WorkerOperationResponse.Result>(SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_result","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"result":{"any":["thing"]}}""")));
        Assert.NotNull(withValue.Value);

        var error = Assert.IsType<WorkerOperationResponse.Error>(SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_error","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"code":"service_not_found","message":"gone"}""")));
        Assert.Equal("service_not_found", error.Code);
        Assert.Equal("gone", error.Message);

        // The code is optional; the message is not.
        var withoutCode = Assert.IsType<WorkerOperationResponse.Error>(SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_error","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"message":"m"}""")));
        Assert.Null(withoutCode.Code);

        Assert.Null(SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_error","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"code":"nope","message":"m"}""")));
        Assert.Null(SessionWorkerProtocol.TryParseOperationResponse(Node(
            """{"type":"operation_error","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"code":"service_not_found"}""")));
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Command_LooseMembersIgnoreUnknownKeys()
    {
        // shutdown / discover_workers / session_demand use a plain Type.Object.
        Assert.IsType<SessionWorkerCommand.Shutdown>(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"shutdown","whatever":1}""")));
        Assert.IsType<SessionWorkerCommand.DiscoverWorkers>(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"discover_workers","whatever":1}""")));

        var demand = Assert.IsType<SessionWorkerCommand.SessionDemand>(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"session_demand","serverConnectionId":"c","requestId":"r","attachmentId":"a","attached":true}""")));
        Assert.True(demand.Attached);
        Assert.IsType<SessionWorkerCommand.SessionDemand>(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"session_demand","serverConnectionId":"c","requestId":"r","attachmentId":"a","attached":false,"extra":1}""")));
    }

    [Fact]
    public void Command_RejectsMalformedMembers()
    {
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"session_demand","serverConnectionId":"c","requestId":"r","attachmentId":"a"}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"session_demand","serverConnectionId":"c","requestId":"r","attachmentId":"a","attached":"yes"}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"operation_cancel","requestId":"","scope":{"serverConnectionId":"c","attachmentId":"a"}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node(
            """{"type":"operation_cancel","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"extra":1}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node("""{"type":"nope"}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCommand(Node("\"shutdown\"")));
    }

    // ---------------------------------------------------------------- events

    [Fact]
    public void Event_ParsesEveryMember()
    {
        var ready = Assert.IsType<SessionWorkerEvent.WorkerReady>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"worker_ready","token":"t","sessionKey":"k","sessionId":"s","pid":42,"metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":["/a"]}""")));
        Assert.Equal(42, ready.Pid);
        Assert.Equal("s1", ready.Metadata.Id);

        Assert.IsType<SessionWorkerEvent.WorkerFailed>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"worker_failed","token":"t","sessionKey":"k","message":"boom"}""")));
        Assert.IsType<SessionWorkerEvent.DemandApplied>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"demand_applied","token":"t","sessionKey":"k","requestId":"r","attachmentId":"a","attached":true}""")));
        Assert.IsType<SessionWorkerEvent.DemandRejected>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"demand_rejected","token":"t","sessionKey":"k","requestId":"r","message":"no"}""")));

        var response = Assert.IsType<SessionWorkerEvent.OperationResponse>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"operation_response","token":"t","sessionKey":"k","response":{"type":"operation_result","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"}}}""")));
        Assert.IsType<WorkerOperationResponse.Result>(response.Response);

        var update = Assert.IsType<SessionWorkerEvent.ServiceUpdate>(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"service_update","token":"t","sessionKey":"k","scope":{"serverConnectionId":"c","attachmentId":"a"},"subscriptionId":"sub-1","update":null}""")));
        Assert.Equal("sub-1", update.SubscriptionId);
        Assert.Null(update.Update);
    }

    [Fact]
    public void Event_RejectsMalformedMembers()
    {
        // pid must be an integer of at least 1.
        Assert.Null(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"worker_ready","token":"t","sessionKey":"k","sessionId":"s","pid":0,"metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":[]}""")));
        Assert.Null(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"worker_ready","token":"t","sessionKey":"k","sessionId":"s","pid":1.5,"metadata":{"id":"s1","createdAt":1,"cwd":"","path":""},"pluginManifestPaths":[]}""")));
        // subscriptionId is minLength 1.
        Assert.Null(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"service_update","token":"t","sessionKey":"k","scope":{"serverConnectionId":"c","attachmentId":"a"},"subscriptionId":"","update":1}""")));
        // The nested response must itself validate.
        Assert.Null(SessionWorkerProtocol.TryParseEvent(Node(
            """{"type":"operation_response","token":"t","sessionKey":"k","response":{"type":"operation_error","requestId":"r","scope":{"serverConnectionId":"c","attachmentId":"a"},"code":"bogus","message":"m"}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseEvent(Node("""{"type":"nope","token":"t"}""")));
    }

    // ---------------------------------------------------------------- coordinator input

    [Fact]
    public void CoordinatorInput_ParsesEveryMember()
    {
        var registered = Assert.IsType<CoordinatorInput.PeerRegistered>(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"peer_registered","peerId":"p"}""")));
        Assert.Null(registered.ServerConnectionId);

        var withServer = Assert.IsType<CoordinatorInput.PeerRegistered>(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"peer_registered","peerId":"p","serverConnectionId":"c"}""")));
        Assert.Equal("c", withServer.ServerConnectionId);

        Assert.IsType<CoordinatorInput.ServerConnected>(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"server_connected","serverConnectionId":"c"}""")));
        Assert.IsType<CoordinatorInput.ServerDisconnected>(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"server_disconnected","serverConnectionId":"c"}""")));
        Assert.IsType<CoordinatorInput.Message>(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"message","from":"server","payload":{"type":"shutdown"}}""")));
    }

    [Fact]
    public void CoordinatorInput_RejectsForeignSendersAndBadShape()
    {
        // Only the server may send the worker routed messages.
        Assert.Null(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"message","from":"peer","payload":{}}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCoordinatorInput(Node(
            """{"type":"peer_registered","peerId":"p","serverConnectionId":null}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCoordinatorInput(Node("""{"type":"nope"}""")));
        Assert.Null(SessionWorkerProtocol.TryParseCoordinatorInput(Node("\"message\"")));
    }

    // ---------------------------------------------------------------- helpers

    [Fact]
    public void DemandKey_JoinsWithNUL()
    {
        // TS joins with "\0" so ordinary ids cannot collide; ids that themselves contain NUL
        // would, exactly as in TS — the protocol ids never do.
        Assert.Equal("a\0b", SessionWorkerProtocol.DemandKey("a", "b"));
        Assert.NotEqual(
            SessionWorkerProtocol.DemandKey("a", "b"),
            SessionWorkerProtocol.DemandKey("ab", ""));
        Assert.NotEqual(
            SessionWorkerProtocol.DemandKey("a-1", "b-2"),
            SessionWorkerProtocol.DemandKey("a", "1\0b-2"));
    }

    [Fact]
    public void LifecycleDelay_UsesTheFallbackAndValidatesOverrides()
    {
        Assert.Equal(10, SessionWorkerProtocol.LifecycleDelay("X", 10, _ => null));
        Assert.Equal(25, SessionWorkerProtocol.LifecycleDelay("X", 10, _ => "25"));
        Assert.Equal(0, SessionWorkerProtocol.LifecycleDelay("X", 10, _ => "0"));
        Assert.Throws<InvalidOperationException>(
            () => SessionWorkerProtocol.LifecycleDelay("X", 10, _ => "-1"));
        Assert.Throws<InvalidOperationException>(
            () => SessionWorkerProtocol.LifecycleDelay("X", 10, _ => "1.5"));
        Assert.Throws<InvalidOperationException>(
            () => SessionWorkerProtocol.LifecycleDelay("X", 10, _ => "9007199254740993"));
    }

    // ---------------------------------------------------------------- lifecycle

    private static (WorkerLifecycle Lifecycle, List<int> Retirements) CreateLifecycle(
        string? initialServerConnectionId = "gen-1",
        int initialDemandGraceMs = 10_000,
        int orphanDemandGraceMs = 10_000)
    {
        var retirements = new List<int>();
        var lifecycle = new WorkerLifecycle(initialServerConnectionId, initialDemandGraceMs, orphanDemandGraceMs, () =>
        {
            retirements.Add(retirements.Count + 1);
        });
        return (lifecycle, retirements);
    }

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

    [Fact]
    public async Task Lifecycle_RetiresWhenNoDemandArrivesWithinTheInitialGrace()
    {
        var (lifecycle, retirements) = CreateLifecycle(initialDemandGraceMs: 20);

        await WaitUntilAsync(() => retirements.Count == 1);

        // Retirement fires exactly once.
        await Task.Delay(60);
        Assert.Single(retirements);
        lifecycle.Close();
    }

    [Fact]
    public void Lifecycle_DemandBeforeTheGracePreventsRetirement()
    {
        var (lifecycle, retirements) = CreateLifecycle(initialDemandGraceMs: 20);

        lifecycle.SetDemand("gen-1", "a-1", attached: true);
        lifecycle.SetHarnessActive(false);

        Assert.Empty(retirements);
        lifecycle.Close();
    }

    [Fact]
    public void Lifecycle_DemandEndRetires()
    {
        var (lifecycle, retirements) = CreateLifecycle();

        lifecycle.SetDemand("gen-1", "a-1", attached: true);
        Assert.Empty(retirements);

        lifecycle.SetDemand("gen-1", "a-1", attached: false);
        Assert.Single(retirements);
    }

    [Fact]
    public async Task Lifecycle_OrphanedDemandSurvivesTheGraceThenRetires()
    {
        var (lifecycle, retirements) = CreateLifecycle(orphanDemandGraceMs: 30);
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        lifecycle.ServerDisconnected("gen-1");
        await Task.Delay(10);
        Assert.Empty(retirements);

        await WaitUntilAsync(() => retirements.Count == 1);
        lifecycle.Close();
    }

    [Fact]
    public async Task Lifecycle_ReconnectCancelsTheOrphanGrace()
    {
        var (lifecycle, retirements) = CreateLifecycle(orphanDemandGraceMs: 40);
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        lifecycle.ServerDisconnected("gen-1");
        lifecycle.ServerConnected("gen-1");
        await Task.Delay(80);

        Assert.Empty(retirements);
        lifecycle.Close();
    }

    [Fact]
    public void Lifecycle_OrphanGraceOnlyCoversTheDisconnectedGeneration()
    {
        var (lifecycle, retirements) = CreateLifecycle(orphanDemandGraceMs: 10_000);
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        // A newer generation does not clear the old demand's orphan timer.
        lifecycle.ServerDisconnected("gen-1");
        lifecycle.ServerConnected("gen-2");

        // The gen-1 demand still holds the worker; a request for gen-2 is a different attachment.
        Assert.Empty(retirements);
        lifecycle.Close();
    }

    [Fact]
    public void Lifecycle_BeginRequestHoldsRetirementUntilReleased()
    {
        var (lifecycle, retirements) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        var release = lifecycle.BeginRequest("gen-1", "a-1");
        lifecycle.SetDemand("gen-1", "a-1", attached: false);
        Assert.Empty(retirements);

        release();
        Assert.Single(retirements);
    }

    [Fact]
    public void Lifecycle_BeginRequestRejectsStaleAndUnknownAttachments()
    {
        var (lifecycle, _) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        Assert.Throws<InvalidOperationException>(() => lifecycle.BeginRequest("gen-2", "a-1"));
        Assert.Throws<InvalidOperationException>(() => lifecycle.BeginRequest("gen-1", "a-2"));
    }

    [Fact]
    public void Lifecycle_BeginRequestRejectsAnOrphanedAttachment()
    {
        var (lifecycle, _) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: true);
        lifecycle.ServerDisconnected("gen-1");

        Assert.Throws<InvalidOperationException>(() => lifecycle.BeginRequest("gen-1", "a-1"));
        lifecycle.Close();
    }

    [Fact]
    public void Lifecycle_HarnessActivityHoldsRetirement()
    {
        var (lifecycle, retirements) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        lifecycle.SetHarnessActive(true);
        lifecycle.SetDemand("gen-1", "a-1", attached: false);
        Assert.Empty(retirements);

        lifecycle.SetHarnessActive(false);
        Assert.Single(retirements);
    }

    [Fact]
    public void Lifecycle_RejectsWorkAfterRetirement()
    {
        var (lifecycle, retirements) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: false);
        Assert.Single(retirements);

        Assert.Throws<InvalidOperationException>(() => lifecycle.SetDemand("gen-1", "a-1", attached: true));
        Assert.Throws<InvalidOperationException>(() => lifecycle.BeginRequest("gen-1", "a-1"));
    }

    [Fact]
    public void Lifecycle_MultipleHoldsAllReleaseBeforeRetiring()
    {
        var (lifecycle, retirements) = CreateLifecycle();
        lifecycle.SetDemand("gen-1", "a-1", attached: true);

        var first = lifecycle.HoldRetirement();
        var second = lifecycle.HoldRetirement();
        lifecycle.SetDemand("gen-1", "a-1", attached: false);
        Assert.Empty(retirements);

        first();
        Assert.Empty(retirements);
        first(); // Releasing twice is harmless.
        second();
        Assert.Single(retirements);
    }

    [Fact]
    public async Task Lifecycle_CloseCancelsTheInitialGrace()
    {
        var (lifecycle, retirements) = CreateLifecycle(initialDemandGraceMs: 20);

        lifecycle.Close();
        await Task.Delay(60);

        Assert.Empty(retirements);
    }

    [Fact]
    public void Lifecycle_StaleDemandIsRejected()
    {
        var (lifecycle, _) = CreateLifecycle(initialServerConnectionId: "gen-1");

        Assert.Throws<InvalidOperationException>(() => lifecycle.SetDemand("gen-2", "a-1", attached: true));
    }
}
