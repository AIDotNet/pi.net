// ============================================================================
// Service-only chat view — port of experimental/client-tui-chat.ts (4e-3c-3)
// ============================================================================
//
// Renders the root conversation's durable view for the service-only experimental
// presentation: the transcript (user / assistant / tool cards), the queued inbox items,
// and the live status line (retry, deferred, compaction, running tool, working run).
//
// The view arrives over the `pi.transcript` service as a chord value tree (C365/C368):
// `entries` decode into typed <see cref="EntryRecord"/>s (see RemoteServiceValues.DecodeView),
// while the built-in documents (`pi.live`, `pi.inbox`) stay value trees — exactly what the
// worker published. TS reads the same trees through its `LiveState` / `InboxState` casts;
// this port reads them field by field (C375), projecting the streamed assistant partial
// through <see cref="JsonTrees.ToAssistantMessage"/>.

using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools.Renderers;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Pi.Tui;
using Pi.Tui.Components;
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// The <c>pi.live</c> document of a view: the active run, the streaming answer, and running
/// tools. TS <c>liveOf</c>; the port hands back the raw value tree because the document
/// crosses the wire as one (C375).
/// </summary>
public static class ClientTuiChat
{
    /// <summary>TS <c>liveOf(view)</c>: the live document, or an empty tree when absent.</summary>
    public static IReadOnlyDictionary<string, object?> LiveOf(ConversationView view)
        => view.Docs.TryGetValue("pi.live", out var live) ? live : EmptyTree;

    /// <summary>TS the inline <c>(view.docs["pi.inbox"] ?? { items: [] })</c> cast.</summary>
    public static IReadOnlyList<object?> InboxItemsOf(ConversationView view)
        => view.Docs.TryGetValue("pi.inbox", out var inbox)
            && inbox.TryGetValue("items", out var items)
            && items is IReadOnlyList<object?> list
                ? list
                : [];

