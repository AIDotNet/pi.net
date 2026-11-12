using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// 上游 1.5.2 #11972 / #9262：Windows 上 find 工具路径模式（含 <c>/</c> 的 glob）的匹配决策。
/// fd 10.5.1 之前完全不识别 <c>--full-path</c> glob，10.5.1 起旧的 <c>[/\\]</c> 转义 workaround 又失效，
/// 而 pi 会下载最新版 fd。因此 Windows 路径模式不再交给 fd 的 <c>--full-path</c>，改为让 fd 列出全部
/// 条目（空 pattern），由端口用 <see cref="Minimatch"/> 逐行过滤，行为与 fd 版本无关。
/// 纯叶子，可跨平台单元测试（用 <paramref name="isWindows"/> 注入，不依赖真实进程/文件系统）。
/// </summary>
public static class FindPathPattern
{
    public sealed record Decision(
        string EffectivePattern,
        bool IsPathPattern,
        bool UseWindowsMatcher,
        string? MatchPattern,
        bool NoCase);

    /// <summary>逐字对齐上游 <c>find.ts</c> 的 <c>effectivePattern</c> / <c>windowsPathMatcher</c> 计算。</summary>
    public static Decision For(string pattern, bool isWindows)
    {
        // Windows 把 \ 当路径分隔符，规范化为 /（#9262）；POSIX 上 \ 是 glob 转义符，保留。
        var effective = isWindows ? pattern.Replace("\\", "/", StringComparison.Ordinal) : pattern;
        var isPathPattern = effective.Contains('/');

        // fd 在 --full-path 模式下拿绝对路径匹配，路径模式需前导 **/ 才能命中（#3302）。
        if (isPathPattern
            && !effective.StartsWith('/')
            && !effective.StartsWith("**/", StringComparison.Ordinal)
            && effective != "**")
        {
            effective = "**/" + effective;
        }

        var useWindowsMatcher = isWindows && isPathPattern;
        // minimatch 的 'src/**' 会连 'src' 自身一起匹配，fd 不会；结尾 /** 补 /*。
        var matchPattern = effective.EndsWith("/**", StringComparison.Ordinal) ? effective + "/*" : effective;
        // smart case：全小写模式大小写不敏感（对齐 fd 的默认行为）。
        var noCase = effective == effective.ToLowerInvariant();

        return new Decision(
            effective,
            isPathPattern,
            useWindowsMatcher,
            useWindowsMatcher ? matchPattern : null,
            noCase);
    }

    /// <summary>
    /// 构造 Windows 路径匹配器，选项逐字对齐上游 <c>new Minimatch(..., { dot, noext, nocase })</c>：
    /// <c>*</c> 匹配 dotfile、<c>+(a|b)</c> 视为字面量、全小写模式 smart case、platform=win32
    /// （使 <c>\</c> 被当作路径分隔符，与 fd 在 Windows 的输出一致）。
    /// </summary>
    public static Minimatch CreateWindowsMatcher(Decision decision)
        => new(decision.MatchPattern!, new GlobOptions
        {
            Dot = true,
            NoExt = true,
            NoCase = decision.NoCase,
            Platform = "win32",
        });

    /// <summary>fd 输出的原始行是否命中路径模式（对齐上游 <c>windowsPathMatcher.match(line)</c>）。</summary>
    public static bool Matches(Minimatch matcher, string line) => matcher.Match(line);
}
