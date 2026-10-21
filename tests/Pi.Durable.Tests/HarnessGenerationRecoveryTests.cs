using Pi.Ai.Types;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

public sealed class HarnessGenerationRecoveryTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    private sealed class InterruptOnceSection(TaskCompletionSource reached) : IPromptSection
    {
        private int _calls;

        public string Key => "preamble";

        public bool? Tag => false;

        public async Task<string?> RenderAsync(PromptInput input, Context context)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.AbortSignal ?? CancellationToken.None);
            }

            return "You are helpful.";
        }
    }

    private static AssistantMessage RetryableError()
        => new([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = "503 Service Unavailable",
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    private static string? TextOf(ChatMessage? message) => message switch
    {
        UserMessage user => string.Concat(user.Content.ToBlocks().OfType<TextContent>().Select(block => block.Text)),
        AssistantMessage assistant => string.Concat(assistant.Content.OfType<TextContent>().Select(block => block.Text)),
        _ => null,
    };

    private static async Task<IReadOnlyList<TaskRecord>> TasksAsync(HarnessImpl harness, ConversationId conversationId)
        => (await harness.CommitAsync(
            tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 10), Ctx)).Items;

    private static string? Phase(TaskRecord task)
        => task.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
            && checkpoint.TryGetValue("phase", out var phase)
            ? phase as string
            : null;

    private static async Task<string?> LivePartialAsync(HarnessImpl harness, ConversationId conversationId)
    {
        var live = await harness.SnapshotAsync(Live.LiveDoc, conversationId, Ctx);
        if (live is null || !live.TryGetValue("generation", out var rawGeneration)
            || rawGeneration is not IReadOnlyDictionary<string, object?> generation
            || !generation.TryGetValue("message", out var rawMessage))
        {
            return null;
        }

        return TextOf(JsonTrees.Deserialize<AssistantMessage>(rawMessage!));
    }

    [Fact]
    public async Task ReopensAndResendsARequestInterruptedBeforeItsFirstPartial()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        var (interrupted, reached, _) = HarnessTestSupport.FakeStep.Gated(
            HarnessTestSupport.AssistantReply("discarded"));
        provider.SetResponses(interrupted);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CloseAsync(Ctx);

        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("recovered")));
        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("recovered", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task RerunsPreparationInterruptedBeforeItsCommit()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer")));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Registry.Install(new HarnessTestSupport.SimpleExtension(
            "interrupt-once", sections: [new InterruptOnceSection(reached)]));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var taskId = Assert.Single(await TasksAsync(harness, root.Id)).Id;
        await harness.CloseAsync(Ctx);

        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var recoveredTask = Assert.Single(await TasksAsync(reopened, reopenedRoot.Id));
            Assert.Equal(TaskStatus.Pending, recoveredTask.State.Status);
            Assert.Equal("prepare", Phase(recoveredTask));
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.system", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("answer", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Equal(1, provider.CallCount);
            Assert.Equal(taskId.Value, Assert.Single(await TasksAsync(reopened, reopenedRoot.Id)).Id.Value);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task ReadsTheDurableAnswerAfterReopenWithoutRepeatingGeneration()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("42")));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("answer?")] }, Ctx);
        await submission.WaitAsync(Ctx);
        var completed = Assert.IsType<SubmissionRecord.InputRecord>(await submission.StatusAsync(Ctx));
        Assert.Equal(SubmissionStatus.Done, completed.Status);
        await harness.CloseAsync(Ctx);

        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            var persisted = Assert.IsType<SubmissionRecord.InputRecord>(
                await reopenedSubmission!.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Done, persisted.Status);
            Assert.Equal(completed.Answer, persisted.Answer);
            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("42", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task ConvertsCommittedPartialToAbortedEntryBeforeResendingAfterReopen()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Progress = AgentDocs.DefaultProgressPolicy with { PartialIntervalMs = 0 },
        };
        const int deltaCount = 5;
        var (interrupted, reached, _) = HarnessTestSupport.FakeStep.GatedAfterTextDelta(
            HarnessTestSupport.AssistantReply(new string('z', 400)), deltaCount);
        provider.SetResponses(interrupted);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));
        // reached 在第 deltaCount 个事件被产出时触发，消费者可能还没提交该增量；等 live 快照
        // 真正包含全部增量后再读取，否则 close 前落盘的 partial 可能比这里捕获的多一个字符。
        await HarnessTestSupport.WaitForAsync(
            async () => await LivePartialAsync(harness, root.Id) is { Length: >= deltaCount });
        var partial = await LivePartialAsync(harness, root.Id);
        Assert.NotNull(partial);
        await harness.CloseAsync(Ctx);

        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("recovered")));
        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind));
            var interruptedEntry = Assert.IsType<AssistantMessage>(Assert.Single(entries[1].Model!));
            Assert.Equal(StopReason.Aborted, interruptedEntry.StopReason);
            Assert.Equal(partial, TextOf(interruptedEntry));
            Assert.Equal("recovered", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Empty((await reopened.SnapshotAsync(Live.LiveDoc, reopenedRoot.Id, Ctx))!);
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task ResumesRetryBackoffAfterReopen()
    {
        var now = 1_000L;
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Now = () => Interlocked.Read(ref now);
        setup.Settings = setup.Settings with
        {
            Retry = new ConversationRetryPolicy { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(RetryableError()),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("recovered")));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await HarnessTestSupport.WaitForAsync(async () =>
            (await TasksAsync(harness, root.Id)).Any(task =>
                task.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
                && checkpoint.TryGetValue("phase", out var phase) && Equals(phase, "retry")));
        await harness.CloseAsync(Ctx);

        Interlocked.Exchange(ref now, 61_000L);
        var (reopened, _) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var recoveredTask = Assert.Single(await TasksAsync(reopened, root.Id));
            Assert.Equal(TaskStatus.Pending, recoveredTask.State.Status);
            var checkpoint = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(recoveredTask.State.Checkpoint);
            Assert.Equal("retry", checkpoint["phase"]);
            Assert.Equal(61_000L, Convert.ToInt64(checkpoint["until"]));

            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(await reopened.RootAsync(Ctx));
            Assert.Equal(["pi.user", "pi.assistant", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("recovered", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task ResumesDeferredPollingAfterReopen()
    {
        var now = 1_000L;
        var faux = Faux.CreateProvider(new Faux.FauxProviderOptions
        {
            Deferred = new Faux.FauxDeferredOptions { PollAfterMs = 60_000 },
        });
        var setup = HarnessTestSupport.NewChatSetup(faux.Provider);
        setup.Now = () => Interlocked.Read(ref now);
        setup.Settings = setup.Settings with
        {
            Stream = new ConversationStreamOptions
            {
                Deferred = new ConversationStreamOptions.DeferredOptions { Enabled = true },
            },
        };
        faux.SetResponses([Faux.Response.Text("deferred answer")]);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await HarnessTestSupport.WaitForAsync(async () =>
            (await TasksAsync(harness, root.Id)).Any(task =>
                task.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
                && checkpoint.TryGetValue("phase", out var phase) && Equals(phase, "poll")));
        await harness.CloseAsync(Ctx);

        Interlocked.Exchange(ref now, 61_000L);
        var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            Assert.Equal("deferred answer", TextOf(entries[^1].Model?.SingleOrDefault()));
            Assert.Equal(1, faux.State.DeferredFetchCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task FailsDeferredPollWhenItsPinnedModelIsUnavailableAfterReopen()
    {
        var faux = Faux.CreateProvider(new Faux.FauxProviderOptions
        {
            Deferred = new Faux.FauxDeferredOptions { PollAfterMs = 60_000 },
        });
        var setup = HarnessTestSupport.NewChatSetup(faux.Provider);
        setup.Settings = setup.Settings with
        {
            Stream = new ConversationStreamOptions
            {
                Deferred = new ConversationStreamOptions.DeferredOptions { Enabled = true },
            },
        };
        faux.SetResponses([Faux.Response.Text("never fetched")]);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await HarnessTestSupport.WaitForAsync(async () =>
            (await TasksAsync(harness, root.Id)).Any(task =>
                task.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
                && checkpoint.TryGetValue("phase", out var phase) && Equals(phase, "poll")));
        await harness.CloseAsync(Ctx);

        var unavailableModelSetup = new HarnessTestSupport.ChatSetup
        {
            Provider = setup.Provider,
            ModelId = setup.ModelId,
            Models = new Models(),
            Registry = setup.Registry,
            Reports = setup.Reports,
            Settings = setup.Settings,
            Now = setup.Now,
        };
        var (reopened, _) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), unavailableModelSetup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission!.WaitAsync(Ctx);

            var record = Assert.IsType<SubmissionRecord.InputRecord>(await reopenedSubmission.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("no_model", record.Reason);
            Assert.Equal(0, faux.State.DeferredFetchCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }

    [Fact]
    public async Task FailsPinnedRequestWhenItsModelIsUnavailableAfterReopen()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        var (interrupted, reached, _) = HarnessTestSupport.FakeStep.Gated(
            HarnessTestSupport.AssistantReply("discarded"));
        provider.SetResponses(interrupted);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CloseAsync(Ctx);

        var unavailableModelSetup = new HarnessTestSupport.ChatSetup
        {
            Provider = setup.Provider,
            ModelId = setup.ModelId,
            Models = new Models(),
            Registry = setup.Registry,
            Reports = setup.Reports,
            Settings = setup.Settings,
            Now = setup.Now,
        };
        var (reopened, _) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), unavailableModelSetup);
        try
        {
            var reopenedSubmission = await reopened.SubmissionAsync(submission.Id, Ctx);
            Assert.NotNull(reopenedSubmission);
            reopened.Resume();
            await reopenedSubmission.WaitAsync(Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>(await reopenedSubmission.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("no_model", record.Reason);
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(reopened);
        }
    }
}
