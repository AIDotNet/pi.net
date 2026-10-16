using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.Codemode;
using Pi.Codemode.Runtime;
using Pi.CodingAgent.Extensions.Codemode;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>独占运行：本 collection 的用例会改 <see cref="CodemodeExecute.EngineDefaults"/>。</summary>
[CollectionDefinition(nameof(CodemodeOutputLayoutCollection), DisableParallelization = true)]
public sealed class CodemodeOutputLayoutCollection;

/// <summary>
/// codemode 输出的版式（上游 <c>eb326d265</c>，S4）。
/// <para>
/// 供应商把相邻文本块用换行或什么都不加地拼在一起，模型分不清一个 <c>text()</c> / <c>console.log()</c>
/// 输出到哪里结束。上游的修法是：文本项多于一个时，每项以 <c>==&gt; text N/M &lt;==</c> 起头；
/// <c>console.*</c> 的行聚成一个 <c>&lt;console_output&gt;</c> 块，排在其它输出之后。
/// </para>
/// <para>
/// 这些断言按上游 <c>agent-session-codemode.test.ts</c> 的期望值手算，而不是从 C# 实现回抄。
/// </para>
/// <para>
/// 用例通过 <see cref="CodemodeExecute.EngineDefaults"/> 这个**进程级静态**注入假引擎，故本类
/// 必须独占运行——同 4f-3c 对 EditPreview 的处理，放进 <c>DisableParallelization</c> collection，
/// 免得与其它 collection 并发时互相看到对方的引擎。
/// </para>
/// </summary>
[Collection(nameof(CodemodeOutputLayoutCollection))]
public class CodemodeOutputLayoutTests
{
    private static async Task<AgentToolResult> RunAsync(params WorkerToHostMessage[] messages)
    {
        var previous = CodemodeExecute.EngineDefaults.Override;
        CodemodeExecute.EngineDefaults.Override = () => new ScriptedEngine(messages);
        try
        {
            return await CodemodeExecute.ExecuteCodemodeAsync(
                "call-1",
                new Dictionary<string, object?> { ["code"] = "return 1" },
                CancellationToken.None,
                null,
                null);
        }
        finally
        {
            CodemodeExecute.EngineDefaults.Override = previous;
        }
    }

    private static string OutputText(AgentToolResult result)
        => string.Concat(result.Content.OfType<TextContent>().Skip(1).Select(block => block.Text));

    private static WorkerToHostMessage Text(string text) =>
        new WorkerToHostMessage.Output(new CodemodeOutputItem.Text(text));

    private static WorkerToHostMessage Console(string text) =>
        new WorkerToHostMessage.Output(new CodemodeOutputItem.Text(text) { Console = true });

    [Fact]
    public async Task NumbersEachTextItemAndGroupsConsoleLinesIntoOneTrailingBlock()
    {
        // 脚本：text("one\ntwo"); console.log("a"); console.log("b"); text("three\n"); return 4;
        // 三项非 console 文本（含 return 的那项）→ M = 3；console 两行聚成一块排最后。
        var result = await RunAsync(
            Text("one\ntwo"),
            Console("a"),
            Console("b"),
            Text("three\n"),
            new WorkerToHostMessage.Done(true, "4", "[]", null));

        // 相邻文本块被合并成一个 → 头 + 一块正文 = 2。
        Assert.Equal(2, result.Content.Count);
        Assert.Equal(
            "==> text 1/3 <==\none\ntwo\n==> text 2/3 <==\nthree\n==> text 3/3 <==\n4\n"
                + "<console_output>\na\nb\n</console_output>",
            OutputText(result));
    }

    [Fact]
    public async Task SingleTextItemGetsNoHeader()
    {
        var result = await RunAsync(
            Text("only"),
            new WorkerToHostMessage.Done(true, null, "[]", null));

        Assert.Equal("only", OutputText(result));
    }

    [Fact]
    public async Task ConsoleLinesStillFormABlockWhenTheyAreTheOnlyOutput()
    {
        var result = await RunAsync(
            Console("a"),
            Console("b"),
            new WorkerToHostMessage.Done(true, null, "[]", null));

        // 非 console 文本项为 0 → 没有编号；console 块仍是唯一输出。
        Assert.Equal("<console_output>\na\nb\n</console_output>", OutputText(result));
    }

    [Fact]
    public async Task ScriptErrorFollowsTheConsoleBlock()
    {
        // 脚本：text("partial"); console.log("log"); throw new Error("boom");
        var result = await RunAsync(
            Text("partial"),
            Console("log"),
            new WorkerToHostMessage.Done(false, null, null, new CodemodeScriptError("boom")
            {
                Name = "Error",
                Stack = "Error: boom\n    at codemode.js:4:1",
            }));

        Assert.True(result.IsError);
        Assert.StartsWith(
            "partial\n<console_output>\nlog\n</console_output>\nScript error:\nError: boom\n    at codemode.js:4:1",
            OutputText(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyTextItemStillCountsTowardTheTotal()
    {
        var result = await RunAsync(
            Text("a"),
            Text(""),
            new WorkerToHostMessage.Done(true, null, "[]", null));

        // total = 2，故两块都带编号；空块的那一行只有一个编号头。
        Assert.Equal("==> text 1/2 <==\na\n==> text 2/2 <==\n", OutputText(result));
    }

    /// <summary>按脚本回放的假引擎：不执行 JavaScript，只按 protocol 与 host 对话。</summary>
    private sealed class ScriptedEngine : ICodemodeJsEngine
    {
        private readonly IReadOnlyList<WorkerToHostMessage> messages;

        internal ScriptedEngine(IReadOnlyList<WorkerToHostMessage> messages) => this.messages = messages;

        public ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
        {
            // 对应 worker 线程：脚本在后台运行，进度经消息送达 host。
            _ = Task.Run(async () =>
            {
                foreach (var message in this.messages)
                {
                    await onMessage(message).ConfigureAwait(false);
                }
            });
            return new SilentVm();
        }
    }

    private sealed class SilentVm : ICodemodeVm
    {
        public void Post(HostToWorkerMessage message)
        {
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
