using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 私有参数 CSI（`b5fc6dc84`）：`ESC[&lt;35;59;1M`（SGR 鼠标报告）、`ESC[&gt;0;276;0c`（终端回复）这类带 `&lt;`/`=`/`&gt;`
/// 前缀的序列要整体剥掉；而 `ESC=` / `ESC&gt;`（键盘模式）后面跟着的数字是正文，不能被吞。
/// </summary>
public class AnsiTextPrivateCsiTests
{
    [Fact]
    public void StripsPrivateParameterCsiSequencesWithTheirParameters()
    {
        Assert.Equal("step 1", AnsiText.StripAnsi("\x1b[<35;59;1Mstep 1"));
        Assert.Equal("ab", AnsiText.StripAnsi("a\x1b[<0;10;20mb"));
        Assert.Equal("ab", AnsiText.StripAnsi("a\x1b[>0;276;0cb"));
        Assert.Equal("ab", AnsiText.StripAnsi("a\x1b[>cb"));
        Assert.Equal("ab", AnsiText.StripAnsi("a\x1b[=5hb"));
        Assert.Equal("ab", AnsiText.StripAnsi("a\x9b<35;59;1Mb"));
    }

    [Fact]
    public void KeepsDigitsAfterBareEscKeypadModeSequences()
    {
        Assert.Equal("123 tests passed", AnsiText.StripAnsi("\x1b[?1h\x1b=123 tests passed"));
        Assert.Equal("42 files", AnsiText.StripAnsi("\x1b[?1l\x1b>42 files"));
    }
}
