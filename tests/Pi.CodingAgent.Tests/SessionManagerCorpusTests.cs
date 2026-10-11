using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 4e-2c-3 differential replay for the `SessionManager` class.
//
// tools/gen-coding-agent-session-manager-corpus.mjs drives the original TypeScript class in Node
// through an op interpreter and records the tokenized output of every op. This suite re-runs the
// same op list against the C# port, normalizes both sides with the same rules (uuid/8-hex ids →
// "#N", ISO timestamps → "<tsN>", file-name timestamps → "<file-ts>", the temp root → "<TMP>"),
// and asserts byte-identical results.
// ============================================================================

public sealed class SessionManagerCorpusCollection;

[CollectionDefinition(nameof(SessionManagerCorpusCollection), DisableParallelization = true)]
public sealed class SequentialSessionManagerCorpus;

/// <summary>One recorded op result. Corresponds to TS <c>{ op, out }</c>.</summary>
public sealed record RecordedOp(string Op, JsonNode? Out);

[Collection(nameof(SessionManagerCorpusCollection))]
public sealed class SessionManagerCorpusTests
{
    private static readonly Lazy<JsonObject> CorpusLazy = new(() =>
        JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "session-manager-corpus.json")))!
            .AsObject());

    private static JsonObject Corpus => CorpusLazy.Value;

    // ── text-level token patterns (must mirror the generator's) ────────────────────────────────────
    private static readonly Regex UuidPattern = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled);
    private static readonly Regex ShortIdPattern = new(
        "(?<![0-9a-f-])[0-9a-f]{8}(?![0-9a-f-])", RegexOptions.Compiled);
    private static readonly Regex OffsetPattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?\+00:00", RegexOptions.Compiled);
    private static readonly Regex IsoPattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z", RegexOptions.Compiled);
    private static readonly Regex FileTsPattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-\d{3}Z", RegexOptions.Compiled);

    /// <summary>
    /// Serialize helper: session-line options (camelCase, message role polymorphism, toolResult
    /// string-content tolerance) with relaxed escaping for TS <c>JSON.stringify</c> parity.
    /// </summary>
    private static readonly JsonSerializerOptions NodeOptions =
        new(SessionEntries.JsonOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IEnumerable<object[]> ScenarioNames() =>
        ((JsonArray)Corpus["scenarios"]!).Select(node => new object[]
        {
            (string)((JsonObject)node!)["name"]!,
        });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Replay(string scenarioName)
    {
        var scenario = (JsonObject)((JsonArray)Corpus["scenarios"]!)
            .First(node => (string)((JsonObject)node!)["name"]! == scenarioName)!;
        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"pi-session-manager-corpus-{Guid.NewGuid():N}");
        var previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        var previousCwd = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(tempRoot);
        try
        {
            // The TS generator chdirs into each scenario temp root; the C# port mirrors that so
            // process.cwd()/GetCurrentDirectory() fallbacks (Open without a header) match.
            Directory.SetCurrentDirectory(tempRoot);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(tempRoot, "agent"));
            var actual = await RunOpsAsync((JsonArray)scenario["ops"]!, tempRoot);
            var expected = (JsonArray)scenario["results"]!;

            for (var index = 0; index < Math.Max(actual.Count, expected.Count); index++)
            {
                var expectedOp = index < expected.Count ? (string)((JsonObject)expected[index]!)["op"]! : "<none>";
                var actualOp = index < actual.Count ? actual[index].Op : "<none>";
                Assert.True(
                    expectedOp == actualOp,
                    $"[{scenarioName}] op #{index}: expected {expectedOp} but ran {actualOp}");

                var expectedText = Tokenize(
                    Serialize(Normalize(((JsonObject)expected[index]!)["out"], tempRoot)), tempRoot);
                var actualText = Tokenize(Serialize(Normalize(actual[index].Out, tempRoot)), tempRoot);
                Assert.True(
                    expectedText == actualText,
                    $"[{scenarioName}] op #{index} ({expectedOp}) output mismatch:\n"
                    + $"  expected: {expectedText}\n"
                    + $"  actual:   {actualText}");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            if (previousAgentDir is not null)
            {
                Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previousAgentDir);
            }
            else
            {
                Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            }

            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // op interpreter
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<List<RecordedOp>> RunOpsAsync(JsonArray ops, string tempRoot)
    {
        SessionManager? manager = null;
        var ids = new List<string>();
        var paths = new List<string>();
        var results = new List<RecordedOp>();

        JsonNode? Deref(JsonNode? value)
        {
            if (value is JsonValue { } v && v.TryGetValue<string>(out var s))
            {
                var idMatch = Regex.Match(s, @"^\$(\d+)$");
                if (idMatch.Success)
                {
                    var index = int.Parse(idMatch.Groups[1].Value) - 1;
                    return index < ids.Count ? JsonValue.Create(ids[index]) : value;
                }

                var pathMatch = Regex.Match(s, @"^\$p(\d+)$");
                if (pathMatch.Success)
                {
                    var index = int.Parse(pathMatch.Groups[1].Value) - 1;
                    return index < paths.Count ? JsonValue.Create(paths[index]) : value;
                }

                if (s == "$root")
                {
                    return JsonValue.Create(tempRoot);
                }

                if (s.StartsWith("$root/", StringComparison.Ordinal))
                {
                    return JsonValue.Create(Path.Join(tempRoot, s[6..]));
                }
            }

            return value;
        }

        JsonObject DerefArgs(JsonObject args)
        {
            var result = new JsonObject();
            foreach (var (key, value) in args)
            {
                if (value is JsonObject obj)
                {
                    result[key] = DerefArgs(obj);
                }
                else if (value is JsonArray array)
                {
                    var newArray = new JsonArray();
                    foreach (var item in array)
                    {
                        if (item is JsonObject itemObj)
                        {
                            newArray.Add(DerefArgs(itemObj));
                        }
                        else
                        {
                            newArray.Add(Deref(item)?.DeepClone());
                        }
                    }

                    result[key] = newArray;
                }
                else
                {
                    result[key] = Deref(value)?.DeepClone();
                }
            }

            return result;
        }

        // "/rel" joins the scenario temp root; absolute paths (from "$pN" refs) pass through.
        string Abs(string value) =>
            value.StartsWith('/') ? Path.Join(tempRoot, value[1..]) : value;

        string? OptDir(JsonNode? node) =>
            node is not JsonValue { } value ? null : Abs(value.GetValue<string>());

        string? OptString(JsonNode? node) => (string?)node;

        void Record(string op, JsonNode? value) => results.Add(new RecordedOp(op, value));

        void RecordId(string op, JsonNode? value)
        {
            if (value is JsonValue { } v && v.TryGetValue<string>(out var id))
            {
                ids.Add(id);
            }
            else if (value?["id"] is JsonValue { } idValue && idValue.TryGetValue<string>(out var entryId))
            {
                ids.Add(entryId);
            }

            Record(op, value);
        }

        void RecordPath(string op, JsonNode? value)
        {
            if (value is JsonValue { } v && v.TryGetValue<string>(out var path))
            {
                paths.Add(path);
            }

            Record(op, value);
        }

        JsonNode? SerializeToNode(object? value) =>
            value is null
                ? null
                // FileEntry 按**声明类型**序列化才会写出 `type` discriminator（运行时类型
                // 不带 JsonPolymorphic 元数据）；其余标量/容器按运行时类型。
                : JsonSerializer.SerializeToNode(
                    value, value is FileEntry ? typeof(FileEntry) : value.GetType(), NodeOptions);

        ChatMessage Message(JsonNode node) =>
            node.Deserialize<ChatMessage>(SessionEntries.JsonOptions)!;

        Usage? OptUsage(JsonNode? node) =>
            node is null ? null : node.Deserialize<Usage>(SessionEntries.JsonOptions);

        NewSessionOptions? OptOptions(JsonNode? node) =>
            node is null ? null : node.Deserialize<NewSessionOptions>(SessionEntries.JsonOptions);

        ContextEditReplacement? OptReplacement(JsonNode? node) =>
            node is null
                ? null
                : node.Deserialize<ContextEditReplacement>(SessionEntries.JsonOptions);

        static string[] ParseLines(string content)
        {
            var lines = content.Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0)
            {
                lines = lines[..^1];
            }

            return lines;
        }

        JsonNode FileLines(string content)
        {
            var array = new JsonArray();
            foreach (var line in ParseLines(content))
            {
                if (line.Trim().Length == 0)
                {
                    array.Add("");
                    continue;
                }

                try
                {
                    array.Add(JsonNode.Parse(line));
                }
                catch (JsonException)
                {
                    array.Add(line); // non-JSON lines (corrupt-file scenarios) are recorded verbatim
                }
            }

            return array;
        }

        async Task RunOneAsync(string op, JsonObject a, JsonObject? run = null)
        {
            switch (op)
            {
                case "writeFile":
                {
                    var path = Abs((string)a["path"]!);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // "$root" sits inside an already-serialized JSON line, so the temp root's
                    // backslashes must be JSON-escaped or the line becomes invalid JSON.
                    var content = (string)a["content"]!;
                    content = content.Replace("$root", tempRoot.Replace("\\", "\\\\"));
                    File.WriteAllText(path, content, new UTF8Encoding(false));
                    if (a["mtimeMs"] is { } mtime)
                    {
                        var when = DateTimeOffset.FromUnixTimeMilliseconds((long)mtime);
                        File.SetLastWriteTimeUtc(path, when.UtcDateTime);
                        File.SetLastAccessTimeUtc(path, when.UtcDateTime);
                    }

                    Record(op, null);
                    break;
                }
                case "mkdir":
                    Directory.CreateDirectory(Abs((string)a["path"]!));
                    Record(op, null);
                    break;
                case "create":
                    manager = SessionManager.Create(
                        Abs((string)a["cwd"]!), OptDir(a["sessionDir"]), OptOptions(a["options"]));
                    Record(op, null);
                    break;
                case "open":
                    manager = SessionManager.Open(
                        Abs((string)a["path"]!), OptDir(a["sessionDir"]), OptString(a["cwdOverride"]));
                    Record(op, null);
                    break;
                case "continueRecent":
                    manager = SessionManager.ContinueRecent(
                        Abs((string)a["cwd"]!), OptDir(a["sessionDir"]));
                    Record(op, null);
                    break;
                case "inMemory":
                    manager = SessionManager.InMemory(
                        Abs((string)a["cwd"]!),
                        OptOptions(a["options"]),
                        a["entries"] is null
                            ? null
                            : a["entries"]!.Deserialize<FileEntry[]>(SessionEntries.JsonOptions));
                    Record(op, null);
                    break;
                case "forkFrom":
                    manager = SessionManager.ForkFrom(
                        Abs((string)a["sourcePath"]!),
                        Abs((string)a["targetCwd"]!),
                        OptDir(a["sessionDir"]),
                        OptOptions(a["options"]));
                    Record(op, null);
                    break;
                case "newSession":
                    RecordPath(op, SerializeToNode(manager!.NewSession(OptOptions(a["options"]))));
                    break;
                case "setSessionFile":
                    manager!.SetSessionFile(Abs((string)a["path"]!));
                    Record(op, null);
                    break;
                case "appendMessage":
                    RecordId(op, SerializeToNode(manager!.AppendMessage(Message(a["message"]!))));
                    break;
                case "appendThinkingLevelChange":
                    RecordId(op, SerializeToNode(
                        manager!.AppendThinkingLevelChange((string)a["thinkingLevel"]!)));
                    break;
                case "appendModelChange":
                    RecordId(op, SerializeToNode(
                        manager!.AppendModelChange((string)a["provider"]!, (string)a["modelId"]!)));
                    break;
                case "appendUsage":
                    RecordId(op, SerializeToNode(manager!.AppendUsage(
                        (string)a["kind"]!,
                        (string)a["provider"]!,
                        (string)a["model"]!,
                        a["usage"]!.Deserialize<Usage>(SessionEntries.JsonOptions)!,
                        OptString(a["note"]))));
                    break;
                case "appendCompaction":
                    RecordId(op, SerializeToNode(manager!.AppendCompaction(
                        (string)a["summary"]!,
                        (string?)a["firstKeptEntryId"],
                        (long)a["tokensBefore"]!,
                        a["details"]?.DeepClone(),
                        (bool?)a["fromHook"],
                        OptUsage(a["usage"]))));
                    break;
                case "appendCustomEntry":
                    RecordId(op, SerializeToNode(
                        manager!.AppendCustomEntry((string)a["customType"]!, a["data"]?.DeepClone())));
                    break;
                case "appendSessionInfo":
                    RecordId(op, SerializeToNode(manager!.AppendSessionInfo((string)a["name"]!)));
                    break;
                case "appendCustomMessageEntry":
                    RecordId(op, SerializeToNode(manager!.AppendCustomMessageEntry(
                        (string)a["customType"]!,
                        a["content"]!.Deserialize<MessageContent>(SessionEntries.JsonOptions)!,
                        (bool)a["display"]!,
                        a["details"]?.DeepClone())));
                    break;
                case "appendContextEdit":
                    RecordId(op, SerializeToNode(manager!.AppendContextEdit(
                        (string)a["targetId"]!, OptReplacement(a["replacement"]))));
                    break;
                case "appendLabelChange":
                    RecordId(op, SerializeToNode(
                        manager!.AppendLabelChange((string)a["targetId"]!, OptString(a["label"]))));
                    break;
                case "branch":
                    manager!.Branch((string)a["fromId"]!);
                    Record(op, null);
                    break;
                case "branchWithSummary":
                    RecordId(op, SerializeToNode(manager!.BranchWithSummary(
                        (string?)a["branchFromId"],
                        (string)a["summary"]!,
                        a["details"]?.DeepClone(),
                        (bool?)a["fromHook"],
                        OptUsage(a["usage"]))));
                    break;
                case "resetLeaf":
                    manager!.ResetLeaf();
                    Record(op, null);
                    break;
                case "createBranchedSession":
                    RecordPath(op, SerializeToNode(manager!.CreateBranchedSession((string)a["leafId"]!)));
                    break;
                case "getEntries":
                    Record(op, SerializeToNode(manager!.GetEntries()));
                    break;
                case "getLeafId":
                    Record(op, SerializeToNode(manager!.GetLeafId()));
                    break;
                case "getLeafEntry":
                    Record(op, SerializeToNode(manager!.GetLeafEntry()));
                    break;
                case "getEntry":
                    Record(op, SerializeToNode(manager!.GetEntry((string)a["id"]!)));
                    break;
                case "getChildren":
                    Record(op, SerializeToNode(manager!.GetChildren((string)a["parentId"]!)));
                    break;
                case "getLabel":
                    Record(op, SerializeToNode(manager!.GetLabel((string)a["id"]!)));
                    break;
                case "getBranch":
                    Record(op, SerializeToNode(manager!.GetBranch(OptString(a["fromId"]))));
                    break;
                case "buildContextEntries":
                    Record(op, SerializeToNode(manager!.BuildContextEntries()));
                    break;
                case "buildSessionProjection":
                    Record(op, SerializeToNode(manager!.BuildSessionProjection()));
                    break;
                case "buildSessionContext":
                    Record(op, SerializeToNode(manager!.BuildSessionContext()));
                    break;
                case "getHeader":
                    Record(op, SerializeToNode(manager!.GetHeader()));
                    break;
                case "getEntryCount":
                    Record(op, SerializeToNode(manager!.GetEntryCount()));
                    break;
                case "getSessionName":
                    Record(op, SerializeToNode(manager!.GetSessionName()));
                    break;
                case "getTree":
                    Record(op, SerializeToNode(manager!.GetTree()));
                    break;
                case "isPersisted":
                    Record(op, SerializeToNode(manager!.IsPersisted()));
                    break;
                case "getCwd":
                    Record(op, SerializeToNode(manager!.GetCwd()));
                    break;
                case "getSessionDir":
                    Record(op, SerializeToNode(manager!.GetSessionDir()));
                    break;
                case "usesDefaultSessionDir":
                    Record(op, SerializeToNode(manager!.UsesDefaultSessionDir()));
                    break;
                case "getSessionId":
                    Record(op, SerializeToNode(manager!.GetSessionId()));
                    break;
                case "getSessionFile":
                    RecordPath(op, SerializeToNode(manager!.GetSessionFile()));
                    break;
                case "listFiles":
                {
                    var dir = Abs((string)a["dir"]!);
                    var array = new JsonArray();
                    foreach (var name in Directory.GetFileSystemEntries(dir)
                                 .Select(Path.GetFileName)
                                 .OrderBy(name => name, StringComparer.Ordinal))
                    {
                        var full = Path.Join(dir, name);
                        array.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["lines"] = File.Exists(full) ? FileLines(File.ReadAllText(full)) : null,
                        });
                    }

                    Record(op, array);
                    break;
                }
                case "readSessionFile":
                    Record(op, FileLines(File.ReadAllText(Abs((string)a["path"]!), Encoding.UTF8)));
                    break;
                case "expectThrow":
                {
                    var nested = run ?? throw new InvalidOperationException("expectThrow requires a run sibling");
                    var debug = Environment.GetEnvironmentVariable("PI_SM_CORPUS_DEBUG") is not null;
                    try
                    {
                        var nestedArgs = nested["args"] as JsonObject is { } args ? DerefArgs(args) : [];
                        if (debug)
                        {
                            Console.Error.WriteLine($"[expectThrow] nested={nested["op"]} args={nestedArgs.ToJsonString()}");
                        }

                        await RunOneAsync((string)nested["op"]!, nestedArgs);
                        if (debug)
                        {
                            Console.Error.WriteLine($"[expectThrow] nested={nested["op"]} did NOT throw");
                        }

                        Record(op, new JsonObject { ["error"] = null });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (debug)
                        {
                            Console.Error.WriteLine($"[expectThrow] nested={nested["op"]} threw {ex.GetType().Name}: {ex.Message}");
                        }

                        Record(op, new JsonObject { ["error"] = ex.Message });
                    }

                    break;
                }
                case "staticList":
                    Record(op, SerializeToNode(await SessionManager.ListAsync(
                        Abs((string)a["cwd"]!), OptDir(a["sessionDir"]))));
                    break;
                case "staticFindById":
                    RecordPath(op, SerializeToNode(
                        SessionManager.FindById(
                            Abs((string)a["cwd"]!), (string)a["id"]!, OptDir(a["sessionDir"])) ?? null));
                    break;
                case "staticListAll":
                    Record(op, SerializeToNode(
                        await SessionManager.ListAllAsync(OptDir(a["sessionDir"]))));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown op: {op}");
            }
        }

        foreach (var opNode in ops.OfType<JsonObject>())
        {
            var op = (string)opNode["op"]!;
            var args = opNode["args"] as JsonObject ?? [];
            // fixture 先展开 __pad 压缩格式，再解引用 $N/$pN/$root；expectThrow 的 "run" 是 op 的
            // 兄弟键，作为参数传入（与 TS 解释器一致）。
            await RunOneAsync(op, DerefArgs((JsonObject)ExpandPad(args)), opNode["run"] as JsonObject);
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // normalization + tokenization (must mirror the generator)
    // ─────────────────────────────────────────────────────────────────────────────

    private static string Serialize(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(NodeOptions);

    /// <summary>
    /// Value-level normalization: numbers under a <c>timestamp</c> key → "&lt;millis&gt;", strings
    /// longer than 128 chars → "&lt;str:len&gt;" (length measured after folding the temp root into
    /// "&lt;TMP&gt;", matching the TS generator), separators unified, object keys sorted.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node, string tempRoot)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    result.Add(Normalize(item, tempRoot)?.DeepClone());
                }

                return result;
            }
            case JsonObject obj:
            {
                var entries = new List<(string Key, JsonNode? Value)>();
                foreach (var (key, value) in obj)
                {
                    var normalized = key == "timestamp"
                                    && value is JsonValue { } number
                                    && !number.TryGetValue<string>(out _)
                        ? (JsonNode?)JsonValue.Create("<millis>")
                        : Normalize(value, tempRoot);
                    entries.Add((key, normalized));
                }

                entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
                var result = new JsonObject();
                foreach (var (key, value) in entries)
                {
                    result[key] = value?.DeepClone();
                }

                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var s):
            {
                var unified = s.Replace('\\', '/');
                // 与 TS 生成器一致：tempRoot 与会话目录 safePath 形态都折叠后再取长度。
                var withTmp = unified.Replace(tempRoot.Replace('\\', '/'), "<TMP>");
                withTmp = withTmp.Replace("--" + SafePathEncode(tempRoot), "--<TMP>");
                return withTmp.Length > 128 ? JsonValue.Create($"<str:{withTmp.Length}>") : JsonValue.Create(withTmp);
            }
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// 语料里的 <c>{"__pad": {"p": prefix, "n": count}}</c> 还原为 <c>prefix + "x".repeat(count)</c>
    /// （超长 fixture 的压缩格式，见生成器头部注释）。对应 TS 解释器的 <c>expandPad</c>。
    /// </summary>
    private static JsonNode ExpandPad(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["__pad"] is JsonObject pad
                && pad["p"] is JsonValue { } prefixNode && prefixNode.TryGetValue<string>(out var prefix)
                && pad["n"] is JsonValue { } countNode && countNode.TryGetValue<int>(out var count))
            {
                // p + x*n + s（suffix 可选）：x-run 后可能还拼接了内容（huge-header fixture 的第二行）。
                var rebuilt = prefix + new string('x', count);
                if (pad["s"] is JsonValue { } suffixNode && suffixNode.TryGetValue<string>(out var suffix))
                {
                    rebuilt += suffix;
                }

                return JsonValue.Create(rebuilt);
            }

            var copy = new JsonObject();
            foreach (var (key, value) in obj)
            {
                copy[key] = value is null ? null : ExpandPad(value);
            }

            return copy;
        }

        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array)
            {
                copy.Add(item is null ? null : ExpandPad(item));
            }

            return copy;
        }

        return node.DeepClone();
    }

    private static string NextToken(Dictionary<string, int> counters, string prefix)
    {
        var next = counters.GetValueOrDefault(prefix) + 1;
        counters[prefix] = next;
        return prefix + next;
    }

    private static string Tokenize(string text, string tempRoot)
    {
        var outText = text
            .Replace(tempRoot, "<TMP>")
            .Replace(tempRoot.Replace('\\', '/'), "<TMP>")
            .Replace("--" + SafePathEncode(tempRoot), "--<TMP>")
            .Replace('\\', '/');

        var counters = new Dictionary<string, int>();
        outText = UuidPattern.Replace(outText, _ => NextToken(counters, "#"));
        outText = ShortIdPattern.Replace(outText, _ => NextToken(counters, "#"));
        outText = OffsetPattern.Replace(outText, _ => NextToken(counters, "<ts") + ">");
        outText = IsoPattern.Replace(outText, _ => NextToken(counters, "<ts") + ">");
        outText = FileTsPattern.Replace(outText, "<file-ts>");
        return outText;
    }

    /// <summary>
    /// Session-dir safePath encoding of the temp-root prefix: leading slash stripped, separators and
    /// ":" dashed — "--C--Users-...-proj--" collapses to "--&lt;TMP&gt;-proj--".
    /// </summary>
    private static string SafePathEncode(string path)
    {
        var stripped = path.StartsWith('/') || path.StartsWith('\\') ? path[1..] : path;
        var builder = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            builder.Append(ch is '/' or '\\' or ':' ? '-' : ch);
        }

        return builder.ToString();
    }
}
