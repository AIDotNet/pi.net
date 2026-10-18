// ============================================================================
// Transcript service contract — port of experimental/services/transcript.ts (4g-3b)
// ============================================================================
//
// The root conversation's durable view: active entries plus its live, inbox, agent, and usage
// documents. The worker owns the conversation; presentations only observe the replicated view.

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Durable.Harness;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Port of the TS <c>Transcript</c> interface.</summary>
public interface ITranscript
{
    /// <summary>The replicated durable view of the root conversation.</summary>
    IReplicatedState<ConversationView> State { get; }
}

/// <summary>TS <c>export const Transcript = defineService&lt;Transcript&gt;("pi.transcript")</c>.</summary>
public static class Transcript
{
    /// <summary>The chord service handle for <see cref="ITranscript"/>.</summary>
    public static readonly Service<ITranscript> Service = Api.DefineService<ITranscript>("pi.transcript");
}
