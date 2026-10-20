using System.Text.Json;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using AiThinkingLevels = Pi.Ai.Models.ThinkingLevels;
using CoreThinkingLevels = Pi.CodingAgent.Core.ThinkingLevels;

namespace Pi.CodingAgent.Experimental;

public sealed record ExperimentalSessionView(string Id, string Directory, string Cwd);

public sealed record ExperimentalConversationSummary(ConversationId Id, string Label, string? Title = null);

public sealed record ExperimentalModelSummary(ModelRef Reference, string Name, long ContextWindow);

public sealed record ExperimentalNotice(int Id, string Level, string Message);

public sealed record ExperimentalView
{
    public required ExperimentalSessionView Session { get; init; }

    public required ConversationView Conversation { get; init; }

    public required IReadOnlyList<ExperimentalConversationSummary> Conversations { get; init; }

    public required IReadOnlyList<ExperimentalModelSummary> Models { get; init; }

    public required IReadOnlyList<ExperimentalNotice> Notices { get; init; }

    public IReadOnlyList<TaskGraphNode>? Tasks { get; init; }
}

public interface IExperimentalViewSource
{
    ExperimentalView Current { get; }

    IDisposable Subscribe(Action listener);
}

public interface IExperimentalController
{
    Task SubmitAsync(string text, string whenBusy);

    Task CompactAsync(string? instructions);

    Task AbortAsync();

    Task CycleThinkingAsync();

    Task SetModelAsync(ModelRef model);

    Task ToggleTasksAsync();

    Task SwitchConversationAsync(ConversationId id);
}

public sealed record OpenExperimentalOptions
{
    public string? Cwd { get; init; }

    public bool ContinueSession { get; init; }

    public string? AgentDirectory { get; init; }
}

public sealed class OpenExperimentalResult : IAsyncDisposable
{
    private readonly Func<Task> _close;
    private Task? _closeTask;
    private readonly object _closeGate = new();

    internal OpenExperimentalResult(
        IExperimentalViewSource view,
        IExperimentalController controller,
        SettingsManager settings,
        Func<Task> close)
    {
        View = view;
        Controller = controller;
        Settings = settings;
        _close = close;
    }

    public IExperimentalViewSource View { get; }

    public IExperimentalController Controller { get; }

    public SettingsManager Settings { get; }

    public Task CloseAsync()
    {
        lock (_closeGate) return _closeTask ??= _close();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

internal sealed class ExperimentalViewSource(ExperimentalView initial) : IExperimentalViewSource
{
    private readonly object _gate = new();
    private readonly HashSet<Action> _listeners = [];
    private ExperimentalView _current = initial;
    private bool _notificationQueued;

    public ExperimentalView Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate) _listeners.Add(listener);
        return new Unsubscription(() =>
        {
            lock (_gate) _listeners.Remove(listener);
        });
    }

    public void Update(Func<ExperimentalView, ExperimentalView> update)
    {
        lock (_gate)
        {
            _current = update(_current);
            if (_notificationQueued) return;
            _notificationQueued = true;
        }

        ThreadPool.QueueUserWorkItem(static state => ((ExperimentalViewSource)state!).Notify(), this);
    }

    private void Notify()
    {
        Action[] listeners;
        lock (_gate)
        {
            _notificationQueued = false;
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"experimental view listener failed: {error}");
            }
        }
    }

    private sealed class Unsubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}

/// <summary>
/// 两个实验宿主之间唯一真正不同的部分。上游 <c>experimental/durable/</c> 与 <c>experimental/vacation/</c>
/// 的 <c>runtime.ts</c> 只差这几处（<c>main.ts</c> 与 <c>tui.ts</c> 逐字节相同），故此处收敛为一个值。
/// </summary>
public sealed record ExperimentalProfile
{
    /// <summary>会话目录的种类（目录名与文案）。</summary>
    public required ExperimentalSessionKind SessionKind { get; init; }

    /// <summary>往注册表里装工具与提示。</summary>
    public required Action<IRegistry, SettingsManager, string> InstallRegistry { get; init; }

    /// <summary>根对话 agent 的扩展集合；null 表示不设（沿用 pi 的默认）。</summary>
    public IReadOnlyList<IExtension>? RootExtensions { get; init; }

