namespace Pi.Chord.Delta;

/// <summary>
/// <see cref="PendingOps.Take"/> 交出的内容：折叠后的待发 ops，或溢出后的 <see cref="Replace"/>——后者由队列属主
/// 换成一次「用当前值整体替换」的根写。对应 TS <c>PendingTake</c>。
/// </summary>
public abstract record PendingTake
{
    private PendingTake() { }

    /// <summary>折叠后的待发 ops（可能为空）。</summary>
    public sealed record Ops(IReadOnlyList<DeltaOp> Operations) : PendingTake;

    /// <summary>溢出：改为用属主的当前值做一次根替换。</summary>
    public sealed record Replace : PendingTake;
}

/// <summary>
/// 一个状态对一个慢消费者的待发 ops：按到达顺序、原样保留；折叠只在必须时发生。对应 TS
/// <c>delta/pending.ts</c> 的 <c>PendingOps</c>。
///
/// <para>折叠规则：<c>r</c> 丢弃它之前的一切；叶子 op（<c>a</c> 追加、原语的 <c>s</c> 设置）合并到同路径上最后一个
/// 待发叶子 op（<c>a</c>+<c>a</c> 拼接、<c>s</c> 覆盖 <c>s</c> 或 <c>a</c>、字符串 <c>s</c> 之后的 <c>a</c> 追进去）；
/// 其余任何 op 清空叶子索引。不同路径的叶子 op 可交换，祖先原语 <c>s</c> 之后的子孙写不行——那要先由一次结构 op
/// 清掉索引。超过 <c>MaxPendingOps</c> 之后只保留「用当前值替换」。</para>
///
/// <para>设计差异：TS 的叶子索引键是 <c>JSON.stringify(path)</c>（因为 <c>["a.b"]</c> 与 <c>["a","b"]</c> 必须不同键），
/// C# 的 <see cref="Path"/> 本就按段序列做值相等，直接当键即可；代价是数字段与同形字符串段（<c>1</c> 与 <c>"1"</c>）
/// 在端口里也不同键，比 TS 更保守——少合并一次，不会错合并。</para>
///
/// <para>收到的 op 数组与操作对象一律不改（它们同时喂给别的消费者），合并总是新建一个 op。</para>
/// </summary>
public sealed class PendingOps
{
    /// <summary>超过此数的待发 op 之后，只保留「用当前值替换」。对应 TS <c>MAX_PENDING_OPS</c>。</summary>
    public const int MaxPendingOps = 10_000;

    private static readonly PendingTake.Ops NoOps = new([]);

    private static readonly PendingTake.Replace ReplaceTake = new();

    private State _state = State.Empty;

