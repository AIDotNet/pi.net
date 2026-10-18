// ============================================================================
// Slash command service provider — port of experimental/services/slash-commands-provider.ts (4g-3b)
// ============================================================================
//
// Two facets: the runtime facet that owns the command registry, and the built-in facet that registers
// the /model, /thinking, /compact and /reload commands. The registry stages same-name replacements so
// a reload never leaves a command missing: the head of each name's entry list is what `list()` shows,
// and a retiring entry is only dropped once its own handle is disposed.
//
// TS builds the facets with `defineFacet({ id, setup })`; C# implements `IFacet` directly (C317).
// TS resolves sibling services with `env.use(...)` inside `setup`; the C# facet environment splits that
// into `Require` (declaration, in Setup) plus `Use` (resolution, in OnActivate).

using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Facets;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Port of <c>createSlashCommandsRuntimeFacet</c> / <c>createBuiltInSlashCommandsFacet</c>.</summary>
public static class SlashCommandsProvider
{
    /// <summary>
    /// The facet that owns the command registry (TS <c>@pi/slash-commands-runtime</c>). The caller may
    /// pass a registry to share one across facets, exactly like the TS default parameter.
    /// </summary>
    public static IFacet CreateSlashCommandsRuntimeFacet(ISlashCommands? registry = null)
        => new RuntimeFacet(registry ?? new SlashCommandRegistry());

    /// <summary>
    /// The facet that registers the built-in commands (TS <c>@pi/slash-commands-builtin</c>).
    /// <paramref name="reloadPresentationPlugins"/> is TS <c>options.reloadPresentationPlugins</c>: the
    /// presentation-side half of a reload, fed the value the server-side plugins answer with.
    /// </summary>
    public static IFacet CreateBuiltInSlashCommandsFacet(Func<object?, Task> reloadPresentationPlugins)
        => new BuiltInFacet(reloadPresentationPlugins);

    // ---------------------------------------------------------------- decision leaves

    /// <summary>
    /// TS <c>exactModel</c>: an empty query matches nothing; otherwise the query must equal
    /// <c>provider/modelId</c> or the bare model id (case-insensitively), and exactly one model may
    /// match — two matches are as good as none.
    /// </summary>
    internal static ModelSummary? ExactModel(IReadOnlyList<ModelSummary> models, string query)
    {
        if (query.Length == 0) return null;
        var normalized = query.ToLowerInvariant();
        ModelSummary? found = null;
        var matches = 0;
        foreach (var model in models)
        {
            var qualified = $"{model.Provider}/{model.ModelId}".ToLowerInvariant();
            if (qualified != normalized && !string.Equals(model.ModelId.ToLowerInvariant(), normalized, StringComparison.Ordinal))
            {
                continue;
            }

            matches += 1;
            if (matches == 1) found = model;
        }

        return matches == 1 ? found : null;
    }

    /// <summary>TS <c>THINKING_DESCRIPTIONS</c>: the one-line hint per level.</summary>
    internal static string ThinkingDescription(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "No reasoning",
        ThinkingLevel.Minimal => "Very brief reasoning",
        ThinkingLevel.Low => "Light reasoning",
        ThinkingLevel.Medium => "Moderate reasoning",
        ThinkingLevel.High => "Deep reasoning",
        ThinkingLevel.XHigh => "Extra-high reasoning",
        _ => "Maximum reasoning",
    };

    /// <summary>
    /// The qualified form both the picker and <see cref="ExactModel"/> use: <c>provider/modelId</c>.
    /// </summary>
    internal static string Qualified(ModelRef model) => $"{model.Provider}/{model.ModelId}";

