using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;

/// <summary>
/// 在调用方提交内受理提交（cc9c52310，#11149）：<c>SubmitAsync(tx, draft)</c> 与宿主自己的检查/写入同一次提交，
/// 或者一起存储、或者一起丢掉；它必须在该提交的首次表写之前，此后任何路径都抛 <c>ReadAfterWrite</c>。
/// </summary>
public class HarnessSubmitInCommitTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task AdmitsWithTheHostsOwnWritesInOneCommit()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var first = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await provider.Reached.WaitAsync(TimeSpan.FromSeconds(5));

            // 宿主自己的文档写可以在受理之前，自己的表写必须在它之后。
            var admitted = await harness.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, root.Id);
                Assert.NotNull(((IReadOnlyDictionary<string, object?>)live.Draft["run"]!)["taskId"]);
                var id = await root.SubmitAsync(tx, new SubmissionDraft.Input
                {
                    Content = [new TextContent("steer")],
                    WhenBusy = "steer",
                });
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.sent" });
                return id;
            }, Ctx);

            // 同一次提交里两样都在：宿主条目落盘，提交在队列里等边界。
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Contains(entries, entry => entry.Kind == "app.sent");
            var record = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await harness.SubmissionAsync(admitted, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Queued, record.Status);
            var liveValue = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            var run = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(liveValue!["run"]);
            // 受理没有并进当前运行：它排在下一边界。
            Assert.Equal(
                [first.Id.Value],
                ((IReadOnlyList<object?>)run["inputs"]!).Select(item => Convert.ToInt64(item)).ToArray());
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                var inbox = await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Ctx);
                return inbox is not null
                    && inbox.TryGetValue("items", out var items)
                    && items is IReadOnlyList<object?> list
                    && list.Any(item => item is IReadOnlyDictionary<string, object?> entry
                        && Convert.ToInt64(entry["id"]) == admitted.Value);
            });
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task IsRolledBackWithTheCommitWhenTheCallbackThrowsLater()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("ok")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CommitAsync(async tx =>
            {
                await root.SubmitAsync(tx, new SubmissionDraft.Input
                {
                    Content = [new TextContent("hi")],
                    RequestId = "r1",
                });
                throw new InvalidOperationException("host check failed");
            }, Ctx));
            Assert.Equal("host check failed", failed.Message);

            // 提交失败之后：按请求 ID 找不到它，也没有起运行、没有落条目。
            Assert.Null(await harness.CommitAsync(tx => tx.GetSubmissionByRequestAsync(root.Id, "r1"), Ctx));
            Assert.Empty(await HarnessTestSupport.AllEntriesAsync(root));
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.True(live is null || !live.ContainsKey("run"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartsTheRunOfAnIdleInputOnceTheCommitIsStored()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer")));
        // 不调用 Resume()：受理自己请求进度，运行照样起得来。
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var id = await harness.CommitAsync(
                tx => root.SubmitAsync(tx, new SubmissionDraft.Input { Content = [new TextContent("hi")] }), Ctx);
            // 受理请求了进度：不用 Resume()，运行也会被保留并跑完。
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                var status = (SubmissionRecord.InputRecord)(await (await harness.SubmissionAsync(id, Ctx))!
                    .StatusAsync(Ctx));
                return status.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered;
            });
            var settled = Assert.IsType<SubmissionRecord.InputRecord>(
                await (await harness.SubmissionAsync(id, Ctx))!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Done, settled.Status);
            Assert.Equal(
                ["pi.user", "pi.assistant"],
                (await HarnessTestSupport.AllEntriesAsync(root)).Select(entry => entry.Kind));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RefusesToAdmitAfterTheFirstTableWrite()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        try
        {
            var error = await Assert.ThrowsAsync<ReadAfterWrite>(() => harness.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
                return await root.SubmitAsync(tx, new SubmissionDraft.Input { Content = [new TextContent("hi")] });
            }, Ctx));
            Assert.Equal(
                "submit(tx, draft) cannot read tables after the first table write", error.Message);
            Assert.Empty(await HarnessTestSupport.AllEntriesAsync(root));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RefusesATransactionOfAnotherSession()
    {
        var (firstHarness, firstRoot) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var (secondHarness, _) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => secondHarness.CommitAsync(
                tx => firstRoot.SubmitAsync(tx, new SubmissionDraft.Input { Content = [new TextContent("hi")] }),
                Ctx));
            Assert.Equal("The transaction belongs to another Session", error.Message);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(firstHarness);
            await HarnessTestSupport.CloseQuietlyAsync(secondHarness);
        }
    }

    [Fact]
    public async Task RejectsABusyConversationsContinueBeforeAnyWriteSoTheCallbackCanCatchIt()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await provider.Reached.WaitAsync(TimeSpan.FromSeconds(5));

            // 拒绝发生在任何写之前：回调捕获它，自己的写入仍然提交成功。
            var caught = await harness.CommitAsync(async tx =>
            {
                Exception? rejection = null;
                try
                {
                    await root.SubmitAsync(tx, new SubmissionDraft.Continue());
                }
                catch (ConversationBusy busy)
                {
                    rejection = busy;
                }

                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.sent" });
                return rejection;
            }, Ctx);
            Assert.IsType<ConversationBusy>(caught);
            Assert.Contains(
                await HarnessTestSupport.AllEntriesAsync(root), entry => entry.Kind == "app.sent");
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RefusesASecondAdmissionInTheSameCommit()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await provider.Reached.WaitAsync(TimeSpan.FromSeconds(5));

            // 受理本身是表写：同一次提交里的第二次受理（以及任何受理之后的表写）都被拒。
            var error = await Assert.ThrowsAsync<ReadAfterWrite>(() => harness.CommitAsync(async tx =>
            {
                await root.SubmitAsync(tx, new SubmissionDraft.Input
                {
                    Content = [new TextContent("one")],
                    WhenBusy = "steer",
                });
                await root.SubmitAsync(tx, new SubmissionDraft.Input
                {
                    Content = [new TextContent("two")],
                    WhenBusy = "steer",
                });
            }, Ctx));
            Assert.Equal(
                "submit(tx, draft) cannot read tables after the first table write", error.Message);
            // 整次提交回滚：收件箱没有留下任何一件。
            var inbox = await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Ctx);
            Assert.True(
                inbox is null || ((IReadOnlyList<object?>)inbox["items"]!).Count == 0,
                "the rolled-back admissions must not queue anything");
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ContinuesAFailedRunOnlyBeforeTheCommitsTableWrites()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Failure()),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("recovered")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var first = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("Analyze this")] }, Ctx);
            Assert.Equal(SubmissionStatus.Unanswered, ((SubmissionRecord.InputRecord)
                (await first.WaitAsync(Ctx)).Record).Status);

            // 晚于表写的 continue 被拒。
            var late = await Assert.ThrowsAsync<ReadAfterWrite>(() => harness.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" });
                await root.SubmitAsync(tx, new SubmissionDraft.Continue());
            }, Ctx));
            Assert.Equal(
                "submit(tx, draft) cannot read tables after the first table write", late.Message);

            // 先受理再写：continue 与宿主条目同一次提交，运行继续。
            var continued = await harness.CommitAsync(async tx =>
            {
                var id = await root.SubmitAsync(tx, new SubmissionDraft.Continue());
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" });
                return id;
            }, Ctx);
            await HarnessTestSupport.WaitForAsync(async () =>
                ((SubmissionRecord.ContinueRecord)(await (await harness.SubmissionAsync(continued, Ctx))!
                    .StatusAsync(Ctx))).Status == SubmissionStatus.Done);
            // 继续后的那一轮真的答了。
            var assistant = (await HarnessTestSupport.AllEntriesAsync(root))
                .Last(entry => entry.Kind == "pi.assistant").Model!
                .OfType<AssistantMessage>().Last();
            Assert.Equal("recovered", assistant.Content.OfType<TextContent>().First().Text);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task LeavesNothingBehindWhenStorageFailsTheCommitAlsoAfterReopening()
    {
        var inner = new MemoryStorage();
        var storage = new ControlledHarnessStorage(inner);
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("ok")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var conversationId = root.Id;
        var failure = new InvalidOperationException("disk full");
        try
        {
            storage.FailNextCommit(failure);
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CommitAsync(async tx =>
            {
                await root.SubmitAsync(tx, new SubmissionDraft.Input
                {
                    Content = [new TextContent("hi")],
                    RequestId = "r1",
                });
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.sent" });
            }, Ctx));
            Assert.Same(failure, failed);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }

        // 存储拒绝这次提交之后：按请求 ID 找不到它，也没有条目、没有运行。
        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(inner.Reopen(), setup);
        try
        {
            Assert.Equal(conversationId, reopenedRoot.Id);
            var found = await reopened.CommitAsync(
                tx => tx.GetSubmissionByRequestAsync(conversationId, "r1"), Ctx);
            Assert.Null(found);
            Assert.Empty(await HarnessTestSupport.AllEntriesAsync(reopenedRoot));
            var live = await reopened.SnapshotAsync(Live.LiveDoc, conversationId, Ctx);
            Assert.True(live is null || !live.ContainsKey("run"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task DeduplicatesRequestIdsInsideTheCommit()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await provider.Reached.WaitAsync(TimeSpan.FromSeconds(5));

            var draft = new SubmissionDraft.Input
            {
                Content = [new TextContent("steer")],
                WhenBusy = "steer",
                RequestId = "r1",
            };
            var first = await harness.CommitAsync(tx => root.SubmitAsync(tx, draft), Ctx);
            var again = await harness.CommitAsync(tx => root.SubmitAsync(tx, draft), Ctx);
            Assert.Equal(first, again);
            // 只排了一件。
            var inbox = await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Ctx);
            var items = (IReadOnlyList<object?>)inbox!["items"]!;
            Assert.Single(items);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task AdmitsFromAToolsCommitThroughItsHandle()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        IConversation? other = null;
        IConversationHandle? handle = null;
        TaskId<object?>? toolTask = null;
        SubmissionId admitted = default;
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool(
            "delegate", "Delegates to another conversation", async (api, callContext) =>
            {
                toolTask = api.TaskId;
                handle = (await api.ConversationAsync(other!.Id, callContext).ConfigureAwait(false))!;
                admitted = await api.CommitAsync(async tx =>
                {
                    var id = await handle.SubmitAsync(tx, new SubmissionDraft.Input
                    {
                        Content = [new TextContent("from the tool")],
                    }).ConfigureAwait(false);
                    await tx.AppendEntryAsync(other!.Id, new EntryDraft { Kind = "app.delegated" })
                        .ConfigureAwait(false);
                    return id;
                }, callContext).ConfigureAwait(false);
                return new ToolExecutionResult { Content = [] };
            }));
        var reply = HarnessTestSupport.FakeStep.Of(messages =>
            messages.LastOrDefault() is UserMessage last
            && last.Content.ToBlocks().OfType<TextContent>().FirstOrDefault()?.Text == "delegate"
                ? new AssistantMessage([new ToolCallContent("c1", "delegate", new Dictionary<string, object?>())])
                {
                    StopReason = StopReason.ToolUse,
                    Model = "faux-1",
                    Provider = "faux",
                    UsageStats = new Pi.Ai.Types.Usage(0, 0),
                }
                : HarnessTestSupport.AssistantReply("ok"));
        provider.SetResponses(reply, reply, reply);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            other = await harness.CreateConversationAsync(new ConversationCreateOptions
            {
                Ownership = new ConversationOwnership.Ownerless(),
                Agent = new AgentChange { Model = new ModelRef("faux", "faux-1") },
            }, Ctx);
            var run = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("delegate")] }, Ctx);
            Assert.Equal(SubmissionStatus.Done, ((SubmissionRecord.InputRecord)
                (await run.WaitAsync(Ctx)).Record).Status);
            await harness.WaitForIdleAsync(Ctx);

            // 受理随工具的提交一起存储：另一条对话里放置的输入带这个工具任务的 byTaskId。
            var status = (SubmissionRecord.InputRecord)(await (await harness.SubmissionAsync(admitted, Ctx))!
                .StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Done, status.Status);
            Assert.NotNull(status.Entry);
            var placed = await harness.CommitAsync(tx => tx.GetEntryAsync(status.Entry!.Value), Ctx);
            Assert.Equal(toolTask, placed?.ByTaskId);
            Assert.Contains(
                await HarnessTestSupport.AllEntriesAsync(other), entry => entry.Kind == "app.delegated");

            // 调用结束后句柄不再受理。
            var ended = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CommitAsync(
                tx => handle!.SubmitAsync(tx, new SubmissionDraft.Input { Content = [new TextContent("late")] }),
                Ctx));
            Assert.Contains("invocation has ended", ended.Message);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    /// <summary>以 <c>error</c> 停止原因结束的空应答，让运行以 <c>model_error</c> 失败。</summary>
    private static AssistantMessage Failure() => new([])
    {
        StopReason = StopReason.Error,
        ErrorMessage = "Invalid request",
        Model = "faux-1",
        Provider = "faux",
        UsageStats = new Pi.Ai.Types.Usage(0, 0),
    };
}
