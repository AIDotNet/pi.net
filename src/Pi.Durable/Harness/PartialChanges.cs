using Pi.Ai.Utils;
using Pi.Chord.Delta;
using Pi.Ai.Types;

namespace Pi.Durable.Harness;

using TxDocChange = Tracker<IReadOnlyDictionary<string, object?>>.Change;
using Path = Pi.Chord.Delta.Path;

/// <summary>
/// 收集一次响应里 partial 消息自上次提交以来增长的文本、思维增量与工具调用参数，让这次提交按「追加」写入，而不是
/// 把整段字符串重新赋值——重新赋值会让 Chord 逐字符比较新旧串，每次提交 O(长度)；追加只花增量本身的长度。
///
/// <para>工具调用参数：每个被跟踪的调用有自己的 <see cref="StreamingJsonParser"/>，逐段吃掉
/// <c>toolcall_delta</c> 的原始文本并给出变更；提交时把这些变更按序应用到已提交的参数上，参数本身不参与整消息赋值，
/// 因此已提交的参数恰好等于「读到目前为止的文本」。（上游 <c>da412b039</c>。）</para>
///
/// <para>块结束（<c>text_end</c> / <c>thinking_end</c> / <c>toolcall_end</c>）会丢掉增量：provider 可能在这时替换
/// 整段内容，那一次提交整体赋值才能修正。对应 TS <c>PartialChanges</c>。</para>
/// </summary>
internal sealed class PartialChanges
{
    /// <summary>一次提交要追加的一段文本：内容块序号、字段（<c>text</c> / <c>thinking</c>）与文本。</summary>
    internal readonly record struct TextAppend(int Index, string Field, string Text);

    /// <summary>
    /// 一次提交要写的东西：文本追加，以及被跟踪工具调用自上次提交以来的参数变更与当时的解析快照（按块序号）。
    /// 对应 TS <c>PartialCommit</c>。
    /// </summary>
    internal sealed record Taken(
        IReadOnlyList<TextAppend> TextAppends,
        IReadOnlyList<ToolTake> ToolCalls);

    /// <summary>一个被跟踪工具调用本次提交要处理的东西。</summary>
    /// <param name="Index">内容块序号。</param>
    /// <param name="Changes">自上次提交以来的参数变更，按序。</param>
    /// <param name="Snapshot">取走变更那一刻解析出的完整参数；块被整段重写时用它重建。</param>
    internal sealed record ToolTake(
        int Index,
        IReadOnlyList<JsonChange> Changes,
        IReadOnlyDictionary<string, object?>? Snapshot);

    /// <summary>一个被跟踪的工具调用：它的解析器、自上次提交以来的变更，以及是否有提交取走过变更。</summary>
    private sealed class ToolCallState
    {
        public required StreamingJsonParser Parser { get; init; }

        public List<JsonChange> Changes { get; set; } = [];

        public bool Committed { get; set; }
    }

    private readonly Dictionary<int, (string Field, string Text)> _text = [];
    private readonly Dictionary<int, ToolCallState> _toolCalls = [];

    /// <summary>记下 <paramref name="evt"/> 改了什么。对应 TS <c>observe()</c>。</summary>
    public void Observe(AssistantMessageEvent evt)
    {
        switch (evt)
        {
            case AssistantMessageEvent.TextDelta delta:
                Add(delta.ContentIndex, "text", delta.Delta);
                return;
            case AssistantMessageEvent.ThinkingDelta delta:
                Add(delta.ContentIndex, "thinking", delta.Delta);
                return;
            case AssistantMessageEvent.ToolCallStart start:
                _toolCalls[start.ContentIndex] = new ToolCallState { Parser = new StreamingJsonParser() };
                return;
            case AssistantMessageEvent.ToolCallDelta delta:
            {
                if (!_toolCalls.TryGetValue(delta.ContentIndex, out var call)) return;
                foreach (var change in call.Parser.Append(delta.Delta))
                {
                    // 参数是对象：其他根显示为「没有参数」，于是改为整段赋值（丢掉跟踪）。
                    if (change.Path.Count == 0
                        && (change is not JsonChange.Set root || root.Value is not IReadOnlyDictionary<string, object?>))
                    {
                        _toolCalls.Remove(delta.ContentIndex);
                        return;
                    }

                    call.Changes.Add(change);
                }

                return;
            }
            case AssistantMessageEvent.TextEnd end:
                _text.Remove(end.ContentIndex);
                return;
            case AssistantMessageEvent.ThinkingEnd end:
                _text.Remove(end.ContentIndex);
                return;
            case AssistantMessageEvent.ToolCallEnd end:
                _toolCalls.Remove(end.ContentIndex);
                return;
        }
    }