    /// <summary>根对话 agent 的 <c>cwd</c>；null 表示不写。</summary>
    public Func<ExperimentalSessionLocation, string?>? RootCwd { get; init; }

    /// <summary>
    /// 按已定位的会话构建执行环境与随附清理；null 表示不给（工具拿不到环境）。
    /// 上游在拿到会话目录之后才建环境（环境按目录缓存），故此处以 location 为参数。
    /// </summary>
    public Func<ExperimentalSessionLocation, ExperimentalEnvironments?>? CreateEnvironments { get; init; }

    /// <summary>写进错误文案的宿主名。</summary>
    public required string Label { get; init; }
}

/// <summary>实验宿主的执行环境工厂与它的清理。对应 TS <c>ExecutionEnvs</c> 的 <c>env</c> / <c>cleanup</c>。</summary>
public sealed record ExperimentalEnvironments(
    Func<EnvTarget, Context, Task<IExecutionEnv?>> Env,
    Func<Context, Task> Cleanup);

public static class ExperimentalRuntime
{
    private const int ScanPageSize = 256;
    private const string AgentDocumentKind = "pi.agent";
    private static readonly Context RuntimeContext = Context.Todo;

    public static async Task<OpenExperimentalResult> OpenAsync(
        ExperimentalProfile profile, OpenExperimentalOptions? options = null)
    {
        options ??= new OpenExperimentalOptions();
        var location = await ExperimentalSessions.SelectAsync(
            profile.SessionKind,
            options.Cwd ?? Environment.CurrentDirectory,
            options.ContinueSession,
            options.AgentDirectory).ConfigureAwait(false);

        HarnessImpl? harness = null;
        IStorage? storage = null;
        try
        {
            var modelRuntime = await ModelRuntime.CreateAsync().ConfigureAwait(false);
            var settingsManager = SettingsManager.Create(location.Cwd);
            HttpDispatcher.ApplyHttpProxySettings(settingsManager.GetGlobalSettings().HttpProxy);

            var registry = Registry.CreateRegistry();
            profile.InstallRegistry(registry, settingsManager, location.Cwd);
            var environments = profile.CreateEnvironments?.Invoke(location);

            var pendingReports = new List<object>();
            object reportGate = new();
            ExperimentalViewSource? viewSource = null;
            var noticeId = 0;
            void Report(object error)
            {
                lock (reportGate)
                {
                    if (viewSource is null)
                    {
                        pendingReports.Add(error);
                        return;
                    }
                }

                AddNotice(
                    viewSource,
                    ref noticeId,
                    "warning",
                    error is Exception exception ? exception.Message : error.ToString() ?? "Unknown report");
            }

            storage = await SqliteStorage.OpenAsync(location.Database).ConfigureAwait(false);
            harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = modelRuntime.DurableModels,
                Registry = registry,
                SettingsSource = () => SessionWorkerHarnessSetup.CreateHarnessSettings(settingsManager),
                EnvFactory = environments?.Env,
                OnReport = Report,
            }, RuntimeContext).ConfigureAwait(false);

            var initial = location.Created
                ? await ModelResolver.FindInitialModelAsync(new InitialModelOptions
                {
                    ScopedModels = [],
                    IsContinuing = false,
                    DefaultProvider = settingsManager.GetDefaultProvider(),
                    DefaultModelId = settingsManager.GetDefaultModel(),
                    DefaultThinkingLevel = CoreThinkingLevels.Parse(settingsManager.GetDefaultThinkingLevel()),
                    Runtime = modelRuntime,
                }).ConfigureAwait(false)
                : null;
            var root = await harness.RootAsync(RuntimeContext, new AgentChange
            {
                Extensions = profile.RootExtensions is { } exact
                    ? new AgentChange.ExtensionChange { Exact = exact }
                    : null,
                Cwd = profile.RootCwd?.Invoke(location),
                Model = initial?.Model is { } selected
                    ? new ModelRef(selected.Provider, selected.Id)
                    : null,
                ThinkingLevel = initial?.ThinkingLevel,
            }).ConfigureAwait(false);

