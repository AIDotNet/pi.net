// ============================================================================
// InteractiveMode — event dispatch and session rendering, slice 2 of 4f-4m.
//
// Port of interactive-mode.ts's handleEvent (L3386-3771), the rendering pipeline it drives:
// addCustomEntryToChat (L3825-3845), addMessageToChat (L3847-3968), renderSessionItems
// (L3970-4062), renderSessionEntries (L4070-4087), the usage/cost/cache-miss notices
// (L4089-4186), renderInitialMessages (L4187-4202), renderProjectTrustWarningIfNeeded
// (L4204-4223), getUserInput (L4225-4237), rebuildChatFromMessages (L4239-4242),
// renderCurrentSessionState (L2201-2210), the markdown/transformer/tool-definition resolvers
// (L1370-1375, L2219-2227, plus the mermaid transformer field L516-519), the bug-report and
// install-change warnings (L2159-2199), and the queued/pending family (L4666-4846):
// getAllQueuedMessages / clearAllQueues / updatePendingMessagesDisplay /
// restoreQueuedMessagesToEditor / queueCompactionMessage / isExtensionCommand /
// flushCompactionQueue / flushPendingBashComponents. stop (L7063-7081) lands here too because
// dispose paths (footer/data provider/theme auto-sync) are already available.
//
// Registered seams (retired in later 4f-4m slices):
//   InitAsync (init, L936-1123) and CheckShutdownRequested (L4362-4366) → lifecycle slice.
// ============================================================================

