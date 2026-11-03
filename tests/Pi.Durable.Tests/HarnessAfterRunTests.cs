using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 运行结束任务（harness-after-run.test.ts）：<c>afterRun</c> 钩子在运行回答之后要持久任务，生成以
/// <c>finish</c> / <c>drain</c> 两阶段把它们作为自己的孩子创建并等待；期间对话保持忙碌，任务终态后运行才结束。
/// </summary>
public class HarnessAfterRunTests
{
    private static readonly Context Ctx = Context.Background;

    private static readonly DurableTask<JsonDict, JsonDict, JsonDict> Audit = HarnessTestSupport.DefineTask(
        "test.audit", 1,
        [
            ("run", (TaskPhaseHandler)((_, runtime, context) => runtime.CommitAsync(
                (_, _) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
                }), context))),
        ]);

    [Fact]
    public async Task AfterRunHookTasksRunAfterTheAnswerAndKeepTheConversationBusy()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
        setup.Registry.Install(new HarnessTestSupport.TaskExtension("audit", [AnyDurableTask.From(Audit)]));
        var asked = new List<RunSummary>();
        HarnessTestSupport.AddHooks(setup.Registry, "pi.generation", new HarnessTestSupport.RunEndHooksImpl(run =>
        {
            asked.Add(run);
            return [new RunEndTaskRequest { Task = AnyDurableTask.From(Audit) }];
        }));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, record.Status);
            // 输入在答案提交时就结算 done；对话仍忙到 afterRun 的任务终态为止。
            await harness.WaitForIdleAsync(Ctx);

            // 钩子被问了一次，拿到的是已回答的运行：first 是运行第一个条目（这次是放置的用户条目），不是答案。
            var run = Assert.Single(asked);
            Assert.Equal("answer", run.Reason);
            Assert.Equal(record.Answer, run.Answer);
            var userEntry = (await HarnessTestSupport.AllEntriesAsync(root)).First(entry => entry.Kind == "pi.user");
            Assert.Equal(userEntry.Id, run.First);
            Assert.NotEqual(run.Answer, run.First);

            // 它要的任务确实被创建（属于生成）并跑完了。
            var tasks = await root.CommitAsync(
                tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 50), Ctx);
            var audit = Assert.Single(tasks.Items, task => task.Kind == "test.audit");
            Assert.Equal(TaskStatus.Terminal, audit.State.Status);
            Assert.Equal(TaskOutcomeStatus.Completed, audit.State.Outcome!.Status);
            Assert.NotNull(audit.Owner);

            // 运行在任务终态之后才结束：live 清空、没有失败记录。
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.Empty(live!);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ARunsBuiltInTaskRequestIsRejectedAndTheRunStillEnds()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
        HarnessTestSupport.AddHooks(setup.Registry, "pi.generation", new HarnessTestSupport.RunEndHooksImpl(_ =>
            [new RunEndTaskRequest { Task = AnyDurableTask.From(Audit) }]));
        // 内建任务不能被 afterRun 启动：写一个 pi.* 名字的请求。
        var builtin = new AnyDurableTask
        {
            Name = "pi.audit",
            Version = 1,
            Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
            Phases = new Dictionary<string, object?> { ["run"] = null },
        };
        HarnessTestSupport.AddHooks(setup.Registry, "pi.generation", new HarnessTestSupport.RunEndHooksImpl(_ =>
            [new RunEndTaskRequest { Task = builtin }]));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);
            // 运行照常结束（坏请求不拖住运行），也没有建出内建任务。
            Assert.Equal(SubmissionStatus.Done, record.Status);
            await harness.WaitForIdleAsync(Ctx);
            var tasks = await root.CommitAsync(
                tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 50), Ctx);
            Assert.DoesNotContain(tasks.Items, task => task.Kind == "pi.audit");
            Assert.Empty((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
