// ============================================================================
// Agent controller provider — port of experimental/services/agent-controller-provider.ts (4g-3b)
// ============================================================================
//
// The worker-side implementation of `pi.agent-controller`: every presentation request becomes a
// durable submission on the root conversation, so a reconnecting presentation can still observe the
// outcome.
//
// Two Node-isms are worth noting:
// - TS `conversation.submit()` accepts `string | Content[]`; the C# draft always carries a block list,
//   so a plain message becomes a single `TextContent` (C318).
// - `whenBusy` is a string union in TS and a string here (the durable layer validates it).

using System.Globalization;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable;
using Pi.Durable.Harness;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Port of <c>createAgentController</c>.</summary>
public static class AgentControllerProvider
{
    // TS: /^[1-9][0-9]*$/ — no leading zero, no sign, no whitespace.
    private static readonly Regex SubmissionIdPattern = new(@"^[1-9][0-9]*\z", RegexOptions.Compiled);

    /// <summary>Number.MAX_SAFE_INTEGER — TS rejects ids beyond it because Number() would round.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991L;

    /// <summary>Build the controller over one conversation.</summary>
    public static IAgentController Create(IHarness harness, IConversation conversation) =>
        new Controller(harness, conversation);

    /// <summary>TS <c>parseSubmissionId</c>: a positive safe integer, else undefined.</summary>
    internal static SubmissionId? ParseSubmissionId(string value)
    {
        if (!SubmissionIdPattern.IsMatch(value))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id <= MaxSafeInteger
                ? new SubmissionId(id)
                : null;
    }

    /// <summary>
    /// TS <c>toInput</c>: no images keeps the bare message; with images the message leads and the
    /// images follow as blocks.
    /// </summary>
    internal static IReadOnlyList<ContentBlock> ToInput(AgentPromptRequest request)
    {
        if (request.Images is null || request.Images.Count == 0)
        {
            // TS returns the raw string, which the durable layer normalizes into one text block.
            return [new TextContent(request.Message)];
        }

        return
        [
            new TextContent(request.Message),
            .. request.Images.Select(image => (ContentBlock)new ImageContent(image.Data, image.MimeType)),
        ];
    }

    /// <summary>TS <c>toAgentError</c>: a busy conversation is reported as <c>busy</c>, everything else fails.</summary>
    internal static AgentOperationError ToAgentError(Exception error) => error switch
    {
        ConversationBusy => new AgentOperationError("busy", error.Message),
        _ => new AgentOperationError("operation_failed", error.Message),
    };

