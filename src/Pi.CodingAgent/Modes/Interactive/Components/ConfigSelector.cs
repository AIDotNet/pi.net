// ============================================================================
// TUI component for managing package resources (enable/disable).
// Port of modes/interactive/components/config-selector.ts
// ============================================================================

using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

// Design differences (porting ledger):
// - C245 (ScopedResolvedPaths): TS `Record<"global" | "project", ResolvedPaths>` →
//   `ScopedResolvedResources` with `Global` / `Project` members; `ResolvedPaths` itself is named
//   `ResolvedResources` in the port (PackageManager.cs).
// - Not numbered: the string unions (ResourceType, ConfigWriteScope, SettingsScope,
//   ProjectOverrideState) are carried as `string` constants per C242; `localeCompare` group and
//   display-name sorts use `StringComparer.CurrentCulture` per C241; node:path helpers map onto
//   `NodePath`/`Paths`; the packages JSON array is carried as `IReadOnlyList<JsonNode?>` with
//   JsonObject element form (SettingsManager's existing shape).

/// <summary>TS <c>ScopedResolvedPaths</c>: resolved resource paths per write scope.</summary>
public sealed record ScopedResolvedResources
{
    public ResolvedResources Global { get; init; } = new();
    public ResolvedResources Project { get; init; } = new();
}

internal static class ConfigSelectorStatics
{
    public static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];

    public static readonly IReadOnlyDictionary<string, string> ResourceTypeLabels =
        new Dictionary<string, string>
        {
            ["extensions"] = "Extensions",
            ["skills"] = "Skills",
            ["prompts"] = "Prompts",
            ["themes"] = "Themes",
        };

    private static readonly IReadOnlyDictionary<string, int> TypeOrder =
        new Dictionary<string, int> { ["extensions"] = 0, ["skills"] = 1, ["prompts"] = 2, ["themes"] = 3 };

    public static string FormatBaseDir(string baseDir)
    {
        var homeDir = Paths.GetHomeDirectory();
        string displayPath;

        if (string.Equals(baseDir, homeDir, StringComparison.Ordinal))
        {
            displayPath = "~";
        }
        else if (baseDir.StartsWith(homeDir, StringComparison.Ordinal))
        {
            // Replace home prefix with ~, normalize separators for display
            displayPath = $"~{baseDir[homeDir.Length..].Replace('\\', '/')}";
        }
        else
        {
            displayPath = baseDir.Replace('\\', '/');
        }

        return displayPath.EndsWith('/') ? displayPath : $"{displayPath}/";
    }

    public static string GetGroupLabel(PathMetadata metadata, string agentDir)
    {
        if (metadata.Origin == "package")
        {
            return $"{metadata.Source} ({metadata.Scope})";
        }
        if (metadata.Source == "builtin")
        {
            return metadata.Scope == "user" ? "Built-in" : "Built-in (project override)";
        }
        // Top-level resources
        if (metadata.Source == "auto")
        {
            if (metadata.BaseDir is { } baseDir)
            {
                return metadata.Scope == "user"
                    ? $"User ({FormatBaseDir(baseDir)})"
                    : $"Project ({FormatBaseDir(baseDir)})";
            }
            return metadata.Scope == "user"
                ? $"User ({FormatBaseDir(agentDir)})"
                : $"Project ({Config.ConfigDirName}/)";
        }
        return metadata.Scope == "user" ? "User settings" : "Project settings";
    }

    public static List<ResourceGroup> BuildGroups(ResolvedResources resolved, string agentDir)
    {
        var groupMap = new Dictionary<string, ResourceGroup>();

        void AddToGroup(IReadOnlyList<ResolvedResource> resources, string resourceType)
        {
            foreach (var res in resources)
            {
                var metadata = res.Metadata;
                var groupKey = $"{metadata.Origin}:{metadata.Scope}:{metadata.Source}:{metadata.BaseDir ?? ""}";

                if (!groupMap.TryGetValue(groupKey, out var group))
                {
                    group = new ResourceGroup
                    {
                        Key = groupKey,
                        Label = GetGroupLabel(metadata, agentDir),
                        Scope = metadata.Scope,
                        Origin = metadata.Origin,
                        Source = metadata.Source,
                    };
                    groupMap.Add(groupKey, group);
                }

                var subgroupKey = $"{groupKey}:{resourceType}";

                var subgroup = group.Subgroups.FirstOrDefault(sg => sg.Type == resourceType);
                if (subgroup is null)
                {
                    subgroup = new ResourceSubgroup
                    {
                        Type = resourceType,
                        Label = ResourceTypeLabels[resourceType],
                    };
                    group.Subgroups.Add(subgroup);
                }

                var fileName = NodePath.Basename(res.Path);
                var parentFolder = NodePath.Basename(NodePath.Dirname(res.Path));
                string displayName;
                if (metadata.Source == "builtin")
                {
                    displayName = res.Path[SourceInfos.BuiltinPathPrefix.Length..];
                }
                else if (resourceType == "extensions" && parentFolder != "extensions")
                {
                    displayName = $"{parentFolder}/{fileName}";
                }
                else if (resourceType == "skills" && fileName == "SKILL.md")
                {
                    displayName = parentFolder;
                }
                else
                {
                    displayName = fileName;
                }
                subgroup.Items.Add(new ResourceItem
                {
                    Path = res.Path,
                    Enabled = res.Enabled,
                    Metadata = metadata,
                    ResourceType = resourceType,
                    DisplayName = displayName,
                    GroupKey = groupKey,
                    SubgroupKey = subgroupKey,
                });
            }
        }

        AddToGroup(resolved.Extensions, "extensions");
        AddToGroup(resolved.Skills, "skills");
        AddToGroup(resolved.Prompts, "prompts");
        AddToGroup(resolved.Themes, "themes");

        // Sort groups: packages first, then top-level; user before project.
        // TS uses a stable sort (V8); the LINQ chain below is stable as well.
        var groups = groupMap.Values
            .OrderBy(group => group.Origin == "package" ? 0 : 1)
            .ThenBy(group => group.Scope == "user" ? 0 : 1)
            .ThenBy(group => group.Source, StringComparer.CurrentCulture)
            .ToList();

        // Sort subgroups within each group by type order, and items by name
        foreach (var group in groups)
        {
            group.Subgroups.Sort((a, b) => TypeOrder[a.Type] - TypeOrder[b.Type]);
            foreach (var subgroup in group.Subgroups)
            {
                subgroup.Items.Sort((a, b) => string.Compare(
                    a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
            }
        }

        return groups;
    }
}

internal sealed class ResourceItem
{
    public required string Path { get; init; }
    public bool Enabled { get; set; }
    public required PathMetadata Metadata { get; init; }
    public required string ResourceType { get; init; }
    public required string DisplayName { get; init; }
    public required string GroupKey { get; init; }
    public required string SubgroupKey { get; init; }
}

internal sealed class ResourceSubgroup
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public List<ResourceItem> Items { get; } = [];
}

