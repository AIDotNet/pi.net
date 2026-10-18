using Pi.Ai.Types;
using Pi.CodingAgent.Experimental.Services;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the slash-command provider (4g-3b): the registry's staging semantics, the
/// name validation, and the two pure leaves TS keeps at module level.
/// </summary>
public class SlashCommandsProviderTests
{
    private static ModelSummary Summary(string provider, string id, string name)
        => new(provider, id, name, Reasoning: false);

    private static ISlashCommandContribution Command(string name) => new FakeCommand(name);

    // ---------------------------------------------------------------- exactModel

    [Fact]
    public void ExactModel_EmptyQueryMatchesNothing()
        => Assert.Null(SlashCommandsProvider.ExactModel([Summary("openai", "gpt-5", "GPT-5")], ""));

    [Fact]
    public void ExactModel_MatchesTheQualifiedFormCaseInsensitively()
    {
        var models = new[] { Summary("openai", "gpt-5", "GPT-5"), Summary("faux", "faux-1", "Faux") };

        var selected = SlashCommandsProvider.ExactModel(models, "OpenAI/GPT-5");

        Assert.NotNull(selected);
        Assert.Equal("faux-1", SlashCommandsProvider.ExactModel(models, "FAUX-1")!.ModelId);
        Assert.Equal("gpt-5", selected.ModelId);
    }

    [Fact]
    public void ExactModel_AmbiguousMatchIsNoMatch()
    {
        // Both the qualified form of one and the bare id of another normalize to the same query.
        var models = new[] { Summary("a", "x", "X"), Summary("b", "a/x", "Y") };

        Assert.Null(SlashCommandsProvider.ExactModel(models, "a/x"));
    }

    [Fact]
    public void ExactModel_BareIdMustBeUnique()
    {
        var models = new[] { Summary("a", "same", "X"), Summary("b", "same", "Y") };

        Assert.Null(SlashCommandsProvider.ExactModel(models, "same"));
    }

    [Fact]
    public void ExactModel_SubstringIsNotAMatch()
        => Assert.Null(SlashCommandsProvider.ExactModel([Summary("openai", "gpt-5", "GPT-5")], "gpt"));

    // ---------------------------------------------------------------- thinking descriptions

    [Theory]
    [InlineData(ThinkingLevel.Off, "No reasoning")]
    [InlineData(ThinkingLevel.Minimal, "Very brief reasoning")]
    [InlineData(ThinkingLevel.Low, "Light reasoning")]
    [InlineData(ThinkingLevel.Medium, "Moderate reasoning")]
    [InlineData(ThinkingLevel.High, "Deep reasoning")]
    [InlineData(ThinkingLevel.XHigh, "Extra-high reasoning")]
    [InlineData(ThinkingLevel.Max, "Maximum reasoning")]
    public void ThinkingDescription_CoversEveryLevel(ThinkingLevel level, string expected)
        => Assert.Equal(expected, SlashCommandsProvider.ThinkingDescription(level));

    // ---------------------------------------------------------------- name validation

