// ============================================================================
// Tests for modes/interactive/components/extension-editor.ts
// Upstream has no test file for this component; the vectors lock the behaviour
// of the source itself (title/description/hint layout, input routing, the
// injected app keybinding for the external editor, and the submit wiring).
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// `KeybindingsManager` exists in both namespaces (the TUI's base manager and the app's layered one);
// the app one is what TS's `core/keybindings.ts` exports.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Same fixture pair as the selector tests (TS <c>initTheme("dark")</c> +
/// <c>setKeybindings(new KeybindingsManager())</c>) plus the external-editor spawn seam reset.
/// </summary>
public sealed class ExtensionEditorTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousKeybindings = GlobalKeybindings.Get();
    private readonly Func<string, IReadOnlyList<string>, string, Task<int?>>? _previousOverride =
        ExternalEditor.SpawnOverride;

    public ExtensionEditorTests()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new CoreKeybindingsManager());
    }

    public void Dispose()
    {
        ExternalEditor.SpawnOverride = _previousOverride;
        GlobalKeybindings.Set(_previousKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
        GC.SuppressFinalize(this);
    }

    private static ExtensionEditorComponent Create(
        Action<string>? onSubmit = null,
        Action? onCancel = null,
        ExtensionEditorOptions? options = null,
        string? prefill = null,
        CoreKeybindingsManager? keybindings = null,
        string? externalEditorCommand = "notepad")
    {
        var submitted = new List<string>();
        var cancelled = 0;
        return new ExtensionEditorComponent(
            new FakeTui(),
            keybindings ?? new CoreKeybindingsManager(),
            "Extension config",
            prefill,
            onSubmit ?? submitted.Add,
            onCancel ?? (Action)(() => cancelled++),
            options,
            externalEditorCommand);
    }

    private static string RenderText(IComponent component, int width) =>
        AnsiText.StripAnsi(string.Join("\n", component.Render(width)));

    private static async Task WaitUntilRenderedAsync(IComponent component, string expected, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (RenderText(component, 120).Contains(expected))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"render never contained '{expected}'");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void RendersTitleDescriptionHintAndPrefill()
    {
        var editor = Create(
            options: new ExtensionEditorOptions { Description = "Enter the skill metadata" },
            prefill: "name: demo");

        var text = RenderText(editor, 120);
        Assert.Contains("Extension config", text);
        Assert.Contains("Enter the skill metadata", text);
        Assert.Contains("name: demo", text);
        // The four hints: submit / newline / cancel / external editor.
        Assert.Contains("submit", text);
        Assert.Contains("newline", text);
        Assert.Contains("cancel", text);
        Assert.Contains("external editor", text);
    }

    [Fact]
    public void CancelKeybindingInvokesOnCancel()
    {
        var cancelled = 0;
        var editor = Create(onCancel: () => cancelled++);

        editor.HandleInput("\x1b"); // escape

        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void PrintableInputIsForwardedToTheEditor()
    {
        var editor = Create();

        editor.HandleInput("h");
        editor.HandleInput("i");

        Assert.Contains("hi", RenderText(editor, 120));
    }

    [Fact]
    public void EnterSubmitsTheEditorContent()
    {
        var submitted = new List<string>();
        var editor = Create(onSubmit: submitted.Add);

        editor.HandleInput("a");
        editor.HandleInput("b");
        editor.HandleInput("\r");

        var value = Assert.Single(submitted);
        Assert.Equal("ab", value);
    }

    [Fact]
    public async Task InjectedExternalEditorKeybindingOpensTheExternalEditor()
    {
        // app.editor.external rebound to ctrl+e on the *injected* manager; ctrl+g must then fall
        // through to the editor instead of opening the external editor.
        var keybindings = new CoreKeybindingsManager(new KeybindingsConfig
        {
            [AppKeybindings.AppEditorExternal] = "ctrl+e",
        });
        var editor = Create(keybindings: keybindings, prefill: "draft");
        editor.HandleInput("\x07"); // ctrl+g — no longer the external-editor binding
        Assert.Contains("draft", RenderText(editor, 120));

        ExternalEditor.SpawnOverride = (_, _, filePath) =>
        {
            File.WriteAllText(filePath, "rewritten");
            return Task.FromResult<int?>(0);
        };

        editor.HandleInput("\x05"); // ctrl+e

        await WaitUntilRenderedAsync(editor, "rewritten");
    }

    [Fact]
    public async Task FailedExternalEditKeepsTheOriginalContent()
    {
        var editor = Create(prefill: "draft");
        ExternalEditor.SpawnOverride = (_, _, _) => Task.FromResult<int?>(1);

        editor.HandleInput("\x07"); // ctrl+g (default binding)

        await WaitUntilRenderedAsync(editor, "draft");
        Assert.DoesNotContain("rewritten", RenderText(editor, 120));
    }
}
