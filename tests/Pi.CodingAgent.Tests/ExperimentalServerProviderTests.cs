using Pi.Ai.Types;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental.Services;
using Pi.Server;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the experimental server services (4g-3c): the routed host publishes the session
/// directory / management / presentation plugins per attached presentation, mutations run through a
/// serialised tail, and the directory is refreshed after every create and remove.
/// </summary>
public class ExperimentalServerProviderTests
{
    private static SessionSummary Summary(string sessionId, double createdAt = 1)
        => new("server-1", sessionId, createdAt);

    [Fact]
    public async Task AttachClient_PublishesTheThreeServices()
    {
        var (services, _, attachment) = await AttachAsync();

        var result = await attachment.InvokeServiceAsync(
            ServiceWire.CreateServiceCatalogueCall(), (_, _, _) => Task.CompletedTask, Context.Background);

        // The catalogue crosses as the wire value tree, the shape the protocol layer carries.
        var catalogue = Assert.IsType<List<object?>>(result);
        var ids = catalogue
            .Select(entry => Assert.IsType<Dictionary<string, object?>>(entry)["serviceId"])
            .ToList();
        Assert.Contains("pi.session-directory", ids);
        Assert.Contains("pi.session-management", ids);
        Assert.Contains("pi.presentation-plugins", ids);
    }

    [Fact]
    public async Task Create_OverTheWire_AnswersTheSummaryAndRefreshesTheDirectory()
    {
        var (services, host, attachment) = await AttachAsync();

        var created = await InvokeAsync(attachment, new ServiceCall(
            "pi.session-management", "create", [new Dictionary<string, object?> { ["id"] = "s1" }]));

        var summary = Assert.IsType<Dictionary<string, object?>>(created);
        Assert.Equal("server-1", summary["serverId"]);
        Assert.Equal("s1", summary["sessionId"]);
        Assert.Equal(1d, summary["createdAt"]);

        // The create refreshed the directory: revision 2 with the new session.
        var state = await DirectoryStateAsync(attachment);
        Assert.Equal(2L, Assert.IsType<long>(state["revision"]));
        var sessions = Assert.IsType<List<object?>>(state["sessions"]);
        var single = Assert.IsType<Dictionary<string, object?>>(Assert.Single(sessions));
        Assert.Equal("s1", single["sessionId"]);
        Assert.Equal(1, host.Created);
    }

    [Fact]
    public async Task Remove_PreparesTheRemovalBeforeDeleting()
    {
        var (services, host, attachment) = await AttachAsync();

        await InvokeAsync(attachment, new ServiceCall("pi.session-management", "remove", ["s1"]));

        Assert.Equal(["prepare-removal:s1", "remove:s1"], host.Log);
        var state = await DirectoryStateAsync(attachment);
        Assert.Equal(2L, state["revision"]);
        Assert.Empty(Assert.IsType<List<object?>>(state["sessions"]));
    }

    [Fact]
    public async Task AttachAndDetach_ForwardToThePresentation()
    {
        var (services, host, attachment) = await AttachAsync();

        Assert.Null(await InvokeAsync(attachment, new ServiceCall("pi.session-management", "attach", ["s1"])));
        Assert.Null(await InvokeAsync(attachment, new ServiceCall("pi.session-management", "detach", [])));

        Assert.Equal(["attach:s1", "detach"], host.Log);
    }

