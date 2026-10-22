using Pi.Ai.Types;
using Pi.Ai.Providers;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

public sealed class HarnessCompactionTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    private static AssistantMessage RetryableError()
        => new([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = "503 Service Unavailable",
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    private static AssistantMessage ContextOverflowError(string message)
        => new([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    private static async Task<IReadOnlyList<TaskRecord>> TasksAsync(HarnessImpl harness, ConversationId conversationId)
        => (await harness.CommitAsync(
            tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 20), Ctx)).Items;

    private static string? Phase(TaskRecord task)
        => task.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
            && checkpoint.TryGetValue("phase", out var phase)
            ? phase as string
            : null;

    private sealed class SummaryHook(string summary) : ICompactionHooks
    {
        public CompactionInput? Input { get; private set; }

        public Task<CompactionHookDecision?> BeforeCompact(
            CompactionInput compaction, IHookApi api, Context context)
        {
            Input = compaction;
            return Task.FromResult<CompactionHookDecision?>(new CompactionHookDecision { Summary = summary });
        }
    }

    private sealed class InterruptibleCompactionHook : ICompactionHooks
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public async Task<CompactionHookDecision?> BeforeCompact(
            CompactionInput compaction, IHookApi api, Context context)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Reached.TrySetResult();
                var signal = context.AbortSignal
                    ?? throw new InvalidOperationException("Compaction hook context has no abort signal");
                await HarnessTestSupport.RaceAsync(_gate.Task, signal).ConfigureAwait(false);
            }

            return new CompactionHookDecision { Summary = "RECOVERED HOOK SUMMARY" };
        }
    }

    private static string? TextOf(ChatMessage? message) => message switch
    {
        UserMessage user => string.Concat(user.Content.ToBlocks().OfType<TextContent>().Select(block => block.Text)),
        AssistantMessage assistant => string.Concat(assistant.Content.OfType<TextContent>().Select(block => block.Text)),
        _ => null,
    };

    [Fact]
    public void SerializeConversationFormatsTranscriptOmitsThinkingAndTruncatesToolResults()
    {
        var messages = new ChatMessage[]
        {
            new SystemMessage("", Timestamp: 0, Sections: new Dictionary<string, string?> { ["s"] = "hidden" }),
            new UserMessage([new TextContent("hello")], 0),
            new AssistantMessage([
                new ThinkingContent("hmm"),
                new TextContent("sure"),
                new ToolCallContent("c", "read", new Dictionary<string, object?> { ["path"] = "a.ts" }),
            ]),
            new ToolResultMessage("c", "read", [new TextContent(new string('y', 2500))]),
        };

        var serialized = Compaction.SerializeConversation(messages);

        Assert.DoesNotContain("hidden", serialized);
        Assert.Contains("[User]: hello", serialized);
        // 思维链整段省略（上游 15e7bdc14）：回放的思维块常常不在模型活上下文里。
        Assert.DoesNotContain("hmm", serialized);
        Assert.DoesNotContain("[Assistant thinking]", serialized);
        Assert.Contains("[Assistant]: sure", serialized);
        Assert.Contains("[Assistant tool calls]: read(path=\"a.ts\")", serialized);
        Assert.Contains(
            $"[Tool result]: {new string('y', 2000)}\n\n[... 500 more characters truncated]",
            serialized);
    }

    [Fact]
    public async Task ManualCompactionCompletesWithoutSummaryWhenThereIsNothingToCut()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("hello")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await submission.WaitAsync(Ctx);
            var callsBeforeCompaction = provider.CallCount;

            var id = await root.CompactAsync(null, Ctx);
            var settled = await harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);

            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
            Assert.Equal(callsBeforeCompaction, provider.CallCount);
            Assert.DoesNotContain(
                await HarnessTestSupport.AllEntriesAsync(root),
                entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ManualCompactionSummarizesSelectedPrefixAndPreservesRawHistory()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { KeepRecentTokens = 1 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("SUMMARY")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var before = await HarnessTestSupport.AllEntriesAsync(root);
            var beforeView = await root.ContextAsync(Ctx);
            var cut = Assert.IsType<int>(Compaction.SelectCut(beforeView, 1));
            var firstKept = beforeView.Entries[cut].Id;

            var id = await root.CompactAsync("keep the answer", Ctx);
            var settled = await harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
            await root.WaitForIdleAsync(Ctx);

            var after = await HarnessTestSupport.AllEntriesAsync(root);
            var beforeHistory = before.Select(entry =>
                (entry.Id, entry.Kind, entry.Head, Text: TextOf(entry.Model?.SingleOrDefault()))).ToArray();
            var afterHistory = after.Take(before.Count).Select(entry =>
                (entry.Id, entry.Kind, entry.Head, Text: TextOf(entry.Model?.SingleOrDefault()))).ToArray();
            Assert.Equal(beforeHistory, afterHistory);
            var marker = Assert.Single(after, entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
            Assert.Equal(firstKept, marker.Head);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(marker.Data);
            Assert.Equal("manual", data["reason"]);
            var summaryMessage = Assert.IsType<UserMessage>(Assert.Single(marker.Model!));
            Assert.Equal(
                "The conversation history before this point was compacted into the following summary:\n\n<summary>\nSUMMARY\n</summary>",
                Assert.IsType<TextContent>(summaryMessage.Content.ToBlocks().Single()).Text);
            Assert.Equal(3, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task DoesNotRepeatAfterPlacedSummaryIsReopened()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { KeepRecentTokens = 1 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("SUMMARY")));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var id = await root.CompactAsync(null, Ctx);
            var settled = await harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
            await root.WaitForIdleAsync(Ctx);

            var before = await HarnessTestSupport.AllEntriesAsync(root);
            var beforeHistory = before.Select(entry =>
                (entry.Id, entry.Kind, entry.Head, Text: TextOf(entry.Model?.SingleOrDefault()))).ToArray();
            var callsBeforeReopen = provider.CallCount;
            await harness.CloseAsync(Ctx);

            var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
            try
            {
                await reopenedRoot.WaitForIdleAsync(Ctx);

                var after = await HarnessTestSupport.AllEntriesAsync(reopenedRoot);
                var afterHistory = after.Select(entry =>
                    (entry.Id, entry.Kind, entry.Head, Text: TextOf(entry.Model?.SingleOrDefault()))).ToArray();
                Assert.Equal(beforeHistory, afterHistory);
                Assert.Equal(callsBeforeReopen, provider.CallCount);
                var persistedTask = Assert.Single(
                    await TasksAsync(reopened, root.Id), task => task.Id.Value == id.Value);
                Assert.Equal(TaskOutcomeStatus.Completed, persistedTask.State.Outcome?.Status);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(reopened);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task BeforeCompactHookCanSupplySummaryWithoutCallingTheProvider()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { KeepRecentTokens = 1 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")));
        var hook = new SummaryHook("FROM HOOK");
        HarnessTestSupport.AddHooks(setup.Registry, Compaction.TaskName, hook);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var id = await root.CompactAsync("focus", Ctx);
            var settled = await harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
            await root.WaitForIdleAsync(Ctx);

            Assert.Equal(2, provider.CallCount);
            Assert.Equal(Pi.Durable.Harness.CompactionReason.Manual, hook.Input?.Reason);
            Assert.Equal("focus", hook.Input?.Instructions);
            var marker = Assert.Single(
                await HarnessTestSupport.AllEntriesAsync(root),
                entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
            var summary = Assert.IsType<UserMessage>(Assert.Single(marker.Model!));
            Assert.Contains("FROM HOOK", summary.Content.ToBlocks().OfType<TextContent>().Single().Text);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RerunsSelectionHookAfterReopenWhenTheFirstInvocationIsInterrupted()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { Enabled = false, KeepRecentTokens = 1 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")));
        var hook = new InterruptibleCompactionHook();
        HarnessTestSupport.AddHooks(setup.Registry, Compaction.TaskName, hook);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var id = await root.CompactAsync(null, Ctx);
            await hook.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.CloseAsync(Ctx);

            var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
            try
            {
                var settled = await reopened.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
                Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
                await reopenedRoot.WaitForIdleAsync(Ctx);

                Assert.Equal(2, hook.Calls);
                Assert.Equal(2, provider.CallCount);
                var marker = Assert.Single(
                    await HarnessTestSupport.AllEntriesAsync(reopenedRoot),
                    entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
                var summary = Assert.IsType<UserMessage>(Assert.Single(marker.Model!));
                Assert.Contains(
                    "RECOVERED HOOK SUMMARY",
                    summary.Content.ToBlocks().OfType<TextContent>().Single().Text);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(reopened);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ResumesSummarizingCompactionAfterReopen()
    {
        var faux = Faux.CreateProvider();
        var setup = HarnessTestSupport.NewChatSetup(faux.Provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { KeepRecentTokens = 1 },
        };
        var summaryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedSummary = new Faux.FactoryStep(async (_, options, _, _) =>
        {
            summaryReached.TrySetResult();
            await HarnessTestSupport.RaceAsync(summaryGate.Task, options?.Signal ?? default);
            return Faux.ScriptedMessage("discarded");
        });
        faux.SetResponses([
            Faux.Response.Text("answer 1"),
            Faux.Response.Text("answer 2"),
            interruptedSummary,
        ]);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var id = await root.CompactAsync(null, Ctx);
            await summaryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.CloseAsync(Ctx);

            faux.SetResponses([Faux.Response.Text("RECOVERED SUMMARY")]);
            var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
            try
            {
                var settled = await reopened.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
                Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
                await reopenedRoot.WaitForIdleAsync(Ctx);

                var marker = Assert.Single(
                    await HarnessTestSupport.AllEntriesAsync(reopenedRoot),
                    entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
                var summary = Assert.IsType<UserMessage>(Assert.Single(marker.Model!));
                Assert.Contains("RECOVERED SUMMARY", summary.Content.ToBlocks().OfType<TextContent>().Single().Text);
                Assert.Equal(4, faux.State.CallCount);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(reopened);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ResumesCompactionRetryBackoffAfterReopen()
    {
        var now = 1_000L;
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Now = () => Interlocked.Read(ref now);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with { KeepRecentTokens = 1 },
            Retry = new ConversationRetryPolicy { Enabled = true, MaxRetries = 2, BaseDelayMs = 60_000 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(RetryableError()),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("RECOVERED SUMMARY")));
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var id = await root.CompactAsync(null, Ctx);
            await HarnessTestSupport.WaitForAsync(async () =>
                (await TasksAsync(harness, root.Id)).Any(task =>
                    task.Id.Value == id.Value && Phase(task) == "retry"));
            await harness.CloseAsync(Ctx);

            Interlocked.Exchange(ref now, 61_000L);
            var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
            try
            {
                var retryTask = Assert.Single(await TasksAsync(reopened, root.Id), task => task.Id.Value == id.Value);
                Assert.Equal(TaskStatus.Pending, retryTask.State.Status);
                Assert.Equal("retry", Phase(retryTask));
                var settled = await reopened.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
                Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
                await reopenedRoot.WaitForIdleAsync(Ctx);

                var marker = Assert.Single(
                    await HarnessTestSupport.AllEntriesAsync(reopenedRoot),
                    entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
                var summary = Assert.IsType<UserMessage>(Assert.Single(marker.Model!));
                Assert.Contains("RECOVERED SUMMARY", summary.Content.ToBlocks().OfType<TextContent>().Single().Text);
                Assert.Equal(4, provider.CallCount);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(reopened);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task KeepsGenerationWaitingOnBlockingCompactionAcrossReopen()
    {
        var faux = Faux.CreateProvider();
        var setup = HarnessTestSupport.NewChatSetup(faux.Provider);
        var policy = AgentDocs.DefaultCompactionPolicy with
        {
            Enabled = false,
            ReserveTokens = 127_999,
            KeepRecentTokens = 1,
            BackgroundTokens = 0,
        };
        setup.Settings = setup.Settings with
        {
            Compaction = policy,
        };
        var summaryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedSummary = new Faux.FactoryStep(async (_, options, _, _) =>
        {
            summaryReached.TrySetResult();
            await HarnessTestSupport.RaceAsync(summaryGate.Task, options?.Signal ?? default);
            return Faux.ScriptedMessage("discarded");
        });
        faux.SetResponses([
            Faux.Response.Text("answer 1"),
            Faux.Response.Text("answer 2"),
            interruptedSummary,
        ]);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            setup.Settings = setup.Settings with { Compaction = policy with { Enabled = true } };
            await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("current input")] }, Ctx);
            await summaryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.CloseAsync(Ctx);

            faux.SetResponses([
                Faux.Response.Text("RECOVERED SUMMARY"),
                Faux.Response.Text("final answer"),
            ]);
            var (reopened, reopenedRoot) = await HarnessTestSupport.OpenChatAsync(storage.Reopen(), setup);
            try
            {
                await reopenedRoot.WaitForIdleAsync(Ctx);
                var kinds = (await HarnessTestSupport.AllEntriesAsync(reopenedRoot))
                    .Select(entry => entry.Kind).ToArray();
                Assert.Equal(
                    [DurableEntries.CompactionEntry.Kind, DurableEntries.SystemEntry.Kind, DurableEntries.AssistantEntry.Kind],
                    kinds[^3..]);
                Assert.Equal(5, faux.State.CallCount);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(reopened);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartsBackgroundCompactionWithoutBlockingTheRunOrIdleWait()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        var policy = AgentDocs.DefaultCompactionPolicy with
        {
            Enabled = false,
            ReserveTokens = 127_000,
            KeepRecentTokens = 1,
            BackgroundTokens = 999,
        };
        setup.Settings = setup.Settings with { Compaction = policy };
        var summaryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = HarnessTestSupport.FakeStep.Of(async messages =>
        {
            if (messages.OfType<SystemMessage>().Any(message =>
                message.Content?.ToBlocks().OfType<TextContent>().Any(block =>
                    block.Text.Contains("context summarization assistant", StringComparison.Ordinal)) == true))
            {
                summaryReached.TrySetResult();
                await summaryGate.Task;
                return HarnessTestSupport.AssistantReply("SUMMARY");
            }

            return HarnessTestSupport.AssistantReply("answer");
        });
        provider.SetResponses(Enumerable.Repeat(response, 8).ToArray());
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            setup.Settings = setup.Settings with { Compaction = policy with { Enabled = true } };
            var current = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("current input")] }, Ctx);
            await summaryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var settled = await current.WaitAsync(Ctx);
            var inputRecord = Assert.IsType<SubmissionRecord.InputRecord>(settled.Record);
            Assert.Equal(SubmissionStatus.Done, inputRecord.Status);
            await root.WaitForIdleAsync(Ctx).WaitAsync(TimeSpan.FromSeconds(5));

            var compaction = Assert.Single(
                await TasksAsync(harness, root.Id),
                task => Phase(task) == "summarize");
            Assert.Equal(TaskStatus.Running, compaction.State.Status);

            summaryGate.TrySetResult();
            var completed = await harness.WaitForTaskAsync(compaction.Id, Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, completed.State.Outcome?.Status);
            await root.WaitForIdleAsync(Ctx);
            Assert.Single(
                await HarnessTestSupport.AllEntriesAsync(root),
                entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
        }
        finally
        {
            summaryGate.TrySetResult();
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task CompactsAndRetriesAContextOverflowWithoutCountingItAsAnotherAttempt()
    {
        const string overflowText = "prompt is too long: 250000 tokens > 200000 maximum";
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with
            {
                Enabled = true,
                KeepRecentTokens = 1,
                BackgroundTokens = 0,
            },
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(ContextOverflowError(overflowText)),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("SUMMARY")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("final answer")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var submissionAfterOverflow = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("current input")] }, Ctx);
            await submissionAfterOverflow.WaitAsync(Ctx);
            await root.WaitForIdleAsync(Ctx);

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Single(entries, entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
            Assert.Contains(entries, entry => entry.Model?.OfType<AssistantMessage>()
                .Any(message => message.StopReason == StopReason.Error && message.ErrorMessage == overflowText) == true);
            Assert.Equal(5, provider.CallCount);
            Assert.Equal(
                "final answer",
                TextOf(Assert.IsType<AssistantMessage>(entries[^1].Model?.SingleOrDefault())));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task DoesNotRecompactAfterTheRetryAlsoOverflows()
    {
        const string overflowText = "prompt is too long: 250000 tokens > 200000 maximum";
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with
            {
                Enabled = true,
                KeepRecentTokens = 1,
                BackgroundTokens = 0,
            },
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(ContextOverflowError(overflowText)),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("SUMMARY")),
            HarnessTestSupport.FakeStep.Of(ContextOverflowError(overflowText)));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var submissionAfterOverflow = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("current input")] }, Ctx);
            var settled = await submissionAfterOverflow.WaitAsync(Ctx);
            await root.WaitForIdleAsync(Ctx);

            var inputRecord = Assert.IsType<SubmissionRecord.InputRecord>(settled.Record);
            Assert.Equal(SubmissionStatus.Unanswered, inputRecord.Status);
            Assert.Equal("model_error", inputRecord.Reason);
            Assert.Equal(overflowText, inputRecord.Detail);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Single(entries, entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
            Assert.Equal(2, entries.Sum(entry => entry.Model?.OfType<AssistantMessage>()
                .Count(message => message.StopReason == StopReason.Error && message.ErrorMessage == overflowText) ?? 0));
            Assert.Equal(5, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task DoesNotCompactOverflowWhenCompactionIsDisabled()
    {
        const string overflowText = "prompt is too long: 250000 tokens > 200000 maximum";
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            Compaction = AgentDocs.DefaultCompactionPolicy with
            {
                Enabled = false,
                KeepRecentTokens = 1,
                BackgroundTokens = 0,
            },
            Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("answer 2")),
            HarnessTestSupport.FakeStep.Of(ContextOverflowError(overflowText)));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            foreach (var input in new[] { "first", "second" })
            {
                var submission = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent(input)] }, Ctx);
                await submission.WaitAsync(Ctx);
            }

            var submissionAfterOverflow = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("current input")] }, Ctx);
            var settled = await submissionAfterOverflow.WaitAsync(Ctx);

            var inputRecord = Assert.IsType<SubmissionRecord.InputRecord>(settled.Record);
            Assert.Equal(SubmissionStatus.Unanswered, inputRecord.Status);
            Assert.Equal("model_error", inputRecord.Reason);
            Assert.Equal(overflowText, inputRecord.Detail);
            Assert.DoesNotContain(
                await HarnessTestSupport.AllEntriesAsync(root),
                entry => entry.Kind == DurableEntries.CompactionEntry.Kind);
            Assert.Equal(3, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
