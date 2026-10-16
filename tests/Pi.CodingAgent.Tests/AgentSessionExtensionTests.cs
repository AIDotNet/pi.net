using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Xunit;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9d：<c>agent-session.ts</c> 中扩展绑定与运行时重建的纯决策逻辑。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
public sealed class AgentSessionExtensionTests
{
    // ---------------------------------------------------------------------
    // 构造辅助
    // ---------------------------------------------------------------------

    private static ToolSchema Schema() => new(new Dictionary<string, object?>());

    private static ToolDefinition Def(string name, string? exposure = null, bool? defaultActive = null,
        string? promptSnippet = null, IReadOnlyList<string>? promptGuidelines = null)
        => new()
        {
            Name = name,
            Label = name,
            Description = $"desc:{name}",
            Parameters = Schema(),
            Execute = (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
            Exposure = exposure,
            DefaultActive = defaultActive,
            PromptSnippet = promptSnippet,
            PromptGuidelines = promptGuidelines,
        };

    private static ToolDefinitionEntry Entry(string name, string? exposure = null, bool? defaultActive = null,
        string? promptSnippet = null, IReadOnlyList<string>? promptGuidelines = null)
        => new()
        {
            Definition = Def(name, exposure, defaultActive, promptSnippet, promptGuidelines),
            SourceInfo = SourceInfos.CreateSynthetic($"builtin:{name}", "builtin"),
        };

    private static SourceInfo Info(string path) => SourceInfos.CreateSynthetic(path, "test");

    private static PromptTemplate Template(string name, string description)
        => new()
        {
            Name = name,
            Description = description,
            Content = "body",
            SourceInfo = Info($"/prompts/{name}.md"),
            FilePath = $"/prompts/{name}.md",
        };

    private static Skill SkillOf(string name, string description)
        => new()
        {
            Name = name,
            Description = description,
            FilePath = $"/skills/{name}/SKILL.md",
            BaseDir = $"/skills/{name}",
            SourceInfo = Info($"/skills/{name}/SKILL.md"),
            DisableModelInvocation = false,
        };

    private static IReadOnlyDictionary<string, ToolDefinitionEntry> Registry(params ToolDefinitionEntry[] entries)
    {
        var map = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Definition.Name] = entry;
        }

