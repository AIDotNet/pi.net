using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Path = Pi.Chord.Delta.Path;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>
/// The wire-form → protocol value tree conversions (4g-3d-4a): the protocol layer speaks value
/// trees while the endpoint answers with decoded records, so the catalogue, the subscribe
/// snapshot and the updates cross as their wire form. These tests round-trip each conversion
/// through the wire parsers and pin the integral payloads — a long/double conditional arm would
/// silently widen the integers the parsers cast back to long.
/// </summary>
public class ServiceWireToValueTests
{
    private static readonly ServiceInstanceAddress Addr = new("inst-a", 1L);

    private sealed record Summary(string ServerId, string SessionId, double CreatedAt);

    private sealed record Directory(long Revision, IReadOnlyList<Summary> Sessions);

    private static WireServiceSubscriptionSnapshot SampleSnapshot() => new("pi.session-directory",
        ServiceMode.Singleton,
        [
            new WireServiceInstanceSnapshot(Addr,
            [
                new WireServiceMemberSnapshot.State("state", 2L,
                    [["r", new Directory(7L, [new Summary("server-1", "s1", 1)])]]),
                new WireServiceMemberSnapshot.Method("read"),
            ]),
        ]);

    [Fact]
    public void SnapshotValue_RoundTripsThroughTheWireParser()
    {
        var value = ServiceWire.ToValue(SampleSnapshot());

        var wire = ServiceWire.ParseWireServiceSubscriptionSnapshot(value);
        Assert.Equal("pi.session-directory", wire.ServiceId);
        Assert.Equal(ServiceMode.Singleton, wire.Mode);
        var instance = Assert.Single(wire.Instances);
        Assert.Equal(Addr, instance.Instance);
        Assert.Equal(2, instance.Members.Count);
        var state = Assert.IsType<WireServiceMemberSnapshot.State>(instance.Members[0]);
        Assert.Equal("state", state.Name);
        Assert.Equal(2L, state.Sequence);

        // The decoded payload keeps its integral fields as longs.
        var decoded = new ServiceStateDecoder().DecodeSnapshot(wire);
        var decodedState = Assert.IsType<ServiceMemberSnapshot.State>(
            Assert.Single(Assert.Single(decoded.Instances).Members.OfType<ServiceMemberSnapshot.State>()));
        var replace = Assert.IsType<DeltaOp.Replace>(Assert.Single(decodedState.Ops));
        var directory = Assert.IsType<Dictionary<string, object?>>(replace.Value);
        Assert.IsType<long>(directory["revision"]);
        Assert.Equal(7L, directory["revision"]);
    }

    [Fact]
    public void UpdateValue_RoundTripsThroughTheWireParser()
    {
        // The subscription hydrates from its snapshot first, as the client and the manager do.
        var decoder = new ServiceStateDecoder();
        _ = decoder.DecodeSnapshot(SampleSnapshot());

        var update = new WireServiceProviderUpdate.StateUpdate(Addr, "state", 3L,
            [["s", new List<object?> { "revision" }, 4L]]);
        var value = ServiceWire.ToValue(update);

        var wire = ServiceWire.ParseWireServiceProviderUpdate(value);
        var state = Assert.IsType<WireServiceProviderUpdate.StateUpdate>(wire);
        Assert.Equal(Addr, state.Instance);
        Assert.Equal("state", state.Member);
        Assert.Equal(3L, state.Sequence);

        var decoded = Assert.IsType<ServiceProviderUpdate.StateUpdate>(
            decoder.DecodeUpdate(wire));
        var set = Assert.IsType<DeltaOp.Set>(Assert.Single(decoded.Ops));
        Assert.Equal(new Path([Seg.Key("revision")]), set.Path);
        Assert.Equal(4L, set.Value);
    }

    [Fact]
    public void CatalogueValue_RoundTripsThroughTheWireParser()
    {
        var entries = new ServiceCatalogueEntry[]
        {
            new("pi.session-directory", ServiceMode.Singleton),
            new("pi.tasks", ServiceMode.Keyed),
        };

        var value = ServiceWire.ToValue(entries);

        var wire = ServiceWire.ParseServiceCatalogue(value);
        Assert.Equal(2, wire.Count);
        Assert.Equal("pi.session-directory", wire[0].ServiceId);
        Assert.Equal(ServiceMode.Singleton, wire[0].Mode);
        Assert.Equal(ServiceMode.Keyed, wire[1].Mode);
    }

    [Fact]
    public void UpdateValue_CarriesTheInstanceOnlyWhenItHasOne()
    {
        var without = ServiceWire.ToValue(
            new WireServiceProviderUpdate.StateUpdate(null, "state", 1L, [["r", 1L]]));
        Assert.False(without.ContainsKey("instance"));

        var with = ServiceWire.ToValue(
            new WireServiceProviderUpdate.StateUpdate(Addr, "state", 1L, [["r", 1L]]));
        var address = Assert.IsType<Dictionary<string, object?>>(with["instance"]);
        Assert.Equal("inst-a", address["key"]);
        Assert.Equal(1L, address["generation"]);
    }

    [Fact]
    public void ResetValue_KeepsItsSnapshot()
    {
        var value = ServiceWire.ToValue(
            new WireServiceProviderUpdate.Reset(SampleSnapshot()));
        Assert.Equal("reset", value["type"]);

        var wire = ServiceWire.ParseWireServiceProviderUpdate(value);
        var reset = Assert.IsType<WireServiceProviderUpdate.Reset>(wire);
        Assert.Equal("pi.session-directory", reset.Snapshot.ServiceId);
    }

    [Fact]
    public void CatalogueValue_MatchesTheJSONShapeTSSends()
    {
        var value = ServiceWire.ToValue([new ServiceCatalogueEntry("fs", ServiceMode.Singleton)]);
        var entry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(value));
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { ["serviceId"] = "fs", ["mode"] = "singleton" },
            JsonSerializer.SerializeToNode(entry)));
    }
}
