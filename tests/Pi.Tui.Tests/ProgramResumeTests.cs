using System.Security.Cryptography;
using System.Text;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>OSC 7577 程序恢复协议：与 TS tui/program-resume.ts 的线格式对齐。</summary>
public class ProgramResumeTests
{
    private static readonly ProgramResumeSender Sender = new("secret-token", 4242, 1_700_000_000);

    [Fact]
    public void FormatsReportWithSha256Mac()
    {
        var sequence = ProgramResume.Format(
            new ProgramResumeCommand(["pi", "--session", "abc"], "/tmp/work"), Sender);
        Assert.NotNull(sequence);
        Assert.StartsWith("\x1b]7577;pid=4242:time=1700000000:", sequence);
        Assert.EndsWith("\x1b\\", sequence);

        var body = sequence![7..^2]; // "\x1b]7577;" 共 7 个字符
        var mac = body.Split(':').Last(f => f.StartsWith("mac="))[4..];
        var payload = body[..body.LastIndexOf(":mac=", StringComparison.Ordinal)];
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{payload}:secret-token"))).ToLowerInvariant();
        Assert.Equal(expected, mac);
        Assert.Contains($"argv={Convert.ToBase64String(Encoding.UTF8.GetBytes("pi"))}", body);
        Assert.Contains($"cwd={Convert.ToBase64String(Encoding.UTF8.GetBytes("/tmp/work"))}", body);
    }

    [Fact]
    public void RejectsRelativeProgramAndControlCharacters()
    {
        Assert.Null(ProgramResume.Format(new ProgramResumeCommand(["./pi"]), Sender));
        Assert.Null(ProgramResume.Format(new ProgramResumeCommand(["pi", "bad\nvalue"]), Sender));
    }

    [Fact]
    public void FormatClearUsesClearMarker()
    {
        var sequence = ProgramResume.FormatClear(Sender);
        Assert.Contains("clear=1", sequence);
        Assert.DoesNotContain("argv=", sequence);
    }

    [Fact]
    public void TakeTokenRemovesItFromEnvironment()
    {
        const string name = ProgramResume.TokenEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "token-value");
            var env = new Dictionary<string, string?> { [name] = "token-value" };
            Assert.Equal("token-value", ProgramResume.TakeToken(env));
            Assert.False(env.ContainsKey(name));
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
