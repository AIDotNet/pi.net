using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 钩子与任务的条目读取（harness-hook-reads.test.ts）：读的是**原始历史**，压缩从不改动条目，所以由条目 ID
/// 界定的区间在压缩前后、以及重跑之间读到的是同一段。
/// </summary>
public class HarnessHookReadsTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task AHookReadsRawHistoryAndSingleEntries()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("done"));
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var seen = new List<(IReadOnlyList<string> Kinds, string? UserKind)>();
        HarnessTestSupport.AddHooks(setup.Registry, "pi.generation", new HarnessTestSupport.GenerationHooksImpl
        {
            OnYieldAsync = async (_, api, context) =>
            {
                var page = await api.EntriesAsync(new EntryRange { Order = ScanOrder.Ascending }, 10, null, context);
                var first = page.Items[0];
                var user = await api.EntryAsync(DurableEntries.UserEntry, first.Id, context);
                seen.Add((page.Items.Select(entry => entry.Kind).ToList(), user?.Kind));
                // 种类不符：null。
                Assert.Null(await api.EntryAsync(DurableEntries.AssistantEntry, first.Id, context));
                return null;
            },
        });
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx)).WaitAsync(Ctx);
            // onYield 提问时 assistant 条目还没提交。
            var only = Assert.Single(seen);
            Assert.Equal(["pi.user"], only.Kinds);
            Assert.Equal("pi.user", only.UserKind);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ATaskPagesItsConversationHistoryByEntryIdRange()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("done"));
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var pages = new List<IReadOnlyList<EntryId>>();
        var reader = HarnessTestSupport.DefineTask("test.reader", 1,
        [
            ("run", async (_, runtime, context) =>
            {
                var all = await runtime.EntriesAsync(new EntryRange { Order = ScanOrder.Ascending }, 1, null, context);
                pages.Add(all.Items.Select(entry => entry.Id).ToList());
                var rest = await runtime.EntriesAsync(
                    new EntryRange { Order = ScanOrder.Ascending }, 1, all.Next, context);
                pages.Add(rest.Items.Select(entry => entry.Id).ToList());
                var bounded = await runtime.EntriesAsync(new EntryRange
                {
                    MinEntryId = rest.Items[0].Id,
                    MaxEntryId = rest.Items[0].Id,
                }, 10, null, context);
                pages.Add(bounded.Items.Select(entry => entry.Id).ToList());
                await runtime.CommitAsync(
                    (_, _) => Task.FromResult<TaskState?>(new TaskState
                    {
                        Status = TaskStatus.Terminal,
                        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
                    }),
                    context);
            }),
        ]);
        // 计划外的任务定义不会被调度器接手，必须先注册到注册表。
        setup.Registry.Install(new HarnessTestSupport.TaskExtension("reader", [AnyDurableTask.From(reader)]));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx)).WaitAsync(Ctx);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(reader, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = root.Id,
            }), Ctx);
            harness.Resume();
            await HarnessTestSupport.WaitForAsync(() => pages.Count == 3);
            await harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            // 每页一条，按 ID 升序翻页；再按同一条 ID 做上下界。
            Assert.Equal(
                [[entries[0].Id], [entries[1].Id], [entries[1].Id]],
                pages);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
