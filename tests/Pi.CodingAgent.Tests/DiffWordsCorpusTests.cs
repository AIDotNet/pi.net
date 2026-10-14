// ============================================================================
// Replays JsDiff.DiffWords against diffwords-corpus.json, captured from the upstream
// jsdiff 8.0.4 UMD bundle by tools/gen-diffwords-corpus.mjs.
// ============================================================================
//
// `diffWords` is what the edit renderer (core/tools/renderers/edit.ts, via
// modes/interactive/components/diff.ts) uses for intra-line change highlighting, and its whitespace
// post-pass has a lot of documented edge cases. The corpus drives the real algorithm, so these vectors
// pin this port against jsdiff rather than against a reading of its source.

using System.Text.Json;
using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class DiffWordsCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "diffwords-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static List<DiffPart> Expected(JsonElement item) =>
        item.GetProperty("parts").EnumerateArray()
            .Select(part => new DiffPart(
                part.GetProperty("value").GetString()!,
                part.GetProperty("added").GetBoolean(),
                part.GetProperty("removed").GetBoolean()))
            .ToList();

    private static string Describe(IEnumerable<DiffPart> parts) =>
        string.Join(
            ", ",
            parts.Select(part =>
                $"{(part.Added ? "+" : part.Removed ? "-" : "=")}{JsonSerializer.Serialize(part.Value)}"));

    public static IEnumerable<object[]> Cases() =>
        Corpus.GetProperty("cases").EnumerateArray()
            .Select((item, index) => new object[] { index, item.GetProperty("old").GetString()!, item.GetProperty("new").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void DiffWords_MatchesTheJsdiffCorpus(int index, string oldText, string newText)
    {
        var expected = Expected(Corpus.GetProperty("cases")[index]);
        var actual = JsDiff.DiffWords(oldText, newText);

        Assert.True(
            expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First == pair.Second),
            $"case {index} ({JsonSerializer.Serialize(oldText)} → {JsonSerializer.Serialize(newText)}): " +
            $"expected [{Describe(expected)}] but got [{Describe(actual)}]");
    }

    [Fact]
    public void Corpus_HasTheExpectedShape()
    {
        Assert.Equal("8.0.4", Corpus.GetProperty("jsdiffVersion").GetString());
        Assert.True(Corpus.GetProperty("cases").GetArrayLength() >= 50);
    }

    // ---------------------------------------------------------------------------------------------
    // Pinned examples — the five worked cases from jsdiff's dedupeWhitespaceInChangeObjects docs.
    // They are in the corpus too; these keep the expected behaviour readable in the test itself.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DiffWords_ReplacementKeepsTheSharedWhitespaceOnTheKeepTokens()
    {
        // 'foo bar baz' vs 'foo qux baz' → K:'foo ' D:'bar' I:'qux' K:' baz'
        AssertParts(
            JsDiff.DiffWords("foo bar baz", "foo qux baz"),
            ("foo ", false, false),
            ("bar", false, true),
            ("qux", true, false),
            (" baz", false, false));
    }

    [Fact]
    public void DiffWords_PureDeletionKeepsTheTrailingWhitespaceOnTheDeletedToken()
    {
        // 'foo bar baz' vs 'foo baz' → K:'foo ' D:'bar ' K:'baz'
        AssertParts(
            JsDiff.DiffWords("foo bar baz", "foo baz"),
            ("foo ", false, false),
            ("bar ", false, true),
            ("baz", false, false));
    }

    [Fact]
    public void DiffWords_NewlineDeletionStaysAttachedToTheDeletedToken()
    {
        // 'foo\nbar baz' vs 'foo baz' → K:'foo' D:'\nbar' K:' baz'
        AssertParts(
            JsDiff.DiffWords("foo\nbar baz", "foo baz"),
            ("foo", false, false),
            ("\nbar", false, true),
            (" baz", false, false));
    }

    [Fact]
    public void DiffWords_RunLengthChangeMovesTheExtraWhitespace()
    {
        // 'foo   bar baz' vs 'foo  baz' → K:'foo  ' D:' bar ' K:'baz'
        AssertParts(
            JsDiff.DiffWords("foo   bar baz", "foo  baz"),
            ("foo  ", false, false),
            (" bar ", false, true),
            ("baz", false, false));
    }

    [Fact]
    public void DiffWords_SingleWordSwapInCode()
    {
        AssertParts(
            JsDiff.DiffWords("const x = 1;", "const x = 2;"),
            ("const x = ", false, false),
            ("1", false, true),
            ("2", true, false),
            (";", false, false));
    }

    private static void AssertParts(
        List<DiffPart> actual, params (string Value, bool Added, bool Removed)[] expected)
    {
        Assert.Equal(
            expected.Select(entry => new DiffPart(entry.Value, entry.Added, entry.Removed)).ToList(),
            actual);
    }
}
