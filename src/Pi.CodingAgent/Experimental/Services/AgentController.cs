// ============================================================================
// Agent controller service contract — port of experimental/services/agent-controller.ts (4g-3b)
// ============================================================================
//
// The presentation-safe command facade over the worker-owned root conversation. A presentation never
// touches the agent directly: every prompt/steer/follow-up/compact goes through this service, which
// the session worker provides.
//
// TS expresses the replies as discriminated unions (`{ accepted: true; … } | { accepted: false; … }`).
// C# keeps one record per variant under a shared base so callers can switch on the type; the base also
// carries `Accepted` because `SlashCommandRunResult` is the *union* of the operation and queue replies
// and only ever asks "was it accepted" (C313).

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>An image attached to a prompt. TS <c>AgentPromptImage</c> (<c>type</c> is the literal "image").</summary>
public sealed record AgentPromptImage(string Data, string MimeType)
{
    /// <summary>The discriminant TS pins to the literal <c>"image"</c>.</summary>
    public string Type => "image";
}

/// <summary>A prompt or steer request. TS <c>AgentPromptRequest</c>.</summary>
public sealed record AgentPromptRequest(string Message, IReadOnlyList<AgentPromptImage>? Images);

/// <summary>A rejected operation. TS <c>AgentOperationError</c>.</summary>
public sealed record AgentOperationError(string Code, string Message);

/// <summary>
/// Common shape of the two submission replies. TS keeps them as separate unions; the port adds this
/// base so <see cref="SlashCommandRunResult"/> can be one type (C313).
/// </summary>
public abstract record AgentSubmissionResponse
{
    /// <summary>Whether the worker took the submission.</summary>
    public abstract bool IsAccepted { get; }

    /// <summary>The rejection, or <see langword="null"/> when accepted.</summary>
    public abstract AgentOperationError? Error { get; }
}

/// <summary>
/// <c>operationId</c> identifies the durable submission of a prompt, or the task of a compaction.
/// TS <c>AgentOperationResponse</c>.
/// </summary>
public abstract record AgentOperationResponse : AgentSubmissionResponse
{
    /// <summary>The durable operation id.</summary>
    public sealed record Accepted(string OperationId) : AgentOperationResponse
    {
        public override bool IsAccepted => true;

        public override AgentOperationError? Error => null;
    }

    /// <summary>The worker refused the operation (for example <c>busy</c>).</summary>
    public sealed record Rejected(AgentOperationError Reason) : AgentOperationResponse
    {
        public override bool IsAccepted => false;

        public override AgentOperationError? Error => Reason;
    }
}

/// <summary>
/// <c>entryId</c> identifies the durable submission; <c>cancelQueued()</c> withdraws it while it is
/// still queued. TS <c>AgentQueueResponse</c>.
/// </summary>
public abstract record AgentQueueResponse : AgentSubmissionResponse
{
    /// <summary>The durable queue entry id.</summary>
    public sealed record Accepted(string EntryId) : AgentQueueResponse
    {
        public override bool IsAccepted => true;

        public override AgentOperationError? Error => null;
    }

    /// <summary>The worker refused the submission.</summary>
    public sealed record Rejected(AgentOperationError Reason) : AgentQueueResponse
    {
        public override bool IsAccepted => false;

        public override AgentOperationError? Error => Reason;
    }
}

/// <summary>
/// The settled outcome of a prompt: the answer's text, or why it got none.
/// TS <c>AgentPromptResult</c>.
/// </summary>
public abstract record AgentPromptResult
{
    /// <summary>The prompt was answered.</summary>
    public sealed record Done(string Text) : AgentPromptResult;

    /// <summary>The prompt settled without an answer.</summary>
    public sealed record Unanswered(string Reason) : AgentPromptResult;
}

/// <summary>A compaction request. TS <c>AgentCompactionRequest</c>.</summary>
public sealed record AgentCompactionRequest(string? CustomInstructions);

/// <summary>The outcome of withdrawing a queued entry. TS the inline union of <c>cancelQueued</c>.</summary>
public enum AgentCancelOutcome
{
    Cancelled,
    AlreadyConsumed,
    NotFound,
}

/// <summary>
/// Presentation-safe command facade over the worker-owned root conversation.
/// TS <c>AgentController</c>.
/// </summary>
public interface IAgentController
{
    /// <summary>Start a run; rejected with <c>busy</c> while one is active.</summary>
    Task<AgentOperationResponse> PromptAsync(AgentPromptRequest request, Context context);

    /// <summary>Steer the active run, or start one when idle.</summary>
    Task<AgentQueueResponse> SteerAsync(AgentPromptRequest request, Context context);

    /// <summary>Queue input for after the active run, or start one when idle.</summary>
    Task<AgentQueueResponse> FollowUpAsync(AgentPromptRequest request, Context context);

    /// <summary>Withdraw a queued entry, if it is still queued.</summary>
    Task<AgentCancelOutcome> CancelQueuedAsync(string entryId, Context context);

    /// <summary>Withdraw queued input and abort the active run and compaction.</summary>
    Task AbortAsync(Context context);

    /// <summary>Compact the conversation.</summary>
    Task<AgentOperationResponse> CompactAsync(AgentCompactionRequest request, Context context);

    /// <summary>Wait until the prompt with this <paramref name="operationId"/> is answered or settles unanswered.</summary>
    Task<AgentPromptResult> WaitForPromptAsync(string operationId, Context context);
}

/// <summary>TS <c>export const AgentController = defineService&lt;AgentController&gt;(…)</c>.</summary>
public static class AgentController
{
    /// <summary>The chord service handle for <see cref="IAgentController"/>.</summary>
    public static readonly Service<IAgentController> Service =
        Api.DefineService<IAgentController>("pi.agent-controller");
}
