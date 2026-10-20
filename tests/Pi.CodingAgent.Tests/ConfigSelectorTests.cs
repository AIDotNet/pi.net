// ============================================================================
// Tests for modes/interactive/components/config-selector.ts
//
// The upstream suite has no dedicated config-selector test file, so every case
// here is derived from the source behaviour (same approach as Batch E-3).
// ============================================================================

using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;
using Pi.Durable.Env;

namespace Pi.CodingAgent.Tests;

public sealed class ConfigSelectorTests : SelectorTestFixture
{
    private const string AgentDir = @"C:\Users\u\.pi\agent";
    private const string Cwd = @"C:\proj";
    private const string ProjectConfigDir = @"C:\proj\.pi";

    private static PathMetadata Meta(
        string source = "auto", string scope = "user", string origin = "top-level", string? baseDir = null) =>
        new() { Source = source, Scope = scope, Origin = origin, BaseDir = baseDir };

    private static ResolvedResource Resource(string path, bool enabled, PathMetadata? metadata = null) =>
        new() { Path = path, Enabled = enabled, Metadata = metadata ?? Meta() };

    private static (SettingsManager Manager, InMemorySettingsStorage Storage) CreateSettings(
        string? globalJson = null, string? projectJson = null)
    {
        var storage = new InMemorySettingsStorage();
        storage.WithLock(SettingsScope.Global, _ => globalJson ?? "{}");
        storage.WithLock(SettingsScope.Project, _ => projectJson ?? "{}");
        return (SettingsManager.FromStorage(storage), storage);
    }

    /// <summary>A component over a synthetic catalogue. The project view is what
    /// <c>resolvePackageSources</c> would hand over: the inherited global resources (scope=user)
    /// plus the project-local ones.</summary>
    private static (ConfigSelectorComponent Component, SettingsManager Manager) CreateComponent(
        SettingsManager manager,
        string writeScope = "global")
    {
        var resolved = new ScopedResolvedResources
        {
            Global = new ResolvedResources
            {
                Extensions =
                [
                    Resource($@"{AgentDir}\extensions\alpha.ts", true, Meta()),
                ],
                Skills =
                [
                    Resource($@"{AgentDir}\skills\deploy\SKILL.md", true, Meta()),
                ],
            },
            Project = new ResolvedResources
            {
                Extensions =
                [
                    Resource($@"{ProjectConfigDir}\extensions\beta.ts", true, Meta(scope: "project")),
                    Resource($@"{AgentDir}\extensions\alpha.ts", true, Meta()),
                ],
            },
        };
        var component = new ConfigSelectorComponent(
            resolved, manager, Cwd, AgentDir,
            onClose: () => { }, onExit: () => { }, requestRender: () => { },
            writeScope: writeScope);
        return (component, manager);
    }

    // ------------------------------------------------------------- build groups

    [Fact]
    public void BuildGroups_SortsPackagesFirstUserFirstAndBySource()
    {
        var resolved = new ResolvedResources
        {
            Extensions =
            [
                // top-level project (listed first in input, but must sort last)
                Resource($@"{ProjectConfigDir}\extensions\b.ts", true, Meta(scope: "project")),
                // package user
                Resource(@"C:\pkgs\zeta\ext\z.ts", true, Meta(source: "zeta", origin: "package", baseDir: @"C:\pkgs\zeta")),
                // top-level user
                Resource($@"{AgentDir}\extensions\a.ts", true, Meta()),
                // package project
                Resource(@"C:\proj\pkgs\acme\ext\m.ts", true, Meta(source: "acme", scope: "project", origin: "package", baseDir: @"C:\proj\pkgs\acme")),
            ],
        };

        var groups = ConfigSelectorStatics.BuildGroups(resolved, AgentDir);

        // Packages before top-level; within each origin user before project; then by source.
        Assert.Equal(
            ["zeta:user", "acme:project", "auto:user", "auto:project"],
            [.. groups.Select(g => $"{g.Source}:{g.Scope}")]);
        Assert.Equal(["package", "package", "top-level", "top-level"], [.. groups.Select(g => g.Origin)]);
    }

