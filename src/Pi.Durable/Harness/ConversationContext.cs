using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 锁定一个已提交上下文范围的 head 标记与最新可见条目。对应 TS harness <c>context.ts</c>
/// 的 <c>ContextBounds</c>（TS 的 head 是带 head 的条目；C# 以 <see cref="EntryRecord.Head"/>
/// 非空表达同一事实）。
/// </summary>
public sealed record ContextBounds(EntryRecord? Head, EntryId Tail);

internal sealed record ConversationContextRange(
    ContextBounds Bounds,
    IReadOnlyList<EntryRecord> Entries,
    ContextView View,
    IReadOnlySet<EntryId> EditedTargets,
    IReadOnlyList<ChatMessage> Settled,
    IReadOnlyList<ChatMessage> Open);

internal sealed record ConversationContextRead(ContextView View, ConversationContextRange? Range);

/// <summary>
/// 对话上下文的捕获与派生（对应 TS <c>harness/context.ts</c> 全量）：
/// <see cref="CaptureContextBoundsAsync"/> 在 Session 线上两次 O(1) 读锁定范围；
/// <see cref="DeriveContextAsync"/> 可离线扫描派生活动转录与模型上下文；
/// <see cref="OrderToolResults"/> 按调用顺序排放工具结果。
/// </summary>
public static class ConversationContext
{
    private const int ScanPageSize = 256;

    private static readonly HashSet<StopReason> ExcludedStopReasons =
        [StopReason.Aborted, StopReason.Error, StopReason.Deferred];

    private const string MissingResultText =
        "Tool result unavailable: history ends before this call completed.";

    /// <summary>
    /// 捕获当前上下文（或截止到可见条目 <paramref name="at"/> 的上下文）的范围。
    /// 必须运行在 Session 线上；tail 及以下的条目不可变，派生可离线进行。
    /// </summary>
    public static async Task<ContextBounds?> CaptureContextBoundsAsync(
        IStorage storage, ConversationId conversationId, Context context, EntryId? at = null)
    {
        _ = context;
        EntryId? tail;
        if (at is null)
        {
            var page = await storage.ScanEntriesAsync(
                new EntryQuery { ConversationId = conversationId }, 1).ConfigureAwait(false);
            tail = page.Items.Count > 0 ? page.Items[0].Id : null;
            if (tail is null) return null;
        }
        else
        {
            if (await storage.GetEntryAsync(conversationId, at.Value).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    $"Entry {at.Value} is not visible from conversation {conversationId}");
            }
            tail = at;
        }

