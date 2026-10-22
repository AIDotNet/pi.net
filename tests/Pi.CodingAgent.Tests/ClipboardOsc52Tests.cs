using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>PI_CLIPBOARD_OSC52=1</c>（上游 #10826/#10835/#10114）：远程客户端（多路复用器附加、远程 IDE
/// daemon）不留下 SSH 环境标记，需要显式强制 OSC 52 复制。
/// </summary>
public sealed class ClipboardOsc52Tests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] entries)
    {
        var map = entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    [Fact]
    public void SshAndMoshMarkersMarkTheSessionRemote()
    {
        Assert.True(Clipboard.IsRemoteSession(Env(("SSH_CONNECTION", "1.2.3.4 22 5.6.7.8 22"))));
        Assert.True(Clipboard.IsRemoteSession(Env(("SSH_CLIENT", "1.2.3.4 22 22"))));
        Assert.True(Clipboard.IsRemoteSession(Env(("MOSH_CONNECTION", "1.2.3.4 22 5.6.7.8 22"))));
    }

    [Fact]
    public void LocalSessionIsNotRemote()
    {
        Assert.False(Clipboard.IsRemoteSession(Env()));
        Assert.False(Clipboard.IsRemoteSession(Env(("PI_CLIPBOARD_OSC52", "0"))));
        Assert.False(Clipboard.IsRemoteSession(Env(("PI_CLIPBOARD_OSC52", ""))));
        // 只认精确的 "1"，与上游一致。
        Assert.False(Clipboard.IsRemoteSession(Env(("PI_CLIPBOARD_OSC52", "true"))));
    }

    [Fact]
    public void PiClipboardOsc52ForcesRemoteSession()
    {
        Assert.True(Clipboard.IsRemoteSession(Env(("PI_CLIPBOARD_OSC52", "1"))));
    }
}