internal sealed class ResourceGroup
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Scope { get; init; }
    public required string Origin { get; init; }
    public required string Source { get; init; }
    public List<ResourceSubgroup> Subgroups { get; } = [];
}

internal enum FlatEntryKind
{
    Group,
    Subgroup,
    Item,
}

internal readonly record struct FlatEntry(
    FlatEntryKind Kind,
    ResourceGroup? Group,
    ResourceSubgroup? Subgroup,
    ResourceItem? Item)
{
    public static FlatEntry ForGroup(ResourceGroup group) => new(FlatEntryKind.Group, group, null, null);
    public static FlatEntry ForSubgroup(ResourceSubgroup subgroup, ResourceGroup group) =>
        new(FlatEntryKind.Subgroup, group, subgroup, null);
    public static FlatEntry ForItem(ResourceItem item) => new(FlatEntryKind.Item, null, null, item);
}

internal sealed class ConfigSelectorHeader : IComponent
{
    private string _writeScope;
    private readonly bool _projectModeAvailable;

    public ConfigSelectorHeader(string writeScope, bool projectModeAvailable)
    {
        _writeScope = writeScope;
        _projectModeAvailable = projectModeAvailable;
    }

    public void SetWriteScope(string writeScope) => _writeScope = writeScope;

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var title = theme.Bold(_writeScope == "project" ? "Project Local Resources" : "Global Resources");
        var sep = theme.Fg("muted", " · ");
        var switchHint = _projectModeAvailable ? KeybindingHints.KeyHint("tui.input.tab", "switch mode") + sep : "";
        var actionHint = _writeScope == "project"
            ? KeybindingHints.RawKeyHint("space", "cycle inherit/+/-")
            : KeybindingHints.RawKeyHint("space", "toggle");
        var hint = switchHint + actionHint + sep + KeybindingHints.RawKeyHint("esc", "close");
        var spacing = Math.Max(1, width - UnicodeWidth.VisibleWidth(title) - UnicodeWidth.VisibleWidth(hint));
        var scopeHint = _writeScope == "project"
            ? theme.Fg("muted", $"{Config.ConfigDirName}/settings.json · inherited global resources are dimmed")
            : theme.Fg("muted", $"~/{Config.ConfigDirName}/agent/settings.json");

        return
        [
            TextLayout.TruncateToWidth($"{title}{new string(' ', spacing)}{hint}", width, ""),
            TextLayout.TruncateToWidth(scopeHint, width, ""),
        ];
    }
}

