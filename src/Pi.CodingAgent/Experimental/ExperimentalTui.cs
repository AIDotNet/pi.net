using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Pi.Tui;
using Pi.Tui.Components;
using Text = Pi.Tui.Components.Text;
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Experimental;

public static class ExperimentalTui
{
    private static readonly SelectListTheme SelectorTheme = new()
    {
        SelectedPrefix = text => ThemeRuntime.Current.Fg("accent", text),
        SelectedText = text => ThemeRuntime.Current.Fg("accent", text),
        Description = text => ThemeRuntime.Current.Fg("muted", text),
        ScrollInfo = text => ThemeRuntime.Current.Fg("dim", text),
        NoMatch = text => ThemeRuntime.Current.Fg("warning", text),
    };

    public static async Task RunAsync(
        IExperimentalViewSource source,
        IExperimentalController controller,
        SettingsManager settings)
    {
        TerminalImage.SetCapabilityOverrides(
            InteractiveMode.ToTuiCapabilityOverrides(settings.GetTerminalCapabilityOverrides()));
        var ui = new TuiAltScreen(new ProcessTerminal(), showHardwareCursor: false);
        var keybindings = KeybindingsManager.Create();
        GlobalKeybindings.Set(keybindings);
        var view = new ExperimentalTuiView(ui, source, controller, keybindings);
        var themes = new InteractiveThemeController(ui, new InteractiveThemeControllerOptions
        {
            GetSettingsManager = () => settings,
            ShowError = message => Console.Error.WriteLine(message),
            OnChanged = () => ui.RequestRender(),
        });
        var unsubscribe = source.Subscribe(view.ApplyCurrent);

        try
        {
            ui.Start();
            themes.ApplyFromSettings();
            view.ApplyCurrent();
            await view.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            unsubscribe.Dispose();
            themes.Dispose();
            view.Dispose();
            ui.Stop();
        }
    }

    private sealed class ExperimentalTuiView : IDisposable
    {
        private readonly TuiAltScreen _ui;
        private readonly IExperimentalViewSource _source;
        private readonly IExperimentalController _controller;
        private readonly CustomEditor _editor;
        private readonly Container _editorContainer = new();
        private readonly Container _tasks = new();
        private readonly Container _notices = new();
        private readonly Container _footer = new();
        private readonly Text _footerStats = new("", 1, 0);
        private readonly Text _footerHints = new("", 1, 0);
        private readonly ExperimentalChatView _chat;
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _expanded;

        public ExperimentalTuiView(
            TuiAltScreen ui,
            IExperimentalViewSource source,
            IExperimentalController controller,
            KeybindingsManager keybindings)
        {
            _ui = ui;
            _source = source;
            _controller = controller;
            _chat = new ExperimentalChatView(ui, source.Current.Session.Cwd);
            _editor = new CustomEditor(ui, ThemeHelpers.GetEditorTheme(), keybindings, new CustomEditorOptions
            {
                PaddingX = 1,
                EmbedWorkingStatus = true,
            });
            _editor.OnSubmit = HandleSubmit;
            _editor.OnEscape = () => _ = _controller.AbortAsync();
            _editor.OnCtrlD = Exit;
            _editor.OnAction(AppKeybindings.AppClear, Exit);
            _editor.OnAction(AppKeybindings.AppModelSelect, OpenModelSelector);
            _editor.OnAction(AppKeybindings.AppThinkingCycle, () => _ = _controller.CycleThinkingAsync());
            _editor.OnAction(AppKeybindings.AppToolsExpand, ToggleExpanded);
            _editor.OnAction(AppKeybindings.AppMessageFollowUp, QueueFollowUp);
            _editorContainer.AddChild(_editor);
            _footer.AddChild(_footerStats);
            _footer.AddChild(_footerHints);

            _ui.SetLayoutRoot(ChatViewportFactory.CreateChatViewport(new ChatViewportOptions
            {
                Document = _chat.Transcript,
                PendingMessages = _chat.PendingMessages,
                Status = _chat.Status,
                WidgetsAbove = _tasks,
                WidgetsBelow = _notices,
                Editor = _editorContainer,
                Footer = _footer,
            }).Root);
            _ui.SetFocus(_editor);
        }

        public Task WaitForExitAsync() => _exit.Task;

        public void ApplyCurrent() => Apply(_source.Current);

        public void Dispose() => _chat.Dispose();

