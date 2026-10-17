// ============================================================================
// Tests for the InteractiveMode class assembly, slice 1 of 4f-4m.
//
// Upstream has no test file for interactive-mode.ts. The vectors are derived from the source:
// the constructor wiring (containers / editor / footer / keybindings / theme controller), the
// chat notices, the status-indicator chain, the extension widget/footer/header swaps, the custom
// editor swap, the extension dialogs and the TUI mode switch are exercised against a real
// AgentSessionRuntime (same fixture path as AgentSessionRuntimeTests) with a StringTerminal so
// nothing touches the process console.
//
// The class members under test are TS-private; the port widens exactly those to internal
// (test-only seam, see InteractiveMode.cs).
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeTests : SelectorTestFixture, IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly string _sessionDir;
    private readonly StringTerminal _terminal = new();

    public InteractiveModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-imode-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        _sessionDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_agentDir);
    }

    public new void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // 夹具：与 AgentSessionRuntimeTests 同款装配路径（services builder → runtime host）。
    // ==================================================================

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    private sealed class StubApiKeyAuth : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential?.Key ?? "stub-key" },
                Source = "stub-env",
            });
    }

    /// <summary>以一条终态消息立即结束流。</summary>
    private sealed class RecordingProvider : IProvider
    {
        public ModelSpec Model { get; } = new()
        {
            Id = "stub-chat",
            Name = "stub-chat",
            Api = "stub-api",
            Provider = "stub",
            BaseUrl = "https://stub.invalid",
            Type = ModelType.Chat,
            Input = [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = 1_000,
            MaxTokens = 100,
            Reasoning = true,
        };

        public string Id => "stub";

        public string Name => "Stub";

        public string? BaseUrl => null;

        public ProviderAuth? Auth => new() { ApiKey = new StubApiKeyAuth() };

        public IReadOnlyList<ModelSpec> GetModels() => [Model];

        public IReadOnlyList<ModelSpec> GetAllModels() => [Model];

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Respond(model);

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Respond(model);

        private static IAssistantMessageEventStream Respond(ModelSpec model)
        {
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                Content: [],
                StopReason: StopReason.Stop,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return stream;
        }
    }

    private async Task<AgentSessionRuntime> CreateHostAsync()
    {
        var provider = new RecordingProvider();
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
            AuthContext = new StubAuthContext(name => name == "PI_OFFLINE" ? "1" : null),
        });
        runtime.RegisterNativeProvider(provider);
        if (runtime.PendingBackgroundRefresh is { } pending)
        {
            await pending;
        }

        var extension = Ext("interactive-mode-noop", _ => { });
        CreateAgentSessionRuntimeFactory factory = async options =>
        {
            var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
                new CreateAgentSessionServicesOptions
                {
                    Cwd = options.Cwd,
                    AgentDir = _agentDir,
                    ModelRuntime = runtime,
                    ResourceLoaderOptions = new ResourceLoaderExtraOptions
                    {
                        NoSkills = true,
                        NoPromptTemplates = true,
                        NoThemes = true,
                        NoContextFiles = true,
                        ExtensionFactories = [extension],
                    },
                });
            var created = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
                new CreateAgentSessionFromServicesOptions
                {
                    Services = services,
                    SessionManager = options.SessionManager,
                    SessionStartEvent = options.SessionStartEvent,
                    Model = provider.Model,
                },
                options.Cwd);
            return new CreateAgentSessionRuntimeResult
            {
                Session = created.Session,
                ExtensionsResult = created.ExtensionsResult,
                ModelFallbackMessage = created.ModelFallbackMessage,
                Services = services,
                Diagnostics = services.Diagnostics,
            };
        };

        var host = await AgentSessionRuntime.CreateAgentSessionRuntimeAsync(
            factory,
            new CreateAgentSessionRuntimeOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                SessionManager = SessionManager.Create(_cwd, _sessionDir),
            });
        await host.Session.BindExtensionsAsync(new ExtensionBindings());
        return host;
    }

    private async Task<InteractiveMode> CreateModeAsync(
        Func<InteractiveModeOptions, InteractiveModeOptions>? configureOptions = null,
        AgentSessionRuntime? host = null)
    {
        host ??= await CreateHostAsync();
        var options = new InteractiveModeOptions { Terminal = _terminal };
        if (configureOptions is not null)
        {
            options = configureOptions(options);
        }

        return new InteractiveMode(host, options);
    }

    // ==================================================================
    // 构造器装配
    // ==================================================================

    [Fact]
    public async Task CtorAssemblesEditorFooterAndContainers()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        // 编辑器与 footer 各自挂进自己的容器。
        var editorChildren = mode.EditorContainerChildrenForTests;
        Assert.Single(editorChildren);
        Assert.Same(mode.EditorForTests, editorChildren[0]);
        var footerChildren = mode.FooterContainerChildrenForTests;
        Assert.Single(footerChildren);
        Assert.IsType<FooterComponent>(footerChildren[0]);

        // 文档树：header → loadedResources → chat。
        var documentChildren = mode.ChatChildrenForTests; // chat 起始为空
        Assert.Empty(documentChildren);
        Assert.False(mode.ActiveWorkingIndicatorEmbeddedForTests);
        Assert.Null(mode.ActiveStatusIndicatorForTests);
    }

    [Fact]
    public async Task CtorResolvesTuiModeFromOverride()
    {
        var host = await CreateHostAsync();

        var regular = await CreateModeAsync(o => o with { TuiMode = TuiMode.Regular }, host);
        Assert.Equal(TuiMode.Regular, regular.OptionsForTests.TuiMode);
        Assert.IsType<TuiMainScreen>(regular.RendererForTests);

        var fullscreen = await CreateModeAsync(o => o with { TuiMode = TuiMode.Fullscreen }, host);
        Assert.Equal(TuiMode.Fullscreen, fullscreen.OptionsForTests.TuiMode);
        Assert.IsType<TuiAltScreen>(fullscreen.RendererForTests);
    }

    [Fact]
    public async Task CtorRegistersTheKeybindingsManagerGlobally()
    {
        var host = await CreateHostAsync();
        var previous = GlobalKeybindings.Get();

        _ = await CreateModeAsync(host: host);

        Assert.NotSame(previous, GlobalKeybindings.Get());
    }

    // ==================================================================
    // 聊天通知
    // ==================================================================

    [Fact]
    public async Task ShowErrorAppendsSpacerAndNotice()
    {
        var mode = await CreateModeAsync();
        mode.ShowError("boom");

        var children = mode.ChatChildrenForTests;
        Assert.Equal(2, children.Count);
        Assert.IsType<Spacer>(children[0]);
        var text = Assert.IsType<ThemedText>(children[1]);
        var rendered = string.Join("\n", text.Render(80));
        Assert.Contains("Error: boom", rendered);

        // 第二条错误是独立一行（状态行的合并规则只属于 showStatus）。
        mode.ShowError("again");
        Assert.Equal(4, mode.ChatChildrenForTests.Count);
    }

    [Fact]
    public async Task ShowWarningUsesTheWarningPrefixAndHardcodedPadding()
    {
        var mode = await CreateModeAsync();
        mode.ShowWarning("careful");

        var text = Assert.IsType<ThemedText>(mode.ChatChildrenForTests[1]);
        var rendered = string.Join("\n", text.Render(80));
        Assert.Contains("Warning: careful", rendered);
    }

    [Fact]
    public async Task ShowStatusCoalescesConsecutiveStatusLines()
    {
        var mode = await CreateModeAsync();
        mode.ShowStatus("first");
        Assert.Equal(2, mode.ChatChildrenForTests.Count);

        var trackedText = mode.ChatChildrenForTests[1];

        mode.ShowStatus("second");
        Assert.Equal(2, mode.ChatChildrenForTests.Count);
        Assert.Same(trackedText, mode.ChatChildrenForTests[1]);

        var rendered = string.Join("\n", ((ThemedText)trackedText).Render(80));
        Assert.Contains("second", rendered);
        Assert.DoesNotContain("first", rendered);
    }

    [Fact]
    public async Task ShowStatusStopsCoalescingWhenAnotherComponentInterleaves()
    {
        var mode = await CreateModeAsync();
        mode.ShowStatus("first");
        mode.ShowError("boom");
        var countAfterError = mode.ChatChildrenForTests.Count;

        mode.ShowStatus("second");
        Assert.Equal(countAfterError + 2, mode.ChatChildrenForTests.Count);
    }

    // ==================================================================
    // 终端标题
    // ==================================================================

    [Fact]
    public async Task UpdateTerminalTitleWritesTheOscTitle()
    {
        var mode = await CreateModeAsync();
        _terminal.ClearOutput();

        mode.UpdateTerminalTitle();

        var expected = $"{Config.AppTitle} - {Path.GetFileName(_cwd)}";
        Assert.Contains($"\x1b]0;{expected}\x07", _terminal.Output);
    }

    // ==================================================================
    // 状态指示器链
    // ==================================================================

    [Fact]
    public async Task WorkingIndicatorEmbedsIntoTheCustomEditor()
    {
        var mode = await CreateModeAsync();
        try
        {
            var indicator = new WorkingStatusIndicator(mode.UiForTests, "Working hard");

            mode.ShowStatusIndicator(indicator);

            Assert.Same(indicator, mode.ActiveStatusIndicatorForTests);
            Assert.True(mode.ActiveWorkingIndicatorEmbeddedForTests);
            Assert.Empty(mode.StatusContainerChildrenForTests);
        }
        finally
        {
            // 指示器的动画定时器会在夹具重置主题后触发，必须先停掉（4f-3d Batch E-5 的坑）。
            mode.ClearStatusIndicator();
        }
    }

    [Fact]
    public async Task ClearStatusIndicatorRemovesTheIndicator()
    {
        var mode = await CreateModeAsync();
        mode.ShowStatusIndicator(new WorkingStatusIndicator(mode.UiForTests, "Working hard"));

        mode.ClearStatusIndicator();

        Assert.Null(mode.ActiveStatusIndicatorForTests);
        Assert.False(mode.ActiveWorkingIndicatorEmbeddedForTests);
        Assert.Empty(mode.StatusContainerChildrenForTests);
    }

    [Fact]
    public async Task ClearStatusIndicatorIgnoresAMismatchedKind()
    {
        var mode = await CreateModeAsync();
        try
        {
            var indicator = new WorkingStatusIndicator(mode.UiForTests, "Working hard");
            mode.ShowStatusIndicator(indicator);

            mode.ClearStatusIndicator(StatusIndicatorKind.Retry);

            Assert.Same(indicator, mode.ActiveStatusIndicatorForTests);
        }
        finally
        {
            mode.ClearStatusIndicator();
        }
    }

    [Fact]
    public async Task SetWorkingVisibleFalseClearsTheWorkingIndicator()
    {
        var mode = await CreateModeAsync();
        mode.ShowStatusIndicator(new WorkingStatusIndicator(mode.UiForTests, "Working hard"));

        mode.SetWorkingVisible(false);

        Assert.Null(mode.ActiveStatusIndicatorForTests);

        // 会话未在流式输出中，重新可见不重建指示器。
        mode.SetWorkingVisible(true);
        Assert.Null(mode.ActiveStatusIndicatorForTests);
    }

    [Fact]
    public async Task SetWorkingIndicatorUpdatesTheActiveWorkingIndicator()
    {
        var mode = await CreateModeAsync();
        try
        {
            mode.ShowStatusIndicator(new WorkingStatusIndicator(mode.UiForTests, "Working hard"));

            mode.SetWorkingIndicator(new WorkingIndicatorOptions { Frames = ["a", "b"], IntervalMs = 10 });

            Assert.Equal(StatusIndicatorKind.Working, mode.ActiveStatusIndicatorForTests!.Kind);
        }
        finally
        {
            mode.ClearStatusIndicator();
        }
    }

    // ==================================================================
    // 扩展 widget
    // ==================================================================

    [Fact]
    public async Task StringWidgetRendersAboveTheEditor()
    {
        var mode = await CreateModeAsync();
        mode.SetExtensionWidget("k", ["a", "b"]);

        var children = mode.WidgetContainerAboveChildrenForTests;
        Assert.Equal(2, children.Count);
        Assert.IsType<Spacer>(children[0]);
        var widget = Assert.IsType<Container>(children[1]);
        Assert.Equal(2, widget.Children.Count);
        Assert.Empty(mode.WidgetContainerBelowChildrenForTests);
    }

    [Fact]
    public async Task StringWidgetTruncatesBeyondTenLines()
    {
        var mode = await CreateModeAsync();
        mode.SetExtensionWidget("k", Enumerable.Range(1, 12).Select(i => $"line-{i}").ToList());

        var widget = Assert.IsType<Container>(mode.WidgetContainerAboveChildrenForTests[1]);
        Assert.Equal(11, widget.Children.Count);
        var note = Assert.IsType<ThemedText>(widget.Children[10]);
        Assert.Contains("... (widget truncated)", string.Join("\n", note.Render(80)));
    }

    [Fact]
    public async Task SettingTheSameKeyReplacesTheWidget()
    {
        var mode = await CreateModeAsync();
        mode.SetExtensionWidget("k", ["a"]);
        mode.SetExtensionWidget("k", ["b", "c"]);

        var children = mode.WidgetContainerAboveChildrenForTests;
        Assert.Equal(2, children.Count);
        var widget = Assert.IsType<Container>(children[1]);
        Assert.Equal(2, widget.Children.Count);
    }

    [Fact]
    public async Task ClearExtensionWidgetsRestoresTheEmptySpacers()
    {
        var mode = await CreateModeAsync();
        mode.SetExtensionWidget("k", ["a"]);
        mode.SetExtensionWidget("below", ["x"], new ExtensionWidgetOptions { Placement = WidgetPlacement.BelowEditor });

        mode.ClearExtensionWidgets();

        // 上方容器空列表保留一个 spacer；下方容器空列表不加。
        var above = mode.WidgetContainerAboveChildrenForTests;
        Assert.Single(above);
        Assert.IsType<Spacer>(above[0]);
        Assert.Empty(mode.WidgetContainerBelowChildrenForTests);
    }

    [Fact]
    public async Task BelowWidgetHasNoLeadingSpacer()
    {
        var mode = await CreateModeAsync();
        mode.SetExtensionWidget("k", ["x"], new ExtensionWidgetOptions { Placement = WidgetPlacement.BelowEditor });

        var children = mode.WidgetContainerBelowChildrenForTests;
        Assert.Single(children);
        Assert.IsType<Container>(children[0]);
    }

    private sealed class DisposableText : Text, IDisposable
    {
        public DisposableText() : base("custom")
        {
        }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ReplacingAFactoryWidgetDisposesTheOldOne()
    {
        var mode = await CreateModeAsync();
        var first = new DisposableText();
        mode.SetExtensionWidget("k", (tui, theme) => first);

        var second = new DisposableText();
        mode.SetExtensionWidget("k", (tui, theme) => second);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Same(second, mode.WidgetContainerAboveChildrenForTests[1]);
    }

    // ==================================================================
    // footer / header 替换
    // ==================================================================

    [Fact]
    public async Task SetExtensionFooterSwapsAndRestoresTheBuiltInFooter()
    {
        var mode = await CreateModeAsync();
        var builtIn = mode.FooterContainerChildrenForTests[0];

        var custom = new Text("custom-footer");
        mode.SetExtensionFooter((tui, theme, data) => custom);
        Assert.Same(custom, mode.FooterContainerChildrenForTests[0]);

        mode.SetExtensionFooter(null);
        Assert.Same(builtIn, mode.FooterContainerChildrenForTests[0]);
    }

    [Fact]
    public async Task SetExtensionHeaderIsAGuardWhenTheHeaderIsNotBuilt()
    {
        var mode = await CreateModeAsync();
        // init 之前 builtInHeader 尚不存在：工厂调用直接被忽略（TS L2544 的早退分支）。
        mode.SetExtensionHeader((tui, theme) => new Text("custom-header"));

        Assert.Empty(mode.HeaderContainerChildrenForTests);
    }

    // ==================================================================
    // 自定义编辑器切换
    // ==================================================================

    [Fact]
    public async Task SetCustomEditorComponentSwapsAndRestoresWithTheTextCarriedOver()
    {
        var mode = await CreateModeAsync();
        mode.EditorForTests.SetText("hello");
        var defaultEditor = mode.DefaultEditorForTests;

        CustomEditor? created = null;
        mode.SetCustomEditorComponent((tui, theme, keybindings) =>
        {
            created = new CustomEditor(tui, theme, keybindings);
            return created;
        });

        Assert.NotSame(defaultEditor, mode.EditorForTests);
        Assert.Same(created, mode.EditorForTests);
        Assert.Equal("hello", mode.EditorForTests.GetText());
        Assert.Same(created, mode.EditorContainerChildrenForTests[0]);

        mode.SetCustomEditorComponent(null);

        Assert.Same(defaultEditor, mode.EditorForTests);
        Assert.Equal("hello", mode.EditorForTests.GetText());
        Assert.Same(defaultEditor, mode.EditorContainerChildrenForTests[0]);
    }

    [Fact]
    public async Task SetCustomEditorComponentCopiesActionHandlersOntoCustomEditors()
    {
        var mode = await CreateModeAsync();
        var seen = new List<string>();
        mode.DefaultEditorForTests.OnAction("test-action", () => seen.Add("default"));

        CustomEditor? created = null;
        mode.SetCustomEditorComponent((tui, theme, keybindings) =>
        {
            created = new CustomEditor(tui, theme, keybindings);
            return created;
        });

        Assert.NotNull(created);
        Assert.True(created!.ActionHandlers.ContainsKey("test-action"));
        created.ActionHandlers["test-action"]();
        Assert.Equal(["default"], seen);
    }

    // ==================================================================
    // 扩展对话框
    // ==================================================================

    [Fact]
    public async Task PreAbortedSignalResolvesTheSelectorImmediately()
    {
        var mode = await CreateModeAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await mode.ShowExtensionSelectorAsync("title", ["a", "b"], new ExtensionUIDialogOptions { Signal = cts.Token });

        Assert.Null(result);
        Assert.Null(mode.ExtensionSelectorForTests);
    }

    [Fact]
    public async Task CancellingTheSignalHidesTheSelectorAndRestoresTheEditor()
    {
        var mode = await CreateModeAsync();
        using var cts = new CancellationTokenSource();
        var pending = mode.ShowExtensionSelectorAsync("title", ["a", "b"], new ExtensionUIDialogOptions { Signal = cts.Token });

        Assert.NotNull(mode.ExtensionSelectorForTests);
        cts.Cancel();

        Assert.Null(await pending);
        Assert.Null(mode.ExtensionSelectorForTests);
        Assert.Same(mode.EditorForTests, mode.EditorContainerChildrenForTests[0]);
    }

    [Fact]
    public async Task ConfirmingTheSelectorResolvesWithTheOption()
    {
        var mode = await CreateModeAsync();
        var pending = mode.ShowExtensionSelectorAsync("title", ["a", "b"]);

        mode.ExtensionSelectorForTests!.HandleInput("\r");

        Assert.Equal("a", await pending);
        Assert.Null(mode.ExtensionSelectorForTests);
        Assert.Same(mode.EditorForTests, mode.EditorContainerChildrenForTests[0]);
    }

    [Fact]
    public async Task ShowExtensionConfirmMapsYesToTrueAndCancelToFalse()
    {
        var mode = await CreateModeAsync();

        var yesPending = mode.ShowExtensionConfirmAsync("title", "message");
        mode.ExtensionSelectorForTests!.HandleInput("\r");
        Assert.True(await yesPending);

        var cancelledPending = mode.ShowExtensionConfirmAsync("title", "message");
        mode.ExtensionSelectorForTests!.HandleInput("\x1b");
        Assert.False(await cancelledPending);
    }

    [Fact]
    public async Task PreAbortedSignalResolvesTheInputImmediately()
    {
        var mode = await CreateModeAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await mode.ShowExtensionInputAsync("title", null, new ExtensionUIDialogOptions { Signal = cts.Token });

        Assert.Null(result);
    }

    [Fact]
    public async Task ShowExtensionNotifyRoutesByType()
    {
        var mode = await CreateModeAsync();

        mode.ShowExtensionNotify("err", "error");
        Assert.Contains("Error: err", string.Join("\n", ((ThemedText)mode.ChatChildrenForTests[1]).Render(80)));

        var count = mode.ChatChildrenForTests.Count;
        mode.ShowExtensionNotify("st");
        Assert.Equal(count + 2, mode.ChatChildrenForTests.Count);
    }

    // ==================================================================
    // TUI 模式切换与运行时设置
    // ==================================================================

    [Fact]
    public async Task SwitchTuiModeIsANoOpForTheSameMode()
    {
        var mode = await CreateModeAsync();
        var renderer = mode.RendererForTests;

        Assert.True(mode.SwitchTuiMode(renderer.Mode));
        Assert.Same(renderer, mode.RendererForTests);
    }

    [Fact]
    public async Task SwitchTuiModeFlipsTheRendererWithoutStartingIt()
    {
        // 默认（未设置 tuiMode）解析为 Fullscreen；切到 Regular 不需要全屏布局根。
        var mode = await CreateModeAsync();
        Assert.Equal(TuiMode.Fullscreen, mode.RendererForTests.Mode);

        Assert.True(mode.SwitchTuiMode(TuiMode.Regular, restoreProgress: false, startRenderer: false));

        Assert.Equal(TuiMode.Regular, mode.RendererForTests.Mode);
        Assert.IsType<TuiMainScreen>(mode.RendererForTests);
        Assert.Equal(TuiMode.Regular, mode.OptionsForTests.TuiMode);
    }

    [Fact]
    public async Task ApplyRuntimeSettingsAppliesEditorPadding()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        mode.ApplyRuntimeSettings();

        Assert.Equal(
            (int)host.Session.SettingsManager.GetEditorPaddingX(),
            mode.DefaultEditorForTests.GetPaddingX());
    }
}
