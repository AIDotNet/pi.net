using Pi.Chord.Delta;
using Pi.Ai.Types;

namespace Pi.Durable.Harness;

using TxDocChange = Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>
/// 收集一次响应里 partial 消息自上次提交以来增长的文本与思维增量，让这次提交按「追加」写入，而不是把整段字符串
/// 重新赋值——重新赋值会让 Chord 逐字符比较新旧串，每次提交 O(长度)；追加只花增量本身的长度。
///
/// <para>上游对应物还跟踪工具调用参数的增量（自己的 <c>StreamingJsonParser</c>），那需要 pi-ai 的增量 JSON 解析器，
/// 端口没有移植；工具调用块因此仍整体赋值（协议语义相同，只是大参数流式时每次提交仍 O(参数长度)）。</para>
///
/// <para>块结束（<c>text_end</c> / <c>thinking_end</c>）会丢掉增量：provider 可能在这时替换整段文本，那一次提交
/// 整体赋值才能修正。对应 TS <c>PartialChanges</c>（文本部分）。</para>
/// </summary>
internal sealed class PartialChanges
{
    /// <summary>一次提交要追加的一段文本：内容块序号、字段（<c>text</c> / <c>thinking</c>）与文本。</summary>
    internal readonly record struct TextAppend(int Index, string Field, string Text);

    private readonly Dictionary<int, (string Field, string Text)> _text = [];

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
            case AssistantMessageEvent.TextEnd end:
                _text.Remove(end.ContentIndex);
                return;
            case AssistantMessageEvent.ThinkingEnd end:
                _text.Remove(end.ContentIndex);
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

    /// <summary>取走自上次调用以来累积的增量；本次提交要追加它们。</summary>
    public IReadOnlyList<TextAppend> Take()
    {
        if (_text.Count == 0) return [];
        var taken = new List<TextAppend>(_text.Count);
        foreach (var (index, (field, text)) in _text) taken.Add(new TextAppend(index, field, text));
        _text.Clear();
        return taken;
    }

    /// <summary>
    /// 把 <paramref name="message"/> 写进 <c>pi.live</c> 的 <c>generation.message</c>：被跟踪的块逐叶赋值（追加字段除外）
    /// 再追加文本，其余块整体赋值。首次提交（没有已提交消息）时整条赋值。
    /// 对应 TS <c>PartialChanges.commit()</c>。
    /// </summary>
    public static void Commit(
        TxDocChange live,
        IReadOnlyList<TextAppend> taken,
        Dictionary<string, object?> message,
        IReadOnlyDictionary<string, object?>? committed)
    {
        var committedBlocks = committed is not null
            && committed.TryGetValue("content", out var raw)
            && raw is IReadOnlyList<object?> list
                ? list
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
            // 或 provider 换掉了块）：整段写入。一条消息里这只发生几次（块开始/结束时），不在流的热路径上。
            live.Set(Live.MessagePath.Append(Seg.Key("content")), blocks);
        }
        else
        {
            var appends = new Dictionary<int, TextAppend>(taken.Count);
            foreach (var append in taken) appends[append.Index] = append;

            for (var index = 0; index < blocks.Count; index++)
            {
                var blockPath = Live.MessagePath.Append(Seg.Key("content")).Append(Seg.Index(index));
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
}