    private static IReadOnlyDictionary<string, object?> EmptyTree { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Renders the root conversation's durable view for the service-only experimental presentation.</summary>
/// <remarks>
/// Port of TS <c>ExperimentalChatView</c>. The class-level fields TS keeps private become
/// private members here; the three containers TS exposes as <c>readonly</c> stay public so the
/// TUI can mount them (its layout owns the document / pending-messages / status rows).
/// </remarks>
public sealed class ExperimentalChatView
{
    private static readonly IReadOnlyDictionary<string, ToolRenderers> Renderers =
        BuiltInToolRenderers.CreateAllToolRenderers();

    public Container Transcript { get; } = new();

    public Container PendingMessages { get; } = new();

    public Container Status { get; } = new();

    private readonly ITui _ui;
    private readonly string _cwd;

    /// <summary>The newest card per call ID; provider call IDs may repeat across turns.</summary>
    private readonly Dictionary<string, ToolExecutionComponent> _tools = new(StringComparer.Ordinal);

    /// <summary>Every card shown, also older ones whose call ID a later turn reused.</summary>
    private readonly List<ToolExecutionComponent> _cards = [];

    /// <summary>Call IDs whose cards the streaming answer created; its entry takes them over.</summary>
    private readonly HashSet<string> _streamingCalls = new(StringComparer.Ordinal);

    private List<double> _renderedEntryIds = [];
    private AssistantMessageComponent? _streaming;
    private StatusIndicator? _indicator;
    private string _statusText = "";

    public ExperimentalChatView(ITui ui, string cwd)
    {
        _ui = ui;
        _cwd = cwd;
    }

    public void Apply(ConversationView view)
    {
        var live = ClientTuiChat.LiveOf(view);
        SyncTranscript(view.Entries);
        var message = GenerationMessage(live);
        // A partial without its entry was dropped, for example by a retry: render the transcript again.
        if (message is null && _streaming is not null) Rebuild(view.Entries);
        if (message is not null) SyncStreaming(message);
        if (live.TryGetValue("tools", out var tools) && tools is IReadOnlyList<object?> slots)
        {
            foreach (var slot in slots)
            {
                var fields = Fields(slot);
                var status = Text(Opt(fields, "status"));
                if (status == "pending") continue;
                var component = Tool(Text(Opt(fields, "name")), Text(Opt(fields, "callId")));
                component.SetArgsComplete();
                if (status != "running") continue;
                component.MarkExecutionStarted();
                if (fields.ContainsKey("output") && Opt(fields, "output") is string output)
                {
                    component.UpdateResult(
                        new AgentToolResult([new TextContent(output)],
                            Details: Opt(fields, "details"), IsError: false),
                        isPartial: true);
                }
            }
        }

        SyncQueue(ClientTuiChat.InboxItemsOf(view));
        SyncStatus(live);
        Transcript.Invalidate();
        PendingMessages.Invalidate();
        Status.Invalidate();
    }

    public void RefreshTheme(ConversationView view)
    {
        _indicator?.Dispose();
        _indicator = null;
        _statusText = "";
        Status.Clear();
        Rebuild(view.Entries);
        Apply(view);
    }

    public void Dispose()
    {
        DiscardTools();
        _indicator?.Dispose();
    }

    /// <summary>Finish every card: a running bash card keeps a timer until it gets a final result.</summary>
    private void DiscardTools()
    {
        foreach (var component in _cards)
        {
            component.UpdateResult(new AgentToolResult([]), isPartial: false);
        }

        _cards.Clear();
        _tools.Clear();
        _streamingCalls.Clear();
    }

    private void SyncQueue(IReadOnlyList<object?> items)
    {
        PendingMessages.Clear();
        foreach (var item in items)
        {
            var fields = Fields(item);
            var mode = Text(Opt(fields, "mode"));
            var text = mode == "write"
                ? $"<{EntryKind(fields)}>"
                : UserText(Opt(fields, "content")).ReplaceWhitespace();
            PendingMessages.AddChild(new TruncatedText(
                ThemeRuntime.Current.Fg("muted", $"[{mode}] {text}"), 1, 0));
        }
    }

    private void SyncStatus(IReadOnlyDictionary<string, object?> live)
    {
        var generation = ObjectAt(live, "generation");
        var compaction = FirstAt(live, "compactions");
        var runningTool = FindRunningTool(live);
        var text = "";
        // TS tests the keys with `!== undefined`; the producer deletes a key to clear it, so
        // key presence is the faithful check (never a null value in a published tree).
        if (generation is not null && generation.ContainsKey("retry"))
        {
            var retry = Fields(Opt(generation, "retry")!);
            // TS: attempt is 0-based; the message counts from 1.
            text = $"Retrying (attempt {Number(Opt(generation, "attempt")) + 1}): {Text(Opt(retry, "error"))}";
        }
        else if (generation is not null && generation.ContainsKey("deferred"))
        {
            text = "Waiting for deferred response...";
        }
        else if (compaction is not null)
        {
            text = compaction.ContainsKey("retry")
                ? $"Retrying {Text(Opt(compaction, "reason"))} compaction (attempt {Number(Opt(compaction, "attempt")) + 1})..."
                : $"Compacting ({Text(Opt(compaction, "reason"))})...";
        }
        else if (runningTool is not null)
        {
            text = $"Running {Text(Opt(runningTool, "name"))}... (esc to abort)";
        }
        else if (live.ContainsKey("run"))
        {
            text = "Working... (esc to abort)";
        }

        if (text == _statusText) return;
        _statusText = text;
        _indicator?.Dispose();
        _indicator = null;
        Status.Clear();
        if (text.Length > 0)
        {
            _indicator = new WorkingStatusIndicator(_ui, text);
            Status.AddChild(_indicator);
        }
    }

    private void SyncTranscript(IReadOnlyList<EntryRecord> entries)
    {
        // Compaction and resets replace the head of the active transcript.
        if (_renderedEntryIds.Where((id, index) =>
                entries.Count <= index || entries[index].Id.Value != (long)id).Any())
        {
            Rebuild(entries);
        }

        for (var index = _renderedEntryIds.Count; index < entries.Count; index++)
        {
            AddEntry(entries[index]);
            _renderedEntryIds.Add(entries[index].Id.Value);
        }
    }

    private void Rebuild(IReadOnlyList<EntryRecord> entries)
    {
        Transcript.Clear();
        DiscardTools();
        _renderedEntryIds = [];
        _streaming = null;
        foreach (var entry in entries)
        {
            AddEntry(entry);
            _renderedEntryIds.Add(entry.Id.Value);
        }
    }

    private void AddEntry(EntryRecord entry)
    {
        var message = entry.Model is { Count: > 0 } ? entry.Model[0] : null;
        if (entry.Kind == "pi.user" && message is UserMessage user)
        {
            Transcript.AddChild(new Spacer(1));
            Transcript.AddChild(new UserMessageComponent(UserText(user.Content)));
        }
        else if (entry.Kind == "pi.assistant" && message is AssistantMessage assistant)
        {
            var component = _streaming ?? new AssistantMessageComponent();
            if (_streaming is null) Transcript.AddChild(component);
            _streaming = null;
            component.UpdateContent(assistant, isStreaming: false);
            // Only a tool-calling answer runs its calls; an aborted, failed, or truncated one never does.
            var ran = assistant.StopReason == StopReason.ToolUse;
            foreach (var content in assistant.Content)
            {
                if (content is not ToolCallContent call) continue;
                var streamed = _streamingCalls.Contains(call.Id);
                if (!ran && !streamed) continue;
                var card = Tool(call.Name, call.Id, call.Arguments, fresh: !streamed);
                card.SetArgsComplete();
                if (!ran)
                {
                    const string text = "Not run: the answer was interrupted.";
                    card.UpdateResult(AgentToolResult.FromError(text), isPartial: false);
                }
            }

            _streamingCalls.Clear();
        }
        else if (entry.Kind == "pi.tool-result" && message is ToolResultMessage result)
        {
            Tool(result.ToolName, result.ToolCallId).UpdateResult(
                new AgentToolResult(result.Content, Details: result.Details, IsError: result.IsError));
        }
        else if (entry.Kind == "pi.compaction")
        {
            AddText(ThemeRuntime.Current.Fg("muted", "[compaction]"));
            if (message is UserMessage compactionUser) AddText(UserText(compactionUser.Content));
        }
        else if (entry.Kind == "pi.reset")
        {
            AddText(ThemeRuntime.Current.Fg("muted", "[new context]"));
        }
    }

    private void SyncStreaming(AssistantMessage message)
    {
        if (_streaming is null)
        {
            _streaming = new AssistantMessageComponent();
            Transcript.AddChild(_streaming);
        }

        _streaming.UpdateContent(message, isStreaming: true);
        foreach (var content in message.Content)
        {
            if (content is not ToolCallContent call) continue;
            Tool(call.Name, call.Id, call.Arguments, fresh: !_streamingCalls.Contains(call.Id));
            _streamingCalls.Add(call.Id);
        }
    }

    /// <summary>The card of a call; <paramref name="fresh"/> starts a new one for a call ID an earlier turn used.</summary>
    private ToolExecutionComponent Tool(
        string toolName, string toolCallId, object? args = null, bool fresh = false)
    {
        if (!fresh && _tools.TryGetValue(toolCallId, out var existing))
        {
            if (args is not null) existing.UpdateArgs(ValueTreeArgs.Of(args));
            return existing;
        }

        var component = new ToolExecutionComponent(
            toolName,
            toolCallId,
            ValueTreeArgs.Of(args),
            options: null,
            Renderers.TryGetValue(toolName, out var renderers) ? renderers : null,
            _ui,
            _cwd);
        Transcript.AddChild(component);
        _cards.Add(component);
        _tools[toolCallId] = component;
        return component;
    }

    private void AddText(string text)
    {
        Transcript.AddChild(new Spacer(1));
        Transcript.AddChild(new Text(text, 1, 0));
    }

    // ---------------------------------------------------------------- tree reads

    /// <summary>TS <c>live.generation?.message</c>: the committed throttled partial, as a tree.</summary>
    private static AssistantMessage? GenerationMessage(IReadOnlyDictionary<string, object?> live)
        => Opt(ObjectAt(live, "generation"), "message") is IReadOnlyDictionary<string, object?> tree
            ? JsonTrees.ToAssistantMessage(tree)
            : null;

    private static IReadOnlyDictionary<string, object?>? ObjectAt(
        IReadOnlyDictionary<string, object?> fields, string key)
        => Opt(fields, key) as IReadOnlyDictionary<string, object?>;

    private static IReadOnlyDictionary<string, object?>? FirstAt(
        IReadOnlyDictionary<string, object?> fields, string key)
        => Opt(fields, key) is IReadOnlyList<object?> { Count: > 0 } list
            ? list[0] as IReadOnlyDictionary<string, object?>
            : null;

    private static IReadOnlyDictionary<string, object?>? FindRunningTool(
        IReadOnlyDictionary<string, object?> live)
    {
        if (Opt(live, "tools") is not IReadOnlyList<object?> slots) return null;
        foreach (var slot in slots)
        {
            var fields = Fields(slot);
            if (Text(Opt(fields, "status")) == "running") return fields;
        }

        return null;
    }

    /// <summary>TS <c>item.entry.kind</c> for a queued write.</summary>
    private static string EntryKind(IReadOnlyDictionary<string, object?> item)
        => Opt(Fields(Opt(item, "entry")), "kind") is string kind ? kind : "undefined";

    /// <summary>TS <c>userText(content)</c> for a typed user message: the text branch, or the text blocks joined.</summary>
    private static string UserText(MessageContent content) => content switch
    {
        MessageContent.Text text => text.Value,
        MessageContent.Blocks blocks => string.Concat(
            blocks.Value.OfType<TextContent>().Select(block => block.Text)),
        _ => "",
    };

    /// <summary>
    /// TS <c>userText(content)</c> over a value tree (the inbox's <c>JsonRepresentation&lt;UserInput&gt;</c>):
    /// a bare string, or the text blocks of an array joined.
    /// </summary>
    private static string UserText(object? content)
    {
        if (content is string text) return text;
        if (content is IReadOnlyList<object?> blocks)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var block in blocks)
            {
                if (Opt(Fields(block), "text") is string blockText) builder.Append(blockText);
            }

            return builder.ToString();
        }

        return "";
    }

