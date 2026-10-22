using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Tui.Components;
using Pi.Tui.Marked;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// The <c>marked-cjk-friendly</c> em/strong extension that <c>components/markdown.ts</c> installs
/// (upstream <c>markdown.test.ts:1658</c>, issue #10154).
/// </summary>
/// <remarks>
/// <para>
/// Two layers. <c>markdown-cjk-corpus.json</c> pins the inline token trees for a curated input list,
/// emitted by running the real <c>marked@18.0.11</c> + <c>marked-cjk-friendly@0.1.2</c> pair twice per
/// input — once with the extension (<c>cjk</c>) and once without (<c>plain</c>). Regenerate with
/// <c>node tools/gen-tui-cjk-corpus.mjs &gt; tests/Pi.Tui.Tests/markdown-cjk-corpus.json</c>.
/// </para>
/// <para>
/// The render assertions below are the upstream test block copied verbatim: the theme prints
/// <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c> instead of ANSI so the expected lines stay readable.
/// </para>
/// </remarks>
public class MarkdownCjkTests
{
    private static readonly string CorpusPath = Path.Combine(AppContext.BaseDirectory, "markdown-cjk-corpus.json");

    /// <summary>Attributes are only preserved when a hyperlink-capable theme is irrelevant; no links here.</summary>
    private static readonly MarkdownTheme TagTheme = new()
    {
        Heading = text => text,
        Link = text => text,
        LinkUrl = text => text,
        Code = text => text,
        CodeBlock = text => text,
        CodeBlockBorder = text => text,
        Quote = text => text,
        QuoteBorder = text => text,
        Hr = text => text,
        ListBullet = text => text,
        Bold = text => $"<b>{text}</b>",
        Italic = text => $"<i>{text}</i>",
        Strikethrough = text => text,
        Underline = text => text,
    };

    private static string[] Render(string text) =>
        new Markdown(text, 0, 0, TagTheme)
            .Render(80)
            .Select(line => line.TrimEnd())
            .ToArray();

    [Fact]
    public void RendersEmphasisDelimitedNextToCjkPunctuation()
    {
        Assert.Equal(["<b>这是测试。</b>后面"], Render("**这是测试。**后面"));
        Assert.Equal(["<b>一句话说清：</b>一台机器"], Render("**一句话说清：**一台机器"));
        Assert.Equal(["<b>要点到此为止。</b>20 小时"], Render("**要点到此为止。**20 小时"));
        Assert.Equal(["字<b>「引用」</b>字"], Render("字**「引用」**字"));
        Assert.Equal(["<i>斜体。</i>后面"], Render("*斜体。*后面"));
    }

    [Fact]
    public void KeepsCommonMarkFlankingRulesForNonCjkText()
    {
        Assert.Equal(["**end.**Then next"], Render("**end.**Then next"));
    }

    [Fact]
    public void TokenTreesMatchTheReferenceExtension()
    {
        using var stream = File.OpenRead(CorpusPath);
        using var document = JsonDocument.Parse(stream);
        var cases = document.RootElement.GetProperty("cases");
        var compared = 0;

        foreach (var @case in cases.EnumerateArray())
        {
            var input = @case.GetProperty("input").GetString()!;
            var withCjk = MarkedLexer.LexInline(input, new MarkedOptions { CjkFriendlyEmphasis = true });
            var withoutCjk = MarkedLexer.LexInline(input, new MarkedOptions());

            AssertMatches(@case.GetProperty("cjk"), withCjk, input, cjk: true);
            AssertMatches(@case.GetProperty("plain"), withoutCjk, input, cjk: false);
            compared++;
        }

        Assert.Equal(cases.GetArrayLength(), compared);
    }

    /// <summary>
    /// The corpus only records the fields <c>components/markdown.ts</c> reads back, so the comparison
    /// projects a port token onto the same shape and compares JSON text (both sides go through the
    /// same serializer, so escaping cannot mask a difference).
    /// </summary>
    private static void AssertMatches(JsonElement expected, List<MarkedToken> actual, string input, bool cjk)
    {
        var expectedJson = JsonNode.Parse(expected.GetRawText())!.ToJsonString();
        var actualJson = new JsonArray(actual.Select(token => (JsonNode)JsonNode.Parse(Project(token))!).ToArray())
            .ToJsonString();

        Assert.True(
            expectedJson == actualJson,
            $"mode={(cjk ? "cjk" : "plain")} input={input}\n expected: {expectedJson}\n   actual: {actualJson}");
    }

    private static string Project(MarkedToken token)
    {
        var node = new JsonObject
        {
            ["type"] = token.Type,
            ["raw"] = token.Raw,
        };

        if (token.Text is not null)
        {
            node["text"] = token.Text;
        }

        if (token.Tokens is not null)
        {
            node["tokens"] = new JsonArray(token.Tokens.Select(child => (JsonNode)JsonNode.Parse(Project(child))!).ToArray());
        }

        return node.ToJsonString();
    }
}
