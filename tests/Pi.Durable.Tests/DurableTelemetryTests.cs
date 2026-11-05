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
}
