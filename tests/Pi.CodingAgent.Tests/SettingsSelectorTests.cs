// ============================================================================
// Tests for modes/interactive/components/settings-selector.ts
//
// All four upstream cases are transcribed; the rest cover the item insertion
// order, the warnings/model-thinking submenu completions and the theme submenu
// apply/cancel paths the upstream suite leaves to the interactive-mode wiring.
// ============================================================================

using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SettingsSelectorTests : SelectorTestFixture
{
    private const string Provider = "acme";

    private static ModelSpec ThinkingModel() => new()
    {
        Id = "thinking-model",
        Name = "Thinking Model",
        Api = "openai-completions",
        Provider = Provider,
        BaseUrl = "https://example.invalid",
        Reasoning = true,
    };

    // ------------------------------------------------ upstream: settings-selector.test.ts

    [Fact]
    public void CyclesThroughFullscreenSettings()
    {
        var exitOutput = new List<string>();
        var scrollbar = new List<ScrollViewScrollbar>();
        var copyOnSelect = new List<bool>();
        var wheelLines = new List<WheelScrollLines>();
        var config = new SettingsConfig
        {
            FullscreenExitOutput = "transcript",
            FullscreenScrollbar = ScrollViewScrollbar.Auto,
            FullscreenCopyOnSelect = true,
            FullscreenWheelScrollLines = new WheelScrollLines(7),
        };
        var callbacks = new SettingsCallbacks
        {
            OnFullscreenExitOutputChange = value => exitOutput.Add(value),
            OnFullscreenScrollbarChange = value => scrollbar.Add(value),
            OnFullscreenCopyOnSelectChange = value => copyOnSelect.Add(value),
            OnFullscreenWheelScrollLinesChange = value => wheelLines.Add(value),
        };

        void Cycle(string label, int count)
        {
            var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();
            foreach (var character in label)
            {
                list.HandleInput(character.ToString());
            }
            for (var i = 0; i < count; i++)
            {
                list.HandleInput("\r");
            }
        }

        Cycle("Fullscreen exit output", 2);
        Assert.Equal(["resume-hint", "transcript"], exitOutput);
        Cycle("Fullscreen scrollbar", 3);
        Assert.Equal([ScrollViewScrollbar.Always, ScrollViewScrollbar.Hidden, ScrollViewScrollbar.Auto], scrollbar);
        Cycle("Fullscreen copy on select", 2);
        Assert.Equal([false, true], copyOnSelect);
        // #9758: custom values from settings.json stay in the cycle.
        Cycle("Fullscreen wheel scrolling", 3);
        Assert.Equal(
        [
            new WheelScrollLines(10),
            WheelScrollLines.Auto,
            new WheelScrollLines(1),
        ], wheelLines);
    }

    [Fact]
    public void KeepsTheConfiguredFixedThemeMarkedWhileBrowsing()
    {
        var config = new SettingsConfig
        {
            CurrentTheme = "dark",
            TerminalTheme = "dark",
            AvailableThemes = ["system", "dark", "light"],
        };
        var callbacks = new SettingsCallbacks
        {
            OnThemePreview = _ => { },
            OnCancel = () => { },
        };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("theme");
        list.HandleInput("\r");
        var lines = RenderLines(list, 120);
        var systemLine = lines.First(line => line.TrimEnd().StartsWith("    system", StringComparison.Ordinal));
        var automaticLine = lines.First(line => line.TrimEnd().StartsWith("    automatic", StringComparison.Ordinal));
        Assert.StartsWith("    system", systemLine, StringComparison.Ordinal);
        Assert.Contains("Theme created from your terminal's colors", systemLine, StringComparison.Ordinal);
        Assert.StartsWith("    automatic", automaticLine, StringComparison.Ordinal);
        Assert.Contains("Use separate themes", automaticLine, StringComparison.Ordinal);
        Assert.Contains("→ ✓ dark", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput("\x1b[B");
        var output = RenderText(list, 120);
        Assert.Contains("  ✓ dark", output, StringComparison.Ordinal);
        Assert.Contains("→   light", output, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAConfiguredAutomaticThemeMarkedWhileBrowsing()
    {
        var config = new SettingsConfig
        {
            CurrentTheme = "light/dark",
            TerminalTheme = "dark",
            AvailableThemes = ["dark", "light", "other"],
        };
        var callbacks = new SettingsCallbacks
        {
            OnThemePreview = _ => { },
            OnCancel = () => { },
        };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("theme");
        list.HandleInput("\r");
        list.HandleInput("\r");
        var output = RenderText(list, 120);
        Assert.Contains("→ ✓ light", output, StringComparison.Ordinal);

        list.HandleInput("\x1b[B");
        output = RenderText(list, 120);
        Assert.Contains("  ✓ light", output, StringComparison.Ordinal);
        Assert.Contains("→   other", output, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsTheConfiguredPerModelThinkingLevelMarkedWhileBrowsing()
    {
        var model = ThinkingModel();
        var modelKey = $"{model.Provider}/{model.Id}";
        var config = new SettingsConfig
        {
            DefaultModel = modelKey,
            AvailableDefaultModels = [model],
            ThinkingLevel = "high",
            ModelThinkingLevels = new Dictionary<string, string> { [modelKey] = "medium" },
        };
        var callbacks = new SettingsCallbacks { OnCancel = () => { } };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("model-thinking");
        list.HandleInput("\r");
        list.HandleInput("\r");

        var output = RenderText(list, 120);
        Assert.Contains("→ ✓ medium", output, StringComparison.Ordinal);
        Assert.Contains("    (clear override)", output, StringComparison.Ordinal);

        list.HandleInput("\x1b[B");
        output = RenderText(list, 120);
        Assert.Contains("  ✓ medium", output, StringComparison.Ordinal);
        Assert.Contains("→   high", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- item order

    [Fact]
    public void InsertsImageEntriesAfterAutocompactWhenTheTerminalSupportsImages()
    {
        var previous = TerminalImage.GetCapabilities();
        TerminalImage.SetCapabilityOverrides(new CapabilityOverrides { Images = ImageProtocol.Iterm2 });
        try
        {
            var list = new SettingsSelectorComponent(new SettingsConfig(), new SettingsCallbacks()).GetSettingsList();
            var compact = IndexOfLabel(list, "Auto-compact");
            var showImages = IndexOfLabel(list, "Show images");
            var imageWidth = IndexOfLabel(list, "Image width");
            var autoResize = IndexOfLabel(list, "Auto-resize images");
            Assert.True(compact >= 0 && showImages > compact, "Show images must follow Auto-compact");
            Assert.True(imageWidth > showImages, "Image width must follow Show images");
            Assert.True(autoResize > imageWidth, "Auto-resize images must follow Image width");
        }
        finally
        {
            TerminalImage.SetCapabilityOverrides(new CapabilityOverrides
            {
                Images = previous.Images,
                TrueColor = previous.TrueColor,
                Hyperlinks = previous.Hyperlinks,
            });
        }
    }

    [Fact]
    public void KeepsTheInsertionOrderOfTheTailToggles()
    {
        var list = new SettingsSelectorComponent(new SettingsConfig(), new SettingsCallbacks()).GetSettingsList();
        var chain = new[]
        {
            "Skill commands", "Show hardware cursor", "Editor padding", "Output padding",
            "Autocomplete max items", "Clear on shrink", "Terminal progress",
        };
        var indexes = chain.Select(label => IndexOfLabel(list, label)).ToList();
        Assert.All(indexes, index => Assert.True(index >= 0, $"missing label in {string.Join(", ", chain)}"));
        for (var i = 1; i < indexes.Count; i++)
        {
            Assert.True(indexes[i - 1] < indexes[i], $"{chain[i - 1]} should precede {chain[i]}");
        }
    }

    // ------------------------------------------------------------ warnings submenu

    [Fact]
    public void WarningSettingsSubmenuTogglesAnthropicExtraUsage()
    {
        var warnings = new List<JsonObject>();
        var config = new SettingsConfig { Warnings = new JsonObject() };
        var callbacks = new SettingsCallbacks { OnWarningsChange = warnings.Add };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("warnings");
        list.HandleInput("\r");
        Assert.Contains("Anthropic extra usage", RenderText(list, 120), StringComparison.Ordinal);
        Assert.Contains("true", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput("\r"); // toggle true → false
        list.HandleInput("\x1b"); // close submenu (done without a value)
        Assert.Single(warnings);
        Assert.False(warnings[0]["anthropicExtraUsage"]!.GetValue<bool>());
        Assert.Contains("false", RenderText(list, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void WarningSettingsSubmenuDefaultsToTrueWhenAbsent()
    {
        var list = new SettingsSelectorComponent(
            new SettingsConfig { Warnings = new JsonObject() }, new SettingsCallbacks()).GetSettingsList();

        list.SelectItem("warnings");
        list.HandleInput("\r");
        Assert.Contains("true", RenderText(list, 120), StringComparison.Ordinal);
    }

    // ------------------------------------------------------- model-thinking submenu

    [Fact]
    public void ModelThinkingCompletionCallsChangeThenRemove()
    {
        var model = ThinkingModel();
        var modelKey = $"{model.Provider}/{model.Id}";
        var changes = new List<(string Provider, string Id, string Level)>();
        var removals = new List<(string Provider, string Id)>();
        var config = new SettingsConfig
        {
            DefaultModel = modelKey,
            AvailableDefaultModels = [model],
            ThinkingLevel = "high",
            ModelThinkingLevels = new Dictionary<string, string> { [modelKey] = "medium" },
        };
        var callbacks = new SettingsCallbacks
        {
            OnModelThinkingLevelChange = (provider, id, level) => changes.Add((provider, id, level)),
            OnModelThinkingLevelRemove = (provider, id) => removals.Add((provider, id)),
            OnCancel = () => { },
        };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("model-thinking");
        list.HandleInput("\r"); // open the stepped submenu (step 1: model picker)
        list.HandleInput("\r"); // pick the only model → step 2
        list.HandleInput("\r"); // pick "medium" (preselected) → onComplete, loop back to step 1

        Assert.Equal([(model.Provider, model.Id, "medium")], changes);

        list.HandleInput("\r"); // enter step 2 again
        list.HandleInput("\x1b[B"); // one step below the "medium" preselect ("high")
        list.HandleInput("\x1b[B"); // → (clear override)
        list.HandleInput("\r"); // pick it → onModelThinkingLevelRemove

        Assert.Equal([(model.Provider, model.Id)], removals);
    }

    [Fact]
    public void ModelThinkingEmptyCatalogShowsNoModelsAvailable()
    {
        var list = new SettingsSelectorComponent(
            new SettingsConfig { DefaultModel = "not set" }, new SettingsCallbacks()).GetSettingsList();

        list.SelectItem("model-thinking");
        list.HandleInput("\r");
        var output = RenderText(list, 120);
        Assert.Contains("No models available", output, StringComparison.Ordinal);
        Assert.Contains("Log in to a provider or configure an API key first", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- theme submenu

    [Fact]
    public void ThemeSubmenuApplyCallsOnThemeChange()
    {
        var themeChanges = new List<string>();
        var config = new SettingsConfig
        {
            CurrentTheme = "dark",
            TerminalTheme = "dark",
            AvailableThemes = ["system", "dark", "light"],
        };
        var callbacks = new SettingsCallbacks { OnThemeChange = themeChanges.Add, OnCancel = () => { } };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("theme");
        list.HandleInput("\r"); // open theme submenu (single mode, preselect "dark")
        list.HandleInput("\x1b[B"); // dark → light
        list.HandleInput("\r"); // apply "light"

        Assert.Equal(["light"], themeChanges);
    }

    [Fact]
    public void ThemeSubmenuAutomaticApplySavesLightDarkPair()
    {
        var themeChanges = new List<string>();
        var config = new SettingsConfig
        {
            CurrentTheme = "dark",
            TerminalTheme = "dark",
            AvailableThemes = ["system", "dark", "light"],
        };
        var callbacks = new SettingsCallbacks
        {
            OnThemeChange = themeChanges.Add,
            OnCancel = () => { },
        };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("theme");
        list.HandleInput("\r"); // open theme submenu (single list: system, automatic, dark, light)
        list.HandleInput("\x1b[B"); // dark → light
        list.HandleInput("\x1b[A"); // light → dark
        list.HandleInput("\x1b[A"); // dark → automatic
        list.HandleInput("\r"); // choose automatic → switch to the automatic menu
        Assert.Contains("Light theme", RenderText(list, 120), StringComparison.Ordinal);
        // With a fixed current theme, both automatic slots default to it.
        Assert.Contains("dark", RenderText(list, 120), StringComparison.Ordinal);

        list.HandleInput("\r"); // open the Light theme picker (current "dark")
        list.HandleInput("\x1b[B"); // dark → light
        list.HandleInput("\r"); // pick "light" → preview + done("light")

        list.HandleInput("\x1b[B"); // Light theme → Dark theme
        list.HandleInput("\x1b[B"); // Dark theme → Apply
        list.HandleInput("\r"); // apply → getAutomaticThemeSetting()

        Assert.Equal(["light/dark"], themeChanges);
    }

    [Fact]
    public void ThemeSubmenuCancelRestoresTheOriginalPreview()
    {
        var previews = new List<string>();
        var themeChanges = new List<string>();
        var config = new SettingsConfig
        {
            CurrentTheme = "dark",
            TerminalTheme = "dark",
            AvailableThemes = ["system", "dark", "light"],
        };
        var callbacks = new SettingsCallbacks
        {
            OnThemePreview = previews.Add,
            OnThemeChange = themeChanges.Add,
            OnCancel = () => { },
        };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("theme");
        list.HandleInput("\r"); // open theme submenu
        list.HandleInput("\x1b[B"); // dark → light (fires a preview)
        list.HandleInput("\x1b"); // cancel → restore the original setting

        Assert.Equal("dark", previews[^1]);
        Assert.Empty(themeChanges);
        Assert.Contains("Color theme for the interface", RenderText(list, 120), StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- value cycles

    [Fact]
    public void HttpIdleTimeoutCycleMapsLabelsBackToMs()
    {
        var recorder = new List<long>();
        var config = new SettingsConfig { HttpIdleTimeoutMs = 300_000 };
        var callbacks = new SettingsCallbacks { OnHttpIdleTimeoutMsChange = recorder.Add };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("http-idle-timeout");
        Assert.Contains("5 min", RenderText(list, 120), StringComparison.Ordinal);
        list.HandleInput("\r"); // 5 min → disabled
        Assert.Equal([0L], recorder);
        list.HandleInput("\r"); // disabled → 30 sec
        Assert.Equal([0L, 30_000L], recorder);
    }

    [Fact]
    public void DefaultProjectTrustCyclesByLabel()
    {
        var changes = new List<string>();
        var config = new SettingsConfig { DefaultProjectTrust = "ask" };
        var callbacks = new SettingsCallbacks { OnDefaultProjectTrustChange = changes.Add };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("default-project-trust");
        list.HandleInput("\r"); // Ask → Always trust
        Assert.Equal(["always"], changes);
        list.HandleInput("\r"); // Always trust → Never trust
        Assert.Equal(["always", "never"], changes);
    }

    [Fact]
    public void QuietStartupCyclesThroughTheHeaderState()
    {
        var changes = new List<string>();
        var config = new SettingsConfig { QuietStartup = "false" };
        var callbacks = new SettingsCallbacks { OnQuietStartupChange = changes.Add };
        var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();

        list.SelectItem("quiet-startup");
        list.HandleInput("\r"); // false → true
        Assert.Equal(["true"], changes);
        list.HandleInput("\r"); // true → header
        Assert.Equal(["true", "header"], changes);
        list.HandleInput("\r"); // header → false
        Assert.Equal(["true", "header", "false"], changes);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Render row index of the first line whose trimmed content starts with
    /// <paramref name="label"/>, or -1.</summary>
    private static int IndexOfLabel(SettingsList list, string label)
    {
        var lines = RenderLines(list, 120);
        for (var i = 0; i < lines.Length; i++)
        {
            var content = lines[i].TrimEnd().TrimStart('→', ' ');
            if (content.StartsWith(label, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }
}