using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Core.Tools.Renderers;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>
    /// TS field <c>mermaidMarkdownTransformer</c> (L516-519). TS initialises it at field-initializer
    /// time with the module-level <c>theme</c> binding (a live proxy) and a lazy mode getter. The C#
    /// construction is deferred to the end of the constructor (after the theme registry is set up)
    /// because <see cref="ThemeRuntime.Current"/> throws before <c>initTheme</c>; the mode stays
    /// lazy, and the theme snapshot only matters to the not-yet-ported render engine (C258).
    /// </summary>
    private MarkdownTransformer? _mermaidMarkdownTransformerField;

    private MarkdownTransformer MermaidMarkdownTransformer =>
        _mermaidMarkdownTransformerField ??= Mermaid.CreateMarkdownTransformer(new MermaidTransformerOptions
        {
            GetMode = () => SettingsManager.GetMermaidRenderingMode(),
            Theme = ThemeRuntime.Current,
        });

    // ---------------------------------------------------------------- resolvers (L1370-1375, L2219-2227)

    internal MarkdownTheme GetMarkdownThemeWithSettings()
    {
        var theme = ThemeHelpers.GetMarkdownTheme();
        return new MarkdownTheme
        {
            Heading = theme.Heading,
            Link = theme.Link,
            LinkUrl = theme.LinkUrl,
            Code = theme.Code,
            CodeBlock = theme.CodeBlock,
            CodeBlockBorder = theme.CodeBlockBorder,
            Quote = theme.Quote,
            QuoteBorder = theme.QuoteBorder,
            Hr = theme.Hr,
            ListBullet = theme.ListBullet,
            Bold = theme.Bold,
            Italic = theme.Italic,
            Underline = theme.Underline,
            Strikethrough = theme.Strikethrough,
            HighlightCode = theme.HighlightCode,
            CodeBlockIndent = SettingsManager.GetCodeBlockIndent(),
        };
    }

    private IReadOnlyList<MarkdownTransformer> GetMarkdownTransformers() =>
        [MermaidMarkdownTransformer, .. Session.ExtensionRunner.GetMarkdownTransformers()];

    private ToolRenderers? GetRegisteredToolDefinition(string toolName) =>
        Session.ExtensionRunner.ResolveToolRenderers(toolName, () =>
            BuiltInToolRenderers.WithBuiltInRenderers(
                toolName,
                Session.GetToolDefinition(toolName) is { } definition
                    ? ToolExecutionComponent.FromDefinition(definition)
                    : null));

    // ---------------------------------------------------------------- bug report / install warning (L2159-2199)

    private void SuggestBugReport()
    {
        if (_bugReportHintShown)
        {
            return;
        }

        _bugReportHintShown = true;
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg("muted", InteractiveModeDiagnostics.BugReportHint()),
            _outputPad,
            0));
        _ui.RequestRender();
    }

    private void MaybeSuggestBugReport(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Error || Pi.Ai.Utils.Retry.IsRetryableAssistantError(message))
        {
            return;
        }

        if (InteractiveModeDiagnostics.IsAbortOrCancelMessage(message.ErrorMessage))
        {
            return;
        }

        if (MaybeShowInstallChangeWarning())
        {
            return;
        }

        SuggestBugReport();
    }

    /// <summary>
    /// After an error, check whether an update replaced or removed this install while the session
    /// ran. Code loaded on demand then fails with missing modules until restart (#10439).
    /// </summary>
    private bool MaybeShowInstallChangeWarning()
    {
        if (_installChangeWarningShown)
        {
            return true;
        }

        var change = Config.DetectInstallChange();
        if (change is null)
        {
            return false;
        }

        _installChangeWarningShown = true;
        var cause = change.Kind == "updated"
            ? $"{Config.AppName} was updated to {change.Version} while this session was running ({Config.Version})"
            : $"The {Config.AppName} installation this session runs from was removed or replaced";
        var sessionFile = SessionManager.GetSessionFile();
        var resumeCommand = InteractiveModeLifecycle.FormatResumeCommand(
            isTty: !Console.IsOutputRedirected,
            isPersisted: SessionManager.IsPersisted(),
            sessionFile: sessionFile,
            sessionFileExists: sessionFile is not null && File.Exists(sessionFile),
            usesDefaultSessionDir: SessionManager.UsesDefaultSessionDir(),
            sessionDir: SessionManager.GetSessionDir(),
            sessionId: SessionManager.GetSessionId(),
            appName: Config.AppName);
        var restart = resumeCommand is not null
            ? $"Restart with `{resumeCommand}` to continue this session."
            : $"Restart {Config.AppName}.";
        ShowWarning($"{cause}. Features that load code on demand can fail until restart. {restart}");
        return true;
    }

    // ---------------------------------------------------------------- custom entries (L3825-3845)

    private void AddCustomEntryToChat(CustomEntry entry)
    {
        var renderer = Session.ExtensionRunner.GetEntryRenderer(entry.CustomType);
        if (renderer is null)
        {
            return;
        }

        var component = new CustomEntryComponent(entry, renderer, _outputPad);
        component.SetExpanded(_toolOutputExpanded);
        if (!component.HasContent())
        {
            return;
        }

        if (_streamingComponent is not null
            && _chatContainer.InsertChildBefore(_streamingComponent, component))
        {
            return;
        }

        _chatContainer.AddChild(component);
    }

    // ---------------------------------------------------------------- messages (L3847-3968)

    internal void AddMessageToChat(ChatMessage message, bool populateHistory = false)
    {
        switch (message)
        {
            case BashExecutionMessage bash:
            {
                var component = new BashExecutionComponent(bash.Command, _ui, bash.ExcludeFromContext == true, _outputPad);
                if (bash.Output is not null)
                {
                    component.AppendOutput(bash.Output);
                }

                component.SetComplete(
                    bash.ExitCode,
                    bash.Cancelled,
                    bash.Truncated ? new Truncate.TruncationResult
                    {
                        Content = "",
                        Truncated = true,
                        TotalLines = 0,
                        TotalBytes = 0,
                        OutputLines = 0,
                        OutputBytes = 0,
                        LastLinePartial = false,
                        FirstLineExceedsLimit = false,
                        MaxLines = 0,
                        MaxBytes = 0,
                    } : null,
                    bash.FullOutputPath);
                _chatContainer.AddChild(component);
                break;
            }

            case CustomMessage custom:
            {
                if (custom.Display == true)
                {
                    var renderer = Session.ExtensionRunner.GetMessageRenderer(custom.CustomType);
                    var component = new CustomMessageComponent(custom, renderer, GetMarkdownThemeWithSettings(), _outputPad);
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);
                }

                break;
            }

            case CompactionSummaryMessage compactionSummary:
            {
                _chatContainer.AddChild(new Spacer(1));
                var component = new CompactionSummaryMessageComponent(
                    compactionSummary, GetMarkdownThemeWithSettings(), _outputPad);
                component.SetExpanded(_toolOutputExpanded);
                _chatContainer.AddChild(component);
                break;
            }

            case BranchSummaryMessage branchSummary:
            {
                _chatContainer.AddChild(new Spacer(1));
                var component = new BranchSummaryMessageComponent(
                    branchSummary, GetMarkdownThemeWithSettings(), _outputPad);
                component.SetExpanded(_toolOutputExpanded);
                _chatContainer.AddChild(component);
                break;
            }

            case SystemMessage:
                break;

            case UserMessage:
            {
                var textContent = InteractiveModeRender.GetUserMessageText(message);
                if (textContent.Length > 0)
                {
                    if (_chatContainer.GetChildrenSnapshot().Count > 0)
                    {
                        _chatContainer.AddChild(new Spacer(1));
                    }

                    var skillBlock = AgentSession.ParseSkillBlock(textContent);
                    if (skillBlock is not null)
                    {
                        // Render skill block (collapsible)
                        var component = new SkillInvocationMessageComponent(
                            skillBlock, GetMarkdownThemeWithSettings(), _outputPad);
                        component.SetExpanded(_toolOutputExpanded);
                        _chatContainer.AddChild(component);

                        // Render user message separately if present
                        if (skillBlock.UserMessage is not null)
                        {
                            _chatContainer.AddChild(new Spacer(1));
                            var userComponent = new UserMessageComponent(
                                skillBlock.UserMessage,
                                GetMarkdownThemeWithSettings(),
                                _outputPad,
                                GetMarkdownTransformers());
                            _chatContainer.AddChild(userComponent);
                        }
                    }
                    else
                    {
                        var userComponent = new UserMessageComponent(
                            textContent,
                            GetMarkdownThemeWithSettings(),
                            _outputPad,
                            GetMarkdownTransformers());
                        _chatContainer.AddChild(userComponent);
                    }

                    if (populateHistory)
                    {
                        _editor.AddToHistory(textContent);
                    }
                }

                break;
            }

            case AssistantMessage assistant:
            {
                var assistantComponent = new AssistantMessageComponent(
                    assistant,
                    _hideThinkingBlock,
                    GetMarkdownThemeWithSettings(),
                    _hiddenThinkingLabel,
                    _outputPad,
                    GetMarkdownTransformers());
                _chatContainer.AddChild(assistantComponent);
                break;
            }

            case ToolResultMessage:
                // Tool results are rendered inline with tool calls, handled separately
                break;
        }
    }

    // ---------------------------------------------------------------- session replay (L3970-4087, L2201-2210)

    /// <summary>TS <c>RenderSessionItem</c> union, carried as a nested hierarchy.</summary>
    internal abstract record RenderSessionItem
    {
        internal sealed record CustomItem(CustomEntry Entry) : RenderSessionItem;

        internal sealed record UsageItem(UsageEntry Entry) : RenderSessionItem;

        internal sealed record MessageItem(ChatMessage Message) : RenderSessionItem;

        internal sealed record CompactionCostItem(CompactionCostNotice Notice) : RenderSessionItem;
    }

    internal void RenderSessionEntries(IReadOnlyList<SessionEntry> entries, bool updateFooter = false, bool populateHistory = false)
    {
        // Selection coordinates point into the transcript being replaced (#9311).
        if (_renderer is TuiAltScreen altScreen)
        {
            altScreen.ResetTextSelection();
        }

        var items = new List<RenderSessionItem>();
        foreach (var entry in entries)
        {
            var entryType = SessionEntryTypeOf(entry);
            if (InteractiveModeRender.IsRenderedDirectly(entryType, entry is UsageEntry warm ? warm.Kind : null))
            {
                items.Add(entry is CustomEntry custom
                    ? new RenderSessionItem.CustomItem(custom)
                    : new RenderSessionItem.UsageItem((UsageEntry)entry));
                continue;
            }

            var messages = SessionEntries.SessionEntryToContextMessages(entry);
            var hasUsage = entry is CompactionEntry compaction ? compaction.UsageStats is not null
                : entry is BranchSummaryEntry branch ? branch.UsageStats is not null
                : false;
            items.AddRange(messages.Select(m => (RenderSessionItem)new RenderSessionItem.MessageItem(m)));
            if (InteractiveModeRender.ShouldAppendCompactionCost(entryType, hasUsage, messages.Count))
            {
                var usage = entry is CompactionEntry c ? c.UsageStats! : ((BranchSummaryEntry)entry).UsageStats!;
                items.Add(new RenderSessionItem.CompactionCostItem(new CompactionCostNotice
                {
                    Kind = entryType,
                    Usage = usage,
                }));
            }
        }

        RenderSessionItems(items, updateFooter, populateHistory);
    }

    /// <summary>TS <c>new Date().toISOString()</c> (millisecond precision, UTC).</summary>
    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>TS compaction reason literal → enum.</summary>
    private static CompactionStatusReason CompactionReasonOf(string reason) => reason switch
    {
        "manual" => CompactionStatusReason.Manual,
        "overflow" => CompactionStatusReason.Overflow,
        _ => CompactionStatusReason.Threshold,
    };

    /// <summary>TS <c>entry.type</c> literal; C# entries are distinct record types.</summary>
    private static string SessionEntryTypeOf(SessionEntry entry) => entry switch
    {
        SessionMessageEntry => "message",
        ThinkingLevelChangeEntry => "thinkingLevelChange",
        ModelChangeEntry => "modelChange",
        UsageEntry => "usage",
        CompactionEntry => "compaction",
        BranchSummaryEntry => "branch_summary",
        CustomEntry => "custom",
        CustomMessageEntry => "custom_message",
        ContextEditEntry => "contextEdit",
        LabelEntry => "label",
        SessionInfoEntry => "sessionInfo",
        _ => entry.GetType().Name,
    };

    internal void RenderSessionItems(IReadOnlyList<RenderSessionItem> items, bool updateFooter = false, bool populateHistory = false)
    {
        _pendingTools.Clear();
        var renderedPendingTools = new Dictionary<string, ToolExecutionComponent>(StringComparer.Ordinal);
        // Cache misses are not persisted, unlike successful cache-warming usage.
        // Re-derive them and inject them after the assistant messages that paid for them.
        var cacheMisses = SettingsManager.GetShowCacheMissNotices()
            ? CacheStats.CollectCacheMisses(SessionManager.GetEntries(), new ModelRuntimePriceSource(Session.ModelRuntime))
            : new Dictionary<AssistantMessage, CacheStats.CacheMiss>();

        if (updateFooter)
        {
            _footer.Invalidate();
            UpdateEditorBorderColor();
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case RenderSessionItem.CustomItem custom:
                    AddCustomEntryToChat(custom.Entry);
                    continue;
                case RenderSessionItem.UsageItem usage:
                    AddCacheWarmingUsage(usage.Entry);
                    continue;
                case RenderSessionItem.CompactionCostItem cost:
                    AddCompactionCostNotice(cost.Notice);
                    continue;
            }

            var message = ((RenderSessionItem.MessageItem)item).Message;
            // Assistant messages need special handling for tool calls
            if (message is AssistantMessage assistant)
            {
                AddMessageToChat(assistant);
                // Render tool call components
                foreach (var content in assistant.Content)
                {
                    if (content is not ToolCallContent toolCall)
                    {
                        continue;
                    }

                    var component = NewToolExecutionComponent(toolCall.Name, toolCall.Id, toolCall.Arguments);
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);

                    if (InteractiveModeRender.IsFailedStopReason(StopReasonText(assistant.StopReason)))
                    {
                        var errorMessage = InteractiveModeRender.ResolveToolResultErrorMessage(
                            StopReasonText(assistant.StopReason), assistant.ErrorMessage, (int)Session.RetryAttempt);
                        component.UpdateResult(AgentToolResult.FromError(errorMessage));
                    }
                    else
                    {
                        renderedPendingTools[toolCall.Id] = component;
                    }
                }

                if (!InteractiveModeRender.IsFailedStopReason(StopReasonText(assistant.StopReason))
                    && cacheMisses.TryGetValue(assistant, out var miss))
                {
                    AddCacheMissNotice(miss);
                }
            }
            else if (message is ToolResultMessage toolResult)
            {
                // Match tool results to pending tool components
                if (renderedPendingTools.Remove(toolResult.ToolCallId, out var pendingComponent))
                {
                    pendingComponent.UpdateResult(new AgentToolResult(toolResult.Content));
                }
            }
            else
            {
                // All other messages use standard rendering
                AddMessageToChat(message, populateHistory);
            }
        }

        foreach (var (toolCallId, component) in renderedPendingTools)
        {
            _pendingTools[toolCallId] = component;
        }

        _ui.RequestRender();
    }

    private ToolExecutionComponent NewToolExecutionComponent(string name, string id, object? args) =>
        new(
            name,
            id,
            AsArgsDictionary(args),
            new ToolExecutionOptions
            {
                ShowImages = SettingsManager.GetShowImages(),
                ImageWidthCells = (int)SettingsManager.GetImageWidthCells(),
                OutputPad = _outputPad,
            },
            GetRegisteredToolDefinition(name),
            _ui,
            SessionManager.GetCwd());

    /// <summary>TS tool-call arguments are plain objects; the C# component takes a dictionary.</summary>
    private static IReadOnlyDictionary<string, object?> AsArgsDictionary(object? args) =>
        args as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();

    private void RenderCurrentSessionState()
    {
        _loadedResourcesContainer.Clear();
        _chatContainer.Clear();
        _pendingMessagesContainer.Clear();
        _compactionQueuedMessages.Clear();
        _streamingComponent = null;
        _streamingMessage = null;
        _pendingTools.Clear();
        RenderInitialMessages();
    }

    internal void RenderInitialMessages()
    {
        var entries = SessionManager.BuildContextEntries();
        RenderSessionEntries(entries, updateFooter: true, populateHistory: true);
        RenderProjectTrustWarningIfNeeded();

        // Show compaction info if session was compacted
        var allEntries = SessionManager.GetEntries();
        var compactionCount = allEntries.Count(e => e is CompactionEntry);
        if (compactionCount > 0)
        {
            var times = compactionCount == 1 ? "1 time" : $"{compactionCount} times";
            ShowStatus($"Session compacted {times}");
        }
    }

    private void RenderProjectTrustWarningIfNeeded()
    {
        if (SettingsManager.IsProjectTrusted() || !TrustManager.HasTrustRequiringProjectResources(SessionManager.GetCwd()))
        {
            return;
        }

        if (_chatContainer.GetChildrenSnapshot().Count > 0)
        {
            _chatContainer.AddChild(new Spacer(1));
        }

        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg(
                "warning",
                $"This project is not trusted. Project {Config.ConfigDirName} resources and packages are ignored. Use /trust to save a trust decision, then restart pi."),
            1,
            0));
    }

    public Task<string> GetUserInputAsync()
    {
        if (_pendingUserInputs.Count > 0)
        {
            var queuedInput = _pendingUserInputs[0];
            _pendingUserInputs.RemoveAt(0);
            return Task.FromResult(queuedInput);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _onInputCallback = text =>
        {
            _onInputCallback = null;
            completion.TrySetResult(text);
        };
        return completion.Task;
    }

    private void RebuildChatFromMessages()
    {
        _chatContainer.Clear();
        RenderSessionEntries(SessionManager.BuildContextEntries());
    }

    // ---------------------------------------------------------------- notices (L4089-4186)

    private void AddCacheWarmingUsage(UsageEntry entry)
    {
        if (!SettingsManager.GetShowCacheMissNotices())
        {
            return;
        }

        _chatContainer.AddChild(new Spacer(1));
        var usage = CacheWarmerMath.FormatCacheWarmingUsage(entry);
        _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg("dim", usage), 1, 0));
    }

    /// <summary>
    /// Render billing usage for a compaction or branch summary. The notice is derived from persisted
    /// summary usage and is not stored as a separate session entry.
    /// </summary>
    private void AddCompactionCostNotice(CompactionCostNotice notice)
    {
        if (!SettingsManager.GetShowCacheMissNotices())
        {
            return;
        }

        var usage = notice.Usage;
        var tokens = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
        var cost = usage.Cost.Total >= 0.01 ? $" (~${Js.ToFixed(usage.Cost.Total, 2)})" : "";
        var label = notice.Kind == CompactionCostKind.Compaction ? "Compaction" : "Branch summary";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg("warning", $"{label}: {FooterFormatting.FormatTokens(tokens)} tokens billed{cost}"),
            1,
            0));
    }

    private static int CountDroppedThinkingBlocks(AssistantMessage message)
    {
        var count = 0;
        foreach (var diagnostic in message.Diagnostics ?? [])
        {
            if (diagnostic.Type != "anthropic_input_transformations")
            {
                continue;
            }

            if (diagnostic.Details?["transformations"] is not JsonArray transformations)
            {
                continue;
            }

            foreach (var transformation in transformations)
            {
                if (transformation is JsonObject obj
                    && obj.TryGetPropertyValue("type", out var type)
                    && type?.ToString() == "thinking_dropped")
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>TS stop-reason literal; the C# union is an enum.</summary>
    private static string StopReasonText(StopReason reason) => reason switch
    {
        StopReason.Pending => "pending",
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "toolUse",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred",
        _ => reason.ToString(),
    };

    /// <summary>TS 把 modelRuntime 直接当价格源传（结构化类型）；C# 用适配器桥接。</summary>
    private sealed class ModelRuntimePriceSource(ModelRuntime runtime) : CacheStats.IModelPriceSource
    {
        public CacheStats.ModelPriceQuote? GetModel(string provider, string modelId)
        {
            var spec = runtime.GetModel(provider, modelId);
            if (spec is null)
            {
                return null;
            }

            return new CacheStats.ModelPriceQuote
            {
                Cost = new CacheStats.ModelPriceQuoteCost { CacheRead = spec.Cost?.CacheRead ?? 0 },
            };
        }
    }

    private void MaybeShowThinkingDropNotice(AssistantMessage message)
    {
        if (!SettingsManager.GetShowCacheMissNotices())
        {
            return;
        }

        var droppedCount = CountDroppedThinkingBlocks(message);
        if (droppedCount == 0)
        {
            return;
        }

        var previousDroppedCount = 0;
        // message_end reaches the UI before the current message is persisted,
        // so the branch's last assistant message is the previous response.
        var branch = SessionManager.GetBranch();
        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i] is SessionMessageEntry { Message: AssistantMessage previous })
            {
                previousDroppedCount = CountDroppedThinkingBlocks(previous);
                break;
            }
        }

        if (droppedCount <= previousDroppedCount)
        {
            return;
        }

        var noun = droppedCount == 1 ? "thinking block" : "thinking blocks";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg("warning", $"Anthropic dropped {droppedCount} {noun} (details in session)"),
            1,
            0));
    }

    /// <summary>
    /// Show a transcript notice when a completed assistant message paid for a significant cache
    /// miss. Only states observable facts: the miss itself, a model switch, or an idle gap past the
    /// cache TTL.
    /// </summary>
    private void MaybeShowCacheMissNotice(AssistantMessage message)
    {
        if (!SettingsManager.GetShowCacheMissNotices())
        {
            return;
        }

        // Entries don't contain `message` yet: message_end fires before persistence.
        var miss = CacheStats.DetectCacheMiss(SessionManager.GetEntries(), message, new ModelRuntimePriceSource(Session.ModelRuntime));
        if (miss is not null)
        {
            AddCacheMissNotice(miss);
        }
    }

    private void AddCacheMissNotice(CacheStats.CacheMiss miss)
    {
        if (miss.MissedTokens < 20_000 && miss.MissedCost < 0.1)
        {
            return;
        }

        var cost = miss.MissedCost >= 0.01 ? $" (~${Js.ToFixed(miss.MissedCost, 2)})" : "";
        var reBilled = $"{FooterFormatting.FormatTokens(miss.MissedTokens)} tokens re-billed{cost}";
        var label = "Cache miss";
        if (miss.ModelChanged)
        {
            label = "Cache miss after model switch";
        }
        else if (miss.IdleMs >= CacheStats.CacheTtlMs)
        {
            label = $"Cache miss after {(long)Math.Round(miss.IdleMs / 60_000d)}m idle";
        }

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg("warning", $"{label}: {reBilled}"),
            1,
            0));
    }

    // ---------------------------------------------------------------- queued / pending (L4666-4846)

    /// <summary>Get all queued messages (read-only). Combines session queue and compaction queue.</summary>
    internal (IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) GetAllQueuedMessages()
    {
        return (
            [.. Session.GetSteeringMessages(), .. _compactionQueuedMessages.Where(m => m.Mode == CompactionQueueModes.Steer).Select(m => m.Text)],
            [.. Session.GetFollowUpMessages(), .. _compactionQueuedMessages.Where(m => m.Mode == CompactionQueueModes.FollowUp).Select(m => m.Text)]);
    }

    /// <summary>Clear all queued messages and return their contents. Clears both session queue and compaction queue.</summary>
    private (IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) ClearAllQueues()
    {
        var (steering, followUp) = Session.ClearQueue();
        var compactionSteering = _compactionQueuedMessages.Where(m => m.Mode == CompactionQueueModes.Steer).Select(m => m.Text).ToList();
        var compactionFollowUp = _compactionQueuedMessages.Where(m => m.Mode == CompactionQueueModes.FollowUp).Select(m => m.Text).ToList();
        _compactionQueuedMessages.Clear();
        return ([.. steering, .. compactionSteering], [.. followUp, .. compactionFollowUp]);
    }

    internal void UpdatePendingMessagesDisplay()
    {
        _pendingMessagesContainer.Clear();
        var (steeringMessages, followUpMessages) = GetAllQueuedMessages();
        if (steeringMessages.Count > 0 || followUpMessages.Count > 0)
        {
            _pendingMessagesContainer.AddChild(new Spacer(1));
            foreach (var message in steeringMessages)
            {
                var text = ThemeRuntime.Current.Fg("dim", $"Steering: {message}");
                _pendingMessagesContainer.AddChild(new TruncatedText(text, 1, 0));
            }

            foreach (var message in followUpMessages)
            {
                var text = ThemeRuntime.Current.Fg("dim", $"Follow-up: {message}");
                _pendingMessagesContainer.AddChild(new TruncatedText(text, 1, 0));
            }

            var dequeueHint = GetAppKeyDisplay("app.message.dequeue");
            var hintText = ThemeRuntime.Current.Fg("dim", $"↳ {dequeueHint} to edit all queued messages");
            _pendingMessagesContainer.AddChild(new TruncatedText(hintText, 1, 0));
        }
    }

    internal int RestoreQueuedMessagesToEditor(bool abort = false, string? currentText = null)
    {
        var (steering, followUp) = ClearAllQueues();
        var allQueued = steering.Concat(followUp).ToList();
        if (allQueued.Count == 0)
        {
            UpdatePendingMessagesDisplay();
            if (abort)
            {
                _ = Session.AbortAsync();
            }

            return 0;
        }

        var queuedText = string.Join("\n\n", allQueued);
        var baseText = currentText ?? _editor.GetText();
        var combinedText = string.Join("\n\n", new[] { queuedText, baseText }.Where(t => t.Trim().Length > 0));
        _editor.SetText(combinedText);
        UpdatePendingMessagesDisplay();
        if (abort)
        {
            _ = Session.AbortAsync();
        }

        return allQueued.Count;
    }

    internal void QueueCompactionMessage(string text, string mode)
    {
        _compactionQueuedMessages.Add(new CompactionQueuedMessage { Mode = mode, Text = text });
        _editor.AddToHistory(text);
        _editor.SetText("");
        UpdatePendingMessagesDisplay();
        ShowStatus("Queued message for after compaction");
    }

    internal bool IsExtensionCommand(string text)
    {
        if (!text.StartsWith('/'))
        {
            return false;
        }

        var extensionRunner = Session.ExtensionRunner;
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        return extensionRunner.GetCommand(commandName) is not null;
    }

    private async Task FlushCompactionQueueAsync(bool willRetry = false)
    {
        if (_compactionQueuedMessages.Count == 0)
        {
            return;
        }

        var queuedMessages = _compactionQueuedMessages.ToList();
        _compactionQueuedMessages.Clear();
        UpdatePendingMessagesDisplay();

        void RestoreQueue(Exception error)
        {
            Session.ClearQueue();
            _compactionQueuedMessages.AddRange(queuedMessages);
            UpdatePendingMessagesDisplay();
            ShowError(
                $"Failed to send queued message{(queuedMessages.Count > 1 ? "s" : "")}: {error.Message}");
        }

        try
        {
            if (willRetry)
            {
                // When retry is pending, queue messages for the retry turn
                foreach (var message in queuedMessages)
                {
                    if (IsExtensionCommand(message.Text))
                    {
                        await Session.PromptAsync(message.Text).ConfigureAwait(true);
                    }
                    else if (message.Mode == CompactionQueueModes.FollowUp)
                    {
                        await Session.FollowUpAsync(message.Text).ConfigureAwait(true);
                    }
                    else
                    {
                        await Session.SteerAsync(message.Text).ConfigureAwait(true);
                    }
                }

                UpdatePendingMessagesDisplay();
                return;
            }

            // Find first non-extension-command message to use as prompt
            var firstPromptIndex = queuedMessages.FindIndex(message => !IsExtensionCommand(message.Text));
            if (firstPromptIndex == -1)
            {
                // All extension commands - execute them all
                foreach (var message in queuedMessages)
                {
                    await Session.PromptAsync(message.Text).ConfigureAwait(true);
                }

                return;
            }

            // Execute any extension commands before the first prompt
            var preCommands = queuedMessages.Take(firstPromptIndex);
            var firstPrompt = queuedMessages[firstPromptIndex];
            var rest = queuedMessages.Skip(firstPromptIndex + 1);

            foreach (var message in preCommands)
            {
                await Session.PromptAsync(message.Text).ConfigureAwait(true);
            }

            // Start a prompt when idle, or queue it into a run still finishing compaction.
            var promptTask = PromptIgnoringFailureAsync(firstPrompt);

            // Queue remaining messages
            foreach (var message in rest)
            {
                if (IsExtensionCommand(message.Text))
                {
                    await Session.PromptAsync(message.Text).ConfigureAwait(true);
                }
                else if (message.Mode == CompactionQueueModes.FollowUp)
                {
                    await Session.FollowUpAsync(message.Text).ConfigureAwait(true);
                }
                else
                {
                    await Session.SteerAsync(message.Text).ConfigureAwait(true);
                }
            }

            UpdatePendingMessagesDisplay();
            _ = promptTask;
        }
        catch (Exception error)
        {
            RestoreQueue(error);
        }
    }

    /// <summary>TS <c>promptPromise.catch(restoreQueue)</c>: a failed lead prompt restores the queue.</summary>
    private async Task PromptIgnoringFailureAsync(CompactionQueuedMessage firstPrompt)
    {
        try
        {
            await Session.PromptAsync(firstPrompt.Text, new PromptOptions { StreamingBehavior = firstPrompt.Mode }).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Session.ClearQueue();
            _compactionQueuedMessages.Add(firstPrompt);
            UpdatePendingMessagesDisplay();
            ShowError($"Failed to send queued message: {error.Message}");
        }
    }

    /// <summary>Move pending bash components from pending area to chat.</summary>
    private void FlushPendingBashComponents()
    {
        foreach (var component in _pendingBashComponents)
        {
            _pendingMessagesContainer.RemoveChild(component);
            _chatContainer.AddChild(component);
        }

        _pendingBashComponents.Clear();
    }

    private string GetAppKeyDisplay(string action) => KeybindingHints.KeyDisplayText(action) ?? action;

    // ---------------------------------------------------------------- stop (L7063-7081)

    public void Stop(string? fullscreenExitOutput = null)
    {
        DisposeActiveSelector();
        if (SettingsManager.GetShowTerminalProgress())
        {
            _ui.Terminal.SetProgress(false);
        }

        ClearStatusIndicator();
        _themeController.DisableAutoSync();
        ClearExtensionTerminalInputListeners();
        _footer.Dispose();
        _footerDataProvider.Dispose();
        _unsubscribe?.Dispose();
        _unsubscribe = null;
        if (_isInitialized)
        {
            StopInteractiveTui(fullscreenExitOutput ?? SettingsManager.GetFullscreenExitOutput());
            _isInitialized = false;
        }

        UnregisterSignalHandlers();
    }

    /// <summary>Seam: signal-handler registration lands with the 4f-4m lifecycle slice.</summary>
    internal void UnregisterSignalHandlers()
    {
        foreach (var cleanup in _signalCleanupHandlers)
        {
            cleanup();
        }

        _signalCleanupHandlers.Clear();
    }

    // ---------------------------------------------------------------- event dispatch (L3386-3771)

    private async Task HandleEventAsync(AgentSessionEvent @event)
    {
        if (!_isInitialized)
        {
            await InitAsync().ConfigureAwait(true);
        }

        _footer.Invalidate();
        _programStatus.HandleEvent(@event);

        switch (@event)
        {
            case AgentSessionEvent.AgentStartEvent:
                _pendingTools.Clear();
                // Restore main escape handler if retry handler is still active
                // (retry success event fires later, but we need main handler now)
                if (_retryEscapeHandler is not null)
                {
                    _defaultEditor.OnEscape = _retryEscapeHandler;
                    _retryEscapeHandler = null;
                }

                break;

            case AgentSessionEvent.TurnStartEvent:
                if (SettingsManager.GetShowTerminalProgress())
                {
                    _ui.Terminal.SetProgress(true);
                }

                if (_workingVisible)
                {
                    if (_activeStatusIndicator?.Kind != StatusIndicatorKind.Working)
                    {
                        ShowWorkingStatusIndicator();
                    }
                }
                else
                {
                    ClearStatusIndicator();
                }

                _ui.RequestRender();
                break;

            case AgentSessionEvent.QueueUpdateEvent:
                UpdatePendingMessagesDisplay();
                _ui.RequestRender();
                break;

            case AgentSessionEvent.EntryAppendedEvent appended:
                HandleEntryAppended(appended.Entry);
                break;

            case AgentSessionEvent.SessionInfoChangedEvent:
                UpdateTerminalTitle();
                _footer.Invalidate();
                _ui.RequestRender();
                break;

            case AgentSessionEvent.ThinkingLevelChangedEvent:
                _footer.Invalidate();
                UpdateEditorBorderColor();
                break;

            case AgentSessionEvent.MessageStartEvent start:
                HandleMessageStart(start.Message);
                break;

            case AgentSessionEvent.MessageUpdateEvent update:
                HandleMessageUpdate(update.Message);
                break;

            case AgentSessionEvent.MessageEndEvent end:
                HandleMessageEnd(end.Message);
                break;

            case AgentSessionEvent.BashExecutionUpdateEvent:
                // The bash execution callback handles TUI output rendering.
                break;

            case AgentSessionEvent.ToolExecutionStartEvent toolStart:
                HandleToolExecutionStart(toolStart);
                break;

            case AgentSessionEvent.ToolExecutionUpdateEvent toolUpdate:
            {
                if (_pendingTools.TryGetValue(toolUpdate.ToolCallId, out var updateComponent)
                    && toolUpdate.PartialResult is AgentToolResult partial)
                {
                    updateComponent.UpdateResult(partial with { IsError = false }, isPartial: true);
                    _ui.RequestRender();
                }

                break;
            }

            case AgentSessionEvent.ToolExecutionEndEvent toolEnd:
            {
                if (toolEnd.IsError)
                {
                    MaybeShowInstallChangeWarning();
                }

                if (_pendingTools.Remove(toolEnd.ToolCallId, out var endComponent)
                    && toolEnd.Result is AgentToolResult result)
                {
                    endComponent.UpdateResult(result with { IsError = toolEnd.IsError }, durationMs: toolEnd.DurationMs);
                    _ui.RequestRender();
                }

                break;
            }

            case AgentSessionEvent.AgentEndEvent:
                if (SettingsManager.GetShowTerminalProgress())
                {
                    _ui.Terminal.SetProgress(false);
                }

                ClearStatusIndicator(StatusIndicatorKind.Working);
                if (_streamingComponent is not null)
                {
                    _chatContainer.RemoveChild(_streamingComponent);
                    _streamingComponent = null;
                    _streamingMessage = null;
                }

                _pendingTools.Clear();
                _ui.RequestRender();
                break;

            case AgentSessionEvent.AgentSettledEvent:
                CheckShutdownRequested();
                break;

            case AgentSessionEvent.CompactionStartEvent compactionStart:
            {
                if (SettingsManager.GetShowTerminalProgress())
                {
                    _ui.Terminal.SetProgress(true);
                }

                // Keep editor active; submissions are queued during compaction.
                _autoCompactionEscapeHandler = _defaultEditor.OnEscape;
                _defaultEditor.OnEscape = () => Session.AbortCompaction();
                ShowStatusIndicator(new CompactionStatusIndicator(_ui, CompactionReasonOf(compactionStart.Reason)));
                _ui.RequestRender();
                break;
            }

            case AgentSessionEvent.CompactionEndEvent compactionEnd:
                await HandleCompactionEndAsync(compactionEnd).ConfigureAwait(true);
                break;

            case AgentSessionEvent.AutoRetryStartEvent retryStart:
                // Set up escape to abort retry
                _retryEscapeHandler = _defaultEditor.OnEscape;
                _defaultEditor.OnEscape = () => Session.AbortRetry();
                ShowStatusIndicator(new RetryStatusIndicator(_ui, retryStart.Attempt, retryStart.MaxAttempts, retryStart.DelayMs));
                _ui.RequestRender();
                break;

            case AgentSessionEvent.AutoRetryEndEvent retryEnd:
                // Restore escape handler
                if (_retryEscapeHandler is not null)
                {
                    _defaultEditor.OnEscape = _retryEscapeHandler;
                    _retryEscapeHandler = null;
                }

                ClearStatusIndicator(StatusIndicatorKind.Retry);
                // Show error only on final failure (success shows normal response)
                if (!retryEnd.Success)
                {
                    ShowError($"Retry failed after {retryEnd.Attempt} attempts: {retryEnd.FinalError ?? "Unknown error"}");
                }

                _ui.RequestRender();
                break;

            case AgentSessionEvent.SummarizationRetryScheduledEvent scheduled:
                ShowError(scheduled.ErrorMessage);
                ShowStatusIndicator(new RetryStatusIndicator(_ui, scheduled.Attempt, scheduled.MaxAttempts, scheduled.DelayMs));
                _ui.RequestRender();
                break;

            case AgentSessionEvent.SummarizationRetryAttemptStartEvent attemptStart:
                ClearStatusIndicator(StatusIndicatorKind.Retry);
                if (attemptStart.Source == "branchSummary")
                {
                    ShowStatusIndicator(new BranchSummaryStatusIndicator(_ui));
                }
                else
                {
                    ShowStatusIndicator(new CompactionStatusIndicator(_ui, CompactionReasonOf(attemptStart.Reason!)));
                }

                _ui.RequestRender();
                break;

            case AgentSessionEvent.SummarizationRetryFinishedEvent:
                ClearStatusIndicator(StatusIndicatorKind.Retry);
                _ui.RequestRender();
                break;
        }
    }

    // ---------------------------------------------------------------- event branch helpers

    private void HandleEntryAppended(SessionEntry entry)
    {
        if (entry.Id is not null && _entriesRenderedByBoundaryCompaction.Remove(entry.Id))
        {
            return;
        }

        switch (entry)
        {
            case CustomEntry custom:
                AddCustomEntryToChat(custom);
                _ui.RequestRender();
                break;

            case UsageEntry usage when usage.Kind == "cache_warm":
                AddCacheWarmingUsage(usage);
                _ui.RequestRender();
                break;

            case CustomMessageEntry customMessage when customMessage.Display:
                AddMessageToChat(AgentMessages.CreateCustomMessage(
                    customMessage.CustomType,
                    customMessage.Content ?? MessageContent.From(""),
                    customMessage.Display,
                    customMessage.Details,
                    customMessage.Timestamp));
                _ui.RequestRender();
                break;

            case CompactionEntry compaction:
                HandleBoundaryCompactionAppended(compaction);
                break;
        }
    }

    /// <summary>TS <c>entry_appended</c>'s compaction branch: rebuild the transcript around the boundary.</summary>
    private void HandleBoundaryCompactionAppended(CompactionEntry entry)
    {
        var entries = SessionManager.BuildContextEntries();
        if (entries is not [{ Id: var firstId }, ..] || firstId != entry.Id)
        {
            return;
        }

        _chatContainer.Clear();
        var branch = SessionManager.GetBranch().ToList();
        var compactionIndex = branch.FindIndex(e => e.Id == entry.Id);
        var entriesAfterCompaction = branch.Skip(compactionIndex + 1)
            .Select(e => e.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        var retainedEntries = entries.Skip(1).ToList();
        RenderSessionEntries(retainedEntries.Where(e => e.Id is null || !entriesAfterCompaction.Contains(e.Id)).ToList());
        AddMessageToChat(AgentMessages.CreateCompactionSummaryMessage(
            entry.Summary, entry.TokensBefore, entry.Timestamp));
        if (entry.UsageStats is { } usage)
        {
            AddCompactionCostNotice(new CompactionCostNotice
            {
                Kind = CompactionCostKind.Compaction,
                Usage = usage,
            });
        }

        RenderSessionEntries(retainedEntries.Where(e => e.Id is not null && entriesAfterCompaction.Contains(e.Id)).ToList());
        foreach (var entryId in entriesAfterCompaction)
        {
            _entriesRenderedByBoundaryCompaction.Add(entryId);
        }

        _footer.Invalidate();
        _ui.RequestRender();
    }

    private void HandleMessageStart(ChatMessage message)
    {
        switch (message)
        {
            case CustomMessage:
                AddMessageToChat(message);
                _ui.RequestRender();
                break;

            case UserMessage:
                AddMessageToChat(message);
                UpdatePendingMessagesDisplay();
                _ui.RequestRender();
                break;

            case AssistantMessage assistant:
                _streamingComponent = new AssistantMessageComponent(
                    null,
                    _hideThinkingBlock,
                    GetMarkdownThemeWithSettings(),
                    _hiddenThinkingLabel,
                    _outputPad,
                    GetMarkdownTransformers());
                _streamingMessage = assistant;
                _chatContainer.AddChild(_streamingComponent);
                _streamingComponent.UpdateContent(_streamingMessage, true);
                _ui.RequestRender();
                break;
        }
    }

    private void HandleMessageUpdate(AssistantMessage message)
    {
        if (_streamingComponent is null)
        {
            return;
        }

        _streamingMessage = message;
        _streamingComponent.UpdateContent(_streamingMessage, true);

        foreach (var content in _streamingMessage.Content)
        {
            if (content is not ToolCallContent toolCall)
            {
                continue;
            }

            if (!_pendingTools.TryGetValue(toolCall.Id, out var component))
            {
                component = NewToolExecutionComponent(toolCall.Name, toolCall.Id, toolCall.Arguments);
                component.SetExpanded(_toolOutputExpanded);
                _chatContainer.AddChild(component);
                _pendingTools[toolCall.Id] = component;
            }
            else
            {
                component.UpdateArgs(AsArgsDictionary(toolCall.Arguments));
            }
        }

        _ui.RequestRender();
    }

    private void HandleMessageEnd(ChatMessage message)
    {
        if (message is UserMessage)
        {
            return;
        }

        if (_streamingComponent is null || message is not AssistantMessage assistant)
        {
            _ui.RequestRender();
            return;
        }

        _streamingMessage = assistant;
        string? errorMessage = null;
        if (_streamingMessage.StopReason == StopReason.Aborted)
        {
            var retryAttempt = Session.RetryAttempt;
            errorMessage = retryAttempt > 0
                ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}"
                : "Operation aborted";
            _streamingMessage = _streamingMessage with { ErrorMessage = errorMessage };
        }

        _streamingComponent.UpdateContent(_streamingMessage, false);

        if (_streamingMessage.StopReason == StopReason.Aborted || _streamingMessage.StopReason == StopReason.Error)
        {
            errorMessage ??= string.IsNullOrEmpty(_streamingMessage.ErrorMessage) ? "Error" : _streamingMessage.ErrorMessage;
            foreach (var component in _pendingTools.Values)
            {
                component.UpdateResult(AgentToolResult.FromError(errorMessage));
            }

            _pendingTools.Clear();
            MaybeSuggestBugReport(_streamingMessage);
        }
        else
        {
            // Args are now complete - trigger diff computation for edit tools
            foreach (var component in _pendingTools.Values)
            {
                component.SetArgsComplete();
            }

            MaybeShowThinkingDropNotice(_streamingMessage);
            MaybeShowCacheMissNotice(_streamingMessage);
        }

        _streamingComponent = null;
        _streamingMessage = null;
        _footer.Invalidate();
        _ui.RequestRender();
    }

    private void HandleToolExecutionStart(AgentSessionEvent.ToolExecutionStartEvent @event)
    {
        // Nested calls (from codemode scripts) are shown inside their parent's row.
        if (@event.ParentToolCallId is not null)
        {
            return;
        }

        if (!_pendingTools.TryGetValue(@event.ToolCallId, out var component))
        {
            component = NewToolExecutionComponent(@event.ToolName, @event.ToolCallId, @event.Args);
            component.SetExpanded(_toolOutputExpanded);
            _chatContainer.AddChild(component);
            _pendingTools[@event.ToolCallId] = component;
        }

        component.MarkExecutionStarted();
        _ui.RequestRender();
    }

    private async Task HandleCompactionEndAsync(AgentSessionEvent.CompactionEndEvent @event)
    {
        if (SettingsManager.GetShowTerminalProgress())
        {
            _ui.Terminal.SetProgress(false);
        }

        if (_autoCompactionEscapeHandler is not null)
        {
            _defaultEditor.OnEscape = _autoCompactionEscapeHandler;
            _autoCompactionEscapeHandler = null;
        }

        ClearStatusIndicator(StatusIndicatorKind.Compaction);
        if (@event.Aborted)
        {
            if (@event.Reason == "manual")
            {
                ShowError("Compaction cancelled");
            }
            else
            {
                ShowStatus("Auto-compaction cancelled");
            }
        }
        else if (@event.Result is { } result)
        {
            var entries = SessionManager.BuildContextEntries();
            if (entries is not [CompactionEntry, ..])
            {
                throw new InvalidOperationException("Completed compaction is missing from the session context");
            }

            _chatContainer.Clear();
            // The latest compaction is prepended for model context; append it below at its chronological position.
            RenderSessionEntries(entries.Skip(1).ToList());
            AddMessageToChat(AgentMessages.CreateCompactionSummaryMessage(
                result.Summary, result.TokensBefore, NowIso()));
            if (result.Usage is { } usage)
            {
                AddCompactionCostNotice(new CompactionCostNotice
                {
                    Kind = CompactionCostKind.Compaction,
                    Usage = usage,
                });
            }

            _footer.Invalidate();
        }
        else if (@event.ErrorMessage is { } errorMessage)
        {
            if (@event.Reason == "manual")
            {
                ShowError(errorMessage);
            }
            else
            {
                _chatContainer.AddChild(new Spacer(1));
                _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg("error", errorMessage), 1, 0));
            }
        }

        await FlushCompactionQueueAsync(@event.WillRetry).ConfigureAwait(true);
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- seams
}