        return map;
    }

    // ---------------------------------------------------------------------
    // 资源发现
    // ---------------------------------------------------------------------

    [Fact]
    public void GetExtensionSourceLabel_HandlesSyntheticAndFilePaths()
    {
        // 合成路径：删掉所有尖括号，整串作标签。
        Assert.Equal("extension:builtin:read", AgentSession.GetExtensionSourceLabel("builtin:read"));
        Assert.Equal("extension:inline:foo", AgentSession.GetExtensionSourceLabel("<inline:foo>"));
        Assert.Equal("extension:inline:ab", AgentSession.GetExtensionSourceLabel("<inline:a<b>>"));

        // 文件路径：取 basename 并剥掉 .ts / .js。
        Assert.Equal("extension:my-ext", AgentSession.GetExtensionSourceLabel("/a/b/my-ext.ts"));
        Assert.Equal("extension:my-ext", AgentSession.GetExtensionSourceLabel("/a/b/my-ext.js"));
        // 其他后缀不动。
        Assert.Equal("extension:my-ext.mjs", AgentSession.GetExtensionSourceLabel("/a/b/my-ext.mjs"));
        Assert.Equal("extension:my-ext", AgentSession.GetExtensionSourceLabel("/a/b/my-ext"));

        // 只有**开头**是 < 才算合成路径；路径中间的尖括号不影响判定。
        Assert.Equal("extension:ext", AgentSession.GetExtensionSourceLabel("/a/<b>/ext.ts"));
    }

    [Fact]
    public void BuildExtensionResourcePaths_StampsTemporaryTopLevelMetadata()
    {
        var fileEntry = new ExtensionResourceEntry("/res/skill.md", "/ext/my-ext.ts");
        var syntheticEntry = new ExtensionResourceEntry("/res/other.md", "builtin:my-ext");

        var result = AgentSession.BuildExtensionResourcePaths([fileEntry, syntheticEntry]);

        Assert.Equal(2, result.Count);

        var file = result[0];
        Assert.Equal("/res/skill.md", file.Path);
        Assert.Equal("extension:my-ext", file.Metadata.Source);
        Assert.Equal(SourceScope.Temporary, file.Metadata.Scope);
        Assert.Equal(SourceOrigin.TopLevel, file.Metadata.Origin);
        // 文件路径 → baseDir 取 dirname。
        Assert.Equal(NodePath.Dirname("/ext/my-ext.ts"), file.Metadata.BaseDir);

        // 合成路径没有真实目录 → baseDir 为 null（TS 的 undefined）。
        var synthetic = result[1];
        Assert.Equal("extension:builtin:my-ext", synthetic.Metadata.Source);
        Assert.Null(synthetic.Metadata.BaseDir);
    }

    // ---------------------------------------------------------------------
    // 命令清单
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildSlashCommandInfos_OrdersExtensionThenPromptThenSkill()
    {
        var commands = new List<RegisteredExtensionCommand>
        {
            new("ext-cmd", "an extension command", Info("<inline:x>")),
        };
        var templates = new List<PromptTemplate> { Template("tpl", "a template") };
        var skills = new List<Skill> { SkillOf("review", "a skill") };

        var result = AgentSession.BuildSlashCommandInfos(commands, templates, skills);

        Assert.Equal(3, result.Count);
        Assert.Equal(["ext-cmd", "tpl", "skill:review"], result.Select(info => info.Name));
        Assert.Equal(
            [SlashCommandSource.Extension, SlashCommandSource.Prompt, SlashCommandSource.Skill],
            result.Select(info => info.Source));
        Assert.Equal(["an extension command", "a template", "a skill"],
            result.Select(info => info.Description));

        // 溯源随各自来源带过来。
        Assert.Equal("/prompts/tpl.md", result[1].SourceInfo.Path);
        Assert.Equal("/skills/review/SKILL.md", result[2].SourceInfo.Path);
    }

    [Fact]
    public void BuildSlashCommandInfos_PrefixesSkillNames()
    {
        var result = AgentSession.BuildSlashCommandInfos(
            [],
            [],
            [SkillOf("a", "da"), SkillOf("b", "db")]);

        Assert.Equal(["skill:a", "skill:b"], result.Select(info => info.Name));
    }

    // ---------------------------------------------------------------------
    // 定义表
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildDefinitionRegistry_FiltersBuiltinsAndLetsCustomToolsWin()
    {
        var builtins = new List<KeyValuePair<string, ToolDefinition>>
        {
            new("read", Def("read")),
            new("bash", Def("bash")),
        };
        var custom = new List<ToolDefinitionEntry>
        {
            // 与内置同名 → 覆盖，且来源换成扩展的。
            new() { Definition = Def("read", promptSnippet: "custom read"), SourceInfo = Info("<inline:ext>") },
            new() { Definition = Def("mytool"), SourceInfo = Info("<inline:ext>") },
        };

        var registry = AgentSession.BuildDefinitionRegistry(
            builtins, custom, ToolFilter.None);

        Assert.Equal(["read", "bash", "mytool"], registry.Keys);

        // 内置来源是 builtin:<name>。
        Assert.Equal("builtin:bash", registry["bash"].SourceInfo.Path);
        // 自定义工具覆盖了同名内置：定义与来源都换成自定义的。
        Assert.Equal("custom read", registry["read"].Definition.PromptSnippet);
        Assert.Equal("<inline:ext>", registry["read"].SourceInfo.Path);
    }

    [Fact]
    public void BuildDefinitionRegistry_FiltersBuiltinsByAllowDeny()
    {
        var builtins = new List<KeyValuePair<string, ToolDefinition>>
        {
            new("read", Def("read")),
            new("bash", Def("bash")),
            new("edit", Def("edit")),
        };

        var registry = AgentSession.BuildDefinitionRegistry(
            builtins, [], ToolFilter.From(["read", "bash"], ["bash"]));

        Assert.Equal(["read"], registry.Keys);
        Assert.Equal("builtin:read", registry["read"].SourceInfo.Path);
    }

    [Fact]
    public void BuildPromptSnippets_NormalizesAndSkipsEmpty()
    {
        var definitions = new List<ToolDefinitionEntry>
        {
            Entry("read", promptSnippet: "  read\n  a file  "),
            Entry("bash", promptSnippet: "   "),
            Entry("edit"),
        };

        var snippets = AgentSession.BuildPromptSnippets(definitions);

        Assert.Equal(["read"], snippets.Keys);
        Assert.Equal("read a file", snippets["read"]);
    }

    [Fact]
    public void BuildPromptGuidelines_NormalizesAndSkipsEmpty()
    {
        var definitions = new List<ToolDefinitionEntry>
        {
            Entry("read", promptGuidelines: ["  be careful  ", "be careful", ""]),
            Entry("bash", promptGuidelines: []),
            Entry("edit"),
        };

        var guidelines = AgentSession.BuildPromptGuidelines(definitions);

        Assert.Equal(["read"], guidelines.Keys);
        // trim + 丢空 + 按首次出现去重。
        Assert.Equal(["be careful"], guidelines["read"]);
    }

    // ---------------------------------------------------------------------
    // 活跃工具名
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveNextActiveToolNames_FallsBackToPreviousWhenNoRequest()
    {
        var definitions = Registry(Entry("read"), Entry("bash"));

        // 稳态：read/bash 早就注册过，因此都在 previousActivatedOnRegistration 里——
        // 「无请求」分支不会把它们当成新注册的工具再加一遍。
        // （previousActivatedOnRegistration 由**注册表**推导，与当前活跃集无关。）
        var previouslyActivated = new HashSet<string>(["read", "bash"], StringComparer.Ordinal);

        // 无请求 → 起点是上一轮的活跃集合（read），而不是注册表的全部。
        Assert.Equal(["read"], AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: null,
            previousActiveToolNames: ["read"],
            toolRegistryNames: ["read", "bash"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.None,
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: previouslyActivated,
            pendingToolNames: [],
            definitions));

        // 有请求 → 请求覆盖上一轮的活跃集合。
        Assert.Equal(["bash"], AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: ["bash"],
            previousActiveToolNames: ["read"],
            toolRegistryNames: ["read", "bash"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.None,
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: previouslyActivated,
            pendingToolNames: [],
            definitions));
    }

    [Fact]
    public void ResolveNextActiveToolNames_FiltersByAllowDeny()
    {
        var definitions = Registry(Entry("read"), Entry("bash"), Entry("edit"));

        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: ["read", "bash", "edit"],
            previousActiveToolNames: [],
            toolRegistryNames: ["read", "bash", "edit"],
            wrappedExtensionToolNames: [],
            // allowlist 非 null 会走「点名即激活」分支；用 deny 排除 bash。
            filter: ToolFilter.From(null, ["bash"]),
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: new HashSet<string>(StringComparer.Ordinal),
            pendingToolNames: [],
            definitions);

        Assert.Equal(["read", "edit"], result);
    }

    [Fact]
    public void ResolveNextActiveToolNames_AllowListActivatesNamedDeclarableTools()
    {
        var definitions = Registry(
            Entry("read"),
            Entry("hidden", exposure: ToolExposure.Hidden),
            Entry("modelOnly", exposure: ToolExposure.ModelOnly),
            Entry("inactive", defaultActive: false));


        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: [],
            previousActiveToolNames: [],
            toolRegistryNames: ["read", "hidden", "modelOnly", "inactive"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.From(["read", "hidden", "modelOnly", "inactive"], null),
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: new HashSet<string>(StringComparer.Ordinal),
            pendingToolNames: [],
            definitions);

        // 点名即激活，但只有 direct / model-only 是「可声明」的；hidden 排除，
        // inactive 虽 defaultActive=false 仍被点名激活。
        Assert.Equal(["read", "modelOnly", "inactive"], result);
    }

    [Fact]
    public void ResolveNextActiveToolNames_IncludeAllExtensionToolsActivatesOnRegistration()
    {
        var definitions = Registry(
            Entry("builtin-read"),
            Entry("ext-on"),
            Entry("ext-off", defaultActive: false),
            Entry("ext-hidden", exposure: ToolExposure.Hidden));

        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: ["builtin-read"],
            previousActiveToolNames: [],
            toolRegistryNames: ["builtin-read", "ext-on", "ext-off", "ext-hidden"],
            wrappedExtensionToolNames: ["ext-on", "ext-off", "ext-hidden"],
            filter: ToolFilter.None,
            includeAllExtensionTools: true,
            previousActivatedOnRegistration: new HashSet<string>(StringComparer.Ordinal),
            pendingToolNames: [],
            definitions);

        // 起点保留，再叠加「注册即激活」的扩展工具（ext-off 默认不活跃、ext-hidden 不可声明）。
        Assert.Equal(["builtin-read", "ext-on"], result);
    }

    [Fact]
    public void ResolveNextActiveToolNames_WithoutRequestActivatesOnlyNewlyRegistered()
    {
        var definitions = Registry(
            Entry("read"),
            Entry("already"),
            Entry("fresh"));

        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: null,
            previousActiveToolNames: ["read"],
            toolRegistryNames: ["read", "already", "fresh"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.None,
            includeAllExtensionTools: false,
            // 上一轮就已「注册即激活」的 only already。
            previousActivatedOnRegistration: new HashSet<string>(["already"], StringComparer.Ordinal),
            pendingToolNames: [],
            definitions);

        Assert.Equal(["read", "fresh"], result);
    }

    [Fact]
    public void ResolveNextActiveToolNames_PendingToolsAlwaysBecomeActive()
    {
        var definitions = Registry(Entry("read"), Entry("restored"));

        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: ["read"],
            previousActiveToolNames: [],
            toolRegistryNames: ["read", "restored"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.None,
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: new HashSet<string>(StringComparer.Ordinal),
            // 待激活工具无论默认是否活跃都转正。
            pendingToolNames: ["restored"],
            definitions);

        Assert.Equal(["read", "restored"], result);
    }

    [Fact]
    public void ResolveNextActiveToolNames_DedupesPreservingFirstOccurrence()
    {
        var definitions = Registry(Entry("read"), Entry("bash"));

        var result = AgentSession.ResolveNextActiveToolNames(
            requestedActiveToolNames: ["read", "bash", "read"],
            previousActiveToolNames: [],
            toolRegistryNames: ["read", "bash"],
            wrappedExtensionToolNames: [],
            filter: ToolFilter.None,
            includeAllExtensionTools: false,
            previousActivatedOnRegistration: new HashSet<string>(StringComparer.Ordinal),
            // 与起点重复。
            pendingToolNames: ["bash", "new"],
            definitions);

        Assert.Equal(["read", "bash", "new"], result);
    }

    // ---------------------------------------------------------------------
    // 运行时构建与 reload
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveBaseActiveToolNames_PrefersRequestThenOverrideThenDefaults()
    {
        // 显式请求优先。
        Assert.Equal(["a"], AgentSession.ResolveBaseActiveToolNames(["a"], ["x", "y"]));

        // 无请求 → 用 baseToolsOverride 的键。
        Assert.Equal(["x", "y"], AgentSession.ResolveBaseActiveToolNames(null, ["x", "y"]));

        // 两者都没有 → 内置默认四件套。
        Assert.Equal(
            SettingsManager.DefaultToolNames,
            AgentSession.ResolveBaseActiveToolNames(null, null));

        // baseToolsOverride 配成空对象 → 空列表，**不**回落到默认值。
        Assert.Empty(AgentSession.ResolveBaseActiveToolNames(null, []));
    }

    [Fact]
    public void ResolvePreviousDefaultTools_EmptyWhenNotUsingDefaultTools()
    {
        var settings = new List<string> { "read", "custom" };

        var used = AgentSession.ResolvePreviousDefaultTools(true, settings);
        Assert.Equal(["read", "custom"], used);

        // 未使用默认工具集 → 空集。
        Assert.Empty(AgentSession.ResolvePreviousDefaultTools(false, settings));

        // settings 里没有 defaultTools → 回落内置默认四件套。
        // 返回类型是 IReadOnlySet，集合比较需先排序（xUnit2027）。
        Assert.Equal(
            SettingsManager.DefaultToolNames.OrderBy(x => x, StringComparer.Ordinal),
            AgentSession.ResolvePreviousDefaultTools(true, null).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ResolveAddedDefaultTools_ReturnsOnlyNewlyAdded()
    {
        var previous = new HashSet<string>(["read", "bash"], StringComparer.Ordinal);

        // read/bash 仍在，custom 是新加的，gone 已被移除（不算「新增」）。
        Assert.Equal(
            ["custom"],
            AgentSession.ResolveAddedDefaultTools(true, previous, ["read", "bash", "custom"]));

        // 全部照旧 → 没有新增。
        Assert.Empty(AgentSession.ResolveAddedDefaultTools(true, previous, ["read", "bash"]));

        // 未使用默认工具集 → 一律空。
        Assert.Empty(AgentSession.ResolveAddedDefaultTools(false, previous, ["custom"]));
    }

    [Fact]
    public void ResolveCurrentDefaultTools_AppliesToolModifiers()
    {
        // 上游 ddaa0a034：reload 的 getDefaultTools() 把 --tools 的 +name/-name 作用到刷新后的
        // defaultTools 上，使「被移除的工具保持移除」。
        var settings = new List<string> { "read", "bash", "edit" };

        Assert.Equal(
            ["read", "edit", "grep"],
            AgentSession.ResolveCurrentDefaultTools(true, settings, ["-bash", "+grep"]));

        // 未使用默认工具集 → 空列表（修饰符不生效）。
        Assert.Empty(AgentSession.ResolveCurrentDefaultTools(false, settings, ["-bash"]));

        // 无修饰符 → 原样（缺省回落内置默认四件套）。
        Assert.Equal(["read", "bash", "edit"], AgentSession.ResolveCurrentDefaultTools(true, settings));
        Assert.Equal(
            SettingsManager.DefaultToolNames,
            AgentSession.ResolveCurrentDefaultTools(true, null));
    }

    [Fact]
    public void ResolvePreviousDefaultTools_SeesModifiers()
    {
        var settings = new List<string> { "read", "bash", "edit" };

        var previous = AgentSession.ResolvePreviousDefaultTools(true, settings, ["-bash"]);

        Assert.Equal(["read", "edit"], previous);
    }

    [Fact]
    public void ResolveAddedDefaultTools_SeesModifiers()
    {
        var previous = new HashSet<string>(["read"], StringComparer.Ordinal);

        // 设置里新增 bash/edit，但 -edit 把它摘掉 → 只报 bash。
        Assert.Equal(
            ["bash"],
            AgentSession.ResolveAddedDefaultTools(true, previous, ["read", "bash", "edit"], ["-edit"]));
    }

    [Fact]
    public void HasExtensionBindings_OrOfFour()
    {
        Assert.True(AgentSession.HasExtensionBindings(true, false, false, false));
        Assert.True(AgentSession.HasExtensionBindings(false, true, false, false));
        Assert.True(AgentSession.HasExtensionBindings(false, false, true, false));
        Assert.True(AgentSession.HasExtensionBindings(false, false, false, true));
        Assert.False(AgentSession.HasExtensionBindings(false, false, false, false));
    }

    [Fact]
    public void ShouldRefreshModelFromRegistry_RequiresRealChange()
    {
        Assert.True(AgentSession.ShouldRefreshModelFromRegistry(true, true, false));

        // 无当前模型 → 不动。
        Assert.False(AgentSession.ShouldRefreshModelFromRegistry(false, true, false));
        // 注册表里查不到 → 不动。
        Assert.False(AgentSession.ShouldRefreshModelFromRegistry(true, false, false));
        // 查到的就是同一个对象（引用同一性）→ 不动。
        Assert.False(AgentSession.ShouldRefreshModelFromRegistry(true, true, true));
    }
}