    /// <summary>TS <c>#validate</c>: the name pattern, rejecting anything else.</summary>
    internal static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9:-]*$", RegexOptions.Compiled);

    internal static void ValidateName(ISlashCommandContribution command)
    {
        if (!NamePattern.IsMatch(command.Name))
        {
            // TS throws a TypeError here; the port uses ArgumentException for rejected arguments
            // (the convention `SessionEntries.AssertValidSessionId` follows).
            throw new ArgumentException($"Invalid slash command name: {command.Name}");
        }
    }

    /// <summary>TS <c>@pi/slash-commands-runtime</c>: provides the registry.</summary>
    private sealed class RuntimeFacet(ISlashCommands registry) : IFacet
    {
        public string Id => "@pi/slash-commands-runtime";

        public void Setup(IFacetEnvironment environment)
            => environment.Provide(SlashCommands.Service, registry);
    }

    /// <summary>TS <c>env.own(handle)</c> for an unsubscribe handle (TS's <c>own</c> takes either).</summary>
    private static void Own(IFacetEnvironment environment, IDisposable handle)
        => environment.Own(() =>
        {
            handle.Dispose();
            return Task.CompletedTask;
        });

    /// <summary>TS <c>@pi/slash-commands-builtin</c>: registers the four built-in commands.</summary>
    private sealed class BuiltInFacet(Func<object?, Task> reloadPresentationPlugins) : IFacet
    {
        public string Id => "@pi/slash-commands-builtin";

        public void Setup(IFacetEnvironment environment)
        {
            // TS resolves these with env.use(...) during setup; C# declares the dependencies here and
            // resolves them once the facet graph is assembled.
            environment.Require(SlashCommands.Service);
            environment.Require(Models.Service);
            environment.Require(AgentController.Service);
            environment.Require(PresentationUI.Service);
            environment.Require(PresentationPlugins.Service);
            environment.Require(SessionPlugins.Service);
            environment.OnActivate(() =>
            {
                var commands = environment.Use(SlashCommands.Service);
                var models = environment.Use(Models.Service);
                var controller = environment.Use(AgentController.Service);
                var ui = environment.Use(PresentationUI.Service);
                var presentationPlugins = environment.Use(PresentationPlugins.Service);
                var sessionPlugins = environment.Use(SessionPlugins.Service);

                // TS: env.own(commands.replace(…)) — own takes the unsubscribe handle.
                Own(environment, commands.Replace(new ModelCommand(models, ui)));
                Own(environment, commands.Replace(new ThinkingCommand(models, ui)));
                Own(environment, commands.Replace(new CompactCommand(controller, ui)));
                Own(environment, commands.Replace(new ReloadCommand(
                    presentationPlugins, sessionPlugins, ui, reloadPresentationPlugins)));
                return Task.CompletedTask;
            });
        }
    }

    // ---------------------------------------------------------------- built-in commands

    private sealed class ModelCommand : ISlashCommandContribution
    {
        private readonly IModels _models;
        private readonly IPresentationUI _ui;
        private readonly Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>> _completions;

        public ModelCommand(IModels models, IPresentationUI ui)
        {
            _models = models;
            _ui = ui;
            _completions = Completions;
        }

        public string Name => "model";

        public string? Description => "Select model";

        public string? ArgumentHint => "<provider/model>";

        public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions => _completions;

        private Task<IReadOnlyList<SlashCommandCompletion>?> Completions(string prefix)
        {
            var normalized = prefix.ToLowerInvariant();
            var available = _models.State.Value?.Catalog.AvailableModels ?? [];
            var completions = new List<SlashCommandCompletion>();
            foreach (var model in available)
            {
                // TS matches on "provider/modelId name" — the display name is searchable too.
                var haystack = $"{Qualified(model)} {model.Name}".ToLowerInvariant();
                if (haystack.Contains(normalized, StringComparison.Ordinal))
                {
                    completions.Add(new SlashCommandCompletion(
                        Qualified(model), model.ModelId, model.Provider));
                }
            }

            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>?>(completions);
        }

        public async Task<AgentSubmissionResponse?> RunAsync(string args, Context context)
        {
            var state = _models.State.Value;
            if (state is null)
            {
                throw new InvalidOperationException("Models service is not ready");
            }

            var selected = ExactModel(state.Catalog.AvailableModels, args);
            if (args.Length > 0 && selected is null)
            {
                throw new InvalidOperationException($"Unknown model: {args}");
            }

            if (selected is null)
            {
                var configurationModel = state.Configuration.Model;
                var items = new List<PresentationSelectItem>();
                foreach (var model in state.Catalog.AvailableModels)
                {
                    var isSelected = configurationModel is not null
                        && configurationModel.Provider == model.Provider
                        && configurationModel.ModelId == model.ModelId;
                    items.Add(new PresentationSelectItem(
                        Qualified(model),
                        isSelected ? $"{model.Name} (selected)" : model.Name,
                        Qualified(model)));
                }

                var value = await _ui.SelectAsync(
                    "Select model:",
                    items,
                    configurationModel is null ? null : Qualified(configurationModel),
                    context).ConfigureAwait(false);
                if (value is null) return null;
                selected = ExactModel(state.Catalog.AvailableModels, value);
                if (selected is null)
                {
                    throw new InvalidOperationException($"Unknown model: {value}");
                }
            }

            await _models.SelectAsync(selected, context).ConfigureAwait(false);
            _ui.ShowStatus($"Selected {Qualified(selected)}.", context);
            return null;
        }
    }

    private sealed class ThinkingCommand(IModels models, IPresentationUI ui) : ISlashCommandContribution
    {
        public string Name => "thinking";

        public string? Description => "Set thinking level";

        public string? ArgumentHint => "<level>";

        public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions => null;

        public async Task<AgentSubmissionResponse?> RunAsync(string args, Context context)
        {
            var levels = await models.GetThinkingLevelsAsync(context).ConfigureAwait(false);
            var texts = new List<string>(levels.Count);
            foreach (var level in levels)
            {
                texts.Add(ThinkingLevelText.ToText(level));
            }

            // TS: levels.find(level => level === args.toLowerCase()) — the argument is case-insensitive.
            var normalized = args.ToLowerInvariant();
            var selected = texts.Contains(normalized, StringComparer.Ordinal)
                ? normalized
                : null;
            if (args.Length > 0 && selected is null)
            {
                throw new InvalidOperationException(
                    $"Unknown thinking level \"{args}\". Available levels: {string.Join(", ", texts)}.");
            }

            if (selected is null)
            {
                var current = models.State.Value?.Configuration.ThinkingLevel;
                var currentText = current is { } level ? ThinkingLevelText.ToText(level) : null;
                var items = new List<PresentationSelectItem>();
                foreach (var text in texts)
                {
                    items.Add(new PresentationSelectItem(
                        text,
                        currentText == text ? $"{text} (selected)" : text,
                        ThinkingDescription(ThinkingLevelText.Parse(text) ?? ThinkingLevel.Off)));
                }

                var value = await ui.SelectAsync(
                    "Select thinking level:",
                    items,
                    currentText,
                    context).ConfigureAwait(false);
                if (value is null) return null;
                selected = texts.Contains(value, StringComparer.Ordinal) ? value : null;
                if (selected is null)
                {
                    throw new InvalidOperationException($"Unknown thinking level: {value}");
                }
            }

            await models.SelectThinkingAsync(
                ThinkingLevelText.Parse(selected) ?? ThinkingLevel.Off, context).ConfigureAwait(false);
            ui.ShowStatus($"Thinking level: {selected}.", context);
            return null;
        }
    }

    private sealed class CompactCommand(IAgentController controller, IPresentationUI ui) : ISlashCommandContribution
    {
        public string Name => "compact";

        public string? Description => "Manually compact the session context";

        public string? ArgumentHint => "<instructions>";

        public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions => null;

        public async Task<AgentSubmissionResponse?> RunAsync(string args, Context context)
        {
            ui.ShowStatus("Compacting…", context);
            var request = new AgentCompactionRequest(args.Length == 0 ? null : args);
            return await controller.CompactAsync(request, context).ConfigureAwait(false);
        }
    }

    private sealed class ReloadCommand(
        IPresentationPlugins presentationPlugins,
        ISessionPlugins sessionPlugins,
        IPresentationUI ui,
        Func<object?, Task> reloadPresentationPlugins) : ISlashCommandContribution
    {
        public string Name => "reload";

        public string? Description => "Reload server-selected plugins";

        public string? ArgumentHint => null;

        public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions => null;

        public async Task<AgentSubmissionResponse?> RunAsync(string args, Context context)
        {
            ui.ShowStatus("Reloading plugins…", context);
            var data = await presentationPlugins.ReloadAsync(context).ConfigureAwait(false);
            await sessionPlugins.ReloadAsync(context).ConfigureAwait(false);
            await reloadPresentationPlugins(data).ConfigureAwait(false);
            ui.ShowStatus("Reloaded plugins.", context);
            return null;
        }
    }
}

