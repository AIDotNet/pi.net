using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Port of <c>packages/ai/test/streaming-json.test.ts</c>（上游 <c>#9265</c> 的增量解析器）。期望值全部由上游 TS
/// 实跑取得，不是手算。上游那套「差分模糊 + 分配量测量」的用例依赖 JS 运行时（参考实现 <c>parseStreamingJson</c>、
/// <c>process.memoryUsage</c>、JSONTestSuite 语料），这里换成等价的确定性用例：分片不变性、逐段快照、变更序列。
/// </summary>
public class StreamingJsonTests
{
    // ─── 快照：分片不变性 ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void CompleteDocumentsReadLikeJsonAtEveryChunkSize(int chunk)
    {
        AssertJson(Dict(("a", 1.0)), SnapshotInChunks("""{"a":1}""", chunk));
        AssertJson(Dict(("a", "xéy")), SnapshotInChunks("""{"a":"x\u00e9y"}""", chunk));
        AssertJson(
            Dict(("a", L(1.0, 2.0, Dict(("b", true))))),
            SnapshotInChunks("""{"a":[1,2,{"b":true}]}""", chunk));
        AssertJson(
            Dict(("a", "x\ny \\d \\u12")),
            SnapshotInChunks("{\"a\":\"x\ny \\d \\u12\"}", chunk));
        AssertJson(
            Dict(("path", "a.tex"), ("content", "\\usepackage{x}"), ("mode", "w")),
            SnapshotInChunks("""{"path":"a.tex","content":"\usepackage{x}","mode":"w"}""", chunk));
    }

    [Fact]
    public void SnapshotNeverChangesOnceReturned()
    {
        var parser = new StreamingJsonParser();
        parser.Append("""{"a":1,"b":[2""");
        var first = parser.Snapshot();
        parser.Append(",3]}");
        var second = parser.Snapshot();
        // 第一个快照保持原样：后续文本不会改它。
        AssertJson(Dict(("a", 1.0), ("b", L(2.0))), first);
        AssertJson(Dict(("a", 1.0), ("b", L(2.0, 3.0))), second);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData("""{"a":[1,2,3],"b":{"c":true,"d":null}}""")]
    [InlineData("""{"s":"a\\b\"c\u00e9\n\t"}""")]
    [InlineData("[[],{},[1,[2,[3]]]]")]
    [InlineData("""{"n":-0.5,"e":1e21,"z":-5e-8}""")]
    [InlineData("""{"u":"\ud83d\ude00","e":"😀"}""")]
    [InlineData("""  {"a" : 1 }  """)]
    public void CompleteDocumentsMatchTheSystemParserAtEveryChunkSize(string text)
    {
        var expected = FromNode(JsonNode.Parse(text));
        for (var chunk = 1; chunk <= 3; chunk++)
        {
            AssertJson(expected, SnapshotInChunks(text, chunk));
        }
    }

    // ─── 快照：字符串与转义 ─────────────────────────────────────────────────

    [Fact]
    public void HoldsAnEscapeBackUntilItIsComplete()
    {
        // 转义跨段：前半段只显示已读到的字符（上游 `#10868` 的形状）。
        var parser = new StreamingJsonParser();
        parser.Append("""{"a":"x\u00""");
        AssertJson(Dict(("a", "x")), parser.Snapshot());
        parser.Append("""e9y"}""");
        AssertJson(Dict(("a", "xéy")), parser.Snapshot());
    }

    [Fact]
    public void JoinsSurrogatePairsSplitAcrossPieces()
    {
        foreach (var text in new[] { """{"a":"\ud83d\ude00!"}""", "{\"a\":\"😀!\"}" })
        {
            for (var split = 0; split <= text.Length; split++)
            {
                var parser = new StreamingJsonParser();
                parser.Append(text[..split]);
                parser.Snapshot();
                parser.Append(text[split..]);
                AssertJson(Dict(("a", "😀!")), parser.Snapshot());
            }
        }

        // 低位还没到时，高位单独显示，与 JSON.parse() 对转义的结果一致。
        AssertJson(Dict(("a", "\ud83d")), SnapshotOf("""{"a":"\ud83d"""));
    }

    [Fact]
    public void KeepsTrailingWhitespaceOfAnUnterminatedString()
    {
        AssertJson(Dict(("code", "x\t\t  ")), SnapshotOf("{\"code\":\"x\t\t  "));
    }

    [Fact]
    public void KeepsRawControlCharactersAndInvalidEscapesVisible()
    {
        AssertJson(Dict(("a", "x\ny \\d \\u12")), SnapshotOf("{\"a\":\"x\ny \\d \\u12\"}"));
    }

    [Fact]
    public void KeepsInvalidUnicodeEscapesLiteralWithoutDroppingFields()
    {
        AssertJson(
            Dict(("path", "a.tex"), ("content", "\\usepackage{x}"), ("mode", "w")),
            SnapshotInChunks("""{"path":"a.tex","content":"\usepackage{x}","mode":"w"}""", 1));
        AssertJson(Dict(("text", "café \\url")), SnapshotInChunks("""{"text":"caf\u00e9 \url"}""", 3));
        AssertJson(Dict(("text", "ab")), SnapshotOf("""{"text":"ab\u00"""));
    }

