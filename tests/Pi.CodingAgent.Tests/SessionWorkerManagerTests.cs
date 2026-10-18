using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Context;
using Path = Pi.Chord.Delta.Path;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental;
using Pi.Server;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the session worker manager (4g-3d-4a): the server half of the session-worker
/// protocol — worker discovery and adoption, plugin-selection conflicts, demand reconciliation
/// with its bounded shutdown, operation correlation, and the service subscription fan-out.
/// Port of <c>experimental-session-worker-manager.test.ts</c>; TS drives a fake clock, the port
/// injects millisecond-scale real timeouts instead.
/// </summary>
public class SessionWorkerManagerTests
{
    private static readonly SessionCatalogMetadata Metadata = new("session-1", 1, "/tmp", "/tmp/session-1");

    private static readonly SessionWorkerManagerOptions FastTimeouts = new()
    {
        DemandMs = 60,
        ShutdownMs = 60,
    };

    // ---------------------------------------------------------------- lifecycle failures

    [Fact]
    public async Task AdoptsADiscoveredWorkerWithItsExistingSessionPluginSelection()
    {
        var coordinator = new FakeCoordinator();
        var workers = new SessionWorkerManager(coordinator, "/tmp", timeouts: FastTimeouts);

        await workers.DiscoverAsync(new HashSet<string> { "worker-1" });

        Assert.DoesNotContain(coordinator.Sent,
            sent => sent.PeerId == "worker-1" && PayloadType(sent.Payload) == "shutdown");
        Assert.Single(workers.WorkerPids);
        workers.AssertSessionPluginManifestPaths(Metadata, []);
        workers.Detach();
    }

    [Fact]
    public async Task RejectsADifferentPluginSelectionForAnActiveSessionWithoutStoppingIt()
    {
        var (_, workers, _, _) = await AttachedWorkerAsync();

        var error = Assert.Throws<SessionPluginSelectionConflictError>(() =>
            workers.AssertSessionPluginManifestPaths(Metadata, ["/tmp/plugin/chord-facets.json"]));
        Assert.Contains("active with a different plugin selection", error.Message);
        Assert.Single(workers.WorkerPids);
        workers.Detach();
    }

