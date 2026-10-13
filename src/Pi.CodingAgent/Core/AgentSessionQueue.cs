using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using PromptTemplatesApi = Pi.CodingAgent.Core.PromptTemplates;

namespace Pi.CodingAgent.Core;

// ============================================================================
// AgentSession 的输入与排队层 — TS `agent-session.ts` 的 `_runInputHandlers`（1916-1934）、
// `_queueUserInput`（2172-2208）、`steer`（2210）/ `followUp`（2226）、`_queueSteer`（2237）/
// `_queueFollowUp`（2254）、`_throwIfExtensionCommand`（2267-2277）、队列模式（2686-2715）
// 与 `setCacheWarmingMode`（1435）/ `setAutoCompactionEnabled`（3248）。
// ============================================================================
//
// 纯决策半边已在 9b/9c 落地为静态叶子（`ResolveInputHandlerOutcome` /
// `ResolveQueueStreamingBehavior` / `FormatQueuedExtensionCommandError` / `ClearQueue`）；
// 本文件是实例接线层，逐条对照 TS 参考实现。

/// <summary>对应 TS <c>steer</c> / <c>followUp</c> 的 <c>options</c>（<c>{ source?: InputSource }</c>）。</summary>
public sealed record QueueInputOptions
{
    public string? Source { get; init; }
}

/// <summary>TS <c>QueueMode</c>（<c>"all" | "one-at-a-time"</c>）的文本转换。</summary>
/// <remarks>
/// 差异 C158：TS 的 <c>QueueMode</c> 本身就是字符串字面量联合，端口在 <see cref="Pi.Agent.Agent"/>
/// 内部用枚举承载，故跨边界（设置文件、会话 getter、扩展 API）必须显式转文本。此前
/// <c>AgentSession.SteeringMode</c> 直接 <c>ToString()</c> 得到 <c>"All"</c> / <c>"OneAtATime"</c>，
/// 与 TS 的 <c>"all"</c> / <c>"one-at-a-time"</c> 不符——本批一并修掉。
/// </remarks>
public static class QueueModes
{
    public const string All = "all";

    public const string OneAtATime = "one-at-a-time";

    /// <summary>枚举 → TS 文本。</summary>
    public static string ToText(QueueMode mode) => mode == QueueMode.All ? All : OneAtATime;

    /// <summary>TS 文本 → 枚举；未知值按 TS 的队列构造默认回落到 <c>one-at-a-time</c>。</summary>
    public static QueueMode Parse(string? mode) => mode == All ? QueueMode.All : QueueMode.OneAtATime;
}

public partial class AgentSession
{
    // =========================================================================
    // 输入处理（TS 1916-1934）
    // =========================================================================

    /// <summary>
    /// TS <c>_runInputHandlers(text, images, source, streamingBehavior)</c>。无 <c>input</c> 处理器时
    /// 原样透传；<c>handled</c> 返回 <c>null</c>（TS <c>undefined</c>），调用方据此中止提交。
    /// </summary>
    private async Task<InputHandlerOutcome?> RunInputHandlersAsync(
        string text,
        IReadOnlyList<ImageContent>? images,
        string source,
        string? streamingBehavior)
    {
        var hasHandlers = _extensionRunner.HasHandlers(ExtensionEventNames.Input);
        var result = hasHandlers
            ? await _extensionRunner.EmitInputAsync(text, images, source, streamingBehavior).ConfigureAwait(false)
            : null;

        return ResolveInputHandlerOutcome(
            hasHandlers,
            result switch
            {
                InputEventResult.Handled => "handled",
                InputEventResult.Transform => "transform",
                _ => "continue",
            },
            result is InputEventResult.Transform transform ? transform.Text : text,
            result is InputEventResult.Transform transformWithImages ? transformWithImages.Images : null,
            text,
            images);
    }

    // =========================================================================
    // 排队（TS 2172-2266）
    // =========================================================================