            var conversations = await ReadConversationsAsync(harness, root.Id).ConfigureAwait(false);
            var conversationState = await root.ViewStateAsync(RuntimeContext).ConfigureAwait(false);
            var initialConversation = conversationState.Value
                ?? throw new InvalidOperationException($"The root {profile.Label} conversation has no view state");
            var openedViewSource = new ExperimentalViewSource(new ExperimentalView
            {
                Session = new ExperimentalSessionView(location.Id, location.Directory, location.Cwd),
                Conversation = initialConversation,
                Conversations = conversations,
                Models = modelRuntime.GetAvailableSnapshot()
                    .Select(model => new ExperimentalModelSummary(
                        new ModelRef(model.Provider, model.Id), model.Name, model.ContextWindow))
                    .ToList(),
                Notices = [],
            });
            lock (reportGate) viewSource = openedViewSource;
            IDisposable conversationSubscription = conversationState.Subscribe((value, _, _) =>
            {
                if (value is not null)
                    viewSource.Update(current => current with { Conversation = value });
                return null;
            });

            IDisposable commitSubscription = harness.SubscribeCommits((publication, _) =>
            {
                var updated = viewSource.Current.Conversations.ToList();
                var changed = false;
                foreach (var change in publication.Changes)
                {
                    switch (change)
                    {
                        case CommitChange.ConversationTable conversationChange:
                        {
                            var id = conversationChange.Value.Id;
                            if (updated.All(summary => summary.Id != id))
                            {
                                updated.Add(new ExperimentalConversationSummary(
                                    id,
                                    id == root.Id ? "main" : $"subagent {id}"));
                                changed = true;
                            }
                            break;
                        }
                        case CommitChange.EntryTable { Value.Kind: "pi.user" } entryChange:
                        {
                            var entry = entryChange.Value;
                            var index = updated.FindIndex(summary => summary.Id == entry.ConversationId);
                            if (index >= 0 && updated[index].Title is null)
                            {
                                updated[index] = updated[index] with { Title = TitleOf(entry) };
                                changed = true;
                            }
                            break;
                        }
                    }
                }

                if (changed)
                    viewSource.Update(current => current with { Conversations = updated });
            });

            AttachedReplicatedState<IReadOnlyDictionary<string, object?>>? taskGraph = null;
            IDisposable? taskGraphSubscription = null;
            IConversation current = root;
            AttachedReplicatedState<ConversationView> currentState = conversationState;
            IDisposable currentSubscription = conversationSubscription;
            Task commandQueue = Task.CompletedTask;

            void Fail(Exception error)
                => AddNotice(viewSource, ref noticeId, "error", error.Message);

