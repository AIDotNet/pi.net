using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.CodingAgent.Core;
using Pi.Durable;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Tools;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.CodingAgent.Experimental.Vacation;

using JsonDict = IReadOnlyDictionary<string, object?>;

/// <summary>The durable search/research tools and prompt used by the Vacation planner.</summary>
public static class VacationExtensions
{
    public const string ResearchTaskName = "vacation.research";

    public const string Prompt =
        "You are a friendly vacation planning assistant. Keep answers short. Hand all research to the research "
        + "tool; while it runs, keep chatting with the user. When a [research report] arrives, turn it into a short plan.";

    public const string ResearchInstructions =
        "You are a research subagent for a vacation planner. Search weather, museums, and trains in parallel, "
        + "in one step, then report the findings in a few short bullet points.";

    private static readonly DurableTask<JsonDict, JsonDict, JsonDict> ResearchTask =
        DurableTasks.DefineTask(new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = ResearchTaskName,
            Version = 1,
            Initial = _ => Checkpoint("deliver"),
            Phases = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deliver"] = (TaskPhaseHandler)DeliverResearchAsync,
                ["report"] = (TaskPhaseHandler)DeliverReportAsync,
            },
            Abort = (TaskPhaseHandler)AbortResearchAsync,
        });

    public static IExtension Search { get; } = new SearchExtension(new SearchTool());

    public static IExtension Vacation { get; } = new PlannerExtension(
        new ResearchTool(),
        Define.Section("preamble", (_, _) => Task.FromResult<string?>(Prompt), tag: false),
        AnyDurableTask.From(ResearchTask));

    private static readonly ToolSchema SearchParameters = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["topic"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("weather", "museums", "trains"),
            },
            ["city"] = ToolSchemaBuilder.String(),
        },
        "topic",
        "city");

    private static readonly ToolSchema ResearchParameters = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["task"] = ToolSchemaBuilder.String("What to research, with the city and dates"),
        },
        "task");

    private static readonly IReadOnlyDictionary<string, SearchData> SearchDataByTopic =
        new Dictionary<string, SearchData>(StringComparer.Ordinal)
        {
            ["weather"] = new(6,
                ["Saturday: 24°C and sunny", "Sunday: 21°C, a short shower around 3 pm"]),
            ["museums"] = new(10,
            [
                "Kunsthistorisches Museum: open 10-18, book a time slot",
                "Belvedere: Klimt's The Kiss, quietest before 11",
                "Albertina: Monet to Picasso, open until 21 on Friday",
            ]),
            ["trains"] = new(30,
                ["Railjet from Salzburg: 2h 22m, every 30 min", "Nightjet from Munich: arrives 06:20"]),
        };

    private sealed record SearchData(int Seconds, IReadOnlyList<string> Results);

    private sealed class SearchTool(
        Func<TimeSpan, CancellationToken, Task>? delay = null) : IToolRegistration
    {
        private readonly Func<TimeSpan, CancellationToken, Task> _delay =
            delay ?? ((duration, signal) => Task.Delay(duration, signal));

        public string Name => "search";

        public string Description =>
            "Search travel information about a city. Topics: weather, museums, trains. Slow; call several in parallel.";

        public ToolSchema Parameters => SearchParameters;

        public string? Replay => "safe";

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => null;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var parameters = ToolArgs.Object(args);
            var topic = ToolArgs.RequiredString(parameters, "topic");
            var city = ToolArgs.RequiredString(parameters, "city");
            if (!SearchDataByTopic.TryGetValue(topic, out var found))
                throw new InvalidOperationException($"Unsupported search topic: {topic}");

            var steps = (int)Math.Ceiling(found.Seconds / 2d);
            var signal = context.AbortSignal.GetValueOrDefault();
            for (var step = 1; step <= steps; step++)
            {
                api.Output($"searching {topic} in {city}: source {step}/{steps}\n");
                await _delay(TimeSpan.FromSeconds(2), signal).ConfigureAwait(false);
            }
            foreach (var result in found.Results) api.Output($"- {result}\n");
            return new ToolExecutionResult();
        }
    }

    private sealed class ResearchTool : IToolRegistration
    {
        public string Name => "research";

        public string Description =>
            "Start a research subagent in the background. It searches while you keep talking with the user; "
            + "its report arrives later as a message starting with [research report].";

        public ToolSchema Parameters => ResearchParameters;

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => null;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var taskDescription = ToolArgs.RequiredString(ToolArgs.Object(args), "task");
            var input = new Dictionary<string, object?> { ["task"] = taskDescription };
            await api.CommitAsync(async tx =>
            {
                var owner = await tx.CreateTaskAsync(
                    ResearchTask,
                    input,
                    new TaskOptions
                    {
                        Ownership = new TaskOwnership.ConversationOwner(),
                        Background = true,
                    }).ConfigureAwait(false);
                var child = await tx.CreateConversationAsync(
                    new ConversationOwnership.TaskOwned(TaskId<object?>.From(owner.Value))).ConfigureAwait(false);
                await AgentDocs.ConfigureAsync(tx, child.Id, new AgentChange
                {
                    Extensions = new AgentChange.ExtensionChange { Exact = [Search] },
                    Instructions = ResearchInstructions,
                }).ConfigureAwait(false);
                return true;
            }, context).ConfigureAwait(false);

            return new ToolExecutionResult
            {
                Content = [new TextContent("Research started in the background.")],
            };
        }
    }

    private sealed class SearchExtension(IToolRegistration search) : IExtension
    {
        public string Name => "vacation-search";

        public IReadOnlyList<IToolRegistration>? Tools => [search];

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }

    private sealed class PlannerExtension(
        IToolRegistration research,
        IPromptSection preamble,
        AnyDurableTask task) : IExtension
    {
        public string Name => "vacation";

        public IReadOnlyList<IToolRegistration>? Tools => [research];

        public IReadOnlyList<IPromptSection>? Sections => [preamble];

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => [task];
    }

    private static async Task DeliverResearchAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        ConversationId? childId = null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var page = await tx.ScanConversationsAsync(
                new ConversationQuery { OwnerTaskId = task.Id }, 1).ConfigureAwait(false);
            childId = page.Items.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException($"Research task {task.Id.Value} has no owned conversation");
            return null;
        }, context).ConfigureAwait(false);

        var ownedConversation = childId
            ?? throw new InvalidOperationException($"Research task {task.Id.Value} has no owned conversation");
        var child = await runtime.ConversationAsync(ownedConversation, context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Research conversation {ownedConversation.Value} is unavailable");
        var request = new SubmissionDraft.Input
        {
            Content = [new TextContent(ReadTaskDescription(task.Input))],
            RequestId = $"research:{task.Id.Value}",
        };
        var settled = await (await child.SubmitAsync(request, context).ConfigureAwait(false))
            .WaitAsync(context).ConfigureAwait(false);

        await runtime.CommitAsync(async (tx, _) =>
        {
            var report = await CreateReportAsync(tx, settled.Record).ConfigureAwait(false);
            return new TaskState
            {
                Status = DurableTaskStatus.Running,
                Checkpoint = Checkpoint("report", report),
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task DeliverReportAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = ReadCheckpoint(task.State.Checkpoint);
        var main = await runtime.ConversationAsync(runtime.ConversationId, context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Main conversation {runtime.ConversationId.Value} is unavailable");
        await main.SubmitAsync(new SubmissionDraft.Input
        {
            Content = [new TextContent(RequiredString(checkpoint, "report"))],
            WhenBusy = "followUp",
            RequestId = $"research-report:{task.Id.Value}",
        }, context).ConfigureAwait(false);

        await runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = DurableTaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
            }),
            context).ConfigureAwait(false);
    }

    private static Task AbortResearchAsync(TaskRecord task, ITaskRuntime runtime, Context context)
        => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = DurableTaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
            }),
            context);

    private static async Task<string> CreateReportAsync(Transaction transaction, SubmissionRecord record)
    {
        if (record is not SubmissionRecord.InputRecord input) return "[research report] The research failed: ?";
        if (input.Status != SubmissionStatus.Done || input.Answer is not { } answer)
        {
            var reason = input.Status == SubmissionStatus.Unanswered ? input.Reason ?? "?" : "?";
            return $"[research report] The research failed: {reason}";
        }

        var entry = await transaction.GetEntryAsync(DurableEntries.AssistantEntry, answer).ConfigureAwait(false);
        var message = entry?.Model?.FirstOrDefault() as AssistantMessage;
        var text = message is null
            ? ""
            : string.Concat(message.Content.OfType<TextContent>().Select(content => content.Text));
        return $"[research report] {text}";
    }

    private static string ReadTaskDescription(object? value)
        => RequiredString(ReadCheckpoint(value), "task");

    private static JsonDict ReadCheckpoint(object? value)
    {
        if (value is JsonDict dictionary) return dictionary;
        if (value is System.Text.Json.JsonElement element
            && JsonTrees.FromElement(element) is JsonDict converted)
        {
            return converted;
        }
        throw new InvalidOperationException("Vacation research task data must be a JSON object");
    }

    private static string RequiredString(JsonDict values, string key)
        => values.TryGetValue(key, out var value) && value is string text
            ? text
            : throw new InvalidOperationException($"Vacation research task is missing string field '{key}'");

    private static JsonDict Checkpoint(string phase, string? report = null)
    {
        var checkpoint = new Dictionary<string, object?> { ["phase"] = phase };
        if (report is not null) checkpoint["report"] = report;
        return checkpoint;
    }
}
