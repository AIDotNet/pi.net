using Pi.Ai.Types;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// 初始提示词组装 — TS `cli/initial-message.ts`（43 行）。
//
// 非交互模式（print/json/rpc）把 stdin 内容、@file 文本和第一条 CLI 消息
// 拼成一条初始提示词；剩下的消息继续走普通入队路径。TS 直接改
// `parsed.messages`（shift 掉第一条），端口保持同样的原地修改语义。
// ============================================================================

/// <summary>对应 TS <c>InitialMessageInput</c>。</summary>
public sealed record InitialMessageInput
{
    public required Args Parsed { get; init; }

    public string? FileText { get; init; }

    public IReadOnlyList<ImageContent>? FileImages { get; init; }

    public string? StdinContent { get; init; }
}

/// <summary>对应 TS <c>InitialMessageResult</c>。</summary>
public sealed record InitialMessageResult
{
    public string? InitialMessage { get; init; }

    public IReadOnlyList<ImageContent>? InitialImages { get; init; }
}

/// <summary>对应 TS <c>buildInitialMessage</c>。</summary>
public static class InitialMessages
{
    public static InitialMessageResult Build(InitialMessageInput input)
    {
        var parts = new List<string>();
        if (input.StdinContent is not null)
        {
            parts.Add(input.StdinContent);
        }

        // TS `if (fileText)` — 空串为假，跳过。
        if (!string.IsNullOrEmpty(input.FileText))
        {
            parts.Add(input.FileText);
        }

        if (input.Parsed.Messages.Count > 0)
        {
            parts.Add(input.Parsed.Messages[0]);
            input.Parsed.Messages.RemoveAt(0);
        }

        return new InitialMessageResult
        {
            InitialMessage = parts.Count > 0 ? string.Concat(parts) : null,
            InitialImages = input.FileImages is { Count: > 0 } images ? images : null,
        };
    }
}
