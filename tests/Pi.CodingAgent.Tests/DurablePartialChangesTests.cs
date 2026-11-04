using Pi.Ai.Types;
using Pi.Chord.Delta;
using Pi.Durable.Harness;
using Xunit;

namespace Pi.CodingAgent.Tests;

using Path = Pi.Chord.Delta.Path;

/// <summary>
/// <c>PartialChanges</c>（durable 内部）的写入形状：文本与工具参数都按增量写，参数不参与整消息赋值。
/// 上游 <c>partial-changes.test.ts</c> 的核心断言在这里以「ops + 草稿值」直接钉住。
/// </summary>
/// <remarks>
/// 放在这个工程是因为 <c>Pi.Durable</c> 的 <c>InternalsVisibleTo</c> 已包含 <c>Pi.CodingAgent.Tests</c>；
/// 给 <c>Pi.Durable.Tests</c> 开放 internal 会连带暴露内部的 <c>Pi.Durable.Testing.Assert</c>，与 xUnit 的
/// <c>Assert</c> 在 20 多个文件里撞名。
/// </remarks>
public class PartialChangesTests
{
    private static readonly Path MessagePath =
        Path.Root.Append(Seg.Key("generation")).Append(Seg.Key("message"));

    private static readonly Path ArgumentsPath =
        MessagePath.Append(Seg.Key("content")).Append(Seg.Index(0)).Append(Seg.Key("arguments"));

    private static readonly Path QueryPath = ArgumentsPath.Append(Seg.Key("query"));

    [Fact]
    public void StreamsToolArgumentsAsIncrementalWrites()
    {
        const string json = """{"query":"hello tool"}""";
        var changes = new PartialChanges();
        ObserveToolCall(changes, json);

        var taken = changes.Take();
        var toolTake = Assert.Single(taken.ToolCalls);
        Assert.NotEmpty(toolTake.Changes);

        // 首次提交：块数从 0 变 1，内容块整段写入，参数由变更重建。
        var message = Message(ToolCall("call-1", "echo", []));
        var change = BeginChange();
        changes.Commit(change, taken, message, committed: null);

        Assert.Equal("hello tool", QueryOf(change.Draft));
        var ops = change.Prepare().Ops;
        // 没有任何一次「把整个参数对象重赋」——只有建根的那次空对象。
        Assert.DoesNotContain(ops, op => op is DeltaOp.Set set
            && SamePath(set.Path, ArgumentsPath)
            && set.Value is IReadOnlyDictionary<string, object?> { Count: > 0 });
        // 参数的字符串是逐段追加写的。
        Assert.Contains(ops, op => op is DeltaOp.Append appended && SamePath(appended.Path, QueryPath));
    }

    [Fact]
    public void AppliesOnlyTheChangesOfLaterPieces()
    {
        const string first = """{"query":"hel""";
        const string second = """lo"}""";
        var changes = new PartialChanges();
        ObserveToolCall(changes, first);

        var opened = changes.Take();
        var message = Message(ToolCall("call-1", "echo", []));
        var initial = BeginChange();
        changes.Commit(initial, opened, message, committed: null);
        Assert.Equal("hel", QueryOf(initial.Draft));

        // 第二批增量：只写新增的那一段，已提交的参数不重赋。
        foreach (var ch in second)
        {
            changes.Observe(new AssistantMessageEvent.ToolCallDelta(0, 0, ch.ToString(), Partial()));
        }

        var taken = changes.Take();
        // 已提交的状态：参数停在第一批文本上。
        var committed2 = Message(ToolCall("call-1", "echo", new() { ["query"] = "hel" }));
        var message2 = Message(ToolCall("call-1", "echo", new() { ["query"] = "hello" }));
        var next = BeginChange(committed2);
        changes.Commit(next, taken, message2, committed2);

        Assert.Equal("hello", QueryOf(next.Draft));
        var ops = next.Prepare().Ops;
        Assert.DoesNotContain(ops, op => op is DeltaOp.Set set && SamePath(set.Path, ArgumentsPath));
        Assert.All(
            ops.Where(op => op is DeltaOp.Append).Cast<DeltaOp.Append>(),
            append => Assert.True(SamePath(append.Path, QueryPath)));
        // 只追加了 "lo"。
        Assert.Equal("lo", string.Concat(ops.OfType<DeltaOp.Append>().Select(append => append.Text)));
    }

    [Fact]
    public void StopsTrackingACallWhoseRootIsNotAnObject()
    {
        var changes = new PartialChanges();
        ObserveToolCall(changes, "[1,2]");
        Assert.Empty(changes.Take().ToolCalls);
    }

    [Fact]
    public void StreamsTextAsAppendsInsteadOfReassigningTheMessage()
    {
        var changes = new PartialChanges();
        changes.Observe(new AssistantMessageEvent.TextDelta(0, "hello", 0, Partial()));
        var taken = changes.Take();
        Assert.Single(taken.TextAppends);

        // 已提交的块数相同：只追加文本，其余字段不动。
        var committed = Message(Text("hell"));
        var change = BeginChange(committed);
        changes.Commit(change, taken, Message(Text("hello")), committed);

        var ops = change.Prepare().Ops;
        var append = Assert.Single(ops.OfType<DeltaOp.Append>());
        Assert.True(SamePath(append.Path, MessagePath
            .Append(Seg.Key("content")).Append(Seg.Index(0)).Append(Seg.Key("text"))));
        Assert.Equal("hello", append.Text);
    }

    // ─── 夹具 ───────────────────────────────────────────────────────────────

    private static void ObserveToolCall(PartialChanges changes, string arguments)
    {
        changes.Observe(new AssistantMessageEvent.ToolCallStart(0, Partial()));
        foreach (var ch in arguments)
        {
            changes.Observe(new AssistantMessageEvent.ToolCallDelta(0, 0, ch.ToString(), Partial()));
        }
    }

    private static AssistantMessage Partial() => new([]);

    private static Dictionary<string, object?> Message(params object?[] blocks) =>
        new() { ["content"] = blocks.ToList() };

    private static Dictionary<string, object?> Text(string text) =>
        new() { ["kind"] = "text", ["text"] = text };

    private static Dictionary<string, object?> ToolCall(
        string id, string name, Dictionary<string, object?> arguments) => new()
    {
        ["kind"] = "toolCall",
        ["id"] = id,
        ["name"] = name,
        ["arguments"] = arguments,
    };

    /// <summary>以「<c>generation.message</c> 已是这条消息」的文档为基座开一次变更。</summary>
    private static Tracker<IReadOnlyDictionary<string, object?>>.Change BeginChange(
        IReadOnlyDictionary<string, object?>? message = null)
        => new Tracker<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["generation"] = new Dictionary<string, object?> { ["message"] = message },
        }).BeginChange();

    private static string? QueryOf(IReadOnlyDictionary<string, object?> draft)
        => draft.TryGetValue("generation", out var generation)
            && generation is IReadOnlyDictionary<string, object?> generationMap
            && generationMap.TryGetValue("message", out var message)
            && message is IReadOnlyDictionary<string, object?> messageMap
            && messageMap.TryGetValue("content", out var content)
            && content is IReadOnlyList<object?> blocks
            && blocks.Count > 0
            && blocks[0] is IReadOnlyDictionary<string, object?> block
            && block.TryGetValue("arguments", out var arguments)
            && arguments is IReadOnlyDictionary<string, object?> argumentMap
            && argumentMap.TryGetValue("query", out var query)
                ? query as string
                : null;

    private static bool SamePath(Path path, Path expected) => path.Segments.SequenceEqual(expected.Segments);
}
