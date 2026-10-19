using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Llama;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

[CollectionDefinition("extension-manager-view", DisableParallelization = true)]
public sealed class ExtensionManagerViewCollection
{
    public const string Name = "extension-manager-view";
}

[Collection(ExtensionManagerViewCollection.Name)]
public sealed class ExtensionManagerViewTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousKeybindings = GlobalKeybindings.Get();
    private readonly CoreKeybindingsManager _keybindings = new();
    private readonly FakeTui _tui = new();

    public ExtensionManagerViewTests()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(_keybindings);
    }

    public void Dispose()
    {
        GlobalKeybindings.Set(_previousKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    [Fact]
    public void BuiltInManagerHooks_DefaultToTheConcreteTuiViews()
    {
        Assert.Equal(McpManagerView.ShowMcpManagerAsync, new McpExtensionOptions().ShowManager);
        Assert.Equal(
            LlamaManagerView.ShowLlamaUiAsync,
            new LlamaExtension.LlamaExtensionOptions().ShowUi);
    }

    [Fact]
    public async Task McpMenu_RebuildsAndPreservesTheHighlightedItem()
    {
        var view = new McpManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var reordered = false;
        Action? changed = null;
        var unsubscribeCount = 0;
        var menuTask = view.MenuAsync(
            () => new McpMenu
            {
                Title = "MCP servers",
                Items = reordered
                    ? [Item("two"), Item("one"), Item("three")]
                    : [Item("one"), Item("two")],
                ConfirmLabel = "manage",
                CancelLabel = "close",
            },
            listener =>
            {
                changed = listener;
                return () => unsubscribeCount++;
            });

        view.HandleInput("\x1b[B");
        reordered = true;
        changed!();
        view.HandleInput("\r");

        Assert.Equal("two", await menuTask);
        Assert.Equal(1, unsubscribeCount);
    }

    [Fact]
    public async Task McpEmptyMenu_CanBeCancelled()
    {
        var view = new McpManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var menuTask = view.MenuAsync(() => new McpMenu
        {
            Title = "MCP servers",
            Items = [],
            Empty = "No servers configured.",
            ConfirmLabel = "manage",
            CancelLabel = "close",
        });

        Assert.Contains("No servers configured.", Render(view), StringComparison.Ordinal);
        view.HandleInput("\x1b");

        Assert.Null(await menuTask);
    }

    [Fact]
    public void McpStatus_RendersTheOperationMessage()
    {
        var view = new McpManagerView(_tui, ThemeRuntime.Current, _keybindings);

        view.Status("Connecting", "Waiting for the server handshake.");

        var rendered = Render(view);
        Assert.Contains("Connecting", rendered, StringComparison.Ordinal);
        Assert.Contains("Waiting for the server handshake.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpRedirectUrl_SubmitsTrimmedInputAndHonorsCancellation()
    {
        var view = new McpManagerView(_tui, ThemeRuntime.Current, _keybindings);
        using var cancellation = new CancellationTokenSource();
        var redirectTask = view.RedirectUrlAsync("Sign in", "https://auth.example/", cancellation.Token);
        view.HandleInput("  https://localhost/callback?code=abc  ");
        view.HandleInput("\r");

        Assert.Equal("https://localhost/callback?code=abc", await redirectTask);

        using var aborted = new CancellationTokenSource();
        var abortedTask = view.RedirectUrlAsync("Sign in", "https://auth.example/", aborted.Token);
        aborted.Cancel();

        Assert.Null(await abortedTask);
    }

    [Fact]
    public async Task LlamaModelMenu_PrioritizesLoadedModelsAndReturnsTheSelectedModel()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var unloaded = Model("z-unloaded", "unloaded");
        var loadedB = Model("b-loaded", "loaded");
        var loadedA = Model("a-loaded", "loaded");
        var selection = view.ShowModels("http://localhost:8080", [unloaded, loadedB, loadedA]);
        var rendered = Render(view);

        Assert.True(rendered.IndexOf("a-loaded", StringComparison.Ordinal)
            < rendered.IndexOf("b-loaded", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("b-loaded", StringComparison.Ordinal)
            < rendered.IndexOf("z-unloaded", StringComparison.Ordinal));

        view.HandleInput("\r");

        var action = Assert.IsType<LlamaManagerAction.Model>(await selection);
        Assert.Same(loadedA, action.Value);
    }

    [Fact]
    public async Task LlamaConfirmAndConnectionError_UseSelectableChoices()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);

        var confirm = view.Confirm("Stop download?", "The current transfer will be cancelled.");
        view.HandleInput("\r");
        Assert.True(await confirm);

        var retry = view.ConnectionError("http://localhost:8080", "Connection refused");
        view.HandleInput("\r");
        Assert.Equal("retry", await retry);

        var close = view.ConnectionError("http://localhost:8080", "Connection refused");
        view.HandleInput("\x1b[B");
        view.HandleInput("\r");
        Assert.Equal("close", await close);
    }

    [Fact]
    public async Task LlamaSearch_DebouncesRequestsAndReusesCachedResults()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var calls = 0;
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<HuggingFaceModel>> Search(string query, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult(query);
            return Task.FromResult<IReadOnlyList<HuggingFaceModel>>([new HuggingFaceModel("ab/model", 1200)]);
        }

        var first = view.SearchModels(Search);
        view.HandleInput("ab");
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("ab", await started.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await WaitForContentAsync(view, "ab/model");
        view.HandleInput("\x1b");
        Assert.Null(await first);

        var second = view.SearchModels(Search);
        view.HandleInput("AB");
        Assert.Contains("ab/model", Render(view), StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref calls));
        view.HandleInput("\r");

        Assert.Equal("ab/model", await second);
    }

    [Theory]
    [InlineData("owner/repository")]
    [InlineData("owner/repository:Q4_K_M")]
    public async Task LlamaSearch_AcceptsExactModelSpecsWithoutWaitingForSearch(string spec)
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var calls = 0;
        var selection = view.SearchModels((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<HuggingFaceModel>>([]);
        });

        view.HandleInput(spec);
        view.HandleInput("\r");

        Assert.Equal(spec, await selection);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task LlamaSearch_CancelsOldRequestsAndIgnoresStaleResults()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var oldStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<IReadOnlyList<HuggingFaceModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<IReadOnlyList<HuggingFaceModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<IReadOnlyList<HuggingFaceModel>> Search(string query, CancellationToken cancellationToken) =>
            query == "ab"
                ? StartOld(cancellationToken)
                : StartNew();

        Task<IReadOnlyList<HuggingFaceModel>> StartOld(CancellationToken cancellationToken)
        {
            oldStarted.TrySetResult(cancellationToken);
            return oldResult.Task;
        }

        Task<IReadOnlyList<HuggingFaceModel>> StartNew()
        {
            newStarted.TrySetResult(true);
            return newResult.Task;
        }

        var selection = view.SearchModels(Search);
        view.HandleInput("ab");
        var oldToken = await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        view.HandleInput("c");
        Assert.True(oldToken.IsCancellationRequested);
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        oldResult.TrySetResult([new HuggingFaceModel("ab/old", 10)]);
        await Task.Delay(50);
        Assert.DoesNotContain("ab/old", Render(view), StringComparison.Ordinal);

        newResult.TrySetResult([new HuggingFaceModel("abc/current", 20)]);
        await WaitForContentAsync(view, "abc/current");
        Assert.DoesNotContain("ab/old", Render(view), StringComparison.Ordinal);
        view.HandleInput("\r");

        Assert.Equal("abc/current", await selection);
    }

    [Fact]
    public async Task LlamaSearch_ShowsSearchErrors()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var selection = view.SearchModels((_, _) =>
            Task.FromException<IReadOnlyList<HuggingFaceModel>>(new InvalidOperationException("search failed")));
        view.HandleInput("er");

        await WaitForContentAsync(view, "search failed");

        Assert.Contains("search failed", Render(view), StringComparison.Ordinal);
        view.HandleInput("\x1b");
        Assert.Null(await selection);
    }

    [Fact]
    public async Task LlamaProgress_RendersUpdatesAndResolvesOnStop()
    {
        var view = new LlamaManagerView(_tui, ThemeRuntime.Current, _keybindings);
        var state = new LlamaProgressState
        {
            Title = "Loading model",
            Model = "model-q4",
            Message = "Downloading weights",
            Ratio = 0.5,
            Detail = "5 MB / 10 MB",
        };
        var stopped = view.Progress(state);
        view.UpdateProgress(state with { Message = "Halfway" });

        var rendered = Render(view);
        Assert.Contains("model-q4", rendered, StringComparison.Ordinal);
        Assert.Contains("Halfway", rendered, StringComparison.Ordinal);
        Assert.Contains("50%", rendered, StringComparison.Ordinal);
        Assert.Contains("5 MB / 10 MB", rendered, StringComparison.Ordinal);

        view.HandleInput("\x1b");
        await stopped.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static SelectItem Item(string value) => new() { Value = value, Label = value };

    private static LlamaModelInfo Model(string id, string status) => new(new JsonObject
    {
        ["id"] = id,
        ["status"] = new JsonObject { ["value"] = status },
    });

    private static string Render(IComponent component) =>
        AnsiText.StripAnsi(string.Join("\n", component.Render(120)));

    private static async Task WaitForContentAsync(IComponent component, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!Render(component).Contains(expected, StringComparison.Ordinal))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
