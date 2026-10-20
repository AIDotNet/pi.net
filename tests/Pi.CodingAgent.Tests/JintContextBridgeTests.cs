using Jint;
using Jint.Runtime;
using Pi.Chord.Context;
using Pi.CodingAgent.Experimental.Plugins;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class JintContextBridgeTests
{
    private static Engine CreateAbortListenerEngine()
    {
        var engine = new Engine(options => options.ExperimentalFeatures = ExperimentalFeature.TaskInterop);
        JintFacetModuleHost.InitializeEngine(engine);
        engine.Execute("""
            globalThis.__pi_functions.set("listener", event => {
                globalThis.abortEventType = event.type;
            });
            """);
        return engine;
    }

    [Fact]
    public async Task ContextBridge_PreservesTypedKeyIdentityAndAbortSignal()
    {
        var engine = new Engine(options => options.ExperimentalFeatures = ExperimentalFeature.TaskInterop);
        JintFacetModuleHost.InitializeEngine(engine);
        var runtime = new JintPluginRuntime(engine);
        using var cancellation = new CancellationTokenSource();
        var key = new ContextKey<string>("shared");
        var otherKey = new ContextKey<string>("shared");
        var context = Context.Background
            .WithValue(key, "available")
            .WithValue(Context.AbortSignalKey, (CancellationToken?)cancellation.Token);
        using var bridge = new JsContextBridge(runtime, "context-id", context, _ => { });

        engine.SetValue("contextBridge", bridge);
        engine.SetValue("key", key);
        engine.SetValue("otherKey", otherKey);
        engine.Execute("globalThis.context = globalThis.__pi_contextProxy(contextBridge);");

        Assert.Equal("available", engine.Evaluate("context.value(key)").AsString());
        Assert.True(engine.Evaluate("context.value(otherKey) === undefined").AsBoolean());
        Assert.False(engine.Evaluate("context.abortSignal.aborted").AsBoolean());
        engine.Execute("""
            globalThis.abortEvent = undefined;
            context.abortSignal.addEventListener("abort", event => {
                globalThis.abortEvent = {
                    type: event.type,
                    targetMatches: event.target === context.abortSignal,
                    reasonName: context.abortSignal.reason.name
                };
            });
            """);

        cancellation.Cancel();
        await bridge.AbortSignalBridge!.DispatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(engine.Evaluate("context.abortSignal.aborted").AsBoolean());
        Assert.True(engine.Evaluate("context.abortSignal.reason === context.abortSignal.reason").AsBoolean());
        Assert.Equal("abort", engine.Evaluate("abortEvent.type").AsString());
        Assert.True(engine.Evaluate("abortEvent.targetMatches").AsBoolean());
        Assert.Equal("AbortError", engine.Evaluate("abortEvent.reasonName").AsString());
    }

    [Fact]
    public async Task AbortSignalBridge_DispatchesAbortEvents()
    {
        var engine = CreateAbortListenerEngine();
        var runtime = new JintPluginRuntime(engine);
        using var cancellation = new CancellationTokenSource();
        var errors = new List<Exception>();
        using var signal = new JsAbortSignalBridge(runtime, cancellation.Token, errors.Add);
        var handle = signal.AddListener("listener");
        Assert.NotEmpty(handle);

        cancellation.Cancel();
        await signal.DispatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("abort", engine.GetValue("abortEventType").AsString());
        Assert.True(signal.Aborted);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AbortSignalBridge_DoesNotDispatchRemovedListeners()
    {
        var engine = CreateAbortListenerEngine();
        var runtime = new JintPluginRuntime(engine);
        using var cancellation = new CancellationTokenSource();
        var errors = new List<Exception>();
        using var signal = new JsAbortSignalBridge(runtime, cancellation.Token, errors.Add);
        var handle = signal.AddListener("listener");
        Assert.NotEmpty(handle);

        signal.RemoveListener(handle);
        cancellation.Cancel();
        await signal.DispatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(engine.GetValue("abortEventType").IsUndefined());
        Assert.Empty(errors);
    }
}