    [Fact]
    public async Task CompensatesATimedOutAttachmentBeforeRejectingIt()
    {
        var (coordinator, workers, handle) = await OpenAdoptedSessionAsync(ackDemands: false);

        var demands = new List<(string AttachmentId, bool Attached)>();
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "session_demand") return;
            var attachmentId = payload["attachmentId"]!.GetValue<string>();
            var attached = payload["attached"]!.GetValue<bool>();
            demands.Add((attachmentId, attached));
            // Only the compensating detach is acknowledged, so the attach times out.
            if (attached) return;
            coordinator.EmitFrom(peerId, DemandAppliedJson(payload["requestId"]!.GetValue<string>(), attachmentId, attached));
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handle.AttachClientAsync(Context.Background));
        Assert.Contains("timed out", error.Message);
        Assert.Equal(2, demands.Count);
        Assert.True(demands[0].Attached);
        Assert.Equal(demands[0].AttachmentId, demands[1].AttachmentId);
        Assert.False(demands[1].Attached);
        workers.Detach();
    }

    [Fact]
    public async Task KillsAWorkerWhenATimedOutDemandCannotBeReconciled()
    {
        var (coordinator, workers, handle) = await OpenAdoptedSessionAsync(ackDemands: false);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handle.AttachClientAsync(Context.Background));
        Assert.StartsWith("Session worker demand reconciliation failed; worker was terminated",
            error.Message);
        Assert.Equal(123, Assert.Single(coordinator.Killed));
        Assert.Empty(workers.WorkerPids);
        workers.Detach();
    }

    [Fact]
    public async Task BoundsHarnessDrivenWorkerShutdown()
    {
        var (coordinator, workers, handle, _) = await AttachedWorkerAsync();
        coordinator.OnSend = (_, _) => { };

        await handle.CloseAsync(Context.Background);

        Assert.Equal(123, Assert.Single(coordinator.Killed));
        Assert.Empty(workers.WorkerPids);
        workers.Detach();
    }

    // ---------------------------------------------------------------- operations

    [Fact]
    public async Task CorrelatesServiceResultsToTheWorkerGenerationAndAttachment()
    {
        var (coordinator, workers, _, attachment) = await AttachedWorkerAsync();
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "operation") return;
            coordinator.EmitFrom(peerId, OperationResultJson(
                payload["requestId"]!.GetValue<string>(),
                payload["scope"]!.DeepClone(),
                new JsonObject { ["accepted"] = true }));
        };

        var result = await attachment.InvokeServiceAsync(
            ServiceCall(), NoUpdates, Context.Background);

        var accepted = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(true, accepted["accepted"]);
        var operation = coordinator.Sent
            .Select(sent => sent.Payload)
            .FirstOrDefault(payload => PayloadType(payload) == "operation");
        Assert.NotNull(operation);
        Assert.Equal("server-generation-1", operation!["scope"]!["serverConnectionId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            new JsonObject
            {
                ["serviceId"] = "test.session",
                ["member"] = "run",
                ["args"] = new JsonArray { "Hello" },
            },
            operation["call"]));
        workers.Detach();
        await attachment.ReleaseAsync(Context.Background);
    }

    [Fact]
    public async Task RejectsACorrelatedResponseWithMismatchedWorkerIdentity()
    {
        var (coordinator, workers, _, attachment) = await AttachedWorkerAsync();
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "operation") return;
            var response = OperationResultJson(
                payload["requestId"]!.GetValue<string>(),
                payload["scope"]!.DeepClone(),
                new JsonObject { ["accepted"] = true });
            response["token"] = "wrong-token";
            coordinator.EmitFrom(peerId, response);
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => attachment.InvokeServiceAsync(ServiceCall(), NoUpdates, Context.Background));
        Assert.Contains("mismatched operation response", error.Message);
        workers.Detach();
        await attachment.ReleaseAsync(Context.Background);
    }

    [Fact]
    public async Task RejectsANullRequestScope()
    {
        var (coordinator, workers, _, attachment) = await AttachedWorkerAsync();
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "operation") return;
            coordinator.EmitFrom(peerId, new JsonObject
            {
                ["type"] = "operation_response",
                ["token"] = "worker-token",
                ["sessionKey"] = Metadata.Path,
                ["response"] = new JsonObject
                {
                    ["type"] = "operation_result",
                    ["requestId"] = payload["requestId"]!.GetValue<string>(),
                    ["scope"] = null,
                    ["result"] = new JsonObject { ["accepted"] = true },
                },
            });
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => attachment.InvokeServiceAsync(ServiceCall(), NoUpdates, Context.Background));
        Assert.Contains("invalid operation response", error.Message);
        workers.Detach();
        await attachment.ReleaseAsync(Context.Background);
    }

    [Fact]
    public async Task RejectsPendingServiceCallsOnReplacementWithoutStoppingTheWorker()
    {
        var (coordinator, workers, _, attachment) = await AttachedWorkerAsync();
        coordinator.OnSend = (_, _) => { };

        var calling = attachment.InvokeServiceAsync(ServiceCall(), NoUpdates, Context.Background);
        workers.Detach();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => calling);
        Assert.Contains("replaced during a worker operation", error.Message);
        Assert.DoesNotContain(coordinator.Sent,
            sent => PayloadType(sent.Payload) == "shutdown");
        Assert.Empty(workers.WorkerPids);
    }

    // ---------------------------------------------------------------- service subscriptions

    [Fact]
    public async Task RoutesASubscriptionsSnapshotAndUpdatesToTheClient()
    {
        var (coordinator, workers, _, attachment) = await AttachedWorkerAsync();
        JsonNode? scope = null;
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "operation") return;
            scope = payload["scope"]!.DeepClone();
            coordinator.EmitFrom(peerId, OperationResultJson(
                payload["requestId"]!.GetValue<string>(), scope, SubscriptionSnapshot()));
        };

        var delivered = new TaskCompletionSource<ServiceProviderUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceUpdatePublisher publish = (subscriptionId, update, _) =>
        {
            if (subscriptionId == "sub-1") delivered.TrySetResult(update);
            return Task.CompletedTask;
        };

        var snapshot = await attachment.InvokeServiceAsync(
            ServiceWire.CreateServiceSubscribeCall("sub-1", "test.session", ServiceMode.Singleton),
            publish,
            Context.Background);

        Assert.True(JsonNode.DeepEquals(SubscriptionSnapshot(), JsonSerializer.SerializeToNode(snapshot)),
            $"snapshot crossed the wire as {JsonSerializer.SerializeToNode(snapshot)?.ToJsonString()}");

        // A state update the worker publishes after the snapshot: wire ops, decoded by the
        // subscription's mirror decoder and handed to the client already decoded.
        coordinator.EmitFrom(FakeCoordinator.WorkerPeer, new JsonObject
        {
            ["type"] = "service_update",
            ["token"] = "worker-token",
            ["sessionKey"] = Metadata.Path,
            ["scope"] = scope!.DeepClone(),
            ["subscriptionId"] = "sub-1",
            ["update"] = new JsonObject
            {
                ["type"] = "state",
                ["member"] = "value",
                ["sequence"] = 2L,
                ["ops"] = new JsonArray { new JsonArray { "s", new JsonArray { "count" }, 2L } },
            },
        });

        var update = await AwaitWithTimeoutAsync(delivered.Task);
        var state = Assert.IsType<ServiceProviderUpdate.StateUpdate>(update);
        Assert.Equal("value", state.Member);
        Assert.Equal(2, state.Sequence);
        var op = Assert.IsType<DeltaOp.Set>(Assert.Single(state.Ops));
        Assert.Equal(new Path([Seg.Key("count")]), op.Path);
        Assert.Equal(2L, op.Value);
        workers.Detach();
        await attachment.ReleaseAsync(Context.Background);
    }

    // ---------------------------------------------------------------- helpers

    private static ServiceCall ServiceCall() => new("test.session", "run", ["Hello"]);

    private static Task NoUpdates(string subscriptionId, ServiceProviderUpdate update, Context context)
        => Task.CompletedTask;

    private static string PayloadType(JsonNode payload)
        => payload is JsonObject json && json["type"] is JsonValue type
            && type.GetValueKind() == JsonValueKind.String
            ? type.GetValue<string>()
            : "";

    private static JsonObject SubscriptionSnapshot() => new()
    {
        ["serviceId"] = "test.session",
        ["mode"] = "singleton",
        ["instances"] = new JsonArray
        {
            new JsonObject
            {
                ["members"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "state",
                        ["name"] = "value",
                        // Wire-faithful numbers: parsed JSON backs integral values with longs.
                        ["sequence"] = 1L,
                        ["ops"] = new JsonArray
                        {
                            new JsonArray { "r", new JsonObject { ["count"] = 1L } },
                        },
                    },
                },
            },
        },
    };

    private static JsonObject OperationResultJson(string requestId, JsonNode? scope, JsonNode result) => new()
    {
        ["type"] = "operation_response",
        ["token"] = "worker-token",
        ["sessionKey"] = Metadata.Path,
        ["response"] = new JsonObject
        {
            ["type"] = "operation_result",
            ["requestId"] = requestId,
            ["scope"] = scope,
            ["result"] = result,
        },
    };

    private static JsonObject DemandAppliedJson(string requestId, string attachmentId, bool attached) => new()
    {
        ["type"] = "demand_applied",
        ["token"] = "worker-token",
        ["sessionKey"] = Metadata.Path,
        ["requestId"] = requestId,
        ["attachmentId"] = attachmentId,
        ["attached"] = attached,
    };

    private static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, int milliseconds = 5_000)
    {
        var winner = await Task.WhenAny(task, Task.Delay(milliseconds));
        if (!ReferenceEquals(winner, task))
        {
            throw new TimeoutException("the awaited task never completed");
        }

        return await task;
    }

    /// <summary>
    /// Adopt a worker and open its session. The demand the attach sends is acknowledged
    /// immediately, as TS's helper does with a queued microtask; the timeout tests open without
    /// acks and drive them themselves.
    /// </summary>
    private static async Task<(
        FakeCoordinator Coordinator,
        SessionWorkerManager Workers,
        IRoutedSessionHandle Handle)> OpenAdoptedSessionAsync(bool ackDemands)
    {
        var coordinator = new FakeCoordinator();
        var workers = new SessionWorkerManager(
            coordinator, "/tmp", timeouts: FastTimeouts, killWorker: pid => coordinator.Killed.Add(pid));
        await workers.DiscoverAsync(new HashSet<string> { "worker-1" });
        var handle = await workers.OpenSessionAsync(Metadata, Context.Background, []);
        coordinator.OnSend = (peerId, payload) =>
        {
            if (PayloadType(payload) != "session_demand" || !ackDemands) return;
            coordinator.EmitFrom(peerId, DemandAppliedJson(
                payload["requestId"]!.GetValue<string>(),
                payload["attachmentId"]!.GetValue<string>(),
                payload["attached"]!.GetValue<bool>()));
        };

        return (coordinator, workers, handle);
    }

    private static async Task<(
        FakeCoordinator Coordinator,
        SessionWorkerManager Workers,
        IRoutedSessionHandle Handle,
        IRoutedSessionAttachment Attachment)> AttachedWorkerAsync()
    {
        var (coordinator, workers, handle) = await OpenAdoptedSessionAsync(ackDemands: true);
        var attachment = await handle.AttachClientAsync(Context.Background);
        return (coordinator, workers, handle, attachment);
    }

    /// <summary>
    /// The coordinator surface the manager uses, driven by the test: sends are recorded and handed
    /// to the <see cref="OnSend"/> hook, and discovery is answered with a ready worker.
    /// </summary>
    private sealed class FakeCoordinator : ISessionWorkerCoordinator
    {
        public const string WorkerPeer = "worker-1";

        public string ControlPath => "/tmp/control.sock";

        public string ServerConnectionId => "server-generation-1";

        public bool WasReplaced => false;

        public List<(string PeerId, JsonObject Payload)> Sent { get; } = [];

        public List<long> Killed { get; } = [];

        public Action<string, JsonObject>? OnSend { get; set; }

        private readonly HashSet<Action<CoordinatorConnectionEvent>> _listeners = [];

        public Action OnEvent(Action<CoordinatorConnectionEvent> listener)
        {
            _listeners.Add(listener);
            return () => _listeners.Remove(listener);
        }

        public Task SendAsync(string peerId, JsonNode? payload)
        {
            if (payload is JsonObject json)
            {
                Sent.Add((peerId, json));
                OnSend?.Invoke(peerId, json);
            }

            return Task.CompletedTask;
        }

        public Task BroadcastAsync(JsonNode? payload)
        {
            if (payload is JsonObject json && PayloadType(json) == "discover_workers")
            {
                Emit(new ConnectionMessage(WorkerPeer, new JsonObject
                {
                    ["type"] = "worker_ready",
                    ["token"] = "worker-token",
                    ["sessionKey"] = Metadata.Path,
                    ["sessionId"] = Metadata.Id,
                    ["pid"] = 123,
                    ["metadata"] = new JsonObject
                    {
                        ["id"] = Metadata.Id,
                        ["createdAt"] = Metadata.CreatedAt,
                        ["cwd"] = Metadata.Cwd,
                        ["path"] = Metadata.Path,
                    },
                    ["pluginManifestPaths"] = new JsonArray(),
                }));
            }

            return Task.CompletedTask;
        }

        public void Emit(CoordinatorConnectionEvent @event)
        {
            foreach (var listener in _listeners) listener(@event);
        }

        /// <summary>Deliver a routed payload from one peer, as the coordinator does.</summary>
        public void EmitFrom(string peerId, JsonObject payload)
            => Emit(new ConnectionMessage(peerId, payload));
    }
}