    [Fact]
    public void KeepsTheLastValueOfADuplicateKeyInTheFirstKeysPosition()
    {
        var value = SnapshotInChunks("""{"a":1,"b":2,"a":"three"}""", 1);
        AssertJson(Dict(("a", "three"), ("b", 2.0)), value);
        // JSON.parse() 保留首次出现的位置：键序是 a 再 b。
        Assert.Equal(["a", "b"], ((IReadOnlyDictionary<string, object?>)value!).Keys.ToArray());
    }

    // ─── 快照：不完整与畸形文本 ─────────────────────────────────────────────

    [Theory]
    [InlineData("""{"a":""", "{}")]
    [InlineData("""{"a" 1}""", """{"a":1}""")] // 缺少冒号被容忍
    [InlineData("""{"a":1 "b":2}""", """{"a":1,"b":2}""")] // 缺少逗号被容忍
    [InlineData("[1,,2]", "[1,2]")] // 位置非法的元素被丢弃
    [InlineData("""{"a":}""", "{}")] // 值还没开始：成员省略
    [InlineData("[1 2]", "[1,2]")]
    [InlineData("""{"a":1,}""", """{"a":1}""")]
    public void ToleratesMissingPunctuationAndDropsValuesInInvalidPositions(string text, string expected)
    {
        AssertJson(FromNode(JsonNode.Parse(expected)), SnapshotOf(text));
    }

    [Fact]
    public void ShowsAnUnterminatedScalarWhenItIsAlreadyAValue()
    {
        AssertJson(-1.25, SnapshotOf("-1.25e+"));
        AssertJson(-1.25, SnapshotOf("-1.25e"));
        AssertJson(42.0, SnapshotOf("42"));
        AssertJson(true, SnapshotOf("tru"));
    }

    [Fact]
    public void NeverThrowsAtAnyChunkSizeOrSplit()
    {
        const string text = "{\"a\":[1,{\"b\":\"x\\y\"}],\"c\":nul";
        for (var chunk = 1; chunk <= text.Length; chunk++)
        {
            var parser = new StreamingJsonParser();
            for (var index = 0; index < text.Length; index += chunk)
            {
                parser.Append(text[index..Math.Min(index + chunk, text.Length)]);
                // 任何前缀都不抛，且快照是普通 JSON 值（可再次序列化）。
                _ = JsonNode.Parse(ToJson(parser.Snapshot()));
            }
        }
    }

    // ─── 变更序列 ───────────────────────────────────────────────────────────

    [Fact]
    public void ReportsTheChangesAPieceMadeInOrder()
    {
        AssertChanges("""{"a":1}""", new Change([], Dict()), new Change(["a"], 1.0));
        // 数字未结束时不报：只有开容器那一条。
        AssertChanges("""{"a":1""", new Change([], Dict()));
        AssertChanges("""{"a":"hello""", new Change([], Dict()), new Change(["a"], "hello"));
        AssertChanges(
            """{"a":[1,2]""",
            new Change([], Dict()),
            new Change(["a"], new List<object?>()),
            new Change(["a", 0], 1.0),
            new Change(["a", 1], 2.0));
        AssertChanges("""{"a":{"b":""", new Change([], Dict()), new Change(["a"], Dict()));
        AssertChanges("""{"a":1} trailing""", new Change([], Dict()), new Change(["a"], 1.0));
        AssertChanges(
            "[1,\"x",
            new Change([], new List<object?>()),
            new Change([0], 1.0),
            new Change([1], "x"));
    }

    [Fact]
    public void ReportsAContainerNestedDeeperThan32LevelsOnceWhole()
    {
        var text = string.Concat(Enumerable.Repeat("[", 34)) + "1" + string.Concat(Enumerable.Repeat("]", 34));
        var parser = new StreamingJsonParser();
        var changes = parser.Append(text);

        // 根 + 32 层各一条，第 33 层在关闭时整体上报一次（路径到 32 为止，故最后两条同为 32）。
        Assert.Equal(34, changes.Count);
        Assert.All(changes, change => Assert.IsType<JsonChange.Set>(change));
        Assert.Equal(32, changes[32].Path.Count);
        Assert.Equal(32, changes[33].Path.Count);
        Assert.IsType<List<object?>>(((JsonChange.Set)changes[33]).Value);
    }

    // ─── ToolCallArgumentsStream ────────────────────────────────────────────

    [Fact]
    public void ShowsPartialArgumentsAsAnObject()
    {
        var stream = new ToolCallArgumentsStream();
        Assert.Empty(stream.Append("""{"a":"""));
        // 数字未结束也算「已经是值」：`{"a":1` 的参数是 {a:1}（上游一致）。
        Assert.Equal(1.0, stream.Append("1")["a"]);
        Assert.Equal(1.0, stream.Append("}")["a"]);
        Assert.Equal("""{"a":1}""", stream.Text);
    }

