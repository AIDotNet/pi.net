namespace Pi.Ai.Api;

/// <summary>
/// 会话级「strict 工具集被 Claude 以容量上限拒绝」的记忆。对应 TS 1.5.1
/// <c>api/strict-tool-limits.ts</c>（#11626）：同一会话后续请求一开始就按非 strict 发送
/// prefer 工具，避免每轮都白挨一次 400。Anthropic 与 Bedrock 共用同一套 Claude 限额。
/// </summary>
public static class StrictToolLimits
{
    private const int MaxSessions = 10_000;

    /// <summary>Claude（Anthropic API 与 Bedrock 一致）拒绝超限 strict 工具集的文案。</summary>
    private static readonly string[] LimitErrorMessages =
    [
        "The compiled grammar is too large",
        "Too many strict tools (",
        "Schemas contains too many parameters with union types (",
    ];

    private static readonly object Gate = new();
    // LinkedList 保持插入顺序，Dictionary 支持按 sessionId 定向删除，二者合起来等价于 TS 的 Set。
    private static readonly LinkedList<string> Order = new();
    private static readonly Dictionary<string, LinkedListNode<string>> Nodes = new(StringComparer.Ordinal);

    static StrictToolLimits()
    {
        // 会话结束（或进程退出）时释放记忆，对应 TS registerSessionResourceCleanup。
        SessionResources.Register(sessionId =>
        {
            lock (Gate)
            {
                if (sessionId is null)
                {
                    Order.Clear();
                    Nodes.Clear();
                    return;
                }
                if (Nodes.Remove(sessionId, out var node)) Order.Remove(node);
            }
        });
    }

    /// <summary>这个错误文案是不是在说「请求的 strict 工具集超出了 provider 的容量」。</summary>
    public static bool IsStrictToolLimitErrorMessage(string message)
        => LimitErrorMessages.Any(entry => message.Contains(entry, StringComparison.Ordinal));

    /// <summary>记下某会话的 strict 工具集已被拒；无 session 时不记，最多保留 MaxSessions 个。</summary>
    public static void MarkExceeded(string? sessionId)
    {
        if (sessionId is null) return;
        lock (Gate)
        {
            if (Nodes.ContainsKey(sessionId)) return;
            var node = Order.AddLast(sessionId);
            Nodes.Add(sessionId, node);
            if (Order.Count > MaxSessions)
            {
                var oldest = Order.First!;
                Order.RemoveFirst();
                Nodes.Remove(oldest.Value);
            }
        }
    }

    /// <summary>该会话此前是否有过 strict 工具集被拒。</summary>
    public static bool HasExceeded(string? sessionId)
    {
        if (sessionId is null) return false;
        lock (Gate) return Nodes.ContainsKey(sessionId);
    }
}