    /// <summary>
    /// The controller is both the typed local view and the wire member dictionary: TS hands chord a
    /// plain object and lets it classify the properties; the C# port needs the members spelled out
    /// (C325). Member names are the TS property names.
    /// </summary>
    private sealed class Controller(IHarness harness, IConversation conversation)
        : ServiceWireAdapter, IAgentController
    {
        protected override IEnumerable<KeyValuePair<string, object>> WireMembers() =>
        [
            Method("prompt", PromptWire),
            Method("steer", SteerWire),
            Method("followUp", FollowUpWire),
            Method("cancelQueued", CancelQueuedWire),
            Method("abort", AbortWire),
            Method("compact", CompactWire),
            Method("waitForPrompt", WaitForPromptWire),
        ];

        private Task<object?> PromptWire(object?[] args)
            => OperationWire(PromptAsync(ServiceWireValues.Read<AgentPromptRequest>(args[0]), ContextOf(args)));

        private Task<object?> SteerWire(object?[] args)
            => QueueWire(SteerAsync(ServiceWireValues.Read<AgentPromptRequest>(args[0]), ContextOf(args)));

        private Task<object?> FollowUpWire(object?[] args)
            => QueueWire(FollowUpAsync(ServiceWireValues.Read<AgentPromptRequest>(args[0]), ContextOf(args)));

        private async Task<object?> CancelQueuedWire(object?[] args)
            => ServiceWireValues.CancelOutcome(
                await CancelQueuedAsync(ServiceWireValues.Text(args[0]), ContextOf(args)).ConfigureAwait(false));

        private async Task<object?> AbortWire(object?[] args)
        {
            await AbortAsync(ContextOf(args)).ConfigureAwait(false);
            return null;
        }

        private Task<object?> CompactWire(object?[] args)
            => OperationWire(CompactAsync(ServiceWireValues.Read<AgentCompactionRequest>(args[0]), ContextOf(args)));

        private async Task<object?> WaitForPromptWire(object?[] args)
        {
            var result = await WaitForPromptAsync(ServiceWireValues.Text(args[0]), ContextOf(args)).ConfigureAwait(false);
            return result switch
            {
                AgentPromptResult.Done done => ServiceWireValues.PromptResult("done", done.Text, null),
                AgentPromptResult.Unanswered unanswered =>
                    ServiceWireValues.PromptResult("unanswered", null, unanswered.Reason),
                _ => throw new InvalidOperationException($"Unknown prompt result: {result.GetType().Name}"),
            };
        }

        private static async Task<object?> OperationWire(Task<AgentOperationResponse> call)
        {
            var response = await call.ConfigureAwait(false);
            return ServiceWireValues.OperationResponse(
                response.IsAccepted,
                (response as AgentOperationResponse.Accepted)?.OperationId,
                response.Error);
        }

        private static async Task<object?> QueueWire(Task<AgentQueueResponse> call)
        {
            var response = await call.ConfigureAwait(false);
            return ServiceWireValues.QueueResponse(
                response.IsAccepted,
                (response as AgentQueueResponse.Accepted)?.EntryId,
                response.Error);
        }

        public async Task<AgentOperationResponse> PromptAsync(AgentPromptRequest request, Context context)
        {
            try
            {
                var submission = await conversation.SubmitAsync(
                    new SubmissionDraft.Input { Content = ToInput(request), WhenBusy = "reject" }, context)
                    .ConfigureAwait(false);
                return new AgentOperationResponse.Accepted(FormatId(submission.Id));
            }
            catch (Exception error)
            {
                return new AgentOperationResponse.Rejected(ToAgentError(error));
            }
        }

        public Task<AgentQueueResponse> SteerAsync(AgentPromptRequest request, Context context) =>
            QueueAsync("steer", request, context);

        public Task<AgentQueueResponse> FollowUpAsync(AgentPromptRequest request, Context context) =>
            QueueAsync("followUp", request, context);

        public async Task<AgentCancelOutcome> CancelQueuedAsync(string entryId, Context context)
        {
            if (ParseSubmissionId(entryId) is not { } id)
            {
                return AgentCancelOutcome.NotFound;
            }

            var result = await harness.AbortSubmissionAsync(id, context, conversation.Id).ConfigureAwait(false);
            return result switch
            {
                "aborted" => AgentCancelOutcome.Cancelled,
                "not_found" => AgentCancelOutcome.NotFound,
                _ => AgentCancelOutcome.AlreadyConsumed,
            };
        }

        public Task AbortAsync(Context context) => conversation.AbortAsync(context);

        public async Task<AgentOperationResponse> CompactAsync(AgentCompactionRequest request, Context context)
        {
            try
            {
                var id = await conversation.CompactAsync(request.CustomInstructions, context).ConfigureAwait(false);
                return new AgentOperationResponse.Accepted(id.Value.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception error)
            {
                return new AgentOperationResponse.Rejected(ToAgentError(error));
            }
        }

        public async Task<AgentPromptResult> WaitForPromptAsync(string operationId, Context context)
        {
            var submission = ParseSubmissionId(operationId) is { } id
                ? await harness.SubmissionAsync(id, context).ConfigureAwait(false)
                : null;
            if (submission is null)
            {
                // TS throws here; an unknown id is a programming error, not a settled outcome.
                throw new InvalidOperationException($"Unknown prompt: {operationId}");
            }

            var settled = await submission.WaitAsync(context).ConfigureAwait(false);
            if (settled.Record is SubmissionRecord.InputRecord { Status: SubmissionStatus.Unanswered } unanswered)
            {
                return new AgentPromptResult.Unanswered(unanswered.Reason ?? string.Empty);
            }

            // TS: settled.type === "input" ? settled.answer : undefined.
            var answer = settled.Record is SubmissionRecord.InputRecord { Answer: { } entryId } ? entryId : (EntryId?)null;
            EntryRecord? entry = null;
            if (answer is { } id2)
            {
                entry = await harness.CommitAsync(tx => tx.GetEntryAsync(id2), context).ConfigureAwait(false);
            }

            // TS: message?.role === "assistant" ? content.flatMap(c => c.type === "text" ? [c.text] : []).join("") : "".
            var message = entry?.Model is { Count: > 0 } model ? model[0] : null;
            var text = message is AssistantMessage assistant
                ? string.Concat(assistant.Content.OfType<TextContent>().Select(block => block.Text))
                : string.Empty;
            return new AgentPromptResult.Done(text);
        }

        private async Task<AgentQueueResponse> QueueAsync(
            string whenBusy,
            AgentPromptRequest request,
            Context context)
        {
            try
            {
                var submission = await conversation.SubmitAsync(
                    new SubmissionDraft.Input { Content = ToInput(request), WhenBusy = whenBusy }, context)
                    .ConfigureAwait(false);
                return new AgentQueueResponse.Accepted(FormatId(submission.Id));
            }
            catch (Exception error)
            {
                return new AgentQueueResponse.Rejected(ToAgentError(error));
            }
        }

        /// <summary>TS <c>String(submission.id)</c> — the durable id is a number there.</summary>
        private static string FormatId(SubmissionId id) => id.Value.ToString(CultureInfo.InvariantCulture);
    }
}