    /// <summary>TS <c>steer(text, images?, options?)</c>：排队一条 steering 消息。</summary>
    public Task<string> SteerAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        QueueInputOptions? options = null)
        => QueueUserInputAsync(text, images, "steer", options?.Source ?? InputSource.Interactive);

    /// <summary>TS <c>followUp(text, images?, options?)</c>：排队一条 follow-up 消息。</summary>
    public Task<string> FollowUpAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        QueueInputOptions? options = null)
        => QueueUserInputAsync(text, images, "followUp", options?.Source ?? InputSource.Interactive);

    /// <summary>TS <c>_queueUserInput(text, images, behavior, source)</c>。</summary>
    private async Task<string> QueueUserInputAsync(
        string text,
        IReadOnlyList<ImageContent>? images,
        string behavior,
        string source)
    {
        if (text.StartsWith('/'))
        {
            ThrowIfExtensionCommand(text);
        }

        var processed = await RunInputHandlersAsync(
            text, images, source, ResolveQueueStreamingBehavior(IsStreaming, behavior)).ConfigureAwait(false);
        if (processed is null)
        {
            return QueuedInputDisposition.Handled;
        }

        // TS：先展开 skill 命令，再展开文件型提示词模板。
        var expanded = ExpandSkillCommandForSession(processed.Text);
        expanded = PromptTemplatesApi.ExpandPromptTemplate(expanded, [.. PromptTemplates]);

        if (behavior == "steer")
        {
            await QueueSteerAsync(expanded, processed.Images).ConfigureAwait(false);
        }
        else
        {
            await QueueFollowUpAsync(expanded, processed.Images).ConfigureAwait(false);
        }

        return QueuedInputDisposition.Queued;
    }

    /// <summary>TS <c>_queueSteer(text, images?)</c>：先记 UI 队列再排进 agent。</summary>
    private Task QueueSteerAsync(string text, IReadOnlyList<ImageContent>? images)
    {
        _steeringMessages.Add(text);
        EmitQueueUpdate();
        _agent.Steer(QueuedUserMessage(text, images));
        return Task.CompletedTask;
    }

    /// <summary>TS <c>_queueFollowUp(text, images?)</c>。</summary>
    private Task QueueFollowUpAsync(string text, IReadOnlyList<ImageContent>? images)
    {
        _followUpMessages.Add(text);
        EmitQueueUpdate();
        _agent.FollowUp(QueuedUserMessage(text, images));
        return Task.CompletedTask;
    }

    /// <summary>TS 里 <c>{ role: "user", content, timestamp: Date.now() }</c> 的队列载荷。</summary>
    private static UserMessage QueuedUserMessage(string text, IReadOnlyList<ImageContent>? images)
    {
        var content = new List<ContentBlock> { new TextContent(text) };
        if (images is not null)
        {
            content.AddRange(images);
        }

        return new UserMessage(content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>TS <c>_throwIfExtensionCommand(text)</c>。</summary>
    private void ThrowIfExtensionCommand(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        if (_extensionRunner.GetCommand(commandName) is not null)
        {
            throw new InvalidOperationException(FormatQueuedExtensionCommandError(commandName));
        }
    }

    /// <summary>
    /// TS <c>_expandSkillCommand(text)</c> 的实例面：读的是**当前 loader 的 skills**，读文件失败时
    /// 上报扩展错误后返回原文。
    /// </summary>
    /// <remarks>
    /// 与静态 <see cref="ExpandSkillCommand"/> 的差别只有一处：静态版把读失败吞成「返回原文」，
    /// 而 TS 在这里 <c>emitError({ event: "skill_expansion" })</c>；故实例面自带错误上报，
    /// 不转发静态版（差异 C159）。
    /// </remarks>
    private string ExpandSkillCommandForSession(string text)
    {
        var parsed = ParseSkillCommand(text);
        if (parsed is null)
        {
            return text;
        }

        Skill? skill = null;
        foreach (var candidate in _resourceLoader.Skills)
        {
            if (string.Equals(candidate.Name, parsed.Name, StringComparison.Ordinal))
            {
                skill = candidate;
                break;
            }
        }

        if (skill is null)
        {
            return text;
        }

        try
        {
            var body = Frontmatter.Strip(File.ReadAllText(skill.FilePath)).Trim();
            var skillBlock =
                $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\n"
                + $"References are relative to {skill.BaseDir}.\n\n{body}\n</skill>";
            return parsed.Args.Length > 0 ? $"{skillBlock}\n\n{parsed.Args}" : skillBlock;
        }
        catch (Exception error)
        {
            _extensionRunner.EmitError(new ExtensionError(
                skill.FilePath, "skill_expansion", error.Message, error.StackTrace));
            return text;
        }
    }

    // =========================================================================
    // 队列模式（TS 2686-2715）
    // =========================================================================

    /// <summary>TS <c>_syncQueueModesFromSettings()</c>：reload 时把设置里的模式灌回 agent。</summary>
    private void SyncQueueModesFromSettings()
    {
        _agent.SteeringMode = QueueModes.Parse(SettingsManager.GetSteeringMode());
        _agent.FollowUpMode = QueueModes.Parse(SettingsManager.GetFollowUpMode());
    }

    /// <summary>TS <c>setSteeringMode(mode)</c>：改 agent 并落设置。</summary>
    public void SetSteeringMode(string mode)
    {
        _agent.SteeringMode = QueueModes.Parse(mode);
        SettingsManager.SetSteeringMode(QueueModes.ToText(_agent.SteeringMode));
    }

    /// <summary>TS <c>setFollowUpMode(mode)</c>。</summary>
    public void SetFollowUpMode(string mode)
    {
        _agent.FollowUpMode = QueueModes.Parse(mode);
        SettingsManager.SetFollowUpMode(QueueModes.ToText(_agent.FollowUpMode));
    }

    // =========================================================================
    // 设置转发（TS 1435-1438 / 3248-3256）
    // =========================================================================

    /// <summary>
    /// TS <c>setCacheWarmingMode(mode)</c>。
    /// </summary>
    /// <remarks>
    /// 差异 C158 的近亲：TS 还要 <c>this._cacheWarmer?.onModeChanged()</c> 把新档位推给已排定的温存任务；
    /// 端口的 <see cref="CacheWarmer"/> 每次读模式都走注入的 <c>getMode</c> 委托（<c>Status</c> 里现算），
    /// 没有缓存档位，故无需通知。
    /// </remarks>
    public void SetCacheWarmingMode(string mode) => SettingsManager.SetCacheWarmingMode(mode);

    /// <summary>TS <c>setAutoCompactionEnabled(enabled)</c>。</summary>
    public void SetAutoCompactionEnabled(bool enabled) => SettingsManager.SetCompactionEnabled(enabled);

    /// <summary>TS <c>get autoCompactionEnabled()</c>。</summary>
    public bool AutoCompactionEnabled => SettingsManager.GetCompactionEnabled();
}
