// ============================================================================
// 4e-2d unit tests: Skills directory loading (gitignore matcher, discovery rules,
// validation/collisions), SessionExport JSONL shape, and the EstimateTokens
// toolsAdded branch (pinned algorithmically — its TS JSON.stringify length is not
// comparable to the C# ToolDefinition wire shape).
// ============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class CompactionSkillsExportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-skills-tests-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public CompactionSkillsExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover fixture directory is harmless.
        }
    }

    private string Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private string Dir(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // ─── skills: discovery ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadSkillsFromDir_SkillMdStopsRecursion()
    {
        Write("nested/SKILL.md", "---\nname: declared\ndescription: from skill.md\n---\nbody");
        Write("nested/deep/fallback.md", "---\ndescription: should not be reached\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("nested"), Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("declared", skill.Name);
        Assert.Equal(Path.Combine(Dir("nested"), "SKILL.md"), skill.FilePath);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LoadSkillsFromDir_RootMarkdownOnlyAtTopLevel()
    {
        Write("top.md", "---\nname: top\ndescription: root file\n---\nbody");
        Write("sub/SKILL.md", "---\nname: sub\ndescription: nested skill\n---\nbody");

        var included = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = _root, Source = "path" });
        Assert.Equal(["sub", "top"], [.. included.Skills.Select(s => s.Name)]);

        var excluded = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("sub"), Source = "path" });
        // includeRootFiles=false for recursion, but the public entry point scans a fresh root: the
        // sub directory has no root .md files, so only the nested SKILL.md loads.
        var skill = Assert.Single(excluded.Skills);
        Assert.Equal("sub", skill.Name);
    }

    [Fact]
    public void LoadSkillsFromDir_NameFallsBackToParentDirectory()
    {
        Write("my-dir/SKILL.md", "---\ndescription: no name in frontmatter\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("my-dir"), Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("my-dir", skill.Name);
    }

    [Fact]
    public void LoadSkillsFromDir_MissingDescription_DropsDeclaredSkillWithWarning()
    {
        Write("broken/SKILL.md", "---\nname: broken\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("broken"), Source = "path" });

        Assert.Empty(result.Skills);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ResourceDiagnosticType.Warning, diagnostic.Type);
        Assert.Equal("description is required", diagnostic.Message);
    }

    [Fact]
    public void LoadSkillsFromDir_RootMarkdownWithoutDescription_IsSilentlySkipped()
    {
        Write("silent.md", "---\nname: silent\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = _root, Source = "path" });

        Assert.Empty(result.Skills);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LoadSkillsFromDir_NameValidation_WarnsButStillLoads()
    {
        Write("bad-name/SKILL.md", "---\nname: Bad_Name\ndescription: loads anyway\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("bad-name"), Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("Bad_Name", skill.Name);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("invalid characters"));
    }

    [Fact]
    public void LoadSkillsFromDir_SkipsNodeModulesAndHiddenDirectories()
    {
        Write("node_modules/pkg/SKILL.md", "---\nname: dep\ndescription: dependency\n---\nbody");
        Write(".hidden/secret/SKILL.md", "---\nname: secret\ndescription: hidden\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = _root, Source = "path" });

        Assert.Empty(result.Skills);
    }

    [Fact]
    public void LoadSkillsFromDir_DisableModelInvocation()
    {
        Write("hidden/SKILL.md", "---\nname: hidden\ndescription: manual only\ndisable-model-invocation: true\n---\nbody");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("hidden"), Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.True(skill.DisableModelInvocation);
        // Prompt formatting hides it, leaving an empty output.
        Assert.Equal("", Skills.FormatSkillsForPrompt([skill]));
    }

    // ─── skills: gitignore matching ────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadSkillsFromDir_GitignoreFilters()
    {
        Write("ignored-dir/inner/SKILL.md", "---\nname: inner\ndescription: ignored dir\n---\nbody");
        Write("kept/SKILL.md", "---\nname: kept\ndescription: kept skill\n---\nbody");
        Write("partial.md", "---\nname: partial\ndescription: ignored file\n---\nbody");
        Write("kept/skip-me.md", "---\nname: skip\ndescription: ignored by pattern\n---\nbody");
        Write(".gitignore", "ignored-dir/\npartial.md\nkept/skip-me.md");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = _root, Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("kept", skill.Name);
    }

    [Fact]
    public void LoadSkillsFromDir_GitignoreNegationAndGlobs()
    {
        Write("vendor/a/SKILL.md", "---\nname: va\ndescription: vendor a\n---\nbody");
        Write("vendor/keep/SKILL.md", "---\nname: vk\ndescription: vendor keep\n---\nbody");
        Write(".gitignore", "vendor/*\n!vendor/keep/");

        var result = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = _root, Source = "path" });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("vk", skill.Name);
    }

    [Fact]
    public void LoadSkillsFromDir_SubdirectoryIgnoreIsAnchoredToItsDirectory()
    {
        // top/.gitignore says "build" — anchored, so it only ignores the top-level build directory;
        // sibling/ignore-target/SKILL.md must survive because the rule is prefixed when merged.
        Write("top/.gitignore", "build");
        Write("top/build/x/SKILL.md", "---\nname: bx\ndescription: ignored\n---\nbody");
        Write("top/keep/SKILL.md", "---\nname: keep\ndescription: kept\n---\nbody");
        Write("sibling/ignore-target/SKILL.md", "---\nname: it\ndescription: kept\n---\nbody");

        var top = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("top"), Source = "path" });
        Assert.Equal(["keep"], [.. top.Skills.Select(s => s.Name)]);

        var sibling = Skills.LoadSkillsFromDir(new LoadSkillsFromDirOptions { Dir = Dir("sibling"), Source = "path" });
        Assert.Equal(["it"], [.. sibling.Skills.Select(s => s.Name)]);
    }

    // ─── skills: loadSkills collision ──────────────────────────────────────────────────────────────

    [Fact]
    public void LoadSkills_FirstLoaderWinsAndCollisionIsReported()
    {
        Write("one/dup/SKILL.md", "---\nname: dup\ndescription: first\n---\nbody");
        Write("two/dup/SKILL.md", "---\nname: dup\ndescription: second\n---\nbody");

        var result = Skills.LoadSkills(new LoadSkillsOptions
        {
            Cwd = _root,
            AgentDir = _root,
            SkillPaths = [Dir("one"), Dir("two")],
            IncludeDefaults = false,
        });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("first", skill.Description);
        var collision = Assert.Single(result.Diagnostics);
        Assert.Equal(ResourceDiagnosticType.Collision, collision.Type);
        Assert.NotNull(collision.Collision);
        Assert.Equal(ResourceType.Skill, collision.Collision!.ResourceType);
        Assert.Equal("dup", collision.Collision.Name);
        Assert.Equal(Path.Combine(Dir("one"), "dup", "SKILL.md"), collision.Collision.WinnerPath);
        Assert.Equal(Path.Combine(Dir("two"), "dup", "SKILL.md"), collision.Collision.LoserPath);
    }

    [Fact]
    public void LoadSkills_MissingPathWarns()
    {
        var result = Skills.LoadSkills(new LoadSkillsOptions
        {
            Cwd = _root,
            AgentDir = _root,
            SkillPaths = [Path.Combine(_root, "does-not-exist")],
            IncludeDefaults = false,
        });

        Assert.Empty(result.Skills);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("skill path does not exist", warning.Message);
    }

    // ─── estimateTokens: the toolsAdded branch, pinned algorithmically ─────────────────────────────

    [Fact]
    public void EstimateTokens_SystemToolsAdded_UsesWireSerializationLength()
    {
        ToolDefinition[] tools = [new("read", "reads files", new ToolSchema(new Dictionary<string, object?>()))];
        var message = new SystemMessage
        {
            Content = MessageContent.From(""),
            ToolsAdded = tools,
            Timestamp = 0,
        };

        var expectedChars = CompatShims.SerializeToPlainText(tools).Length;
        Assert.Equal((expectedChars + 3) / 4, Compaction.EstimateTokens(message));
    }

    // ─── session export ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SerializeSessionBranch_WritesHeaderParentChainAndTrailingNewline()
    {
        var session = SessionManager.InMemory();
        var first = session.AppendMessage(new UserMessage(MessageContent.From("hello"), 1));
        session.AppendMessage(new AssistantMessage(
            [new TextContent("hi")], StopReason.Stop) { Timestamp = 2 });

        var jsonl = SessionExport.SerializeSessionBranch(session);

        Assert.EndsWith("\n", jsonl, StringComparison.Ordinal);
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        var header = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("session", header["type"]!.GetValue<string>());
        Assert.Equal(SessionEntries.CurrentVersion, header["version"]!.GetValue<int>());
        Assert.Equal(session.GetSessionId(), header["id"]!.GetValue<string>());
        Assert.Equal(session.GetCwd(), header["cwd"]!.GetValue<string>());
        Assert.False(header.ContainsKey("parentId"));

        var firstEntry = JsonNode.Parse(lines[1])!.AsObject();
        Assert.Equal(first, firstEntry["id"]!.GetValue<string>());
        // First branch entry: explicit JSON null (TS always emits the field).
        Assert.True(firstEntry.ContainsKey("parentId") && firstEntry["parentId"] is null);

        var secondEntry = JsonNode.Parse(lines[2])!.AsObject();
        Assert.Equal(first, secondEntry["parentId"]!.GetValue<string>());
    }

    [Fact]
    public void SerializeSessionBranch_AppendsTrailingEntriesWithParentLink()
    {
        var session = SessionManager.InMemory();
        var only = session.AppendMessage(new UserMessage(MessageContent.From("hello"), 1));

        var jsonl = SessionExport.SerializeSessionBranch(
            session,
            (parentId, timestamp) =>
            {
                Assert.Equal(only, parentId);
                Assert.False(string.IsNullOrEmpty(timestamp));
                return [new CustomMessageEntry
                {
                    Id = "trailing-1",
                    Timestamp = timestamp,
                    CustomType = "notes",
                    Content = MessageContent.From("export only"),
                    Display = false,
                }];
            });

        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var trailing = JsonNode.Parse(lines[2])!.AsObject();
        Assert.Equal("custom_message", trailing["type"]!.GetValue<string>());
        // The factory left ParentId unset, so the wire keeps an explicit JSON null.
        Assert.True(trailing.ContainsKey("parentId") && trailing["parentId"] is null);
        Assert.Equal("notes", trailing["customType"]!.GetValue<string>());
    }

    [Fact]
    public void ExportSessionToJsonl_WritesFileAndReturnsPath()
    {
        var session = SessionManager.InMemory();
        session.AppendMessage(new UserMessage(MessageContent.From("hello"), 1));

        var outputPath = Path.Combine(_root, "out", "session.jsonl");
        var returned = SessionExport.ExportSessionToJsonl(session, outputPath);

        Assert.Equal(Path.GetFullPath(outputPath), returned);
        Assert.True(File.Exists(returned));
        // Fresh NowIso() per call, so compare structure rather than bytes.
        var lines = File.ReadAllLines(returned);
        Assert.Equal(2, lines.Length);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("session", header["type"]!.GetValue<string>());
        Assert.Equal(session.GetSessionId(), header["id"]!.GetValue<string>());
        var entry = JsonNode.Parse(lines[1])!.AsObject();
        Assert.Equal("message", entry["type"]!.GetValue<string>());
        Assert.True(entry.ContainsKey("parentId") && entry["parentId"] is null);
    }
}
