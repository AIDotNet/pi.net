// ============================================================================
// Incremental, tolerant parsing of JSON text that arrives in pieces, such as streamed tool-call arguments.
// Port of packages/ai/src/utils/streaming-json.ts
// ============================================================================

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Ai.Utils;

/// <summary>
/// 一次 <see cref="StreamingJsonParser.Append"/> 对值所做的变更，供「自己留一份值、按变更打补丁」的写入方使用。
/// 路径的每一项是成员键（<see cref="string"/>）或数组下标（<see cref="int"/>）。对应 TS <c>JsonChange</c>。
/// </summary>
public abstract record JsonChange(IReadOnlyList<object> Path)
{
    /// <summary>某个键、某个数组项或某个已完结的值出现了（新字符串以其首段文本出现一次，容器以空值出现）。</summary>
    public sealed record Set(IReadOnlyList<object> Path, object? Value) : JsonChange(Path);

    /// <summary>该段文本延长了一个尚未结束的字符串。</summary>
    public sealed record Append(IReadOnlyList<object> Path, string Text) : JsonChange(Path);
}

/// <summary>
/// 增量、宽容的 JSON 解析器：<see cref="Append"/> 每段文本只读一次，<see cref="Snapshot"/> 给出「读到目前为止」的值。
///
/// <para>快照是普通 JSON 值，返回之后不再变化：解析器把**仍然打开的容器**（open path）复制进每个新快照，
/// 已完成的容器与字符串则共享引用。值表示为 <c>null</c> / <see cref="bool"/> / <see cref="string"/> /
/// <see cref="double"/> / <c>List&lt;object?&gt;</c> / <c>Dictionary&lt;string, object?&gt;</c>。</para>
///
/// <para>不完整文本的规则与上游一致：未结束的字符串显示已读到的字符（未完成的转义先扣住）；未结束的容器显示已读到
/// 的成员与元素；键或值尚未开始的成员被省略；未结束的字面量在成为 <c>true</c>/<c>false</c>/<c>null</c> 的前缀时
/// 显示其值；未结束的数字在已读数字构成数字时显示（没有数字的指数被忽略）。</para>
///
/// <para>畸形文本按 <c>repairJson()</c> 的宽容方式读：字符串里的裸控制字符保留，非法转义（如 <c>\d</c>）留作字面
/// 文本，缺少逗号与冒号被容忍，位置非法的值与未知单词被丢弃，根值之后的文本被忽略。解析器从不抛出。</para>
///
/// <para>与 <see cref="JsonParse.ParseStreamingJson"/> 在不完整文本上的差异：未结束字符串的尾随空白保留（那边会
/// 裁掉），含裸控制字符或非法转义的字符串在流式过程中可见（那边要到文档完整才出现），<c>NaN</c> 与 <c>Infinity</c>
/// 被丢弃。</para>
///
/// <para>成本：复制 open path 的代价是其成员与元素数量。每次 <see cref="Append"/> 按文本长度加一个常数累积「拷贝
/// 信用」，<see cref="Snapshot"/> 花掉它；open path 比信用更贵时返回上一个快照。因此长期打开一个超大容器（数千元素）
/// 的文档，其预览会略微落后于文本，而任何输入的总体工作量都不会快于文本增长。</para>
/// </summary>
public sealed partial class StreamingJsonParser
{
    /// <summary>变更最多上报到这个深度，使路径构造保持 O(1)。对应 TS <c>MAX_REPORTED_DEPTH</c>。</summary>
    private const int MaxReportedDepth = 32;

    /// <summary>每次追加与每个追加字符的拷贝信用；下限让小型文档在每次快照时都保持新鲜。</summary>
    private const int CreditPerAppend = 256;

    private const int CreditPerCharacter = 8;

    /// <summary>超过这个长度的未结束数字不显示，直到它结束，使快照保持廉价。</summary>
    private const int MaxPendingScalarLength = 64;

