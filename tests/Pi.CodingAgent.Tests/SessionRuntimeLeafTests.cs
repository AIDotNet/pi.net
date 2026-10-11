// ============================================================================
// 4e-3a unit tests: cache-stats, nested-tool-calls, settings-diagnostics,
// bash-executor, and output-guard. The first three files have TypeScript
// counterparts under packages/coding-agent/test/; bash-executor and output-guard
// have no dedicated TS tests, so the C# cases pin the documented contracts.
// ============================================================================

using System.Text;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SessionRuntimeLeafTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-4e3a-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public SessionRuntimeLeafTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static readonly CacheStats.IModelPriceSource Models = new StaticPriceSource(0.3);

    private sealed class StaticPriceSource(double cacheRead) : CacheStats.IModelPriceSource
    {
        public CacheStats.ModelPriceQuote? GetModel(string provider, string modelId)
            => new() { Cost = new() { CacheRead = cacheRead } };
    }

    private static AssistantMessage Assistant(
        long input = 0,
        long cacheRead = 0,
        long cacheWrite = 0,
        UsageCost? cost = null,
        string model = "test-model",
        long timestamp = 0)
        => new(
            Content: [],
            StopReason: StopReason.Stop,
            UsageStats: new Usage(input, 10, cacheRead, cacheWrite)
            {
                TotalTokens = 0,
                Cost = cost ?? UsageCost.Zero,
            },
            Model: model,
            Api: "anthropic-messages",
            Provider: "test",
            Timestamp: timestamp);

    private static SessionMessageEntry MessageEntry(AssistantMessage message)
        => new()
        {
            Id = "x",
            ParentId = null,
            Timestamp = "",
            Message = message,
        };

    private static UsageEntry UsageKind(string kind, long timestampMs)
        => new()
        {
            Id = $"usage-{kind}",
            ParentId = null,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Kind = kind,
            Provider = "test",
            Model = "test-model",
            Usage = new Usage(0, 0, 100_000)
            {
                TotalTokens = 100_000,
                Cost = UsageCost.Zero,
            },
        };

    // Turn 1: fresh 100k cache write at $3.75/M
    private static AssistantMessage Turn1()
        => Assistant(cacheWrite: 100_000, cost: new UsageCost(0, 0, 0, 0.375, 0), timestamp: 0);

    // Turn 2: healthy, everything read back at $0.30/M
    private static AssistantMessage Turn2()
        => Assistant(
            cacheRead: 100_000,
            cacheWrite: 5_000,
            cost: new UsageCost(0, 0, 0.03, 0.019, 0),
            timestamp: 60_000);

    // ---- cache-stats -------------------------------------------------------

    [Fact]
    public void CacheStats_AccumulatesMissedTokensAndCost()
    {
        var turn3 = Assistant(cacheWrite: 110_000, cost: new UsageCost(0, 0, 0, 0.4125, 0), timestamp: 120_000);
        var totals = CacheStats.ComputeCacheWaste([MessageEntry(Turn1()), MessageEntry(Turn2()), MessageEntry(turn3)], Models);
        Assert.Equal(105_000, totals.MissedTokens);
        Assert.Equal(0.36225, totals.MissedCost, 5);
    }

    [Fact]
    public void CacheStats_CountsNothingForHealthySessions()
    {
        var totals = CacheStats.ComputeCacheWaste([MessageEntry(Turn1()), MessageEntry(Turn2())], Models);
        Assert.Equal(0, totals.MissedTokens);
        Assert.Equal(0, totals.MissedCost);
    }

    [Fact]
    public void CacheStats_SkipsTheTurnAfterCompactionReset()
    {
        var afterReset = Assistant(cacheWrite: 20_000, cost: new UsageCost(0, 0, 0, 0.075, 0));
        var totals = CacheStats.ComputeCacheWaste(
            [
                MessageEntry(Turn1()),
                new CompactionEntry
                {
                    Id = "c",
                    ParentId = null,
                    Timestamp = "",
                    Summary = "",
                    TokensBefore = 0,
                },
                MessageEntry(afterReset),
            ],
            Models);
        Assert.Equal(0, totals.MissedTokens);
    }

    [Fact]
    public void CacheStats_CountsMissesCausedByModelSwitches()
    {
        var otherModel = Assistant(cacheWrite: 100_000, cost: new UsageCost(0, 0, 0, 0.375, 0), model: "other-model");
        var totals = CacheStats.ComputeCacheWaste([MessageEntry(Turn1()), MessageEntry(otherModel)], Models);
        Assert.Equal(100_000, totals.MissedTokens);
        Assert.Equal(1, totals.MissCount);
    }

    [Fact]
    public void CacheStats_SkipsProvidersThatReportNoCacheActivity()
    {
        var totals = CacheStats.ComputeCacheWaste(
            [MessageEntry(Assistant(input: 100_000)), MessageEntry(Assistant(input: 110_000))],
            Models);
        Assert.Equal(0, totals.MissedTokens);
    }

    [Fact]
    public void CacheStats_CollectsMissesKeyedByMessageIdentity()
    {
        var missTurn = Assistant(cacheWrite: 110_000, cost: new UsageCost(0, 0, 0, 0.4125, 0), timestamp: 120_000);
        var misses = CacheStats.CollectCacheMisses(
            [MessageEntry(Turn1()), MessageEntry(Turn2()), MessageEntry(missTurn)],
            Models);
        Assert.Single(misses);
        Assert.Equal(105_000, misses[missTurn].MissedTokens);
    }

    [Fact]
    public void CacheStats_DetectsMissWithIdleTime()
    {
        var missMessage = Assistant(cacheWrite: 110_000, cost: new UsageCost(0, 0, 0, 0.4125, 0), timestamp: 600_000);
        var miss = CacheStats.DetectCacheMiss([MessageEntry(Turn1()), MessageEntry(Turn2())], missMessage, Models);
        Assert.NotNull(miss);
        Assert.Equal(105_000, miss.MissedTokens);
        Assert.Equal(0.36225, miss.MissedCost, 5);
        Assert.Equal(540_000, miss.IdleMs);
        Assert.False(miss.ModelChanged);
    }

    [Fact]
    public void CacheStats_FlagsModelSwitchesOnDetectedMisses()
    {
        var otherModel = Assistant(
            cacheWrite: 110_000,
            cost: new UsageCost(0, 0, 0, 0.4125, 0),
            model: "other-model",
            timestamp: 120_000);
        var miss = CacheStats.DetectCacheMiss([MessageEntry(Turn1()), MessageEntry(Turn2())], otherModel, Models);
        Assert.NotNull(miss);
        Assert.Equal(105_000, miss.MissedTokens);
        Assert.True(miss.ModelChanged);
    }

    [Fact]
    public void CacheStats_UsesOnlyCacheWarmUsageEntriesAsRefreshes()
    {
        var missMessage = Assistant(cacheWrite: 110_000, cost: new UsageCost(0, 0, 0, 0.4125, 0), timestamp: 600_000);
        var afterCacheWarm = CacheStats.DetectCacheMiss(
            [MessageEntry(Turn1()), UsageKind("cache_warm", 500_000)],
            missMessage,
            Models);
        var afterOtherUsage = CacheStats.DetectCacheMiss(
            [MessageEntry(Turn1()), UsageKind("custom_operation", 500_000)],
            missMessage,
            Models);
        Assert.Equal(100_000, afterCacheWarm!.IdleMs);
        Assert.Equal(600_000, afterOtherUsage!.IdleMs);
    }

    [Fact]
    public void CacheStats_ReturnsNullForHealthyTurns()
    {
        var healthy = Assistant(
            cacheRead: 105_000,
            cacheWrite: 2_000,
            cost: new UsageCost(0, 0, 0.0315, 0.0075, 0),
            timestamp: 120_000);
        Assert.Null(CacheStats.DetectCacheMiss([MessageEntry(Turn1()), MessageEntry(Turn2())], healthy, Models));
    }

    [Fact]
    public void CacheStats_ReturnsNullForTheFirstTurn()
        => Assert.Null(CacheStats.DetectCacheMiss([], Turn1(), Models));

    // ---- nested-tool-calls -------------------------------------------------

    private static AgentToolResult ToolText(string text, object? details = null)
        => new([new TextContent(text)], Details: details ?? new JsonObject());

    private static AgentTool MakeTool(
        string name,
        Func<string, object?, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> execute,
        ToolExecutionMode? executionMode = null)
        => new(
            name,
            $"{name} tool",
            new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            name,
            execute,
            executionMode);

    private sealed class RecordingHost : INestedToolCallHost
    {
        public List<AgentTool> Tools { get; } = [];

        public bool Sequential { get; set; }

        public List<NestedToolExecutionEvent> Events { get; } = [];

        public Func<ToolCallContent, string, CancellationToken, Func<AgentToolResult, Task>, Task<AgentToolCallOutcome>>? Run { get; set; }

        public IReadOnlyList<AgentTool> GetTools() => Tools;

        public bool IsSequential() => Sequential;

        public Task<AgentToolCallOutcome> RunToolCallAsync(
            ToolCallContent toolCall,
            string parentToolCallId,
            CancellationToken cancellationToken,
            Func<AgentToolResult, Task> onUpdate)
        {
            if (Run is not null)
            {
                return Run(toolCall, parentToolCallId, cancellationToken, onUpdate);
            }

            var tool = Tools.FirstOrDefault(candidate => candidate.Name == toolCall.Name);
            if (tool is null)
            {
                var missing = new AgentToolResult([new TextContent($"unknown tool: {toolCall.Name}")], IsError: true);
                return Task.FromResult(new AgentToolCallOutcome(toolCall, missing, true));
            }

            return InvokeAsync(tool, toolCall, cancellationToken, onUpdate);
        }

        private static async Task<AgentToolCallOutcome> InvokeAsync(
            AgentTool tool,
            ToolCallContent toolCall,
            CancellationToken cancellationToken,
            Func<AgentToolResult, Task> onUpdate)
        {
            var result = await tool.Execute(toolCall.Id, toolCall.Arguments, cancellationToken, async partial =>
            {
                await onUpdate(partial);
            });
            return new AgentToolCallOutcome(toolCall, result, result.IsError);
        }

        public Task EmitAsync(NestedToolExecutionEvent @event)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NestedCalls_RecordsNestedCallsAndEmitsEvents()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("echo", async (_, _, _, onUpdate) =>
        {
            if (onUpdate is not null)
            {
                await onUpdate(ToolText("partial"));
            }

            return ToolText("ok");
        }));
        var runner = new NestedToolCallRunner(host);
        var outcome = await runner.ExecuteAsync("parent-1", "echo", new JsonObject { ["x"] = 1 });
        Assert.False(outcome.IsError);
        Assert.Equal("ok", ((TextContent)outcome.Result.Content[0]).Text);

        var record = runner.TakeRecord("parent-1");
        Assert.NotNull(record);
        Assert.NotNull(record.Calls);
        Assert.True(record.Calls.Complete);
        Assert.Single(record.Calls.Calls);
        var call = record.Calls.Calls[0];
        Assert.Equal("parent-1/1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Equal("ok", call.Status);
        Assert.True(call.DurationMs >= 0);
        Assert.Equal(1, call.Arguments!["x"]!.GetValue<int>());
        Assert.Null(call.Error);

        Assert.Equal(3, host.Events.Count);
        Assert.IsType<NestedToolExecutionEvent.Start>(host.Events[0]);
        Assert.IsType<NestedToolExecutionEvent.Update>(host.Events[1]);
        Assert.IsType<NestedToolExecutionEvent.End>(host.Events[2]);
        var start = (NestedToolExecutionEvent.Start)host.Events[0];
        Assert.Equal("parent-1/1", start.ToolCallId);
        Assert.Equal("echo", start.ToolName);
        Assert.Equal("parent-1", start.ParentToolCallId);
        Assert.Equal(1, ((JsonObject)start.Args!)["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task NestedCalls_NestsIdsAndSharesTheRecorder()
    {
        NestedToolCallRunner? runner = null;
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("inner", (_, _, _, _) => Task.FromResult(ToolText("inner"))));
        host.Tools.Add(MakeTool("outer", async (id, _, _, _) =>
        {
            await runner!.ExecuteAsync(id, "inner", new JsonObject());
            return ToolText("outer");
        }));
        runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync("parent-1", "outer", new JsonObject());
        var record = runner.TakeRecord("parent-1");
        Assert.Equal(["parent-1/1", "parent-1/1/1"], record!.Calls!.Calls.Select(call => call.Id).ToArray());
        Assert.Equal(["outer", "inner"], record.Calls.Calls.Select(call => call.Name).ToArray());
        Assert.True(record.Calls.Complete);
        Assert.Null(runner.TakeRecord("parent-1"));
    }

    [Fact]
    public async Task NestedCalls_RecordsErrorsWithoutThrowing()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("boom", (_, _, _, _) => Task.FromResult(
            new AgentToolResult([new TextContent("nope")], Details: new JsonObject { ["reason"] = "bad" }, IsError: true))));
        var runner = new NestedToolCallRunner(host);
        var outcome = await runner.ExecuteAsync("parent-1", "boom", new JsonObject());
        Assert.True(outcome.IsError);
        var call = runner.TakeRecord("parent-1")!.Calls!.Calls[0];
        Assert.Equal("error", call.Status);
        Assert.Equal("nope", call.Error);
    }

    [Fact]
    public async Task NestedCalls_TruncatesLongErrorText()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("boom", (_, _, _, _) => Task.FromResult(
            new AgentToolResult([new TextContent(new string('x', NestedCallLimits.MaxErrorChars + 50))], IsError: true))));
        var runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync("parent-1", "boom", new JsonObject());
        Assert.Equal(NestedCallLimits.MaxErrorChars, runner.TakeRecord("parent-1")!.Calls!.Calls[0].Error!.Length);
    }

    [Fact]
    public async Task NestedCalls_DropsArgumentsThatExceedThePerCallLimit()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("echo", (_, _, _, _) => Task.FromResult(ToolText("ok"))));
        var runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync(
            "parent-1",
            "echo",
            new JsonObject { ["text"] = new string('x', NestedCallLimits.MaxArgumentBytesPerCall) });
        var record = runner.TakeRecord("parent-1")!;
        Assert.False(record.Calls!.Complete);
        Assert.Null(record.Calls.Calls[0].Arguments);
        Assert.True(record.Calls.Calls[0].ArgumentsBytes > NestedCallLimits.MaxArgumentBytesPerCall);
    }

    [Fact]
    public async Task NestedCalls_StopsRecordingArgumentsAfterTheTotalLimit()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("echo", (_, _, _, _) => Task.FromResult(ToolText("ok"))));
        var runner = new NestedToolCallRunner(host);
        var args = new JsonObject { ["text"] = new string('x', 7000) };
        for (var i = 0; i < 5; i++)
        {
            await runner.ExecuteAsync("parent-1", "echo", args);
        }

        var record = runner.TakeRecord("parent-1")!;
        Assert.False(record.Calls!.Complete);
        Assert.Equal(4, record.Calls.Calls.Count(call => call.Arguments is not null));
        Assert.Single(record.Calls.Calls, call => call.Arguments is null && call.ArgumentsBytes is not null);
    }

    [Fact]
    public async Task NestedCalls_StopsRecordingCallsAfterTheCountLimit()
    {
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("echo", (_, _, _, _) => Task.FromResult(ToolText("ok"))));
        var runner = new NestedToolCallRunner(host);
        for (var i = 0; i < NestedCallLimits.MaxCalls + 3; i++)
        {
            await runner.ExecuteAsync("parent-1", "echo", new JsonObject());
        }

        var record = runner.TakeRecord("parent-1")!;
        Assert.False(record.Calls!.Complete);
        Assert.Equal(NestedCallLimits.MaxCalls, record.Calls.Calls.Count);
    }

    [Fact]
    public async Task NestedCalls_KeepsUnfinishedCallsIncomplete()
    {
        var host = new RecordingHost();
        host.Run = async (toolCall, _, _, _) =>
        {
            await Task.Delay(60);
            return new AgentToolCallOutcome(toolCall, ToolText("ok"), false);
        };
        var runner = new NestedToolCallRunner(host);
        var pending = runner.ExecuteAsync("parent-1", "slow", new JsonObject());
        await Task.Delay(5);
        var snapshot = runner.TakeRecord("parent-1")!;
        Assert.False(snapshot.Calls!.Complete);
        Assert.Equal("unfinished", snapshot.Calls.Calls[0].Status);
        Assert.Null(snapshot.Calls.Calls[0].DurationMs);
        await pending;
    }

    [Fact]
    public async Task NestedCalls_SnapshotIsACopy()
    {
        var recorder = new NestedCallRecorder();
        var record = recorder.Start(new ToolCallContent("c1", "echo", new JsonObject { ["a"] = 1 }))!;
        recorder.Finish(record, false, "");
        var snapshot = recorder.Snapshot()!;
        record.Status = "error";
        record.Arguments = new JsonObject { ["a"] = 2 };
        Assert.Equal("ok", snapshot.Calls[0].Status);
        Assert.Equal(1, snapshot.Calls[0].Arguments!["a"]!.GetValue<int>());
    }

    [Fact]
    public void NestedCalls_EmptyCompleteRecorderSnapshotsToNull()
        => Assert.Null(new NestedCallRecorder().Snapshot());

    [Fact]
    public async Task NestedCalls_ForwardsPartialUpdates()
    {
        AgentToolResult? seen = null;
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("echo", async (_, _, _, onUpdate) =>
        {
            if (onUpdate is not null)
            {
                await onUpdate(ToolText("partial"));
            }

            return ToolText("ok");
        }));
        var runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync("parent-1", "echo", new JsonObject(), new NestedToolCallOptions
        {
            OnUpdate = result =>
            {
                seen = result;
                return Task.CompletedTask;
            },
        });
        Assert.Equal("partial", ((TextContent)seen!.Content[0]).Text);
        Assert.Contains(host.Events, e => e is NestedToolExecutionEvent.Update);
    }

    [Fact]
    public async Task NestedCalls_QueuesExclusiveCalls()
    {
        var started = new List<string>();
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("slow", async (_, _, _, _) =>
        {
            started.Add("slow");
            await Task.Delay(40);
            return ToolText("slow");
        }, ToolExecutionMode.Sequential));
        host.Tools.Add(MakeTool("fast", (_, _, _, _) =>
        {
            started.Add("fast");
            return Task.FromResult(ToolText("fast"));
        }));
        var runner = new NestedToolCallRunner(host);
        var first = runner.ExecuteAsync("parent-1", "slow", new JsonObject());
        await Task.Delay(5);
        var second = runner.ExecuteAsync("parent-2", "fast", new JsonObject());
        await Task.WhenAll(first, second);
        Assert.Equal(["slow", "fast"], started);
    }

    [Fact]
    public async Task NestedCalls_DoesNotMakeNestedCallsWaitForSequentialParent()
    {
        NestedToolCallRunner? runner = null;
        var started = new List<string>();
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("inner", (_, _, _, _) =>
        {
            started.Add("inner");
            return Task.FromResult(ToolText("inner"));
        }));
        host.Tools.Add(MakeTool("outer", async (id, _, _, _) =>
        {
            started.Add("outer");
            await runner!.ExecuteAsync(id, "inner", new JsonObject());
            return ToolText("outer");
        }, ToolExecutionMode.Sequential));
        runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync("parent-1", "outer", new JsonObject());
        Assert.Equal(["outer", "inner"], started);
    }

    [Fact]
    public async Task NestedCalls_SumsNestedUsage()
    {
        NestedToolCallRunner? runner = null;
        var usage = new Usage(3, 5, 1, 2)
        {
            TotalTokens = 11,
            Cost = new UsageCost(0.03, 0.05, 0.001, 0.002, 0.083),
        };
        var host = new RecordingHost();
        host.Tools.Add(MakeTool("inner", (_, _, _, _) => Task.FromResult(
            new AgentToolResult([new TextContent("inner")], UsageStats: usage))));
        host.Tools.Add(MakeTool("outer", async (id, _, _, _) =>
        {
            await runner!.ExecuteAsync(id, "inner", new JsonObject());
            await runner.ExecuteAsync(id, "inner", new JsonObject());
            return ToolText("outer");
        }));
        runner = new NestedToolCallRunner(host);
        await runner.ExecuteAsync("parent-1", "outer", new JsonObject());
        var record = runner.TakeRecord("parent-1")!;
        Assert.Equal(6, record.Usage!.Input);
        Assert.Equal(10, record.Usage.Output);
        Assert.Equal(2, record.Usage.CacheRead);
        Assert.Equal(4, record.Usage.CacheWrite);
        Assert.Equal(0.166, record.Usage.Cost.Total, 5);
        Assert.Null(runner.TakeRecord("parent-1"));
    }

    // ---- settings-diagnostics ----------------------------------------------

    [Fact]
    public void SettingsDiagnostics_MapsFileBackedErrors()
    {
        var tempDir = Path.Combine(_root, "settings-file");
        var agentDir = Path.Combine(tempDir, "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "settings.json"), "{");
        var manager = SettingsManager.Create(tempDir, agentDir);
        var diagnostics = SettingsDiagnostics.CollectSettingsDiagnostics(manager);
        Assert.Single(diagnostics);
        Assert.Equal("warning", diagnostics[0].Type);
        Assert.Contains("Invalid settings file", diagnostics[0].Message);
        Assert.Contains("settings.json", diagnostics[0].Message);
        Assert.Empty(SettingsDiagnostics.CollectSettingsDiagnostics(manager));
    }

    [Fact]
    public void SettingsDiagnostics_DeduplicatesByTypeAndMessage()
    {
        var diagnostics = new AgentSessionRuntimeDiagnostic[]
        {
            new() { Type = "warning", Message = "Invalid settings file /tmp/settings.json: Unexpected end of JSON" },
            new() { Type = "error", Message = "other" },
            new() { Type = "warning", Message = "Invalid settings file /tmp/settings.json: Unexpected end of JSON" },
        };
        var unique = SettingsDiagnostics.DeduplicateDiagnostics(diagnostics);
        Assert.Equal(2, unique.Count);
        Assert.Equal(["warning", "error"], unique.Select(item => item.Type).ToArray());
    }

    // ---- bash-executor -----------------------------------------------------

    [Fact]
    public async Task BashExecutor_StreamsAndReturnsOutput()
    {
        var chunks = new List<string>();
        var result = await BashExecutor.ExecuteBashWithOperationsAsync(
            "echo hi",
            _root,
            new ScriptedBashOperations("hi\n", 0),
            new BashExecutorOptions { OnChunk = chunks.Add });
        Assert.Equal("hi\n", result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.False(result.Truncated);
        Assert.Null(result.FullOutputPath);
        Assert.Equal(["hi\n"], chunks);
    }

    [Fact]
    public async Task BashExecutor_TreatsCancellationAsNullExit()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await BashExecutor.ExecuteBashWithOperationsAsync(
            "sleep",
            _root,
            new CancellingBashOperations(),
            new BashExecutorOptions { CancellationToken = cts.Token });
        Assert.True(result.Cancelled);
        Assert.Null(result.ExitCode);
    }

    // ---- output-guard ------------------------------------------------------

    [Fact]
    public async Task OutputGuard_RedirectsStdoutAndKeepsRawChannel()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);
        try
        {
            Assert.False(OutputGuard.IsStdoutTakenOver);
            OutputGuard.TakeOverStdout();
            Assert.True(OutputGuard.IsStdoutTakenOver);
            Console.Write("redirected");
            Assert.Equal("redirected", capturedErr.ToString());
            Assert.Equal("", capturedOut.ToString());
            OutputGuard.WriteRawStdout("raw");
            await OutputGuard.FlushRawStdoutAsync();
            Assert.Equal("raw", capturedOut.ToString());
            OutputGuard.RestoreStdout();
            Assert.False(OutputGuard.IsStdoutTakenOver);
        }
        finally
        {
            OutputGuard.RestoreStdout();
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private sealed class ScriptedBashOperations(string text, int? exitCode) : IBashToolOperations
    {
        public Task<ShellExecResult> ExecAsync(
            string command,
            string cwd,
            ShellExecOptions options,
            CancellationToken cancellationToken)
        {
            options.OnData(Encoding.UTF8.GetBytes(text));
            return Task.FromResult(new ShellExecResult(exitCode));
        }
    }

    private sealed class CancellingBashOperations : IBashToolOperations
    {
        public Task<ShellExecResult> ExecAsync(
            string command,
            string cwd,
            ShellExecOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ShellExecResult(0));
        }
    }
}
