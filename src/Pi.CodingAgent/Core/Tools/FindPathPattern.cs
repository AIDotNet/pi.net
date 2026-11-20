using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// find 工具路径模式（含 <c>/</c> 的 glob）的匹配决策。
/// <para>
/// 上游 #12066：路径模式相对搜索目录锚定（<c>src/*.ts</c> 不再命中 <c>pkg/src/b.ts</c>，
/// 需要 <c>**/src/*.ts</c>），与 grep 的 <c>glob</c> 一致。POSIX 上 fd 用绝对路径匹配，
/// 因此把转义后的搜索路径作为前缀；Windows 上 fd 只列全部条目，端口用 Minimatch 对
/// 「相对搜索路径的结果」匹配。绝对模式仍按绝对路径匹配。
/// </para>
/// 纯叶子，可跨平台单元测试（用 <paramref name="isWindows"/> 注入）。
/// </summary>
public static class FindPathPattern
{
    public sealed record Decision(
        string EffectivePattern,
        bool IsPathPattern,
        bool IsRelativePathPattern,
        bool UseWindowsMatcher,
        string? MatchPattern,
        bool NoCase);

    /// <summary>逐字对齐上游 <c>find.ts</c> 的 <c>effectivePattern</c> / <c>windowsPathMatcher</c> 计算。</summary>
    public static Decision For(string pattern, bool isWindows, string searchPath = "")
    {
        // Windows 把 \ 当路径分隔符，规范化为 /（#9262）；POSIX 上 \ 是 glob 转义符，保留。
        var effective = isWindows ? pattern.Replace("\\", "/", StringComparison.Ordinal) : pattern;
        // fd 的完整路径永远不含 "/./"，"./src/*.ts" 会一条都找不到（#12020）。
        effective = Regex.Replace(effective, "^(?:\\./)+", "");

        var isPathPattern = effective.Contains('/');
        // smart case：用户模式全小写才忽略大小写，不受搜索路径里的字母影响。
        var noCase = effective == effective.ToLowerInvariant();
        var isAbsolute = effective.StartsWith('/') || (effective.Length >= 3
            && char.IsAsciiLetter(effective[0]) && effective[1] == ':' && effective[2] == '/');
        var isRelativePathPattern = isPathPattern && !isAbsolute;

        var fdPattern = effective;
        if (isRelativePathPattern && !isWindows && searchPath.Length > 0)
        {
            // fd 匹配绝对路径，所以把 glob 转义后的搜索路径作为前缀（#12066）。
            var root = Regex.Replace(searchPath.TrimEnd('/'), @"[\\*?\[\]{}]", "\\$&");
            fdPattern = $"{root}/{effective}";
        }

        var useWindowsMatcher = isWindows && isPathPattern;
        // minimatch 的 'src/**' 会连 'src' 自身一起匹配，fd 不会；结尾 /** 补 /*。
        var matchPattern = effective.EndsWith("/**", StringComparison.Ordinal) ? effective + "/*" : effective;

        return new Decision(
            fdPattern,
            isPathPattern,
            isRelativePathPattern,
            useWindowsMatcher,
            useWindowsMatcher ? matchPattern : null,
            noCase);
    }

    /// <summary>
    /// 构造 Windows 路径匹配器，选项逐字对齐上游 <c>new Minimatch(..., { dot, noext, nocase })</c>。
    /// </summary>
    public static Minimatch CreateWindowsMatcher(Decision decision)
        => new(decision.MatchPattern!, new GlobOptions
        {
            Dot = true,
            NoExt = true,
            NoCase = decision.NoCase,
            Platform = "win32",
        });

    /// <summary>fd 输出的原始行是否命中路径模式（相对模式先相对化，绝对模式按原行）。</summary>
    public static bool Matches(Minimatch matcher, string line, Decision decision, string searchPath)
        => matcher.Match(decision.IsRelativePathPattern && searchPath.Length > 0
            ? FindTool.RelativizeFindResultPath(line, searchPath)
            : line);
}
