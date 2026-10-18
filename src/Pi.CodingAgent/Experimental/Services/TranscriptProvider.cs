// ============================================================================
// Transcript service facet — port of experimental/services/transcript-provider.ts (4g-3b)
// ============================================================================
//
// The facet that serves a conversation's durable view as the `pi.transcript` service. The facet owns
// the attached state: disposing the facet disposes the replicated view, which stops the underlying
// watch.
//
// TS builds the facet with `defineFacet({ id, setup })`; C# implements `IFacet` directly (C317).

using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Durable.Harness;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Port of <c>createTranscriptServiceFacet</c>.</summary>
public static class TranscriptProvider
{
    /// <summary>
    /// Serve the conversation's durable view state. The facet owns and disposes the attached state.
    /// </summary>
    public static async Task<IFacet> CreateTranscriptServiceFacetAsync(IConversation conversation, Context context)
    {
        var state = await conversation.ViewStateAsync(context).ConfigureAwait(false);
        return new TranscriptFacet(state);
    }

    /// <summary>The facet itself: id <c>@pi/transcript</c>, providing <see cref="ITranscript"/>.</summary>
    private sealed class TranscriptFacet(AttachedReplicatedState<ConversationView> state) : IFacet
    {
        public string Id => "@pi/transcript";

        public void Setup(IFacetEnvironment environment)
        {
            // TS: env.own(() => state.dispose()).
            environment.Own(() =>
            {
                state.Dispose();
                return Task.CompletedTask;
            });
            environment.Provide(Transcript.Service, new TranscriptService(state));
        }
    }

    /// <summary>Read-only view over the attached state.</summary>
    private sealed class TranscriptService(AttachedReplicatedState<ConversationView> state) : ITranscript
    {
        public IReplicatedState<ConversationView> State => state;
    }
}