// ============================================================================
// SlashCommandRegistry — port of the class of the same name (4g-3b)
// ============================================================================

/// <summary>
/// The command registry TS exports from the same module. Registration is per name: the first entry of
/// a name's list is the visible one, and <see cref="ISlashCommands.Replace"/> stages a successor that
/// takes over once the retiring handle is disposed — so a reload never leaves a command missing.
/// </summary>
public sealed class SlashCommandRegistry : ISlashCommands
{
    private static readonly StringComparer NameComparer = StringComparer.Ordinal;

    private readonly Dictionary<string, List<Entry>> _commands = new(NameComparer);

    // A Set in TS: at most one entry per listener, iterated in insertion order.
    private readonly List<Action<IReadOnlyList<ISlashCommandContribution>>> _listeners = [];

    public IDisposable Register(ISlashCommandContribution command)
    {
        SlashCommandsProvider.ValidateName(command);
        if (_commands.ContainsKey(command.Name))
        {
            throw new InvalidOperationException($"Slash command /{command.Name} is already registered");
        }

        return Add(command);
    }

    public IDisposable Replace(ISlashCommandContribution command)
    {
        SlashCommandsProvider.ValidateName(command);
        return Add(command);
    }

    public IReadOnlyList<ISlashCommandContribution> List()
    {
        var commands = new List<ISlashCommandContribution>(_commands.Count);
        foreach (var entries in _commands.Values)
        {
            commands.Add(entries[0].Command);
        }

        return commands;
    }

