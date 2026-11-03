using Pi.Ai.Models;
using Pi.Durable.Env;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;
using Path = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>pi.generation 任务的检查点（JSON 字典承载，与 TS 形状一致）。</summary>
internal abstract record GenerationCheckpoint
{
    public abstract JsonDict ToJson();

    public static GenerationCheckpoint FromJson(JsonDict json) => json["phase"] switch
    {
        "prepare" => new Prepare(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted) && compacted is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted))
                : null,
            json.TryGetValue("overflow", out var overflow) ? overflow as string : null),
        "request" => new Request(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted2) && compacted2 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted2))
                : null,
            new ModelRef((string)((JsonDict)json["model"]!)["provider"]!, (string)((JsonDict)json["model"]!)["modelId"]!),
            ThinkingLevelOf((string)json["thinkingLevel"]!),
            json.TryGetValue("streamOptions", out var stream) && stream is not null
                ? JsonTrees.Deserialize<ConversationStreamOptions>(stream)
                : new ConversationStreamOptions(),
            EntryId.From(Convert.ToInt64(json["cutoff"]!))),
        "retry" => new RetryPhase(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted3) && compacted3 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted3))
                : null,
            Convert.ToInt64(json["until"]!),
            json.TryGetValue("poll", out var retryPoll) && retryPoll is JsonDict pollJson
                ? PollRequestFromJson(pollJson)
                : null),
        "poll" => new Poll(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted4) && compacted4 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted4))
                : null,
            new ModelRef((string)((JsonDict)json["model"]!)["provider"]!, (string)((JsonDict)json["model"]!)["modelId"]!),
            EntryId.From(Convert.ToInt64(json["cutoff"]!)),
            JsonTrees.Deserialize<DeferredHandle>(json["handle"]!),
            Convert.ToInt64(json["pollAt"]!),
            json.TryGetValue("thinkingLevel", out var pollThinking) && pollThinking is string pollLevel
                ? ThinkingLevelOf(pollLevel)
                : null),
        "finish" => new Finish(new RunSummary
        {
            Reason = (string)((JsonDict)json["run"]!)["reason"]!,
            Answer = EntryId.From(Convert.ToInt64(((JsonDict)json["run"]!)["answer"]!)),
            First = EntryId.From(Convert.ToInt64(((JsonDict)json["run"]!)["first"]!)),
            Last = EntryId.From(Convert.ToInt64(((JsonDict)json["run"]!)["last"]!)),
        }),
        "drain" => new Drain(
            EntryId.From(Convert.ToInt64(json["answer"]!)),
            json.TryGetValue("tasks", out var drainTasks) && drainTasks is IReadOnlyList<object?> drainList
                ? drainList.Select(value => TaskId<object?>.From(Convert.ToInt64(value))).ToList()
                : []),
        "tools" => new Tools(
            EntryId.From(Convert.ToInt64(json["assistant"]!)),
            json.TryGetValue("tools", out var tools) && tools is IReadOnlyList<object?> toolList
                ? toolList.Select(value => TaskId<object?>.From(Convert.ToInt64(value))).ToList()
                : [],
            json.TryGetValue("pending", out var pending) && pending is IReadOnlyList<object?> pendingList
                ? pendingList.OfType<string>().ToList()
                : []),
        var other => throw new InvalidOperationException($"Unknown pi.generation checkpoint phase {other}"),
    };

    private static PollRequest PollRequestFromJson(JsonDict json)
        => new(
            new ModelRef((string)((JsonDict)json["model"]!)["provider"]!, (string)((JsonDict)json["model"]!)["modelId"]!),
            json.TryGetValue("thinkingLevel", out var thinkingLevel) && thinkingLevel is string level
                ? ThinkingLevelOf(level)
                : null,
            EntryId.From(Convert.ToInt64(json["cutoff"]!)),
            JsonTrees.Deserialize<DeferredHandle>(json["handle"]!));

    protected static JsonDict ModelRefJson(ModelRef model)
        => new Dictionary<string, object?> { ["provider"] = model.Provider, ["modelId"] = model.ModelId };

    protected static string ThinkingLevelJson(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static ThinkingLevel ThinkingLevelOf(string level) => level switch
    {
        "off" => ThinkingLevel.Off,
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "xhigh" => ThinkingLevel.XHigh,
        "max" => ThinkingLevel.Max,
        var other => throw new InvalidOperationException($"Unknown thinking level {other}"),
    };

    public sealed record Prepare(int Attempt, TaskId<object?>? Compacted, string? Overflow) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?> { ["phase"] = "prepare", ["attempt"] = (long)Attempt };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            if (Overflow is not null) json["overflow"] = Overflow;
            return json;
        }
    }

    public sealed record Request(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, ThinkingLevel ThinkingLevel,
        ConversationStreamOptions StreamOptions, EntryId Cutoff) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "request",
                ["attempt"] = (long)Attempt,
                ["model"] = ModelRefJson(Model),
                ["thinkingLevel"] = ThinkingLevelJson(ThinkingLevel),
                ["streamOptions"] = JsonTrees.ToTree(StreamOptions),
                ["cutoff"] = Cutoff.Value,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            return json;
        }
    }

    /// <summary>
    /// 退避的睡眠。<see cref="Polling"/> 非空时，宿主是在轮询这次取回的延迟应答时死的：下一次尝试再轮询同一个应答，
    /// 既不丢掉提供方的工作也不重复付费。对应 TS <c>retry.poll</c>。
    /// </summary>
    public sealed record RetryPhase(
        int Attempt, TaskId<object?>? Compacted, long Until, PollRequest? Polling = null) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "retry", ["attempt"] = (long)Attempt, ["until"] = Until,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            if (Polling is { } poll) json["poll"] = poll.ToJson();
            return json;
        }
    }

    /// <summary>一次要再轮询的延迟应答（<c>retry.poll</c> 的四个字段）。对应 TS <c>GenerationCheckpoint["retry"]["poll"]</c>。</summary>
    public sealed record PollRequest(ModelRef Model, ThinkingLevel? ThinkingLevel, EntryId Cutoff, DeferredHandle Handle)
    {
        public JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["model"] = ModelRefJson(Model),
                ["cutoff"] = Cutoff.Value,
                ["handle"] = JsonTrees.ToTree(Handle),
            };
            if (ThinkingLevel is { } thinkingLevel) json["thinkingLevel"] = ThinkingLevelJson(thinkingLevel);
            return json;
        }
    }

    public sealed record Poll(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, EntryId Cutoff,
        DeferredHandle Handle, long PollAt, ThinkingLevel? ThinkingLevel = null) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "poll",
                ["attempt"] = (long)Attempt,
                ["model"] = ModelRefJson(Model),
                ["cutoff"] = Cutoff.Value,
                ["handle"] = JsonTrees.ToTree(Handle),
                ["pollAt"] = PollAt,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            // 更早版本的检查点没有这一项。
            if (ThinkingLevel is { } thinkingLevel) json["thinkingLevel"] = ThinkingLevelJson(thinkingLevel);
            return json;
        }
    }

    /// <summary>
    /// 运行已回答：答案已提交、输入已结算，运行仍持有 <c>pi.live.run</c>，等 <c>afterRun</c> 钩子要任务（spec §8.3）。
    /// 在这一阶段崩溃会把钩子再问一次（什么都还没创建）。对应 TS <c>finish</c>。
    /// </summary>
    public sealed record Finish(RunSummary Run) : GenerationCheckpoint
    {
        public override JsonDict ToJson() => new Dictionary<string, object?>
        {
            ["phase"] = "finish",
            ["run"] = new Dictionary<string, object?>
            {
                ["reason"] = Run.Reason,
                ["answer"] = Run.Answer.Value,
                ["first"] = Run.First.Value,
                ["last"] = Run.Last.Value,
            },
        };
    }

    /// <summary>等待 <c>afterRun</c> 钩子要来的任务（生成拥有它们）；它们终态后运行结束。对应 TS <c>drain</c>。</summary>
    public sealed record Drain(EntryId Answer, IReadOnlyList<TaskId<object?>> Tasks) : GenerationCheckpoint
    {
        public override JsonDict ToJson() => new Dictionary<string, object?>
        {
            ["phase"] = "drain",
            ["answer"] = Answer.Value,
            ["tasks"] = Tasks.Select(task => (object?)task.Value).ToList(),
        };
    }

    /// <summary>等待本轮的工具任务；生成拥有它们（spec §8.5）。</summary>
    public sealed record Tools(EntryId Assistant, IReadOnlyList<TaskId<object?>> ToolsIds, IReadOnlyList<string> Pending)
        : GenerationCheckpoint
    {
        public override JsonDict ToJson() => new Dictionary<string, object?>
        {
            ["phase"] = "tools",
            ["assistant"] = Assistant.Value,
            ["tools"] = ToolsIds.Select(id => (object?)id.Value).ToList(),
            ["pending"] = Pending.ToList(),
        };
    }
}

