using Pi.Ai.Models;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Services;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the session worker's service assembly (4g-3b). The pure leaf (<c>serviceScopeKey</c>) is
/// pinned directly; the rest needs a live durable harness, so the remaining cases build the worker over
/// an in-memory harness and drive its call entry point — which exercises the whole facet graph (agent
/// controller, session plugins, models, transcript) and the per-scope endpoint cache.
/// </summary>
public class SessionWorkerProviderTests
{
    [Fact]
    public void ServiceScopeKey_JoinsTheTwoIdsWithNul()
    {
        // TS: `${scope.serverConnectionId}\0${scope.attachmentId}`.
        Assert.Equal("conn\0attach", SessionWorkerProvider.ServiceScopeKey(
            new WorkerServiceScope("conn", "attach")));
        Assert.Equal("\0", SessionWorkerProvider.ServiceScopeKey(new WorkerServiceScope("", "")));
        // Distinct pairs stay distinct as long as neither id contains the separator; ids that do are
        // ambiguous, exactly like the TS template join.
        Assert.NotEqual(
            SessionWorkerProvider.ServiceScopeKey(new WorkerServiceScope("a", "b")),
            SessionWorkerProvider.ServiceScopeKey(new WorkerServiceScope("a\0b", "")));
    }

    [Fact]
    public async Task CreateSessionWorkerServicesAsync_ServesTheServiceCatalogue()
    {
        var (harness, root) = await OpenHarnessAsync();
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new MemoryCredentialStore(),
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            ModelsPathDisabled = true,
            RefreshOnCreate = false,
        });

        var published = new List<string>();
        var services = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(
            new SessionWorkerOptions
            {
                Harness = harness,
                Conversation = root,
                ModelRuntime = modelRuntime,
                SettingsManager = SettingsManager.InMemory(),
                Publish = (scope, subscriptionId, _) =>
                {
                    published.Add($"{scope.ServerConnectionId}/{subscriptionId}");
                    return Task.CompletedTask;
                },
            });

        var scope = new WorkerServiceScope("server-1", "attachment-1");
        var result = await services.InvokeAsync(
            ServiceWire.CreateServiceCatalogueCall(), scope, Context.Background);

        // The provider answers with the catalogue entries themselves; the wire form (which
        // `ServiceWire.ParseServiceCatalogue` reads) is what a transport serializes them to.
        var catalogue = Assert.IsAssignableFrom<IReadOnlyList<ServiceCatalogueEntry>>(result);
        var ids = catalogue.Select(entry => entry.ServiceId).ToList();
        // The four built-in facets' services; the local ones are not routable and stay out.
        Assert.Contains("pi.agent-controller", ids);
        Assert.Contains("pi.models", ids);
        Assert.Contains("pi.transcript", ids);
        Assert.Contains("pi.session-plugins", ids);
        Assert.DoesNotContain("pi.local.slash-commands", ids);
        Assert.Empty(published);

        await services.DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionWorkerServicesAsync_ReusesTheEndpointPerScopeAndDropsItOnRemoval()
    {
        var (harness, root) = await OpenHarnessAsync();
        var services = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(
            new SessionWorkerOptions
            {
                Harness = harness,
                Conversation = root,
                ModelRuntime = null,
                Publish = (_, _, _) => Task.CompletedTask,
            });

        var first = new WorkerServiceScope("server-1", "attachment-1");
        var other = new WorkerServiceScope("server-1", "attachment-2");
        var call = ServiceWire.CreateServiceCatalogueCall();

        // Both scopes are served, repeatedly (each scope keeps its endpoint), and removal of one does
        // not disturb the other.
        Assert.NotNull(await services.InvokeAsync(call, first, Context.Background));
        Assert.NotNull(await services.InvokeAsync(call, first, Context.Background));
        Assert.NotNull(await services.InvokeAsync(call, other, Context.Background));

        services.RemoveSubscriptions(scope => scope.AttachmentId == "attachment-1");
        Assert.NotNull(await services.InvokeAsync(call, other, Context.Background));

        services.RemoveSubscriptions(_ => true);
        // After dropping every endpoint the worker still serves: it recreates one on demand.
        Assert.NotNull(await services.InvokeAsync(call, first, Context.Background));
        await services.DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionWorkerServicesAsync_UnknownServiceCallFails()
    {
        var (harness, root) = await OpenHarnessAsync();
        var services = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(
            new SessionWorkerOptions
            {
                Harness = harness,
                Conversation = root,
                ModelRuntime = null,
                Publish = (_, _, _) => Task.CompletedTask,
            });

        var call = new ServiceCall("pi.nope", "whatever", []);
        var scope = new WorkerServiceScope("server-1", "attachment-1");

        // TS throws the chord RemoteServiceError here ("service_not_allowed").
        await Assert.ThrowsAsync<RemoteServiceError>(() =>
            services.InvokeAsync(call, scope, Context.Background));
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Invoke_ModelsGetThinkingLevels_AnswersOverTheWire()
    {
        var (harness, root) = await OpenHarnessAsync();
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new MemoryCredentialStore(),
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            ModelsPathDisabled = true,
            RefreshOnCreate = false,
        });
        var services = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(
            new SessionWorkerOptions
            {
                Harness = harness,
                Conversation = root,
                ModelRuntime = modelRuntime,
                Publish = (_, _, _) => Task.CompletedTask,
            });

        // No model is selected, so the worker reports the single "off" level — as the TS literals.
        var result = await services.InvokeAsync(
            new ServiceCall("pi.models", "getThinkingLevels", []),
            new WorkerServiceScope("server-1", "attachment-1"),
            Context.Background);

        Assert.Equal(new List<object?> { "off" }, result);
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Invoke_SessionPluginsReload_RunsTheChainedReload()
    {
        var (harness, root) = await OpenHarnessAsync();
        var reloads = 0;
        var services = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(
            new SessionWorkerOptions
            {
                Harness = harness,
                Conversation = root,
                ModelRuntime = null,
                // A plugin loader that counts its loads: the worker loads the initial generation once,
                // then each reload loads a candidate and swaps it in (the tail serialises them).
                FacetLoader = new CountingFacetLoader(() => reloads += 1),
                Publish = (_, _, _) => Task.CompletedTask,
            });

        var call = new ServiceCall("pi.session-plugins", "reload", []);
        var scope = new WorkerServiceScope("server-1", "attachment-1");

        Assert.Equal(1, reloads); // the initial generation
        Assert.Null(await services.InvokeAsync(call, scope, Context.Background));
        Assert.Null(await services.InvokeAsync(call, scope, Context.Background));
        Assert.Equal(3, reloads); // plus the two reloads

        await services.DisposeAsync();
    }

    private sealed class CountingFacetLoader(Action onLoad) : IFacetLoader
    {
        public Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
        {
            onLoad();
            return Task.FromResult(new LoadedFacets { Facets = [], Dispose = () => Task.CompletedTask });
        }
    }

    private static async Task<(HarnessImpl Harness, IConversation Root)> OpenHarnessAsync()
    {
        var harness = await Harness.OpenAsync(
            new MemoryStorage(),
            new HarnessOptions
            {
                Models = new Pi.Ai.Models.Models(),
                Registry = Registry.CreateRegistry(),
                SettingsSource = () => new HarnessSettings
                {
                    Extensions = [],
                    Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
                },
            },
            Context.Background);
        var root = await harness.RootAsync(Context.Background);
        return (harness, root);
    }

    private sealed class MemoryCredentialStore : Pi.Ai.Auth.ICredentialStore
    {
        public Task<Pi.Ai.Auth.Credential?> ReadAsync(string providerId,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<Pi.Ai.Auth.Credential?>(null);

        public Task<IReadOnlyList<Pi.Ai.Auth.CredentialInfo>> ListAsync(
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Pi.Ai.Auth.CredentialInfo>>([]);

        public Task<Pi.Ai.Auth.Credential?> ModifyAsync(
            string providerId,
            Func<Pi.Ai.Auth.Credential?, Task<Pi.Ai.Auth.Credential?>> change,
            System.Threading.CancellationToken cancellationToken = default)
            => change(null);

        public Task DeleteAsync(string providerId,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
