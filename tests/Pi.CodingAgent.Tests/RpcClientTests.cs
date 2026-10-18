// ============================================================================
// Tests for RpcClient (TS `modes/rpc/rpc-client.ts`), landed with 4f-5b.
// The first two cases mirror test/rpc-client-clear-queue.test.ts and
// rpc-client-clone.test.ts (which monkey-patch the private send/getData); the
// third mirrors rpc-client-process-exit.test.ts (a stub child script that
// exits mid-flight). Expected shapes come from the TS bodies.
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Modes.Rpc;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class RpcClientTests : IDisposable
{
    private readonly string _root;

    public RpcClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-rpc-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    /// <summary>替换 send/getData 的测试替身（对应 TS 测试的 monkey-patch）。</summary>
    private sealed class StubbedClient(JsonNode response) : RpcClient
    {
        public RpcCommand? Sent { get; private set; }

        internal override Task<JsonNode> SendAsync(RpcCommand command)
        {
            Sent = command;
            return Task.FromResult(response);
        }

        internal override T GetData<T>(JsonNode response)
        {
            var data = response["data"]!;
            return data.Deserialize<T>(JsonL.Options)!;
        }
    }

    [Fact]
    public async Task ClearQueue_SendsTheClearQueueCommand()
    {
        var client = new StubbedClient(JsonNode.Parse("""
        {"type":"response","command":"clear_queue","success":true,"data":{"steering":["Change direction"],"followUp":["Summarize when finished"]}}
        """)!);

        var result = await client.ClearQueueAsync();

        Assert.IsType<RpcClearQueueCommand>(client.Sent);
        Assert.Equal(new[] { "Change direction" }, result.Steering);
        Assert.Equal(new[] { "Summarize when finished" }, result.FollowUp);
    }

    [Fact]
    public async Task Clone_SendsTheCloneCommand()
    {
        var client = new StubbedClient(JsonNode.Parse("""
        {"type":"response","command":"clone","success":true,"data":{"cancelled":false}}
        """)!);

        var result = await client.CloneAsync();

        Assert.IsType<RpcCloneCommand>(client.Sent);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void GetData_FailureResponse_ThrowsTheServerError()
    {
        // TS getData: `throw new Error(errorResponse.error)`。直取真实实现（桩替身只换 send）。
        var client = new RpcClient();
        var failure = JsonNode.Parse("""
        {"type":"response","command":"get_entries","success":false,"error":"Entry not found: x"}
        """)!;

        var error = Assert.Throws<InvalidOperationException>(() => client.GetData<RpcEntriesData>(failure));

        Assert.Equal("Entry not found: x", error.Message);
    }

    [Fact]
    public async Task ProcessExit_RejectsInFlightRequest()
    {
        // 对应 TS rpc-client-process-exit.test.ts：子进程收到第一行 stdin 后以 43 退出。
        var scriptPath = Path.Combine(_root, "child.mjs");
        await File.WriteAllTextAsync(scriptPath, """
process.stdin.once("data", () => {
	process.exit(43);
});
process.stdin.resume();
""");

        var client = new RpcClient(new RpcClientOptions { FileName = "node", CliPath = scriptPath });
        try
        {
            await client.StartAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCommandsAsync());

            Assert.Matches("Agent process exited \\(code=43 signal=null\\)", error.Message);
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_KillsTheChildProcess()
    {
        var scriptPath = Path.Combine(_root, "sleeper.mjs");
        await File.WriteAllTextAsync(scriptPath, "setInterval(() => {}, 1000);\n");

        var client = new RpcClient(new RpcClientOptions { FileName = "node", CliPath = scriptPath });
        await client.StartAsync();

        await client.StopAsync();

        // 再发命令应报 "Client not started"（TS: stop 后 process 置空）。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCommandsAsync());
        Assert.Equal("Client not started", error.Message);
    }

    [Fact]
    public void GetCommandType_MatchesTheSerializedDiscriminatorForEveryCommand()
    {
        // 防漂移：超时文案里的命令名必须与线路判别式一致。
        RpcCommand[] commands =
        [
            new RpcPromptCommand { Message = "m" },
            new RpcSteerCommand { Message = "m" },
            new RpcFollowUpCommand { Message = "m" },
            new RpcAbortCommand(),
            new RpcClearQueueCommand(),
            new RpcNewSessionCommand(),
            new RpcGetStateCommand(),
            new RpcSetModelCommand { Provider = "p", ModelId = "m" },
            new RpcCycleModelCommand(),
            new RpcGetAvailableModelsCommand(),
            new RpcSetThinkingLevelCommand { Level = Pi.Ai.Types.ThinkingLevel.High },
            new RpcCycleThinkingLevelCommand(),
            new RpcGetAvailableThinkingLevelsCommand(),
            new RpcSetSteeringModeCommand { Mode = "all" },
            new RpcSetFollowUpModeCommand { Mode = "all" },
            new RpcCompactCommand(),
            new RpcSetAutoCompactionCommand { Enabled = true },
            new RpcSetAutoRetryCommand { Enabled = true },
            new RpcAbortRetryCommand(),
            new RpcBashCommand { Command = "c" },
            new RpcAbortBashCommand(),
            new RpcGetSessionStatsCommand(),
            new RpcExportHtmlCommand(),
            new RpcSwitchSessionCommand { SessionPath = "s" },
            new RpcForkCommand { EntryId = "e" },
            new RpcCloneCommand(),
            new RpcGetForkMessagesCommand(),
            new RpcGetEntriesCommand(),
            new RpcGetTreeCommand(),
            new RpcGetLastAssistantTextCommand(),
            new RpcSetSessionNameCommand { Name = "n" },
            new RpcGetMessagesCommand(),
            new RpcGetCommandsCommand(),
        ];

        Assert.Equal(33, commands.Length);
        foreach (var command in commands)
        {
            var discriminator = ((JsonObject)JsonNode.Parse(JsonL.SerializeLine(command))!)["type"]!.GetValue<string>();
            Assert.Equal(discriminator, RpcClient.GetCommandType(command));
        }
    }

    [Fact]
    public async Task HandleLine_RoutesResponsesAndEvents()
    {
        // 响应走 pending 请求；其余作为事件派发（TS handleLine）。
        var events = new List<JsonNode>();
        var client = new RpcClient();
        var unsubscribe = client.OnEvent(events.Add);

        var responseLine = """{"type":"response","id":"req_9","command":"get_state","success":true,"data":{}}""";
        var eventLine = """{"type":"agent_settled"}""";
        var garbageLine = "not json";

        // 经反射调用私有 HandleLine（读取泵在生产里由 stdout 驱动）。
        var handleLine = typeof(RpcClient).GetMethod(
            "HandleLine",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        handleLine.Invoke(client, [responseLine]);
        handleLine.Invoke(client, [eventLine]);
        handleLine.Invoke(client, [garbageLine]);

        // 无 pending 请求时响应也按事件派发（TS: pendingRequests.has(id) 为假时落到监听方）。
        Assert.Equal(2, events.Count);
        Assert.Equal("response", events[0]["type"]!.GetValue<string>());
        Assert.Equal("agent_settled", events[1]["type"]!.GetValue<string>());
        Assert.NotNull(client.GetStderr());
        unsubscribe();
    }
}
