using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
using JsMath = Pi.Tui.JsMath;
using JsNumber = Pi.CodingAgent.Utils.JsNumber;
using JsString = Pi.Tui.JsString;
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>The interactive llama.cpp model manager and Hugging Face search view from <c>ui.ts</c>.</summary>
public sealed class LlamaManagerView : IComponent, IFocusable, ILlamaUi
{
    private readonly ITui _tui;
    private readonly Theme _theme;
    private readonly CoreKeybindingsManager _keybindings;
    private readonly Dictionary<string, IReadOnlyList<HuggingFaceModel>> _searchCache = new(StringComparer.Ordinal);
    private Container _content;
    private Action<string>? _inputHandler;
    private IFocusable? _inputTarget;
    private TaskCompletionSource<bool>? _progressCompletion;
    private bool _showingProgress;
    private bool _focused;

    public LlamaManagerView(ITui tui, Theme theme, CoreKeybindingsManager keybindings)
    {
        _tui = tui;
        _theme = theme;
        _keybindings = keybindings;
        _content = Frame(theme, "llama.cpp models", [new TuiText(theme.Fg("muted", "Loading…"), 1, 1)]);
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            if (_inputTarget is not null)
            {
                _inputTarget.Focused = value;
            }
        }
    }

    public Task<LlamaManagerAction> ShowModels(string serverUrl, IReadOnlyList<LlamaModelInfo> models)
    {
        var sorted = models
            .OrderByDescending(model => model.StatusValue == LlamaModelStatusValue.Loaded)
            .ThenBy(model => model.Id, StringComparer.CurrentCulture)
            .ToList();
        var byId = new Dictionary<string, LlamaModelInfo>(StringComparer.Ordinal);
        foreach (var model in sorted)
        {
            byId[model.Id] = model;
        }

        var items = sorted.Select(model => new SelectItem
        {
            Value = model.Id,
            Label = model.Id,
            Description = LlamaUi.ModelDescription(model),
        }).ToList();
        items.Add(new SelectItem
        {
            Value = "\0download",
            Label = "Download model…",
            Description = "Hugging Face owner/repository[:quant]",
        });

        var completion = new TaskCompletionSource<LlamaManagerAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new SelectList(
            items,
            Math.Min(items.Count, 12),
            SelectTheme(_theme),
            new SelectListLayoutOptions { MinPrimaryColumnWidth = 36, MaxPrimaryColumnWidth = 56 });
        list.OnSelect = item =>
        {
            if (item.Value == "\0download")
            {
                completion.TrySetResult(new LlamaManagerAction.Download());
            }
            else if (byId.TryGetValue(item.Value, out var model))
            {
                completion.TrySetResult(new LlamaManagerAction.Model(model));
            }
        };
        list.OnCancel = () => completion.TrySetResult(new LlamaManagerAction.Close());
        SetContent(
            Frame(
                _theme,
                "llama.cpp models",
                [new TuiText(_theme.Fg("dim", serverUrl), 1, 0), new Spacer(1), list],
                $"{KeybindingHints.KeyHint("tui.select.confirm", "load/unload/download")} • " +
                KeybindingHints.KeyHint("tui.select.cancel", "close")),
            list.HandleInput);
        return completion.Task;
    }

    public Task<string?> Select(string title, IReadOnlyList<string> options)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new SelectList(
            options.Select(option => new SelectItem { Value = option, Label = option }),
            Math.Min(options.Count, 12),
            SelectTheme(_theme));
        list.OnSelect = item => completion.TrySetResult(item.Value);
        list.OnCancel = () => completion.TrySetResult(null);
        SetContent(
            Frame(
                _theme,
                title,
                [new Spacer(1), list],
                $"{KeybindingHints.KeyHint("tui.select.confirm", "select")} • " +
                KeybindingHints.KeyHint("tui.select.cancel", "cancel")),
            list.HandleInput);
        return completion.Task;
    }

    public async Task<bool> Confirm(string title, string message) =>
        await Select($"{title}\n{message}", ["Yes", "No"]).ConfigureAwait(false) == "Yes";

    public async Task<string> ConnectionError(string serverUrl, string message)
    {
        var choice = await Select($"llama.cpp unavailable\n{serverUrl}\n\n{message}", ["Retry", "Close"])
            .ConfigureAwait(false);
        return choice == "Retry" ? "retry" : "close";
    }

    public Task<string?> SearchModels(
        Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> search)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = new HuggingFaceSearch(
            _tui,
            _theme,
            _keybindings,
            search,
            _searchCache,
            model => completion.TrySetResult(model));
        SetContent(
            Frame(
                _theme,
                "Download model",
                [new Spacer(1), component],
                $"{KeybindingHints.KeyHint("tui.select.confirm", "select")} • " +
                KeybindingHints.KeyHint("tui.select.cancel", "back")),
            component.HandleInput,
            component);
        return completion.Task;
    }

    public void ShowStatus(string title, string message) =>
        SetContent(Frame(_theme, title, [new Spacer(1), new TuiText(_theme.Fg("muted", message), 1, 0)]));

    public Task Progress(LlamaProgressState state)
    {
        _progressCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _showingProgress = true;
        UpdateProgress(state);
        return _progressCompletion.Task;
    }

    public void UpdateProgress(LlamaProgressState state)
    {
        if (!_showingProgress)
        {
            return;
        }

        var body = new List<IComponent>
        {
            new TuiText(_theme.Fg("text", state.Model), 1, 0),
            new Spacer(1),
            new TuiText(_theme.Fg("muted", state.Message), 1, 0),
        };
        if (state.Ratio is { } ratio)
        {
            const int available = 40;
            var filled = (int)JsMath.Round(Math.Clamp(ratio, 0, 1) * available);
            var percent = JsNumber.ToJsString(JsMath.Round(ratio * 100));
            body.Add(new TuiText(
                _theme.Fg("accent", $"{new string('█', filled)}{new string('─', available - filled)} {percent}%"),
                1,
                0));
        }
        if (!string.IsNullOrEmpty(state.Detail))
        {
            body.Add(new TuiText(_theme.Fg("dim", state.Detail), 1, 0));
        }

        _content = Frame(_theme, state.Title, body, KeybindingHints.KeyHint("tui.select.cancel", "stop"));
        _inputHandler = null;
        _tui.RequestRender();
    }

    public void HandleInput(string data)
    {
        if (_progressCompletion is not null && _keybindings.Matches(data, "tui.select.cancel"))
        {
            var completion = _progressCompletion;
            _progressCompletion = null;
            completion.TrySetResult(true);
            return;
        }

        _inputHandler?.Invoke(data);
        _tui.RequestRender();
    }

    public string[] Render(int width) =>
        _content.Render(width)
            .Select(line => UnicodeWidth.VisibleWidth(line) > width ? TextLayout.TruncateToWidth(line, width, "") : line)
            .ToArray();

    public void Invalidate() => _content.Invalidate();

    private void SetContent(Container content, Action<string>? inputHandler = null, IFocusable? inputTarget = null)
    {
        if (_inputTarget is not null)
        {
            _inputTarget.Focused = false;
        }

        _progressCompletion = null;
        _showingProgress = false;
        _content = content;
        _inputHandler = inputHandler;
        _inputTarget = inputTarget;
        if (_inputTarget is not null)
        {
            _inputTarget.Focused = _focused;
        }

        _tui.RequestRender();
    }

    private static SelectListTheme SelectTheme(Theme theme) => new()
    {
        SelectedPrefix = text => theme.Fg("accent", text),
        SelectedText = text => theme.Fg("accent", text),
        Description = text => theme.Fg("muted", text),
        ScrollInfo = text => theme.Fg("dim", text),
        NoMatch = text => theme.Fg("warning", text),
    };

    private static Container Frame(Theme theme, string title, IEnumerable<IComponent> body, string? footer = null)
    {
        var container = new Container();
        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        container.AddChild(new TuiText(theme.Fg("accent", theme.Bold(title)), 1, 0));
        foreach (var child in body)
        {
            container.AddChild(child);
        }

        if (!string.IsNullOrEmpty(footer))
        {
            container.AddChild(new Spacer(1));
            container.AddChild(new TuiText(theme.Fg("dim", footer), 1, 0));
        }

        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        return container;
    }

    /// <summary>Runs the llama manager inside the extension's focused custom TUI slot.</summary>
    public static async Task ShowLlamaUiAsync(
        IExtensionCommandContext context,
        Func<ILlamaUi, Task> run)
    {
        _ = await context.Ui.Custom<bool>((tui, theme, keybindings, done) =>
        {
            var view = new LlamaManagerView(tui, theme, keybindings);
            _ = RunAsync();
            return view;

            async Task RunAsync()
            {
                await Task.Yield();
                try
                {
                    await run(view).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    context.Ui.Notify(error.Message, NotifyType.Error);
                }
                finally
                {
                    done(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private sealed class HuggingFaceSearch : Container, IFocusable
    {
        private readonly object _sync = new();
        private readonly ITui _tui;
        private readonly Theme _theme;
        private readonly CoreKeybindingsManager _keybindings;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> _search;
        private readonly Dictionary<string, IReadOnlyList<HuggingFaceModel>> _cache;
        private readonly Action<string?> _onSelectModel;
        private readonly Input _input = new();
        private readonly Container _resultsContainer = new();
        private IReadOnlyList<HuggingFaceModel> _results = [];
        private List<HuggingFaceModel> _filteredResults = [];
        private CancellationTokenSource? _debounce;
        private CancellationTokenSource? _request;
        private int _selectedIndex;
        private string _query = "";
        private string _status = "Type at least 2 characters";
        private bool _closed;
        private bool _focused;

        public HuggingFaceSearch(
            ITui tui,
            Theme theme,
            CoreKeybindingsManager keybindings,
            Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> search,
            Dictionary<string, IReadOnlyList<HuggingFaceModel>> cache,
            Action<string?> onSelectModel)
        {
            _tui = tui;
            _theme = theme;
            _keybindings = keybindings;
            _search = search;
            _cache = cache;
            _onSelectModel = onSelectModel;
            AddChild(new TuiText(theme.Fg("dim", "Model name or owner/repository[:quant]"), 1, 0));
            AddChild(_input);
            AddChild(new Spacer(1));
            AddChild(_resultsContainer);
            UpdateResults();
        }

        public bool Focused
        {
            get
            {
                lock (_sync)
                {
                    return _focused;
                }
            }
            set
            {
                lock (_sync)
                {
                    _focused = value;
                    _input.Focused = value;
                }
            }
        }

        public override void HandleInput(string data)
        {
            lock (_sync)
            {
                HandleInputCore(data);
            }
        }

        public override string[] Render(int width)
        {
            lock (_sync)
            {
                return base.Render(width);
            }
        }

        public override void Invalidate()
        {
            lock (_sync)
            {
                base.Invalidate();
            }
        }

        private void HandleInputCore(string data)
        {
            if (_keybindings.Matches(data, "tui.select.up"))
            {
                if (_filteredResults.Count > 0)
                {
                    _selectedIndex = _selectedIndex == 0 ? _filteredResults.Count - 1 : _selectedIndex - 1;
                    UpdateResults();
                }
                return;
            }

            if (_keybindings.Matches(data, "tui.select.down"))
            {
                if (_filteredResults.Count > 0)
                {
                    _selectedIndex = _selectedIndex == _filteredResults.Count - 1 ? 0 : _selectedIndex + 1;
                    UpdateResults();
                }
                return;
            }

            if (_keybindings.Matches(data, "tui.select.confirm"))
            {
                var exact = IsExactModelSpec(_query) ? _query : null;
                var selected = exact ?? (_selectedIndex < _filteredResults.Count ? _filteredResults[_selectedIndex].Id : null);
                if (!string.IsNullOrEmpty(selected))
                {
                    Close(selected);
                }
                return;
            }

            if (_keybindings.Matches(data, "tui.select.cancel"))
            {
                Close(null);
                return;
            }

            _input.HandleInput(data);
            var query = JsString.Trim(_input.GetValue());
            if (query == _query)
            {
                return;
            }

            _query = query;
            ScheduleSearch();
        }

        private void UpdateResults()
        {
            lock (_sync)
            {
                _resultsContainer.Clear();
                const int maxVisible = 10;
                var start = Math.Max(
                    0,
                    Math.Min(_selectedIndex - maxVisible / 2, _filteredResults.Count - maxVisible));
                var end = Math.Min(start + maxVisible, _filteredResults.Count);
                for (var index = start; index < end; index++)
                {
                    var model = _filteredResults[index];
                    var prefix = index == _selectedIndex ? "→ " : "  ";
                    var details = $"{LlamaUi.CompactCount(model.Downloads)} downloads";
                    var text = index == _selectedIndex
                        ? _theme.Fg("accent", $"{prefix}{model.Id}  {details}")
                        : $"{prefix}{model.Id}{_theme.Fg("muted", $"  {details}")}";
                    _resultsContainer.AddChild(new TuiText(text, 0, 0));
                }

                if (start > 0 || end < _filteredResults.Count)
                {
                    _resultsContainer.AddChild(new TuiText(
                        _theme.Fg("dim", $"  ({_selectedIndex + 1}/{_filteredResults.Count})"),
                        0,
                        0));
                }

                if (_filteredResults.Count == 0)
                {
                    _resultsContainer.AddChild(new TuiText(_theme.Fg("dim", $"  {_status}"), 0, 0));
                }
                else if (_status == "Searching Hugging Face…")
                {
                    _resultsContainer.AddChild(new TuiText(_theme.Fg("dim", $"  {_status}"), 0, 0));
                }
            }

            _tui.RequestRender();
        }

        private void FilterResults()
        {
            if (_query.Length > 0)
            {
                var matches = new HashSet<string>(
                    Fuzzy.Filter(_results, _query, model => model.Id).Select(model => model.Id),
                    StringComparer.Ordinal);
                _filteredResults = _results.Where(model => matches.Contains(model.Id)).ToList();
            }
            else
            {
                _filteredResults = _results.ToList();
            }

            _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _filteredResults.Count - 1));
            UpdateResults();
        }

        private void ScheduleSearch()
        {
            Cancel(_debounce);
            _debounce = null;
            Cancel(_request);
            _request = null;

            if (_query.Length < 2)
            {
                _status = "Type at least 2 characters";
                FilterResults();
                return;
            }

            var cacheKey = JsString.ToLowerCase(_query);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                _results = cached;
                _status = cached.Count == 0 ? "No GGUF models found" : "";
                FilterResults();
                return;
            }

            _status = "Searching Hugging Face…";
            FilterResults();
            var debounce = new CancellationTokenSource();
            _debounce = debounce;
            _ = DebouncedSearchAsync(_query, debounce);
        }

        private async Task DebouncedSearchAsync(string query, CancellationTokenSource debounce)
        {
            var token = debounce.Token;
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_debounce, debounce))
                    {
                        _debounce = null;
                    }
                }
                debounce.Dispose();
            }

            lock (_sync)
            {
                if (_closed || token.IsCancellationRequested || _query != query)
                {
                    return;
                }
            }

            await RunSearchAsync(query).ConfigureAwait(false);
        }

        private async Task RunSearchAsync(string query)
        {
            var request = new CancellationTokenSource();
            lock (_sync)
            {
                if (_closed || _query != query)
                {
                    request.Dispose();
                    return;
                }
                _request = request;
            }

            try
            {
                var results = await _search(query, request.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    _cache[JsString.ToLowerCase(query)] = results;
                    if (_closed || request.IsCancellationRequested || _query != query)
                    {
                        return;
                    }

                    _results = results;
                    _selectedIndex = 0;
                    _status = results.Count == 0 ? "No GGUF models found" : "";
                    FilterResults();
                }
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    if (_closed || request.IsCancellationRequested || _query != query)
                    {
                        return;
                    }

                    _results = [];
                    _status = error.Message;
                    FilterResults();
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_request, request))
                    {
                        _request = null;
                    }
                }
                request.Dispose();
            }
        }

        private void Close(string? model)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Cancel(_debounce);
            Cancel(_request);
            _onSelectModel(model);
        }

        private static bool IsExactModelSpec(string query)
        {
            var slash = query.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }

            for (var index = 0; index < slash; index++)
            {
                if (JsString.IsWhitespace(query[index]))
                {
                    return false;
                }
            }

            var colon = query.IndexOf(':', slash + 1);
            var repositoryEnd = colon < 0 ? query.Length : colon;
            if (repositoryEnd == slash + 1)
            {
                return false;
            }

            for (var index = slash + 1; index < repositoryEnd; index++)
            {
                if (query[index] == ':' || JsString.IsWhitespace(query[index]))
                {
                    return false;
                }
            }

            if (colon < 0)
            {
                return true;
            }
            if (colon == query.Length - 1)
            {
                return false;
            }

            for (var index = colon + 1; index < query.Length; index++)
            {
                if (query[index] == ':' || JsString.IsWhitespace(query[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void Cancel(CancellationTokenSource? source)
        {
            if (source is null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