    private void Add(int index, string field, string text)
    {
        if (text.Length == 0) return;
        if (_text.TryGetValue(index, out var pending) && pending.Field == field)
        {
            _text[index] = (field, pending.Text + text);
            return;
        }

        // 同一块换了字段（provider 重开了内容块）：旧增量作废。
        _text[index] = (field, text);
    }

    /// <summary>取走自上次调用以来累积的增量；本次提交要写入它们。对应 TS <c>take()</c>。</summary>
    public Taken Take()
    {
        var text = new List<TextAppend>(_text.Count);
        foreach (var (index, (field, value)) in _text) text.Add(new TextAppend(index, field, value));
        _text.Clear();

        var toolCalls = new List<ToolTake>(_toolCalls.Count);
        foreach (var (index, call) in _toolCalls)
        {
            if (call.Changes.Count == 0) continue;
            var snapshot = call.Parser.Snapshot() as IReadOnlyDictionary<string, object?>;
            toolCalls.Add(new ToolTake(index, call.Changes, snapshot));
            call.Changes = [];
        }

        return new Taken(text, toolCalls);
    }

    /// <summary>
    /// 把 <paramref name="message"/> 写进 <c>pi.live</c> 的 <c>generation.message</c>：被跟踪的块逐叶赋值（追加字段与
    /// 参数除外）再追加文本 + 应用参数变更，其余块整体赋值。首次提交（没有已提交消息）或块数变化时整条赋值。
    /// 对应 TS <c>PartialChanges.commit()</c>。
    /// </summary>
    public void Commit(
        TxDocChange live,
        Taken taken,
        Dictionary<string, object?> message,
        IReadOnlyDictionary<string, object?>? committed)
    {
        var committedBlocks = committed is not null
            && committed.TryGetValue("content", out var committedRaw)
            && committedRaw is IReadOnlyList<object?> committedList
                ? committedList
                : (IReadOnlyList<object?>)[];

        if (!message.TryGetValue("content", out var value) || value is not List<object?> blocks)
        {
            // 没有内容块（开流等）：整条赋值。
            live.Set(Live.MessagePath, message);
            return;
        }

        if (committedBlocks.Count == 0 || committedBlocks.Count != blocks.Count)
        {
            // 草稿里还没有 content（这条 generation 是请求开始时建的），或者这条消息的块数变了（新块出现、
            // 或 provider 换掉了块）：整段写入内容块。一条消息里这只发生几次（块开始/结束时），不在流的热路径上。
            //
            // 被跟踪调用的参数要小心：整段重写会覆盖已提交的参数，且新块的首段变更可能就在这一批里。
            //   · 之前提交过参数的调用 → 用解析器的快照重建（快照含它到此刻的全部文本）；
            //   · 新出现的调用 → 先清空，再按变更重建（否则同批到达的首段会丢）。
            var rebuild = new List<ToolTake>(taken.ToolCalls.Count);
            foreach (var take in taken.ToolCalls)
            {
                if (take.Index >= blocks.Count || blocks[take.Index] is not Dictionary<string, object?> block) continue;
                var committedAlready = _toolCalls.TryGetValue(take.Index, out var state) && state.Committed;
                if (committedAlready)
                {
                    block["arguments"] = take.Snapshot ?? new Dictionary<string, object?>();
                }
                else
                {
                    block["arguments"] = new Dictionary<string, object?>();
                    rebuild.Add(take);
                }
            }

            live.Set(Live.MessagePath.Append(Seg.Key("content")), blocks);
            foreach (var take in rebuild)
            {
                var argsPath = Live.MessagePath.Append(Seg.Key("content")).Append(Seg.Index(take.Index))
                    .Append(Seg.Key("arguments"));
                ApplyToolChanges(live, argsPath, take.Changes, baseArguments: null);
                if (_toolCalls.TryGetValue(take.Index, out var state)) state.Committed = true;
            }

            foreach (var take in taken.ToolCalls)
            {
                if (_toolCalls.TryGetValue(take.Index, out var state)) state.Committed = true;
            }
        }
        else
        {
            var takes = new Dictionary<int, ToolTake>(taken.ToolCalls.Count);
            foreach (var take in taken.ToolCalls) takes[take.Index] = take;
            var appends = new Dictionary<int, TextAppend>(taken.TextAppends.Count);
            foreach (var append in taken.TextAppends) appends[append.Index] = append;

            for (var index = 0; index < blocks.Count; index++)
            {
                var blockPath = Live.MessagePath.Append(Seg.Key("content")).Append(Seg.Index(index));
                if (takes.TryGetValue(index, out var take)
                    && blocks[index] is Dictionary<string, object?> trackedBlock)
                {
                    // 参数从变更写：块本身只写其余字段。
                    foreach (var (field, fieldValue) in trackedBlock)
                    {
                        if (field == "arguments") continue;
                        live.Set(blockPath.Append(Seg.Key(field)), fieldValue);
                    }

                    var argsPath = blockPath.Append(Seg.Key("arguments"));
                    var baseArguments = committedBlocks[index] is IReadOnlyDictionary<string, object?> previousBlock
                        && previousBlock.TryGetValue("arguments", out var previousArguments)
                            ? previousArguments
                            : null;
                    ApplyToolChanges(live, argsPath, take.Changes, baseArguments);

                    if (_toolCalls.TryGetValue(index, out var state)) state.Committed = true;
                    continue;
                }

                if (!appends.TryGetValue(index, out var append)
                    || blocks[index] is not Dictionary<string, object?> block
                    || !block.TryGetValue(append.Field, out var current)
                    || current is not string
                    || committedBlocks[index] is not IReadOnlyDictionary<string, object?> committedBlock
                    || !committedBlock.TryGetValue(append.Field, out var previous)
                    || previous is not string)
                {
                    // 没跟踪的块、或已提交的消息里没有可追加的基串：整体赋值（provider 的替换也在这里修正）。
                    live.Set(blockPath, blocks[index]);
                    continue;
                }

                foreach (var (field, fieldValue) in block)
                {
                    if (field == append.Field) continue;
                    live.Set(blockPath.Append(Seg.Key(field)), fieldValue);
                }

                if (append.Text.Length > 0) live.Append(blockPath.Append(Seg.Key(append.Field)), append.Text);
            }
        }

        // 除 content 外的字段逐叶赋值（用量、停止原因、模型等，都是小值）。
        foreach (var (field, fieldValue) in message)
        {
            if (field == "content") continue;
            live.Set(Live.MessagePath.Append(Seg.Key(field)), fieldValue);
        }
    }

