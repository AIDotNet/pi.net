using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

// ============================================================================
// core/compaction/compaction.ts — 长会话上下文压缩（4e-2d）
//
// 纯函数处理压缩逻辑；I/O 由 SessionManager 负责，压缩后由调用方重载会话。
// ============================================================================

/// <summary>CompactionEntry.details 里存的文件跟踪信息。对应 TS <c>CompactionDetails</c>。</summary>
public sealed record CompactionDetails(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

/// <summary>压缩设置（settings.jsonl）。对应 TS <c>CompactionSettings</c>。</summary>
public sealed record CompactionSettings(bool Enabled, long ReserveTokens, long KeepRecentTokens);

/// <summary>压缩的默认设置。对应 TS <c>DEFAULT_COMPACTION_SETTINGS</c>。</summary>
public static class CompactionDefaults
{
    public static readonly CompactionSettings DefaultSettings = new(true, 16384, 20000);
}

/// <summary>compact() 的结果 —— SessionManager 保存时补 uuid/parentId。对应 TS <c>CompactionResult</c>。</summary>
public sealed record CompactionResult(
    string Summary,
    string FirstKeptEntryId,
    long TokensBefore,
    long? EstimatedTokensAfter,
    Usage? Usage,
    object? Details);

/// <summary>上下文用量估算。对应 TS <c>ContextUsageEstimate</c>。</summary>
public sealed record ContextUsageEstimate(
    long Tokens,
    long UsageTokens,
    long TrailingTokens,
    int? LastUsageIndex);

/// <summary>切分点结果。对应 TS <c>CutPointResult</c>。</summary>
public sealed record CutPointResult(
    int FirstKeptEntryIndex,
    int TurnStartIndex,
    bool IsSplitTurn);

/// <summary>供扩展使用的压缩准备数据。对应 TS <c>CompactionPreparation</c>。</summary>
public sealed record CompactionPreparation(
    string FirstKeptEntryId,
    IReadOnlyList<ChatMessage> MessagesToSummarize,
    IReadOnlyList<ChatMessage> TurnPrefixMessages,
    bool IsSplitTurn,
    long TokensBefore,
    string? PreviousSummary,
    FileOperations FileOps,
    CompactionSettings Settings);

/// <summary>上下文压缩。对应 TS <c>core/compaction/compaction.ts</c>。</summary>
public static class Compaction
{
    // ───────────────────────────────────────────────────────────────────────
    // Token 计算
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 usage 计算总上下文 token：优先用原生 totalTokens，缺省回退到分量求和。
    /// 对应 TS <c>calculateContextTokens</c>。
    /// </summary>
    public static long CalculateContextTokens(Usage usage) =>
        usage.TotalTokens != 0
            ? usage.TotalTokens
            : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    /// <summary>
    /// 取助手消息上的有效 usage；跳过 aborted/error/全零（无有效数据）。对应 TS <c>getAssistantUsage</c>。
    /// </summary>
    private static Usage? GetAssistantUsage(ChatMessage message)
    {
        if (message is AssistantMessage assistant
            && assistant.StopReason != StopReason.Aborted
            && assistant.StopReason != StopReason.Error
            && assistant.UsageStats is { } usage
            && CalculateContextTokens(usage) > 0)
        {
            return usage;
        }

        return null;
    }

    /// <summary>从会话条目里找最后一个有效助手 usage。对应 TS <c>getLastAssistantUsage</c>。</summary>
    public static Usage? GetLastAssistantUsage(IReadOnlyList<SessionEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                var usage = GetAssistantUsage(assistant);
                if (usage is not null) return usage;
            }
        }

        return null;
    }

    private static (Usage Usage, int Index)? GetLastAssistantUsageInfo(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var usage = GetAssistantUsage(messages[index]);
            if (usage is not null) return (usage, index);
        }

        return null;
    }

    /// <summary>
    /// 从消息估算上下文 token：有最后一个助手 usage 时以它为准，之后的尾部消息用估算。
    /// 对应 TS <c>estimateContextTokens</c>。
    /// </summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        var usageInfo = GetLastAssistantUsageInfo(messages);

        if (usageInfo is null)
        {
            long estimated = 0;
            foreach (var message in messages)
            {
                estimated += EstimateTokens(message);
            }

            return new ContextUsageEstimate(estimated, 0, estimated, null);
        }

        var (usage, index) = usageInfo.Value;
        var usageTokens = CalculateContextTokens(usage);
        long trailingTokens = 0;
        for (var i = index + 1; i < messages.Count; i++)
        {
            trailingTokens += EstimateTokens(messages[i]);
        }

        return new ContextUsageEstimate(usageTokens + trailingTokens, usageTokens, trailingTokens, index);
    }

    /// <summary>
    /// 估算投影上下文，不信任在其后的编辑/压缩之前捕获的 usage。
    /// 对应 TS <c>estimateProjectedContextTokens</c>。
    /// </summary>
    public static ContextUsageEstimate EstimateProjectedContextTokens(
        SessionProjection projection, IReadOnlyList<SessionEntry> branchEntries)
    {
        var estimate = EstimateContextTokens(projection.Messages);
        if (estimate.LastUsageIndex is { } lastUsageIndex)
        {
            long projectedMessageIndex = 0;
            string? usageEntryId = null;
            foreach (var entry in projection.Entries)
            {
                var nextMessageIndex = projectedMessageIndex + entry.Messages.Count;
                if (lastUsageIndex < nextMessageIndex)
                {
                    usageEntryId = entry.SourceEntry.Id;
                    break;
                }

                projectedMessageIndex = nextMessageIndex;
            }

            var usageEntryIndex = usageEntryId is not null
                ? branchEntries.ToList().FindIndex(entry => entry.Id == usageEntryId)
                : -1;
            var latestInvalidatingEntryIndex = -1;
            for (var i = branchEntries.Count - 1; i >= 0; i--)
            {
                if (branchEntries[i] is ContextEditEntry or CompactionEntry)
                {
                    latestInvalidatingEntryIndex = i;
                    break;
                }
            }

            if (usageEntryIndex > latestInvalidatingEntryIndex) return estimate;
        }

        var currentSystem = Transcript.GetCurrentSystemMessage(projection.Messages);
        var tokens = currentSystem is not null ? EstimateTokens(currentSystem) : 0;
        foreach (var message in projection.Messages)
        {
            if (message is not SystemMessage) tokens += EstimateTokens(message);
        }

        return new ContextUsageEstimate(tokens, 0, tokens, null);
    }

    /// <summary>按上下文用量判断是否应触发压缩。对应 TS <c>shouldCompact</c>。</summary>
    public static bool ShouldCompact(long contextTokens, long contextWindow, CompactionSettings settings)
    {
        if (!settings.Enabled) return false;
        return contextTokens > contextWindow - settings.ReserveTokens;
    }

    // ───────────────────────────────────────────────────────────────────────
    // 切分点检测
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>图片块估算字符数。对应 TS <c>ESTIMATED_IMAGE_CHARS</c>。</summary>
    private const int EstimatedImageChars = 4800;

    private static long EstimateTextAndImageContentChars(MessageContent? content) => content switch
    {
        MessageContent.Text text => text.Value.Length,
        MessageContent.Blocks blocks => blocks.Value.Sum(EstimateBlockChars),
        _ => 0,
    };

    private static long EstimateBlockChars(ContentBlock block) => block switch
    {
        TextContent text => text.Text.Length,
        ImageContent => EstimatedImageChars,
        _ => 0,
    };

    /// <summary>
    /// 用 chars/4 启发式估算单条消息的 token（保守，偏高）。对应 TS <c>estimateTokens</c>。
    /// </summary>
    public static long EstimateTokens(ChatMessage message)
    {
        long chars;
        switch (message)
        {
            case SystemMessage system:
            {
                chars = EstimateTextAndImageContentChars(system.Content);
                if (system.Sections is not null)
                {
                    foreach (var section in system.Sections.Values)
                    {
                        if (section is not null) chars += section.Length;
                    }
                }

                if (system.ToolsAdded is not null)
                {
                    chars += CompatShims.SerializeToPlainText(system.ToolsAdded).Length;
                }

                return (chars + 3) / 4;
            }
            case UserMessage user:
                chars = EstimateTextAndImageContentChars(user.Content);
                return (chars + 3) / 4;
            case AssistantMessage assistant:
            {
                chars = 0;
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent text:
                            chars += text.Text.Length;
                            break;
                        case ThinkingContent thinking:
                            chars += thinking.Thinking.Length;
                            break;
                        case ToolCallContent toolCall:
                            chars += toolCall.Name.Length + CompatShims.SerializeToPlainText(toolCall.Arguments).Length;
                            break;
                    }
                }

                return (chars + 3) / 4;
            }
            case CustomMessage custom:
                chars = EstimateTextAndImageContentChars(custom.Content);
                return (chars + 3) / 4;
            case ToolResultMessage toolResult:
                chars = toolResult.Content.Sum(EstimateBlockChars);
                return (chars + 3) / 4;
            case BashExecutionMessage bash:
                chars = bash.Command.Length + bash.Output.Length;
                return (chars + 3) / 4;
            case BranchSummaryMessage branchSummary:
                chars = branchSummary.Summary.Length;
                return (chars + 3) / 4;
            case CompactionSummaryMessage compactionSummary:
                chars = compactionSummary.Summary.Length;
                return (chars + 3) / 4;
            default:
                // hookMessage 等未列角色：TS 的 switch 不穷尽 → 0。
                return 0;
        }
    }

    private static bool IsCutPointMessage(ChatMessage message) => message switch
    {
        UserMessage or AssistantMessage or BashExecutionMessage or CustomMessage
            or BranchSummaryMessage or CompactionSummaryMessage => true,
        _ => false,
    };

    private static bool IsTurnStartMessage(ChatMessage message) => message switch
    {
        UserMessage or BashExecutionMessage or CustomMessage or BranchSummaryMessage or CompactionSummaryMessage => true,
        _ => false,
    };

    private static bool IsTurnStartEntry(SessionEntry entry)
    {
        if (entry is CompactionEntry) return false;
        return SessionEntries.SessionEntryToContextMessages(entry).Any(IsTurnStartMessage);
    }

    /// <summary>
    /// 找有效切分点：上下文可见的 user/assistant 类消息索引。绝不在 tool result 处切
    /// （它们必须跟在工具调用后）。对应 TS <c>findValidCutPoints</c>。
    /// </summary>
    private static List<int> FindValidCutPoints(IReadOnlyList<SessionEntry> entries, int startIndex, int endIndex)
    {
        var cutPoints = new List<int>();
        for (var i = startIndex; i < endIndex; i++)
        {
            if (entries[i] is CompactionEntry) continue;
            if (SessionEntries.SessionEntryToContextMessages(entries[i]).Any(IsCutPointMessage))
            {
                cutPoints.Add(i);
            }
        }

        return cutPoints;
    }

    /// <summary>
    /// 找包含给定条目索引的回合的起始 user 消息；找不到返回 -1。对应 TS <c>findTurnStartIndex</c>。
    /// </summary>
    public static int FindTurnStartIndex(IReadOnlyList<SessionEntry> entries, int entryIndex, int startIndex)
    {
        for (var i = entryIndex; i >= startIndex; i--)
        {
            if (IsTurnStartEntry(entries[i])) return i;
        }

        return -1;
    }

    /// <summary>
    /// 在会话条目里找保持约 <paramref name="keepRecentTokens"/> 的切分点。从最新往回走、累计
    /// 估算消息大小，达到预算即切。可在 user 或 assistant 处切（绝不在 tool result）。
    /// 对应 TS <c>findCutPoint</c>。
    /// </summary>
    public static CutPointResult FindCutPoint(
        IReadOnlyList<SessionEntry> entries, int startIndex, int endIndex, long keepRecentTokens)
    {
        var cutPoints = FindValidCutPoints(entries, startIndex, endIndex);

        if (cutPoints.Count == 0)
        {
            return new CutPointResult(startIndex, -1, false);
        }

        // 从最新往回走，累计估算消息大小。
        long accumulatedTokens = 0;
        var cutIndex = cutPoints[0];

        for (var i = endIndex - 1; i >= startIndex; i--)
        {
            var messageTokens = SessionEntries.SessionEntryToContextMessages(entries[i])
                .Sum(EstimateTokens);
            if (messageTokens == 0) continue;
            accumulatedTokens += messageTokens;

            if (accumulatedTokens >= keepRecentTokens)
            {
                // 优先取本条目之后最近的切分点。若尾部 tool result 单独超预算，
                // 保留其前面的 assistant 工具调用而不是回退到首条消息。
                var candidateCut = cutPoints.Find(candidate => candidate >= i);
                cutIndex = candidateCut >= 0 ? candidateCut : cutPoints[^1];
                break;
            }
        }

        // 从 cutIndex 往回包含不影响上下文的相邻元数据条目。
        while (cutIndex > startIndex)
        {
            var previous = entries[cutIndex - 1];
            if (previous is CompactionEntry
                || SessionEntries.SessionEntryToContextMessages(previous).Count > 0)
            {
                break;
            }

            cutIndex--;
        }

        var cutEntry = entries[cutIndex];
        var startsTurn = IsTurnStartEntry(cutEntry);
        var turnStartIndex = startsTurn ? -1 : FindTurnStartIndex(entries, cutIndex, startIndex);

        return new CutPointResult(cutIndex, turnStartIndex, !startsTurn && turnStartIndex != -1);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 摘要提示词
    // ───────────────────────────────────────────────────────────────────────

    private const string SummarizationPrompt =
        "The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.\n\nUse this EXACT format:\n\n## Goal\n[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]\n\n## Constraints & Preferences\n- [Any constraints, preferences, or requirements mentioned by user]\n- [Or \"(none)\" if none were mentioned]\n\n## Progress\n### Done\n- [x] [Completed tasks/changes]\n\n### In Progress\n- [ ] [Current work]\n\n### Blocked\n- [Issues preventing progress, if any]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale]\n\n## Next Steps\n1. [Ordered list of what should happen next]\n\n## Critical Context\n- [Any data, examples, or references needed to continue]\n- [Or \"(none)\" if not applicable]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    private const string UpdateSummarizationInstructions =
        "Update the existing structured summary with new information. RULES:\n- PRESERVE all existing information from the previous summary\n- ADD new progress, decisions, and context from the new messages\n- UPDATE the Progress section: move items from \"In Progress\" to \"Done\" when completed\n- UPDATE \"Next Steps\" based on what was accomplished\n- PRESERVE exact file paths, function names, and error messages\n- If something is no longer relevant, you may remove it\n\nUse this EXACT format:\n\n## Goal\n[Preserve existing goals, add new ones if the task expanded]\n\n## Constraints & Preferences\n- [Preserve existing, add new ones discovered]\n\n## Progress\n### Done\n- [x] [Include previously done items AND newly completed items]\n\n### In Progress\n- [ ] [Current work - update based on progress]\n\n### Blocked\n- [Current blockers - remove if resolved]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale] (preserve all previous, add new)\n\n## Next Steps\n1. [Update based on current state]\n\n## Critical Context\n- [Preserve important context, add new if needed]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    private const string UpdateSummarizationPrompt =
        "The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.\n\n" +
        UpdateSummarizationInstructions;

    private const string TurnPrefixSummarizationPrompt =
        "The messages above are earlier context from an ongoing conversation. Later messages are stored separately and do not need to be reconstructed.\n\nCreate a concise checkpoint of the user's request and the progress shown above. This checkpoint will be placed before the later messages so the conversation can continue with the necessary context.\n\n## Original Request\n[What did the user ask for?]\n\n## Progress So Far\n- [Key decisions and work completed in these messages]\n\n## Context Needed to Continue\n- [Information from these messages needed to understand the later work]\n\nOnly summarize information explicitly present above. Do not infer or recreate later messages.";

    /// <summary>
    /// 摘要响应无法安全落盘时返回错误消息；length 停止包含部分文本，不能成为会话检查点。
    /// 对应 TS <c>getSummarizationFailure</c>。
    /// </summary>
    public static string? GetSummarizationFailure(AssistantMessage response, string label)
    {
        if (response.StopReason == StopReason.Error)
        {
            return $"{label} failed: {(string.IsNullOrEmpty(response.ErrorMessage) ? "Unknown error" : response.ErrorMessage)}";
        }

        if (response.StopReason == StopReason.Length)
        {
            return $"{label} failed: generation hit the token cap and the summary is incomplete";
        }

        return null;
    }

    private static SimpleStreamOptions CreateSummarizationOptions(
        ModelSpec model, string? apiKey,
        IReadOnlyDictionary<string, string?>? headers, IReadOnlyDictionary<string, string>? env,
        CancellationToken? signal, string? thinkingLevel, string? sessionId)
    {
        // 不设 MaxTokens：摘要请求与普通回合一样由 provider 层按实际请求与模型输出上限定尺寸。
        // 原先按 reserveTokens 折算的上限会在思考占用大部分输出预算时让摘要直接撞 token 上限（上游 #9512）。
        var options = new SimpleStreamOptions
        {
            Signal = signal ?? default,
            ApiKey = apiKey,
            Headers = headers,
            Env = env,
            SessionId = sessionId,
        };
        if (model.Reasoning && thinkingLevel is not null && thinkingLevel != "off")
        {
            // TS `options.reasoning = thinkingLevel`：摘要请求沿用会话的思考档位。
            options = options with { Reasoning = thinkingLevel };
        }

        return options;
    }

    /// <summary>
    /// 所有压缩/分支摘要 LLM 调用的共同咽喉：包一层 <see cref="Retry.AssistantCallAsync"/>，
    /// 让瞬断（terminated、socket close）按配置的重试策略重试。确定性错误与中止立即返回。
    /// 对应 TS <c>completeSummarization</c>。
    /// </summary>
    public static async Task<AssistantMessage> CompleteSummarizationAsync(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions options,
        StreamFn? streamFn = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken signal = default)
    {
        // 一次性摘要不写提示缓存。复用调用方给的路由；无会话 id 的调用方（含分支摘要）拿新路由 id。
        var requestOptions = options with
        {
            CacheRetention = "none",
            SessionId = options.SessionId ?? Uuid.Uuidv7(),
        };
        return await Retry.AssistantCallAsync(
            async () => streamFn is not null
                ? await (await streamFn(model.ToModel(), context, requestOptions, signal)
                        .ConfigureAwait(false))
                    .WaitForDoneAsync(signal).ConfigureAwait(false)
                : await CompatApi.CompleteSimpleAsync(
                        model, context.Messages, CompatShims.ToDictionary(requestOptions), signal)
                    .ConfigureAwait(false),
            retry,
            signal,
            callbacks).ConfigureAwait(false);
    }

    /// <summary>独立摘要请求的 provider 上下文。对应 TS <c>buildSummarizationContext</c>。</summary>
    private static TranscriptContext BuildSummarizationContext(string promptText)
    {
        return Transcript.NormalizeContext(
            CompactionUtils.SummarizationSystemPrompt,
            null,
            [new UserMessage(MessageContent.BlocksOf([new TextContent(promptText)]),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
    }

    /// <summary>用 LLM 生成（或合并更新）会话摘要并返回 provider usage。对应 TS <c>generateSummaryWithUsage</c>。</summary>
    public static async Task<(string Text, Usage Usage)> GenerateSummaryWithUsageAsync(
        IReadOnlyList<ChatMessage> currentMessages,
        ModelSpec model,
        long reserveTokens,
        string? apiKey,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken signal = default,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        StreamFn? streamFn = null,
        IReadOnlyDictionary<string, string>? env = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        string? sessionId = null)
    {
        // reserveTokens 仅用于判定「何时开始压缩」，不再折算摘要的输出上限（上游 #9512）。
        _ = reserveTokens;

        var basePrompt = previousSummary is not null ? UpdateSummarizationPrompt : SummarizationPrompt;
        if (customInstructions is not null)
        {
            basePrompt = $"{basePrompt}\n\nAdditional focus: {customInstructions}";
        }

        // 序列化为文本避免模型试图继续对话。先转 LLM 消息（处理 bashExecution/custom 等自定义类型）。
        var llmMessages = AgentMessages.ConvertToLlm(currentMessages);
        var conversationText = CompactionUtils.SerializeConversation(llmMessages);

        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n";
        if (previousSummary is not null)
        {
            promptText += $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n";
        }

        promptText += basePrompt;

        var completionOptions = CreateSummarizationOptions(
            model, apiKey, headers, env, signal.CanBeCanceled ? signal : null, thinkingLevel, sessionId);

        var response = await CompleteSummarizationAsync(
            model,
            BuildSummarizationContext(promptText),
            completionOptions,
            streamFn,
            retry,
            callbacks,
            signal).ConfigureAwait(false);

        var failure = GetSummarizationFailure(response, "Summarization");
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        if (response.Content.Any(block => block is ToolCallContent))
        {
            throw new InvalidOperationException("Summarization attempted to call a tool");
        }

        return (Text.ContentText(response.Content), response.UsageStats ?? new Usage(0, 0));
    }

    /// <summary>用 LLM 生成会话摘要。对应 TS <c>generateSummary</c>。</summary>
    public static async Task<string> GenerateSummaryAsync(
        IReadOnlyList<ChatMessage> currentMessages,
        ModelSpec model,
        long reserveTokens,
        string? apiKey,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken signal = default,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        StreamFn? streamFn = null,
        IReadOnlyDictionary<string, string>? env = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        string? sessionId = null)
    {
        var (text, _) = await GenerateSummaryWithUsageAsync(
            currentMessages, model, reserveTokens, apiKey, headers, signal, customInstructions,
            previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId).ConfigureAwait(false);
        return text;
    }

    // ───────────────────────────────────────────────────────────────────────
    // 压缩准备（供扩展）
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>从消息与前次压缩条目提取文件操作。对应 TS <c>extractFileOperations</c>。</summary>
    private static FileOperations ExtractFileOperations(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<SessionEntry> entries, int prevCompactionIndex)
    {
        var fileOps = CompactionUtils.CreateFileOps();

        // 从前次压缩的 details 收集（仅 pi 生成的）。
        if (prevCompactionIndex >= 0
            && entries[prevCompactionIndex] is CompactionEntry { FromHook: not true, Details: { } detailsObject })
        {
            var details = DeserializeDetails(detailsObject);
            if (details is not null)
            {
                foreach (var file in details.ReadFiles) fileOps.Read.Add(file);
                foreach (var file in details.ModifiedFiles) fileOps.Edited.Add(file);
            }
        }

        foreach (var message in messages)
        {
            CompactionUtils.ExtractFileOpsFromMessage(message, fileOps);
        }

        return fileOps;
    }

    /// <summary>details 的两种线路形态（JsonObject / JsonElement）→ CompactionDetails。</summary>
    internal static CompactionDetails? DeserializeDetails(object detailsObject) => detailsObject switch
    {
        JsonObject obj => FromNode(obj),
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element
            => FromNode((JsonObject)JsonSerializerDeserializeNode(element)!),
        _ => null,
    };

    private static System.Text.Json.Nodes.JsonNode? JsonSerializerDeserializeNode(System.Text.Json.JsonElement element)
        => System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());

    private static CompactionDetails? FromNode(JsonObject obj)
    {
        var readFiles = StringArray(obj, "readFiles");
        var modifiedFiles = StringArray(obj, "modifiedFiles");
        if (readFiles is null && modifiedFiles is null) return null;
        return new CompactionDetails(readFiles ?? [], modifiedFiles ?? []);
    }

    private static IReadOnlyList<string>? StringArray(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonArray array
            ? array.Select(item => item is JsonValue { } value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(text => text is not null).Cast<string>().ToList()
            : null;

    /// <summary>压缩条目不产出消息；system 消息是 prompt 状态，由压缩条目承载回放。对应 TS <c>getMessagesFromProjectedEntryForCompaction</c>。</summary>
    private static IReadOnlyList<ChatMessage> GetMessagesFromProjectedEntryForCompaction(ProjectedSessionEntry entry)
    {
        if (entry.SourceEntry is CompactionEntry) return [];
        return entry.Messages.Where(message => message is not SystemMessage).ToList();
    }

    private static bool IsProjectedTurnStart(ProjectedSessionEntry entry)
    {
        if (entry.SourceEntry is CompactionEntry) return false;
        return entry.Messages.Any(IsTurnStartMessage);
    }

    private static int FindProjectedTurnStartIndex(
        IReadOnlyList<ProjectedSessionEntry> entries, int entryIndex, int startIndex)
    {
        for (var i = entryIndex; i >= startIndex; i--)
        {
            if (IsProjectedTurnStart(entries[i])) return i;
        }

        return -1;
    }

    private static CutPointResult FindProjectedCutPoint(
        IReadOnlyList<ProjectedSessionEntry> entries, int startIndex, int endIndex, long keepRecentTokens)
    {
        var cutPoints = new List<int>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var entry = entries[i];
            if (entry.SourceEntry is not CompactionEntry && entry.Messages.Any(IsCutPointMessage)) cutPoints.Add(i);
        }

        if (cutPoints.Count == 0)
        {
            return new CutPointResult(startIndex, -1, false);
        }

        long accumulatedTokens = 0;
        var exceededBudget = false;
        var cutIndex = cutPoints[0];
        for (var i = endIndex - 1; i >= startIndex; i--)
        {
            var messageTokens = entries[i].Messages.Sum(EstimateTokens);
            if (messageTokens == 0) continue;
            accumulatedTokens += messageTokens;
            if (accumulatedTokens >= keepRecentTokens)
            {
                exceededBudget = true;
                var candidateCut = cutPoints.Find(candidate => candidate >= i);
                cutIndex = candidateCut >= 0 ? candidateCut : cutPoints[^1];
                break;
            }
        }

        // 恢复尝试及其省略编辑在最后一个可见输入之后上下文不可见。仅当后缀闭合且含被省略的
        // assistant 尝试时前移；任意元数据不得把切分点移过未发送的输入。
        var suffix = entries.Skip(cutIndex + 1).Take(Math.Max(0, endIndex - cutIndex - 1)).ToList();
        bool IsIntrinsicallyVisible(ProjectedSessionEntry entry) =>
            entry.SourceEntry is not ContextEditEntry
            && SessionEntries.SessionEntryToContextMessages(entry.SourceEntry).Count > 0;
        bool IsOmitted(ProjectedSessionEntry entry) =>
            IsIntrinsicallyVisible(entry) && entry.Messages.Count == 0;
        var omittedSuffixIds = suffix.Where(IsOmitted)
            .Select(entry => entry.SourceEntry.Id).ToHashSet(StringComparer.Ordinal);
        var hasExternalReplacement = suffix.Any(entry =>
            entry.SourceEntry is ContextEditEntry { Replacement: not null } edit
            && !omittedSuffixIds.Contains(edit.TargetId));
        var isRecoveryOmissionSuffix =
            exceededBudget
            && !hasExternalReplacement
            && suffix.Any(entry =>
                entry.SourceEntry is SessionMessageEntry { Message: AssistantMessage } && IsOmitted(entry))
            && suffix.All(entry =>
                entry.SourceEntry is not CompactionEntry && (!IsIntrinsicallyVisible(entry) || IsOmitted(entry)));
        if (isRecoveryOmissionSuffix) cutIndex++;

        while (cutIndex > startIndex)
        {
            var previous = entries[cutIndex - 1];
            if (previous.SourceEntry is CompactionEntry || previous.Messages.Count > 0) break;
            cutIndex--;
        }

        var startsTurn = IsProjectedTurnStart(entries[cutIndex]);
        var turnStartIndex = startsTurn ? -1 : FindProjectedTurnStartIndex(entries, cutIndex, startIndex);
        return new CutPointResult(cutIndex, turnStartIndex, !startsTurn && turnStartIndex != -1);
    }

    /// <summary>
    /// 为压缩做准备。返回 undefined 表示无需压缩（末尾已是压缩条目、无可摘要内容等）。
    /// 对应 TS <c>prepareCompaction</c>。
    /// </summary>
    public static CompactionPreparation? PrepareCompaction(
        IReadOnlyList<SessionEntry> pathEntries, CompactionSettings settings)
    {
        if (pathEntries.Count > 0 && pathEntries[^1] is CompactionEntry)
        {
            return null;
        }

        var projection = SessionEntries.BuildSessionProjection(pathEntries);
        var projectedEntries = projection.Entries;
        var sourceEntries = projectedEntries.Select(entry => entry.SourceEntry).ToList();
        // 最新压缩先被投影。更旧的压缩条目仍可能出现在其保留区间内，但投影贡献为空。
        // IReadOnlyList 无 FindIndex，手写前向扫描。
        var prevCompactionIndex = -1;
        for (var scan = 0; scan < projectedEntries.Count; scan++)
        {
            if (projectedEntries[scan].SourceEntry is CompactionEntry
                && projectedEntries[scan].Messages.Count > 0)
            {
                prevCompactionIndex = scan;
                break;
            }
        }

        string? previousSummary = null;
        var boundaryStart = 0;
        if (prevCompactionIndex >= 0)
        {
            previousSummary = ((CompactionEntry)projectedEntries[prevCompactionIndex].SourceEntry).Summary;
            // 规范投影已选定前次压缩保留的尾部。
            boundaryStart = prevCompactionIndex + 1;
        }

        var boundaryEnd = projectedEntries.Count;
        var tokensBefore = EstimateProjectedContextTokens(projection, pathEntries).Tokens;
        var cutPoint = FindProjectedCutPoint(
            projectedEntries, boundaryStart, boundaryEnd, settings.KeepRecentTokens);

        var firstKeptEntry = cutPoint.FirstKeptEntryIndex < projectedEntries.Count
            ? projectedEntries[cutPoint.FirstKeptEntryIndex].SourceEntry
            : null;
        if (firstKeptEntry?.Id is not { } firstKeptEntryId) return null;
        var historyEnd = cutPoint.IsSplitTurn ? cutPoint.TurnStartIndex : cutPoint.FirstKeptEntryIndex;

        var messagesToSummarize = projectedEntries
            .Skip(boundaryStart)
            .Take(Math.Max(0, historyEnd - boundaryStart))
            .SelectMany(GetMessagesFromProjectedEntryForCompaction)
            .ToList();
        var turnPrefixMessages = cutPoint.IsSplitTurn
            ? projectedEntries
                .Skip(cutPoint.TurnStartIndex)
                .Take(Math.Max(0, cutPoint.FirstKeptEntryIndex - cutPoint.TurnStartIndex))
                .SelectMany(GetMessagesFromProjectedEntryForCompaction)
                .ToList()
            : new List<ChatMessage>();

        if (messagesToSummarize.Count == 0 && turnPrefixMessages.Count == 0) return null;

        var fileOps = ExtractFileOperations(messagesToSummarize, sourceEntries, prevCompactionIndex);

        if (cutPoint.IsSplitTurn)
        {
            foreach (var message in turnPrefixMessages)
            {
                CompactionUtils.ExtractFileOpsFromMessage(message, fileOps);
            }
        }

        return new CompactionPreparation(
            firstKeptEntryId,
            messagesToSummarize,
            turnPrefixMessages,
            cutPoint.IsSplitTurn,
            tokensBefore,
            previousSummary,
            fileOps,
            settings);
    }

    /// <summary>用准备好的数据生成压缩摘要。对应 TS <c>compact</c>。</summary>
    public static async Task<CompactionResult> CompactAsync(
        CompactionPreparation preparation,
        ModelSpec model,
        string? apiKey,
        IReadOnlyDictionary<string, string?>? headers = null,
        string? customInstructions = null,
        CancellationToken signal = default,
        string? thinkingLevel = null,
        StreamFn? streamFn = null,
        IReadOnlyDictionary<string, string>? env = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        string? sessionId = null)
    {
        string summary;
        Usage summaryUsage;

        if (preparation.IsSplitTurn && preparation.TurnPrefixMessages.Count > 0)
        {
            var historyText = preparation.PreviousSummary ?? "No prior history.";
            Usage? historyUsage = null;
            if (preparation.MessagesToSummarize.Count > 0)
            {
                var historyResult = await GenerateSummaryWithUsageAsync(
                    preparation.MessagesToSummarize,
                    model,
                    preparation.Settings.ReserveTokens,
                    apiKey,
                    headers,
                    signal,
                    customInstructions,
                    preparation.PreviousSummary,
                    thinkingLevel,
                    streamFn,
                    env,
                    retry,
                    callbacks,
                    sessionId).ConfigureAwait(false);
                historyText = historyResult.Text;
                historyUsage = historyResult.Usage;
            }

            var turnPrefixResult = await GenerateTurnPrefixSummaryAsync(
                preparation.TurnPrefixMessages,
                model,
                preparation.Settings.ReserveTokens,
                apiKey,
                headers,
                env,
                signal,
                thinkingLevel,
                streamFn,
                retry,
                callbacks,
                sessionId).ConfigureAwait(false);

            summary =
                $"{historyText}\n\n---\n\n**Turn Context (split turn):**\n\n{turnPrefixResult.Text}";
            summaryUsage = historyUsage is not null
                ? UsageTotalsApi.CombineUsage(historyUsage, turnPrefixResult.Usage)
                : turnPrefixResult.Usage;
        }
        else
        {
            var result = await GenerateSummaryWithUsageAsync(
                preparation.MessagesToSummarize,
                model,
                preparation.Settings.ReserveTokens,
                apiKey,
                headers,
                signal,
                customInstructions,
                preparation.PreviousSummary,
                thinkingLevel,
                streamFn,
                env,
                retry,
                callbacks,
                sessionId).ConfigureAwait(false);
            summary = result.Text;
            summaryUsage = result.Usage;
        }

        var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(preparation.FileOps);
        summary += CompactionUtils.FormatFileOperations(readFiles, modifiedFiles);

        if (preparation.FirstKeptEntryId.Length == 0)
        {
            throw new InvalidOperationException("First kept entry has no UUID - session may need migration");
        }

        return new CompactionResult(
            summary,
            preparation.FirstKeptEntryId,
            preparation.TokensBefore,
            null,
            summaryUsage,
            new CompactionDetails(readFiles, modifiedFiles));
    }

    /// <summary>拆分回合时为回合前缀生成摘要。对应 TS <c>generateTurnPrefixSummary</c>。</summary>
    private static async Task<(string Text, Usage Usage)> GenerateTurnPrefixSummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        ModelSpec model,
        long reserveTokens,
        string? apiKey,
        IReadOnlyDictionary<string, string?>? headers,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken signal,
        string? thinkingLevel,
        StreamFn? streamFn,
        RetryPolicy? retry,
        RetryCallbacks? callbacks,
        string? sessionId)
    {
        // 回合前缀不再使用折算预算，与主摘要一致由 provider 层定尺寸（上游 #9512）。
        _ = reserveTokens;
        var llmMessages = AgentMessages.ConvertToLlm(messages);
        var conversationText = CompactionUtils.SerializeConversation(llmMessages);
        var promptText = $"# Conversation\n{conversationText}\n\n# Instructions\n{TurnPrefixSummarizationPrompt}";

        var response = await CompleteSummarizationAsync(
            model,
            BuildSummarizationContext(promptText),
            CreateSummarizationOptions(
                model, apiKey, headers, env, signal.CanBeCanceled ? signal : null, thinkingLevel, sessionId),
            streamFn,
            retry,
            callbacks,
            signal).ConfigureAwait(false);

        var failure = GetSummarizationFailure(response, "Turn prefix summarization");
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        if (response.Content.Any(block => block is ToolCallContent))
        {
            throw new InvalidOperationException("Turn prefix summarization attempted to call a tool");
        }

        return (Text.ContentText(response.Content), response.UsageStats ?? new Usage(0, 0));
    }
}