    public IDisposable Subscribe(Action<IReadOnlyList<ISlashCommandContribution>> listener)
    {
        if (!_listeners.Contains(listener))
        {
            _listeners.Add(listener);
        }

        // TS hydrates the new listener with the current list.
        listener(List());
        return new Unsubscription(() => _listeners.Remove(listener));
    }

    private IDisposable Add(ISlashCommandContribution command)
    {
        if (!_commands.TryGetValue(command.Name, out var entries))
        {
            entries = [];
            _commands[command.Name] = entries;
        }

        var entry = new Entry(command);
        entries.Add(entry);
        // TS publishes only when this is the first entry for the name: a staged replacement is
        // invisible until it takes over.
        if (entries.Count == 1)
        {
            Publish();
        }

        return new Unsubscription(() => Retire(command.Name, entries, entry));
    }

    private void Retire(string name, List<Entry> entries, Entry entry)
    {
        if (entry.Closed) return;
        entry.Closed = true;
        if (!ReferenceEquals(entries[0], entry)) return;

        // Drop the closed head, plus any successors that retired while it was still visible.
        while (entries.Count > 0 && entries[0].Closed)
        {
            entries.RemoveAt(0);
        }

        if (entries.Count == 0)
        {
            _commands.Remove(name);
        }

        Publish();
    }

    private void Publish()
    {
        var commands = List();
        // TS iterates the live Set: a listener an earlier one unsubscribes during this pass is not
        // visited. Iterating a copy but skipping whoever left reproduces that (and keeps the pass safe
        // against a concurrent Add).
        foreach (var listener in _listeners.ToArray())
        {
            if (_listeners.Contains(listener))
            {
                listener(commands);
            }
        }
    }

    private sealed class Entry(ISlashCommandContribution command)
    {
        public ISlashCommandContribution Command { get; } = command;

        public bool Closed { get; set; }
    }

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