            Task Command(Func<Task> operation)
            {
                commandQueue = commandQueue.ContinueWith(
                    async _ =>
                    {
                        try
                        {
                            await operation().ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            Fail(error);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    System.Threading.Tasks.TaskScheduler.Default).Unwrap();
                return commandQueue;
            }

            async Task ObserveSubmissionAsync(ISubmission submission)
            {
                try
                {
                    var settled = await submission.WaitAsync(RuntimeContext).ConfigureAwait(false);
                    if (settled.Record is SubmissionRecord.InputRecord
                        {
                            Status: SubmissionStatus.Unanswered,
                            Reason: not "aborted",
                        } unanswered)
                    {
                        var detail = unanswered.Detail is null
                            ? ""
                            : $" {JsonSerializer.Serialize(unanswered.Detail)}";
                        AddNotice(viewSource, ref noticeId, "error",
                            $"No answer: {unanswered.Reason}{detail}");
                    }
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }

            async Task ObserveCompactionAsync(TaskId<object?> id)
            {
                try
                {
                    var settled = await harness.WaitForTaskAsync(id, RuntimeContext).ConfigureAwait(false);
                    var outcome = settled.State.Outcome
                        ?? throw new InvalidOperationException($"Compaction task {id.Value} has no outcome");
                    if (outcome.Status == TaskOutcomeStatus.Completed)
                    {
                        var message = "Nothing to compact: the context fits in the recent window that is kept verbatim.";
                        if (outcome.Result is { } resultValue)
                        {
                            var result = JsonObject(resultValue);
                            if (result.ContainsKey("entryId"))
                            {
                                message = "Compacted.";
                            }
                            else if (result.TryGetValue("submissionId", out var submissionValue))
                            {
                                var submissionId = SubmissionId.From(Convert.ToInt64(
                                    submissionValue,
                                    System.Globalization.CultureInfo.InvariantCulture));
                                var submission = await harness.SubmissionAsync(submissionId, RuntimeContext)
                                    .ConfigureAwait(false)
                                    ?? throw new InvalidOperationException(
                                        $"Compaction submission {submissionId.Value} does not exist");
                                var submissionRecord = await submission.StatusAsync(RuntimeContext)
                                    .ConfigureAwait(false);
                                var status = submissionRecord switch
                                {
                                    SubmissionRecord.InputRecord input => input.Status,
                                    SubmissionRecord.WriteRecord write => write.Status,
                                    _ => throw new InvalidOperationException(
                                        $"Compaction submission {submissionId.Value} has an unknown type"),
                                };
                                message = status switch
                                {
                                    SubmissionStatus.Done => "Compacted.",
                                    SubmissionStatus.Queued =>
                                        "Compaction summary queued; it is placed at the next turn boundary.",
                                    SubmissionStatus.Unanswered =>
                                        "Compaction summary dropped: the context changed under it.",
                                    _ => message,
                                };
                            }
                        }

                        AddNotice(viewSource, ref noticeId, "info", message);
                    }
                    else if (outcome.Status == TaskOutcomeStatus.Aborted)
                    {
                        AddNotice(viewSource, ref noticeId, "info", "Compaction aborted.");
                    }
                    else
                    {
                        AddNotice(viewSource, ref noticeId, "error",
                            $"Compaction {outcome.Status.ToString().ToLowerInvariant()}: "
                            + $"{outcome.Error?.Message ?? outcome.Reason ?? ""}");
                    }
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }

            var controller = new ExperimentalController(
                viewSource,
                harness,
                modelRuntime,
                () => current,
                value => current = value,
                () => currentState,
                (state, subscription) =>
                {
                    currentSubscription.Dispose();
                    currentState.Dispose();
                    currentState = state;
                    currentSubscription = subscription;
                },
                () => taskGraph,
                value => taskGraph = value,
                () => taskGraphSubscription,
                value => taskGraphSubscription = value,
                Command,
                ObserveSubmissionAsync,
                ObserveCompactionAsync,
                Fail,
                RuntimeContext,
                () => (currentSubscription, commitSubscription));

            void SettlePendingReports()
            {
                object[] reports;
                lock (reportGate)
                {
                    reports = [.. pendingReports];
                    pendingReports.Clear();
                }

                foreach (var error in reports)
                {
                    AddNotice(
                        viewSource,
                        ref noticeId,
                        "warning",
                        error is Exception exception ? exception.Message : error.ToString() ?? "Unknown report");
                }
            }

            SettlePendingReports();
            var agent = AgentOf(initialConversation);
            if (agent?.Model is null)
            {
                AddNotice(viewSource, ref noticeId, "warning", "No model configured; select one with /model.");
            }
            else if (modelRuntime.GetModel(agent.Model.Provider, agent.Model.ModelId) is null)
            {
                AddNotice(viewSource, ref noticeId, "warning",
                    $"Saved model is unavailable: {agent.Model.Provider}/{agent.Model.ModelId}");
            }

            if (initial?.FallbackMessage is { } fallback)
                AddNotice(viewSource, ref noticeId, "info", fallback);

            await controller.ToggleTasksAsync().ConfigureAwait(false);
            harness.Resume();

            return new OpenExperimentalResult(
                viewSource,
                controller,
                settingsManager,
                async () =>
                {
                    var failures = new List<Exception>();
                    try
                    {
                        controller.Close();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }

                    try
                    {
                        await harness.CloseAsync(RuntimeContext).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }

                    if (environments is { } opened)
                    {
                        try
                        {
                            await opened.Cleanup(RuntimeContext).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }
                    }

                    try
                    {
                        await location.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }

                    ThrowCloseFailures(failures);
                });
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (harness is not null)
            {
                try
                {
                    await harness.CloseAsync(RuntimeContext).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    failures.Add(cleanupError);
                }
            }
            else if (storage is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    failures.Add(cleanupError);
                }
            }

            try
            {
                await location.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                failures.Add(cleanupError);
            }

            if (failures.Count == 1) throw;
            throw new AggregateException($"{profile.Label} startup and cleanup failed", failures);
        }
    }

    private static async Task<IReadOnlyList<ExperimentalConversationSummary>> ReadConversationsAsync(
        HarnessImpl harness, ConversationId rootId)
    {
        var summaries = new List<ExperimentalConversationSummary>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await harness.CommitAsync(
                tx => tx.ScanConversationsAsync(new ConversationQuery(), ScanPageSize, cursor),
                RuntimeContext).ConfigureAwait(false);
            foreach (var record in page.Items)
            {
                var title = await FirstInputAsync(harness, record.Id).ConfigureAwait(false);
                summaries.Add(new ExperimentalConversationSummary(
                    record.Id,
                    record.Id == rootId ? "main" : $"subagent {record.Id}",
                    title));
            }

            cursor = page.Next;
        } while (cursor is not null);

        return summaries;
    }

