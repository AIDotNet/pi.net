using System.Text.Json;
using Pi.Chord.Delta;
using Xunit;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>
/// 慢消费者的待发 op 折叠队列（<c>delta/pending.ts</c>）。核心不变式：把折叠后的 ops 应用到初始值，与把原 ops
/// 依序应用得到同一个值——折叠对副本不可见。
/// </summary>
public class PendingOpsTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Path P(params object[] segments) => new([.. segments.Select(segment => segment switch
    {
        string key => Seg.Key(key),
        int index => Seg.Index(index),
        long index => Seg.Index(index),
        _ => throw new ArgumentException("segment must be a string or an index", nameof(segments)),
    })]);

    private static string JsonOf(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Describe(DeltaOp op) => op switch
    {
        DeltaOp.Replace replace => $"[\"r\",{JsonOf(replace.Value)}]",
        DeltaOp.Set set => $"[\"s\",{PathJson(set.Path)},{JsonOf(set.Value)}]",
        DeltaOp.Delete delete => $"[\"d\",{PathJson(delete.Path)}]",
        DeltaOp.Append append => $"[\"a\",{PathJson(append.Path)},{JsonOf(append.Text)}]",
        DeltaOp.Truncate truncate => $"[\"t\",{PathJson(truncate.Path)},{truncate.Length}]",
        DeltaOp.Splice splice =>
            $"[\"p\",{PathJson(splice.Path)},{splice.Index},{splice.Remove},{JsonOf(splice.Items)}]",
        DeltaOp.Move move => $"[\"m\",{PathJson(move.Path)},{JsonOf(move.Permutation)}]",
        _ => throw new ArgumentException("unknown op", nameof(op)),
    };

    private static string PathJson(Path path) => JsonOf(path.Segments.Select(segment => segment.Value).ToList());

    private static string Describe(object? value) => JsonOf(value);

    private static List<string> Describe(IReadOnlyList<DeltaOp> ops) => [.. ops.Select(Describe)];

    /// <summary>逐条 push、一次 take，并核对回放结果与线上编解码都一致。</summary>
    private static IReadOnlyList<DeltaOp> FoldOneByOne(object? initial, IReadOnlyList<DeltaOp> operations)
    {
        var queue = new PendingOps();
        foreach (var op in operations) queue.Push([op]);
        var taken = Assert.IsType<PendingTake.Ops>(queue.Take());
        var expected = DeltaApply.Apply(operations, Json.DeepClone(initial));
        var actual = DeltaApply.Apply(taken.Operations, Json.DeepClone(initial));
        Assert.Equal(Describe(expected), Describe(actual));
        return taken.Operations;
    }

    private static List<DeltaOp> DistinctSets(int count, int from = 0) =>
        [.. Enumerable.Range(0, count).Select(index => (DeltaOp)new DeltaOp.Set(P("k", from + index), (long)index))];

    // ---------------------------------------------------------------- 折叠

    [Fact]
    public void InterleavedLeavesFoldInPlaceAndAStructuralOpRestartsTheLeafIndex()
    {
        var initial = new Dictionary<string, object?>
        {
            ["a"] = "",
            ["b"] = "",
            ["list"] = new List<object?> { 0L },
        };
        var folded = FoldOneByOne(initial, [
            new DeltaOp.Append(P("a"), "1"),
            new DeltaOp.Append(P("b"), "1"),
            new DeltaOp.Append(P("a"), "2"),
            new DeltaOp.Splice(P("list"), 1, 0, [1L]),
            new DeltaOp.Append(P("b"), "2"),
            new DeltaOp.Append(P("a"), "3"),
        ]);
        Assert.Equal(
            ["[\"a\",[\"a\"],\"12\"]", "[\"a\",[\"b\"],\"1\"]", "[\"p\",[\"list\"],1,0,[1]]",
             "[\"a\",[\"b\"],\"2\"]", "[\"a\",[\"a\"],\"3\"]"],
            Describe(folded));
    }

    [Fact]
    public void JoinsAppendsFoldsAppendsIntoAStringSetAndKeepsSplitSurrogates()
    {
        var text = FoldOneByOne(new Dictionary<string, object?> { ["s"] = "", ["t"] = 0L }, [
            new DeltaOp.Set(P("s"), "ab"),
            new DeltaOp.Append(P("s"), "c"),
            new DeltaOp.Set(P("t"), 1L),
            new DeltaOp.Append(P("s"), "d"),
        ]);
        Assert.Equal(["[\"s\",[\"s\"],\"abcd\"]", "[\"s\",[\"t\"],1]"], Describe(text));

        // 一个代理对拆在两条追加里：拼接后仍是完整码位（TS 侧同一用例）。
        var surrogates = FoldOneByOne(new Dictionary<string, object?> { ["s"] = "", ["n"] = 0L }, [
            new DeltaOp.Append(P("s"), "\uD83D"),
            new DeltaOp.Set(P("n"), 1L),
            new DeltaOp.Append(P("s"), "\uDE00"),
            new DeltaOp.Append(P("s"), "é"),
        ]);
        // 直接比文本：JSON 序列化会把孤立代理项转义，反而看不出拼接是否成对。
        Assert.Equal(2, surrogates.Count);
        Assert.Equal("😀é", Assert.IsType<DeltaOp.Append>(surrogates[0]).Text);
        Assert.Equal(1L, Assert.IsType<DeltaOp.Set>(surrogates[1]).Value);
    }

    [Fact]
    public void AStructuralSetBelowAParentSetIsNotFoldedIntoIt()
    {
        // 祖先原语 s 之后的子孙写必须先被结构 op 清掉索引，否则会错合并。
        var folded = FoldOneByOne(new Dictionary<string, object?> { ["o"] = new Dictionary<string, object?> { ["a"] = "x", ["b"] = "y" } }, [
            new DeltaOp.Append(P("o", "a"), "1"),
            new DeltaOp.Set(P("o", "b"), "q"),
            new DeltaOp.Set(P("o"), "flat"),
            new DeltaOp.Append(P("o"), "!"),
        ]);
        Assert.Equal(
            ["[\"a\",[\"o\",\"a\"],\"1\"]", "[\"s\",[\"o\",\"b\"],\"q\"]", "[\"s\",[\"o\"],\"flat!\"]"],
            Describe(folded));
    }

    [Fact]
    public void DoesNotFoldAcrossOpsThatShiftWhatAPathNames()
    {
        Assert.Equal(3, FoldOneByOne(
            new Dictionary<string, object?> { ["arr"] = new List<object?> { "a", "b", "c" } },
            [
                new DeltaOp.Set(P("arr", 2L), "C"),
                new DeltaOp.Delete(P("arr", 0L)),
                new DeltaOp.Set(P("arr", 1L), "Z"),
            ]).Count);

        Assert.Equal(3, FoldOneByOne(
            new Dictionary<string, object?> { ["arr"] = new List<object?> { 1L, 2L, 3L } },
            [
                new DeltaOp.Set(P("arr", 0L), 10L),
                new DeltaOp.Move(P("arr"), [2L, 1L, 0L]),
                new DeltaOp.Set(P("arr", 0L), 20L),
            ]).Count);

        Assert.Equal(4, FoldOneByOne(
            new Dictionary<string, object?> { ["arr"] = new List<object?> { new Dictionary<string, object?> { ["x"] = 0L } } },
            [
                new DeltaOp.Set(P("arr", 0L, "x"), 1L),
                new DeltaOp.Splice(P("arr"), 1, 0, [new Dictionary<string, object?> { ["x"] = 0L }]),
                new DeltaOp.Set(P("arr", 0L, "x"), 2L),
                new DeltaOp.Set(P("arr", 1L, "x"), 3L),
            ]).Count);
    }

    [Fact]
    public void KeysOfOneParentNeverCollide()
    {
        // "a.b" 与 a/b 是两条不同路径；数字段 1 与字符串段 "1" 在端口里也不同键（比 TS 更保守）。
        var folded = FoldOneByOne(
            new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["b"] = "x" }, ["a.b"] = "z" },
            [
                new DeltaOp.Append(P("a", "b"), "1"),
                new DeltaOp.Set(P("a.b"), "y"),
                new DeltaOp.Append(P("a", "b"), "2"),
            ]);
        Assert.Equal(["[\"a\",[\"a\",\"b\"],\"12\"]", "[\"s\",[\"a.b\"],\"y\"]"], Describe(folded));

        var arrays = FoldOneByOne(
            new Dictionary<string, object?> { ["o"] = new Dictionary<string, object?> { ["1"] = "a" }, ["arr"] = new List<object?> { "x", "y" } },
            [
                new DeltaOp.Set(P("o", "1"), "b"),
                new DeltaOp.Set(P("arr", 1L), "z"),
                new DeltaOp.Set(P("o", "1"), "c"),
                new DeltaOp.Set(P("arr", 1L), "w"),
            ]);
        Assert.Equal(
            ["[\"s\",[\"o\",\"1\"],\"c\"]", "[\"s\",[\"arr\",1],\"w\"]"],
            Describe(arrays));
    }

    [Fact]
    public void ARootReplacementDropsEverythingBeforeIt()
    {
        var replacement = new Dictionary<string, object?> { ["b"] = "x" };
        var folded = FoldOneByOne(new Dictionary<string, object?> { ["a"] = 1L }, [
            new DeltaOp.Set(P("a"), 2L),
            new DeltaOp.Replace(replacement),
            new DeltaOp.Append(P("b"), "y"),
        ]);
        Assert.Equal(["[\"r\",{\"b\":\"x\"}]", "[\"a\",[\"b\"],\"y\"]"], Describe(folded));
    }

    // ---------------------------------------------------------------- 上限

    [Fact]
    public void KeepsTheLimitPendingAndReplacesPastIt()
    {
        var queue = new PendingOps();
        queue.Push([]);
        queue.Push(DistinctSets(PendingOps.MaxPendingOps));
        Assert.Equal(PendingOps.MaxPendingOps, Assert.IsType<PendingTake.Ops>(queue.Take()).Operations.Count);

        // 单批超过上限不算溢出；第二批到来才折叠、才溢出。
        queue.Push(DistinctSets(PendingOps.MaxPendingOps / 2));
        queue.Push(DistinctSets(PendingOps.MaxPendingOps / 2, PendingOps.MaxPendingOps / 2));
        Assert.Equal(PendingOps.MaxPendingOps, Assert.IsType<PendingTake.Ops>(queue.Take()).Operations.Count);

        queue.Push(DistinctSets(PendingOps.MaxPendingOps / 2));
        queue.Push(DistinctSets(PendingOps.MaxPendingOps / 2 + 1, PendingOps.MaxPendingOps / 2));
        Assert.IsType<PendingTake.Replace>(queue.Take());
    }

    [Fact]
    public void CountsOpsAfterFoldingSoRepeatedAppendsNeverOverflow()
    {
        var queue = new PendingOps();
        for (var index = 0; index < 3 * PendingOps.MaxPendingOps; index++)
            queue.Push([new DeltaOp.Append(P("text"), "x")]);
        var taken = Assert.IsType<PendingTake.Ops>(queue.Take());
        var append = Assert.IsType<DeltaOp.Append>(Assert.Single(taken.Operations));
        Assert.Equal(new string('x', 3 * PendingOps.MaxPendingOps), append.Text);
    }

    [Fact]
    public void AccumulatesNothingOnceReplacedUntilTakenOrABatchStartingWithAReplacement()
    {
        var queue = new PendingOps();
        queue.Push(DistinctSets(1));
        queue.Push(DistinctSets(PendingOps.MaxPendingOps + 1));
        queue.Push(DistinctSets(3));
        Assert.IsType<PendingTake.Replace>(queue.Take());
        Assert.Empty(Assert.IsType<PendingTake.Ops>(queue.Take()).Operations);

        queue.Push(DistinctSets(1));
        queue.Push(DistinctSets(PendingOps.MaxPendingOps + 1));
        IReadOnlyList<DeltaOp> fresh = [new DeltaOp.Replace(new Dictionary<string, object?> { ["k"] = new List<object?> { 0L } })];
        queue.Push(fresh);
        Assert.Same(fresh, Assert.IsType<PendingTake.Ops>(queue.Take()).Operations);

        queue.Push(DistinctSets(1));
        queue.Push(fresh);
        queue.Push([new DeltaOp.Set(P("n"), 1L)]);
        var taken = Assert.IsType<PendingTake.Ops>(queue.Take());
        Assert.Equal(["[\"r\",{\"k\":[0]}]", "[\"s\",[\"n\"],1]"], Describe(taken.Operations));
    }

    [Fact]
    public void HandsOneBatchOverAsReceivedWhateverItsSize()
    {
        var queue = new PendingOps();
        var batch = DistinctSets(PendingOps.MaxPendingOps + 1);
        queue.Push(batch);
        Assert.Same(batch, Assert.IsType<PendingTake.Ops>(queue.Take()).Operations);

        queue.Push(batch);
        queue.Push([new DeltaOp.Set(P("n"), 1L)]);
        Assert.IsType<PendingTake.Replace>(queue.Take());
    }

    // ---------------------------------------------------------------- 所有权

    [Fact]
    public void NeverMutatesReceivedBatchesOrOps()
    {
        var first = new List<DeltaOp> { new DeltaOp.Append(P("s"), "1") };
        var queue = new PendingOps();
        queue.Push(first);
        Assert.Same(first, Assert.IsType<PendingTake.Ops>(queue.Take()).Operations);

        var second = new List<DeltaOp> { new DeltaOp.Append(P("s"), "2") };
        queue.Push(first);
        queue.Push(second);
        Assert.Equal(["[\"a\",[\"s\"],\"12\"]"], Describe(Assert.IsType<PendingTake.Ops>(queue.Take()).Operations));
        // 收到的两个批都没被改。
        Assert.Equal(["[\"a\",[\"s\"],\"1\"]"], Describe(first));
        Assert.Equal(["[\"a\",[\"s\"],\"2\"]"], Describe(second));
    }

    [Fact]
    public void SizeCountsAfterFoldingTheReplacementOnceOverflowedAndNothingOnceTaken()
    {
        var queue = new PendingOps();
        Assert.Equal(0, queue.Size);
        queue.Push([]);
        Assert.Equal(0, queue.Size);
        queue.Push([new DeltaOp.Append(P("text"), "a"), new DeltaOp.Set(P("n"), 1L)]);
        Assert.Equal(2, queue.Size);
        queue.Push([
            new DeltaOp.Append(P("text"), "b"),
            new DeltaOp.Set(P("n"), 2L),
            new DeltaOp.Splice(P("list"), 0, 0, [1L]),
        ]);
        Assert.Equal(3, queue.Size);
        queue.Take();
        Assert.Equal(0, queue.Size);

        queue.Push(DistinctSets(PendingOps.MaxPendingOps));
        Assert.Equal(PendingOps.MaxPendingOps, queue.Size);
        queue.Push([new DeltaOp.Set(P("n"), 1L)]);
        Assert.Equal(1, queue.Size);
        Assert.IsType<PendingTake.Replace>(queue.Take());
        Assert.Equal(0, queue.Size);
    }

    // ---------------------------------------------------------------- 折叠对副本不可见（随机游走）

    [Fact]
    public void FoldingIsInvisibleToAReplica()
    {
        var mutations = 0;
        var foldedOps = 0;
        for (var seed = 1; seed <= 4; seed++)
        {
            var random = new Random(seed);
            for (var run = 0; run < 200; run++)
            {
                var (ops, folded) = RunTrackedSequence(random);
                mutations += ops;
                foldedOps += folded;
            }
        }
        // 折叠必须真的发生：一条从不合并的随机序列证明不了任何东西。
        Assert.True(foldedOps < mutations * 0.9, $"折叠比 {foldedOps}/{mutations} 不够");
    }

    private static (int Ops, int Folded) RunTrackedSequence(Random random)
    {
        var initial = new Dictionary<string, object?>
        {
            ["name"] = "pi",
            ["count"] = 0L,
            ["text"] = "",
            ["list"] = new List<object?> { 0L, 1L, 2L },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1L },
        };
        var tracker = new Tracker<Dictionary<string, object?>>(initial);
        var all = new List<DeltaOp>();
        var queue = new PendingOps();
        var steps = 1 + random.Next(30);
        for (var step = 0; step < steps; step++)
        {
            var change = tracker.BeginChange();
            for (var verb = 0; verb < 1 + random.Next(3); verb++) MutateOnce(change, random);
            var prepared = change.Prepare();
            all.AddRange(prepared.Ops);
            queue.Push(prepared.Ops);
            tracker.Adopt(prepared);
        }
        var folded = Assert.IsType<PendingTake.Ops>(queue.Take()).Operations;
        var expected = DeltaApply.Apply(all, Json.DeepClone(initial));
        var actual = DeltaApply.Apply(folded, Json.DeepClone(initial));
        Assert.Equal(Describe(expected), Describe(actual));
        return (all.Count, folded.Count);
    }

    private static void MutateOnce(Tracker<Dictionary<string, object?>>.Change change, Random random)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var op = RandomOp(change.Draft, random);
            if (op is null) continue;
            // 先试应用：不适用的 op 直接跳过，避免把半条 op 记进变更里。
            try
            {
                DeltaApply.Apply([op], change.Draft);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            switch (op)
            {
                case DeltaOp.Set set:
                    change.Set(set.Path, set.Value);
                    break;
                case DeltaOp.Delete delete:
                    change.Delete(delete.Path);
                    break;
                case DeltaOp.Append append:
                    change.Append(append.Path, append.Text);
                    break;
                case DeltaOp.Truncate truncate:
                    change.Truncate(truncate.Path, truncate.Length);
                    break;
                case DeltaOp.Splice splice:
                    change.Splice(splice.Path, splice.Index, splice.Remove, splice.Items);
                    break;
                case DeltaOp.Move move:
                    change.Move(move.Path, move.Permutation);
                    break;
                default:
                    throw new ArgumentException("unexpected op", nameof(op));
            }
            return;
        }
    }

    private static DeltaOp? RandomOp(Dictionary<string, object?> draft, Random random)
    {
        var list = draft["list"] as List<object?>;
        var nested = draft["nested"] as Dictionary<string, object?>;
        return random.Next(9) switch
        {
            0 => new DeltaOp.Set(P("name"), random.Next(2) == 0 ? "pi.net" : "π"),
            1 => new DeltaOp.Set(P("count"), (long)random.Next(50)),
            2 => new DeltaOp.Append(P("text"), new string('t', 1 + random.Next(3))),
            3 => list is null || list.Count == 0
                ? new DeltaOp.Set(P("text"), "empty")
                : new DeltaOp.Splice(P("list"), random.Next(list.Count + 1), 0, [(long)random.Next(10)]),
            4 => list is null
                ? new DeltaOp.Set(P("text"), "nolist")
                : new DeltaOp.Truncate(P("list"), random.Next(list.Count + 1)),
            5 => list is null || list.Count == 0
                ? new DeltaOp.Set(P("nested", "count"), 2L)
                : new DeltaOp.Delete(P("list", (long)random.Next(list.Count))),
            6 => list is null || list.Count < 2
                ? new DeltaOp.Set(P("nested", "count"), 3L)
                : new DeltaOp.Move(P("list"), [.. Enumerable.Range(0, list.Count).OrderBy(_ => random.Next())]),
            7 => new DeltaOp.Set(P("nested", "count"), nested is null ? 0L : (long)random.Next(10)),
            _ => new DeltaOp.Set(P("list", (long)(list?.Count ?? 0)), (long)random.Next(10)),
        };
    }
}
