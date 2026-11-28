using System.Security.Cryptography;
using System.Text;

namespace Pi.Tui;

/// <summary>
/// Program Resume Protocol（OSC 7577）：程序告诉终端「重启后用什么命令把自己带回来」。
/// 报告用终端注入环境变量的 token 做 SHA-256 认证，避免经过终端的普通输出（cat/日志/ssh）
/// 冒注册命令。对应 TS <c>tui/program-resume.ts</c>。
/// </summary>
public sealed record ProgramResumeCommand(IReadOnlyList<string> Argv, string? Cwd = null);

/// <summary>报告身份：终端注入的 token、进程 id、Unix 秒级时间。</summary>
public sealed record ProgramResumeSender(string Token, int Pid, long Time);

public static class ProgramResume
{
    /// <summary>终端把 token 交给第一个进程的环境变量名。</summary>
    public const string TokenEnvironmentVariable = "OSC7577_TOKEN";

    private const int MaxSequenceBytes = 2048;
    private const int MaxArgvElements = 64;

    /// <summary>从环境取出 token 并移除：报告方绝不能把 token 传给子进程。</summary>
    public static string? TakeToken(IDictionary<string, string?>? env = null)
    {
        var token = env is not null
            ? env.TryGetValue(TokenEnvironmentVariable, out var supplied) ? supplied : null
            : Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrEmpty(token)) return null;
        if (env is not null) env.Remove(TokenEnvironmentVariable);
        Environment.SetEnvironmentVariable(TokenEnvironmentVariable, null);
        return token;
    }

    /// <summary>
    /// 编码一条报告。命令含控制字符、相对程序名/cwd 或超过长度限制时返回 null，
    /// 调用方应改发 <see cref="FormatClear"/> 以免终端保留旧命令。
    /// </summary>
    public static string? Format(ProgramResumeCommand command, ProgramResumeSender sender)
    {
        if (!IsReportable(command)) return null;
        var pairs = new List<string> { $"argv={string.Join(",", command.Argv.Select(EncodeText))}" };
        if (command.Cwd is not null) pairs.Add($"cwd={EncodeText(command.Cwd)}");
        var sequence = FormatReport(pairs, sender);
        return Encoding.UTF8.GetByteCount(sequence) <= MaxSequenceBytes ? sequence : null;
    }

    /// <summary>清除本进程在终端注册的命令。</summary>
    public static string FormatClear(ProgramResumeSender sender) => FormatReport(["clear=1"], sender);

    private static string FormatReport(IEnumerable<string> pairs, ProgramResumeSender sender)
    {
        var body = string.Join(":", new[] { $"pid={sender.Pid}", $"time={sender.Time}" }.Concat(pairs));
        var mac = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{body}:{sender.Token}")))
            .ToLowerInvariant();
        return $"\x1b]7577;{body}:mac={mac}\x1b\\";
    }

    private static bool IsReportable(ProgramResumeCommand command)
    {
        if (command.Argv.Count == 0 || command.Argv.Count > MaxArgvElements) return false;
        var program = command.Argv[0];
        if (program.Length == 0) return false;
        var isBareName = !program.StartsWith('-') && !program.Contains('/') && !program.Contains('\\');
        if (!isBareName && !Path.IsPathRooted(program)) return false;
        if (command.Cwd is not null && !(Path.IsPathRooted(command.Cwd) && IsReportableText(command.Cwd))) return false;
        return command.Argv.All(IsReportableText);
    }

    private static bool IsReportableText(string text)
    {
        if (text.Any(char.IsControl)) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    private static string EncodeText(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
