namespace Pi.Durable;

/// <summary>事务在首次表写之后读表。对应 TS <c>ReadAfterWrite</c>。</summary>
public sealed class ReadAfterWrite : Exception
{
    public ReadAfterWrite(string method)
        : base($"Tx.{method}() cannot read tables after the first table write")
    {
    }
}

/// <summary>storage 在任何持久化效果前拒绝了批次；所属 Session 可安全继续。对应 TS <c>StorageRejected</c>。</summary>
public sealed class StorageRejected : Exception
{
    public StorageRejected(string message)
        : base(message)
    {
    }

    public StorageRejected(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Harness 以只读方式打开（<see cref="Harness.HarnessOptions.ReadOnly"/>）：带写的提交、或会运行任务的调用被拒。
/// 什么都没写，Harness 仍可读取与观察。对应 TS <c>ReadOnlySession</c>。
/// </summary>
public sealed class ReadOnlySession : Exception
{
    public ReadOnlySession(string what)
        : base($"Read-only Harness: {what} refused")
    {
    }
}

/// <summary>
/// 带 <c>whenIdle: "reject"</c> 的输入到达空闲对话且未被受理：在任何写之前就拒绝，因此它从不启动一次运行。
/// 对应 TS <c>ConversationIdle</c>。
/// </summary>
public sealed class ConversationIdle : Exception
{
    /// <summary>空闲的对话 ID。</summary>
    public Types.ConversationId ConversationId { get; }

    public ConversationIdle(Types.ConversationId conversationId)
        : base($"Conversation {conversationId.Value} is idle")
        => ConversationId = conversationId;
}

/// <summary>提交到达忙碌对话且未被受理。对应 TS <c>ConversationBusy</c>。</summary>
public sealed class ConversationBusy : Exception
{
    /// <summary>忙碌的对话 ID。</summary>
    public Types.ConversationId ConversationId { get; }

    public ConversationBusy(Types.ConversationId conversationId)
        : base($"Conversation {conversationId.Value} is busy")
        => ConversationId = conversationId;
}
