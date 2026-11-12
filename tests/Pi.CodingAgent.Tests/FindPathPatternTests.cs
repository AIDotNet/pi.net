using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.2 #11972 / #9262：Windows find 路径模式不再交给 fd 的 --full-path（fd 10.5.1 前后
/// 行为相反，pi 会下载最新版），改为列全部 + Minimatch 过滤。逐字镜像上游
/// 3302-find-path-glob.test.ts 的匹配语义与 find.ts 的 decision 计算。
/// </summary>
public class FindPathPatternTests
{
    // ---------------------------------------------------------------- decision（FindPathPattern.For）
    [Fact]
    public void WindowsNormalizesBackslashesAndEnablesMatcher()
    {
        var d = FindPathPattern.For(@"src\**\*.ts", isWindows: true);

        Assert.True(d.IsPathPattern);
        Assert.True(d.UseWindowsMatcher);
        Assert.DoesNotContain("\\", d.EffectivePattern);
        Assert.StartsWith("**/", d.EffectivePattern);
        Assert.NotNull(d.MatchPattern);
    }

    [Fact]
    public void PathPatternGetsLeadingGlobstar()
    {
        Assert.Equal("**/src/**/*.ts", FindPathPattern.For("src/**/*.ts", isWindows: true).MatchPattern);
        Assert.Equal("**/src/*.ts", FindPathPattern.For("src/*.ts", isWindows: true).MatchPattern);
    }

    [Fact]
    public void AlreadyLeadingGlobstarOrAbsoluteIsNotDoublePrefixed()
    {
        Assert.Equal("**/*.ts", FindPathPattern.For("**/*.ts", isWindows: true).MatchPattern);
        Assert.Equal("/src/*.ts", FindPathPattern.For("/src/*.ts", isWindows: true).MatchPattern);
        // 纯 "**" 不含 / → basename 模式，不进路径分支、不启用 Windows matcher。
        var bare = FindPathPattern.For("**", isWindows: true);
        Assert.Null(bare.MatchPattern);
        Assert.Equal("**", bare.EffectivePattern);
    }

    [Fact]
    public void TrailingGlobstarBecomesGlobstarSlashStar()
    {
        // minimatch 的 'dir/**' 会连 'dir' 自身一起匹配，fd 不会 → 结尾补 /*（上游逐字逻辑）。
        Assert.Equal("**/some/parent/child/**/*",
            FindPathPattern.For("some/parent/child/**", isWindows: true).MatchPattern);
    }

    [Fact]
    public void BasenamePatternDoesNotUseMatcher()
    {
        var d = FindPathPattern.For("*.spec.ts", isWindows: true);

        Assert.False(d.IsPathPattern);
        Assert.False(d.UseWindowsMatcher);
        Assert.Null(d.MatchPattern);
        Assert.Equal("*.spec.ts", d.EffectivePattern);
    }

    [Fact]
    public void PosixPathPatternUsesFullPathNotWindowsMatcher()
    {
        // POSIX 上 fd 的 --full-path 行为确定，仍走原来的 --glob/--full-path 分支。
        var d = FindPathPattern.For("src/**/*.ts", isWindows: false);

        Assert.True(d.IsPathPattern);
        Assert.False(d.UseWindowsMatcher);
        Assert.Equal("**/src/**/*.ts", d.EffectivePattern);
        // POSIX 上 \ 是 glob 转义符，不被规范化。
        Assert.Equal(@"**/src\/**/*.ts", FindPathPattern.For(@"src\/**/*.ts", isWindows: false).EffectivePattern);
    }

    [Fact]
    public void SmartCaseIsCaseInsensitiveOnlyForAllLowercasePatterns()
    {
        Assert.True(FindPathPattern.For("src/**/*.ts", isWindows: true).NoCase);
        Assert.False(FindPathPattern.For("src/**/*.TS", isWindows: true).NoCase);
    }

    // ---------------------------------------------------------------- 匹配语义（Minimatch）
    private static Minimatch Matcher(string pattern) =>
        FindPathPattern.CreateWindowsMatcher(FindPathPattern.For(pattern, isWindows: true));

    private static bool Matches(string pattern, string path) =>
        FindPathPattern.Matches(Matcher(pattern), path);

    [Fact]
    public void DoubleGlobstarAlsoMatchesFilesDirectlyInDirectory()
    {
        // 上游 #11972：'src/**/*.ts' 也命中 src/ 下直接的文件（**/ 可匹配零段）。
        Assert.True(Matches("src/**/*.ts", "src/direct.spec.ts"));
        Assert.True(Matches("src/**/*.ts", "src/foo/bar/example.spec.ts"));
    }

    [Fact]
    public void SingleStarDoesNotMatchIntoSubdirectories()
    {
        // 上游 #11972：'src/*.ts' 不再命中子目录里的文件。
        Assert.True(Matches("src/*.ts", "src/direct.spec.ts"));
        Assert.False(Matches("src/*.ts", "src/foo/bar/example.spec.ts"));
    }

    [Fact]
    public void TrailingGlobstarMatchesEntriesBelowButNotTheDirectoryItself()
    {
        Assert.True(Matches("some/parent/child/**", "some/parent/child/file.ext"));
        Assert.False(Matches("some/parent/child/**", "some/parent/child/"));
    }

    [Fact]
    public void DotOptionLetsStarMatchDotfiles()
    {
        // dot:true：* 匹配 dotfile（对齐 fd 的 glob 行为）。
        Assert.True(Matches("src/*", "src/.hidden.ts"));
    }

    [Fact]
    public void NoCaseMatchesMixedCasePathsForLowercasePattern()
    {
        // 全小写模式 smart case 关闭大小写敏感。
        Assert.True(Matches("src/*.ts", "src/ReadMe.TS"));
        // 含大写的模式大小写敏感。
        Assert.False(Matches("src/*.TS", "src/ReadMe.ts"));
    }

    [Fact]
    public void WindowsPathSeparatorInFdOutputIsMatched()
    {
        // Platform=win32 使 Minimatch 把输入路径里的 \ 当作 /（fd 在 Windows 的输出）。
        Assert.True(Matches(@"src\**\*.ts", @"src\foo\bar\example.spec.ts"));
    }

    [Fact]
    public void LeadingGlobstarMatchesFdAbsolutePaths()
    {
        // 上游 #11972：Windows 路径模式下 fd 空 pattern 列出的是绝对路径，
        // 前导 **/ 正是为命中任意前导绝对/相对目录前缀（端口生产接线用原始行匹配）。
        var abs = "C:/Users/me/project/src/foo/bar/example.spec.ts";
        Assert.True(Matches("src/**/*.ts", abs));
        Assert.True(Matches("src/**/*.ts", "C:/Users/me/project/src/direct.spec.ts"));
        Assert.False(Matches("src/*.ts", abs));
    }
}