    private static async Task<string?> FirstInputAsync(HarnessImpl harness, ConversationId id)
    {
        if (id == DurableIdConstants.RootConversation) return null;
        var conversation = await harness.ConversationAsync(id, RuntimeContext).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {id.Value} does not exist");
        EntryRecord? first = null;
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await conversation.EntriesAsync(
                new EntryQuery { ConversationId = id }, ScanPageSize, cursor, RuntimeContext).ConfigureAwait(false);
            first = page.Items.LastOrDefault(entry => entry.Kind == "pi.user") ?? first;
            cursor = page.Next;
        } while (cursor is not null);

        return TitleOf(first);
    }

    private static string? TitleOf(EntryRecord? entry)
    {
        var message = entry?.Model?.FirstOrDefault();
        if (message is not UserMessage user) return null;
        var text = user.Content switch
        {
            MessageContent.Text textContent => textContent.Value,
            MessageContent.Blocks blocks => string.Join(
                " ",
                blocks.Value.OfType<TextContent>().Select(block => block.Text)),
            _ => "",
        };
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static IReadOnlyDictionary<string, object?> JsonObject(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> dictionary) return dictionary;
        if (value is JsonElement element
            && JsonTrees.FromElement(element) is IReadOnlyDictionary<string, object?> converted)
        {
            return converted;
        }
        throw new InvalidOperationException("Compaction task result must be a JSON object");
    }

    public static AgentState? AgentOf(ConversationView view)
        => view.Docs.TryGetValue(AgentDocumentKind, out var agent) ? AgentDocs.StateFromJson(agent) : null;

    private static void AddNotice(
        ExperimentalViewSource source, ref int nextId, string level, string message)
    {
        var id = Interlocked.Increment(ref nextId);
        source.Update(current => current with
        {
            Notices = current.Notices.Append(new ExperimentalNotice(id, level, message)).TakeLast(20).ToList(),
        });
    }

    private static void ThrowCloseFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("experimental runtime and session cleanup failed", failures);
    }

    private sealed class ExperimentalController(
        ExperimentalViewSource view,
        HarnessImpl harness,
        ModelRuntime models,
        Func<IConversation> getCurrent,
        Action<IConversation> setCurrent,
        Func<AttachedReplicatedState<ConversationView>> getCurrentState,
        Action<AttachedReplicatedState<ConversationView>, IDisposable> switchState,
        Func<AttachedReplicatedState<IReadOnlyDictionary<string, object?>>?> getTaskGraph,
        Action<AttachedReplicatedState<IReadOnlyDictionary<string, object?>>?> setTaskGraph,
        Func<IDisposable?> getTaskGraphSubscription,
        Action<IDisposable?> setTaskGraphSubscription,
        Func<Func<Task>, Task> command,
        Func<ISubmission, Task> observeSubmission,
        Func<TaskId<object?>, Task> observeCompaction,
        Action<Exception> fail,
        Context context,
        Func<(IDisposable Conversation, IDisposable Commits)> getSubscriptions) : IExperimentalController
    {
        private bool _closed;

        public Task SubmitAsync(string text, string whenBusy)
            => command(async () =>
            {
                var submission = await getCurrent().SubmitAsync(new SubmissionDraft.Input
                {
                    Content = [new TextContent(text)],
                    WhenBusy = whenBusy,
                }, context).ConfigureAwait(false);
                _ = observeSubmission(submission);
            });

        public Task CompactAsync(string? instructions)
            => command(async () =>
            {
                var id = await getCurrent().CompactAsync(instructions, context).ConfigureAwait(false);
                _ = observeCompaction(TaskId<object?>.From(id.Value));
            });

        public async Task AbortAsync()
        {
            try
            {
                await getCurrent().AbortAsync(context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                fail(error);
            }
        }

        public Task CycleThinkingAsync()
            => command(async () =>
            {
                var agent = AgentOf(view.Current.Conversation);
                var reference = agent?.Model
                    ?? throw new InvalidOperationException("No model selected");
                var model = models.GetModel(reference.Provider, reference.ModelId)
                    ?? throw new InvalidOperationException("Current model is unavailable");
                if (!model.Reasoning)
                    throw new InvalidOperationException("Current model does not support thinking");
                var levels = AiThinkingLevels.GetSupported(model);
                var current = Pi.CodingAgent.Core.ThinkingLevels.ToText(
                    agent?.ThinkingLevel ?? ThinkingLevel.Off);
                var index = Array.FindIndex(levels.ToArray(), level => level == current);
                var next = levels[(index + 1 + levels.Count) % levels.Count];
                var level = Pi.CodingAgent.Core.ThinkingLevels.Parse(next)
                    ?? throw new InvalidOperationException($"Unknown thinking level: {next}");
                await getCurrent().ConfigureAsync(new AgentChange { ThinkingLevel = level }, context)
                    .ConfigureAwait(false);
            });

        public Task SetModelAsync(ModelRef modelReference)
            => command(async () =>
            {
                var model = models.GetModel(modelReference.Provider, modelReference.ModelId)
                    ?? throw new InvalidOperationException(
                        $"Unknown model: {modelReference.Provider}/{modelReference.ModelId}");
                var agent = AgentOf(view.Current.Conversation);
                var level = Pi.CodingAgent.Core.ThinkingLevels.ToText(
                    agent?.ThinkingLevel ?? ThinkingLevel.Off);
                var clamped = AiThinkingLevels.Clamp(model, level);
                var thinking = Pi.CodingAgent.Core.ThinkingLevels.Parse(clamped)
                    ?? throw new InvalidOperationException($"Unknown thinking level: {clamped}");
                await getCurrent().ConfigureAsync(new AgentChange
                {
                    Model = modelReference,
                    ThinkingLevel = thinking,
                }, context).ConfigureAwait(false);
            });

        public Task ToggleTasksAsync()
            => command(async () =>
            {
                var graph = getTaskGraph();
                if (graph is not null)
                {
                    getTaskGraphSubscription()?.Dispose();
                    setTaskGraphSubscription(null);
                    graph.Dispose();
                    setTaskGraph(null);
                    view.Update(current => current with { Tasks = null });
                    return;
                }

                var opened = await harness.TaskGraphAsync(context).ConfigureAwait(false);
                setTaskGraph(opened);
                setTaskGraphSubscription(opened.Subscribe((value, _, _) =>
                {
                    if (value is not null)
                        view.Update(current => current with { Tasks = TaskGraphReader.Nodes(value) });
                    return null;
                }));
                view.Update(current => current with
                {
                    Tasks = TaskGraphReader.Nodes(opened.Value
                        ?? throw new InvalidOperationException("Task graph has no state")),
                });
            });

        public Task SwitchConversationAsync(ConversationId id)
            => command(async () =>
            {
                var next = await harness.ConversationAsync(id, context).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Conversation {id.Value} does not exist");
                var nextState = await next.ViewStateAsync(context).ConfigureAwait(false);
                var nextValue = nextState.Value
                    ?? throw new InvalidOperationException($"Conversation {id.Value} has no view state");
                var subscription = nextState.Subscribe((value, _, _) =>
                {
                    if (value is not null)
                        view.Update(current => current with { Conversation = value });
                    return null;
                });
                switchState(nextState, subscription);
                setCurrent(next);
                view.Update(current => current with { Conversation = nextValue });
            });

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            var (conversation, commits) = getSubscriptions();
            var failures = new List<Exception>();
            Dispose(conversation, failures);
            Dispose(commits, failures);
            if (getTaskGraphSubscription() is { } graphSubscription)
                Dispose(graphSubscription, failures);
            if (getTaskGraph() is { } graph) Dispose(graph, failures);
            Dispose(getCurrentState(), failures);
            ThrowCloseFailures(failures);

            static void Dispose(IDisposable resource, List<Exception> failures)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

        }
    }
}
