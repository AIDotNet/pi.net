using System.Collections.Concurrent;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using JsonDict = IReadOnlyDictionary<string, object?>;

public sealed class HarnessContextCacheTests
{
    private static readonly Context Ctx = Context.Background;

    private sealed record ContextRead(ContextView View, int ScannedRows);

    private sealed record Fixture(
        HarnessImpl Harness,
        IConversation Root,
        CountingStorage Storage,
        List<object> Reports);

    [Fact]
    public async Task RuntimeContext_CachesAcrossTasksAndScansOnlyAppendedEntries()
    {
        var storage = new CountingStorage(new MemoryStorage());
        var reads = new ConcurrentQueue<ContextRead>();
        var probe = CreateProbe(async (runtime, context, conversationId) =>
            reads.Enqueue(await ReadAsync(storage, runtime, context, conversationId)));
        var fixture = await OpenAsync(storage, probe);
        try
        {
            await AppendMessageAsync(fixture.Root, User("first", 1));
            await AppendMessageAsync(fixture.Root, new SystemMessage(
                Sections: new Dictionary<string, string?> { ["base"] = "prompt" }, Timestamp: 2));

            await RunProbeAsync(fixture, probe);
            var first = Dequeue(reads);
            Assert.Equal(3, first.ScannedRows);
            Assert.IsType<SystemMessage>(first.View.Messages[0]);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), first.View);