    [Fact]
    public void BuildGroups_ComputesDisplayNamesByRule()
    {
        var resolved = new ResolvedResources
        {
            Extensions =
            [
                // not under an "extensions" folder → parent/file
                Resource($@"{AgentDir}\plugins\loose.ts", true, Meta()),
            ],
            Skills =
            [
                // SKILL.md → parent folder name
                Resource($@"{AgentDir}\skills\deploy\SKILL.md", true, Meta()),
            ],
            Prompts =
            [
                Resource($@"{AgentDir}\prompts\review.md", true, Meta()),
            ],
            Themes =
            [
                // builtin → strip the prefix
                Resource($"{SourceInfos.BuiltinPathPrefix}dark", true, Meta(source: "builtin")),
            ],
        };

        var groups = ConfigSelectorStatics.BuildGroups(resolved, AgentDir);

        var items = groups.SelectMany(g => g.Subgroups).SelectMany(s => s.Items).ToList();
        static int TypeRank(string type) => type switch
        {
            "extensions" => 0,
            "skills" => 1,
            "prompts" => 2,
            _ => 3,
        };
        Assert.Equal(
            ["plugins/loose.ts", "deploy", "review.md", "dark"],
            [.. items.OrderBy(i => TypeRank(i.ResourceType)).ThenBy(i => i.DisplayName, StringComparer.Ordinal)
                .Select(i => i.DisplayName)]);
    }

    [Fact]
    public void BuildGroups_LabelsBuiltinAndAutoGroups()
    {
        var resolved = new ResolvedResources
        {
            Extensions =
            [
                Resource($"{SourceInfos.BuiltinPathPrefix}tools", true, Meta(source: "builtin")),
                Resource($@"{AgentDir}\extensions\a.ts", true, Meta(source: "auto", baseDir: AgentDir)),
            ],
        };

        var groups = ConfigSelectorStatics.BuildGroups(resolved, AgentDir);

        // Same scope → sorted by source: "auto" < "builtin".
        Assert.Equal(
            [$"User ({ConfigSelectorStatics.FormatBaseDir(AgentDir)})", "Built-in"],
            [.. groups.Select(g => g.Label)]);
    }

    [Fact]
    public void FormatBaseDirCollapsesHomeAndNormalizesSeparators()
    {
        var home = Paths.GetHomeDirectory();
        Assert.Equal("~/", ConfigSelectorStatics.FormatBaseDir(home));
        Assert.Equal("~/sub/dir/", ConfigSelectorStatics.FormatBaseDir(NodePath.Join(home, "sub", "dir")));
        Assert.Equal("C:/abs/path/", ConfigSelectorStatics.FormatBaseDir(@"C:\abs\path"));
    }

    // ------------------------------------------------------------- rendering

