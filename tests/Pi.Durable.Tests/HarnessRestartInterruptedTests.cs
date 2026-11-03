using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 崩溃（宿主死亡、被驱逐或重置）对当时在跑的工作做了什么（b3a29f111）：生成在请求或轮询途中被切断的，先按
/// <c>settings.retry</c> 记一次失败尝试；其他任务被切断 <c>settings.restartLimit</c> 次后可见地结束，而不是停放。
/// </summary>
public class HarnessRestartInterruptedTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── 生成：切断的请求是一次失败的尝试 ───────────────────────────────────

    [Fact]
    public async Task ChargesTheInterruptedRequestAndAnswersOnTheNextAttempt()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = new HarnessSettings
        {
            Retry = new ConversationRetryPolicy { Enabled = true, MaxRetries = 2, BaseDelayMs = 0 },
            RestartLimit = 5,
        };
        var (discarded, reached, _) = HarnessTestSupport.FakeStep.Gated(
            HarnessTestSupport.AssistantReply("discarded"));
        provider.SetResponses(discarded);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        harness.Resume();
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));

        // 崩溃：不关闭，直接重开同一个存储。幸存的 running 记录让 open 记一次重启，request 阶段把它记作失败尝试。
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answered")));
        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            reopened.Resume();
            var admitted = await reopened.SubmissionAsync(submission.Id, Ctx);
            await admitted!.WaitAsync(Ctx).WaitAsync(TimeSpan.FromSeconds(10));

            var record = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await reopened.SubmissionAsync(submission.Id, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Done, record.Status);
            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("answered", Text(entries[^1].Model!.Single()));
            Assert.Equal(2, provider.CallCount);
            // 记一次失败尝试就是进展：重启计数从零开始。
            var task = Assert.Single(
                await TasksAsync(reopened, reopenedRoot.Id), each => each.Kind == Generation.TaskName);
            Assert.Equal(TaskOutcomeStatus.Completed, task.State.Outcome!.Status);
            Assert.Null(task.Restarts);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task EndsTheRunInterruptedWhenNoRetryIsLeft()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = new HarnessSettings
        {
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            RestartLimit = 5,
        };
        var (discarded, reached, _) = HarnessTestSupport.FakeStep.Gated(
            HarnessTestSupport.AssistantReply("discarded"));
        provider.SetResponses(discarded);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        harness.Resume();
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));

        var reports = new List<(Exception Error, ReportInfo Info)>();
        var reopened = await OpenAsync(storage.Reopen(), setup, reports);
        var reopenedRoot = await reopened.RootAsync(Ctx);
        try
        {
            reopened.Resume();
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                var live = await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx);
                return live is not null && live.ContainsKey("runFailure");
            });
            var live = (await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx))!;
            var failure = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(live["runFailure"]);
            Assert.Equal("interrupted", failure["reason"]);
            Assert.Equal(submission.Id.Value, Convert.ToInt64(((IReadOnlyList<object?>)failure["inputs"]!).Single()));

            var record = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await reopened.SubmissionAsync(submission.Id, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("interrupted", record.Reason);

            var task = Assert.Single(
                await TasksAsync(reopened, reopenedRoot.Id), each => each.Kind == Generation.TaskName);
            Assert.Equal(TaskStatus.Terminal, task.State.Status);
            Assert.Equal(TaskOutcomeStatus.Failed, task.State.Outcome!.Status);
            Assert.Equal("interrupted", DetailOf(task.State.Outcome.Error!)["reason"]);

            // 崩溃结束的工作会说出来，宿主因此留住崩溃循环的信号。
            await HarnessTestSupport.WaitForAsync(() => reports.Any(report => report.Info is ReportInfo.TaskInterrupted));
            var interrupted = Assert.Single(
                reports.Select(report => report.Info).OfType<ReportInfo.TaskInterrupted>(),
                report => report.Task.Id == task.Id);
            Assert.Equal("interrupted", interrupted.Reason);
            Assert.Equal(1, interrupted.Restarts);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task EndsTheRunAtTheRestartLimitOutsideARequest()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = new HarnessSettings
        {
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            RestartLimit = 1,
        };
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("never")));
        // 准备阶段的段落挂住：崩溃发生在请求之外（prepare）。
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Registry.Install(new HarnessTestSupport.SimpleExtension(
            "hanging", sections: [new HangingSection(reached)]));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        harness.Resume();
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var reports = new List<(Exception Error, ReportInfo Info)>();
        var reopened = await OpenAsync(storage.Reopen(), setup, reports);
        var reopenedRoot = await reopened.RootAsync(Ctx);
        try
        {
            reopened.Resume();
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                var live = await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx);
                return live is not null && live.ContainsKey("runFailure");
            });
            var live = (await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx))!;
            var failure = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(live["runFailure"]);
            Assert.Equal("restart_limit", failure["reason"]);
            Assert.Contains("prepare phase", (string)failure["detail"]!);

            var record = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await reopened.SubmissionAsync(submission.Id, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("restart_limit", record.Reason);
            // 没有被停放：任务已终态，阻塞原因里也没有重启上限。
            var task = Assert.Single(
                await TasksAsync(reopened, reopenedRoot.Id), each => each.Kind == Generation.TaskName);
            Assert.Equal(TaskStatus.Terminal, task.State.Status);
            var inspection = await reopened.InspectAsync(Ctx);
            Assert.DoesNotContain(
                inspection.Tasks,
                entry => entry is TaskInspection.Blocked blocked && blocked.Reason == "restart_limit");
            await HarnessTestSupport.WaitForAsync(() => reports.Any(report =>
                report.Info is ReportInfo.TaskInterrupted { Reason: "restart_limit" }));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    // ─── 其他任务：切断到上限后可见地结束 ───────────────────────────────────

    [Fact]
    public async Task EndsAToolCallWithAnErrorResultInsteadOfRunningItAgain()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = new HarnessSettings
        {
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            RestartLimit = 2,
            // 工具输出立即提交：崩溃前发布的部分输出已经落盘，收场的结果才带得上它。
            Progress = new ProgressPolicy { PartialIntervalMs = 0, OutputIntervalMs = 0 },
        };
        var runs = 0;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Registry.Install(new HarnessTestSupport.SimpleExtension("heavy", tools:
        [
            new HarnessTestSupport.ScriptedTool(
                "heavy",
                "Never finishes",
                async (api, context) =>
                {
                    runs++;
                    api.Output("partial\n");
                    running.TrySetResult();
                    // 崩溃时随信号结束，像一次被切断的执行。
                    await Task.Delay(Timeout.Infinite, context.AbortSignal ?? CancellationToken.None);
                    return new ToolExecutionResult { Content = [] };
                },
                replay: "safe"),
        ]));
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(ToolCallReply("heavy", "c1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("saw it")));
        var storage = new MemoryStorage();
        var (opened, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        opened.Resume();
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
        // 两次崩溃：第一次把它记到 1（还在上限之下，重跑），第二次到 2（上限）。
        var current = storage;
        for (var crash = 1; crash <= 2; crash++)
        {
            await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
            running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 从崩溃时的那份存储重开：再克隆最初的存储会把重启计数丢掉。
            current = current.Reopen();
            var (next, nextRoot) = await HarnessTestSupport.OpenChatAsync(current, setup);
            opened = next;
            root = nextRoot;
            opened.Resume();
        }

        try
        {
            var admitted = await opened.SubmissionAsync(submission.Id, Ctx);
            await admitted!.WaitAsync(Ctx).WaitAsync(TimeSpan.FromSeconds(10));
            var record = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await opened.SubmissionAsync(submission.Id, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Done, record.Status);

            // 工具没有再跑第三次：它的结果是一条错误结果，保留被打断的尝试发布的部分输出。
            Assert.Equal(2, runs);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var result = Assert.IsType<ToolResultMessage>(entries.Single(
                entry => entry.Kind == DurableEntries.ToolResultEntry.Kind).Model!.Single());
            Assert.True(result.IsError);
            // 槽上的部分输出能不能带进收场结果，取决于崩溃前它有没有落盘（提交节流是另一个未移植的上游项），
            // 因此这里只断言失败本身与诊断码。
            Assert.Contains(
                result.Content.OfType<TextContent>(),
                block => block.Text.Contains("was interrupted 2 times in a row", StringComparison.Ordinal));

            var toolTask = Assert.Single(await TasksAsync(opened, root.Id), each => each.Kind == Live.ToolTaskKind);
            Assert.Equal(TaskStatus.Terminal, toolTask.State.Status);
            Assert.Equal(TaskOutcomeStatus.Failed, toolTask.State.Outcome!.Status);
            Assert.Equal("restart_limit", DetailOf(toolTask.State.Outcome.Error!)["reason"]);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(opened);
        }
    }

    [Fact]
    public async Task FailsACompactionIntoCompactionFailure()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = new HarnessSettings
        {
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            RestartLimit = 1,
        };
        // 保留的近期上下文很小，于是第一次压缩就有可摘的内容。
        setup.Settings = setup.Settings with
        {
            Compaction = new CompactionPolicy
            {
                Enabled = false, ReserveTokens = 8_192, KeepRecentTokens = 10, BackgroundTokens = 0,
            },
        };
        // 摘要请求永不返回：崩溃发生在压缩的 summarize 阶段。
        var (hanging, reached, _) = HarnessTestSupport.FakeStep.Gated(
            HarnessTestSupport.AssistantReply("summary"));
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("a1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("a2")),
            hanging);
        var storage = new MemoryStorage();
        var (opened, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        opened.Resume();
        // 两轮够长的上下文：保留的近期部分超过 keepRecentTokens，于是切点落在中间，有可摘的内容。
        foreach (var text in new[] { "u1", "u2" })
        {
            var input = await root.SubmitAsync(
                new SubmissionDraft.Input
                {
                    Content = [new TextContent(string.Join(" ", Enumerable.Repeat(text, 200)))],
                }, Ctx);
            await (await opened.SubmissionAsync(input.Id, Ctx))!.WaitAsync(Ctx);
        }

        var compaction = await root.CompactAsync(null, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));

        var reports = new List<(Exception Error, ReportInfo Info)>();
        var reopened = await OpenAsync(storage.Reopen(), setup, reports);
        var reopenedRoot = await reopened.RootAsync(Ctx);
        try
        {
            reopened.Resume();
            var settled = await reopened.WaitForTaskAsync(TaskId<object?>.From(compaction.Value), Ctx);
            Assert.Equal(TaskOutcomeStatus.Failed, settled.State.Outcome!.Status);
            Assert.Equal("Compaction was interrupted once", settled.State.Outcome.Error!.Message);
            Assert.Equal("restart_limit", DetailOf(settled.State.Outcome.Error!)["reason"]);

            // 失败对客户端可见：compactionFailure 带着触发原因与错误，活动的压缩状态已清掉。
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                var live = await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx);
                return live is not null && live.ContainsKey("compactionFailure");
            });
            var liveValue = (await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx))!;
            var failure = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(liveValue["compactionFailure"]);
            Assert.Equal(compaction.Value, Convert.ToInt64(failure["taskId"]));
            Assert.Equal("manual", failure["trigger"]);
            Assert.False(liveValue.ContainsKey("compactions"));
            await HarnessTestSupport.WaitForAsync(() => reports.Any(report =>
                report.Info is ReportInfo.TaskInterrupted interrupted
                && interrupted.Task.Id == compaction
                && interrupted.Reason == "restart_limit"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    // ─── 脚手架 ────────────────────────────────────────────────────────────

    /// <summary>准备阶段挂住的提示段：崩溃发生在请求之前。</summary>
    private sealed class HangingSection(TaskCompletionSource reached) : IPromptSection
    {
        public string Key => "preamble";

        public bool? Tag => false;

        public async Task<string?> RenderAsync(PromptInput input, Context context)
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.AbortSignal ?? CancellationToken.None);
            return null;
        }
    }

    /// <summary>一次工具调用的应答（faux provider 的形状）。</summary>
    private static AssistantMessage ToolCallReply(string name, string callId)
        => new([new ToolCallContent(callId, name, new Dictionary<string, object?>())])
        {
            StopReason = StopReason.ToolUse,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(5, 7),
        };

    private static IReadOnlyDictionary<string, object?> DetailOf(TaskOutcomeError error)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(error.Detail);

    private static string Text(ChatMessage message)
        => string.Concat(message switch
        {
            AssistantMessage assistant => assistant.Content.OfType<TextContent>().Select(block => block.Text),
            ToolResultMessage tool => tool.Content.OfType<TextContent>().Select(block => block.Text),
            UserMessage user => user.Content.ToBlocks().OfType<TextContent>().Select(block => block.Text),
            _ => [],
        });

    private static async Task<IReadOnlyList<TaskRecord>> TasksAsync(HarnessImpl harness, ConversationId conversationId)
    {
        var page = await harness.CommitAsync(
            tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 20), Ctx);
        return page.Items;
    }

    /// <summary>打开 Harness 并收下每次上报的说明（<see cref="HarnessTestSupport.OpenHarnessAsync"/> 只留错误）。</summary>
    private static Task<HarnessImpl> OpenAsync(
        IStorage storage, HarnessTestSupport.ChatSetup setup, List<(Exception Error, ReportInfo Info)> reports)
        => DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models,
            Registry = setup.Registry,
            SettingsSource = () => setup.Settings,
            Now = () => setup.Now(),
            OnReport = (error, info) => reports.Add((error, info)),
        }, Ctx);
}