    /// <summary>按一个提交的 ops 入队。</summary>
    public void Push(IReadOnlyList<DeltaOp> operations)
    {
        if (operations.Count == 0) return;
        // 以 r 开头的批（tracker 与 diffRevisions 唯一放置它的地方）丢弃此前一切，包括溢出，并原样保留。
        if (operations[0] is DeltaOp.Replace)
        {
            _state = State.Batch(operations);
            return;
        }
        switch (_state.Kind)
        {
            case StateKind.Empty:
                _state = State.Batch(operations);
                return;
            case StateKind.Batch:
            {
                var received = _state.BatchOperations!;
                var folding = State.Folding();
                _state = folding;
                if (Fold(received, folding)) Fold(operations, folding);
                return;
            }
            case StateKind.Folding:
                Fold(operations, _state);
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// 折叠之后的待发 op 数；溢出后为 1（属主将要发的那个替换）。一批按收到的原样计数，所以单批超过
    /// <see cref="MaxPendingOps"/> 时仍按全量计数，直到第二批到来才溢出。
    /// </summary>
    public int Size => _state.Kind switch
    {
        StateKind.Empty => 0,
        StateKind.Replace => 1,
        StateKind.Batch => _state.BatchOperations!.Count,
        _ => _state.Folded!.Count,
    };

    /// <summary>交出所有待发内容并恢复空态。</summary>
    public PendingTake Take()
    {
        var state = _state;
        _state = State.Empty;
        switch (state.Kind)
        {
            case StateKind.Empty:
                return NoOps;
            case StateKind.Batch:
                return new PendingTake.Ops(state.BatchOperations!);
            case StateKind.Folding:
                return new PendingTake.Ops(state.Folded!);
            default:
                return ReplaceTake;
        }
    }

    /// <summary>把 ops 折进 folding；溢出后返回 false。</summary>
    private bool Fold(IReadOnlyList<DeltaOp> operations, State folding)
    {
        var folded = folding.Folded!;
        var leaves = folding.Leaves!;
        foreach (var op in operations)
        {
            switch (op)
            {
                case DeltaOp.Replace:
                    folded.Clear();
                    leaves.Clear();
                    break;
                case DeltaOp.Append:
                    if (MergeLeaf(op, folded, leaves)) continue;
                    break;
                case DeltaOp.Set set when IsPrimitive(set.Value):
                    if (MergeLeaf(op, folded, leaves)) continue;
                    break;
                default:
                    if (leaves.Count > 0) leaves.Clear();
                    break;
            }
            folded.Add(op);
            if (folded.Count > MaxPendingOps)
            {
                _state = State.Replace;
                return false;
            }
        }
        return true;
    }

    /// <summary>把叶子 op 合并到同路径上一个待发叶子 op，或把它登记为「即将压入的位置」。合并时返回 true。</summary>
    private static bool MergeLeaf(DeltaOp op, List<DeltaOp> folded, Dictionary<Path, int> leaves)
    {
        var path = op switch
        {
            DeltaOp.Append append => append.Path,
            DeltaOp.Set set => set.Path,
            _ => throw new InvalidOperationException("not a leaf op"),
        };
        if (leaves.TryGetValue(path, out var at))
        {
            var merged = Merge(folded[at], op);
            if (merged is not null)
            {
                folded[at] = merged;
                return true;
            }
        }
        leaves[path] = folded.Count;
        return false;
    }

    /// <summary>同路径上 next 折进 previous，折不进去时返回 null。</summary>
    private static DeltaOp? Merge(DeltaOp previous, DeltaOp next)
    {
        if (next is DeltaOp.Set) return next;
        if (next is not DeltaOp.Append append) return null;
        return previous switch
        {
            DeltaOp.Append prior => new DeltaOp.Append(prior.Path, prior.Text + append.Text),
            // 只有字符串的 s 能吸收追加；tracker 从不向其他原语追加。
            DeltaOp.Set { Value: string text } set => new DeltaOp.Set(set.Path, text + append.Text),
            _ => null,
        };
    }

    private static bool IsPrimitive(object? value) => value
        is null or string or bool
        or sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal;

    private enum StateKind
    {
        Empty,
        Batch,
        Folding,
        Replace,
    }

    /// <summary>
    /// 折叠中间态（ops 与叶子索引）。用可变类而不是 record，因为折叠是逐 op 原地推进的；一旦被
    /// <see cref="Take"/> 交出或溢出，实例就不再被引用。
    /// </summary>
    private sealed class State
    {
        public static readonly State Empty = new(StateKind.Empty);

        public static readonly State Replace = new(StateKind.Replace);

        private State(StateKind kind) => Kind = kind;

        public StateKind Kind { get; private init; }

        public IReadOnlyList<DeltaOp>? BatchOperations { get; private init; }

        public List<DeltaOp>? Folded { get; private init; }

        public Dictionary<Path, int>? Leaves { get; private init; }

        public static State Batch(IReadOnlyList<DeltaOp> operations) =>
            new(StateKind.Batch) { BatchOperations = operations };

        public static State Folding() =>
            new(StateKind.Folding) { Folded = [], Leaves = new Dictionary<Path, int>() };
    }
}
