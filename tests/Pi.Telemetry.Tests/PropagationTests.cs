using Pi.Telemetry;
using Xunit;

namespace Pi.Telemetry.Tests;

/// <summary>
/// 传播能力（<c>TelemetryPropagation</c>）：carrier 是适配器自有的不透明数据，用来把一次提交/一次调用的
/// span 身份带过边界。对应上游 <c>memory.ts</c> 的 `pi-memory-scope/span/trace` 载体。
/// </summary>
public class PropagationTests
{
    [Fact]
    public async Task InjectsAndExtractsSpansWithinOneScope()
    {
        var telemetry = new InMemoryTelemetryContext();
        ITelemetrySpan? captured = null;
        await telemetry.StartSpanAsync(new SpanOptions("parent"), span =>
        {
            captured = span;
            return Task.FromResult(true);
        });

        var carrier = telemetry.Propagation.Inject(captured!);
        Assert.NotNull(carrier);
        Assert.True(carrier.ContainsKey("pi-memory-scope"));
        Assert.True(carrier.ContainsKey("pi-memory-span"));
        Assert.True(carrier.ContainsKey("pi-memory-trace"));

        var parent = telemetry.Propagation.Extract(carrier);
        await parent.StartSpanAsync(new SpanOptions("child"), _ => Task.FromResult(true));

        var spans = telemetry.GetSpans();
        var parentSpan = Assert.Single(spans, span => span.Name == "parent");
        var child = Assert.Single(spans, span => span.Name == "child");
        // 同一 scope 内解出的父上下文能定位本地父 span。
        Assert.Equal(parentSpan.Id, child.ParentId);
    }

    [Fact]
    public async Task UnknownCarriersFallBackToTheRoot()
    {
        var telemetry = new InMemoryTelemetryContext();
        var context = telemetry.Propagation.Extract(new TelemetryCarrier { ["unknown"] = "value" });
        Assert.Same(telemetry, context);

        await context.StartSpanAsync(new SpanOptions("orphan"), _ => Task.FromResult(true));
        var span = Assert.Single(telemetry.GetSpans());
        Assert.Null(span.ParentId);
    }

    [Fact]
    public async Task ForeignScopesInheritTheTraceWithoutALocalParent()
    {
        var telemetry = new InMemoryTelemetryContext();
        var foreign = new TelemetryCarrier
        {
            ["pi-memory-scope"] = "another-scope",
            ["pi-memory-span"] = "7",
            ["pi-memory-trace"] = "another-scope:7",
        };

        var parent = telemetry.Propagation.Extract(foreign);
        ITelemetrySpan? child = null;
        await parent.StartSpanAsync(new SpanOptions("child"), span =>
        {
            child = span;
            return Task.FromResult(true);
        });

        var recorded = Assert.Single(telemetry.GetSpans());
        // 另一个 scope 的 span 不在本地记录里，所以没有父 id。
        Assert.Null(recorded.ParentId);
        var carrier = telemetry.Propagation.Inject(child!);
        Assert.Equal("another-scope:7", carrier!["pi-memory-trace"]);
    }

    [Fact]
    public void NoopPropagationNeverInjects()
    {
        Assert.Null(NoopTelemetryPropagation.Instance.Inject(NoopTelemetryContext.Instance));
        Assert.Same(
            NoopTelemetryContext.Instance,
            NoopTelemetryPropagation.Instance.Extract(new TelemetryCarrier { ["pi-memory-scope"] = "x" }));
    }

    [Fact]
    public async Task LinksResolveToTheTargetTraceAndSpan()
    {
        var telemetry = new InMemoryTelemetryContext();
        ITelemetrySpan? parent = null;
        await telemetry.StartSpanAsync(new SpanOptions("parent"), span =>
        {
            parent = span;
            return Task.FromResult(true);
        });

        await telemetry.StartSpanAsync(new SpanOptions("child"), span =>
        {
            // 外部只给上下文；id 由适配器自己解。
            span.AddLink(new SpanLink(parent!));
            return Task.FromResult(true);
        });

        var spans = telemetry.GetSpans();
        var parentSpan = Assert.Single(spans, span => span.Name == "parent");
        var child = Assert.Single(spans, span => span.Name == "child");
        var link = Assert.Single(child.Links!);
        Assert.Equal(parentSpan.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), link.SpanId);
        Assert.False(string.IsNullOrEmpty(link.TraceId));
    }

    [Fact]
    public async Task UnknownLinkTargetsAreDropped()
    {
        var telemetry = new InMemoryTelemetryContext();
        var foreign = new InMemoryTelemetryContext();
        ITelemetrySpan? foreignSpan = null;
        await foreign.StartSpanAsync(new SpanOptions("elsewhere"), span =>
        {
            foreignSpan = span;
            return Task.FromResult(true);
        });

        await telemetry.StartSpanAsync(new SpanOptions("child"), span =>
        {
            // 另一个适配器实例的上下文：本适配器不认识它，安静丢弃。
            span.AddLink(new SpanLink(foreignSpan!));
            return Task.FromResult(true);
        });

        var child = Assert.Single(telemetry.GetSpans());
        Assert.Empty(child.Links ?? []);
    }
}
