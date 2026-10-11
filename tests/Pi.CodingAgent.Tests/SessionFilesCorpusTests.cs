using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the session-file I/O and directory discovery (port of the 4e-2c-2 slice of
/// <c>core/session-manager.ts</c>). Replays <c>session-files-corpus.json</c>, which the real TS
/// module produced against files in a Node temp directory: the C# test re-creates the same bytes and
/// mtimes in its own temp directory and asserts identical results.
///
/// Covered: <see cref="SessionFiles.LoadEntriesFromFile"/> (valid/empty/header-only/bad-first-line/
/// malformed+blank lines/missing trailing newline repair/BOM/multibyte/truncated tail),
/// <see cref="SessionFiles.FindMostRecentSession"/> (mtime order, cwd filter, non-session skip,
/// missing dir, empty dir), and <c>buildSessionInfo</c>/<c>listSessionsFromDir</c> through the
/// <c>SessionManager.list</c> facade semantics (cwd filter + modified-descending sort).
/// </summary>
public class SessionFilesCorpusTests : IDisposable
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private readonly string _tempRoot;

    public SessionFilesCorpusTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pi-session-files-tests-" + Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "session-files-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    /// <summary>Substitutes the corpus's temp-root token with this test's temp root (forward slashes).</summary>
    private string SubstituteTmp(string normalizedPath) => normalizedPath.Replace("<TMP>", _tempRoot.Replace('\\', '/'), StringComparison.Ordinal);

    // ------------------------------------------------------------------ loadEntriesFromFile

    public static IEnumerable<object[]> LoadKeys() =>
        Corpus.GetProperty("loadEntriesFromFile").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(LoadKeys))]
    public void LoadEntriesFromFile_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("loadEntriesFromFile"), "name", label);
        var content = vector.GetProperty("content").GetString()!;
        var filePath = Path.Combine(_tempRoot, "load", $"{label}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content); // UTF-8 without BOM, matching writeFileSync("utf8").

        var entries = SessionFiles.LoadEntriesFromFile(filePath);

        AssertDeepEquality(
            $"load {label}",
            vector.GetProperty("expected"),
            JsonSerializer.Serialize(entries, SessionEntries.JsonOptions));

        // `missing-trailing-newline` and `truncated-json-tail` repair the file by appending "\n".
        var onDisk = File.ReadAllText(filePath);
        Assert.Equal(vector.GetProperty("repaired").GetBoolean(), onDisk == content + "\n");
    }

    // ------------------------------------------------------------------ findMostRecentSession

    public static IEnumerable<object[]> FindKeys() =>
        Corpus.GetProperty("findMostRecentSession").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(FindKeys))]
    public void FindMostRecentSession_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("findMostRecentSession"), "name", label);
        var dir = Path.Combine(_tempRoot, vector.GetProperty("dirName").GetString()!);
        foreach (var file in vector.GetProperty("files").EnumerateArray())
        {
            var filePath = Path.Combine(dir, file.GetProperty("name").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, file.GetProperty("content").GetString()!);
            SetMtime(filePath, file.GetProperty("mtimeMs"));
        }

        var cwd = vector.GetProperty("cwd");
        var found = SessionFiles.FindMostRecentSession(
            dir,
            cwd.ValueKind == JsonValueKind.Null ? null : cwd.GetString());

        var expected = vector.GetProperty("expected");
        Assert.Equal(
            expected.ValueKind == JsonValueKind.Null ? null : SubstituteTmp(expected.GetString()!),
            found?.Replace('\\', '/'));
    }

    // ------------------------------------------------------------------ listSessions (facade)

    public static IEnumerable<object[]> ListKeys() =>
        Corpus.GetProperty("listSessions").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(ListKeys))]
    public async Task ListSessions_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("listSessions"), "name", label);
        foreach (var file in vector.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("name").GetString()!;
            var filePath = Path.Combine(_tempRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, file.GetProperty("content").GetString()!);
            SetMtime(filePath, file.GetProperty("mtimeMs"));
        }

        var dir = Path.Combine(_tempRoot, "list", label);
        var sessions = await SessionFiles.ListSessionsAsync(dir);

        // The TS corpus drove SessionManager.list(cwd, dir) = listSessionsFromDir + cwd filter +
        // modified-descending sort; replay the same facade around the port's directory scan.
        var facadeCwd = vector.GetProperty("facadeCwd").GetString()!;
        var resolvedCwd = Paths.ResolvePath(facadeCwd);
        var filtered = sessions
            .Where(info => info.Cwd.Length > 0 && Paths.ResolvePath(info.Cwd) == resolvedCwd)
            .OrderByDescending(info => info.Modified) // stable, like the TS Array.sort
            .ToList();

        var expected = vector.GetProperty("expected");
        Assert.Equal(expected.GetArrayLength(), filtered.Count);
        for (var index = 0; index < filtered.Count; index++)
        {
            AssertSessionInfo($"list {label} #{index}", expected[index], filtered[index]);
        }
    }

    private void AssertSessionInfo(string label, JsonElement expected, SessionInfo actual)
    {
        Assert.Equal(SubstituteTmp(expected.GetProperty("path").GetString()!), actual.Path.Replace('\\', '/'));
        Assert.Equal(expected.GetProperty("id").GetString(), actual.Id);
        Assert.Equal(expected.GetProperty("cwd").GetString(), actual.Cwd);
        // TS JSON.stringify omits keys whose value is undefined; treat a missing key as null.
        AssertNullableString(label, "name", TryGet(expected, "name"), actual.Name);
        AssertNullableString(label, "parentSessionPath", TryGet(expected, "parentSessionPath"), actual.ParentSessionPath);
        AssertMillis(label, "created", TryGet(expected, "created"), actual.Created);
        AssertMillis(label, "modified", TryGet(expected, "modified"), actual.Modified);
        Assert.Equal(expected.GetProperty("messageCount").GetInt32(), actual.MessageCount);
        Assert.Equal(expected.GetProperty("firstMessage").GetString(), actual.FirstMessage);
        Assert.Equal(expected.GetProperty("allMessagesText").GetString(), actual.AllMessagesText);
    }

    private static JsonElement TryGet(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : default;

    private static void AssertNullableString(string label, string field, JsonElement expected, string? actual)
    {
        // Undefined: the key is absent (TS JSON.stringify omits undefined values) — same as null.
        var expectedValue = expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : expected.GetString();
        Assert.True(
            string.Equals(expectedValue, actual, StringComparison.Ordinal),
            $"{label}: {field} mismatch (expected {expectedValue ?? "<null>"}, got {actual ?? "<null>"})");
    }

    /// <summary>Compares instants as Unix milliseconds. A null on the TS side is an Invalid Date
    /// (unparsable header.timestamp), which the port folds to <see cref="DateTimeOffset.MinValue"/>.</summary>
    private static void AssertMillis(string label, string field, JsonElement expected, DateTimeOffset actual)
    {
        if (expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            Assert.True(
                actual == DateTimeOffset.MinValue,
                $"{label}: {field} expected Invalid Date (MinValue), got {actual:O}");
            return;
        }

        var expectedMillis = DateTimeOffset.Parse(
                expected.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUnixTimeMilliseconds();
        Assert.True(
            expectedMillis == actual.ToUnixTimeMilliseconds(),
            $"{label}: {field} mismatch (expected {expectedMillis}, got {actual.ToUnixTimeMilliseconds()})");
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Corpus_IsComplete()
    {
        // Lower bounds, so a truncated fixture cannot silently turn the suite green.
        Assert.Equal(9, Corpus.GetProperty("loadEntriesFromFile").GetArrayLength());
        Assert.Equal(5, Corpus.GetProperty("findMostRecentSession").GetArrayLength());
        Assert.Equal(6, Corpus.GetProperty("listSessions").GetArrayLength());

        // The repair behavior must be exercised (missing trailing newline / truncated tail).
        Assert.Equal(2, Corpus.GetProperty("loadEntriesFromFile").EnumerateArray()
            .Count(vector => vector.GetProperty("repaired").GetBoolean()));

        // The listing must include a non-session file and an empty file (both yield no SessionInfo).
        Assert.Contains(Corpus.GetProperty("listSessions").EnumerateArray(),
            vector => vector.GetProperty("name").GetString() == "no-header");
        Assert.Contains(Corpus.GetProperty("listSessions").EnumerateArray(),
            vector => vector.GetProperty("name").GetString() == "empty-file");
    }

    // ------------------------------------------------------------------ helpers

    private static void SetMtime(string filePath, JsonElement mtimeMs) =>
        File.SetLastWriteTimeUtc(
            filePath,
            DateTimeOffset.FromUnixTimeMilliseconds((long)mtimeMs.GetDouble()).UtcDateTime);

    private static void AssertDeepEquality(string label, JsonElement expected, string actualJson)
    {
        var expectedNode = JsonNode.Parse(expected.GetRawText());
        var actualNode = JsonNode.Parse(actualJson);
        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"{label} mismatch:\n  expected: {expectedNode?.ToJsonString()}\n  actual:   {actualNode?.ToJsonString()}");
    }

    private static JsonElement FindByProperty(JsonElement array, string property, string value) =>
        array.EnumerateArray().First(vector => vector.GetProperty(property).GetString() == value);
}
