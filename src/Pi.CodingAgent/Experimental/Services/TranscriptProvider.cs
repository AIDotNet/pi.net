// ============================================================================
// Transcript service facet — port of experimental/services/transcript-provider.ts (4g-3b)
// ============================================================================
//
// The facet that serves a conversation's durable view as the `pi.transcript` service. The facet owns
// the attached state: disposing the facet disposes the replicated view, which stops the underlying
// watch. Upstream removed `viewState()`, so the attached state is rebuilt over the view's exact-frame
// watch (see WatchState).
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
        var watch = await conversation.WatchAsync(context).ConfigureAwait(false);
        var state = WatchState.Attach<ConversationView>(watch);
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

    /// <summary>
    /// Read-only view over the attached state. The transcript service is state-only, so its wire shape
    /// is the single <c>state</c> member TS exposes (C325).
    /// </summary>
    private sealed class TranscriptService(AttachedReplicatedState<ConversationView> state)
        : ServiceWireAdapter, ITranscript
    {
        protected override IEnumerable<KeyValuePair<string, object>> WireMembers()
            => [new("state", state)];

        public IReplicatedState<ConversationView> State => state;
    }
}