    [GeneratedRegex(@"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^(-?(?:0|[1-9]\d*)(?:\.\d+)?)[eE][+-]?$")]
    private static partial Regex NumberWithoutExponentDigitsRegex();

    private static readonly (string Literal, object? Value)[] Literals =
        [("true", true), ("false", false), ("null", null)];

    private static readonly Dictionary<char, string> SimpleEscapes = new()
    {
        ['"'] = "\"",
        ['\\'] = "\\",
        ['/'] = "/",
        ['b'] = "\b",
        ['f'] = "\f",
        ['n'] = "\n",
        ['r'] = "\r",
        ['t'] = "\t",
    };

    private static readonly Slot RootSlot = new Slot.Root();

    private object? _root;
    private bool _hasRoot;
    private bool _done;
    private readonly List<Frame> _stack = [];
    private OpenString? _string;
    private string _scalar = "";
    private Slot? _scalarSlot;
    /// <summary>open path 上 <c>size + 1</c> 之和：一个新鲜快照要复制的量。</summary>
    private int _pathCost;
    private int _credit;
    private bool _dirty;
    private object? _snapshot;
    /// <summary>本次读取的文本所做的变更。</summary>
    private readonly List<OpenChange> _changes = [];

    /// <summary>
    /// 读下一段文本，按顺序返回它所做的变更。代价与 <paramref name="text"/> 长度成线性。
    /// </summary>
    public IReadOnlyList<JsonChange> Append(string text)
    {
        _credit += CreditPerAppend + (CreditPerCharacter * text.Length);
        _changes.Clear();
        if (_string is not null) _string.Change = null;
        var index = 0;
        while (index < text.Length && !_done)
        {
            var open = _string;
            if (open is not null) index = ReadString(open, text, index);
            else if (_scalar.Length != 0) index = ReadScalar(text, index);
            else Structural(text[index++]);
        }

        return Materialize();
    }

    /// <summary>
    /// 读到目前为止的值：值开始之前是 <see langword="null"/> 且 <see cref="HasValue"/> 为 false。返回的普通 JSON 值
    /// 之后不再变化。此后的文本没有改变值、或尚未付清新拷贝的代价时，返回上一个快照。
    /// </summary>
    public object? Snapshot()
    {
        if (!_dirty) return _snapshot;
        var pending = PendingScalar();
        var cost = _pathCost + (pending is null ? 0 : 1);
        if (cost > _credit) return _snapshot;
        _credit -= cost;
        _dirty = false;
        _snapshot = CopyOpenPath(pending);
        return _snapshot;
    }

    /// <summary>从 <paramref name="start"/> 读字符串字符，直到字符串结束或文本用完；返回下一个下标。</summary>
    private int ReadString(OpenString open, string text, int start)
    {
        var runStart = start;
        for (var index = start; index < text.Length; index++)
        {
            var code = text[index];
            if (open.Escape.Length != 0)
            {
                if (ContinueEscape(open, text[index]))
                {
                    runStart = index + 1;
                    continue;
                }

                // 不属于这段转义：下面按普通字符串字符读它。
                runStart = index;
            }

            if (code == '"')
            {
                if (index > runStart) AppendString(open, text[runStart..index]);
                EndString(open);
                return index + 1;
            }

            if (code == '\\')
            {
                if (index > runStart) AppendString(open, text[runStart..index]);
                open.Escape = "\\";
                runStart = index + 1;
            }
        }

        if (text.Length > runStart) AppendString(open, text[runStart..]);
        return text.Length;
    }

    /// <summary>从 <paramref name="start"/> 读数字或字面量字符，直到下一个分隔符；返回该分隔符的下标。</summary>
    private int ReadScalar(string text, int start)
    {
        var end = start;
        while (end < text.Length && !IsDelimiter(text[end])) end++;
        if (end > start)
        {
            _scalar += text[start..end];
            if (_scalarSlot is not null) _dirty = true;
        }

        if (end < text.Length) EndScalar();
        return end;
    }

