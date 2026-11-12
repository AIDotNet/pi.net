using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 对照 TS <c>test/pipe.test.ts</c> 与 <c>test/lines.test.ts</c>：PipeTransport 的
/// 逐行分帧、超大消息丢弃、stderr 尾部缓冲、以及关闭/终止生命周期（1.5.1 新增 API）。
/// 用内存 FakePipe 充当「别处启动的程序」，无需真实子进程。
/// </summary>
public class PipeTransportTests
{
    private static readonly JsonRpcMessage.Request Ping1 =
        new(JsonRpcId.FromNumber(1), "ping", null);

    // ---------------------------------------------------------------- 夹具

    private sealed class FakePipe : IMcpPipe
    {
        private readonly TaskCompletionSource<int> _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly List<string> Written = [];
        public readonly List<string> Calls = [];
        public Action<byte[]>? OnStdout;
        public Action<byte[]>? OnStderr;

        public void Write(byte[] data) => Written.Add(Encoding.UTF8.GetString(data));
        public void CloseStdin() => Calls.Add("closeStdin");
        public void Close()
        {
            Calls.Add("close");
            _exited.TrySetException(new InvalidOperationException("aborted"));
        }
        public Task Exited => _exited.Task;

        public void Exit(int code = 0) => _exited.TrySetResult(code);
        public void Fail(Exception error) => _exited.TrySetException(error);
        public void Stdout(string text) => OnStdout?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Stderr(string text) => OnStderr?.Invoke(Encoding.UTF8.GetBytes(text));
    }

    private sealed class FakeProgram(
        PipeTransport transport,
        FakePipe pipe,
        List<JsonRpcMessage> messages,
        List<Exception> errors,
        Task closed)
    {
        public PipeTransport Transport { get; } = transport;
        public FakePipe Pipe { get; } = pipe;
        public List<JsonRpcMessage> Messages { get; } = messages;
        public List<Exception> Errors { get; } = errors;
        public Task Closed { get; } = closed;
    }

    private static async Task<FakeProgram> StartFake(
        int? maxMessageBytes = null,
        int? maxStderrBytes = null,
        Action<string>? onStderr = null,
        int closeTimeoutMs = 2_000)
    {
        var pipe = new FakePipe();
        var transport = new PipeTransport(new PipeTransportOptions
        {
            Open = handlers =>
            {
                pipe.OnStdout = handlers.OnStdout;
                pipe.OnStderr = handlers.OnStderr;
                return Task.FromResult<IMcpPipe>(pipe);
            },
            MaxMessageBytes = maxMessageBytes,
            MaxStderrBytes = maxStderrBytes,
            OnStderr = onStderr,
            CloseTimeoutMs = closeTimeoutMs,
        });

        var messages = new List<JsonRpcMessage>();
        var errors = new List<Exception>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnMessage(messages.Add);
        transport.OnError(errors.Add);
        transport.OnClose(() => closed.TrySetResult());
        await transport.StartAsync();
        return new FakeProgram(transport, pipe, messages, errors, closed.Task);
    }

    private static string Serialize(JsonRpcMessage message) => JsonRpc.Serialize(message).ToJsonString();

    private static void AssertRequests(IReadOnlyList<JsonRpcMessage> actual, params long[] ids)
    {
        Assert.Equal(ids.Length, actual.Count);
        for (var i = 0; i < ids.Length; i++)
        {
            var request = Assert.IsType<JsonRpcMessage.Request>(actual[i]);
            Assert.Equal("ping", request.Method);
            Assert.Equal(JsonRpcId.FromNumber(ids[i]), request.Id);
        }
    }

    // ---------------------------------------------------------------- 分帧（lines.ts）

    [Fact]
    public async Task JoinsMessagesSplitAcrossChunksAndSplitsSeveralInOneChunk()
    {
        var program = await StartFake();
        var p1 = Serialize(Ping1);
        var p2 = Serialize(new JsonRpcMessage.Request(JsonRpcId.FromNumber(2), "ping", null));
        var p3 = Serialize(new JsonRpcMessage.Request(JsonRpcId.FromNumber(3), "ping", null));
        program.Pipe.Stdout(p1[..10]);
        program.Pipe.Stdout(p1[10..] + "\n" + p2);
        program.Pipe.Stdout("\n" + p3 + "\n");

        AssertRequests(program.Messages, 1, 2, 3);
        Assert.Empty(program.Errors);
    }

    [Fact]
    public async Task StripsTrailingCarriageReturnAndSkipsBlankLines()
    {
        var program = await StartFake();
        program.Pipe.Stdout($"\n\r\n   \n{Serialize(Ping1)}\r\n\n");

        AssertRequests(program.Messages, 1);
        Assert.Empty(program.Errors);
    }

    [Fact]
    public async Task ReportsInvalidMessagesAndKeepsReading()
    {
        var program = await StartFake();
        program.Pipe.Stdout($"not json\n{{\"jsonrpc\":\"1.0\"}}\n{Serialize(Ping1)}\n");

        AssertRequests(program.Messages, 1);
        Assert.Equal(2, program.Errors.Count);
    }

    [Fact]
    public async Task DropsOversizedMessagesCompleteOrNot()
    {
        var program = await StartFake(maxMessageBytes: 48);
        var oversized = Serialize(new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(9), new string('x', 48), null));
        var p1 = Serialize(Ping1);
        var p2 = Serialize(new JsonRpcMessage.Request(JsonRpcId.FromNumber(2), "ping", null));
        program.Pipe.Stdout(oversized + "\n" + p1 + "\n");
        program.Pipe.Stdout(new string('y', 49));
        program.Pipe.Stdout("\n" + p2 + "\n");

