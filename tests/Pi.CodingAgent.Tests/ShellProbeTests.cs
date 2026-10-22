// ============================================================================
// Shell probing follows the real Windows directory and accepts Store aliases
// (upstream 28bea006b, #9501/#9504).
//
// The legacy WSL bash shim is recognised under %SystemRoot%, not only under the conventional
// C:\Windows — a machine with Windows installed elsewhere used to miss it and hand `-c` to a
// shim that only reads stdin. The Store-alias half of the upstream fix (node's existsSync
// rejecting runnable aliases) has no .NET equivalent: File.Exists uses GetFileAttributesEx,
// which does not perform the extra stat that fails with EACCES in nodejs/node#36790.
// ============================================================================

using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ShellProbeTests
{
    [Theory]
    // 约定形态：SystemRoot 未知时按 C:\Windows 认。
    [InlineData(@"C:\Windows\System32\bash.exe", "", true)]
    [InlineData(@"C:\Windows\Sysnative\bash.exe", "", true)]
    [InlineData("x:\\windows\\system32\\bash.exe", "", true)]
    [InlineData(@"/opt/bash.exe", "", false)]
    // 已知的 SystemRoot：只认它下面那两个目录。
    [InlineData(@"C:\Windows\System32\bash.exe", @"C:\Windows", true)]
    [InlineData(@"C:\Windows\Sysnative\bash.exe", @"C:\Windows", true)]
    [InlineData("C:/Windows/System32/bash.exe", @"C:\Windows", true)]
    [InlineData(@"D:\Windows\System32\bash.exe", @"C:\Windows", false)]
    // Windows 装在别处：必须跟着 SystemRoot 走（上游 #9504 的核心）。
    [InlineData(@"D:\WINNT\System32\bash.exe", @"D:\WINNT", true)]
    [InlineData(@"D:\WINNT\Sysnative\bash.exe", @"D:\WINNT", true)]
    [InlineData(@"d:\winnt\system32\bash.exe", @"D:\WINNT\", true)]
    [InlineData(@"C:\Windows\System32\bash.exe", @"D:\WINNT", false)]
    // SystemRoot 不是盘符限定路径时不拼路径，退回约定形态。
    [InlineData(@"C:\Windows\System32\bash.exe", @"\\server\share", false)]
    public void LegacyWslBashIsRecognisedUnderTheRealWindowsDirectory(
        string path, string systemRoot, bool expected)
    {
        Assert.Equal(expected, Shell.IsLegacyWslBashPath(path, systemRoot));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\bash", @"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32\bash.exe.exe", @"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32\wsl.exe", @"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32\sub\bash.exe", @"C:\Windows", false)]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", @"C:\Windows", false)]
    [InlineData("bash.exe", @"C:\Windows", false)]
    public void OnlyTheTwoKnownShimDirectoriesMatch(string path, string systemRoot, bool expected)
    {
        Assert.Equal(expected, Shell.IsLegacyWslBashPath(path, systemRoot));
    }
}