    [Fact]
    public async Task Reload_WithoutAPreparation_Rejects()
    {
        var (services, _, attachment) = await AttachAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            attachment, new ServiceCall("pi.presentation-plugins", "reload", [])));

        Assert.Contains("No Session plugin selection is prepared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareSession_RemembersThePackagePathsForTheReload()
    {
        var (services, host, attachment) = await AttachAsync();

        var prepared = await InvokeAsync(attachment, new ServiceCall(
            "pi.presentation-plugins",
            "prepareSession",
            [new Dictionary<string, object?>
            {
                ["sessionId"] = "s1",
                ["packagePaths"] = new List<object?> { "/pkg/a", "/pkg/b" },
            }]));

        // TS answers with `selected.presentationPlugins` — the presentation facet data itself;
        // the package paths the selection resolved to stay server-side bookkeeping.
        Assert.Equal("plugins", prepared);
        Assert.Equal(["prepare:s1"], host.Log);

        // The reload answers with the presentation-side plugins for the remembered package paths.
        var reloaded = await InvokeAsync(attachment, new ServiceCall(
            "pi.presentation-plugins", "reload", []));
        Assert.Equal("reloaded", reloaded);
        Assert.Equal(["prepare:s1", "reload:/pkg/a,/pkg/b"], host.Log);
    }

    [Fact]
    public async Task Detach_ClearsTheRememberedPackagePaths()
    {
        var (services, _, attachment) = await AttachAsync();

        await InvokeAsync(attachment, new ServiceCall(
            "pi.presentation-plugins",
            "prepareSession",
            [new Dictionary<string, object?> { ["sessionId"] = "s1", ["packagePaths"] = null }]));
        await InvokeAsync(attachment, new ServiceCall("pi.session-management", "detach", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            attachment, new ServiceCall("pi.presentation-plugins", "reload", [])));
    }

    [Fact]
    public async Task PrepareSession_WithoutPackagePaths_RemembersAnEmptySelection()
    {
        var (services, host, attachment) = await AttachAsync();

        // TS: `packagePaths ?? undefined` — a null and a missing key are the same request.
        await InvokeAsync(attachment, new ServiceCall(
            "pi.presentation-plugins",
            "prepareSession",
            [new Dictionary<string, object?> { ["sessionId"] = "s1" }]));

        await InvokeAsync(attachment, new ServiceCall("pi.presentation-plugins", "reload", []));
        Assert.Equal(["prepare:s1", "reload:"], host.Log);
    }

    [Fact]
    public async Task Release_IsIdempotentAndRejectsLaterCalls()
    {
        var (services, _, attachment) = await AttachAsync();

        await attachment.ReleaseAsync(Context.Background);
        await attachment.ReleaseAsync(Context.Background);

        await Assert.ThrowsAsync<InvalidOperationException>(() => attachment.InvokeServiceAsync(
            ServiceWire.CreateServiceCatalogueCall(), (_, _, _) => Task.CompletedTask, Context.Background));
    }

    [Fact]
    public async Task Dispose_ReleasesEveryAttachment()
    {
        var (services, host, _) = await AttachAsync();
        var first = await services.Host.AttachClientAsync(new FakePresentation([]), Context.Background);
        var second = await services.Host.AttachClientAsync(new FakePresentation([]), Context.Background);

        await services.DisposeAsync();

        // Both attachments were released, so neither answers any more.
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.InvokeServiceAsync(
            ServiceWire.CreateServiceCatalogueCall(), (_, _, _) => Task.CompletedTask, Context.Background));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InvokeServiceAsync(
            ServiceWire.CreateServiceCatalogueCall(), (_, _, _) => Task.CompletedTask, Context.Background));
        // Releasing again (from dispose's own bookkeeping) is a no-op, so this does not throw.
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Refresh_BumpsTheRevisionWithoutAMutation()
    {
        var (services, _, attachment) = await AttachAsync();

        await services.RefreshAsync();

        var state = await DirectoryStateAsync(attachment);
        Assert.Equal(2L, state["revision"]);
    }

    [Fact]
    public async Task Mutations_StaySerialised()
    {
        var gate = new TaskCompletionSource();
        var host = new FakeHost
        {
            OnCreate = _ => gate.Task,
        };
        var services = await ExperimentalServerProvider.CreateExperimentalServerServicesAsync(
            Options(host));
        var attachment = await services.Host.AttachClientAsync(new FakePresentation([]), Context.Background);

        // The first create blocks on the gate; the remove queues behind it.
        var create = InvokeAsync(attachment, new ServiceCall(
            "pi.session-management", "create", [new Dictionary<string, object?> { ["id"] = "s1" }]));
        var remove = InvokeAsync(attachment, new ServiceCall(
            "pi.session-management", "remove", ["s1"]));

        Assert.False(create.IsCompleted);
        Assert.False(remove.IsCompleted);
        gate.SetResult();

        await create;
        await remove;
        // The create's refresh happened before the remove's, in tail order.
        Assert.Equal(["create:s1", "remove:s1"], host.Log);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<object?> InvokeAsync(IRoutedServerServiceAttachment attachment, ServiceCall call)
        => attachment.InvokeServiceAsync(call, (_, _, _) => Task.CompletedTask, Context.Background);

    private static async Task<Dictionary<string, object?>> DirectoryStateAsync(
        IRoutedServerServiceAttachment attachment)
    {
        var snapshot = await InvokeAsync(attachment, ServiceWire.CreateServiceSubscribeCall(
            "sub-1", "pi.session-directory", ServiceMode.Singleton));
        // The subscribe answer crosses as the wire value tree, exactly what the protocol layer
        // carries; decode it the way the client does.
        var wire = ServiceWire.ParseWireServiceSubscriptionSnapshot(snapshot);
        var decoded = new ServiceStateDecoder().DecodeSnapshot(wire);
        var instance = Assert.Single(decoded.Instances);
        var member = Assert.IsType<ServiceMemberSnapshot.State>(
            instance.Members.OfType<ServiceMemberSnapshot.State>().Single());
        var op = Assert.IsType<DeltaOp.Replace>(Assert.Single(member.Ops));
        return Assert.IsType<Dictionary<string, object?>>(op.Value);
    }

    private static ExperimentalServerOptions Options(FakeHost host) => new()
    {
        ListAsync = _ => Task.FromResult<IReadOnlyList<SessionSummary>>(
            host.Sessions.Select(id => Summary(id)).ToList()),
        CreateAsync = (options, _) =>
        {
            host.Log.Add($"create:{options.Id}");
            host.Sessions.Add(options.Id!);
            return host.OnCreate is { } open
                ? open(options.Id!).ContinueWith(_ => Summary(options.Id!))
                : Task.FromResult(Summary(options.Id!));
        },
        RemoveAsync = (sessionId, _) =>
        {
            host.Log.Add($"remove:{sessionId}");
            host.Sessions.Remove(sessionId);
            return Task.CompletedTask;
        },
        PrepareSessionPluginsAsync = (sessionId, packagePaths, _) =>
        {
            host.Log.Add($"prepare:{sessionId}");
            return Task.FromResult(new PrepareSessionAnswer(
                packagePaths ?? [], "plugins"));
        },
        ReloadPresentationPluginsAsync = (packagePaths, _) =>
        {
            host.Log.Add($"reload:{string.Join(",", packagePaths)}");
            return Task.FromResult<object?>("reloaded");
        },
    };

    private static async Task<(ExperimentalServerServices Services, FakeHost Host,
        IRoutedServerServiceAttachment Attachment)> AttachAsync()
    {
        var host = new FakeHost();
        var services = await ExperimentalServerProvider.CreateExperimentalServerServicesAsync(Options(host));
        var attachment = await services.Host.AttachClientAsync(new FakePresentation(host.Log), Context.Background);
        return (services, host, attachment);
    }

    private sealed class FakeHost
    {
        public List<string> Sessions { get; } = [];

        public List<string> Log { get; } = [];

        public Func<string, Task>? OnCreate { get; set; }

        public int Created => Log.Count(entry => entry.StartsWith("create:", StringComparison.Ordinal));
    }

    private sealed class FakePresentation(List<string> log) : IRoutedServerPresentation
    {
        public Task AttachSessionAsync(string sessionId, Context context)
        {
            log.Add($"attach:{sessionId}");
            return Task.CompletedTask;
        }

        public Task DetachSessionAsync(Context context)
        {
            log.Add("detach");
            return Task.CompletedTask;
        }

        public Task PrepareSessionRemovalAsync(string sessionId, Context context)
        {
            log.Add($"prepare-removal:{sessionId}");
            return Task.CompletedTask;
        }
    }
}