        AssertRequests(program.Messages, 1, 2);
        Assert.Equal(2, program.Errors.Count);
        Assert.All(program.Errors, e => Assert.Contains("exceeds 48 bytes", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepsBoundedStderrTailAndPassesEveryChunk()
    {
        var chunks = new List<string>();
        var program = await StartFake(maxStderrBytes: 8, onStderr: chunks.Add);
        program.Pipe.Stderr("starting\n");
        program.Pipe.Stderr("ready\n");

        Assert.Equal(["starting\n", "ready\n"], chunks);
        // "starting\nready\n" 的最后 8 字节。
        Assert.Equal("g\nready\n", program.Transport.Stderr);
    }

    [Fact]
    public async Task LargeMessageSplitAcrossManyChunksIsAssembledOnce()
    {
        // #11761：换行只在末尾，迫使每个中间块走「未完成消息」累积路径，最终只合并一次。
        var program = await StartFake();
        var line = Serialize(new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(1), "ping",
            JsonNode.Parse("{\"blob\":\"" + new string('a', 40_000) + "\"}"))) + "\n";
        for (var i = 0; i < line.Length; i += 300)
        {
            program.Pipe.Stdout(line.Substring(i, Math.Min(300, line.Length - i)));
        }

        var request = Assert.IsType<JsonRpcMessage.Request>(Assert.Single(program.Messages));
        Assert.Equal(new string('a', 40_000), ((JsonObject)request.Params!)["blob"]!.GetValue<string>());
        Assert.Empty(program.Errors);
    }

    // ---------------------------------------------------------------- 生命周期（pipe.ts）

    [Fact]
    public async Task WritesEachMessageAsOneLine()
    {
        var program = await StartFake();
        await program.Transport.SendAsync(Ping1);
        Assert.Equal([Serialize(Ping1) + "\n"], program.Pipe.Written);
    }

    [Fact]
    public async Task ReportsIncompleteMessageWhenProgramExitsThenCloses()
    {
        var program = await StartFake();
        program.Pipe.Stdout("{\"jsonrpc\":\"2.0\"");
        program.Pipe.Exit(1);

        await program.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(program.Errors,
            e => e.Message.Contains("incomplete JSON-RPC message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsFailedExitItDidNotCauseThenCloses()
    {
        var program = await StartFake();
        program.Pipe.Fail(new InvalidOperationException("connection lost"));

        await program.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(program.Errors,
            e => e.Message.Contains("connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsSendAfterCloseAndAfterProgramEnded()
    {
        var closedByUs = await StartFake();
        closedByUs.Pipe.Exit();
        await closedByUs.Transport.CloseAsync();
        await Assert.ThrowsAsync<McpConnectionClosedError>(() => closedByUs.Transport.SendAsync(Ping1));

        var ended = await StartFake();
        ended.Pipe.Exit();
        await ended.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<McpConnectionClosedError>(() => ended.Transport.SendAsync(Ping1));
        Assert.Empty(ended.Pipe.Written);
    }

    [Fact]
    public async Task ClosesStdinAndDoesNotKillProgramThatExitsInTime()
    {
        var program = await StartFake();
        var closing = program.Transport.CloseAsync();
        Assert.Equal(["closeStdin"], program.Pipe.Calls);
        await Assert.ThrowsAsync<McpConnectionClosedError>(() => program.Transport.SendAsync(Ping1));
        Assert.Empty(program.Pipe.Written);

        program.Pipe.Exit();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["closeStdin"], program.Pipe.Calls);
        await program.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task KillsProgramOnlyAfterCloseTimeout()
    {
        var program = await StartFake(closeTimeoutMs: 60);
        var closing = program.Transport.CloseAsync();
        Assert.Equal(["closeStdin"], program.Pipe.Calls);

        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["closeStdin", "close"], program.Pipe.Calls);
        // kill 造成的 Exited 拒绝不是错误。
        Assert.Empty(program.Errors);
    }

    [Fact]
    public async Task ShutsDownOnceWhenClosedTwice()
    {
        var program = await StartFake();
        var first = program.Transport.CloseAsync();
        var second = program.Transport.CloseAsync();
        program.Pipe.Exit();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await program.Transport.CloseAsync();
        Assert.Equal(["closeStdin"], program.Pipe.Calls);
        await program.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RejectsStartWhenOpenRejects()
    {
        var transport = new PipeTransport(new PipeTransportOptions
        {
            Open = _ => Task.FromException<IMcpPipe>(new InvalidOperationException("not_supported")),
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartAsync());
        Assert.Contains("not_supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillsProgramThatFinishedStartingAfterClose()
    {
        var opened = new TaskCompletionSource<IMcpPipe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new PipeTransport(new PipeTransportOptions { Open = _ => opened.Task });
        var starting = transport.StartAsync();
        await transport.CloseAsync();

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCalled = 0;
        var pipe = new LambdaPipe(() =>
        {
            closeCalled++;
            exited.TrySetException(new InvalidOperationException("aborted"));
        }, exited.Task);
        opened.SetResult(pipe);

        await Assert.ThrowsAsync<McpConnectionClosedError>(() => starting);
        Assert.Equal(1, closeCalled);
        // Exited 的拒绝被吞掉，不能成为未观察异常。
        _ = exited.Task.Exception;
    }

    private sealed class LambdaPipe(Action onClose, Task exited) : IMcpPipe
    {
        public void Write(byte[] data) { }
        public void CloseStdin() { }
        public void Close() => onClose();
        public Task Exited => exited;
    }
}
