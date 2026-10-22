using System.Text;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// The OSC 7501 program status protocol (port of <c>program-status.ts</c>, upstream 1.1.0 / #10607).
/// </summary>
public class ProgramStatusTests
{
    [Fact]
    public void FormatWritesStateAppAndBlockedKind()
    {
        Assert.Equal(
            "\x1b]7501;state=idle:app=pi\x1b\\",
            ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Idle) { App = "pi" }));

        Assert.Equal(
            "\x1b]7501;state=blocked:app=pi:kind=question:msg=b2s=\x1b\\",
            ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Blocked)
            {
                App = "pi",
                Kind = ProgramStatusKind.Question,
                Message = "ok",
            }));

        // kind is only meaningful for blocked.
        Assert.Equal(
            "\x1b]7501;state=working\x1b\\",
            ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Working)
            {
                Kind = ProgramStatusKind.Auth,
            }));

        // clear carries nothing but the state.
        Assert.Equal(
            "\x1b]7501;state=clear\x1b\\",
            ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Clear) { Message = "" }));
    }

    [Fact]
    public void FormatOmitsAppNamesOutsideTheSpecPattern()
    {
        foreach (var app in new[] { "", "has space", "toolongtoolongtoolongtoolongtoolong", "pi/coding", "π" })
        {
            var formatted = ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Idle) { App = app });
            Assert.Equal("\x1b]7501;state=idle\x1b\\", formatted);
        }

        // 32 characters is the limit and still accepted.
        var longest = new string('a', 32);
        Assert.Equal(
            $"\x1b]7501;state=idle:app={longest}\x1b\\",
            ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Idle) { App = longest }));
    }

    [Fact]
    public void FormatReplacesControlCharactersAndTrims()
    {
        var formatted = ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Error)
        {
            Message = "  line\u0001one\u007f \u0090two  ",
        });

        // Controls become spaces (each run collapses to one), JS trim removes the surrounding
        // whitespace, then the rest is encoded.
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("line one   two"));
        Assert.Equal($"\x1b]7501;state=error:msg={expected}\x1b\\", formatted);
    }

    [Fact]
    public void FormatCapsTheMessageAtTheSpecLimitOnCodePointBoundaries()
    {
        // 4 bytes each: 512 runes fit in 2048 bytes.
        var astral = string.Concat(Enumerable.Repeat("𠀀", 1024));
        var formatted = ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Error) { Message = astral });
        var base64 = formatted[(formatted.IndexOf("msg=", StringComparison.Ordinal) + 4)..^2];
        Assert.Equal(512, Encoding.UTF8.GetString(Convert.FromBase64String(base64)).EnumerateRunes().Count());

        // A 3-byte rune cannot straddle the limit: 683 × 3 = 2049 > 2048, so 682 survive.
        var wide = new string('中', 1000);
        formatted = ProgramStatusProtocol.Format(new ProgramStatus(ProgramStatusState.Error) { Message = wide });
        base64 = formatted[(formatted.IndexOf("msg=", StringComparison.Ordinal) + 4)..^2];
        Assert.Equal(682, Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Length);
    }

    [Theory]
    [InlineData("\x1b]7501;?\x1b\\", true)]
    [InlineData("\x1b]7501;?\x07", true)]
    [InlineData("\x1b]7501;?;x=1\x1b\\", true)]
    [InlineData("\x1b]7501;state=idle\x1b\\", false)]
    [InlineData("\x1b]7501;?\x1b\\x", false)]
    [InlineData("x\x1b]7501;?\x1b\\", false)]
    [InlineData("\x1b]7501;?", false)]
    [InlineData("\x1b]7502;?\x1b\\", false)]
    public void IsReplyMatchesOnlyTheQueryEcho(string sequence, bool expected)
    {
        Assert.Equal(expected, ProgramStatusProtocol.IsReply(sequence));
    }

    [Fact]
    public void FormatUsesTheSpecSpellings()
    {
        Assert.Equal("idle", ProgramStatusProtocol.StateName(ProgramStatusState.Idle));
        Assert.Equal("working", ProgramStatusProtocol.StateName(ProgramStatusState.Working));
        Assert.Equal("blocked", ProgramStatusProtocol.StateName(ProgramStatusState.Blocked));
        Assert.Equal("done", ProgramStatusProtocol.StateName(ProgramStatusState.Done));
        Assert.Equal("error", ProgramStatusProtocol.StateName(ProgramStatusState.Error));
        Assert.Equal("clear", ProgramStatusProtocol.StateName(ProgramStatusState.Clear));

        Assert.Equal("permission", ProgramStatusProtocol.KindName(ProgramStatusKind.Permission));
        Assert.Equal("question", ProgramStatusProtocol.KindName(ProgramStatusKind.Question));
        Assert.Equal("auth", ProgramStatusProtocol.KindName(ProgramStatusKind.Auth));
    }
}