internal sealed class ResourceList : IComponent, IFocusable
{
    private readonly Dictionary<string, List<ResourceGroup>> _groupsByScope;
    private List<FlatEntry> _flatItems = [];
    private List<FlatEntry> _filteredItems = [];
    private int _selectedIndex;
    private readonly Input _searchInput;
    private readonly int _maxVisible;
    private readonly SettingsManager _settingsManager;
    private readonly string _cwd;
    private readonly string _agentDir;
    private string _writeScope;
    private readonly Dictionary<string, bool> _inheritedEnabledByKey;

    public Action? OnCancel { get; set; }
    public Action? OnExit { get; set; }
    public Action<ResourceItem, bool>? OnToggle { get; set; }
    public Action? OnSwitchMode { get; set; }

    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    public ResourceList(
        Dictionary<string, List<ResourceGroup>> groupsByScope,
        SettingsManager settingsManager,
        string cwd,
        string agentDir,
        int? terminalHeight = null,
        string writeScope = "global")
    {
        _groupsByScope = groupsByScope;
        _settingsManager = settingsManager;
        _cwd = cwd;
        _agentDir = agentDir;
        _writeScope = writeScope;
        _inheritedEnabledByKey = BuildInheritedEnabledMap(groupsByScope["global"]);
        _searchInput = new Input();
        // 8 lines of chrome: top spacer + top border + spacer + header (2 lines) + spacer + bottom spacer + bottom border
        const int chrome = 8;
        _maxVisible = Math.Max(5, (terminalHeight ?? 24) - chrome);
        BuildFlatList();
        _filteredItems = [.. _flatItems];
    }

    public void SetWriteScope(string writeScope)
    {
        _writeScope = writeScope;
        BuildFlatList();
        FilterItems(_searchInput.GetValue());
    }

    private List<ResourceGroup> Groups => _groupsByScope[_writeScope];

    private Dictionary<string, bool> BuildInheritedEnabledMap(List<ResourceGroup> groups)
    {
        var result = new Dictionary<string, bool>();
        foreach (var group in groups)
        {
            foreach (var subgroup in group.Subgroups)
            {
                foreach (var item in subgroup.Items)
                {
                    result[GetResourceItemKey(item)] = item.Enabled;
                }
            }
        }
        return result;
    }

    private void BuildFlatList()
    {
        _flatItems = [];
        foreach (var group in Groups)
        {
            _flatItems.Add(FlatEntry.ForGroup(group));
            foreach (var subgroup in group.Subgroups)
            {
                _flatItems.Add(FlatEntry.ForSubgroup(subgroup, group));
                foreach (var item in subgroup.Items)
                {
                    _flatItems.Add(FlatEntry.ForItem(item));
                }
            }
        }
        // Start selection on first item (not header)
        _selectedIndex = _flatItems.FindIndex(entry => entry.Kind == FlatEntryKind.Item);
        if (_selectedIndex < 0) _selectedIndex = 0;
    }

    private int FindNextItem(int fromIndex, int direction)
    {
        var idx = fromIndex + direction;
        while (idx >= 0 && idx < _filteredItems.Count)
        {
            if (_filteredItems[idx].Kind == FlatEntryKind.Item)
            {
                return idx;
            }
            idx += direction;
        }
        return fromIndex; // Stay at current if no item found
    }

    private void FilterItems(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _filteredItems = [.. _flatItems];
            SelectFirstItem();
            return;
        }

        var lowerQuery = query.ToLowerInvariant();
        var matchingItems = new HashSet<ResourceItem>();
        var matchingSubgroups = new HashSet<ResourceSubgroup>();
        var matchingGroups = new HashSet<ResourceGroup>();

        foreach (var entry in _flatItems)
        {
            if (entry.Kind == FlatEntryKind.Item)
            {
                var item = entry.Item!;
                if (item.DisplayName.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                    item.ResourceType.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                    item.Path.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                {
                    matchingItems.Add(item);
                }
            }
        }

        // Find which subgroups and groups contain matching items
        foreach (var group in Groups)
        {
            foreach (var subgroup in group.Subgroups)
            {
                foreach (var item in subgroup.Items)
                {
                    if (matchingItems.Contains(item))
                    {
                        matchingSubgroups.Add(subgroup);
                        matchingGroups.Add(group);
                    }
                }
            }
        }

        _filteredItems = [];
        foreach (var entry in _flatItems)
        {
            if (entry.Kind == FlatEntryKind.Group && matchingGroups.Contains(entry.Group!))
            {
                _filteredItems.Add(entry);
            }
            else if (entry.Kind == FlatEntryKind.Subgroup && matchingSubgroups.Contains(entry.Subgroup!))
            {
                _filteredItems.Add(entry);
            }
            else if (entry.Kind == FlatEntryKind.Item && matchingItems.Contains(entry.Item!))
            {
                _filteredItems.Add(entry);
            }
        }

        SelectFirstItem();
    }

