using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Durable;
using Pi.CodingAgent.Experimental.Plugins;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Tools;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental;

internal static class SessionWorkerHarnessSetup
{
    private static readonly string[] PromptSectionKeys =
        ["preamble", "tools", "rules", "docs", "project_context", "skills", "cwd"];

    private static readonly IReadOnlyDictionary<string, ToolPromptContribution> ToolContributions =
        new Dictionary<string, ToolPromptContribution>(StringComparer.Ordinal)
        {
            ["read"] = new(Pi.CodingAgent.Core.Tools.ReadTool.Snippet, Pi.CodingAgent.Core.Tools.ReadTool.Guidelines),
            ["bash"] = new(Pi.CodingAgent.Core.Tools.BashTool.BashSnippet, Pi.CodingAgent.Core.Tools.BashTool.BashGuidelines),
            ["edit"] = new(Pi.CodingAgent.Core.Tools.EditTool.Snippet, Pi.CodingAgent.Core.Tools.EditTool.Guidelines),
            ["write"] = new(Pi.CodingAgent.Core.Tools.WriteTool.Snippet, Pi.CodingAgent.Core.Tools.WriteTool.Guidelines),
        };

    internal static async Task<SessionWorkerRuntime> CreateAsync(
        string databasePath, SessionWorkerOptions options)
    {
        var cwd = options.Metadata.Cwd;
        var modelRuntime = await ModelRuntime.CreateAsync().ConfigureAwait(false);
        var settingsManager = SettingsManager.Create(cwd);
        // TS 的 session-worker 直接复用 durable/harness-setup.ts 的三件，不另写一份。
        DurableHarnessSetup.ConfigureHarnessHttp(settingsManager);
        var envs = new ExecutionEnvs(cwd);
        HarnessImpl? harness = null;

        try
        {
            var storage = await SqliteStorage.OpenAsync(databasePath).ConfigureAwait(false);
            harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = modelRuntime.DurableModels,
                Registry = DurableHarnessSetup.CreateCodingRegistry(settingsManager, cwd),
                SettingsSource = () => CreateHarnessSettings(settingsManager),
                EnvFactory = envs.EnvAsync,
                OnReport = (error, _) => Console.Error.WriteLine(error),
            }, Context.Todo).ConfigureAwait(false);

            var created = await harness.ConversationAsync(
                DurableIdConstants.RootConversation, Context.Todo).ConfigureAwait(false) is null;
            var initial = created
                ? await FindInitialAgentModelAsync(settingsManager, modelRuntime, options).ConfigureAwait(false)
                : null;
            var conversation = await harness.RootAsync(Context.Todo, new AgentChange
            {
                Cwd = cwd,
                Model = initial?.Model,
                ThinkingLevel = initial?.ThinkingLevel,
            }).ConfigureAwait(false);