    /// <summary>把一次提交的参数变更按序应用到已提交的参数上。对应 TS <c>applyChange()</c>。</summary>
    private static void ApplyToolChanges(
        TxDocChange live, Path argsPath, IReadOnlyList<JsonChange> changes, object? baseArguments)
    {
        // 数组元素上的字符串只能整体赋值，同一提交内要叠加，故留一份本提交的覆盖值。
        Dictionary<Path, string>? overlay = null;
        foreach (var change in changes)
        {
            var path = argsPath;
            foreach (var segment in change.Path)
            {
                path = path.Append(segment is int index ? Seg.Index(index) : Seg.Key((string)segment));
            }

            if (change is JsonChange.Set set)
            {
                live.Set(path, set.Value);
                continue;
            }

            var append = (JsonChange.Append)change;
            if (change.Path.Count > 0 && change.Path[^1] is int)
            {
                overlay ??= [];
                var current = overlay.TryGetValue(path, out var seen)
                    ? seen
                    : ReadString(baseArguments, change.Path) ?? "";
                var next = current + append.Text;
                overlay[path] = next;
                live.Set(path, next);
                continue;
            }

            // 对象成员：Chord 的 appendText() 只收对象成员。
            live.Append(path, append.Text);
        }
    }

    /// <summary>在已提交的参数里按路径读出一个字符串；读不到（或不是字符串）返回 <see langword="null"/>。</summary>
    private static string? ReadString(object? root, IReadOnlyList<object> path)
    {
        var current = root;
        foreach (var segment in path)
        {
            current = current switch
            {
                IReadOnlyDictionary<string, object?> members when segment is string key
                    => members.TryGetValue(key, out var value) ? value : null,
                IReadOnlyList<object?> items when segment is int index
                    => index >= 0 && index < items.Count ? items[index] : null,
                _ => null,
            };
            if (current is null) return null;
        }

        return current as string;
    }
}
