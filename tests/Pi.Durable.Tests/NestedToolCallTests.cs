using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Pi.Telemetry;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 嵌套 durable 调用（`tool.ts`）：工具经 <see cref="IToolExecutionApi.ExecuteToolAsync"/> 调另一个工具，
/// 该调用成为一个嵌套 <c>pi.tool</c> 任务；同一个 key 再调一次不重复执行，直接拿已存结果并发
/// <c>pi.durable.replay</c>。
/// </summary>
public class NestedToolCallTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task TheSameKeyIsAnsweredFromTheStoredResultInsteadOfRerunning()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var telemetry = new InMemoryTelemetryContext();
        setup.Telemetry = telemetry;

        var echo = new EchoTool();
        var outer = new OuterTool();
        setup.Registry.Install(new FakeExtension([outer, echo]));

        var extension = new FakeExtension([outer, echo]);
        setup.Registry.Install(extension);

        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1", outer.Name)),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));

        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            // 扩展要显式成为这个对话 agent 的一部分，工具才会被请求带上。
            await root.CommitAsync(async tx =>
            {
                await AgentDocs.ConfigureAsync((Transaction)tx, root.Id, new AgentChange
                {
                    Extensions = new AgentChange.ExtensionChange { Exact = [extension] },
                }).ConfigureAwait(false);
                return 0;
            }, Ctx);

            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, record.Status);

            // 工具被调了两次（key 相同），内层工具只执行一次。
            Assert.Equal(2, outer.Calls);
            Assert.Equal(1, echo.Runs);

            // 外层结果带上两次的文本：两次都拿到了同一个已存结果。
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var results = entries.Where(entry => entry.Kind == DurableEntries.ToolResultEntry.Kind).ToList();
            var only = Assert.Single(results);
            var message = Assert.IsType<ToolResultMessage>(only.Model!.First());
            var text = Assert.IsType<TextContent>(message.Content.First()).Text;
            Assert.Equal("hi|hi", text);

            // 第二次调用被判定为重放：phase span 上有 pi.durable.replay。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline
                && !telemetry.GetSpans().Any(span => span.Events.Any(@event => @event.Name == "pi.durable.replay")))
            {
                await Task.Delay(10);
            }

            Assert.Contains(
                telemetry.GetSpans(),
                span => span.Events.Any(@event => @event.Name == "pi.durable.replay"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static AssistantMessage Round(string callId, string toolName) => new(
        [new ToolCallContent(callId, toolName, new Dictionary<string, object?>())],
        StopReason.ToolUse);

    /// <summary>内层工具：数自己被真正执行了几次。</summary>
    private sealed class EchoTool : IToolRegistration
    {
        public int Runs { get; private set; }

        public string Name => "echo";

        public string Description => "echo";

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            Runs++;
            var text = args is IReadOnlyDictionary<string, object?> dictionary
                && dictionary.TryGetValue("text", out var value)
                    ? value as string ?? "<not-a-string>"
                    : "<no-args>";
            return Task.FromResult(new ToolExecutionResult { Content = [new TextContent(text)] });
        }
    }

    /// <summary>外层工具：用同一个 key 调两次内层工具，把它们拿到的文本拼起来。</summary>
    private sealed class OuterTool : IToolRegistration
    {
        public int Calls { get; private set; }

        public string Name => "outer";

        public string Description => "outer";

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var arguments = new JsonObject { ["text"] = "hi" };
            var first = await api.ExecuteToolAsync(
                "echo", arguments, context, new ExecuteToolOptions { Key = "c1" });
            var second = await api.ExecuteToolAsync(
                "echo", arguments, context, new ExecuteToolOptions { Key = "c1" });
            Calls = 2;

            var left = first.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "<none>";
            var right = second.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "<none>";
            return new ToolExecutionResult { Content = [new TextContent($"{left}|{right}")] };
        }
    }

    private sealed class FakeExtension(IReadOnlyList<IToolRegistration> tools) : IExtension
    {
        public string Name => "test.nested";

        public IReadOnlyList<IToolRegistration>? Tools { get; } = tools;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }

    [Fact]
    public async Task ASilentNestedCallStillSettlesItsDetails()
    {
        // progress: false 的嵌套调用不写槽位；它的 api.DetailsAsync 不能因此死等一次永不发生的提交。
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var details = new DetailsTool();
        var outer = new SilentOuterTool();
        var extension = new FakeExtension([outer, details]);
        setup.Registry.Install(extension);

        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1", outer.Name)),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));

        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.CommitAsync(async tx =>
            {
                await AgentDocs.ConfigureAsync((Transaction)tx, root.Id, new AgentChange
                {
                    Extensions = new AgentChange.ExtensionChange { Exact = [extension] },
                }).ConfigureAwait(false);
                return 0;
            }, Ctx);

            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var settled = await submission.WaitAsync(Ctx).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(settled.Record is SubmissionRecord.InputRecord { Status: SubmissionStatus.Done });
            Assert.Equal(1, details.Runs);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    /// <summary>声明 progress: false 的内层工具，并且用 api.DetailsAsync 上报明细。</summary>
    private sealed class DetailsTool : IToolRegistration
    {
        public int Runs { get; private set; }

        public string Name => "details";

        public string Description => "details";

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            Runs++;
            await api.DetailsAsync(new Dictionary<string, object?> { ["k"] = 1 }, context);
            return new ToolExecutionResult { Content = [new TextContent("details-ok")] };
        }
    }

    private sealed class SilentOuterTool : IToolRegistration
    {
        public string Name => "silent";

        public string Description => "silent";

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var result = await api.ExecuteToolAsync(
                "details", new Dictionary<string, object?>(), context,
                new ExecuteToolOptions { Key = "c1", Progress = false });
            var text = result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "<none>";
            return new ToolExecutionResult { Content = [new TextContent($"outer:{text}")] };
        }
    }
}