        private void Apply(ExperimentalView view)
        {
            _chat.Apply(view.Conversation);
            SyncTasks(view.Tasks);
            SyncNotices(view.Notices);
            SyncFooter(view);
            var level = ExperimentalRuntime.AgentOf(view.Conversation)?.ThinkingLevel ?? ThinkingLevel.Off;
            _editor.BorderColor = ThemeRuntime.Current.GetThinkingBorderColor(level);
            _ui.RequestRender();
        }

        private void SyncTasks(IReadOnlyList<TaskGraphNode>? tasks)
        {
            _tasks.Clear();
            if (tasks is null) return;

            var nodes = tasks.OrderBy(node => node.Id.Value).ToList();
            _tasks.AddChild(new TruncatedText(
                ThemeRuntime.Current.Fg("accent", $"Tasks ({nodes.Count} live, /tasks to hide)"), 1, 0));
            var ownedConversations = nodes.SelectMany(node => node.Conversations).ToHashSet();

            IEnumerable<TaskGraphNode> Children(TaskGraphNode parent) => nodes.Where(candidate =>
                candidate.Owner == parent.Id
                || (candidate.Owner is null && parent.Conversations.Contains(candidate.ConversationId)));

            void Visit(TaskGraphNode node, int depth)
            {
                var indent = new string(' ', depth * 2 + 2);
                _tasks.AddChild(new TruncatedText(
                    ThemeRuntime.Current.Fg("muted", indent + DescribeTask(node)), 1, 0));
                foreach (var child in Children(node)) Visit(child, depth + 1);
            }

            foreach (var node in nodes)
            {
                if (node.Owner is null && !ownedConversations.Contains(node.ConversationId))
                    Visit(node, 0);
            }
        }

        private void SyncNotices(IReadOnlyList<ExperimentalNotice> notices)
        {
            _notices.Clear();
            foreach (var notice in notices.TakeLast(4))
            {
                var color = notice.Level switch
                {
                    "error" => "error",
                    "warning" => "warning",
                    _ => "muted",
                };
                _notices.AddChild(new TruncatedText(
                    ThemeRuntime.Current.Fg(color, notice.Message), 1, 0));
            }
        }

