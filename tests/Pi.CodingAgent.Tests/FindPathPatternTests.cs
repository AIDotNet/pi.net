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
        Assert.StartsWith("src/", d.EffectivePattern);
        Assert.NotNull(d.MatchPattern);
    }

    [Fact]
    public void PathPatternIsAnchoredWithoutLeadingGlobstar()
    {
        // #12066：不再补 **/，路径模式相对搜索目录。
        Assert.Equal("src/**/*.ts", FindPathPattern.For("src/**/*.ts", isWindows: true).MatchPattern);
        Assert.Equal("src/*.ts", FindPathPattern.For("src/*.ts", isWindows: true).MatchPattern);
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
        Assert.Equal("some/parent/child/**/*",
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
        Assert.Equal("src/**/*.ts", d.EffectivePattern);
        // 有搜索路径时把转义后的搜索路径作为前缀（fd 匹配绝对路径）。
        Assert.Equal("/tmp/app/src/**/*.ts",
            FindPathPattern.For("src/**/*.ts", isWindows: false, "/tmp/app").EffectivePattern);
        // POSIX 上 \ 是 glob 转义符，不被规范化。
        Assert.Equal(@"src\/**/*.ts", FindPathPattern.For(@"src\/**/*.ts", isWindows: false).EffectivePattern);
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
        FindPathPattern.Matches(Matcher(pattern), path, FindPathPattern.For(pattern, isWindows: true), "");

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
    public void RelativePatternMatchesOnlyBelowTheSearchPath()
    {
        // #12066：Windows 上改为对「相对搜索路径的结果」匹配；祖先目录里同名文件夹不再命中。
        var decision = FindPathPattern.For("src/**/*.ts", isWindows: true, "C:/app");
        var matcher = FindPathPattern.CreateWindowsMatcher(decision);

        Assert.True(FindPathPattern.Matches(matcher, "C:/app/src/direct.spec.ts", decision, "C:/app"));
        Assert.True(FindPathPattern.Matches(matcher, "C:/app/src/foo/bar/x.spec.ts", decision, "C:/app"));
        // 搜索路径上方还有 C:/src，旧实现会误命中。
        Assert.False(FindPathPattern.Matches(matcher, "C:/src/b.spec.ts", decision, "C:/app"));
        // **/src/*.ts 才匹配任意层级的 src 文件夹。
        var anywhere = FindPathPattern.For("**/src/*.ts", isWindows: true, "C:/app");
        Assert.True(FindPathPattern.Matches(
            FindPathPattern.CreateWindowsMatcher(anywhere), "C:/app/pkg/src/b.ts", anywhere, "C:/app"));
    }
}
