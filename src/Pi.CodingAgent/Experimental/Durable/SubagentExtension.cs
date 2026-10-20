using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable;
using Pi.Durable.Harness;
using Pi.Durable.Tools;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental.Durable;

/// <summary>
/// 前台子代理：每次调用创建一个由本次调用任务所有的子对话，在其中跑任务并返回子对话的答复。中止调用会中止子对话；
/// 子对话比调用活得更久，用户可以切过去继续聊。对应 TS <c>durable/subagent.ts</c> 的 <c>Subagent</c>。
/// </summary>
public sealed class SubagentExtension : IExtension
{
    public const string ToolName = "subagent";

    /// <summary>子对话拿到的扩展集合里去掉了本扩展，因此子代理不能再往下委派。</summary>
    public static IExtension Instance { get; } = new SubagentExtension();

    public string Name => ToolName;

    public IReadOnlyList<IToolRegistration>? Tools { get; } = [new SubagentTool()];

    public IReadOnlyList<IPromptSection>? Sections => null;

    public IReadOnlyList<HookRegistration>? Hooks => null;

    public IReadOnlyList<Wrap>? Wraps => null;

    public IReadOnlyList<AnyDurableTask>? Tasks => null;

    private sealed class SubagentTool : IToolRegistration
    {
        private static readonly ToolSchema SubagentParameters = ToolSchemaBuilder.Object(
            new Dictionary<string, object?>
            {
                ["task"] = ToolSchemaBuilder.String("What the subagent should do"),
            },
            "task");

        public string Name => ToolName;

        public string Description =>
            "Delegate a self-contained task to a subagent with the same tools and get its answer back. "
            + "Give it everything it needs to know; it does not see this conversation.";

        public ToolSchema Parameters => SubagentParameters;

        /// <summary>崩溃后重跑时能找到它创建的子和它发起的提交。</summary>
        public string? Replay => "safe";

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => null;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var parameters = ToolArgs.Object(args);
            var task = ToolArgs.RequiredString(parameters, "task");

            var child = await api.CommitAsync(async tx =>
            {
                var existing = (await tx.ScanConversationsAsync(
                    new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items;
                if (existing.Count > 0) return existing[0].Id;

                // 起手是本对话 agent 的副本；去掉本扩展，它就再也不能往下委派。
                var created = await tx.CreateConversationAsync(
                    new ConversationOwnership.TaskOwned(api.TaskId));
                await AgentDocs.ConfigureAsync(
                    tx,
                    created.Id,
                    new AgentChange
                    {
                        Extensions = new AgentChange.ExtensionChange { Remove = [Instance] },
                    });
                return created.Id;
            }, context).ConfigureAwait(false);

            await api.DetailsAsync(new Dictionary<string, object?> { ["conversationId"] = child }, context)
                .ConfigureAwait(false);
            var handle = await api.ConversationAsync(child, context).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Subagent conversation {child} is missing");

            var submission = await handle.SubmitAsync(
                new SubmissionDraft.Input
                {
                    Content = [new TextContent(task)],
                    RequestId = $"subagent:{api.TaskId}",
                },
                context).ConfigureAwait(false);
            var settled = await submission.WaitAsync(context).ConfigureAwait(false);
            if (settled.Record is not SubmissionRecord.InputRecord { Status: SubmissionStatus.Done } input
                || input.Answer is not { } answer)
            {
                throw new InvalidOperationException($"Subagent {child} failed: {StatusOf(settled.Record)}");
            }

            var text = await AnswerTextAsync(api, answer, context).ConfigureAwait(false);
            return new ToolExecutionResult
            {
                Content = [new TextContent(text)],
                Details = new Dictionary<string, object?> { ["conversationId"] = child },
            };
        }

        /// <summary>结算记录的状态；<c>write</c> 提交没有状态，按类型名报告。</summary>
        private static string StatusOf(SubmissionRecord record) =>
            record is SubmissionRecord.InputRecord input ? input.Status.ToString().ToLowerInvariant() : record.Type;

        /// <summary>子对话答复条目的文本。对应 TS <c>answerText</c>。</summary>
        private static async Task<string> AnswerTextAsync(IToolExecutionApi api, EntryId answer, Context context)
        {
            var entry = await api.CommitAsync(
                tx => tx.GetEntryAsync(DurableEntries.AssistantEntry, answer), context).ConfigureAwait(false);
            var message = entry?.Model?.FirstOrDefault() as AssistantMessage;
            return message is null
                ? ""
                : string.Concat(message.Content.OfType<TextContent>().Select(block => block.Text));
        }
    }
}
