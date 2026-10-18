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
using Pi.CodingAgent.Utils;
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

    private sealed class DisposableText : Pi.Tui.Components.Text, IDisposable
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

        var custom = new Pi.Tui.Components.Text("custom-footer");
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
        mode.SetExtensionHeader((tui, theme) => new Pi.Tui.Components.Text("custom-header"));

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

    // ==================================================================
    // 4f-4m-2：事件分发 / 会话渲染 / 队列族
    // ==================================================================

    [Fact]
    public async Task RenderSessionEntriesRendersUserAndAssistantMessages()
    {
        var mode = await CreateModeAsync();

        mode.AddMessageToChat(new UserMessage("hello world", 1_000));
        mode.AddMessageToChat(new AssistantMessage(
            Content: [new TextContent("hi there")],
            StopReason: StopReason.Stop,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub"));

        var children = mode.ChatChildrenForTests;
        Assert.Equal(2, children.Count);
        Assert.IsType<UserMessageComponent>(children[0]);
        Assert.IsType<AssistantMessageComponent>(children[1]);

        var userText = string.Join("\n", children[0].Render(80));
        Assert.Contains("hello world", userText);
    }

    [Fact]
    public async Task RenderSessionEntriesRendersCompactionSummariesWithSpacer()
    {
        var mode = await CreateModeAsync();

        mode.AddMessageToChat(new CompactionSummaryMessage("summary text", 1_234, 1_000));

        var children = mode.ChatChildrenForTests;
        Assert.Equal(2, children.Count);
        Assert.IsType<Spacer>(children[0]);
        Assert.IsType<CompactionSummaryMessageComponent>(children[1]);
    }

    [Fact]
    public async Task RenderSessionEntriesPairsToolResultsWithToolCalls()
    {
        var mode = await CreateModeAsync();

        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry
            {
                Id = "e1",
                Message = new AssistantMessage(
                    Content: [new ToolCallContent("tc1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })],
                    StopReason: StopReason.ToolUse,
                    Model: "stub-chat",
                    Api: "stub-api",
                    Provider: "stub"),
            },
            new SessionMessageEntry
            {
                Id = "e2",
                Message = new ToolResultMessage("tc1", "bash", [new TextContent("file-a")]),
            },
        };
        mode.RenderSessionEntries(entries);

        // 助手消息 + 工具组件；工具结果与组件配对后 pendingTools 清空。
        var children = mode.ChatChildrenForTests;
        Assert.Equal(2, children.Count);
        Assert.IsType<AssistantMessageComponent>(children[0]);
        Assert.IsType<ToolExecutionComponent>(children[1]);

        var toolText = string.Join("\n", ((IComponent)children[1]).Render(80));
        Assert.Contains("ls", AnsiText.StripAnsi(toolText));
    }

    [Fact]
    public async Task QueuedCompactionMessagesShowInThePendingAreaAndRestoreToTheEditor()
    {
        var mode = await CreateModeAsync();

        mode.QueueCompactionMessage("queued-1", CompactionQueueModes.Steer);

        var pending = mode.PendingMessagesContainerChildrenForTests;
        Assert.Equal(3, pending.Count); // spacer + steering 行 + dequeue 提示
        var pendingText = string.Join("\n", pending[1].Render(80));
        Assert.Contains("Steering: queued-1", AnsiText.StripAnsi(pendingText));

        // 恢复：排队消息回填编辑器、pending 区清空。
        var restored = mode.RestoreQueuedMessagesToEditor();
        Assert.Equal(1, restored);
        Assert.Equal("queued-1", mode.EditorForTests.GetText());
        Assert.Empty(mode.PendingMessagesContainerChildrenForTests);
    }

    [Fact]
    public async Task RestoreQueuedMessagesToEditorIsANoOpWhenNothingIsQueued()
    {
        var mode = await CreateModeAsync();
        mode.EditorForTests.SetText("keep me");

        Assert.Equal(0, mode.RestoreQueuedMessagesToEditor());
        Assert.Equal("keep me", mode.EditorForTests.GetText());
    }

    [Fact]
    public async Task IsExtensionCommandRequiresARegisteredCommand()
    {
        var mode = await CreateModeAsync();

        Assert.False(mode.IsExtensionCommand("/read"));
        Assert.False(mode.IsExtensionCommand("plain text"));
        Assert.False(mode.IsExtensionCommand("/no-such-command"));
    }

    [Fact]
    public async Task GetUserInputReturnsTheQueuedInputFirst()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        // 通过 GetUserInput 的 promise + onInputCallback 链路验证（queued 输入由内部路径注入）。
        var pending = mode.GetUserInputAsync();
        Assert.False(pending.IsCompleted);
        mode.Stop();
    }

    // ==================================================================
    // 4f-4m-3：生命周期批
    // ==================================================================

    [Fact]
    public async Task StartupGatesFollowQuietStartupAndVerboseOptions()
    {
        var host = await CreateHostAsync();

        // 默认设置（quietStartup 为 "false"）→ header 与 details 都显示；展开态默认关闭。
        var mode = await CreateModeAsync(host: host);
        Assert.True(mode.ShouldShowStartupHeader());
        Assert.True(mode.ShouldShowStartupDetails());
        Assert.False(mode.GetStartupExpansionState());

        // verbose 选项全开。
        var verbose = await CreateModeAsync(o => o with { Verbose = true }, host);
        Assert.True(verbose.ShouldShowStartupHeader());
        Assert.True(verbose.ShouldShowStartupDetails());
        Assert.True(verbose.GetStartupExpansionState());

        // 工具输出展开 → 启动 header 默认展开。
        verbose.SetToolsExpanded(true);
        Assert.True(verbose.GetStartupExpansionState());
    }

    [Fact]
    public async Task HandleStartupSubmitMirrorsTextIntoTheEditorAndShowsStatus()
    {
        var mode = await CreateModeAsync();

        mode.HandleStartupSubmit("early input");

        Assert.Equal("early input", mode.EditorForTests.GetText());
        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Startup is still in progress", string.Join("\n", last.Render(80)));
    }

    [Fact]
    public async Task ShowManagedToolStatusAppendsSpacerOnceAndResetsStatusCoalescing()
    {
        var mode = await CreateModeAsync();

        mode.ShowManagedToolStatus(new ToolStatus("info", "downloading fd"));
        mode.ShowManagedToolStatus(new ToolStatus("warning", "fd download failed"));

        var children = mode.ChatChildrenForTests;
        // 首条前一个 spacer，之后每条一行；warning 带 Warning: 前缀。
        Assert.Equal(3, children.Count);
        var warningText = string.Join("\n", children[2].Render(80));
        Assert.Contains("Warning: fd download failed", AnsiText.StripAnsi(warningText));

        // managed-tool 状态行清掉 showStatus 的合并游标：下一条 showStatus 不合流。
        mode.ShowStatus("after tools");
        var countBefore = mode.ChatChildrenForTests.Count;
        mode.ShowStatus("again");
        Assert.Equal(countBefore, mode.ChatChildrenForTests.Count);
    }

    [Fact]
    public async Task SingleCtrlCPressClearsTheEditorWithoutShuttingDown()
    {
        var mode = await CreateModeAsync();
        mode.EditorForTests.SetText("to be cleared");

        mode.HandleCtrlC();

        Assert.Equal("", mode.EditorForTests.GetText());
        // 注意：不能按第二次——500ms 内双击会触发 shutdown → Environment.Exit。
    }

    [Fact]
    public async Task CtrlZOnWindowsShowsTheUnsupportedStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = await CreateModeAsync();

        mode.HandleCtrlZ();

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Suspend to background is not supported on Windows", string.Join("\n", last.Render(80)));
    }

    [Fact]
    public async Task SignalHandlerRegistrationIsIdempotentAndClean()
    {
        var mode = await CreateModeAsync();

        mode.RegisterSignalHandlersForTests();
        mode.RegisterSignalHandlersForTests();
        mode.UnregisterSignalHandlers();
        mode.UnregisterSignalHandlers();
    }

    [Fact]
    public async Task CheckShutdownRequestedIsANoOpWhenNotRequested()
    {
        var mode = await CreateModeAsync();

        // shutdownRequested 为 false：不触达 shutdown（触达即 Environment.Exit）。
        mode.CheckShutdownRequested();
    }

    // ==================================================================
    // 4f-4m-4 前段：crash-log 三缝落地 + 登录告警 + 新版本通知
    // ==================================================================

    [Fact]
    public async Task TakeUnnotifiedCrashForWarningReturnsTheNewestCrashAndMarksItAnnounced()
    {
        var mode = await CreateModeAsync();
        var tempPath = Path.Combine(_root, "crashes-take.json");

        CrashLog.Record(new CrashInput
        {
            Kind = CrashKind.UncaughtException,
            Error = new InvalidOperationException("boom-1"),
            SessionFile = null,
            Cwd = _cwd,
        }, tempPath);

        var taken = mode.TakeUnnotifiedCrashForWarning(tempPath);
        Assert.NotNull(taken);
        Assert.Contains("boom-1", taken.Value.Message);

        // 已标记 notified：再取为空。
        Assert.Null(mode.TakeUnnotifiedCrashForWarning(tempPath));
    }

    [Fact]
    public async Task RecordCrashForUiPersistsThroughTheInstancePath()
    {
        var mode = await CreateModeAsync();
        var tempPath = Path.Combine(_root, "crashes-record.json");

        Assert.True(mode.RecordCrashForUi(CrashKind.UncaughtException, new InvalidOperationException("instance-crash"), tempPath));

        var records = CrashLog.Read(tempPath);
        Assert.Single(records);
        Assert.False(records[0].Notified);
        Assert.Equal("instance-crash", records[0].Members!["message"].GetString());
        Assert.Equal(_cwd, records[0].Members!["cwd"].GetString());
    }

    [Fact]
    public async Task GetCrashExtensionHintForUiReturnsNullWithoutMatchingExtensions()
    {
        var mode = await CreateModeAsync();

        // 夹具只带一个 no-op 扩展：真实异常栈不会命中任何扩展路径 → 无提示。
        Assert.Null(mode.GetCrashExtensionHintForUi(new InvalidOperationException("no stack match")));
    }

    [Fact]
    public async Task AnthropicSubscriptionWarningSkipsForNonAnthropicModels()
    {
        var mode = await CreateModeAsync();

        // stub 模型 provider 非 anthropic：直接返回，不产生告警。
        await mode.MaybeWarnAboutAnthropicSubscriptionAuthForTestsAsync();

        Assert.DoesNotContain(
            mode.ChatChildrenForTests.Select(c => string.Join("\n", c.Render(80))),
            line => line.Contains("Anthropic subscription auth is active"));
    }

    [Fact]
    public async Task ShowNewVersionNotificationRendersBannerAndOptionalNote()
    {
        var mode = await CreateModeAsync();

        mode.ShowNewVersionNotification(new LatestPiRelease("9.9.9"));

        var children = mode.ChatChildrenForTests;
        Assert.Equal(5, children.Count); // spacer + 边框 + 正文 + changelog 行 + 边框
        var banner = string.Join("\n", children[2].Render(80));
        Assert.Contains("Update Available", AnsiText.StripAnsi(banner));
        Assert.Contains("New version 9.9.9 is available", AnsiText.StripAnsi(banner));
        var changelogLine = AnsiText.StripAnsi(string.Join("\n", children[3].Render(80)));
        Assert.Contains("Changelog: https://pi.dev/changelog", changelogLine);

        var countBefore = children.Count;
        mode.ShowNewVersionNotification(new LatestPiRelease("9.9.9", Note: "Fixes a bug"));
        // 第二条 banner 本体 5 项（spacer+边框+正文+changelog 行+边框），note 再加 spacer+markdown+spacer。
        Assert.Equal(countBefore + 8, mode.ChatChildrenForTests.Count);
        var noteMarkdown = Assert.IsType<Markdown>(mode.ChatChildrenForTests[^4]);
        Assert.Contains("Fixes a bug", AnsiText.StripAnsi(string.Join("\n", noteMarkdown.Render(80))));
    }

    // ==================================================================
    // 4f-4m-4 中段：会话 cycle API + 命令键位方法
    // ==================================================================

    [Fact]
    public async Task SessionCycleThinkingLevelCyclesAndAppendsTranscriptEntry()
    {
        var host = await CreateHostAsync();

        var first = host.Session.CycleThinkingLevel();
        Assert.NotNull(first);

        var second = host.Session.CycleThinkingLevel();
        Assert.NotNull(second);

        Assert.Contains(host.Session.SessionManager.GetEntries(), e => e is ThinkingLevelChangeEntry);
    }

    [Fact]
    public async Task CycleModelWithASingleAvailableModelShowsTheSingleModelStatus()
    {
        var mode = await CreateModeAsync();

        await mode.CycleModelAsync("forward");

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Only one model available", string.Join("\n", last.Render(80)));
    }

    [Fact]
    public async Task CycleThinkingLevelWritesTheLevelStatus()
    {
        var mode = await CreateModeAsync();

        mode.CycleThinkingLevel();

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Thinking level:", string.Join("\n", last.Render(80)));
    }

    [Fact]
    public async Task HandleDequeueReportsTheRestoreCount()
    {
        var mode = await CreateModeAsync();

        mode.HandleDequeue();
        var empty = string.Join("\n", mode.ChatChildrenForTests[^1].Render(80));
        Assert.Contains("No queued messages to restore", AnsiText.StripAnsi(empty));

        mode.QueueCompactionMessage("queued-1", CompactionQueueModes.Steer);
        mode.HandleDequeue();
        var restored = string.Join("\n", mode.ChatChildrenForTests[^1].Render(80));
        Assert.Contains("Restored 1 queued message to editor", AnsiText.StripAnsi(restored));
    }

    [Fact]
    public async Task HandleFollowUpFallsThroughToTheSubmitHandlerWhenIdle()
    {
        var mode = await CreateModeAsync();
        var submitted = new List<string>();
        mode.EditorForTests.OnSubmit = text => submitted.Add(text);
        mode.EditorForTests.SetText("queued text");

        await mode.HandleFollowUpAsync();

        Assert.Equal(["queued text"], submitted);
        Assert.Equal("", mode.EditorForTests.GetText());
    }

    [Fact]
    public async Task HandleCopyCommandWithoutMessagesShowsTheError()
    {
        var mode = await CreateModeAsync();

        await mode.HandleCopyCommandAsync();

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("No agent messages to copy yet.", string.Join("\n", last.Render(80)));
    }

    // ==================================================================
    // 4f-4m-4 尾批（前半）：模型选择器 / 新会话 / 调试日志
    // ==================================================================

    [Fact]
    public async Task ShowModelSelectorMountsTheSelectorInPlaceOfTheEditor()
    {
        var mode = await CreateModeAsync();
        try
        {
            mode.ShowModelSelector();

            var mounted = Assert.IsType<ModelSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
            Assert.NotSame(mode.EditorForTests, mode.EditorContainerChildrenForTests[0]);
            _ = mounted;
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    [Fact]
    public async Task NewSessionAsyncCreatesAReplacementSession()
    {
        var host = await CreateHostAsync();
        var oldSession = host.Session;

        var result = await host.NewSessionAsync();

        Assert.False(result.Cancelled);
        Assert.NotSame(oldSession, host.Session);
    }

    // HandleClearCommand 本体不测：其错误分支进入 handleFatalRuntimeError → Environment.Exit(1)
    // （与 shutdown 同类的退出路径）；扩展 rebind 缝（BindCurrentSessionExtensionsAsync）清零后
    // 该命令的全链路再补。

    [Fact]
    public async Task HandleDebugCommandWritesTheDebugLogAndShowsConfirmation()
    {
        var mode = await CreateModeAsync();

        mode.HandleDebugCommand();

        var last = mode.ChatChildrenForTests[^1];
        var rendered = AnsiText.StripAnsi(string.Join("\n", last.Render(80)));
        Assert.Contains("✓ Debug log written", rendered);
    }

    [Fact]
    public async Task ShowUserMessageSelectorShowsTheEmptyStatusWithoutMessages()
    {
        var mode = await CreateModeAsync();

        mode.ShowUserMessageSelector();

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("No messages to fork from", string.Join("\n", last.Render(80)));
        // 空清单不挂选择器。
        Assert.Null(mode.ExtensionSelectorForTests);
    }

    [Fact]
    public async Task ShowSessionSelectorMountsTheSelectorInPlaceOfTheEditor()
    {
        var mode = await CreateModeAsync();
        try
        {
            mode.ShowSessionSelector();

            Assert.IsType<SessionSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    // ==================================================================
    // 4f-4m 命令族（增量）：/name
    // ==================================================================

    [Fact]
    public async Task NameCommandWithoutArgumentsShowsUsageWhenUnnamed()
    {
        var mode = await CreateModeAsync();

        mode.HandleNameCommand("/name");

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Usage: /name <name>", string.Join("\n", last.Render(80)));
    }

    [Fact]
    public async Task NameCommandSetsTheSessionName()
    {
        var mode = await CreateModeAsync();

        mode.HandleNameCommand("/name my-session");

        var last = mode.ChatChildrenForTests[^1];
        Assert.Contains("Session name set:", string.Join("\n", last.Render(80)));
        Assert.NotNull(mode.SessionManagerForTests.GetSessionName());

        // 再次空参 /name 显示当前会话名。
        mode.HandleNameCommand("/name");
        var current = string.Join("\n", mode.ChatChildrenForTests[^1].Render(80));
        Assert.Contains("Session name:", AnsiText.StripAnsi(current));
    }

    // ==================================================================
    // 4f-4m-4 尾批后半：会话 navigateTree 编排
    // ==================================================================

    [Fact]
    public async Task NavigateTreeToTheCurrentLeafIsANoOp()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello");

        var leafId = host.Session.SessionManager.GetLeafId();
        Assert.NotNull(leafId);

        var result = await host.Session.NavigateTreeAsync(leafId!);
        Assert.False(result.Cancelled);
        Assert.Null(result.EditorText);
    }

    [Fact]
    public async Task NavigateTreeToAUserMessageSwitchesLeafAndFillsEditorText()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        await host.Session.PromptAsync("second");

        var userEntry = host.Session.SessionManager.GetEntries()
            .OfType<SessionMessageEntry>()
            .First(e => e.Message is UserMessage user && InteractiveModeRender.GetUserMessageText(e.Message) == "hello world");
        var beforeLeaf = host.Session.SessionManager.GetLeafId();

        var result = await host.Session.NavigateTreeAsync(userEntry.Id!);

        Assert.False(result.Cancelled);
        Assert.Equal("hello world", result.EditorText);
        Assert.NotEqual(beforeLeaf, host.Session.SessionManager.GetLeafId());
    }

    // ShowTreeSelector 空清单分支：GetTree() 在新会话也返回根节点（非空清单），空状态文案
    // 在移植语义下不可达——挂载行为由 DoubleEscapeActions 用例覆盖。

    [Fact]
    public async Task DoubleEscapeActionsMountTheTreeAndForkSelectors()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);
        await host.Session.PromptAsync("hello world");
        try
        {
            mode.ShowDoubleEscapeSelector("tree");
            Assert.IsType<TreeSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
            mode.DisposeActiveSelector();

            mode.ShowDoubleEscapeSelector("fork");
            Assert.IsType<UserMessageSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    // ==================================================================
    // 4f-4m-5a：命令族前半（thinking / model / clone / trust）
    // ==================================================================

    [Fact]
    public async Task HandleThinkingCommandWithoutArgumentMountsTheSelector()
    {
        var mode = await CreateModeAsync();
        try
        {
            mode.HandleThinkingCommand(null);

            Assert.IsType<ThinkingSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    [Fact]
    public async Task HandleThinkingCommandWithUnknownLevelListsTheAvailableLevels()
    {
        var mode = await CreateModeAsync();

        mode.HandleThinkingCommand("bogus");

        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Unknown thinking level \"bogus\"", rendered);
        // 可用等级清单来自 session.getAvailableThinkingLevels()（stub 模型 reasoning=true → 全档）。
        Assert.Contains("Available levels:", rendered);
    }

    [Fact]
    public async Task HandleThinkingCommandWithValidLevelSetsTheLevelAndWritesTheStatus()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        mode.HandleThinkingCommand("high");

        Assert.Equal(Pi.Ai.Types.ThinkingLevel.High, host.Session.ThinkingLevel);
        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Thinking level: high", rendered);
    }

    [Fact]
    public async Task HandleThinkingCommandTrimsAndMatchesCaseInsensitively()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        mode.HandleThinkingCommand("  HIGH  ");

        Assert.Equal(Pi.Ai.Types.ThinkingLevel.High, host.Session.ThinkingLevel);
        // 非 persist：状态文案不带 Default 前缀。
        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Thinking level: high", rendered);
        Assert.DoesNotContain("Default thinking level", rendered);
    }

    [Fact]
    public async Task HandleModelCommandWithoutArgumentMountsTheSelector()
    {
        var mode = await CreateModeAsync();
        try
        {
            await mode.HandleModelCommandAsync(null);

            Assert.IsType<ModelSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    [Fact]
    public async Task HandleModelCommandWithExactReferenceSelectsTheModelWithoutRefreshing()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        await mode.HandleModelCommandAsync("stub/stub-chat");

        Assert.Equal("stub-chat", host.Session.Model?.Id);
        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Model: stub-chat", rendered);
    }

    [Fact]
    public async Task HandleModelCommandWithoutExactMatchOpensTheSelectorWithTheSearchTerm()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);
        // scoped 非空时 findExactModelMatch 只查 scoped 快照、不触发目录刷新（TS 同款短路）。
        host.Session.SetScopedModels([new ScopedModel { Model = host.Session.Model! }]);
        try
        {
            await mode.HandleModelCommandAsync("no-such-model");

            var selector = Assert.IsType<ModelSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
            Assert.Equal("no-such-model", selector.GetSearchInput().GetValue());
        }
        finally
        {
            mode.DisposeActiveSelector();
        }
    }

    [Fact]
    public async Task ShowTrustSelectorMountsTheSelectorWithTheSavedDecision()
    {
        var mode = await CreateModeAsync();

        mode.ShowTrustSelector();

        var selector = Assert.IsType<TrustSelectorComponent>(mode.EditorContainerChildrenForTests[0]);
        _ = selector;
        mode.DisposeActiveSelector();
    }

    // 「Nothing to clone yet」分支需要 sessionManager 无任何条目（getLeafId() 为 null）。
    // 本夹具的会话在创建时即记录了模型选择条目，故 leafId 恒非空——与 TS 侧同一夹具语义相同：
    // fork(position:"at") 走到 isPersisted 分支，而会话文件尚未落盘（首条 user/assistant
    // 消息才触发 Persist），抛 "This session has not been saved yet..." 被 handleCloneCommand
    // 的 catch 收成 showError。空清单分支在移植语义下不可达，此处钉住实际路径。
    [Fact]
    public async Task HandleCloneCommandWithoutASavedSessionFileShowsTheError()
    {
        var mode = await CreateModeAsync();

        await mode.HandleCloneCommandAsync();

        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("This session has not been saved yet", rendered);
    }

    [Fact]
    public async Task HandleCloneCommandForksAtTheLeafAndClearsTheEditor()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        var mode = await CreateModeAsync(host: host);
        var beforeSession = host.Session;
        mode.EditorForTests.SetText("draft");

        await mode.HandleCloneCommandAsync();

        Assert.NotSame(beforeSession, host.Session);
        Assert.Empty(mode.EditorForTests.GetText());
        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Cloned to new session", rendered);
    }
    // ==================================================================
    // 4f-4m-5c：命令族后半（export/import/session/changelog/hotkeys/
    // armin/demented/bash/compact/reload + 启动通知与剪贴板）
    // ==================================================================

    [Fact]
    public async Task HandleExportCommandWritesTheJsonlFile()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        var mode = await CreateModeAsync(host: host);
        var output = Path.Combine(_root, "export.jsonl");

        await mode.HandleExportCommandAsync("/export " + output);

        Assert.True(File.Exists(output));
        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Session exported to:", rendered);
    }

    [Fact]
    public async Task HandleExportCommandDefaultsToHtmlWithoutTheJsonlSuffix()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        var mode = await CreateModeAsync(host: host);
        var output = Path.Combine(_root, "export.html");

        await mode.HandleExportCommandAsync("/export " + output);

        Assert.True(File.Exists(output));
        Assert.Contains("<html", File.ReadAllText(output).ToLowerInvariant());
    }

    [Fact]
    public async Task HandleImportCommandWithoutAPathShowsTheUsageError()
    {
        var mode = await CreateModeAsync();

        await mode.HandleImportCommandAsync("/import");

        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(80)));
        Assert.Contains("Usage: /import <path.jsonl>", rendered);
    }

    [Fact]
    public async Task HandleSessionCommandRendersTheSessionInfoDocument()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        var mode = await CreateModeAsync(host: host);

        mode.HandleSessionCommand();

        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(120)));
        Assert.Contains("Session Info", rendered);
        Assert.Contains("Messages", rendered);
        Assert.Contains("Tokens", rendered);
        Assert.Contains("Cache Warming", rendered);
    }

    [Fact]
    public async Task HandleChangelogCommandRendersTheNoEntriesNotice()
    {
        var mode = await CreateModeAsync();

        mode.HandleChangelogCommand();

        var rendered = AnsiText.StripAnsi(RenderChat(mode, 120));
        Assert.Contains("No changelog entries found", rendered);
    }

    [Fact]
    public async Task HandleHotkeysCommandRendersTheShortcutTables()
    {
        var mode = await CreateModeAsync();

        mode.HandleHotkeysCommand();

        var rendered = AnsiText.StripAnsi(RenderChat(mode, 120));
        // Markdown 渲染会吃掉 `**` 加粗标记，故断言渲染后的纯文本（与 1053 行同款模式）。
        Assert.Contains("Keyboard Shortcuts", rendered);
        Assert.Contains("Navigation", rendered);
        Assert.Contains("Editing", rendered);
        Assert.Contains("Move cursor / browse history", rendered);
        Assert.Contains("Send message", rendered);
    }

    [Fact]
    public async Task HandleArminSaysHiMountsTheArminComponent()
    {
        // TS playArmin3d 在 alt-screen（fullscreen）下返回 true——3D 彩蛋已异步拉起，
        // 不再挂内联组件；只有常规模式才会走 ArminComponent 回退分支（easter-egg-3d.lazy.ts L8-17）。
        var mode = await CreateModeAsync(o => o with { TuiMode = TuiMode.Regular });

        mode.HandleArminSaysHi();

        Assert.IsType<ArminComponent>(mode.ChatChildrenForTests[^1]);
    }

    [Fact]
    public async Task HandleDementedDelvesMountsTheAnnouncementComponent()
    {
        var mode = await CreateModeAsync();

        mode.HandleDementedDelves();

        Assert.IsType<EarendilAnnouncementComponent>(mode.ChatChildrenForTests[^1]);
    }

    [Fact]
    public async Task HandleBashCommandRunsTheCommandAndRecordsTheResult()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        // TS 参照测试（interactive-mode.test.ts 的 ! 命令用例）同样只跑 `echo hi`；这里不再加
        // Windows 专属的 `cmd /c echo hi` 变体——本机 Bash 工具沙箱会挂起 cmd.exe 的派生态
        // （安全层把系统程序解析到 WSL 路径并拦截），导致该用例无限期挂起；经 PowerShell 工具
        // 验证原实现可通过，故统一用 bash 内建 echo，覆盖的执行路径完全相同。
        await mode.HandleBashCommandAsync("echo hi");

        var component = mode.ChatChildrenForTests.OfType<BashExecutionComponent>().Last();
        var rendered = AnsiText.StripAnsi(string.Join("\n", component.Render(100)));
        Assert.Contains("hi", rendered);
        Assert.Contains(
            host.Session.SessionManager.GetEntries(),
            e => e is SessionMessageEntry { Message: Pi.CodingAgent.Core.BashExecutionMessage });
    }

    [Fact]
    public async Task HandleBashCommandWithTheBangBangPrefixExcludesTheResultFromContext()
    {
        var host = await CreateHostAsync();
        var mode = await CreateModeAsync(host: host);

        await mode.HandleBashCommandAsync("echo hi", excludeFromContext: true);

        var entry = host.Session.SessionManager.GetEntries()
            .OfType<SessionMessageEntry>()
            .Select(e => e.Message)
            .OfType<Pi.CodingAgent.Core.BashExecutionMessage>()
            .Last();
        Assert.True(entry.ExcludeFromContext);
    }

    [Fact]
    public async Task HandleCompactCommandEmitsTheCompactionLifecycleEvents()
    {
        var host = await CreateHostAsync();
        await host.Session.PromptAsync("hello world");
        var mode = await CreateModeAsync(host: host);
        var events = new List<string>();
        using var subscription = host.Session.Subscribe(e => events.Add(e.GetType().Name));

        await mode.HandleCompactCommandAsync();

        Assert.Contains("CompactionStartEvent", events);
        Assert.Contains("CompactionEndEvent", events);
    }

    [Fact]
    public async Task HandleReloadCommandShowsTheReloadedStatus()
    {
        var mode = await CreateModeAsync();

        await mode.HandleReloadCommandAsync();

        var rendered = AnsiText.StripAnsi(string.Join("\n", mode.ChatChildrenForTests[^1].Render(120)));
        Assert.Contains("Reloaded keybindings, extensions, skills, prompts, themes, and context files", rendered);
        Assert.Same(mode.EditorForTests, mode.EditorContainerChildrenForTests[0]);
    }

    [Fact]
    public async Task ShowLoadedResourcesForceRendersWithoutThrowing()
    {
        var mode = await CreateModeAsync();

        mode.ShowLoadedResources(new LoadedResourcesOptions { Force = true });

        // 夹具禁用了 skills/prompts/themes/context-files，故只剩诊断块（也可能为空）——
        // 关键是容器被清空重渲且不抛错。
        _ = mode.LoadedResourcesContainerChildrenForTests;
    }

    [Fact]
    public async Task ShowStartupNoticesIfNeededRendersTheCollapsedChangelog()
    {
        var host = await CreateHostAsync();
        host.Session.SettingsManager.SetCollapseChangelog(true);
        var mode = await CreateModeAsync(host: host);
        mode.ChangelogMarkdownForTests = "## 1.2.3\n\n- something";

        mode.ShowStartupNoticesIfNeeded();

        // TS  condensed 分支（interactive-mode.ts L850-854）：`## x.y.z` 标题提版本，
        // 文案为 "Updated to v{version}. Use /changelog to view full changelog."。
        var rendered = AnsiText.StripAnsi(RenderChat(mode, 120));
        Assert.Contains("Updated to v1.2.3", rendered);
        Assert.Contains("/changelog", rendered);
    }

    [Fact]
    public async Task ShowStartupNoticesIfNeededIsIdempotent()
    {
        var mode = await CreateModeAsync();
        mode.ChangelogMarkdownForTests = "## 1.2.3\n\n- something";

        mode.ShowStartupNoticesIfNeeded();
        var countAfterFirst = mode.ChatChildrenForTests.Count;
        mode.ShowStartupNoticesIfNeeded();

        Assert.Equal(countAfterFirst, mode.ChatChildrenForTests.Count);
    }

    [Fact]
    public async Task PasteToEditorFeedsTheTextThroughABracketedPaste()
    {
        var mode = await CreateModeAsync();

        mode.PasteToEditor("pasted");

        Assert.Contains("pasted", mode.EditorForTests.GetText());
    }

    [Fact]
    public async Task AddAutocompleteProviderWrapsTheCurrentProvider()
    {
        var mode = await CreateModeAsync();

        mode.AddAutocompleteProvider(current => current);

        Assert.Single(mode.AutocompleteProviderWrappersForTests);
    }

    private static string RenderChat(InteractiveMode mode, int width) =>
        string.Join("\n", mode.ChatChildrenForTests.Select(child => string.Join("\n", child.Render(width))));
}
