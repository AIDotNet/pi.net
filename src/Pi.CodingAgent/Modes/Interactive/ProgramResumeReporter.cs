using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// 告诉终端如何在终端重启后恢复当前会话（OSC 7577）。仅在终端注入 token 时创建。
/// 终端每个进程只持有一条命令：命令变化时替换，会话没有文件可恢复或用户退出时清除。
/// 通过终端结束 pi（SIGHUP/SIGTERM）时**保留**，因为那正是终端需要它的时候。
/// 对应 TS <c>modes/interactive/program-resume-reporter.ts</c>。
/// </summary>
public sealed class ProgramResumeReporter
{
    private readonly string _token;
    private readonly Func<ITerminal> _getTerminal;
    private readonly Func<ProgramResumeCommand?> _getResumeCommand;
    private string? _reported;

    public ProgramResumeReporter(string token, Func<ITerminal> getTerminal,
        Func<ProgramResumeCommand?> getResumeCommand)
    {
        _token = token;
        _getTerminal = getTerminal;
        _getResumeCommand = getResumeCommand;
    }

    /// <summary>命令变化时才报告。</summary>
    public void Report()
    {
        var command = _getResumeCommand();
        if (Fingerprint(command) != _reported) Send(command);
    }

    /// <summary>会话首次拿到可恢复文件后报告一次。</summary>
    public void ReportIfNone()
    {
        if (_reported is null) Report();
    }

    /// <summary>即使命令没变也重报（pi 挂起期间可能有其它程序替换了终端里的命令）。</summary>
    public void ReportAgain()
    {
        var command = _getResumeCommand();
        if (command is not null || _reported is not null) Send(command);
    }

    /// <summary>用户正常退出时清除。</summary>
    public void Clear()
    {
        if (_reported is not null) Send(null);
    }

    private void Send(ProgramResumeCommand? command)
    {
        var sender = new ProgramResumeSender(
            _token, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var sequence = command is not null
            ? ProgramResume.Format(command, sender) ?? ProgramResume.FormatClear(sender)
            : ProgramResume.FormatClear(sender);
        _reported = Fingerprint(command);
        _getTerminal().Write(sequence);
    }

    private static string? Fingerprint(ProgramResumeCommand? command)
        => command is null ? null
            : $"{string.Join("\0", command.Argv)}\0{command.Cwd}";
}
