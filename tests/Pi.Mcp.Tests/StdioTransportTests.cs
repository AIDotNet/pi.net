using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// stdio 传输对照测试（TS <c>transports/stdio.ts</c>）：逐行分帧、stderr 滚动缓冲、
/// 环境变量合并、超限/不完整消息错误与关闭语义。使用 Node 作为真实子进程。
/// </summary>
public class StdioTransportTests
{
    private const string EchoServer = """
        process.stdin.setEncoding("utf8");
        let buffer = "";
        process.stdin.on("data", (chunk) => {
          buffer += chunk;
          let newline;
          while ((newline = buffer.indexOf("\n")) >= 0) {
            const line = buffer.slice(0, newline);
            buffer = buffer.slice(newline + 1);
            if (!line.trim()) continue;
            const message = JSON.parse(line);
            process.stdout.write(JSON.stringify({
              jsonrpc: "2.0",
              id: message.id,
              result: {
                echo: message.params && message.params.value,
                explicit: process.env.PI_MCP_EXPLICIT || null,
                path: process.env.PATH || null,
                user: process.env.USERNAME || process.env.USER || null
              }
            }) + "\n");
          }
        });
        process.stderr.write("boot-log\n");
        process.stdin.on("end", () => process.exit(0));
        """;

    private static string NodePath()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Skip("Node.js is not on PATH; stdio transport process tests need a child runtime.");
        return "";
    }

    private static StdioTransportOptions EchoOptions(string script = EchoServer) => new()
    {
        Command = NodePath(),
        Args = ["-e", script],
        Stderr = "pipe",
    };

    [Fact]
    public void OptionsDefaultsMatchTs()
    {
        var options = new StdioTransportOptions { Command = "server" };
        Assert.True(options.InheritEnv);
        Assert.Equal("pipe", options.Stderr);
        Assert.Equal(TransportDefaults.MaxMessageBytes, options.MaxMessageBytes);
        Assert.Equal(64 * 1024, options.MaxStderrBytes);
        Assert.Equal(2_000, options.CloseTimeoutMs);
    }

    [Fact]
    public async Task SendsLineFramedRequestsAndReadsResponses()
    {
        var transport = new StdioTransport(EchoOptions() with
        {
            Env = new Dictionary<string, string> { ["PI_MCP_EXPLICIT"] = "explicit" },
        });
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = transport.OnMessage(message =>
        {
            if (message is JsonRpcMessage.SuccessResponse success
                && success.Result is JsonObject result)
            {
                response.TrySetResult(result);
            }
        });

        try
        {
            await transport.StartAsync();
            Assert.NotNull(transport.Pid);
            await transport.SendAsync(new JsonRpcMessage.Request(
                JsonRpcId.FromNumber(7), "echo", new JsonObject { ["value"] = "hi" }));

            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("hi", result["echo"]!.GetValue<string>());
            Assert.Equal("explicit", result["explicit"]!.GetValue<string>());
            // inheritEnv 缺省 true：宿主 PATH 仍可见。
            Assert.False(string.IsNullOrEmpty(result["path"]?.GetValue<string>()));
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task InheritEnvFalseKeepsOnlyExplicitEnvironment()
    {
        // Node 在 Windows 上启动依赖 SystemRoot；它是操作系统引导变量而非“宿主环境”，
        // 显式保留不影响验证其余变量是否被 inheritEnv=false 清空。
        var environment = new Dictionary<string, string> { ["PI_MCP_EXPLICIT"] = "only" };
        if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            environment["SystemRoot"] = systemRoot;
        }
        var transport = new StdioTransport(EchoOptions() with
        {
            InheritEnv = false,
            Env = environment,
        });
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = transport.OnMessage(message =>
        {
            if (message is JsonRpcMessage.SuccessResponse { Result: JsonObject result })
            {
                response.TrySetResult(result);
            }
        });

        try
        {
            await transport.StartAsync();
            await transport.SendAsync(new JsonRpcMessage.Request(
                JsonRpcId.FromNumber(1), "env", null));

            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("only", result["explicit"]!.GetValue<string>());
            Assert.Null(result["path"]);
            Assert.Null(result["user"]);
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task BuffersStderrAndInvokesChunkCallback()
    {
        var chunks = new List<string>();
        var transport = new StdioTransport(EchoOptions() with
        {
            OnStderr = chunk => { lock (chunks) chunks.Add(chunk); },
        });

        try
        {
            await transport.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!transport.Stderr.Contains("boot-log", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < deadline, "stderr callback did not receive boot-log");
                await Task.Delay(20);
            }

            lock (chunks)
            {
                Assert.Contains(chunks, chunk => chunk.Contains("boot-log", StringComparison.Ordinal));
            }
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task ReportsIncompleteMessageWhenServerExitsMidLine()
    {
        var transport = new StdioTransport(EchoOptions("""
            process.stdout.write('{"jsonrpc":"2.0"');
            process.exit(0);
            """));
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = transport.OnError(exception => error.TrySetResult(exception));

        try
        {
            await transport.StartAsync();
            var exception = await error.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("incomplete JSON-RPC message", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task ReportsOversizedLinesWithoutKillingConnection()
    {
        var transport = new StdioTransport(EchoOptions("""
            process.stdout.write("x".repeat(128) + "\n");
            process.stdout.write(JSON.stringify({jsonrpc:"2.0",id:1,result:{ok:true}}) + "\n");
            process.stdin.on("end", () => process.exit(0));
            process.stdin.resume();
            """) with
        {
            MaxMessageBytes = 64,
            CloseTimeoutMs = 100,
        });
        var errors = new List<Exception>();
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var errorListener = transport.OnError(exception => { lock (errors) errors.Add(exception); });
        using var messageListener = transport.OnMessage(message =>
        {
            if (message is JsonRpcMessage.SuccessResponse { Result: JsonObject result })
            {
                response.TrySetResult(result);
            }
        });

        try
        {
            await transport.StartAsync();
            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result["ok"]!.GetValue<bool>());
            lock (errors)
            {
                Assert.Contains(errors, exception =>
                    exception.Message.Contains("exceeds", StringComparison.Ordinal));
            }
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task RejectsSendBeforeStartAndAfterClose()
    {
        var transport = new StdioTransport(EchoOptions());
        await Assert.ThrowsAsync<McpConnectionClosedError>(() =>
            transport.SendAsync(new JsonRpcMessage.Notification("ping", null)));

        await transport.StartAsync();
        await transport.CloseAsync();
        await Assert.ThrowsAsync<McpConnectionClosedError>(() =>
            transport.SendAsync(new JsonRpcMessage.Notification("ping", null)));
    }

    [Fact]
    public async Task ClosingStdinLetsServerExitGracefully()
    {
        var transport = new StdioTransport(EchoOptions() with { CloseTimeoutMs = 100 });
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = transport.OnClose(() => closed.TrySetResult());

        await transport.StartAsync();
        var started = DateTime.UtcNow;
        await transport.CloseAsync();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // stdin 关闭 → node 自行退出；不应触发强杀升级的长等待。
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Null(transport.Pid);
    }
}