    private void SelectFirstItem()
    {
        var firstItemIndex = _filteredItems.FindIndex(entry => entry.Kind == FlatEntryKind.Item);
        _selectedIndex = firstItemIndex >= 0 ? firstItemIndex : 0;
    }

    public void UpdateItem(ResourceItem item, bool enabled)
    {
        item.Enabled = enabled;
        // Update in groups too
        foreach (var group in Groups)
        {
            foreach (var subgroup in group.Subgroups)
            {
                var found = subgroup.Items.FirstOrDefault(
                    i => i.Path == item.Path && i.ResourceType == item.ResourceType);
                if (found is not null)
                {
                    found.Enabled = enabled;
                    return;
                }
            }
        }
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var lines = new List<string>();

        // Search input
        lines.AddRange(_searchInput.Render(width));
        lines.Add("");

        if (_filteredItems.Count == 0)
        {
            lines.Add(theme.Fg("muted", "  No resources found"));
            return [.. lines];
        }

        // Calculate visible range
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (_maxVisible / 2), _filteredItems.Count - _maxVisible));
        var endIndex = Math.Min(startIndex + _maxVisible, _filteredItems.Count);

        for (var i = startIndex; i < endIndex; i++)
        {
            var entry = _filteredItems[i];
            var isSelected = i == _selectedIndex;

            if (entry.Kind == FlatEntryKind.Group)
            {
                // Main group header (no cursor)
                var inherited = _writeScope == "project" && entry.Group!.Scope == "user";
                var label = theme.Bold($"{entry.Group!.Label}{(inherited ? " · inherited global" : "")}");
                var groupLine = theme.Fg(inherited ? "dim" : "accent", label);
                lines.Add(TextLayout.TruncateToWidth($"  {groupLine}", width, ""));
            }
            else if (entry.Kind == FlatEntryKind.Subgroup)
            {
                // Subgroup header (indented, no cursor)
                var color = _writeScope == "project" && entry.Group!.Scope == "user" ? "dim" : "muted";
                var subgroupLine = theme.Fg(color, entry.Subgroup!.Label);
                lines.Add(TextLayout.TruncateToWidth($"    {subgroupLine}", width, ""));
            }
            else
            {
                // Resource item (cursor only on items)
                var item = entry.Item!;
                var cursor = isSelected ? "> " : "  ";
                var dimmed = IsDimmedItem(item);
                var nameText = isSelected && !dimmed ? theme.Bold(item.DisplayName) : item.DisplayName;
                var name = dimmed ? theme.Fg("dim", nameText) : nameText;
                lines.Add(TextLayout.TruncateToWidth(
                    $"{cursor}    {RenderCheckbox(item)} {name}{GetItemSuffix(item)}", width, "..."));
            }
        }

        // Scroll indicator
        if (startIndex > 0 || endIndex < _filteredItems.Count)
        {
            var itemCount = _filteredItems.Count(entry => entry.Kind == FlatEntryKind.Item);
            var currentItemIndex =
                _filteredItems.Take(_selectedIndex).Count(entry => entry.Kind == FlatEntryKind.Item) + 1;
            lines.Add(theme.Fg("dim", $"  ({currentItemIndex}/{itemCount})"));
        }

        return [.. lines];
    }

    public void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();

        if (kb.Matches(data, TuiKeybindingIds.SelectUp))
        {
            _selectedIndex = FindNextItem(_selectedIndex, -1);
            return;
        }
        if (kb.Matches(data, TuiKeybindingIds.SelectDown))
        {
            _selectedIndex = FindNextItem(_selectedIndex, 1);
            return;
        }
        if (kb.Matches(data, TuiKeybindingIds.SelectPageUp))
        {
            // Jump up by maxVisible, then find nearest item
            var target = Math.Max(0, _selectedIndex - _maxVisible);
            while (target < _filteredItems.Count && _filteredItems[target].Kind != FlatEntryKind.Item)
            {
                target++;
            }
            if (target < _filteredItems.Count)
            {
                _selectedIndex = target;
            }
            return;
        }
        if (kb.Matches(data, TuiKeybindingIds.SelectPageDown))
        {
            // Jump down by maxVisible, then find nearest item
            var target = Math.Min(_filteredItems.Count - 1, _selectedIndex + _maxVisible);
            while (target >= 0 && _filteredItems[target].Kind != FlatEntryKind.Item)
            {
                target--;
            }
            if (target >= 0)
            {
                _selectedIndex = target;
            }
            return;
        }
        if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            OnCancel?.Invoke();
            return;
        }
        if (Keys.MatchesKey(data, "ctrl+c"))
        {
            OnExit?.Invoke();
            return;
        }
        if (kb.Matches(data, TuiKeybindingIds.InputTab))
        {
            OnSwitchMode?.Invoke();
            return;
        }
        if (data == " " || kb.Matches(data, TuiKeybindingIds.SelectConfirm))
        {
            // TS indexes a possibly-empty array here and lets `undefined` fall through the
            // optional chain; the C# guard reproduces that no-op.
            if (_selectedIndex < 0 || _selectedIndex >= _filteredItems.Count)
            {
                return;
            }
            var entry = _filteredItems[_selectedIndex];
            if (entry.Kind == FlatEntryKind.Item &&
                (_writeScope == "project" || GetItemScope(entry.Item!) == "user"))
            {
                var newEnabled = ToggleResource(entry.Item!);
                if (newEnabled is { } enabled)
                {
                    UpdateItem(entry.Item!, enabled);
                    OnToggle?.Invoke(entry.Item!, enabled);
                }
            }
            return;
        }

        // Pass to search input
        _searchInput.HandleInput(data);
        FilterItems(_searchInput.GetValue());
    }

    private bool? ToggleResource(ResourceItem item)
    {
        if (_writeScope == "project")
        {
            var state = GetNextOverrideState(item);
            if (!SetProjectResourceOverride(item, state)) return null;
            return state == "inherit" ? GetInheritedEnabled(item) : state == "load";
        }

        var enabled = !item.Enabled;
        if (item.Metadata.Origin == "top-level")
        {
            ToggleTopLevelResource(item, enabled);
        }
        else
        {
            TogglePackageResource(item, enabled);
        }
        return enabled;
    }

    private void ToggleTopLevelResource(ResourceItem item, bool enabled)
    {
        var scope = item.Metadata.Scope;
        var settings = scope == "project" ? _settingsManager.GetProjectSettings() : _settingsManager.GetGlobalSettings();

        var current = GetTopLevelPaths(settings, item.ResourceType);

        // Generate pattern for this resource
        var pattern = GetResourcePattern(item);
        var disablePattern = $"-{pattern}";
        var enablePattern = $"+{pattern}";

        // Filter out existing patterns for this resource
        var updated = current.Where(p =>
        {
            var stripped = p.StartsWith('!') || p.StartsWith('+') || p.StartsWith('-') ? p[1..] : p;
            return stripped != pattern;
        }).ToList();

        if (enabled)
        {
            updated.Add(enablePattern);
        }
        else
        {
            updated.Add(disablePattern);
        }

        SetTopLevelPaths(scope, item.ResourceType, updated);
    }

    private void SetTopLevelPaths(string scope, string key, IReadOnlyList<string> paths)
    {
        if (scope == "project")
        {
            if (key == "extensions") _settingsManager.SetProjectExtensionPaths(paths);
            else if (key == "skills") _settingsManager.SetProjectSkillPaths(paths);
            else if (key == "prompts") _settingsManager.SetProjectPromptTemplatePaths(paths);
            else _settingsManager.SetProjectThemePaths(paths);
        }
        else
        {
            if (key == "extensions") _settingsManager.SetExtensionPaths(paths);
            else if (key == "skills") _settingsManager.SetSkillPaths(paths);
            else if (key == "prompts") _settingsManager.SetPromptTemplatePaths(paths);
            else _settingsManager.SetThemePaths(paths);
        }
    }

    private static IReadOnlyList<string> GetTopLevelPaths(Settings settings, string key) => key switch
    {
        "extensions" => settings.Extensions ?? [],
        "skills" => settings.Skills ?? [],
        "prompts" => settings.Prompts ?? [],
        _ => settings.Themes ?? [],
    };

    private void TogglePackageResource(ResourceItem item, bool enabled)
    {
        var scope = item.Metadata.Scope;
        var settings = scope == "project" ? _settingsManager.GetProjectSettings() : _settingsManager.GetGlobalSettings();

        var packages = new List<JsonNode?>(settings.Packages ?? []);
        var pkgIndex = packages.FindIndex(pkg =>
        {
            var source = pkg is JsonObject obj ? obj["source"]?.GetValue<string>() : pkg?.GetValue<string>();
            return source is not null && source == item.Metadata.Source;
        });

        if (pkgIndex == -1) return;

        JsonObject pkg;
        if (packages[pkgIndex] is JsonObject existing)
        {
            pkg = existing;
        }
        else
        {
            // Convert string to object form if needed
            pkg = new JsonObject { ["source"] = packages[pkgIndex]?.GetValue<string>() };
            packages[pkgIndex] = pkg;
        }

        // Get the resource array for this type
        var arrayKey = item.ResourceType;
        var current = ToStringList(pkg[arrayKey]);

        // Generate pattern relative to package root
        var pattern = GetPackageResourcePattern(item);
        var disablePattern = $"-{pattern}";
        var enablePattern = $"+{pattern}";

        // Filter out existing patterns for this resource
        var updated = current.Where(p =>
        {
            var stripped = p.StartsWith('!') || p.StartsWith('+') || p.StartsWith('-') ? p[1..] : p;
            return stripped != pattern;
        }).ToList();

        if (enabled)
        {
            updated.Add(enablePattern);
        }
        else
        {
            updated.Add(disablePattern);
        }

        pkg[arrayKey] = updated.Count > 0 ? ToJsonArray(updated) : null;

        // Clean up empty filter object
        var hasFilters = ConfigSelectorStatics.ResourceTypes.Any(k => pkg[k] is not null);
        if (!hasFilters)
        {
            packages[pkgIndex] = pkg["source"]?.DeepClone();
        }

        if (scope == "project")
        {
            _settingsManager.SetProjectPackages(packages);
        }
        else
        {
            _settingsManager.SetPackages(packages);
        }
    }

    private string RenderCheckbox(ResourceItem item)
    {
        var theme = ThemeRuntime.Current;
        if (_writeScope == "project")
        {
            var state = GetProjectOverrideState(item);
            if (state == "load") return theme.Fg("success", "[+]");
            if (state == "unload") return theme.Fg("warning", "[-]");
            return theme.Fg("dim", item.Enabled ? "[x]" : "[ ]");
        }
        return item.Enabled ? theme.Fg("success", "[x]") : theme.Fg("dim", "[ ]");
    }

    private string GetItemSuffix(ResourceItem item)
    {
        var theme = ThemeRuntime.Current;
        if (_writeScope != "project") return "";
        var state = GetProjectOverrideState(item);
        if (state == "load") return theme.Fg("muted", "  project load");
        if (state == "unload") return theme.Fg("muted", "  project unload");
        return IsInheritedGlobalItem(item) ? theme.Fg("dim", "  inherited global") : "";
    }

    private bool IsDimmedItem(ResourceItem item) =>
        _writeScope == "project" && IsInheritedGlobalItem(item) && GetProjectOverrideState(item) == "inherit";

    private bool SetProjectResourceOverride(ResourceItem item, string state) =>
        item.Metadata.Origin == "top-level"
            ? SetProjectTopLevelOverride(item, state)
            : SetProjectPackageOverride(item, state);

    private bool SetProjectTopLevelOverride(ResourceItem item, string state)
    {
        var current = GetTopLevelPaths(_settingsManager.GetProjectSettings(), item.ResourceType);
        var pattern = IsInheritedGlobalItem(item) ? item.Path : GetResourcePatternForScope(item, "project");
        var patterns = GetTopLevelOverridePatterns(item, "project");
        var updated = current.Where(entry =>
        {
            var target = GetPatternEntryTarget(entry);
            if ((entry.StartsWith('!') || entry.StartsWith('+') || entry.StartsWith('-')) && patterns.Contains(target))
                return false;
            return !(state == "inherit" && IsInheritedGlobalItem(item) && target == pattern);
        }).ToList();
        if (state != "inherit")
        {
            // Project entries name inherited files to override them. Built-in paths need no entry.
            if (IsInheritedGlobalItem(item) && item.Metadata.Source != "builtin" && !updated.Contains(pattern, StringComparer.Ordinal))
                updated.Add(pattern);
            updated.Add($"{(state == "load" ? "+" : "-")}{pattern}");
        }
        SetProjectTopLevelPaths(item.ResourceType, updated);
        return true;
    }

    private void SetProjectTopLevelPaths(string key, IReadOnlyList<string> paths)
    {
        if (key == "extensions") _settingsManager.SetProjectExtensionPaths(paths);
        else if (key == "skills") _settingsManager.SetProjectSkillPaths(paths);
        else if (key == "prompts") _settingsManager.SetProjectPromptTemplatePaths(paths);
        else _settingsManager.SetProjectThemePaths(paths);
    }

    private bool SetProjectPackageOverride(ResourceItem item, string state)
    {
        var packages = new List<JsonNode?>(_settingsManager.GetProjectSettings().Packages ?? []);
        var pkgIndex = packages.FindIndex(pkg =>
            PackageSourceStringMatches(
                item.Metadata.Source,
                GetItemScope(item),
                (pkg is JsonObject obj ? obj["source"]?.GetValue<string>() : pkg?.GetValue<string>()) ?? "",
                "project"));
        if (pkgIndex == -1)
        {
            if (state == "inherit") return false;
            packages.Add(CreatePackageOverrideSource(item));
            pkgIndex = packages.Count - 1;
        }
        if (pkgIndex < 0 || pkgIndex >= packages.Count || packages[pkgIndex] is null) return false;
        JsonObject pkg;
        if (packages[pkgIndex] is JsonObject existing)
        {
            pkg = existing;
        }
        else
        {
            pkg = new JsonObject { ["source"] = packages[pkgIndex]?.GetValue<string>() };
            packages[pkgIndex] = pkg;
        }
        var pattern = GetPackageResourcePattern(item);
        var updated = ToStringList(pkg[item.ResourceType])
            .Where(entry => GetPatternEntryTarget(entry) != pattern)
            .ToList();
        if (state != "inherit") updated.Add($"{(state == "load" ? "+" : "-")}{pattern}");
        pkg[item.ResourceType] = updated.Count > 0 ? ToJsonArray(updated) : null;
        if (!ConfigSelectorStatics.ResourceTypes.Any(key => pkg[key] is not null))
        {
            if (pkg["autoload"]?.GetValue<bool>() == false) packages.RemoveAt(pkgIndex);
            else packages[pkgIndex] = pkg["source"]?.DeepClone();
        }
        _settingsManager.SetProjectPackages(packages);
        return true;
    }

    private string GetNextOverrideState(ResourceItem item)
    {
        var state = GetProjectOverrideState(item);
        var inheritedEnabled = GetInheritedEnabled(item);
        if (state == "inherit") return inheritedEnabled ? "unload" : "load";
        if (state == "unload") return inheritedEnabled ? "load" : "inherit";
        return inheritedEnabled ? "inherit" : "unload";
    }

    private string GetProjectOverrideState(ResourceItem item)
    {
        if (_writeScope != "project") return "inherit";
        if (item.Metadata.Origin == "top-level")
        {
            return GetOverrideStateFromEntries(
                GetTopLevelPaths(_settingsManager.GetProjectSettings(), item.ResourceType),
                GetTopLevelOverridePatterns(item, "project"),
                false);
        }
        var pkg = FindMatchingPackageSource(item, "project");
        if (pkg is not JsonObject pkgObj) return "inherit";
        var entries = pkgObj[item.ResourceType];
        if (entries is null) return "inherit";
        return GetOverrideStateFromEntries(
            ToStringList(entries),
            new HashSet<string>(StringComparer.Ordinal) { GetPackageResourcePattern(item) },
            pkgObj["autoload"]?.GetValue<bool>() != false);
    }

    private static string GetOverrideStateFromEntries(
        IReadOnlyList<string> entries,
        IReadOnlySet<string> patterns,
        bool emptyArrayIsUnload)
    {
        if (entries.Count == 0 && emptyArrayIsUnload) return "unload";
        var state = "inherit";
        foreach (var entry in entries)
        {
            if (!patterns.Contains(GetPatternEntryTarget(entry))) continue;
            if (entry.StartsWith('!') || entry.StartsWith('-')) state = "unload";
            else state = "load";
        }
        return state;
    }

    private bool GetInheritedEnabled(ResourceItem item)
    {
        // TS `map.get(key) ?? fallback`: a stored `false` must not fall through to the fallback.
        return _inheritedEnabledByKey.TryGetValue(GetResourceItemKey(item), out var stored)
            ? stored
            : GetItemScope(item) == "user" ? item.Enabled : true;
    }

    private bool IsInheritedGlobalItem(ResourceItem item) =>
        GetItemScope(item) == "user" || _inheritedEnabledByKey.ContainsKey(GetResourceItemKey(item));

    private HashSet<string> GetTopLevelOverridePatterns(ResourceItem item, string scope)
    {
        var baseDir = GetTopLevelBaseDir(scope);
        var patterns = new HashSet<string>(StringComparer.Ordinal)
        {
            GetResourcePatternForScope(item, scope),
            item.Path,
            NodePath.Relative(baseDir, item.Path),
        };
        if (item.Metadata.BaseDir is { } metadataBaseDir)
        {
            patterns.Add(NodePath.Relative(metadataBaseDir, item.Path));
        }
        return patterns;
    }

    private string GetResourcePatternForScope(ResourceItem item, string scope)
    {
        var sourceScope = GetItemScope(item);
        if (scope != sourceScope || item.Metadata.Source == "builtin") return item.Path;
        var baseDir = item.Metadata.BaseDir ?? GetTopLevelBaseDir(sourceScope);
        return NodePath.Relative(baseDir, item.Path);
    }

    private JsonObject CreatePackageOverrideSource(ResourceItem item)
    {
        var source = item.Metadata.Source;
        if (!Paths.IsLocalPath(source)) return new JsonObject { ["source"] = source, ["autoload"] = false };
        var sourcePath = Paths.ResolvePath(
            source, GetTopLevelBaseDir(GetItemScope(item)), new PathInputOptions(Trim: true));
        var relative = NodePath.Relative(GetTopLevelBaseDir("project"), sourcePath);
        return new JsonObject { ["source"] = relative.Length == 0 ? "." : relative, ["autoload"] = false };
    }

    private bool PackageSourceStringMatches(
        string leftSource,
        string leftScope,
        string rightSource,
        string rightScope)
    {
        if (leftSource == rightSource) return true;
        if (!Paths.IsLocalPath(leftSource) || !Paths.IsLocalPath(rightSource)) return false;
        var left = Paths.ResolvePath(leftSource, GetTopLevelBaseDir(leftScope), new PathInputOptions(Trim: true));
        var right = Paths.ResolvePath(rightSource, GetTopLevelBaseDir(rightScope), new PathInputOptions(Trim: true));
        return left == right;
    }

    private JsonNode? FindMatchingPackageSource(ResourceItem item, string targetScope)
    {
        var settings = targetScope == "project"
            ? _settingsManager.GetProjectSettings()
            : _settingsManager.GetGlobalSettings();
        return (settings.Packages ?? []).FirstOrDefault(pkg =>
            PackageSourceStringMatches(
                item.Metadata.Source,
                GetItemScope(item),
                (pkg is JsonObject obj ? obj["source"]?.GetValue<string>() : pkg?.GetValue<string>()) ?? "",
                targetScope));
    }

    private static string GetPatternEntryTarget(string entry) =>
        entry.StartsWith('!') || entry.StartsWith('+') || entry.StartsWith('-') ? entry[1..] : entry;

    private string GetResourceItemKey(ResourceItem item) =>
        $"{item.ResourceType}:{Paths.CanonicalizePath(item.Path)}";

    private static string GetItemScope(ResourceItem item) =>
        item.Metadata.Scope == "project" ? "project" : "user";

    private string GetTopLevelBaseDir(string scope) =>
        scope == "project" ? NodePath.Join(_cwd, Config.ConfigDirName) : _agentDir;

    private string GetResourcePattern(ResourceItem item)
    {
        if (item.Metadata.Source == "builtin") return item.Path;
        var scope = item.Metadata.Scope;
        var baseDir = item.Metadata.BaseDir ?? GetTopLevelBaseDir(scope);
        return NodePath.Relative(baseDir, item.Path);
    }

    private string GetPackageResourcePattern(ResourceItem item)
    {
        var baseDir = item.Metadata.BaseDir ?? NodePath.Dirname(item.Path);
        return NodePath.Relative(baseDir, item.Path);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static IReadOnlyList<string> ToStringList(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        return [.. array.Select(entry => entry?.GetValue<string>() ?? "")];
    }
}

