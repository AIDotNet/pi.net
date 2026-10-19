using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Xunit;

namespace Pi.Durable.Tests;

public class HarnessSettingsSourceTests
{
    [Fact]
    public async Task AgentResolutionReadsTheCurrentSettingsSource()
    {
        var first = new TestExtension("first");
        var second = new TestExtension("second");
        var registry = Registry.CreateRegistry();
        registry.Install(first);
        registry.Install(second);

        var current = new HarnessSettings { Extensions = [first] };
        var harness = await Pi.Durable.Harness.Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
        {
            Models = new Models(),
            Registry = registry,
            Settings = new HarnessSettings { Extensions = [second] },
            SettingsSource = () => current,
        }, Context.Todo);

        try
        {
            var root = await harness.RootAsync(Context.Todo);
            var firstAgent = await root.AgentAsync(Context.Todo);
            Assert.Equal("first", Assert.Single(firstAgent.Extensions).Name);

            current = new HarnessSettings { Extensions = [second] };
            var secondAgent = await root.AgentAsync(Context.Todo);
            Assert.Equal("second", Assert.Single(secondAgent.Extensions).Name);
        }
        finally
        {
            await harness.CloseAsync(Context.Todo);
        }
    }

    private sealed class TestExtension(string name) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }
}