            return new SessionWorkerRuntime(
                harness,
                conversation,
                modelRuntime,
                settingsManager,
                PluginBundles.CreateSessionPluginFacetLoader(options.PluginManifestPaths),
                envs.CleanupAsync);
        }
        catch (Exception error)
        {
            var cleanupErrors = new List<Exception>();
            if (harness is not null)
            {
                try
                {
                    await harness.CloseAsync(Context.Todo).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
            }

            try
            {
                await envs.CleanupAsync(Context.Todo).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            if (cleanupErrors.Count > 0)
            {
                var failures = new List<Exception> { error };
                failures.AddRange(cleanupErrors);
                throw new AggregateException("Session worker Harness startup and cleanup failed", failures);
            }

            throw;
        }
    }

    internal static HarnessSettings CreateHarnessSettings(SettingsManager settingsManager)
    {
        var provider = settingsManager.GetProviderRetrySettings();
        var idleTimeout = settingsManager.GetHttpIdleTimeoutMs();
        var compaction = settingsManager.GetCompactionSettings();
        var retry = settingsManager.GetRetrySettings();

        return new HarnessSettings
        {
            Stream = new ConversationStreamOptions
            {
                TimeoutMs = ToInt32(
                    provider.TimeoutMs ?? (idleTimeout == 0 ? int.MaxValue : idleTimeout),
                    "HTTP timeout"),
                MaxRetryDelayMs = ToInt32(provider.MaxRetryDelayMs, "provider retry max delay"),
                MaxRetries = provider.MaxRetries,
            },
            Compaction = new CompactionPolicy
            {
                Enabled = compaction.Enabled,
                ReserveTokens = compaction.ReserveTokens,
                KeepRecentTokens = compaction.KeepRecentTokens,
                BackgroundTokens = AgentDocs.DefaultCompactionPolicy.BackgroundTokens,
            },
            Retry = new ConversationRetryPolicy
            {
                Enabled = retry.Enabled,
                MaxRetries = retry.MaxRetries,
                BaseDelayMs = ToInt32(retry.BaseDelayMs, "retry base delay"),
                MaxAgentDelayMs = ToInt32(retry.MaxAgentDelayMs, "retry maximum agent delay"),
            },
            SteeringMode = ToHarnessQueueMode(settingsManager.GetSteeringMode()),
            FollowUpMode = ToHarnessQueueMode(settingsManager.GetFollowUpMode()),
        };
    }

    private static int ToInt32(long value, string setting)
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException($"The {setting} setting exceeds the Durable harness range.");
        }

        return (int)value;
    }

    private static Pi.Durable.Harness.QueueMode ToHarnessQueueMode(string mode)
        => QueueModes.Parse(mode) == Pi.Agent.Types.QueueMode.All
            ? Pi.Durable.Harness.QueueMode.All
            : Pi.Durable.Harness.QueueMode.OneAtATime;

    private static async Task<InitialAgentModel?> FindInitialAgentModelAsync(
        SettingsManager settingsManager, ModelRuntime modelRuntime, SessionWorkerOptions options)
    {
        if (options.Model is { } cliModel)
        {
            var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
            {
                CliProvider = options.Provider,
                CliModel = cliModel,
                Runtime = modelRuntime,
            });
            if (resolved.Error is not null)
            {
                throw new InvalidOperationException($"Could not resolve model: {resolved.Error}");
            }

            var model = resolved.Model
                ?? throw new InvalidOperationException($"Could not resolve model: {cliModel}");
            return new InitialAgentModel(
                new ModelRef(model.Provider, model.Id), resolved.ThinkingLevel);
        }

        var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
        {
            ScopedModels = [],
            IsContinuing = false,
            DefaultProvider = settingsManager.GetDefaultProvider(),
            DefaultModelId = settingsManager.GetDefaultModel(),
            DefaultThinkingLevel = ThinkingLevels.Parse(settingsManager.GetDefaultThinkingLevel()),
            Runtime = modelRuntime,
        }).ConfigureAwait(false);
        return initial.Model is { } selected
            ? new InitialAgentModel(
                new ModelRef(selected.Provider, selected.Id), initial.ThinkingLevel)
            : null;
    }

    internal static IExtension CreatePiPrompt(SettingsManager settingsManager, string fallbackCwd)
    {
        var resources = new ConcurrentDictionary<string, PromptResources>(StringComparer.Ordinal);
        var built = new ConditionalWeakTable<PromptInput, IReadOnlyDictionary<string, string>>();

        IReadOnlyDictionary<string, string> Build(PromptInput input)
            => built.GetValue(input, BuildSections);

        IReadOnlyDictionary<string, string> BuildSections(PromptInput input)
        {
            var cwd = input.Env?.Cwd ?? input.Agent.Cwd ?? fallbackCwd;
            var selectedTools = input.Agent.Tools.Select(tool => tool.Name).ToList();
            var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
            var guidelines = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var name in selectedTools)
            {
                if (!ToolContributions.TryGetValue(name, out var contribution)) continue;
                snippets[name] = contribution.Snippet;
                guidelines[name] = contribution.Guidelines;
            }

            var loaded = resources.GetOrAdd(cwd, path => LoadPromptResources(path, settingsManager));
            return SystemPrompt.BuildSystemPromptSections(new BuildSystemPromptOptions
            {
                Cwd = cwd,
                SelectedTools = selectedTools,
                ToolSnippets = snippets,
                ToolGuidelines = guidelines,
                ContextFiles = loaded.ContextFiles,
                Skills = loaded.Skills,
            });
        }

        var sections = PromptSectionKeys.Select(key =>
            Define.Section(
                key,
                (input, _) => Task.FromResult(Build(input).GetValueOrDefault(key)),
                tag: false)).ToList();
        return new PiPromptExtension(sections);
    }

    private static PromptResources LoadPromptResources(string cwd, SettingsManager settingsManager)
    {
        var agentDir = Config.GetAgentDir();
        var contextFiles = DefaultResourceLoader.LoadProjectContextFiles(cwd, agentDir);
        var skills = Skills.LoadSkills(new LoadSkillsOptions
        {
            Cwd = cwd,
            AgentDir = agentDir,
            SkillPaths = settingsManager.GetSkillPaths(),
            IncludeDefaults = true,
        }).Skills;
        return new PromptResources(contextFiles, skills);
    }

    private sealed record InitialAgentModel(ModelRef Model, ThinkingLevel? ThinkingLevel);

    private sealed record PromptResources(
        IReadOnlyList<ContextFile> ContextFiles, IReadOnlyList<Skill> Skills);

    private sealed record ToolPromptContribution(string Snippet, IReadOnlyList<string> Guidelines);

    private sealed class PiPromptExtension(IReadOnlyList<IPromptSection> sections) : IExtension
    {
        public string Name => "pi-prompt";

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => sections;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }
}