/// <summary>
/// Main config selector component (TS <c>ConfigSelectorComponent</c>).
/// </summary>
public sealed class ConfigSelectorComponent : Container, IFocusable
{
    private readonly ConfigSelectorHeader _header;
    private readonly ResourceList _resourceList;
    private string _writeScope;

    private bool _focused;
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _resourceList.Focused = value;
        }
    }

    public ConfigSelectorComponent(
        ScopedResolvedResources resolvedPaths,
        SettingsManager settingsManager,
        string cwd,
        string agentDir,
        Action onClose,
        Action onExit,
        Action requestRender,
        int? terminalHeight = null,
        string writeScope = "global",
        bool projectModeAvailable = true)
    {
        _writeScope = writeScope;
        var groupsByScope = new Dictionary<string, List<ResourceGroup>>(StringComparer.Ordinal)
        {
            ["global"] = ConfigSelectorStatics.BuildGroups(resolvedPaths.Global, agentDir),
            ["project"] = ConfigSelectorStatics.BuildGroups(resolvedPaths.Project, agentDir),
        };

        // Add header
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        _header = new ConfigSelectorHeader(_writeScope, projectModeAvailable);
        AddChild(_header);
        AddChild(new Spacer(1));

        // Resource list
        _resourceList = new ResourceList(
            groupsByScope,
            settingsManager,
            cwd,
            agentDir,
            terminalHeight,
            _writeScope);
        _resourceList.OnCancel = onClose;
        _resourceList.OnExit = onExit;
        _resourceList.OnToggle = (_, _) => requestRender();
        if (projectModeAvailable)
        {
            _resourceList.OnSwitchMode = () =>
            {
                SwitchWriteScope();
                requestRender();
            };
        }
        AddChild(_resourceList);

        // Bottom border
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    private void SwitchWriteScope()
    {
        _writeScope = _writeScope == "global" ? "project" : "global";
        _header.SetWriteScope(_writeScope);
        _resourceList.SetWriteScope(_writeScope);
    }

    internal ResourceList GetResourceList() => _resourceList;
}
