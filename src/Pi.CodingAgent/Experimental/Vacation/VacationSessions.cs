namespace Pi.CodingAgent.Experimental.Vacation;

/// <summary>
/// 选一个 Vacation 会话目录。逻辑与 Durable 版是同一份代码（上游两个 <c>sessions.ts</c> 只差目录名与文案），
/// 已收敛到 <see cref="ExperimentalSessions"/>；这里只固定种类，保持既有调用点不变。
/// </summary>
public static class VacationSessions
{
    public static Task<ExperimentalSessionLocation> SelectAsync(
        string cwdInput,
        bool continueSession,
        string? agentDirectory = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
        => ExperimentalSessions.SelectAsync(
            ExperimentalSessionKind.Vacation,
            cwdInput,
            continueSession,
            agentDirectory,
            timeProvider,
            cancellationToken);
}