    [Theory]
    [InlineData("model")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("my-command")]
    [InlineData("my:command")]
    [InlineData("a1:b2-c3")]
    public void ValidNames_AreAccepted(string name)
    {
        var registry = new SlashCommandRegistry();

        registry.Register(Command(name));

        Assert.Equal(name, Assert.Single(registry.List()).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Model")]        // uppercase
    [InlineData("-model")]       // leading hyphen
    [InlineData(":model")]       // leading colon
    [InlineData("my_command")]   // underscore
    [InlineData("my command")]   // space
    [InlineData("model/")]       // slash
    [InlineData("modél")]        // non-ASCII
    public void InvalidNames_AreRejected(string name)
    {
        var registry = new SlashCommandRegistry();

        // TS throws a TypeError; the port maps it to ArgumentException.
        Assert.Throws<ArgumentException>(() => registry.Register(Command(name)));
        Assert.Throws<ArgumentException>(() => registry.Replace(Command(name)));
        Assert.Empty(registry.List());
    }

    // ---------------------------------------------------------------- register / replace

    [Fact]
    public void Register_ThenList_ShowsTheCommand()
    {
        var registry = new SlashCommandRegistry();

        using var handle = registry.Register(Command("model"));

        var command = Assert.IsType<FakeCommand>(Assert.Single(registry.List()));
        Assert.Equal("model", command.Name);
        Assert.False(command.Disposed);
    }

    [Fact]
    public void Register_RejectsADuplicateName()
    {
        var registry = new SlashCommandRegistry();
        using var first = registry.Register(Command("model"));

        var error = Assert.Throws<InvalidOperationException>(() => registry.Register(Command("model")));

        Assert.Contains("/model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_DisposeRemovesTheCommand()
    {
        var registry = new SlashCommandRegistry();
        var handle = registry.Register(Command("model"));

        handle.Dispose();

        Assert.Empty(registry.List());
    }

    [Fact]
    public void Register_DisposeIsIdempotent()
    {
        var registry = new SlashCommandRegistry();
        var handle = registry.Register(Command("model"));

        handle.Dispose();
        handle.Dispose();

        Assert.Empty(registry.List());
    }

    [Fact]
    public void Replace_StagesASuccessorWithoutHidingTheCurrentOne()
    {
        var registry = new SlashCommandRegistry();
        var original = Command("model");
        using var first = registry.Register(original);
        using var staged = registry.Replace(Command("model"));

        // The staged replacement is invisible until the retiring handle is disposed.
        Assert.Same(original, Assert.Single(registry.List()));
    }

    [Fact]
    public void Replace_WithoutARegistrationBecomesVisible()
    {
        var registry = new SlashCommandRegistry();

        using var handle = registry.Replace(Command("model"));

        Assert.Equal("model", Assert.Single(registry.List()).Name);
    }

    [Fact]
    public void Replace_TakesOverWhenTheRetiringHandleIsDisposed()
    {
        var registry = new SlashCommandRegistry();
        var original = Command("model");
        var successor = Command("model");
        var first = registry.Register(original);
        var staged = registry.Replace(successor);

        first.Dispose();

        Assert.Same(successor, Assert.Single(registry.List()));
        staged.Dispose();
        Assert.Empty(registry.List());
    }

    [Fact]
    public void Replace_DisposingTheSuccessorFirstLeavesTheOriginalVisible()
    {
        var registry = new SlashCommandRegistry();
        var original = Command("model");
        var first = registry.Register(original);
        var staged = registry.Replace(Command("model"));

        staged.Dispose();

        Assert.Same(original, Assert.Single(registry.List()));
        first.Dispose();
        Assert.Empty(registry.List());
    }

    [Fact]
    public void Replace_ThreeGenerationsRetireInOrder()
    {
        var registry = new SlashCommandRegistry();
        var oldest = Command("model");
        var middle = Command("model");
        var newest = Command("model");
        var first = registry.Register(oldest);
        var second = registry.Replace(middle);
        var third = registry.Replace(newest);

        second.Dispose(); // not the head: nothing changes
        Assert.Same(oldest, Assert.Single(registry.List()));

        first.Dispose(); // the head retires; the closed successor is drained with it
        Assert.Same(newest, Assert.Single(registry.List()));

        third.Dispose();
        Assert.Empty(registry.List());
    }

    [Fact]
    public void List_KeepsTheRegistrationOrder()
    {
        var registry = new SlashCommandRegistry();
        using var a = registry.Register(Command("alpha"));
        using var b = registry.Register(Command("beta"));
        using var c = registry.Register(Command("gamma"));

        Assert.Equal(["alpha", "beta", "gamma"], registry.List().Select(command => command.Name));
    }

    // ---------------------------------------------------------------- subscribe

    [Fact]
    public void Subscribe_HydratesImmediately()
    {
        var registry = new SlashCommandRegistry();
        using var registered = registry.Register(Command("model"));
        var seen = new List<IReadOnlyList<string>>();

        using var subscription = registry.Subscribe(commands => seen.Add(commands.Select(c => c.Name).ToList()));

        var hydrated = Assert.Single(seen);
        Assert.Equal(["model"], hydrated);
    }

    [Fact]
    public void Subscribe_PublishesWhenANameAppears()
    {
        var registry = new SlashCommandRegistry();
        var notifications = 0;
        using var subscription = registry.Subscribe(_ => notifications += 1); // hydrate

        using var first = registry.Register(Command("model"));
        using var second = registry.Register(Command("thinking"));
        using var staged = registry.Replace(Command("model")); // staged: no notification

        Assert.Equal(3, notifications);

        first.Dispose(); // the head retires: one more
        Assert.Equal(4, notifications);
    }

    [Fact]
    public void Subscribe_StopsAfterUnsubscribe()
    {
        var registry = new SlashCommandRegistry();
        var notifications = 0;
        var subscription = registry.Subscribe(_ => notifications += 1);

        subscription.Dispose();
        using var registered = registry.Register(Command("model"));

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Subscribe_SameListenerTwiceIsOneSubscription()
    {
        // TS uses a Set and hydrates on every subscribe call: two subscribes notify twice, but the
        // single registration afterwards notifies once (a duplicated listener would make it twice).
        var registry = new SlashCommandRegistry();
        var notifications = 0;
        Action<IReadOnlyList<ISlashCommandContribution>> listener = _ => notifications += 1;

        using var first = registry.Subscribe(listener);
        using var second = registry.Subscribe(listener);
        Assert.Equal(2, notifications);

        using var registered = registry.Register(Command("model"));
        Assert.Equal(3, notifications);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Subscribe_RemovingDuringPublishVisitsTheRestOfTheSnapshot()
    {
        // TS iterates the live Set: the listener that unsubscribes itself has already been entered,
        // and the iterator continues with the remaining listeners. The port snapshots the listener
        // list, which visits the same set for this shape.
        var registry = new SlashCommandRegistry();
        var visited = new List<string>();
        IDisposable? second = null;
        second = registry.Subscribe(_ =>
        {
            visited.Add("second");
            second?.Dispose();
        });
        using var first = registry.Subscribe(_ => visited.Add("first"));
        visited.Clear();

        using var registered = registry.Register(Command("model"));

        Assert.Equal(["second", "first"], visited);
    }

    [Fact]
    public void Subscribe_AListenerRemovedByAnEarlierOneIsNotVisited()
    {
        // TS iterates the live Set, so unsubscribing another listener during the pass skips it.
        var registry = new SlashCommandRegistry();
        var visited = new List<string>();
        IDisposable? second = null;
        using var first = registry.Subscribe(_ =>
        {
            visited.Add("first");
            second?.Dispose();
        });
        second = registry.Subscribe(_ => visited.Add("second"));
        visited.Clear();

        using var registered = registry.Register(Command("model"));

        Assert.Equal(["first"], visited);
    }

    private sealed class FakeCommand(string name) : ISlashCommandContribution
    {
        public string Name { get; } = name;

        public string? Description => null;

        public string? ArgumentHint => null;

        public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions => null;

        public bool Disposed { get; private set; }

        public Task<AgentSubmissionResponse?> RunAsync(string args, Pi.Chord.Context.Context context)
            => Task.FromResult<AgentSubmissionResponse?>(null);

        public void Dispose() => Disposed = true;
    }
}