        private void SyncFooter(ExperimentalView view)
        {
            var agent = ExperimentalRuntime.AgentOf(view.Conversation);
            var usage = TotalUsage(view.Conversation);
            var stats = new List<string>();
            if (usage.Input != 0) stats.Add($"↑{FooterFormatting.FormatTokens(usage.Input)}");
            if (usage.Output != 0) stats.Add($"↓{FooterFormatting.FormatTokens(usage.Output)}");
            if (usage.CacheRead != 0) stats.Add($"R{FooterFormatting.FormatTokens(usage.CacheRead)}");
            if (usage.CacheWrite != 0) stats.Add($"W{FooterFormatting.FormatTokens(usage.CacheWrite)}");
            stats.Add($"${usage.Cost.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
            var contextWindow = agent?.Model is { } modelReference
                ? view.Models.FirstOrDefault(model => model.Reference == modelReference)?.ContextWindow ?? 0
                : 0;
            if (contextWindow > 0)
            {
                var contextTokens = ContextTokens(view.Conversation.Entries);
                var contextPercent = contextTokens is { } tokens
                    ? (double)tokens / contextWindow * 100
                    : (double?)null;
                var context = $"{(contextPercent is { } percent ? percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "?")}%/{FooterFormatting.FormatTokens(contextWindow)}";
                stats.Add(contextPercent is > 90 ? ThemeRuntime.Current.Fg("error", context) : context);
            }

            _footerStats.SetText(ThemeRuntime.Current.Fg("dim", $"{string.Join(" ", stats)}  {view.Session.Cwd}"));
            var model = agent?.Model is null
                ? "no model"
                : $"{agent.Model.Provider}/{agent.Model.ModelId}";
            var level = Core.ThinkingLevels.ToText(agent?.ThinkingLevel ?? ThinkingLevel.Off);
            var conversation = view.Conversations.FirstOrDefault(
                summary => summary.Id == view.Conversation.Conversation.Id);
            var label = conversation?.Label ?? $"conversation {view.Conversation.Conversation.Id}";
            _footerHints.SetText(
                ThemeRuntime.Current.Fg(label == "main" ? "dim" : "accent", label)
                + ThemeRuntime.Current.Fg("dim",
                    $" · {model} · thinking:{level} ({KeybindingHints.KeyDisplayText(AppKeybindings.AppThinkingCycle)})"
                    + $" · {KeybindingHints.KeyDisplayText(AppKeybindings.AppModelSelect)} or /model · /agents · /compact · /tasks · "
                + $"{KeybindingHints.KeyDisplayText(AppKeybindings.AppMessageFollowUp)} follow-up"
                + $" · {KeybindingHints.KeyDisplayText(AppKeybindings.AppClear)} exit"));
        }

        private static UsageTotals TotalUsage(ConversationView conversation)
        {
            var totals = UsageTotalsApi.CreateUsageTotals();
            if (!conversation.Docs.TryGetValue("pi.usage", out var usageState)) return totals;

            foreach (var bucketName in new[]
                     {
                         Pi.Durable.Harness.Usage.ModelsBucket,
                         Pi.Durable.Harness.Usage.ToolsBucket,
                     })
            {
                if (!usageState.TryGetValue(bucketName, out var bucketValue)
                    || bucketValue is not IReadOnlyDictionary<string, object?> bucket)
                {
                    throw new InvalidOperationException($"pi.usage bucket {bucketName} is missing or invalid");
                }

                foreach (var (key, value) in bucket)
                {
                    if (value is not IReadOnlyDictionary<string, object?> usage)
                        throw new InvalidOperationException($"pi.usage entry {bucketName}.{key} is invalid");
                    UsageTotalsApi.AddUsageToTotals(totals, Pi.Durable.Harness.Usage.FromJson(usage));
                }
            }

            return totals;
        }

        private static long? ContextTokens(IReadOnlyList<EntryRecord> entries)
        {
            var latestCompaction = entries
                .Where(entry => entry.Kind == "pi.compaction")
                .Select(entry => entry.Id.Value)
                .DefaultIfEmpty(0)
                .Max();
            foreach (var entry in entries.Reverse())
            {
                if (entry.Id.Value < latestCompaction
                    || entry.Kind != "pi.assistant"
                    || entry.Model?.FirstOrDefault() is not AssistantMessage assistant
                    || assistant.StopReason is StopReason.Aborted or StopReason.Error)
                {
                    continue;
                }

                if (assistant.UsageStats is not { } usage) return null;
                return usage.TotalTokens != 0
                    ? usage.TotalTokens
                    : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
            }

            return null;
        }

        private void HandleSubmit(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return;
            if (trimmed == "/model")
            {
                OpenModelSelector();
            }
            else if (trimmed == "/tasks")
            {
                _ = _controller.ToggleTasksAsync();
            }
            else if (trimmed == "/agents")
            {
                OpenConversationSelector();
            }
            else if (trimmed == "/compact" || trimmed.StartsWith("/compact ", StringComparison.Ordinal))
            {
                var instructions = trimmed["/compact".Length..].Trim();
                _ = _controller.CompactAsync(instructions.Length == 0 ? null : instructions);
            }
            else
            {
                _ = _controller.SubmitAsync(trimmed, "steer");
            }
        }

        private void QueueFollowUp()
        {
            var text = _editor.GetText().Trim();
            if (text.Length == 0) return;
            _editor.SetText("");
            _ = _controller.SubmitAsync(text, "followUp");
        }

        private void ToggleExpanded()
        {
            _expanded = !_expanded;
            _chat.SetExpanded(_expanded);
            _ui.RequestRender();
        }

        private void OpenModelSelector()
        {
            var view = _source.Current;
            var current = ExperimentalRuntime.AgentOf(view.Conversation)?.Model;
            var items = view.Models
                .OrderByDescending(model =>
                    model.Reference.Provider == current?.Provider
                    && model.Reference.ModelId == current.ModelId)
                .Select(model => new SelectItem
                {
                    Value = $"{model.Reference.Provider}/{model.Reference.ModelId}",
                    Label = model.Reference.ModelId,
                    Description = model.Reference.Provider,
                })
                .ToList();
            MountSelector("Select model:", items, current is null
                ? null
                : $"{current.Provider}/{current.ModelId}", value =>
            {
                var separator = value.IndexOf('/');
                if (separator <= 0 || separator == value.Length - 1)
                    throw new InvalidOperationException($"Invalid model reference: {value}");
                _ = _controller.SetModelAsync(new ModelRef(value[..separator], value[(separator + 1)..]));
            });
        }

        private void OpenConversationSelector()
        {
            var view = _source.Current;
            var items = view.Conversations
                .OrderByDescending(summary => summary.Id.Value)
                .Select(summary => new SelectItem
                {
                    Value = summary.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Label = summary.Label,
                    Description = $"{(summary.Id == view.Conversation.Conversation.Id ? "(shown) " : "")}{summary.Title}",
                })
                .ToList();
            MountSelector("Switch to:", items, view.Conversation.Conversation.Id.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture), value =>
            {
                if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var id))
                {
                    throw new InvalidOperationException($"Invalid conversation ID: {value}");
                }

                _ = _controller.SwitchConversationAsync(ConversationId.From(id));
            });
        }

        private void MountSelector(
            string title,
            IReadOnlyList<SelectItem> items,
            string? selectedValue,
            Action<string> onSelect)
        {
            var selector = new ExperimentalListSelector(title, items, selectedValue, value =>
            {
                RestoreEditor();
                onSelect(value);
            }, RestoreEditor);
            _editorContainer.Clear();
            _editorContainer.AddChild(selector);
            _ui.SetFocus(selector);
            _ui.RequestRender();
        }

        private void RestoreEditor()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }

        private void Exit() => _exit.TrySetResult();

        private static string DescribeTask(TaskGraphNode node)
        {
            var status = node.State switch
            {
                TaskGraphState.Active active => $"{active.Status} {active.Phase}",
                TaskGraphState.WaitingOn waiting =>
                    $"waiting on {string.Join(", ", waiting.On.Select(id => id.Value))}",
                TaskGraphState.Completing completing => $"completing ({completing.OutcomeStatus})",
                _ => "unknown",
            };
            var flags = new List<string>();
            if (node.Background) flags.Add("background");
            if (node.AbortRequested) flags.Add("aborting");
            var owned = node.Conversations.Count == 0
                ? ""
                : $" owns conversation {string.Join(", ", node.Conversations)}";
            return $"{node.Kind} #{node.Id.Value}: {status}"
                + (flags.Count == 0 ? "" : $" [{string.Join(", ", flags)}]")
                + owned;
        }
    }

    private sealed class ExperimentalListSelector : Container, IFocusable
    {
        private readonly Input _input = new();
        private readonly Container _listContainer = new();
        private readonly IReadOnlyList<SelectItem> _items;
        private readonly Action<string> _onSelect;
        private readonly Action _onCancel;
        private SelectList _list;
        private bool _focused;

        public ExperimentalListSelector(
            string title,
            IReadOnlyList<SelectItem> items,
            string? selectedValue,
            Action<string> onSelect,
            Action onCancel)
        {
            _items = items;
            _onSelect = onSelect;
            _onCancel = onCancel;
            _list = BuildList(items, selectedValue);
            AddChild(new DynamicBorder());
            AddChild(new Spacer(1));
            AddChild(new Text(ThemeRuntime.Current.Fg("accent", ThemeRuntime.Current.Bold(title)), 1, 0));
            AddChild(_input);
            AddChild(new Spacer(1));
            AddChild(_listContainer);
            AddChild(new DynamicBorder());
        }

        public bool Focused
        {
            get => _focused;
            set
            {
                _focused = value;
                _input.Focused = value;
            }
        }

        public override void HandleInput(string data)
        {
            var keybindings = GlobalKeybindings.Get();
            if (keybindings.Matches(data, TuiKeybindingIds.SelectUp)
                || keybindings.Matches(data, TuiKeybindingIds.SelectDown)
                || keybindings.Matches(data, TuiKeybindingIds.SelectConfirm)
                || keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
            {
                _list.HandleInput(data);
                return;
            }

            _input.HandleInput(data);
            var query = _input.GetValue();
            var filtered = query.Length == 0
                ? _items
                : Fuzzy.Filter(_items, query, item => $"{item.Label} {item.Value}");
            _list = BuildList(filtered, null);
        }

        private SelectList BuildList(IReadOnlyList<SelectItem> items, string? selectedValue)
        {
            var list = new SelectList(items, Math.Max(1, Math.Min(items.Count, 10)), SelectorTheme);
            var selectedIndex = selectedValue is null
                ? -1
                : items.ToList().FindIndex(item => item.Value == selectedValue);
            if (selectedIndex >= 0) list.SetSelectedIndex(selectedIndex);
            list.OnSelect = item => _onSelect(item.Value);
            list.OnCancel = _onCancel;
            _listContainer.Clear();
            _listContainer.AddChild(list);
            return list;
        }
    }
}