    /// <summary>处理字符串之外的一个字符。</summary>
    private void Structural(char ch)
    {
        switch (ch)
        {
            case ' ':
            case '\t':
            case '\n':
            case '\r':
                return;
            case '"':
                StartString();
                return;
            case '{':
                OpenContainer(new Dictionary<string, object?>(StringComparer.Ordinal));
                return;
            case '[':
                OpenContainer(new List<object?>());
                return;
            case '}':
            case ']':
                CloseContainer();
                return;
            case ':':
            {
                var frame = Top();
                if (frame is not null && frame.Container is Dictionary<string, object?> && frame.Phase == Phase.Colon)
                {
                    frame.Phase = Phase.Value;
                }

                return;
            }
            case ',':
            {
                var frame = Top();
                if (frame is not null)
                {
                    frame.Phase = frame.Container is List<object?> ? Phase.Value : Phase.Key;
                }

                return;
            }
            default:
                _scalar = ch.ToString();
                _scalarSlot = ValueSlot();
                if (_scalarSlot is not null) _dirty = true;
                return;
        }
    }

    private Frame? Top() => _stack.Count == 0 ? null : _stack[^1];

    /// <summary>现在开始的值放在哪里；该位置不收值时返回 <see langword="null"/>。</summary>
    private Slot? ValueSlot()
    {
        var frame = Top();
        if (frame is null) return _hasRoot ? null : RootSlot;
        if (frame.Container is List<object?> elements) return new Slot.Element(elements.Count);
        // 缺少冒号被容忍：`{"a" 1}` 读作 `{"a": 1}`。
        if (frame.Phase is Phase.Value or Phase.Colon && frame.Key is not null)
        {
            return new Slot.Member(frame.Key);
        }

        return null;
    }

    private void StartString()
    {
        var frame = Top();
        // 缺少逗号被容忍：`{"a": 1 "b": 2}` 会读第二个成员。
        if (frame is not null
            && frame.Container is Dictionary<string, object?>
            && frame.Phase is Phase.Key or Phase.Comma)
        {
            _string = new OpenString { IsKey = true };
            return;
        }

        var slot = ValueSlot();
        var path = ReportedPath(slot);
        _string = new OpenString { IsKey = false, Slot = slot, Path = path };
        Place(slot, "");
        if (path is not null) _string.Change = Report(new OpenChange { IsSet = true, Path = path, Value = "" });
    }

    /// <summary>最内层打开容器里某个值的路径；其内部不上报时返回 <see langword="null"/>。</summary>
    private List<object>? ReportedPath(Slot? slot)
    {
        if (slot is null) return null;
        if (slot is Slot.Root) return [];
        var frame = _stack[^1];
        if (!frame.ReportsInside) return null;
        return [.. frame.Path!, slot is Slot.Member member ? member.Key : ((Slot.Element)slot).Index];
    }

    private OpenChange Report(OpenChange change)
    {
        _changes.Add(change);
        return change;
    }