    [Fact]
    public void ShowsNonObjectRootsAsNoArgumentsYet()
    {
        var stream = new ToolCallArgumentsStream();
        Assert.Empty(stream.Append("[1"));
        Assert.Empty(stream.Append(",2]"));
    }

    [Fact]
    public void ReplacesTheTextWithTheProvidersCompleteText()
    {
        var stream = new ToolCallArgumentsStream();
        stream.Append("""{"a":1""");
        // 提供方的完整文本延续已读文本时，只补缺的那一段。
        var extended = stream.Replace("""{"a":12}""");
        Assert.Equal("2}", extended.Delta);
        Assert.Equal(12.0, extended.Arguments["a"]);

        var restarted = stream.Replace("""{"b":2}""");
        Assert.Null(restarted.Delta);
        Assert.Equal(2.0, restarted.Arguments["b"]);
        Assert.Equal("""{"b":2}""", stream.Text);
    }

    [Fact]
    public void FinishesWithOneFullParse()
    {
        var stream = new ToolCallArgumentsStream();
        stream.Append("""{"a":""");
        stream.Append("1}");
        var (arguments, error) = stream.Finish();
        Assert.Null(error);
        Assert.Equal(1, arguments["a"]!.GetValue<int>());
    }

    // ─── 夹具 ───────────────────────────────────────────────────────────────

    private sealed record Change(object[] Path, object? Value);

    private static Dictionary<string, object?> Dict(params (string Key, object? Value)[] members)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in members) result[key] = value;
        return result;
    }

    private static List<object?> L(params object?[] items) => [.. items];

    private static object? SnapshotOf(string text)
    {
        var parser = new StreamingJsonParser();
        parser.Append(text);
        return parser.Snapshot();
    }

    private static object? SnapshotInChunks(string text, int chunk)
    {
        var parser = new StreamingJsonParser();
        for (var index = 0; index < text.Length; index += chunk)
        {
            parser.Append(text[index..Math.Min(index + chunk, text.Length)]);
        }

        return parser.Snapshot();
    }

    /// <summary>把 <see cref="JsonNode"/> 期望值转成解析器那套 <c>object?</c> 值。</summary>
    private static object? FromNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => FromNode(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(FromNode).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>(),
            _ => null,
        },
        _ => null,
    };

    private static string ToJson(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        List<object?> items => "[" + string.Join(",", items.Select(ToJson)) + "]",
        IReadOnlyDictionary<string, object?> members => "{" + string.Join(
            ",", members.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + ToJson(pair.Value))) + "}",
        _ => throw new InvalidOperationException($"not a JSON value: {value.GetType()}"),
    };

    private static void AssertJson(object? expected, object? actual) => AssertJsonAt("$", expected, actual);

    private static void AssertJsonAt(string path, object? expected, object? actual)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{path}: expected null, got {Describe(actual)}");
                return;
            case bool flag:
                Assert.True(actual is bool actualFlag && actualFlag == flag,
                    $"{path}: expected {flag}, got {Describe(actual)}");
                return;
            case string text:
                Assert.True(actual is string actualText && actualText == text,
                    $"{path}: expected {text}, got {Describe(actual)}");
                return;
            case double number:
                Assert.True(actual is double actualNumber && actualNumber.Equals(number),
                    $"{path}: expected {number}, got {Describe(actual)}");
                return;
            case List<object?> items:
            {
                if (actual is not List<object?> actualItems)
                {
                    Assert.Fail($"{path}: expected array, got {Describe(actual)}");
                    return;
                }

                Assert.True(items.Count == actualItems.Count,
                    $"{path}: expected {items.Count} items, got {actualItems.Count}");
                for (var index = 0; index < items.Count; index++)
                {
                    AssertJsonAt($"{path}[{index}]", items[index], actualItems[index]);
                }

                return;
            }
            case IReadOnlyDictionary<string, object?> members:
            {
                if (actual is not IReadOnlyDictionary<string, object?> actualMembers)
                {
                    Assert.Fail($"{path}: expected object, got {Describe(actual)}");
                    return;
                }

                Assert.Equal([.. members.Keys], actualMembers.Keys.ToArray());
                foreach (var (key, value) in members) AssertJsonAt($"{path}.{key}", value, actualMembers[key]);
                return;
            }
            default:
                throw new InvalidOperationException($"not a JSON value: {expected.GetType()}");
        }
    }

    private static string Describe(object? value) => value is null ? "null" : $"{value} ({value.GetType().Name})";

    private static void AssertChanges(string text, params Change[] expected)
    {
        var parser = new StreamingJsonParser();
        var changes = parser.Append(text);
        Assert.Equal(expected.Length, changes.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var set = Assert.IsType<JsonChange.Set>(changes[index]);
            Assert.Equal(expected[index].Path, set.Path.ToArray());
            AssertJsonAt($"change[{index}]", expected[index].Value, set.Value);
        }
    }
}
