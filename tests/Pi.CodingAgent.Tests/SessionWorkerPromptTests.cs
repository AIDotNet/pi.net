using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Tools;
using Pi.Durable.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class SessionWorkerPromptTests
{
    [Fact]
    public async Task CreatePiPrompt_RendersOrderedSectionsWithProjectResourcesAndToolContributions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-worker-prompt-{Guid.NewGuid():N}");
        var cwd = Path.Combine(root, "project");
        var agentDir = Path.Combine(root, "agent");
        var skillDir = Path.Combine(agentDir, "skills", "prompt-fixture");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "Project prompt fixture context.");
        File.WriteAllText(Path.Combine(agentDir, "AGENTS.md"), "Global prompt fixture context.");
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: prompt-fixture
            description: confirms production worker prompt resource loading
            ---
            Load this skill when validating the session worker prompt.
            """);

        var previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, agentDir);
        HarnessImpl? harness = null;
        try
        {
            var settingsManager = SettingsManager.Create(cwd, agentDir);
            var registry = Registry.CreateRegistry();
            registry.Install(CodingTools.Extension);
            registry.Install(SessionWorkerHarnessSetup.CreatePiPrompt(settingsManager, cwd));
            harness = await Harness.OpenAsync(
                new MemoryStorage(),
                new HarnessOptions { Models = new Models(), Registry = registry },
                Context.Todo);

            var conversation = await harness.RootAsync(
                Context.Todo, new AgentChange { Cwd = cwd });
            var agent = await conversation.AgentAsync(Context.Todo);
            var input = new PromptInput
            {
                ConversationId = DurableIdConstants.RootConversation,
                Agent = agent,
                Shown = new Dictionary<string, string>(),
                Read = harness,
            };
            var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var section in agent.Sections)
            {
                sections[section.Key] = await section.RenderAsync(input, Context.Todo);
            }

            Assert.Equal(
                new[] { "preamble", "tools", "rules", "docs", "project_context", "skills", "cwd" },
                agent.Sections.Select(section => section.Key));
            Assert.Contains("- read:", sections["tools"]);
            Assert.Contains("- bash:", sections["tools"]);
            Assert.Contains("Project prompt fixture context.", sections["project_context"]);
            Assert.Contains("Global prompt fixture context.", sections["project_context"]);
            Assert.Contains("prompt-fixture", sections["skills"]);
            Assert.Contains("confirms production worker prompt resource loading", sections["skills"]);
            Assert.Contains(Path.GetFullPath(cwd).Replace('\\', '/'), sections["cwd"]);
        }
        finally
        {
            if (harness is not null) await harness.CloseAsync(Context.Todo);
            Environment.SetEnvironmentVariable(Config.EnvAgentDir, previousAgentDir);
            Directory.Delete(root, recursive: true);
        }
    }
}
