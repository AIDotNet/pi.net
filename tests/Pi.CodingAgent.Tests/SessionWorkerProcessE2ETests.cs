using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Utils;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class SessionWorkerProcessE2ETests
{
    [Fact]
    public async Task InternalProcessEntry_UsesSqliteHarnessAcrossWorkerRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-worker-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cwd = Path.Combine(root, "cwd");
        var agentDir = Path.Combine(root, "agent");
        var sessionDir = Path.Combine(root, "session");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(sessionDir);

        var sessionId = "session-1";
        var sessionKey = Path.Combine(root, "session-key");
        var metadata = new SessionWorkerMetadata(
            sessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cwd, sessionDir);
        var databasePath = SessionCatalog.SessionStoragePath(
            new SessionCatalogMetadata(metadata.Id, metadata.CreatedAt, metadata.Cwd, metadata.Path));
        var lockPath = sessionDir + ".lock";
        var modelCatalog = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = Path.Combine(agentDir, "auth.json"),
            ModelsPathDisabled = true,
            RefreshOnCreate = false,
        });
        var models = modelCatalog.GetModels();
        Assert.True(models.Count >= 2, "the offline builtin catalog must provide restart test models");
        var firstModel = models[0];
        var secondModel = models.First(model =>
            model.Provider != firstModel.Provider || model.Id != firstModel.Id);

        var cliExecutable = FindCliExecutable();
        var coordinator = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
        var publicPath = Path.Combine(root, "public.sock");
        var controlPath = Path.Combine(root, "control.sock");
        await coordinator.RunAsync([publicPath, controlPath]);

        ChildProcessHandle? worker = null;
        try
        {
            var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await server.ConnectAsync();
            var messages = new List<JsonObject>();
            var gate = new object();
            server.OnEvent(@event =>
            {
                if (@event is not ConnectionMessage message) return;
                lock (gate) messages.Add((JsonObject)message.Payload!);
            });

            worker = StartWorker(
                cliExecutable, controlPath, metadata, agentDir, sessionKey,
                "worker-first", firstModel.Provider, firstModel.Id);
            var ready = await WaitForEventAsync(messages, gate, worker, "worker_ready");
            Assert.Equal(sessionId, ready["sessionId"]!.GetValue<string>());
            Assert.Equal(cwd, ready["metadata"]!["cwd"]!.GetValue<string>());
            Assert.True(File.Exists(lockPath), "the child holds the session lock while serving");

            await server.SendAsync("worker-first", new JsonObject
            {
                ["type"] = "session_demand",
                ["serverConnectionId"] = server.ServerConnectionId,
                ["requestId"] = "demand-1",
                ["attachmentId"] = "attachment-1",
                ["attached"] = true,
            });
            var demand = await WaitForEventAsync(
                messages, gate, worker, "demand_applied", message =>
                    message["requestId"]?.GetValue<string>() == "demand-1");
            Assert.True(demand["attached"]!.GetValue<bool>());

            await server.SendAsync("worker-first", new JsonObject
            {
                ["type"] = "operation",
                ["requestId"] = "operation-1",
                ["scope"] = new JsonObject
                {
                    ["serverConnectionId"] = server.ServerConnectionId,
                    ["attachmentId"] = "attachment-1",
                },
                ["call"] = new JsonObject
                {
                    ["serviceId"] = "pi.session-plugins",
                    ["member"] = "reload",
                    ["args"] = new JsonArray(),
                },
            });
            var operation = await WaitForEventAsync(
                messages, gate, worker, "operation_response", message =>
                    message["response"]?["requestId"]?.GetValue<string>() == "operation-1");
            Assert.Equal("operation_result", operation["response"]!["type"]!.GetValue<string>());

            await StopWorkerAsync(server, worker, "worker-first");
            Assert.False(File.Exists(lockPath), "shutdown releases the session lock");
            worker.Dispose();
            worker = null;

            lock (gate) messages.Clear();
            worker = StartWorker(
                cliExecutable, controlPath, metadata, agentDir, sessionKey,
                "worker-second", secondModel.Provider, secondModel.Id);
            var reopened = await WaitForEventAsync(messages, gate, worker, "worker_ready");
            Assert.Equal(sessionId, reopened["sessionId"]!.GetValue<string>());

            await StopWorkerAsync(server, worker, "worker-second");
            Assert.False(File.Exists(lockPath), "the restarted worker releases the session lock");
            worker.Dispose();
            worker = null;

            var storage = await SqliteStorage.OpenAsync(databasePath);
            var harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = new Models(),
                Registry = Registry.CreateRegistry(),
            }, Context.Todo);
            try
            {
                var conversation = await harness.ConversationAsync(
                    DurableIdConstants.RootConversation, Context.Todo);
                Assert.NotNull(conversation);
                var agent = await conversation!.AgentAsync(Context.Todo);
                Assert.Equal(new ModelRef(firstModel.Provider, firstModel.Id), agent.Model);
                Assert.Equal(cwd, agent.Cwd);
            }
            finally
            {
                await harness.CloseAsync(Context.Todo);
            }
        }
        finally
        {
            if (worker is not null)
            {
                await InternalProcess.TerminateInternalProcessAsync(worker);
                worker.Dispose();
            }

            await coordinator.ShutdownAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ChildProcessHandle StartWorker(
        string cliExecutable,
        string controlPath,
        SessionWorkerMetadata metadata,
        string agentDir,
        string sessionKey,
        string peerId,
        string provider,
        string model)
    {
        var options = JsonSerializer.Serialize(new
        {
            sessionDir = Path.GetDirectoryName(metadata.Path)!,
            metadata = new
            {
                id = metadata.Id,
                createdAt = metadata.CreatedAt,
                cwd = metadata.Cwd,
                path = metadata.Path,
            },
            provider,
            model,
            pluginManifestPaths = Array.Empty<string>(),
        });
        var env = new Dictionary<string, string?>
        {
            [Config.EnvAgentDir] = agentDir,
            [SessionWorkerEnv.ControlAddress] = controlPath,
            [SessionWorkerEnv.ControlToken] = Guid.NewGuid().ToString("N"),
            [SessionWorkerEnv.SessionKey] = EncodeSessionKey(sessionKey),
            [SessionWorkerEnv.PeerId] = peerId,
            [SessionWorkerEnv.InitialDemandGraceMs] = "600000",
            [SessionWorkerEnv.OrphanDemandGraceMs] = "600000",
        };
        var child = InternalProcess.SpawnInternalProcess(
            InternalProcessRole.SessionWorker,
            [options],
            new InternalProcess.SpawnInternalProcessOptions
            {
                Env = env,
                ExecutablePath = cliExecutable,
            });
        if (child.StartupError is not null)
        {
            throw new InvalidOperationException("Unable to start the CLI worker process", child.StartupError);
        }

        return child;
    }

    private static string FindCliExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root from the test output path");
        }

        var configuration = Directory.GetParent(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.Name;
        var framework = Path.GetFileName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var executableName = OperatingSystem.IsWindows() ? "pi.exe" : "pi";
        var executablePath = Path.Combine(
            directory.FullName, "src", "Pi.Cli", "bin", configuration, framework, executableName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The CLI apphost required for the worker process test was not built", executablePath);
        }

        return executablePath;
    }

    private static string EncodeSessionKey(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task StopWorkerAsync(
        CoordinatorConnection server, ChildProcessHandle worker, string peerId)
    {
        await server.SendAsync(peerId, new JsonObject { ["type"] = "shutdown" });
        var exit = worker.WaitAsync();
        var completed = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(exit, completed);
        Assert.Equal(0, await exit);
    }

    private static async Task<JsonObject> WaitForEventAsync(
        List<JsonObject> messages,
        object gate,
        ChildProcessHandle worker,
        string type,
        Func<JsonObject, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (worker.StartupError is not null)
            {
                throw new InvalidOperationException($"the worker failed before {type}", worker.StartupError);
            }
            if (worker.HasExited)
            {
                throw new InvalidOperationException(
                    $"the worker exited with code {worker.ExitCode?.ToString() ?? "unknown"} before {type}");
            }

            lock (gate)
            {
                var found = messages.FirstOrDefault(message =>
                    message["type"]?.GetValue<string>() == type && (predicate is null || predicate(message)));
                if (found is not null) return found;
            }

            if (DateTime.UtcNow > deadline) throw new TimeoutException($"no {type} event arrived");
            await Task.Delay(10);
        }
    }
}