        return new ContextBounds(
            await storage.FindLatestHeadMarkerAsync(conversationId, tail).ConfigureAwait(false),
            tail.Value);
    }

    /// <summary>一个对话的已提交上下文：范围在线上捕获，条目离线派生。对应 TS <c>readContext</c>。</summary>
    public static async Task<ContextView> ReadContextAsync(
        DurableSession session, IStorage storage, ConversationId conversationId, Context context,
        EntryId? at = null)
    {
        var bounds = await session.ReadOnLineAsync(
            () => CaptureContextBoundsAsync(storage, conversationId, context, at)).ConfigureAwait(false);
        return await DeriveContextAsync(storage, conversationId, bounds, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 调度器使用的增量上下文读取：相同 head 下只扫描缓存 tail 之后的新条目；回退的历史切点从缓存裁剪。
    /// </summary>
    internal static async Task<ConversationContextRead> ReadContextFromAsync(
        DurableSession session, IStorage storage, ConversationId conversationId, Context context,
        EntryId? at, ConversationContextRange? previous)
    {
        var bounds = await session.ReadOnLineAsync(
            () => CaptureContextBoundsAsync(storage, conversationId, context, at)).ConfigureAwait(false);
        if (bounds is null) return new(EmptyView(), null);

        ConversationContextRange range;
        if (previous is null || previous.Bounds.Head?.Id != bounds.Head?.Id)
        {
            range = DeriveRange(
                bounds,
                await ScanRangeAsync(storage, conversationId, bounds, context).ConfigureAwait(false));
        }
        else if (bounds.Tail == previous.Bounds.Tail)
        {
            range = previous;
        }
        else if (bounds.Tail.Value < previous.Bounds.Tail.Value)
        {
            range = DeriveRange(
                bounds,
                previous.Entries.Where(entry => entry.Id.Value <= bounds.Tail.Value).ToList());
        }
        else
        {
            var minEntryId = new EntryId(checked(previous.Bounds.Tail.Value + 1));
            var added = await ScanRangeAsync(storage, new EntryQuery
            {
                ConversationId = conversationId,
                MinEntryId = minEntryId,
                MaxEntryId = bounds.Tail,
            }, context).ConfigureAwait(false);
            range = ExtendRange(previous, bounds, added);
        }

        return new(CloneView(range.View), range);
    }

    /// <summary>
    /// 在捕获的范围之内派生活动转录与模型上下文。对应 TS <c>deriveContext</c>。
    /// </summary>
    public static async Task<ContextView> DeriveContextAsync(
        IStorage storage, ConversationId conversationId, ContextBounds? bounds, Context context)
    {
        if (bounds is null) return EmptyView();
        return DeriveRange(
            bounds,
            await ScanRangeAsync(storage, conversationId, bounds, context).ConfigureAwait(false)).View;
    }

    private static ConversationContextRange DeriveRange(
        ContextBounds bounds, IReadOnlyList<EntryRecord> range)
    {
        var edits = new Dictionary<EntryId, ContextEdit>();
        foreach (var entry in range)
        {
            if (entry.Edits is null) continue;
            foreach (var edit in entry.Edits)
            {
                edits[edit switch
                {
                    ContextEdit.Omit omit => omit.Target,
                    ContextEdit.Replace replace => replace.Target,
                    _ => throw new ArgumentOutOfRangeException(nameof(edit)),
                }] = edit;
            }
        }

        var entries = SelectActive(bounds.Head, range.ToList());
        var contributions = entries.Select(entry =>
            Contribute(entry, edits.GetValueOrDefault(entry.Id))).ToList();
        var (settled, open) = Settle([], contributions.SelectMany(items => items).ToList());
        var messages = LeadWithSystem(settled.Concat(OrderToolResults(open)).ToList());
        var view = new ContextView
        {
            Head = bounds.Head,
            Entries = entries,
            Contributions = contributions,
            Messages = messages,
        };
        return new ConversationContextRange(
            bounds, range, view, edits.Keys.ToHashSet(), settled, open);
    }

    private static ConversationContextRange ExtendRange(
        ConversationContextRange previous, ContextBounds bounds, IReadOnlyList<EntryRecord> added)
    {
        var entries = previous.Entries.Concat(added).ToList();
        if (added.Any(entry =>
                entry.Edits is not null
                || entry.Head is not null
                || previous.EditedTargets.Contains(entry.Id)))
        {
            return DeriveRange(bounds, entries);
        }

        var contributions = added.Select(entry => Contribute(entry, null)).ToList();
        var (settled, open) = Settle(
            previous.Settled,
            previous.Open.Concat(contributions.SelectMany(items => items)).ToList());
        var view = new ContextView
        {
            Head = bounds.Head,
            Entries = previous.View.Entries.Concat(added).ToList(),
            Contributions = previous.View.Contributions.Concat(contributions).ToList(),
            Messages = LeadWithSystem(settled.Concat(OrderToolResults(open)).ToList()),
        };
        return new ConversationContextRange(
            bounds, entries, view, previous.EditedTargets, settled, open);
    }

    private static IReadOnlyList<ChatMessage> Contribute(EntryRecord entry, ContextEdit? edit)
    {
        if (edit is ContextEdit.Omit) return [];
        var messages = edit is ContextEdit.Replace replace ? replace.Messages : entry.Model ?? [];
        return messages
            .Where(message => message is not AssistantMessage assistant
                || !ExcludedStopReasons.Contains(assistant.StopReason))
            .ToList();
    }

    private static (IReadOnlyList<ChatMessage> Settled, IReadOnlyList<ChatMessage> Open) Settle(
        IReadOnlyList<ChatMessage> settled, IReadOnlyList<ChatMessage> open)
    {
        var lastAssistant = -1;
        for (var index = open.Count - 1; index >= 0; index--)
        {
            if (open[index] is not AssistantMessage) continue;
            lastAssistant = index;
            break;
        }

        if (lastAssistant <= 0) return (settled, open);
        return (
            settled.Concat(OrderToolResults(open.Take(lastAssistant).ToList())).ToList(),
            open.Skip(lastAssistant).ToList());
    }

    private static IReadOnlyList<ChatMessage> LeadWithSystem(IReadOnlyList<ChatMessage> messages)
    {
        var firstNonUser = 0;
        while (firstNonUser < messages.Count && messages[firstNonUser] is UserMessage) firstNonUser++;
        if (firstNonUser is 0 || firstNonUser >= messages.Count
            || messages[firstNonUser] is not SystemMessage)
        {
            return messages;
        }

        return [
            messages[firstNonUser],
            .. messages.Take(firstNonUser),
            .. messages.Skip(firstNonUser + 1),
        ];
    }

    private static ContextView CloneView(ContextView view) => new()
    {
        Head = view.Head,
        Entries = view.Entries.ToArray(),
        Contributions = view.Contributions.Select(items => (IReadOnlyList<ChatMessage>)items.ToArray()).ToArray(),
        Messages = view.Messages.ToArray(),
    };

    private static ContextView EmptyView() => new()
    {
        Entries = [],
        Contributions = [],
        Messages = [],
    };

    /// <summary>捕获范围内的原始活动条目，不派生模型上下文。对应 TS <c>activeEntries</c>。</summary>
    public static async Task<IReadOnlyList<EntryRecord>> ActiveEntriesAsync(
        IStorage storage, ConversationId conversationId, ContextBounds? bounds, Context context)
    {
        if (bounds is null) return [];
        return SelectActive(
            bounds.Head,
            await ScanRangeAsync(storage, conversationId, bounds, context).ConfigureAwait(false));
    }

    /// <summary>从 head 标记的 head（或转录起点）到 tail 的可见条目，最旧在前。</summary>
    private static Task<List<EntryRecord>> ScanRangeAsync(
        IStorage storage, ConversationId conversationId, ContextBounds bounds, Context context)
    {
        var query = bounds.Head is null
            ? new EntryQuery { ConversationId = conversationId, MaxEntryId = bounds.Tail }
            : new EntryQuery
            {
                ConversationId = conversationId,
                MinEntryId = bounds.Head.Head!.Value,
                MaxEntryId = bounds.Tail,
            };
        return ScanRangeAsync(storage, query, context);
    }

    private static async Task<List<EntryRecord>> ScanRangeAsync(
        IStorage storage, EntryQuery query, Context context)
    {
        _ = context;
        // 范围内每个条目的编辑都计入，包括 selectActive 丢弃的较旧 head 标记。
        var range = new List<EntryRecord>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await storage.ScanEntriesAsync(query, ScanPageSize, cursor).ConfigureAwait(false);
            range.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null);

        // 扫描按最新优先返回；范围内要求最旧在前。
        range.Reverse();
        return range;
    }

    /// <summary>head 标记 + 范围内的非 head 条目；无标记时即整个范围。对应 TS <c>selectActive</c>。</summary>
    private static List<EntryRecord> SelectActive(EntryRecord? head, List<EntryRecord> range)
        => head is null
            ? range
            : [head, .. range.Where(entry => entry.Head is null)];

    /// <summary>
    /// 把每个 assistant 的工具结果按调用顺序直接排在其后。结果取自下一个 assistant 之前的消息；
    /// 缺失的结果合成，未匹配的结果丢弃。对应 TS <c>orderToolResults</c>。
    /// </summary>
    public static IReadOnlyList<ChatMessage> OrderToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var ordered = new List<ChatMessage>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message is ToolResultMessage) continue;
            ordered.Add(message);
            if (message is not AssistantMessage assistant) continue;
            var calls = assistant.Content
                .OfType<ToolCallContent>()
                .ToList();
            if (calls.Count == 0) continue;

            var results = new Dictionary<string, int>();
            for (var next = index + 1;
                 next < messages.Count && messages[next] is not AssistantMessage;
                 next++)
            {
                if (messages[next] is ToolResultMessage candidate
                    && !results.ContainsKey(candidate.ToolCallId))
                {
                    results[candidate.ToolCallId] = next;
                }
            }

            foreach (var call in calls)
            {
                ordered.Add(
                    results.TryGetValue(call.Id, out var resultIndex)
                        ? messages[resultIndex]
                        : MissingResult(call, assistant.Timestamp ?? 0));
            }
        }

        return ordered;
    }

    private static ToolResultMessage MissingResult(ToolCallContent call, long timestamp)
        => new(call.Id, call.Name, [new TextContent(MissingResultText)], IsError: true,
            Details: new Dictionary<string, object?> { ["reason"] = "missing_result" },
            Timestamp: timestamp);
}
