using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 #12052：HTML 导出把 OSC 8 超链接转成 &lt;a href&gt;，其它转义序列丢弃。
/// </summary>
public class AnsiToHtmlTests
{
    [Fact]
    public void ConvertsOsc8HyperlinksWithStylesNested()
    {
        var input = "see \x1b]8;;https://x.io\x1b\\\x1b[4mREADME\x1b[24m\x1b]8;;\x1b\\.";
        var html = AnsiToHtml.Convert(input);

        Assert.Equal(
            "see <a href=\"https://x.io\"><span style=\"text-decoration:underline\">README</span></a>.",
            html);
    }

    [Fact]
    public void DropsUnsafeSchemesAndOtherEscapes()
    {
        Assert.Equal("x", AnsiToHtml.Convert("\x1b]8;;javascript:alert(1)\x07x\x1b]8;;\x07"));
        Assert.Equal("plain", AnsiToHtml.Convert("\x1b[2Kplain\x1b]0;title\x07"));
    }

    [Fact]
    public void StripEscapesLeavesOnlyText()
    {
        Assert.Equal("abc", AnsiToHtml.StripEscapes("\x1b[0mabc\x1b]8;;\x07"));
    }
}
