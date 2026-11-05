using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Pi.Telemetry;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// durable 遥测（<c>packages/durable/src/harness/telemetry.ts</c>）中本批已接的部分：上报计数。
/// phase span 与另两个指标要等调用模型具备 attempt/resumed，见 <see cref="DurableTelemetry"/> 的说明。
/// </summary>
public class DurableTelemetryTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task CountsReportsByTheirFixedKind()
    {
        var telemetry = new InMemoryTelemetryContext();
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Telemetry = telemetry;
        var (harness, _) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            harness.Report(new InvalidOperationException("boom"), new ReportInfo.ListenerError());

            var measurement = Assert.Single(
                telemetry.GetMeasurements(), item => item.Name == "pi.durable.reports");
            Assert.Equal(1d, measurement.Value);
            Assert.Equal("listener_error", measurement.Attributes["type"]);
            // 上报本身照旧到达宿主。
            Assert.Single(setup.Reports);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ReportsCarryTheTaskIdWhenThereIsOne()
    {
        var telemetry = new InMemoryTelemetryContext();
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Telemetry = telemetry;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var task = new ReportTask
            {
                Id = TaskId<object?>.From(42),
                Kind = "test.telemetry",
                ConversationId = root.Id,
            };
            harness.Report(new InvalidOperationException("boom"), new ReportInfo.TaskFaulted(task, "threw"));

            var measurement = Assert.Single(
                telemetry.GetMeasurements(), item => item.Name == "pi.durable.reports");
            Assert.Equal("task_faulted", measurement.Attributes["type"]);
            Assert.Equal(42d, measurement.Attributes["task.id"]);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task EmitsAPhaseSpanForEachInvocation()
    {
        var telemetry = new InMemoryTelemetryContext();
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Telemetry = telemetry;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            provider.SetResponses(HarnessTestSupport.AssistantReply("ok"));
            var run = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await run.WaitAsync(Ctx);

            // span 由 StartSpanAsync 的续体结算，等它们落定。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline
                && telemetry.GetSpans().Any(span => span.Name == "pi.durable.phase" && !span.Settled))
            {
                await Task.Delay(10);
            }

            var spans = telemetry.GetSpans().Where(span => span.Name == "pi.durable.phase").ToList();
            Assert.NotEmpty(spans);
            Assert.All(spans, span =>
            {
                Assert.Equal("run", span.Attributes["kind"]);
                Assert.True(span.Attributes.ContainsKey("task.id"));
                Assert.True(span.Attributes.ContainsKey("task.name"));
                Assert.True(span.Attributes.ContainsKey("phase"));
                // 本进程新建的任务不是「恢复」的。
                Assert.Equal(false, span.Attributes["resumed"]);
                // 阶段结束写了 outcome（新任务走到下一个阶段或已完结）。
                Assert.True(span.Attributes.ContainsKey("outcome"));
                // 阶段内提交的条目会作为事件挂在这个 span 上。
                // prepare 阶段不提交条目，所以事件不保证每个 span 都有。
            });

            // 提交条目与提交落定的那一刻，事件落在当时上下文里的 span 上。
            Assert.Contains(spans, span => span.Events.Any(@event => @event.Name == "pi.durable.entry"));
            Assert.Contains(spans, span => span.Events.Any(@event => @event.Name == "pi.durable.submission"));
            Assert.Contains(spans, span => span.Events.Any(@event => @event.Name == "pi.durable.checkpoint"));

            var measurements = telemetry.GetMeasurements();
            var durations = measurements.Where(item => item.Name == "pi.durable.phase.duration").ToList();
            Assert.NotEmpty(durations);
            Assert.All(durations, item =>
            {
                Assert.True(item.Attributes.ContainsKey("task"));
                Assert.True(item.Attributes.ContainsKey("phase"));
                Assert.True(item.Attributes.ContainsKey("outcome"));
            });

            // 活跃计数：新建 +1、终态 -1，脚本文本跑完后该任务不再活跃。
            var active = measurements.Where(item => item.Name == "pi.durable.tasks.active").ToList();
            Assert.Contains(active, item => item.Value > 0);
            Assert.Equal(0d, active.Sum(item => item.Value));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task TelemetryDoesNotChangeTaskOutcomes()
    {
        // 调度器热路径只多了「登记 span + 换上下文」：开与不开必须产出同一份记录、同一串提交。
        var without = await RunOnceAsync(null);
        var with = await RunOnceAsync(new InMemoryTelemetryContext());
        Assert.Equal(without, with);
    }

    private static async Task<string> RunOnceAsync(ITelemetryContext? telemetry)
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Telemetry = telemetry;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            provider.SetResponses(HarnessTestSupport.AssistantReply("ok"));
            var run = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await run.WaitAsync(Ctx);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            return System.Text.Json.JsonSerializer.Serialize(entries, JsonOptions);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public async Task EmitsASubmitSpanAroundTheAdmission()
    {
        var telemetry = new InMemoryTelemetryContext();
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Telemetry = telemetry;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            provider.SetResponses(HarnessTestSupport.AssistantReply("ok"));
            var run = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await run.WaitAsync(Ctx);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline
                && telemetry.GetSpans().Any(span => span.Name == "pi.durable.submit" && !span.Settled))
            {
                await Task.Delay(10);
            }

            // 一次运行里可能有多笔提交（例如后续排队项），断言第一笔的属性即可。
            var submit = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit");

            Assert.Equal("input", submit.Attributes["submission.type"]);
            Assert.True(submit.Attributes.ContainsKey("conversation.id"));
            Assert.True(submit.Attributes.ContainsKey("submission.id"));
            Assert.Contains(
                submit.Attributes["result"], new object?[] { "queued", "placed", "done" });
            Assert.True(submit.Settled);

            // 同一次运行也落下了 phase span（submit 是这条 trace 的根）。
            Assert.Contains(telemetry.GetSpans(), span => span.Name == "pi.durable.phase");
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