            await AppendMessageAsync(fixture.Root, User("next", 3));
            await RunProbeAsync(fixture, probe);
            var extended = Dequeue(reads);
            Assert.Equal(2, extended.ScannedRows);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), extended.View);
            Assert.Equal(["system", "user:first", "user:next"], Describe(extended.View.Messages));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(fixture.Harness);
        }
    }

    [Fact]
    public async Task RuntimeContext_ResolvesOpenToolResultsAcrossIncrementalReads()
    {
        var storage = new CountingStorage(new MemoryStorage());
        var reads = new ConcurrentQueue<ContextRead>();
        var probe = CreateProbe(async (runtime, context, conversationId) =>
            reads.Enqueue(await ReadAsync(storage, runtime, context, conversationId)));
        var fixture = await OpenAsync(storage, probe);
        try
        {
            await AppendMessageAsync(fixture.Root, User("question", 1));
            await RunProbeAsync(fixture, probe);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), Dequeue(reads).View);

            var call = new AssistantMessage(
                [new TextContent("calling"), new ToolCallContent(
                    "call-1", "read", new Dictionary<string, object?>())],
                StopReason.ToolUse,
                Timestamp: 2);
            await AppendMessageAsync(fixture.Root, call);
            await RunProbeAsync(fixture, probe);
            var missing = Dequeue(reads);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), missing.View);
            Assert.Equal(
                ["user:question", "assistant:calling:ToolUse", "tool:call-1:True"],
                Describe(missing.View.Messages));

            await AppendMessageAsync(fixture.Root,
                new ToolResultMessage("call-1", "read", [new TextContent("ok")], Timestamp: 3));
            await RunProbeAsync(fixture, probe);
            var completed = Dequeue(reads);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), completed.View);
            Assert.Equal(
                ["user:question", "assistant:calling:ToolUse", "tool:call-1:False"],
                Describe(completed.View.Messages));

            await AppendMessageAsync(fixture.Root, new AssistantMessage([new TextContent("done")]));
            await AppendMessageAsync(fixture.Root,
                new ToolResultMessage("orphan", "read", [new TextContent("unused")], Timestamp: 4));
            await RunProbeAsync(fixture, probe);
            var ignored = Dequeue(reads);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), ignored.View);
            Assert.DoesNotContain(ignored.View.Messages, message => message is ToolResultMessage
                { ToolCallId: "orphan" });
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(fixture.Harness);
        }
    }

    [Fact]
    public async Task RuntimeContext_RebuildsForEditsAndHeadChangesWithoutReplacingNewerCacheWithHistoricalReads()
    {
        var storage = new CountingStorage(new MemoryStorage());
        var reads = new ConcurrentQueue<ContextRead>();
        EntryId? at = null;
        var probe = CreateProbe(async (runtime, context, conversationId) =>
            reads.Enqueue(await ReadAsync(storage, runtime, context, conversationId, at)));
        var fixture = await OpenAsync(storage, probe);
        try
        {
            var first = await AppendMessageAsync(fixture.Root, User("first", 1));
            var second = await AppendMessageAsync(fixture.Root, User("second", 2));

            await RunProbeAsync(fixture, probe);
            Assert.Equal(3, Dequeue(reads).ScannedRows);

            await fixture.Root.CommitAsync(tx => tx.AppendEntryAsync(fixture.Root.Id, new EntryDraft
            {
                Kind = "edit",
                Edits = [new ContextEdit.Replace(first.Id, [User("first edited", 3)])],
            }), Ctx);
            await RunProbeAsync(fixture, probe);
            var edited = Dequeue(reads);
            Assert.Equal(2, edited.ScannedRows);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), edited.View);
            Assert.Equal(["user:first edited", "user:second"], Describe(edited.View.Messages));

            await fixture.Root.CommitAsync(tx => tx.AppendEntryAsync(fixture.Root.Id, new EntryDraft
            {
                Kind = "summary",
                Head = second.Id,
                Model = [User("summary", 4)],
            }), Ctx);
            await RunProbeAsync(fixture, probe);
            var headed = Dequeue(reads);
            Assert.Equal(4, headed.ScannedRows);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), headed.View);

            at = first.Id;
            await RunProbeAsync(fixture, probe);
            var historical = Dequeue(reads);
            Assert.Equal(1, historical.ScannedRows);
            var historicalExpected = await ConversationContext.ReadContextAsync(
                fixture.Harness, fixture.Storage, fixture.Root.Id, Ctx, first.Id);
            AssertEquivalent(historicalExpected, historical.View);

            at = null;
            await RunProbeAsync(fixture, probe);
            var current = Dequeue(reads);
            Assert.Equal(1, current.ScannedRows);
            AssertEquivalent(await fixture.Root.ContextAsync(Ctx), current.View);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(fixture.Harness);
        }
    }

    [Fact]
    public async Task RuntimeContext_DropsIdleCacheWhenRetentionIsZero()
    {
        var storage = new CountingStorage(new MemoryStorage());
        var reads = new ConcurrentQueue<ContextRead>();
        var probe = CreateProbe(async (runtime, context, conversationId) =>
            reads.Enqueue(await ReadAsync(storage, runtime, context, conversationId)));
        var fixture = await OpenAsync(
            storage, probe, new HarnessSettings { ContextRetentionMs = 0 });
        try
        {
            await AppendMessageAsync(fixture.Root, User("first", 1));
            await AppendMessageAsync(fixture.Root, User("second", 2));

            await RunProbeAsync(fixture, probe);
            Assert.Equal(3, Dequeue(reads).ScannedRows);
            await RunProbeAsync(fixture, probe);
            Assert.Equal(3, Dequeue(reads).ScannedRows);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(fixture.Harness);
        }
    }

    [Fact]
    public async Task RuntimeContext_ExpiresIdleCacheOnTimer()
    {
        var clock = 1_000L;
        var storage = new CountingStorage(new MemoryStorage());
        var reads = new ConcurrentQueue<ContextRead>();
        var probe = CreateProbe(async (runtime, context, conversationId) =>
            reads.Enqueue(await ReadAsync(storage, runtime, context, conversationId)));
        var fixture = await OpenAsync(
            storage,
            probe,
            new HarnessSettings { ContextRetentionMs = 40 },
            () => Interlocked.Read(ref clock));
        try
        {
            await AppendMessageAsync(fixture.Root, User("first", 1));
            await AppendMessageAsync(fixture.Root, User("second", 2));

            await RunProbeAsync(fixture, probe);
            Assert.Equal(3, Dequeue(reads).ScannedRows);
            Interlocked.Exchange(ref clock, 1_040);
            await Task.Delay(200);
            Interlocked.Exchange(ref clock, 1_000);

            await RunProbeAsync(fixture, probe);
            Assert.Equal(3, Dequeue(reads).ScannedRows);
            Assert.Empty(fixture.Reports);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(fixture.Harness);
        }
    }

    private static async Task<Fixture> OpenAsync(
        CountingStorage storage,
        DurableTask<JsonDict, JsonDict, JsonDict> probe,
        HarnessSettings? settings = null,
        Func<long>? now = null)
    {
        var registry = Registry.CreateRegistry();
        registry.Install(new HarnessTestSupport.TaskExtension("context-cache", [AnyDurableTask.From(probe)]));
        var reports = new List<object>();
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = new Models(),
            Registry = registry,
            Settings = settings,
            Now = now,
            OnReport = (error, _) => reports.Add(error),
        }, Ctx);
        return new Fixture(harness, await harness.RootAsync(Ctx), storage, reports);
    }

    private static DurableTask<JsonDict, JsonDict, JsonDict> CreateProbe(
        Func<ITaskRuntime, Context, ConversationId, Task> read)
        => HarnessTestSupport.DefineTask(
            "test.context-cache-probe",
            1,
            [("read", async (task, runtime, context) =>
            {
                var conversationId = task.ConversationId;
                await read(runtime, context, conversationId);
                await runtime.CommitAsync(
                    (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
            })],
            abort: (_, runtime, context) => runtime.CommitAsync(
                (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.AbortedWith("aborted")), context));

    private static async Task RunProbeAsync(
        Fixture fixture, DurableTask<JsonDict, JsonDict, JsonDict> probe)
    {
        var id = await fixture.Root.CommitAsync(tx => tx.CreateTaskAsync(probe, null, new TaskOptions
        {
            Ownership = new TaskOwnership.ConversationOwner(),
            ConversationId = fixture.Root.Id,
        }), Ctx);
        var settled = await fixture.Harness.WaitForTaskAsync(TaskId<object?>.From(id.Value), Ctx);
        Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome?.Status);
        await fixture.Root.WaitForIdleAsync(Ctx);
    }

    private static async Task<ContextRead> ReadAsync(
        CountingStorage storage, ITaskRuntime runtime, Context context, ConversationId conversationId,
        EntryId? at = null)
    {
        var before = storage.ScannedRows;
        var view = await runtime.ContextAsync(conversationId, context, at);
        return new ContextRead(view, storage.ScannedRows - before);
    }

    private static async Task<EntryRecord> AppendMessageAsync(IConversation conversation, ChatMessage message)
        => await conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft
        {
            Kind = "message",
            Model = [message],
        }), Ctx);

    private static UserMessage User(string text, long timestamp)
        => new([new TextContent(text)], timestamp);

    private static ContextRead Dequeue(ConcurrentQueue<ContextRead> reads)
    {
        Assert.True(reads.TryDequeue(out var result));
        return result!;
    }

    private static void AssertEquivalent(ContextView expected, ContextView actual)
    {
        Assert.Equal(expected.Head?.Id, actual.Head?.Id);
        Assert.Equal(expected.Entries.Select(entry => entry.Id), actual.Entries.Select(entry => entry.Id));
        Assert.Equal(
            expected.Contributions.Select(items => Describe(items)),
            actual.Contributions.Select(items => Describe(items)));
        Assert.Equal(Describe(expected.Messages), Describe(actual.Messages));
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<ChatMessage> messages)
        => messages.Select(Describe).ToArray();

    private static string Describe(ChatMessage message) => message switch
    {
        SystemMessage => "system",
        UserMessage user => $"user:{user.Content.ToBlocks().OfType<TextContent>().FirstOrDefault()?.Text}",
        AssistantMessage assistant =>
            $"assistant:{string.Join("|", assistant.Content.OfType<TextContent>().Select(block => block.Text))}:{assistant.StopReason}",
        ToolResultMessage result => $"tool:{result.ToolCallId}:{result.IsError}",
        _ => message.GetType().Name,
    };

    private sealed class CountingStorage(IStorage inner) : IStorage
    {
        private int _scannedRows;

        public int ScannedRows => Volatile.Read(ref _scannedRows);

        public Task<Seq> CommitAsync(
            IReadOnlyList<StorageWrite> writes, Action<Seq> adopt, CancellationToken signal = default)
            => inner.CommitAsync(writes, adopt, signal);

        public Task<TId> MintIdAsync<TId>() where TId : struct => inner.MintIdAsync<TId>();

        public Task<ConversationRecord?> GetConversationAsync(ConversationId id) => inner.GetConversationAsync(id);

        public Task<Page<ConversationRecord>> ScanConversationsAsync(
            ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
            => inner.ScanConversationsAsync(query, limit, cursor);

        public Task<StoredEntry?> GetEntryAsync(EntryId id) => inner.GetEntryAsync(id);

        public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
            => inner.GetEntryAsync(conversationId, id);

        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
            => inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId);

        public async Task<Page<EntryRecord>> ScanEntriesAsync(
            EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        {
            var page = await inner.ScanEntriesAsync(query, limit, cursor).ConfigureAwait(false);
            Interlocked.Add(ref _scannedRows, page.Items.Count);
            return page;
        }

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id) => inner.GetTaskAsync(id);

        public Task<Page<TaskRecord>> ScanTasksAsync(
            TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
            => inner.ScanTasksAsync(query, limit, cursor);

        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id) => inner.GetSubmissionAsync(id);

        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
            SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
            => inner.ScanSubmissionsAsync(query, limit, cursor);

        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
            => inner.GetSubmissionByRequestAsync(conversationId, requestId);

        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
            => inner.FindDocumentAsync(address, at);

        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
            => inner.GetDocumentAsync(id, at);

        public Task<Page<DocumentRecord>> ScanDocumentsAsync(
            DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
            => inner.ScanDocumentsAsync(query, limit, cursor);

        public Task CloseAsync() => inner.CloseAsync();

        public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
            => inner.SubscribeAsync(listener, signal);

        public Task UnsubscribeAsync(Func<CommitPublication, Task> listener) => inner.UnsubscribeAsync(listener);

        public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
            => inner.AttachDocumentAsync(id, listener);
    }
}