    private static IReadOnlyDictionary<string, object?> Fields(object? value)
        => value as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException($"Expected an object, got {value?.GetType().Name ?? "null"}");

    private static object? Opt(IReadOnlyDictionary<string, object?>? fields, string key)
        => fields is not null && fields.TryGetValue(key, out var value) ? value : null;

    private static string Text(object? value)
        => value as string ?? throw new InvalidOperationException($"Expected a string, got {value?.GetType().Name ?? "null"}");

    private static double Number(object? value) => value switch
    {
        double number => number,
        long integer => integer,
        int small => small,
        _ => throw new InvalidOperationException($"Expected a number, got {value?.GetType().Name ?? "null"}"),
    };
}

/// <summary>
/// Tool-call arguments as the renderers expect them. TS hands the wire value straight to
/// <c>ToolExecutionComponent</c> (its args are <c>unknown</c>); the C# component takes a
/// dictionary, so a tree is passed through and anything else becomes an empty one — the same
/// "arguments are a JSON object" shape the wire carries.
/// </summary>
internal static class ValueTreeArgs
{
    public static IReadOnlyDictionary<string, object?> Of(object? args)
        => args as IReadOnlyDictionary<string, object?> ?? Empty;

    private static IReadOnlyDictionary<string, object?> Empty { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// TS <c>String.prototype.replace(/\s+/g, " ")</c> for the queued-item previews: every
/// whitespace run (leading and trailing included) collapses to a single space, using the
/// JS <c>\s</c> class (<see cref="JsString.IsWhitespace"/>), not .NET's.
/// </summary>
internal static class UserTextWhitespace
{
    public static string ReplaceWhitespace(this string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var inWhitespace = false;
        foreach (var character in text)
        {
            if (JsString.IsWhitespace(character))
            {
                if (!inWhitespace) builder.Append(' ');
                inWhitespace = true;
            }
            else
            {
                builder.Append(character);
                inWhitespace = false;
            }
        }

        return builder.ToString();
    }
}
