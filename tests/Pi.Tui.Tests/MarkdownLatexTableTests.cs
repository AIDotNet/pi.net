using Pi.Tui.Marked;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 显示公式不得跨表格（上游 #11159）：未闭合的 <c>$$</c> 曾与更后面的 <c>$$</c> 配对，或一直停留在
/// pending 状态，把中间的 GFM 表格整段吞掉；流式输出时表格会短暂消失。
/// </summary>
public class MarkdownLatexTableTests
{
    /// <summary><c>components/markdown.ts</c> 把 LaTeX 扩展装在词法器选项上，测试需同样注册。</summary>
    private static List<MarkedToken> Lex(string source) =>
        MarkedLexer.Lex(source, new MarkedOptions { Extensions = MarkedLatexExtensions.Create() });

    private const string UnclosedMathAroundTable = """
        $$
        | a | b |
        | --- | --- |
        | 1 | 2 |
        """;

    private const string ClosedMathAroundTable = """
        $$
        x
        $$

        | a | b |
        | --- | --- |
        | 1 | 2 |
        """;

    [Fact]
    public void UnclosedDisplayMathDoesNotSwallowFollowingTable()
    {
        var tokens = Lex(UnclosedMathAroundTable);
        // 表格被识别为 table，而不是被并进 latexBlock。
        Assert.Contains(tokens, token => token.Type == "table");
        Assert.DoesNotContain(tokens, token => token.Type == "latexBlock" && token.Raw.Contains('|'));
    }

    [Fact]
    public void ClosedDisplayMathStillRendersTables()
    {
        var tokens = Lex(ClosedMathAroundTable);
        Assert.Contains(tokens, token => token.Type == "latexBlock");
        Assert.Contains(tokens, token => token.Type == "table");
    }

    [Fact]
    public void PipeFreeContentIsNotTreatedAsTableStart()
    {
        // 无竖线的分隔行是 thematic break，不是表格；不能因此把正常的显示公式拆开。
        var tokens = Lex("""
            $$
            ----
            a
            $$
            """);
        Assert.Contains(tokens, token => token.Type == "latexBlock");
    }
}
