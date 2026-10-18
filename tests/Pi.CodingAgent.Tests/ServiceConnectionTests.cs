using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Client;
using Pi.CodingAgent.Experimental.Services;
using Pi.Protocol;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the remote service sources (4g-3c): the pure state-mapping leaves, and a routed binding
/// driven through a loopback transport so the presentation-side path (open → activate → call) is
/// exercised end to end without a real connection.
/// </summary>
public class ServiceConnectionTests
{
    // ---------------------------------------------------------------- toServerConnectionState

    [Fact]
    public void ToServerConnectionState_ConnectingCarriesTheAttempt()
    {
        var state = ServiceConnection.ToServerConnectionState(ConnectionState.Connecting, 3);

        var connecting = Assert.IsType<ServerConnectionState.Connecting>(state);
        Assert.Equal(3, connecting.Attempt);
    }

    [Fact]
    public void ToServerConnectionState_ConnectedCarriesATimestamp()
    {
        var state = ServiceConnection.ToServerConnectionState(ConnectionState.Connected, 1);

        var connected = Assert.IsType<ServerConnectionState.Connected>(state);
        Assert.False(string.IsNullOrEmpty(connected.Since));
    }

    [Fact]
    public void ToServerConnectionState_DisconnectedDefaultsTheReason()
    {
        var state = ServiceConnection.ToServerConnectionState(ConnectionState.Disconnected, 1);

        var disconnected = Assert.IsType<ServerConnectionState.Disconnected>(state);
        Assert.Equal("Client is disconnected", disconnected.Reason);
        Assert.Null(disconnected.RetryAt);
    }

    [Fact]
    public void ToServerConnectionState_DisconnectedTakesTheErrorMessage()
    {
        var state = ServiceConnection.ToServerConnectionState(
            ConnectionState.Disconnected, 1, new InvalidOperationException("socket closed"));

        var disconnected = Assert.IsType<ServerConnectionState.Disconnected>(state);
        Assert.Equal("socket closed", disconnected.Reason);
    }

    // ---------------------------------------------------------------- sameAttachment

    [Fact]
    public void SameAttachment_RequiresAllThreeFields()
    {
        var left = new RpcTarget.SessionTarget("s", "session", "a");

        Assert.True(ServiceConnection.SameAttachment(left, new RpcTarget.SessionTarget("s", "session", "a")));
        Assert.False(ServiceConnection.SameAttachment(left, new RpcTarget.SessionTarget("other", "session", "a")));
        Assert.False(ServiceConnection.SameAttachment(left, new RpcTarget.SessionTarget("s", "other", "a")));
        Assert.False(ServiceConnection.SameAttachment(left, new RpcTarget.SessionTarget("s", "session", "other")));
    }

    [Fact]
    public void SameAttachment_TreatsUndefinedAsItsOwnValue()
    {
        var attachment = new RpcTarget.SessionTarget("s", "session", "a");

        Assert.True(ServiceConnection.SameAttachment(null, null));
        Assert.False(ServiceConnection.SameAttachment(attachment, null));
        Assert.False(ServiceConnection.SameAttachment(null, attachment));
    }

    // ---------------------------------------------------------------- throwFailures

    [Fact]
    public void ThrowFailures_PassesWithoutErrors()
        => ServiceConnection.ThrowFailures([null, null], "unused");

    [Fact]
    public void ThrowFailures_RethrowsASingleError()
    {
        var error = new InvalidOperationException("only");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => ServiceConnection.ThrowFailures([null, error], "unused"));

        Assert.Same(error, thrown);
    }

    [Fact]
    public void ThrowFailures_AggregatesSeveralErrors()
    {
        var first = new InvalidOperationException("one");
        var second = new InvalidOperationException("two");

        var thrown = Assert.Throws<AggregateException>(
            () => ServiceConnection.ThrowFailures([first, second], "several failed"));

        // .NET appends the inner messages to the aggregate's message.
        Assert.StartsWith("several failed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(first, thrown.InnerExceptions);
        Assert.Contains(second, thrown.InnerExceptions);
    }

    // ---------------------------------------------------------------- routed binding

    private static ServiceConnection.RoutedServiceBinding Binding(
        RemoteServiceProvider provider,
        Func<bool>? getBound = null,
        Func<Context, Task>? onActivate = null,
        Action? remove = null)
        => new(new ServiceConnection.RoutedServiceBindingOptions
        {
            Services = ["echoer"],
            Transport = LoopbackServiceTransport.Create(provider),
            GetBound = getBound ?? (() => true),
            OnActivate = onActivate,
            Remove = remove,
        });

    private static RemoteServiceProvider EchoProvider()
    {
        var provider = new RemoteServiceProvider([new RemoteServiceProvider.ProviderDefinition("echoer")]);
        provider.Provide("echoer", new Dictionary<string, object>
        {
            ["echo"] = (Func<object?[], object?>)(args => args[0]),
        });
        return provider;
    }

    [Fact]
    public async Task RoutedBinding_ActivatesOnceAndCallsThroughTheTransport()
    {
        var activations = 0;
        var binding = Binding(EchoProvider(), onActivate: _ =>
        {
            activations += 1;
            return Task.CompletedTask;
        });

        await binding.ReadyAsync(Context.Background);
        await binding.ReadyAsync(Context.Background);

        Assert.Equal(1, activations);

        var facade = binding.Use(new Pi.Chord.Facets.Service<object>("echoer"));
        var answer = await facade.Member("echo").CallAsync(["round-trip"], Context.Background);

        Assert.Equal("round-trip", answer);
    }

    [Fact]
    public async Task RoutedBinding_UpdateBoundBeforeActivationIsIgnored()
    {
        var provider = EchoProvider();
        var binding = Binding(provider, getBound: () => false);

        // TS: a binding that never activated ignores the bound change entirely.
        await binding.UpdateBoundAsync(true, Context.Background);

        var activations = 0;
        var bound = Binding(provider, getBound: () => true, onActivate: _ =>
        {
            activations += 1;
            return Task.CompletedTask;
        });
        await bound.ReadyAsync(Context.Background);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task RoutedBinding_DisposeRemovesItFromTheSource()
    {
        var removed = 0;
        var binding = Binding(EchoProvider(), remove: () => removed += 1);

        await binding.DisposeAsync(Context.Background);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task RoutedBinding_UseRawReturnsTheFacade()
    {
        var binding = Binding(EchoProvider());
        await binding.ReadyAsync(Context.Background);

        var raw = ((Pi.Chord.Facets.IRemoteServices)binding).UseRaw("echoer");

        var facade = Assert.IsType<RemoteServiceFacade>(raw);
        Assert.Contains("echo", facade.Keys);
    }
}