    /// <summary>用一个字符继续转义；<paramref name="ch"/> 不属于这段转义、须按普通字符读时返回 false。</summary>
    private bool ContinueEscape(OpenString open, char ch)
    {
        if (open.Escape == "\\")
        {
            if (SimpleEscapes.TryGetValue(ch, out var decoded))
            {
                open.Escape = "";
                AppendString(open, decoded);
                return true;
            }

            if (ch == 'u')
            {
                open.Escape = "\\u";
                return true;
            }

            // 非法转义：反斜杠留作文本，该字符按普通字符读，与 repairJson() 一致。
            open.Escape = "";
            AppendString(open, "\\");
            return false;
        }

        if (IsHexDigit(ch))
        {
            open.Escape += ch;
            if (open.Escape.Length < 6) return true;
            var decoded = ((char)int.Parse(open.Escape[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToString();
            open.Escape = "";
            AppendString(open, decoded);
            return true;
        }

        // `\u` 后面没有四个十六进制位：留作文本，该字符按普通字符读，与 repairJson() 一致。
        var literal = open.Escape;
        open.Escape = "";
        AppendString(open, literal);
        return false;
    }

    private void AppendString(OpenString open, string text)
    {
        open.Text += text;
        if (open.IsKey) return;
        Place(open.Slot, open.Text);
        if (open.Path is null) return;
        // 没有别的变更排在它之后时，延长这个字符串最新的那条变更；否则上报一次 append。
        var change = open.Change;
        if (change is not null && ReferenceEquals(_changes[^1], change))
        {
            if (change.IsSet) change.Value = (string)change.Value! + text;
            else change.Text += text;
            return;
        }

        open.Change = Report(new OpenChange { IsSet = false, Path = open.Path, Text = text });
    }

    private void EndString(OpenString open)
    {
        _string = null;
        var frame = Top();
        if (open.IsKey)
        {
            if (frame is null) return;
            frame.Key = open.Text;
            frame.Phase = Phase.Colon;
            return;
        }

        ValueEnded(frame);
    }

    private void EndScalar()
    {
        var token = _scalar;
        var slot = _scalarSlot;
        _scalar = "";
        _scalarSlot = null;
        if (TryScalarValue(token, out var value))
        {
            Place(slot, value);
            var path = ReportedPath(slot);
            if (path is not null) Report(new OpenChange { IsSet = true, Path = path, Value = value });
        }

        ValueEnded(Top());
    }

    /// <summary>文本末尾未结束的标量所显示的值（若不显示则为 <see langword="null"/>）。</summary>
    private (Slot Slot, object? Value)? PendingScalar()
    {
        var slot = _scalarSlot;
        var token = _scalar;
        if (slot is null || token.Length == 0 || token.Length > MaxPendingScalarLength) return null;
        return TryPendingScalarValue(token, out var value) ? (slot, value) : null;
    }

    private void OpenContainer(object container)
    {
        var slot = ValueSlot();
        var path = ReportedPath(slot);
        Place(slot, container);
        _stack.Add(new Frame
        {
            Container = container,
            Slot = slot,
            Path = path,
            ReportsInside = path is not null && path.Count < MaxReportedDepth,
            Size = 0,
            Key = null,
            Phase = container is List<object?> ? Phase.Value : Phase.Key,
        });
        _pathCost += 1;
        _dirty = true;
        // 新容器：工作副本会继续变化，而上报的变更不会。
        if (path is not null)
        {
            Report(new OpenChange
            {
                IsSet = true,
                Path = path,
                Value = container is List<object?> ? new List<object?>() : new Dictionary<string, object?>(),
            });
        }
    }

    private void CloseContainer()
    {
        if (_stack.Count == 0) return; // 任何容器之外的野闭合符被忽略
        var frame = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        _pathCost -= frame.Size + 1;
        _dirty = true;
        // 太深而不上报内部：整个值在关闭时（此刻已定稿）上报一次。
        if (frame.Path is not null && !frame.ReportsInside)
        {
            Report(new OpenChange { IsSet = true, Path = frame.Path, Value = frame.Container });
        }

        ValueEnded(Top());
    }

    /// <summary><paramref name="frame"/>（没有 frame 时是根值）里的一个值结束了。</summary>
    private void ValueEnded(Frame? frame)
    {
        if (frame is null)
        {
            // 根值之后的文本被忽略，但只在根值已经存在之后。
            if (_hasRoot) _done = true;
            return;
        }

        frame.Phase = Phase.Comma;
    }

    /// <summary>把值放到 open path 上的 <paramref name="slot"/>；位置非法的值什么都不做。</summary>
    private void Place(Slot? slot, object? value)
    {
        if (slot is null) return;
        _dirty = true;
        if (slot is Slot.Root)
        {
            _root = value;
            _hasRoot = true;
            return;
        }

        var frame = _stack[^1];
        if (slot is Slot.Element element)
        {
            var elements = (List<object?>)frame.Container;
            if (element.Index == elements.Count)
            {
                frame.Size++;
                _pathCost++;
                elements.Add(value);
            }
            else
            {
                elements[element.Index] = value;
            }

            return;
        }

        var members = (Dictionary<string, object?>)frame.Container;
        var key = ((Slot.Member)slot).Key;
        if (!members.ContainsKey(key))
        {
            frame.Size++;
            _pathCost++;
        }

        members[key] = value;
    }

    /// <summary>新鲜快照：从最内层起复制 open path 上的每个容器，各含其打开子节点的副本。</summary>
    private object? CopyOpenPath((Slot Slot, object? Value)? pending)
    {
        if (_stack.Count == 0)
        {
            return pending is { } rootPending && rootPending.Slot is Slot.Root ? rootPending.Value : _root;
        }

        object? child = null;
        var hasChild = false;
        Slot? childSlot = null;
        for (var depth = _stack.Count - 1; depth >= 0; depth--)
        {
            var frame = _stack[depth];
            object copy = frame.Container is List<object?> list
                ? new List<object?>(list)
                : new Dictionary<string, object?>((Dictionary<string, object?>)frame.Container, StringComparer.Ordinal);
            if (depth == _stack.Count - 1 && pending is { } pendingScalar)
            {
                SetSlot(copy, pendingScalar.Slot, pendingScalar.Value);
            }

            if (hasChild && childSlot is not null) SetSlot(copy, childSlot, child);
            child = copy;
            hasChild = true;
            childSlot = frame.Slot;
        }

        return childSlot is Slot.Root ? child : _root;
    }

    private IReadOnlyList<JsonChange> Materialize()
    {
        var changes = new List<JsonChange>(_changes.Count);
        foreach (var change in _changes)
        {
            changes.Add(change.IsSet
                ? new JsonChange.Set(change.Path, change.Value)
                : new JsonChange.Append(change.Path, change.Text));
        }

        return changes;
    }

    private static void SetSlot(object container, Slot slot, object? value)
    {
        if (slot is Slot.Element element)
        {
            var elements = (List<object?>)container;
            // 待发的元素可能正好在末尾之后：数组赋值在 length 处是追加。
            if (element.Index == elements.Count) elements.Add(value);
            else elements[element.Index] = value;
        }
        else if (slot is Slot.Member member)
        {
            ((Dictionary<string, object?>)container)[member.Key] = value;
        }
    }

    private static bool IsDelimiter(char ch) =>
        ch is ' ' or '\t' or '\n' or '\r' or ',' or ':' or '{' or '}' or '[' or ']' or '"';

    private static bool IsHexDigit(char ch) =>
        ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>完整数字或字面量的值；其他一律返回 false（于是被丢弃）。</summary>
    private static bool TryScalarValue(string token, out object? value)
    {
        foreach (var (literal, literalValue) in Literals)
        {
            if (token != literal) continue;
            value = literalValue;
            return true;
        }

        if (NumberRegex().IsMatch(token))
        {
            value = Number(token);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>未结束的数字或字面量所显示的值。</summary>
    private static bool TryPendingScalarValue(string token, out object? value)
    {
        foreach (var (literal, literalValue) in Literals)
        {
            if (!literal.StartsWith(token, StringComparison.Ordinal)) continue;
            value = literalValue;
            return true;
        }

        if (NumberRegex().IsMatch(token))
        {
            value = Number(token);
            return true;
        }

        var mantissa = NumberWithoutExponentDigitsRegex().Match(token);
        if (mantissa.Success)
        {
            value = Number(mantissa.Groups[1].Value);
            return true;
        }

        value = null;
        return false;
    }

    private static double Number(string token) => double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>JSON 值在槽位里；位置非法的值被读到但丢弃。</summary>
    private abstract record Slot
    {
        private Slot()
        {
        }

        public sealed record Root : Slot;

        public sealed record Member(string Key) : Slot;

        public sealed record Element(int Index) : Slot;
    }

    private enum Phase
    {
        Key,
        Colon,
        Value,
        Comma,
    }

    /// <summary>一个打开的容器。它的 <see cref="Container"/> 在打开期间绝不外发；快照拿到的是副本。</summary>
    private sealed class Frame
    {
        public required object Container { get; init; }

        public Slot? Slot { get; init; }

        /// <summary>容器所在的位置；其变更要上报时有值。</summary>
        public List<object>? Path { get; init; }

        /// <summary>其内部变更是否上报；不上报时整个值在关闭时上报一次。</summary>
        public bool ReportsInside { get; init; }

        /// <summary>成员或元素数量；复制该容器的代价。</summary>
        public int Size { get; set; }

        /// <summary>对象：最后读到的键，成员的值放这里。</summary>
        public string? Key { get; set; }

        public Phase Phase { get; set; }
    }

    /// <summary>一个未结束的字符串。</summary>
    private sealed class OpenString
    {
        public required bool IsKey { get; init; }

        public Slot? Slot { get; init; }

        public string Text { get; set; } = "";

        /// <summary>已读到的转义："" 表示没有，否则是一个反斜杠加最多五个字符。</summary>
        public string Escape { get; set; } = "";

        /// <summary>值字符串所在的位置；其变更要上报时有值。</summary>
        public List<object>? Path { get; init; }

        /// <summary>本段最新的变更仍是这条字符串在延长时，指向它。</summary>
        public OpenChange? Change { get; set; }
    }

    /// <summary>仍被正在读的这段文本延长的变更。</summary>
    private sealed class OpenChange
    {
        public required bool IsSet { get; init; }

        public required IReadOnlyList<object> Path { get; init; }

        public object? Value { get; set; }

        public string Text { get; set; } = "";
    }
}

/// <summary>
/// 一次流式工具调用的参数：原始文本（调用结束时做一次完整解析）与中间的增量解析器。提供方每次调用各自持有一个，
/// 放在自己的流状态里而不是内容块上，因此原始文本不会进入 partial 消息或转录。对应 TS <c>ToolCallArgumentsStream</c>。
/// </summary>
public sealed class ToolCallArgumentsStream
{
    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>();

    private string _text = "";
    private StreamingJsonParser _parser = new();
    private object? _snapshot;
    private bool _hasSnapshot;
    private IReadOnlyDictionary<string, object?> _arguments = NoArguments;

    /// <summary>到目前为止收到的原始参数文本。</summary>
    public string Text => _text;

    /// <summary>追加一段参数文本；返回 partial 参数，变化时是新对象。</summary>
    public IReadOnlyDictionary<string, object?> Append(string delta)
    {
        _text += delta;
        _parser.Append(delta);
        return Partial();
    }

    /// <summary>
    /// 接受提供方给出的完整参数文本。它延续已有文本时，把缺的那一段作为 <c>Delta</c> 返回；否则以
    /// <paramref name="text"/> 重新开始。两种情形都返回该文本的 partial 参数。
    /// </summary>
    public (string? Delta, IReadOnlyDictionary<string, object?> Arguments) Replace(string text)
    {
        if (text.StartsWith(_text, StringComparison.Ordinal))
        {
            var delta = text[_text.Length..];
            return (delta, delta.Length == 0 ? Partial() : Append(delta));
        }

        _text = "";
        _parser = new StreamingJsonParser();
        return (null, Append(text));
    }

    /// <summary>最终参数：对完整文本做一次完整解析，畸形参数会被报告出来。</summary>
    public (JsonObject Arguments, string? ArgumentsError) Finish() => JsonParse.ParseToolCallArguments(_text);

    private IReadOnlyDictionary<string, object?> Partial()
    {
        var snapshot = _parser.Snapshot();
        if (_hasSnapshot && ReferenceEquals(snapshot, _snapshot)) return _arguments;
        _snapshot = snapshot;
        _hasSnapshot = true;
        // 工具参数是对象；其他形态的 partial 值显示为「还没有参数」。
        _arguments = snapshot as IReadOnlyDictionary<string, object?> ?? NoArguments;
        return _arguments;
    }
}