    [Fact]
    public void RenderShowsGroupsSubgroupsAndItemsWithCheckboxes()
    {
        var (component, _) = CreateComponent(CreateSettings().Manager);
        var text = RenderText(component.GetResourceList(), 120);

        Assert.Contains("User (", text, StringComparison.Ordinal); // auto group label with base dir
        Assert.Contains("Extensions", text, StringComparison.Ordinal);
        Assert.Contains("Skills", text, StringComparison.Ordinal);
        Assert.Contains(">     [x] alpha.ts", text, StringComparison.Ordinal);
        Assert.Contains("[x] deploy", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderSwitchesTitleAndScopeHintWithWriteScope()
    {
        var (component, _) = CreateComponent(CreateSettings().Manager);
        var globalHeader = string.Join("\n", RenderLines(component, 120));
        Assert.Contains("Global Resources", globalHeader, StringComparison.Ordinal);
        Assert.Contains("~/.pi/agent/settings.json", globalHeader, StringComparison.Ordinal);

        component.GetResourceList().HandleInput("\t"); // tui.input.tab → switch mode
        var projectHeader = string.Join("\n", RenderLines(component, 120));
        Assert.Contains("Project Local Resources", projectHeader, StringComparison.Ordinal);
        Assert.Contains(".pi/settings.json · inherited global resources are dimmed", projectHeader, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- global-mode toggles

    [Fact]
    public void GlobalToggleCyclesTopLevelResourceBetweenPlusAndMinusPatterns()
    {
        var (manager, _) = CreateSettings();
        var (component, _) = CreateComponent(manager);
        var list = component.GetResourceList();

        list.HandleInput(" "); // [x] → [ ]
        Assert.Equal(
            [$"-{NodePath.Relative(AgentDir, $@"{AgentDir}\extensions\alpha.ts")}"],
            manager.GetGlobalExtensionPaths());

        list.HandleInput(" "); // [ ] → [x]
        Assert.Equal(
            [$"+{NodePath.Relative(AgentDir, $@"{AgentDir}\extensions\alpha.ts")}"],
            manager.GetGlobalExtensionPaths());
    }

    [Fact]
    public void GlobalToggleOnPackageResourceConvertsStringEntryToFilteredObject()
    {
        var (manager, _) = CreateSettings(globalJson: """{"packages":["zeta"]}""");
        var resolved = new ScopedResolvedResources
        {
            Global = new ResolvedResources
            {
                Extensions =
                [
                    Resource(@"C:\pkgs\zeta\ext\z.ts", true, Meta(source: "zeta", origin: "package", baseDir: @"C:\pkgs\zeta")),
                ],
            },
        };
        var component = new ConfigSelectorComponent(
            resolved, manager, Cwd, AgentDir,
            () => { }, () => { }, () => { });
        var list = component.GetResourceList();

        list.HandleInput(" ");

        var packages = manager.GetPackages();
        var entry = Assert.IsType<JsonObject>(packages.Single());
        Assert.Equal("zeta", entry["source"]!.GetValue<string>());
        Assert.Equal(
            [$"-{NodePath.Relative(@"C:\pkgs\zeta", @"C:\pkgs\zeta\ext\z.ts")}"],
            [.. entry["extensions"]!.AsArray().Select(n => n!.GetValue<string>())]);
    }

    [Fact]
    public void ProjectModeToggleOnProjectResourceWritesUnloadOverride()
    {
        // A project-scope resource in project mode: not inherited, first toggle lands on "unload"
        // because the inherited-enabled fallback for non-user scopes is true. The user-scope group
        // sorts first, so the project-local beta entry is reached via search.
        var (manager, _) = CreateSettings();
        var (component, _) = CreateComponent(manager, writeScope: "project");
        var list = component.GetResourceList();

        list.HandleInput("b");
        list.HandleInput("e");
        list.HandleInput("t");
        list.HandleInput(" "); // inherit → unload
        Assert.Equal(
            [$"-{NodePath.Relative(ProjectConfigDir, $@"{ProjectConfigDir}\extensions\beta.ts")}"],
            manager.GetProjectExtensionPaths());
        Assert.Contains("[-]", RenderText(list, 120), StringComparison.Ordinal);
        Assert.Contains("project unload", RenderText(list, 120), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ project-mode overrides

    [Fact]
    public void ProjectToggleCyclesInheritedResourceThroughUnloadLoadInherit()
    {
        // The global catalogue enables alpha.ts; the project mode starts at inherit ([x] dimmed).
        var (manager, _) = CreateSettings();
        var (component, _) = CreateComponent(manager, writeScope: "project");
        var list = component.GetResourceList();
        var inheritedPath = $@"{AgentDir}\extensions\alpha.ts";

        // Filter down to the inherited global extension item by search.
        list.HandleInput("a");
        list.HandleInput("l");
        list.HandleInput("p");
        var text = RenderText(list, 120);
        Assert.Contains("[x] alpha.ts", text, StringComparison.Ordinal);
        Assert.Contains("inherited global", text, StringComparison.Ordinal);

        list.HandleInput(" "); // inherit → unload (inherited enabled); the entry names the inherited file
        Assert.Equal([inheritedPath, $"-{inheritedPath}"], manager.GetProjectExtensionPaths());
        Assert.Contains("[-] alpha.ts", RenderText(list, 120), StringComparison.Ordinal);
        Assert.Contains("project unload", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput(" "); // unload → load
        Assert.Equal([inheritedPath, $"+{inheritedPath}"], manager.GetProjectExtensionPaths());
        Assert.Contains("[+] alpha.ts", RenderText(list, 120), StringComparison.Ordinal);
        Assert.Contains("project load", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput(" "); // load → inherit (entries cleared)
        Assert.Empty(manager.GetProjectExtensionPaths());
    }

    [Fact]
    public void ProjectToggleOnPackageResourceCreatesAutoloadFalseOverride()
    {
        var (manager, _) = CreateSettings(globalJson: """{"packages":["npm:zeta"]}""");
        var resolved = new ScopedResolvedResources
        {
            // The project view carries the package resource (as resolvePackageSources would).
            Project = new ResolvedResources
            {
                Extensions =
                [
                    // npm: sources are not local paths, so the override keeps the source verbatim.
                    Resource(@"C:\pkgs\zeta\ext\z.ts", true, Meta(source: "npm:zeta", origin: "package", baseDir: @"C:\pkgs\zeta")),
                ],
            },
        };
        var component = new ConfigSelectorComponent(
            resolved, manager, Cwd, AgentDir, () => { }, () => { }, () => { }, writeScope: "project");
        var list = component.GetResourceList();

        list.HandleInput(" "); // inherit → unload

        var packages = manager.GetProjectSettings().Packages ?? [];
        var entry = Assert.IsType<JsonObject>(packages.Single());
        Assert.Equal("npm:zeta", entry["source"]!.GetValue<string>());
        Assert.False(entry["autoload"]!.GetValue<bool>());
        Assert.Equal(
            [$"-{NodePath.Relative(@"C:\pkgs\zeta", @"C:\pkgs\zeta\ext\z.ts")}"],
            [.. entry["extensions"]!.AsArray().Select(n => n!.GetValue<string>())]);
    }

    // ---------------------------------------------------------------- search

    [Fact]
    public void SearchFiltersGroupsSubgroupsAndItemsCascading()
    {
        var (manager, _) = CreateSettings();
        var (component, _) = CreateComponent(manager);
        var list = component.GetResourceList();

        list.HandleInput("d"); // matches "deploy" (skill display name and path), not alpha.ts
        var text = RenderText(list, 120);
        Assert.Contains("deploy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha.ts", text, StringComparison.Ordinal);

        // A fresh component mirrors the TS behaviour of clearing the query (empty query → full list).
        var fresh = CreateComponent(manager).Component.GetResourceList();
        var fullText = RenderText(fresh, 120);
        Assert.Contains("alpha.ts", fullText, StringComparison.Ordinal);
        Assert.Contains("deploy", fullText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- navigation

    [Fact]
    public void PageDownJumpsToTheLastItem()
    {
        var (component, _) = CreateComponent(CreateSettings().Manager);
        var list = component.GetResourceList();

        list.HandleInput("\x1b[6~"); // pageDown
        // With only two items on screen the selection lands on the last item ("deploy").
        // The search input also renders a "> " row, so require a checkbox column marker.
        var rows = RenderLines(list, 120);
        var selected = rows.FirstOrDefault(line =>
            line.StartsWith("> ", StringComparison.Ordinal) && line.Contains('[', StringComparison.Ordinal));
        Assert.NotNull(selected);
        Assert.Contains("deploy", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeInvokesOnCloseAndCtrlCInvokesOnExit()
    {
        var closed = 0;
        var exited = 0;
        var (manager, _) = CreateSettings();
        var resolved = new ScopedResolvedResources();
        var component = new ConfigSelectorComponent(
            resolved, manager, Cwd, AgentDir,
            onClose: () => closed++, onExit: () => exited++, requestRender: () => { });
        var list = component.GetResourceList();

        list.HandleInput("\x1b");
        Assert.Equal(1, closed);
        Assert.Equal(0, exited);

        // With the default keymap ctrl+c is bound to tui.select.cancel as well, so it closes
        // exactly like Escape (same as upstream, where the onExit branch is only reachable when
        // the user rebinds select.cancel away from ctrl+c).
        list.HandleInput("\x03");
        Assert.Equal(2, closed);
        Assert.Equal(0, exited);
    }
}
