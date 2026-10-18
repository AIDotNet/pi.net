using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Chord.Context;
using Pi.CodingAgent.Experimental;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the run() orchestration end to end (4g-3d-3c): a real coordinator, a real harness
/// over memory storage and the real worker services, driven through the control protocol. The C#
/// mirror of TS's <c>fixtures/faux-session-worker.ts</c> plus the worker half of
/// <c>experimental-remote-runtime.test.ts</c> — the manager itself lands with 4g-3d-4, so this
/// test drives the worker directly in the server role.
/// </summary>
public class SessionWorkerEntryE2ETests
{
    private const string SessionId = "session-1";
    private const string PeerId = "peer-1";
    private const string AttachmentId = "attach-1";

    [Fact]
    public async Task Run_ServesTheSessionUntilShutdown()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-worker-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cwd = Path.Combine(root, "cwd");
        Directory.CreateDirectory(cwd);
        var sessionPath = Path.Combine(root, "session.jsonl");
        var lockPath = sessionPath + ".lock";
        var publicPath = Path.Combine(root, "public.sock");
        var controlPath = Path.Combine(root, "control.sock");

        // Nothing in the worker's path should reach the real agent directory; pin it anyway, the
        // way the theme tests do, so a future facet cannot touch the user's files.
        var previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, Path.Combine(root, "agent"));
        try
        {
            var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
            await process.RunAsync([publicPath, controlPath]);
            try
            {
                var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
                await server.ConnectAsync();

                var models = new Models();
                models.SetProvider(Faux.CreateProvider().Provider);
                var harness = new HarnessImpl(new MemoryStorage(), new HarnessOptions
                {
                    Models = models,
                    Registry = Registry.CreateRegistry(),
                    Settings = new HarnessSettings
                    {
                        Extensions = [],
                        Retry = new ConversationRetryPolicy
                        {
                            Enabled = false, MaxRetries = 0, BaseDelayMs = 0,
                        },
                    },
                }, Context.Background);
                var conversation = await harness.RootAsync(Context.Background, new AgentChange { Cwd = cwd });

                var options = new SessionWorkerOptions(
                    Path.Combine(root, "sessions"),
                    new SessionWorkerMetadata(
                        SessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cwd, sessionPath),
                    null, null, []);

                var env = new Dictionary<string, string?>
                {
                    [SessionWorkerEnv.ControlAddress] = controlPath,
                    [SessionWorkerEnv.ControlToken] = "token",
                    [SessionWorkerEnv.SessionKey] = EncodeSessionKey("session-key"),
                    [SessionWorkerEnv.PeerId] = PeerId,
                    // The lifecycle's grace windows stay far out: this test drives the close itself.
                    [SessionWorkerEnv.InitialDemandGraceMs] = "600000",
                    [SessionWorkerEnv.OrphanDemandGraceMs] = "600000",
                };

                var messages = new List<JsonObject>();
                var gate = new object();
                server.OnEvent(@event =>
                {
                    if (@event is not ConnectionMessage message) return;
                    lock (gate) messages.Add((JsonObject)message.Payload!);
                });

                var run = SessionWorkerEntry.RunAsync(
                    options,
                    (_, _) => Task.FromResult(new SessionWorkerRuntime(harness, conversation)),
                    name => env.GetValueOrDefault(name));

                // 1. The readiness handshake reports the session and its metadata.
                var ready = await WaitForEventAsync(messages, gate, "worker_ready", run);
                Assert.Equal(SessionId, ready["sessionId"]!.GetValue<string>());
                Assert.Equal(cwd, ready["metadata"]!["cwd"]!.GetValue<string>());
                Assert.Equal(sessionPath, ready["metadata"]!["path"]!.GetValue<string>());
                Assert.Empty(ready["pluginManifestPaths"]!.AsArray());

                // 2. The session lock is held for as long as the worker runs.
                Assert.True(File.Exists(lockPath), "the session lock is held while the worker runs");

                // 3. An attaching demand is acknowledged.
                await server.SendAsync(PeerId, JsonNode.Parse(
                    $$"""{"type":"session_demand","serverConnectionId":"{{server.ServerConnectionId}}","requestId":"demand-1","attachmentId":"{{AttachmentId}}","attached":true}"""));
                var applied = await WaitForEventAsync(messages, gate, "demand_applied", run);
                Assert.Equal("demand-1", applied["requestId"]!.GetValue<string>());
                Assert.Equal(AttachmentId, applied["attachmentId"]!.GetValue<string>());
                Assert.True(applied["attached"]!.GetValue<bool>());

                // 4. A service call is dispatched and its (empty) result reported back: the seeded
                // rejecting stub is replaced once the facet host is built, exactly as in TS, so the
                // reload over an empty plugin loader resolves with no result.
                await server.SendAsync(PeerId, JsonNode.Parse(
                    $$$"""{"type":"operation","requestId":"op-1","scope":{"serverConnectionId":"{{{server.ServerConnectionId}}}","attachmentId":"{{{AttachmentId}}}"},"call":{"serviceId":"pi.session-plugins","member":"reload","args":[]}}"""));
                var response = await WaitForEventAsync(messages, gate, "operation_response", run);
                var result = response["response"]!;
                Assert.Equal("operation_result", result["type"]!.GetValue<string>());
                Assert.Equal("op-1", result["requestId"]!.GetValue<string>());
                Assert.False(result.AsObject().ContainsKey("result"));

                // 5. Subscribing to the transcript service answers with the wire snapshot of its
                // state member, and a conversation change publishes a wire-encoded update for it.
                var cwd2 = Path.Combine(root, "cwd2");
                Directory.CreateDirectory(cwd2);
                await server.SendAsync(PeerId, JsonNode.Parse(
                    $$$"""{"type":"operation","requestId":"op-2","scope":{"serverConnectionId":"{{{server.ServerConnectionId}}}","attachmentId":"{{{AttachmentId}}}"},"call":{"serviceId":"$chord.service","member":"subscribe","args":["sub-1","pi.transcript","singleton"]}}"""));
                var subscribed = await WaitForResponseAsync(messages, gate, "op-2", run);
                var subscribedResult = subscribed["response"]!;
                Assert.Equal("operation_result", subscribedResult["type"]!.GetValue<string>());
                Assert.Equal("op-2", subscribedResult["requestId"]!.GetValue<string>());
                var wireSnapshot = subscribedResult["result"]!;
                Assert.Equal("pi.transcript", wireSnapshot["serviceId"]!.GetValue<string>());
                Assert.Equal("singleton", wireSnapshot["mode"]!.GetValue<string>());
                var snapshotMember = wireSnapshot["instances"]![0]!["members"]![0]!;
                Assert.Equal("state", snapshotMember["kind"]!.GetValue<string>());
                Assert.Equal("state", snapshotMember["name"]!.GetValue<string>());
                var snapshotOp = Assert.IsType<JsonArray>(Assert.Single(snapshotMember["ops"]!.AsArray()));
                Assert.Equal("r", snapshotOp[0]!.GetValue<string>());
                // The view crosses as TS's JSON does: camelCase keys, root-replaced.
                var view = Assert.IsType<JsonObject>(snapshotOp[1]);
                Assert.Contains("conversation", view);
                Assert.Contains("entries", view);
                Assert.Contains("docs", view);
                var snapshotSequence = snapshotMember["sequence"]!.GetValue<long>();

                // 6. A conversation change advances the view: the worker publishes the update as
                // its wire form for the subscription it just answered.
                await conversation.ConfigureAsync(new AgentChange { Cwd = cwd2 }, Context.Background);
                var update = await WaitForEventAsync(messages, gate, "service_update", run);
                Assert.Equal("sub-1", update["subscriptionId"]!.GetValue<string>());
                var updateScope = update["scope"]!;
                Assert.Equal(server.ServerConnectionId, updateScope["serverConnectionId"]!.GetValue<string>());
                Assert.Equal(AttachmentId, updateScope["attachmentId"]!.GetValue<string>());
                var updatePayload = update["update"]!;
                Assert.Equal("state", updatePayload["type"]!.GetValue<string>());
                Assert.Equal("state", updatePayload["member"]!.GetValue<string>());
                Assert.True(updatePayload["sequence"]!.GetValue<long>() > snapshotSequence,
                    "the update's sequence follows the snapshot's");
                Assert.NotEmpty(updatePayload["ops"]!.AsArray());

                // 7. Shutdown releases everything: the run ends and the lock file is gone.
                await server.SendAsync(PeerId, JsonNode.Parse("""{"type":"shutdown"}"""));
                var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
                Assert.Same(run, completed);
                await run;

                Assert.False(File.Exists(lockPath), "the session lock is released on shutdown");
            }
            finally
            {
                await process.ShutdownAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Config.EnvAgentDir, previousAgentDir);

            // The lock artifact is existence-based (no held handle), so this succeeds even when
            // the test failed mid-run; tolerate a stubborn file rather than mask the failure.
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string EncodeSessionKey(string value)
        => new(System.Buffers.Text.Base64Url.EncodeToChars(System.Text.Encoding.UTF8.GetBytes(value)));

    private static async Task<JsonObject> WaitForEventAsync(
        List<JsonObject> messages, object gate, string type, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            // A worker that died before the event surfaces its failure, not a bare timeout.
            if (run.IsFaulted) throw new InvalidOperationException(
                $"the worker run failed before {type}", run.Exception);
            if (run.IsCompleted) throw new InvalidOperationException(
                $"the worker run ended before {type}");

            lock (gate)
            {
                var found = messages.FirstOrDefault(
                    message => message["type"]?.GetValue<string>() == type);
                if (found is not null) return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"no {type} event arrived");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Wait for the operation response of one request, not merely the first one.</summary>
    private static async Task<JsonObject> WaitForResponseAsync(
        List<JsonObject> messages, object gate, string requestId, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (run.IsFaulted) throw new InvalidOperationException(
                $"the worker run failed before the response to {requestId}", run.Exception);
            if (run.IsCompleted) throw new InvalidOperationException(
                $"the worker run ended before the response to {requestId}");

            lock (gate)
            {
                var found = messages.FirstOrDefault(message =>
                    message["type"]?.GetValue<string>() == "operation_response"
                    && message["response"]?["requestId"]?.GetValue<string>() == requestId);
                if (found is not null) return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"no response to {requestId} arrived");
            }

            await Task.Delay(10);
        }
    }
}