/// <summary>
/// 内建生成任务：准备位置系统提示与工具负载，请求或轮询模型，重试，并分类响应。
/// 运行的输入在 <c>pi.live.run</c>。对应 TS <c>harness/generation.ts</c> 的 <c>GenerationTask</c> 全量。
/// </summary>
public static partial class Generation
{
    public const string TaskName = "pi.generation";

    private const long DefaultPollAfterMs = 5000;

    /// <summary>擦除形状的内建任务定义（输入 / checkpoint / 结果均为 JSON 字典）。</summary>
    public static readonly DurableTask<JsonDict, JsonDict, JsonDict> Instance = DurableTasks.DefineTask(
        new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = TaskName,
            // Version 2 把「请求未提供的调用」留在顺序轮的 pending 里并在槽上标 unoffered；旧版本 checkpoint
            // 没有这样的调用，原样迁移。
            Version = 2,
            Migrate = new ErasedMigrate((input, checkpoint, _) => (input, checkpoint)),
            Initial = _ => new GenerationCheckpoint.Prepare(1, null, null).ToJson(),
            Phases = new Dictionary<string, object?>
            {
                ["prepare"] = (TaskPhaseHandler)PreparePhaseAsync,
                ["request"] = (TaskPhaseHandler)RequestPhaseAsync,
                ["retry"] = (TaskPhaseHandler)RetryPhaseAsync,
                ["poll"] = (TaskPhaseHandler)PollPhaseAsync,
                ["tools"] = (TaskPhaseHandler)ToolsPhaseAsync,
                ["finish"] = (TaskPhaseHandler)FinishPhaseAsync,
                ["drain"] = (TaskPhaseHandler)DrainPhaseAsync,
            },
            Abort = (TaskPhaseHandler)AbortAsync,
        });

    // ─── 阶段 ───────────────────────────────────────────────────────────────

    /// <summary>渲染系统提示与工具负载并追加所需的位置 <c>pi.system</c> 条目，随后进入 request。</summary>
    private static async Task PreparePhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }

        var conversationId = runtime.ConversationId;
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var settings = runtime.Settings;
        var modelReference = agent.Model;
        var resolved = modelReference is null
            ? null
            : runtime.Models.GetModel(modelReference.Provider, modelReference.ModelId);
        if (modelReference is null || resolved is null)
        {
            await FailNoModelAsync(runtime, modelReference, context).ConfigureAwait(false);
            return;
        }

        var checkpoint = (GenerationCheckpoint.Prepare)ReadCheckpoint(task);
        if (checkpoint.Compacted is { } compacted && checkpoint.Overflow is { } overflow)
        {
            var outcome = (await runtime.OutcomesAsync([compacted], context).ConfigureAwait(false))[0];
            var entryId = outcome is { Status: TaskOutcomeStatus.Completed, Result: JsonDict result }
                && result.TryGetValue("entryId", out var value) && value is not null
                    ? Convert.ToInt64(value)
                    : default(long?);
            if (outcome.Status != TaskOutcomeStatus.Completed || entryId is null)
            {
                await FailModelErrorAsync(runtime, overflow, context).ConfigureAwait(false);
                return;
            }
        }

        var view = await runtime.ContextAsync(conversationId, context).ConfigureAwait(false);
        // 模型只对以用户消息或工具结果结尾的上下文有应答义务；系统消息不算。否则（空的，或以上一次回答结尾，
        // 例如回答之后的 continue）在发出任何请求之前就结束运行。对应 TS 的 failNothingToAnswer()。
        var lastAnswerable = view.Messages.LastOrDefault(message => message is not SystemMessage);
        if (lastAnswerable is not (UserMessage or ToolResultMessage))
        {
            await FailNothingToAnswerAsync(runtime, context).ConfigureAwait(false);
            return;
        }

        var shown = Prompt.ReplaySections(view.Messages);
        var report = new Action<Exception>(error => runtime.Report(error));
        IExecutionEnv? env = null;
        try
        {
            env = await runtime.EnvAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (context.AbortSignal is { IsCancellationRequested: true }) throw;
            report(error);
        }

        var input = new PromptInput
        {
            ConversationId = conversationId,
            Agent = agent,
            Env = env,
            Shown = shown,
            Read = runtime,
        };
        var desired = await Prompt.RenderSections(agent.Sections, input, shown, report, context).ConfigureAwait(false);
        var declarations = agent.Tools.Select(Prompt.DeclarationOf).ToList();
        var entries = Prompt.PlanSystemEntries(view, desired, declarations, runtime.Now());
        var threshold = checkpoint.Compacted is null
            ? ThresholdCompaction(view, entries, resolved.ContextWindow, settings.Compaction.ForModel(modelReference))
            : null;
        if (threshold == "blocking")
        {
            // 先压缩再重新准备；transcript 在压缩追加之前不变。
            await runtime.CommitAsync(async (tx, _) =>
            {
                var child = await Compaction.CreateCompactionAsync(
                    tx, conversationId, CompactionReason.Threshold, runtime.TaskId).ConfigureAwait(false);
                return new TaskState
                {
                    Status = TaskStatus.Waiting,
                    Checkpoint = new GenerationCheckpoint.Prepare(checkpoint.Attempt, child, null).ToJson(),
                    On = [child],
                    Policy = JoinPolicy.AllSettled,
                };
            }, context).ConfigureAwait(false);
            return;
        }

        await runtime.CommitAsync(async (tx, _) =>
        {
            EntryId? cutoff = (await tx.ScanEntriesAsync(
                new EntryQuery { ConversationId = conversationId }, 1).ConfigureAwait(false))
                .Items.FirstOrDefault()?.Id;
            foreach (var entry in entries)
            {
                cutoff = (await tx.AppendEntryAsync(
                    DurableEntries.SystemEntry, conversationId,
                    new TypedEntryDraft<object?> { Model = entry.Model, Edits = entry.Edits }).ConfigureAwait(false)).Id;
            }

            if (cutoff is null)
            {
                throw new InvalidOperationException($"Conversation {conversationId.Value} has no entries to send");
            }

            // 在本提交里检查，使准备期间受理的压缩也计入。
            if (threshold == "background")
            {
                var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                if (!live.Draft.ContainsKey("compactions"))
                {
                    await Compaction.CreateCompactionAsync(tx, conversationId, CompactionReason.Threshold)
                        .ConfigureAwait(false);
                }
            }

            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new GenerationCheckpoint.Request(
                    checkpoint.Attempt, checkpoint.Compacted, modelReference, agent.ThinkingLevel,
                    settings.Stream, cutoff.Value).ToJson(),
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task RequestPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Request)ReadCheckpoint(task);
        // 记账本身可能正是反复失败的那一步（例如转换一个巨大的 partial）：由上限收场。
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }

        // 宿主是在这次请求途中停下的：记一次失败的尝试，而不是重发（spec §8.3）。
        if (task.Restarts is not null)
        {
            await InterruptedAsync(runtime, checkpoint, context).ConfigureAwait(false);
            return;
        }

        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            Live.SetGeneration(live, new Dictionary<string, object?> { ["attempt"] = (long)checkpoint.Attempt });
            return null;
        }, context).ConfigureAwait(false);
        var model = runtime.Models.GetModel(checkpoint.Model.Provider, checkpoint.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, checkpoint.Model, context).ConfigureAwait(false);
            return;
        }

        var view = await runtime.ContextAsync(conversationId, context, checkpoint.Cutoff).ConfigureAwait(false);
        var messages = view.Messages;
        await runtime.Hooks.EachAsync("beforeRequest", async handler =>
        {
            if (handler is not IGenerationHooks hooks) return;
            var replaced = await hooks.BeforeRequest(messages, runtime, context).ConfigureAwait(false);
            if (replaced is { Used: true, Messages: { } replacedMessages }) messages = replacedMessages;
        }).ConfigureAwait(false);
        var options = await Provider.RequestOptionsAsync(
            runtime, checkpoint.StreamOptions, checkpoint.ThinkingLevel).ConfigureAwait(false);
        // 让 beforeProviderRequest 能在请求体发出去之前替换它。
        options["onPayload"] = new Func<System.Text.Json.Nodes.JsonObject, ModelSpec, Task<System.Text.Json.Nodes.JsonObject?>>(
            (payload, requestModel) => ProviderPayloadAsync(runtime, requestModel, payload, context));
        var message = await StreamResponseAsync(runtime, model, messages, options, checkpoint.Attempt, context)
            .ConfigureAwait(false);
        var request = new RequestInfo(
            checkpoint.Attempt, checkpoint.Compacted, checkpoint.Model, checkpoint.Cutoff, view.Messages,
            ThinkingLevel: checkpoint.ThinkingLevel);
        await ClassifyAsync(runtime, request, message, context).ConfigureAwait(false);
    }

    /// <summary>退避睡眠之后；在 <c>until</c> 之前运行（提前唤醒，或旧版本记录）就再睡一次。</summary>
    private static async Task RetryPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.RetryPhase)ReadCheckpoint(task);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }

        if (checkpoint.Until > runtime.Now())
        {
            await runtime.SleepUntilAsync(checkpoint.Until, task.State.Checkpoint, context).ConfigureAwait(false);
            return;
        }

        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var attempt = checkpoint.Attempt + 1;
            // 宿主死在轮询途中的那次，下一次尝试再轮询同一个应答。
            if (checkpoint.Polling is { } poll)
            {
                var pollAt = runtime.Now();
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)attempt,
                    ["deferred"] = new Dictionary<string, object?> { ["pollAt"] = pollAt },
                });
                return new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = new GenerationCheckpoint.Poll(
                        attempt, checkpoint.Compacted, poll.Model, poll.Cutoff, poll.Handle, pollAt, poll.ThinkingLevel)
                        .ToJson(),
                };
            }

            Live.SetGeneration(live, new Dictionary<string, object?> { ["attempt"] = (long)attempt });
            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new GenerationCheckpoint.Prepare(attempt, checkpoint.Compacted, null).ToJson(),
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>睡到 <c>pollAt</c> 之后；在它之前运行（提前唤醒，或旧版本记录）就再睡一次。</summary>
    private static async Task PollPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Poll)ReadCheckpoint(task);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }

        // 宿主是在轮询的取回途中停下的：与请求一样记一次失败尝试。在时间之前判断，因为替身上时钟可能后退；
        // 睡着的轮询从不被切断。
        if (task.Restarts is not null)
        {
            await InterruptedAsync(runtime, checkpoint, context).ConfigureAwait(false);
            return;
        }

        var model = runtime.Models.GetModel(checkpoint.Model.Provider, checkpoint.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, checkpoint.Model, context).ConfigureAwait(false);
            return;
        }

        if (checkpoint.PollAt > runtime.Now())
        {
            await runtime.SleepUntilAsync(checkpoint.PollAt, task.State.Checkpoint, context).ConfigureAwait(false);
            return;
        }

        var message = await runtime.Models.FetchDeferredAsync(model, checkpoint.Handle, null, runtime.Signal)
            .ConfigureAwait(false);
        var request = new RequestInfo(
            checkpoint.Attempt, checkpoint.Compacted, checkpoint.Model, checkpoint.Cutoff, null, checkpoint.PollAt,
            checkpoint.ThinkingLevel);
        await ClassifyAsync(runtime, request, message, context).ConfigureAwait(false);
    }

    private static async Task ToolsPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, context).ConfigureAwait(false);
            return;
        }

        var checkpoint = (GenerationCheckpoint.Tools)ReadCheckpoint(task);
        var next = checkpoint.Pending.FirstOrDefault();
        if (next is null)
        {
            await FinishToolRoundAsync(runtime, checkpoint.Assistant, checkpoint.ToolsIds, context).ConfigureAwait(false);
            return;
        }

        // 顺序轮：启动下一个调用并等待它。
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var slot = Live.ToolSlotsOf(live).FirstOrDefault(
                candidate => candidate.CallId == next && candidate.TaskId is null);
            var taskId = await CreateToolTaskAsync(
                tx, runtime, checkpoint.Assistant, next, slot?.Unoffered).ConfigureAwait(false);
            if (slot is not null) Live.ReplaceSlot(live, slot with { TaskId = taskId });
            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new GenerationCheckpoint.Tools(
                    checkpoint.Assistant,
                    [.. checkpoint.ToolsIds, taskId],
                    checkpoint.Pending.Skip(1).ToList()).ToJson(),
                On = [taskId],
                Policy = JoinPolicy.AllSettled,
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task AbortAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = ReadCheckpoint(task);
        if (DeferredOf(checkpoint) is { } polled) await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);

        var conversationId = runtime.ConversationId;
        // 在本轮的工具任务都终态之后运行；从未启动的调用得到 aborted 结果（spec §8.5）。
        var unstarted = checkpoint is GenerationCheckpoint.Tools tools
            ? await ReadCallsAsync(runtime, tools.Assistant, tools.Pending, context).ConfigureAwait(false)
            : Array.Empty<ToolCallContent>();
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            foreach (var call in unstarted)
            {
                var result = ToolTask.HarnessError("aborted", $"Tool {call.Name} was aborted");
                await ToolTask.AppendToolResultAsync(tx, conversationId, call, result, runtime.Now())
                    .ConfigureAwait(false);
            }

            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("aborted"));
            return new TaskState
            {
                Status = TaskStatus.Terminal, Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 分类与响应 ─────────────────────────────────────────────────────────

    private readonly record struct RequestInfo(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, EntryId Cutoff,
        IReadOnlyList<ChatMessage>? Messages, long? PollAt = null, ThinkingLevel? ThinkingLevel = null);

    /// <summary>assistant 条目中 <paramref name="callIds"/> 的调用，按给定顺序。对应 TS <c>readCalls()</c>。</summary>
    private static async Task<IReadOnlyList<ToolCallContent>> ReadCallsAsync(
        ITaskRuntime runtime, EntryId assistant, IReadOnlyList<string> callIds, Context context)
    {
        var entry = await runtime.EntryAsync(DurableEntries.AssistantEntry, assistant, context).ConfigureAwait(false);
        var message = entry is { Model.Count: > 0 } typed ? typed.Model[0] : null;
        var calls = message is AssistantMessage assistantMessage
            ? assistantMessage.Content.OfType<ToolCallContent>().ToList()
            : [];
        return [.. callIds.SelectMany(id => calls.Where(call => call.Id == id).Take(1))];
    }

    /// <summary>调用 <paramref name="callId"/> 的工具任务，由生成拥有。对应 TS <c>createToolTask()</c>。</summary>
    private static async Task<TaskId<object?>> CreateToolTaskAsync(
        Transaction tx, ITaskRuntime runtime, EntryId assistant, string callId, bool? unoffered = null)
    {
        var input = new ToolTaskInput(assistant, callId, unoffered).ToJson();
        var taskId = await tx.CreateTaskAsync(
            ToolTask.Instance,
            input,
            new TaskOptions
            {
                Ownership = new TaskOwnership.TaskOwner(runtime.TaskId),
                ConversationId = runtime.ConversationId,
            }).ConfigureAwait(false);
        return TaskId<object?>.From(taskId.Value);
    }

    /// <summary>
    /// 准备在请求前启动哪种阈值压缩（spec §8.3）：超过 <c>contextWindow - reserveTokens</c> 为 blocking，
    /// 超过后台阈值为 background，且仅当范围选择找到切点。调用方只在没有列出压缩时才启动后台压缩。
    /// 对应 TS <c>thresholdCompaction()</c>。
    /// </summary>
    internal static string? ThresholdCompaction(
        ContextView view, IReadOnlyList<TypedEntryDraft<object?>> planned, long contextWindow, CompactionPolicy policy)
    {
        if (!policy.Enabled || contextWindow <= 0) return null;
        var tokens = Compaction.EstimateContext(
            view, planned.SelectMany(entry => entry.Model ?? []).ToList());
        var blocking = contextWindow - policy.ReserveTokens;
        var background = blocking - policy.BackgroundTokens;
        var over = tokens > blocking ? "blocking"
            : policy.BackgroundTokens > 0 && tokens > background ? "background"
            : null;
        if (over is null || Compaction.SelectCut(view, policy.KeepRecentTokens) is null) return null;
        return over;
    }

    /// <summary>失败运行的结束所需要从表里读到的内容。对应 TS <c>Failure</c>。</summary>
    private sealed record FailurePolicy(AfterFailedRun Policy, Inbox.Boundary? Boundary);

    /// <summary>
    /// 读取失败运行所需的策略，以及（<c>continue</c> 时）放置排队项用的边界。表读必须先于提交的首次表写，
    /// 因此调用方在其提交开始处调用它。对应 TS <c>prepareFailure()</c>。
    /// </summary>
    private static async Task<FailurePolicy> PrepareFailureAsync(Transaction tx, ITaskRuntime runtime)
    {
        var settings = runtime.Settings;
        if (settings.AfterFailedRun != AfterFailedRun.Continue) return new FailurePolicy(settings.AfterFailedRun, null);
        return new FailurePolicy(
            settings.AfterFailedRun,
            await Inbox.PrepareBoundaryAsync(
                tx, runtime.ConversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false));
    }

    /// <summary>
    /// 以 <paramref name="settlement"/> 结束失败的运行，并按 <c>settings.afterFailedRun</c> 处理其后排队的输入
    /// （spec §6）：留着、用它们启动下一个运行，或以 <c>run_failed</c> 撤回。对应 TS <c>endFailedRun()</c>。
    /// </summary>
    private static async Task EndFailedRunAsync(
        Transaction tx, ITaskRuntime runtime, TxDocChange live, FailurePolicy failure,
        SubmissionSettlement settlement)
    {
        var conversationId = runtime.ConversationId;
        Live.EndRun(tx, live, runtime.TaskId, settlement);
        if (failure.Policy == AfterFailedRun.Withdraw)
        {
            await Inbox.WithdrawQueuedInputsAsync(tx, conversationId, "run_failed").ConfigureAwait(false);
        }

        if (failure.Boundary is null) return;
        var applied = await Inbox.ApplyBoundaryAsync(tx, failure.Boundary, BoundaryAt.Final, runtime.Now())
            .ConfigureAwait(false);
        if (applied.Users.Count > 0)
        {
            await StartRunAsync(tx, conversationId, live, applied.Users, keepFailure: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 不发请求就结束运行：上下文里没有模型会回答的东西。与失败同路（<c>afterFailedRun</c> 决定排队项），
    /// 结算为 <c>nothing_to_answer</c>。对应 TS <c>failNothingToAnswer()</c>。
    /// </summary>
    private static async Task FailNothingToAnswerAsync(ITaskRuntime runtime, Context context)
    {
        const string text = "The conversation has nothing to answer";
        await runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(
                tx, runtime, live, failure,
                new SubmissionSettlement.Unanswered(RunFailureReasons.NothingToAnswer, text)).ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = text,
                        Detail = new Dictionary<string, object?>
                        {
                            ["reason"] = RunFailureReasons.NothingToAnswer,
                        },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 宿主是在这次尝试的请求或轮询途中停下的（<see cref="TaskRecord.Restarts"/>）：按 <c>settings.retry</c> 记一次
    /// 失败的尝试（spec §8.3）。已提交的 partial 变成 aborted 条目；下一次尝试按退避进行——轮询的那次再轮询同一个
    /// 延迟应答——或者在没有重试可用了时让运行以 <c>interrupted</c> 结束。
    /// </summary>
    private static async Task InterruptedAsync(
        ITaskRuntime runtime, GenerationCheckpoint checkpoint, Context context)
    {
        var (attempt, compacted, polled) = checkpoint switch
        {
            GenerationCheckpoint.Request request => (request.Attempt, request.Compacted, (GenerationCheckpoint.PollRequest?)null),
            GenerationCheckpoint.Poll poll => (
                poll.Attempt,
                poll.Compacted,
                new GenerationCheckpoint.PollRequest(poll.Model, poll.ThinkingLevel, poll.Cutoff, poll.Handle)),
            _ => throw new InvalidOperationException(
                $"pi.generation checkpoint {checkpoint.GetType().Name} cannot be interrupted"),
        };
        var policy = runtime.Settings.Retry;
        var retry = policy.Enabled && attempt <= policy.MaxRetries;
        var until = retry ? runtime.Now() + Retry.RetryDelayMs(Compaction.ToPiRetryPolicy(policy), attempt) : 0L;
        var text = polled is null
            ? $"The host stopped while the model was answering, on attempt {attempt}"
            : $"The host stopped while the deferred answer was fetched, on attempt {attempt}";
        // 结束的运行不会留下仍在跑的提供方作业。
        if (!retry && polled is not null) await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            // 首次表写之前读取；只有失败才用它。
            FailurePolicy? failure = retry ? null : await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await ConvertPartialAsync(tx, live, runtime.ConversationId).ConfigureAwait(false);
            if (failure is null)
            {
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)attempt,
                    ["retry"] = new Dictionary<string, object?> { ["at"] = until, ["error"] = text },
                });
                return new TaskState
                {
                    Status = TaskStatus.Sleeping,
                    Checkpoint = new GenerationCheckpoint.RetryPhase(attempt, compacted, until, polled).ToJson(),
                    Until = until,
                };
            }

            await EndFailedRunAsync(
                tx, runtime, live, failure,
                new SubmissionSettlement.Unanswered(RunFailureReasons.Interrupted, text))
                .ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = text,
                        Detail = new Dictionary<string, object?> { ["reason"] = RunFailureReasons.Interrupted },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 结束一次被连续切断 <c>settings.restartLimit</c> 次的运行（spec §5.4）。到达上限时它仍把已提交的 partial
    /// 留作 aborted 条目、并取消它轮询的延迟应答；超过上限后，那次清理本身可能就是切断它的原因，于是不做清理。
    /// </summary>
    private static async Task EndCutOffAsync(ITaskRuntime runtime, TaskRecord task, Context context)
    {
        var checkpoint = ReadCheckpoint(task);
        var phase = task.State.Checkpoint is JsonDict raw && raw.TryGetValue("phase", out var value)
            ? value as string ?? "unknown"
            : "unknown";
        var text = $"The generation was interrupted {Live.CutOffCount(task)} in its {phase} phase";
        var salvage = Live.Salvageable(task, runtime);
        if (salvage && DeferredOf(checkpoint) is { } polled)
        {
            await CancelDeferredAsync(runtime, polled, context).ConfigureAwait(false);
        }

        await runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (salvage) await ConvertPartialAsync(tx, live, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(
                tx, runtime, live, failure,
                new SubmissionSettlement.Unanswered(RunFailureReasons.RestartLimit, text))
                .ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = text,
                        Detail = new Dictionary<string, object?> { ["reason"] = RunFailureReasons.RestartLimit },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>检查点正在轮询（或退避后要再轮询）的延迟应答。</summary>
    private static GenerationCheckpoint.PollRequest? DeferredOf(GenerationCheckpoint checkpoint) => checkpoint switch
    {
        GenerationCheckpoint.Poll poll => new GenerationCheckpoint.PollRequest(
            poll.Model, poll.ThinkingLevel, poll.Cutoff, poll.Handle),
        GenerationCheckpoint.RetryPhase { Polling: { } retryPoll } => retryPoll,
        _ => null,
    };

    /// <summary>取消一个没有尝试会再轮询的延迟应答；失败只上报，从不抛出。</summary>
    private static async Task CancelDeferredAsync(
        ITaskRuntime runtime, GenerationCheckpoint.PollRequest request, Context context)
    {
        var model = runtime.Models.GetModel(request.Model.Provider, request.Model.ModelId);
        if (model is null) return;
        try
        {
            await runtime.Models.CancelDeferredAsync(model, request.Handle, null, runtime.Signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            runtime.Report(error);
        }
    }

    /// <summary>把运行的输入以 <c>model_error</c> 结算为 unanswered 并以 <paramref name="text"/> 失败。</summary>
    private static async Task FailModelErrorAsync(ITaskRuntime runtime, string text, Context context)
    {
        await runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            await EndFailedRunAsync(
                tx, runtime, live, failure, new SubmissionSettlement.Unanswered("model_error", text))
                .ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = text, Detail = new Dictionary<string, object?> { ["reason"] = "model_error" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>把运行的输入以 <c>no_model</c> 结算为 unanswered 并失败。</summary>
    private static async Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, Context context)
    {
        var message = reference is null
            ? "No model is configured"
            : $"Model {reference.Provider}/{reference.ModelId} is not available";
        await runtime.CommitAsync(async (tx, _) =>
        {
            var failure = await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            // detail 说明缺的是哪个模型，客户端不必另找。
            await EndFailedRunAsync(
                tx, runtime, live, failure, new SubmissionSettlement.Unanswered("no_model", message))
                .ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = message, Detail = new Dictionary<string, object?> { ["reason"] = "no_model" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 把被打断、中止、出错或孤立的尝试留下的已提交 partial 追加为 aborted 的 assistant 条目；
    /// 调用方替换或移除 <c>generation</c>。对应 TS <c>convertPartial()</c>。
    /// </summary>
    internal static async Task ConvertPartialAsync(Transaction tx, TxDocChange live, ConversationId conversationId)
    {
        var generation = Live.GenerationOf(live);
        if (generation is null || !generation.TryGetValue("message", out var partial) || partial is null) return;
        var message = JsonTrees.Deserialize<AssistantMessage>(partial) with { StopReason = StopReason.Aborted };
        await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
    }

    /// <summary>追加 provider 结果并在同一提交里把它的用量记入 <c>pi.usage</c>。</summary>
    internal static async Task<TypedEntry<object?>> AppendAssistantAsync(
        Transaction tx, ConversationId conversationId, AssistantMessage message)
    {
        await Usage.RecordUsageAsync(
            tx, conversationId, "models", $"{message.Provider}/{message.Model}", message.UsageStats ?? new Pi.Ai.Types.Usage(0, 0))
            .ConfigureAwait(false);
        return await tx.AppendEntryAsync(
            DurableEntries.AssistantEntry, conversationId,
            new TypedEntryDraft<object?> { Model = [message] }).ConfigureAwait(false);
    }

    /// <summary>单个流事件的 partial 快照（Done / Error 没有）。</summary>
    private static AssistantMessage? PartialOf(AssistantMessageEvent evt) => evt switch
    {
        AssistantMessageEvent.Start e => e.Partial,
        AssistantMessageEvent.TextStart e => e.Partial,
        AssistantMessageEvent.TextDelta e => e.Partial,
        AssistantMessageEvent.TextEnd e => e.Partial,
        AssistantMessageEvent.ThinkingStart e => e.Partial,
        AssistantMessageEvent.ThinkingDelta e => e.Partial,
        AssistantMessageEvent.ThinkingEnd e => e.Partial,
        AssistantMessageEvent.ToolCallStart e => e.Partial,
        AssistantMessageEvent.ToolCallDelta e => e.Partial,
        AssistantMessageEvent.ToolCallEnd e => e.Partial,
        _ => null,
    };

    /// <summary>
    /// 流式完成一次请求并返回终态消息。partial 以至多每 <c>progress.partialIntervalMs</c>（缺省 100ms）一次
    /// 尾随提交的方式落盘（同一时刻至多一个提交在途）；finally 停止节流并等待在途提交，
    /// 因此不会有过期 partial 落在结果之后。对应 TS <c>streamResponse()</c>。
    /// </summary>
    private static async Task<AssistantMessage> StreamResponseAsync(
        ITaskRuntime runtime, ModelSpec model, IReadOnlyList<ChatMessage> messages,
        Dictionary<string, object?> options, int attempt, Context context)
    {
        var throttle = new PartialThrottle(runtime, attempt, context);
        var stream = runtime.Models.StreamSimple(model, messages, options);
        try
        {
            await foreach (var evt in stream.WithCancellation(runtime.Signal).ConfigureAwait(false))
            {
                // 没有内容的 partial（如 pi-ai 的开流 start 事件）什么也不显示；deferred 响应不会
                // 越过它，因此不会留下 partial。记增量与排队在同一次加锁里完成——否则一次 flush 可能取走
                // 增量，而它对应的 partial 还没排上队。
                var partial = PartialOf(evt);
                var shows = evt is not (AssistantMessageEvent.Done or AssistantMessageEvent.Error)
                    && partial is { Content.Count: > 0 };
                throttle.Observe(evt, shows ? partial : null);
            }

            return await stream.WaitForDoneAsync(runtime.Signal).ConfigureAwait(false);
        }
        finally
        {
            await throttle.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>partial 的节流提交器（TS streamResponse 闭包状态的等价物）。</summary>
    private sealed class PartialThrottle(ITaskRuntime runtime, int attempt, Context context)
    {
        private readonly object _gate = new();
        private readonly PartialChanges _changes = new();
        private AssistantMessage? _pending;
        private Task? _inFlight;
        private Action? _disarm;
        private bool _timerScheduled;
        private bool _stopped;

        /// <summary>
        /// 记下 <paramref name="evt"/> 增长的文本/思维增量，并在 <paramref name="partial"/> 非空时排队一次冲刷。
        /// 两件事在同一把锁里完成：否则一次冲刷可能取走增量，而它对应的 partial 还没排队。
        /// 对应 TS <c>changes.observe(evt)</c> 之后紧接 <c>schedule()</c>。
        /// </summary>
        public void Observe(AssistantMessageEvent evt, AssistantMessage? partial)
        {
            lock (_gate)
            {
                _changes.Observe(evt);
                if (partial is null) return;
                _pending = partial;
                if (!_timerScheduled && _inFlight is null && !_stopped) ScheduleLocked();
            }
        }

        /// <summary>在 Harness 的定时器上安排一次 flush；持有 <see cref="_gate"/> 时调用。</summary>
        private void ScheduleLocked()
        {
            _timerScheduled = true;
            _disarm = runtime.SetTimer(() =>
            {
                lock (_gate)
                {
                    _disarm = null;
                    _timerScheduled = false;
                }

                _ = FlushAsync();
            }, runtime.Settings.Progress.PartialIntervalMs);
        }

        private async Task FlushAsync()
        {
            AssistantMessage? partial;
            IReadOnlyList<PartialChanges.TextAppend> taken;
            lock (_gate)
            {
                _timerScheduled = false;
                partial = _pending;
                _pending = null;
                if (partial is null || _stopped) return;
                // 与取走 partial 同一次加锁：这批增量正好属于这条 partial。
                taken = _changes.Take();
            }

            // 同步拷贝：provider 持续修改其 partial。
            var message = JsonTrees.Deserialize<AssistantMessage>(JsonTrees.ToTree(partial));
            var tree = (Dictionary<string, object?>)JsonTrees.ToTree(message)!;
            var commit = runtime.CommitAsync(async (tx, _) =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                var generation = Live.GenerationOf(live);
                if (generation is null)
                {
                    Live.SetGeneration(live, new Dictionary<string, object?>
                    {
                        ["attempt"] = (long)attempt,
                        ["message"] = tree,
                    });
                }
                else
                {
                    // 逐叶赋值 + 文本按追加写：每次提交只花新增文本的长度，而不是整段消息的长度。
                    PartialChanges.Commit(
                        live, taken, tree,
                        generation.TryGetValue("message", out var committed)
                            ? committed as IReadOnlyDictionary<string, object?>
                            : null);
                }

                return null;
            }, context);
            lock (_gate) _inFlight = commit;
            try
            {
                await commit.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 中止标记或关闭之后的拒绝是预期的；已提交状态保持一致。
                if (!runtime.Signal.IsCancellationRequested) runtime.Report(error);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight = null;
                    if (_pending is not null && !_stopped && !_timerScheduled) ScheduleLocked();
                }
            }
        }

        /// <summary>停止节流并等待在途提交；其后不再有 partial 落盘。</summary>
        public async Task StopAsync()
        {
            Task? inFlight;
            lock (_gate)
            {
                _stopped = true;
                _disarm?.Invoke();
                _disarm = null;
                inFlight = _inFlight;
            }

            if (inFlight is not null) await inFlight.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 一次 provider 请求体上的 <c>beforeProviderRequest</c> 替换链：每个钩子拿到上一个钩子的结果，返回非 null 即替换。
    /// 对应 TS <c>providerPayload()</c>。
    /// </summary>
    private static async Task<System.Text.Json.Nodes.JsonObject?> ProviderPayloadAsync(
        ITaskRuntime runtime, ModelSpec model, System.Text.Json.Nodes.JsonObject payload, Context context)
    {
        var current = payload;
        await runtime.Hooks.EachAsync("beforeProviderRequest", async handler =>
        {
            if (handler is not IGenerationHooks hooks) return;
            var replaced = await hooks.BeforeProviderRequest(model, current, runtime, context).ConfigureAwait(false);
            if (replaced is not null) current = replaced;
        }).ConfigureAwait(false);
        return current;
    }

    /// <summary>在也清除 partial 的同一提交里分类一个终态 provider 消息。对应 TS <c>classify()</c>。</summary>
    private static async Task ClassifyAsync(
        ITaskRuntime runtime, RequestInfo request, AssistantMessage message, Context context)
    {
        // 中止标记或关闭：中止调用或重开的运行处理已提交状态。
        runtime.Signal.ThrowIfCancellationRequested();
        // 记下**请求**的档位；provider 回传的实际档位另由 ProviderThinkingLevel 承载。
        if (request.ThinkingLevel is { } requestedThinking) message = message with { ThinkingLevel = requestedThinking };
        var conversationId = runtime.ConversationId;
        if (message.StopReason == StopReason.Deferred && message.Deferred is { } handle)
        {
            var pollAt = Math.Max(
                runtime.Now() + (handle.PollAfterMs ?? DefaultPollAfterMs),
                request.PollAt is { } previous ? previous + 1 : long.MinValue);
            await runtime.CommitAsync(async (tx, _) =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)request.Attempt,
                    ["deferred"] = new Dictionary<string, object?> { ["pollAt"] = pollAt },
                });
                return new TaskState
                {
                    Status = TaskStatus.Sleeping,
                    Checkpoint = new GenerationCheckpoint.Poll(
                        request.Attempt, request.Compacted, request.Model, request.Cutoff, handle, pollAt).ToJson(),
                    Until = pollAt,
                };
            }, context).ConfigureAwait(false);
            return;
        }

        await runtime.Hooks.EachAsync("afterResponse", async handler =>
        {
            if (handler is IGenerationHooks hooks)
            {
                await hooks.AfterResponse(message, runtime, context).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        var calls = message.Content.OfType<ToolCallContent>().ToList();
        if (message.StopReason == StopReason.ToolUse && calls.Count > 0)
        {
            await StartToolRoundAsync(runtime, request, message, calls, context).ConfigureAwait(false);
            return;
        }

        if (message.StopReason is StopReason.Stop or StopReason.Length or StopReason.ToolUse)
        {
            await AnswerAsync(runtime, message, context).ConfigureAwait(false);
            return;
        }

        // 重试与压缩策略决定下一次尝试，因此现在读取而非在准备时固定。
        var settings = runtime.Settings;
        var overflow = message.StopReason == StopReason.Error && Overflow.IsContextOverflow(message);
        var modelPolicy = settings.Compaction.ForModel(request.Model);
        if (overflow && request.Compacted is null && modelPolicy.Enabled)
        {
            var view = await runtime.ContextAsync(conversationId, context, request.Cutoff).ConfigureAwait(false);
            if (Compaction.SelectCut(view, modelPolicy.KeepRecentTokens) is not null)
            {
                var text = message.ErrorMessage ?? "Context overflow";
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                    await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
                    Live.DeleteGeneration(live);
                    var child = await Compaction.CreateCompactionAsync(
                        tx, conversationId, CompactionReason.Overflow, runtime.TaskId).ConfigureAwait(false);
                    return new TaskState
                    {
                        Status = TaskStatus.Waiting,
                        Checkpoint = new GenerationCheckpoint.Prepare(request.Attempt, child, text).ToJson(),
                        On = [child],
                        Policy = JoinPolicy.AllSettled,
                    };
                }, context).ConfigureAwait(false);
                return;
            }
        }

        var retryPolicy = settings.Retry;
        // 溢出永不重试：只有压缩能让下一次请求放得下。
        var retry = message.StopReason == StopReason.Error
            && !overflow
            && Retry.IsRetryableAssistantError(message)
            && retryPolicy.Enabled
            && request.Attempt <= retryPolicy.MaxRetries;
        var until = retry
            ? runtime.Now() + Retry.RetryDelayMs(Compaction.ToPiRetryPolicy(retryPolicy), request.Attempt)
            : 0L;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // 首次表写之前读取；只有失败才用它。
            var failure = retry ? null : await PrepareFailureAsync(tx, runtime).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            if (retry)
            {
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)request.Attempt,
                    ["retry"] = new Dictionary<string, object?>
                    {
                        ["at"] = until, ["error"] = message.ErrorMessage ?? "",
                    },
                });
                return new TaskState
                {
                    Status = TaskStatus.Sleeping,
                    Checkpoint = new GenerationCheckpoint.RetryPhase(request.Attempt, request.Compacted, until).ToJson(),
                    Until = until,
                };
            }

            var failureText = message.ErrorMessage
                ?? $"Model response ended with stop reason {message.StopReason.ToString().ToLowerInvariant()}";
            await EndFailedRunAsync(
                tx, runtime, live, failure!, new SubmissionSettlement.Unanswered("model_error", failureText))
                .ConfigureAwait(false);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = failureText,
                        Detail = new Dictionary<string, object?> { ["reason"] = "model_error" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 最终答案；final 边界放置排队项（spec §6）。首个 onYield 续延追加用户消息并把运行交给后继生成——
    /// 仅当边界没有选中用户项且没有重置。否则运行的输入结算 done，被选中的用户项启动下一个运行。
    /// 对应 TS <c>answer()</c>。
    /// </summary>
    private static async Task AnswerAsync(ITaskRuntime runtime, AssistantMessage message, Context context)
    {
        IReadOnlyList<ContentBlock>? continuation = null;
        await runtime.Hooks.EachAsync("onYield", async handler =>
        {
            if (continuation is not null || handler is not IGenerationHooks hooks) return;
            var decision = await hooks.OnYield(message, runtime, context).ConfigureAwait(false);
            if (decision is { ContinueRun: true, Continue: { Count: > 0 } decided }) continuation = decided;
        }).ConfigureAwait(false);
        // afterRun 钩子声明了吗：声明了就让运行在答案之后等它们的任务，排队项留到那时再放置。
        var afterRun = await AsksAfterRunAsync(runtime, context).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // 队列模式在 Session 线上读取，即边界决定之时。
            var settings = runtime.Settings;
            var boundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var result = new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = entry.Id.Value },
                },
            };
            // 续延在 final 边界放置任何东西之前就已定夺：队列里没有会被选中的用户项，也没有会先跑的重置。
            var continues = TakesContinuation(continuation, boundary) ? continuation : null;
            if (continues is null && afterRun)
            {
                // 输入的 done 结算与保持住运行在同一提交里；钩子在 finish 阶段问。
                var held = HoldRun(tx, live, runtime, "answer", entry.Id, entry.Id);
                if (held is not null) return held;
            }

            var applied = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, runtime.Now())
                .ConfigureAwait(false);
            if (continues is not null)
            {
                await tx.AppendEntryAsync(
                    DurableEntries.UserEntry, conversationId,
                    new TypedEntryDraft<object?> { Model = [new UserMessage(continues, runtime.Now())] })
                    .ConfigureAwait(false);
                // 续延把运行的轮数清零：给 onYield 循环设上限是扩展的事。
                Live.SetRunTurns(live, 0);
                var successor = await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false);
                Live.HandOver(live, runtime.TaskId, successor);
                Live.DeleteGeneration(live);
                return result;
            }

            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Done(entry.Id));
            if (applied.Users.Count > 0)
            {
                await StartRunAsync(tx, conversationId, live, applied.Users, first: applied.First)
                    .ConfigureAwait(false);
            }

            return result;
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 在一个提交里追加 tool-calling 答案并启动其工具轮（spec §8.3）。对请求未提供的工具的调用在此得到
    /// tool_unavailable 结果；其他每个调用得到生成拥有的工具任务——顺序轮只有第一个现在启动。生成随后
    /// 在其 tools 阶段等待它们，持有运行。对应 TS <c>startToolRound()</c>。
    /// </summary>
    // ─── 运行结束任务（afterRun） ────────────────────────────────────────────

    /// <summary>
    /// 本阶段的 agent 是否选了声明 <c>afterRun</c> 的生成钩子：声明了，一次回答的运行要等它要的任务。
    /// 对应 TS <c>asksAfterRun()</c>。
    /// </summary>
    private static async Task<bool> AsksAfterRunAsync(ITaskRuntime runtime, Context context)
    {
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        return agent.Extensions
            .SelectMany(extension => extension.Hooks ?? [])
            .Any(hook => hook.Task == TaskName && hook.Handlers is IRunEndHooks);
    }

    /// <summary>
    /// 已回答且声明了 <c>afterRun</c> 的运行的第 A 个提交（spec §8.3）：输入的 done 结算与保持住
    /// <c>pi.live.run</c> 在同一提交里，随后在 <c>finish</c> 阶段问钩子。final 边界要等运行结束，因此排队项留队列。
    /// 运行不是本次任务持有时返回 null（交回调用方走原路径）。对应 TS <c>holdRun()</c>。
    /// </summary>
    private static TaskState? HoldRun(
        Transaction tx, TxDocChange live, ITaskRuntime runtime, string reason, EntryId answer, EntryId last)
    {
        if (Live.RunTaskId(live) != runtime.TaskId) return null;
        foreach (var input in Live.RunInputs(live))
        {
            tx.SettleSubmission(input, new SubmissionSettlement.Done(answer));
        }

        Live.ClearRunInputs(live);
        // 结束后任务仍在跑：run 上的标记让对话保持忙碌，Stop 会中止它们，崩溃后恢复它们。
        Live.SetRunAfterRun(live);
        Live.DeleteGeneration(live);
        Live.DeleteTools(live);
        var summary = new RunSummary
        {
            Reason = reason,
            Answer = answer,
            // 由边界起的运行有自己的第一个条目；没有记录时以答案代替。
            First = Live.RunFirst(live) ?? answer,
            Last = last,
        };
        return new TaskState
        {
            Status = TaskStatus.Running,
            Checkpoint = new GenerationCheckpoint.Finish(summary).ToJson(),
        };
    }

    /// <summary>
    /// 检查一个 <c>afterRun</c> 钩子要的任务并收进 <paramref name="prepared"/>（在线上之外）：种类必须是已安装的
    /// 非内建任务，输入是 JSON，其定义的 <c>initial()</c> 必须给出该任务某个阶段的 JSON checkpoint。坏请求被跳过
    /// 而不是让创建提交失败，最后把拒绝原因抛出去（由钩子分发报到该扩展名下）。对应 TS <c>prepareRunEndTasks()</c>。
    /// </summary>
    private static void PrepareRunEndTasks(
        ITaskRuntime runtime, IReadOnlyList<RunEndTaskRequest> requests, List<RunEndTaskRequest> prepared)
    {
        var rejected = new List<Exception>();
        foreach (var request in requests)
        {
            try
            {
                var name = request.Task.Name;
                if (name.StartsWith("pi.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"An afterRun hook cannot start the built-in task {name}");
                }

                if (runtime.Registry.Task(name) is null)
                {
                    throw new InvalidOperationException($"Task {name} is not installed");
                }

                if (request.Task.Initial(request.Input) is not JsonDict checkpoint
                    || !checkpoint.TryGetValue("phase", out var phase)
                    || phase is not string phaseName
                    || !request.Task.Phases.ContainsKey(phaseName))
                {
                    throw new InvalidOperationException($"Task {name} starts at an unknown phase");
                }

                // initial() 只跑一次：把校验出来的 checkpoint 固定在定义里，创建时直接用它的值。
                prepared.Add(request with { Task = request.Task with { Initial = _ => checkpoint } });
            }
            catch (Exception error)
            {
                rejected.Add(error);
            }
        }

        if (rejected.Count == 1) throw rejected[0];
        if (rejected.Count > 1) throw new AggregateException(rejected);
    }

    /// <summary>
    /// 问 <c>afterRun</c> 钩子要任务，并在移到 <c>drain</c> 的同一提交里把它们作为生成的孩子创建；一个都没有就
    /// 立刻结束运行。对应 TS <c>finish</c> 阶段。
    /// </summary>
    private static async Task FinishPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Finish)ReadCheckpoint(task);
        var prepared = new List<RunEndTaskRequest>();
        await runtime.Hooks.EachAsync("afterRun", async handler =>
        {
            if (handler is not IRunEndHooks hooks) return;
            var asked = await hooks.AfterRun(checkpoint.Run, runtime, context).ConfigureAwait(false);
            if (asked is not null) PrepareRunEndTasks(runtime, asked, prepared);
        }).ConfigureAwait(false);
        if (prepared.Count == 0)
        {
            await EndAnsweredRunAsync(runtime, checkpoint.Run.Answer, context).ConfigureAwait(false);
            return;
        }

        await runtime.CommitAsync(async (tx, _) =>
        {
            var tasks = new List<TaskId<object?>>();
            foreach (var request in prepared)
            {
                // 擦除形状：registry 存的是 AnyDurableTask，创建时以其定义为准。
                var created = await tx.CreateTaskErasedAsync(request.Task, request.Input, new TaskOptions
                {
                    Ownership = new TaskOwnership.TaskOwner(runtime.TaskId),
                    ConversationId = runtime.ConversationId,
                }).ConfigureAwait(false);
                tasks.Add(created);
            }

            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (Live.RunTaskId(live) == runtime.TaskId) Live.SetRunAfterRun(live, tasks);
            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new GenerationCheckpoint.Drain(checkpoint.Run.Answer, tasks).ToJson(),
                On = tasks,
                Policy = JoinPolicy.AllSettled,
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>afterRun 的任务都终态了（无论结局）：结束运行。对应 TS <c>drain</c> 阶段。</summary>
    private static async Task DrainPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Drain)ReadCheckpoint(task);
        await EndAnsweredRunAsync(runtime, checkpoint.Answer, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 已回答的运行的最后一个提交（spec §8.3）：afterRun 的任务终态了，或本来就没有。放置 final 边界选中的项、
    /// 结束运行，并为选中的用户项启动下一次运行。对应 TS <c>endAnsweredRun()</c>。
    /// </summary>
    private static async Task EndAnsweredRunAsync(ITaskRuntime runtime, EntryId answer, Context context)
    {
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var settings = runtime.Settings;
            var boundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var applied = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, runtime.Now())
                .ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Done(answer));
            if (applied.Users.Count > 0)
            {
                await StartRunAsync(tx, conversationId, live, applied.Users, first: applied.First)
                    .ConfigureAwait(false);
            }

            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = answer.Value },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 应答的 <c>onYield</c> 续延是否被采纳（spec §8.3），在 final 边界放置任何东西之前由收件箱决定：仅当队列里没有
    /// 会被选中的 steer/follow-up 用户项、也没有会先跑的重置。续延会把运行的轮数清零。
    /// 对应 TS <c>takesContinuation()</c>。
    /// </summary>
    private static bool TakesContinuation(IReadOnlyList<ContentBlock>? continuation, Inbox.Boundary boundary)
    {
        if (continuation is null) return false;
        var items = Inbox.QueuedItems(boundary);
        var hasUserItem = items.Any(item => item.TryGetValue("mode", out var mode)
            && mode is "steer" or "followUp");
        return !hasUserItem && !Inbox.HasQueuedReset(items);
    }

    private static async Task StartToolRoundAsync(
        ITaskRuntime runtime, RequestInfo request, AssistantMessage message,
        IReadOnlyList<ToolCallContent> calls, Context context)
    {
        var conversationId = runtime.ConversationId;
        var messages = request.Messages
            ?? (await runtime.ContextAsync(conversationId, context, request.Cutoff).ConfigureAwait(false)).Messages;
        var offered = Transcript.GetCurrentTools(messages)
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        // 轮启动时读取；工具在其工具任务解析它时被解析。
        var agentTools = (await runtime.AgentAsync(context).ConfigureAwait(false)).Tools;
        var sequential = runtime.Settings.ToolExecution == ToolExecutionMode.Sequential
            || calls.Any(call => offered.Contains(call.Name)
                && agentTools.FirstOrDefault(tool => tool.Name == call.Name)?.ExecutionMode
                    == ToolExecutionMode.Sequential);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var slots = new List<ToolSlot>();
            var toolTasks = new List<TaskId<object?>>();
            var pending = new List<string>();
            foreach (var call in calls)
            {
                // 对请求未提供的工具的调用也进轮：由其工具任务结算 tool_unavailable（并跑 afterTool）。
                // 顺序轮不豁免它们：上游同样把未提供的调用排在队列里。
                var unoffered = !offered.Contains(call.Name);
                if (sequential && toolTasks.Count > 0)
                {
                    pending.Add(call.Id);
                    slots.Add(new ToolSlot { CallId = call.Id, Name = call.Name, Status = "pending" });
                    continue;
                }

                var taskId = await CreateToolTaskAsync(tx, runtime, entry.Id, call.Id, unoffered).ConfigureAwait(false);
                toolTasks.Add(taskId);
                slots.Add(new ToolSlot
                {
                    CallId = call.Id, Name = call.Name, TaskId = taskId, Status = "pending", Unoffered = unoffered,
                });
            }

            Live.DeleteGeneration(live);
            Live.SetTools(live, slots);
            // 一个工具轮就是一次运行的一轮（AgentState.MaxTurns 按它计）。
            if (Live.RunTaskId(live) == runtime.TaskId)
            {
                Live.SetRunTurns(live, Live.RunTurns(live, runtime.TaskId) + 1);
                // 没有边界条目可依的运行（例如回答之后的 continue）以它第一个工具轮答案作为第一个条目。
                Live.SetRunFirstIfUnset(live, entry.Id);
            }

            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new GenerationCheckpoint.Tools(entry.Id, toolTasks, pending).ToJson(),
                On = toolTasks,
                Policy = JoinPolicy.AllSettled,
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 本轮的工具都终态了：应用它们的控制，并在 final 边界（terminate、handoff 或排队重置）结束运行，
    /// 或在 postTools 边界把它交给下一个生成（spec §8.5）。对应 TS <c>finishToolRound()</c>。
    /// </summary>
    private static async Task FinishToolRoundAsync(
        ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId<object?>> tools, Context context)
    {
        var conversationId = runtime.ConversationId;
        var outcomes = await runtime.OutcomesAsync(tools, context).ConfigureAwait(false);
        var controls = new List<ToolControl?>();
        var controlByTask = new Dictionary<TaskId<object?>, ToolControl?>();
        for (var index = 0; index < tools.Count; index++)
        {
            var outcome = outcomes[index];
            ToolControl? control = null;
            if (outcome is { Status: TaskOutcomeStatus.Completed, Result: JsonDict result })
            {
                control = ToolTaskResult.FromJson(result).Control;
            }

            controls.Add(control);
            controlByTask[tools[index]] = control;
        }

        var snapshot = await runtime.SnapshotAsync(Live.LiveDoc, conversationId, context).ConfigureAwait(false);
        var slots = snapshot is not null
            && snapshot.TryGetValue("tools", out var toolsValue)
            && toolsValue is IReadOnlyList<object?> list
            ? list.OfType<JsonDict>().Select(ToolSlot.FromJson).ToList()
            : [];
        var results = slots.Where(slot => slot.Entry is not null).Select(slot => slot.Entry!.Value).ToList();
        await runtime.Hooks.EachAsync("afterTools", async handler =>
        {
            if (handler is IGenerationHooks hooks)
            {
                await hooks.AfterTools(assistant, results, runtime, context).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        // 轮的每个调用，包括没有任务就得到回答的调用，都必须请求终止。
        var terminate = slots.Count > 0 && slots.All(slot => slot.TaskId is { } slotTask
            && controlByTask.TryGetValue(slotTask, out var control) && control?.Terminate == true);
        var added = controls.Where(control => control?.AddTools is not null)
            .SelectMany(control => control!.AddTools!).ToList();
        // 调用顺序的最后一个 handoff 胜出。
        var handoff = controls.LastOrDefault(control => control?.Handoff is not null)?.Handoff;
        // 与每个 agent 字段一样读自本阶段的 agent；轮次中途的 configure() 在轮结束时生效。
        var maxTurns = (await runtime.AgentAsync(context).ConfigureAwait(false)).MaxTurns;
        // 只有终止控制的轮才可能由 afterRun 接手：运行先答（这里是工具轮的结果），再等钩子要的任务。
        var afterRun = terminate && await AsksAfterRunAsync(runtime, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var settings = runtime.Settings;
            var boundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            if (added.Count > 0)
            {
                await AgentDocs.AddToolsAsync(tx, conversationId, added).ConfigureAwait(false);
            }

            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var now = runtime.Now();
            var turns = Live.RunTurns(live, runtime.TaskId);
            // 运行到目前为止的最后条目：最新的结果，没有结果条目时是 assistant。
            var last = results.Aggregate(assistant, (newest, id) => id.Value > newest.Value ? id : newest);
            if (handoff is not null)
            {
                // handoff 的重置先于每个排队项：排队的写入与被选中的 steer 都落进它的新上下文。
                var resetEntry = await tx.AppendEntryAsync(
                    DurableEntries.ResetEntry, conversationId,
                    new TypedEntryDraft<object?>
                    {
                        HeadIsSelf = true, Model = [new UserMessage([new TextContent(handoff)], now)],
                    }).ConfigureAwait(false);
                boundary.Head = resetEntry.Id;
                last = resetEntry.Id;
            }

            if (afterRun)
            {
                var held = HoldRun(tx, live, runtime, "terminate", assistant, last);
                if (held is not null) return held;
            }

            if (terminate)
            {
                var final = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, now).ConfigureAwait(false);
                Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Done(assistant));
                if (final.Users.Count > 0)
                {
                    await StartRunAsync(tx, conversationId, live, final.Users, first: final.First)
                        .ConfigureAwait(false);
                }
            }
            else if (maxTurns is { } limit && turns >= limit)
            {
                // 轮次上限让运行像失败一样结束：afterFailedRun 决定排队项，continue 则在轮结果上续跑。
                // 边界的表读已在任何写之前完成。
                var policy = runtime.Settings.AfterFailedRun;
                var failure = new FailurePolicy(
                    policy, policy == AfterFailedRun.Continue ? boundary : null);
                await EndFailedRunAsync(tx, runtime, live, failure,
                    new SubmissionSettlement.Unanswered(
                        RunFailureReasons.TurnLimit, turns == 1 ? "1 turn" : $"{turns} turns")).ConfigureAwait(false);
            }
            else
            {
                var postTools = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.PostTools, now)
                    .ConfigureAwait(false);
                if (postTools.Reset)
                {
                    // 排队的重置在答案之前切掉了运行的上下文。
                    Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("reset"));
                    if (postTools.Users.Count > 0)
                    {
                        await StartRunAsync(
                            tx, conversationId, live, postTools.Users, keepFailure: true).ConfigureAwait(false);
                    }
                }
                else
                {
                    Live.DeleteTools(live);
                    if (Live.RunTaskId(live) == runtime.TaskId)
                    {
                        Live.PushRunInputs(live, postTools.Users);
                    }

                    var successor = await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false);
                    Live.HandOver(live, runtime.TaskId, successor);
                }
            }

            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = assistant.Value },
                },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 运行启动与移交 ─────────────────────────────────────────────────────

    /// <summary>为 <paramref name="inputs"/> 启动运行，放置输入提交：新生成持有 <c>pi.live.run</c>。对应 TS <c>startRun()</c>。</summary>
    /// <summary>
    /// 为 <paramref name="inputs"/> 开始一次运行。清掉 <c>runFailure</c>，除非 <paramref name="keepFailure"/>：
    /// 结束那次失败的同一次提交里开始的运行保留它。对应 TS <c>startRun()</c>。
    /// </summary>
    public static async Task StartRunAsync(
        Transaction tx, ConversationId conversationId, TxDocChange live, IReadOnlyList<SubmissionId> inputs,
        bool keepFailure = false, EntryId? first = null)
    {
        Live.SetRun(live, await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false), inputs, first);
        if (!keepFailure) Live.DeleteRunFailure(live);
    }

    /// <summary>对话拥有的生成。对应 TS <c>createGeneration()</c>。</summary>
    internal static async Task<TaskId<object?>> CreateGenerationAsync(Transaction tx, ConversationId conversationId)
    {
        var taskId = await tx.CreateTaskAsync(
            Instance,
            new Dictionary<string, object?>(),
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conversationId })
            .ConfigureAwait(false);
        return TaskId<object?>.From(taskId.Value);
    }

    // ─── 检查点 ─────────────────────────────────────────────────────────────

    internal static GenerationCheckpoint ReadCheckpoint(TaskRecord task)
        => task.State.Checkpoint is JsonDict json
            ? GenerationCheckpoint.FromJson(json)
            : throw new InvalidOperationException("pi.generation requires a JSON checkpoint");
}
