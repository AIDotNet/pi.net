using System.Runtime.InteropServices;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// transcript 回放工具集。对应 TS <c>utils/transcript.ts</c>：system 消息是
/// prompt 与工具声明的增量载体，按序回放得到当前状态。
/// </summary>
public static class Transcript
{
    /// <summary>为一个 prompt 与工具集构造前导 system 消息；都为空时返回 null。</summary>
    public static SystemMessage? CreateInitialSystemMessage(
        string? systemPrompt, IReadOnlyList<ToolDefinition>? tools)
    {
        var hasSystemPrompt = systemPrompt is not null && systemPrompt.Length > 0;
        var hasTools = tools is not null && tools.Count > 0;
        if (!hasSystemPrompt && !hasTools) return null;
        return new SystemMessage(
            Content: systemPrompt ?? "",
            ToolsAdded: hasTools ? tools : null,
            Timestamp: 0);
    }

    /// <summary>
    /// 把 <c>Context.systemPrompt</c>/<c>Context.tools</c> 折进前导 system 消息。
    /// 对应 TS <c>normalizeContext</c>。
    /// </summary>
    public static TranscriptContext NormalizeContext(string? systemPrompt, IReadOnlyList<ToolDefinition>? tools,
        IReadOnlyList<ChatMessage> messages)
    {
        var initialMessage = CreateInitialSystemMessage(systemPrompt, tools);
        var all = initialMessage is not null ? new[] { initialMessage }.Concat(messages).ToList() : messages.ToList();
        return new TranscriptContext(all);
    }

    /// <summary>transcript 开头的 system 消息（如有）。</summary>
    public static SystemMessage? GetInitialSystemMessage(IReadOnlyList<ChatMessage> messages)
        => messages.Count > 0 && messages[0] is SystemMessage system ? system : null;

    /// <summary>为把 prompt 放在消息列表之外的 API 去掉前导 system 消息。</summary>
    public static IReadOnlyList<ChatMessage> WithoutInitialSystemMessage(IReadOnlyList<ChatMessage> messages)
        => GetInitialSystemMessage(messages) is not null ? messages.Skip(1).ToList() : messages;

    /// <summary>按序应用全部 transcript 增量后的可用工具集。对应 TS <c>getCurrentTools</c>。</summary>
    public static IReadOnlyList<ToolDefinition> GetCurrentTools(IReadOnlyList<ChatMessage> messages)
    {
        // TS Map 是插入序：维护 order 列表保持首声明顺序（C# Dictionary 无序）。
        var tools = new Dictionary<string, ToolDefinition>();
        var order = new List<string>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            if (system.ToolsRemoved is not null)
            {
                foreach (var name in system.ToolsRemoved)
                {
                    if (tools.Remove(name)) order.Remove(name);
                }
            }
            if (system.ToolsAdded is not null)
            {
                foreach (var tool in system.ToolsAdded)
                {
                    if (!tools.ContainsKey(tool.Name)) order.Add(tool.Name);
                    tools[tool.Name] = tool;
                }
            }
        }
        return order.Select(name => tools[name]).ToList();
    }

    /// <summary>
    /// 把全部 system 消息回放成一条持有当前 prompt 与工具的前导 system 消息。
    /// 后续 content 追加到基础提示，sections 按名修补，工具用 <see cref="GetCurrentTools"/> 解析。
    /// 对应 TS <c>getCurrentSystemMessage</c>。
    /// </summary>
    public static SystemMessage? GetCurrentSystemMessage(IReadOnlyList<ChatMessage> messages)
    {
        var content = new List<string>();
        var sections = new Dictionary<string, string?>();
        long? timestamp = null;
        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            timestamp ??= system.Timestamp;
            var text = Text.ContentText(system);
            if (text.Length > 0) content.Add(text);
            if (system.Sections is not null)
            {
                foreach (var (name, value) in system.Sections)
                {
                    if (value is null) sections.Remove(name);
                    else sections[name] = value;
                }
            }
        }
        var tools = GetCurrentTools(messages);
        if (timestamp is null && tools.Count == 0) return null;
        var result = new SystemMessage
        {
            Content = string.Join("\n\n", content),
            Timestamp = timestamp ?? 0,
        };
        if (sections.Count > 0) result = result with { Sections = sections };
        if (tools.Count > 0) result = result with { ToolsAdded = tools };
        return result;
    }

    /// <summary>回放全部 system 消息后的当前系统提示文本。对应 TS <c>getCurrentSystemPrompt</c>。</summary>
    public static string GetCurrentSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var message = GetCurrentSystemMessage(messages);
        return message is not null ? Text.GetSystemMessageText(message) : "";
    }

    /// <summary>
    /// 为不接受中段系统消息的 API 重建 transcript：回放后的 system 消息领先，
    /// 其余 system 消息全部丢弃。对应 TS <c>collapseSystemMessages</c>。
    /// </summary>
    public static TranscriptContext CollapseSystemMessages(TranscriptContext context)
    {
        var head = GetCurrentSystemMessage(context.Messages);
        var messages = context.Messages.Where(message => message is not SystemMessage).ToList();
        return head is not null
            ? new TranscriptContext(new[] { head }.Concat(messages).ToList())
            : new TranscriptContext(messages);
    }

    /// <summary>接受中段系统消息时保持原样，否则折叠。对应 TS <c>resolveTranscript</c>。</summary>
    public static TranscriptContext ResolveTranscript(
        TranscriptContext context, bool? supportsMidConvoSystemMessages)
        => supportsMidConvoSystemMessages == true ? context : CollapseSystemMessages(context);

    /// <summary>剥离工具的可执行/展示字段，得到可比较/可持久化的声明。对应 TS <c>toToolDeclaration</c>。</summary>
    public static ToolDefinition ToToolDeclaration(ToolDefinition tool)
        => tool with
        {
            Parameters = new ToolSchema(System.Text.Json.JsonSerializer.Deserialize<
                IReadOnlyDictionary<string, object?>>(
                System.Text.Json.JsonSerializer.Serialize(tool.Parameters.JsonSchema)) ?? new Dictionary<string, object?>()),
        };

    /// <summary>
    /// 两个工具是否向模型声明了相同接口。两侧都经 <see cref="ToToolDeclaration"/>：
    /// JSON 往返统一键序与形状，序列化串比较即精确比较。对应 TS <c>declarationsEqual</c>。
    /// </summary>
    public static bool DeclarationsEqual(ToolDefinition left, ToolDefinition right)
        => System.Text.Json.JsonSerializer.Serialize(ToToolDeclaration(left))
           == System.Text.Json.JsonSerializer.Serialize(ToToolDeclaration(right));

    /// <summary>两次完整工具状态的差异。定义变化 = 删除 + 新增。对应 TS <c>getToolStateChanges</c>。</summary>
    public static (IReadOnlyList<ToolDefinition> ToolsAdded, IReadOnlyList<string> ToolsRemoved) GetToolStateChanges(
        IReadOnlyList<ToolDefinition> previous, IReadOnlyList<ToolDefinition> current)
    {
        var previousTools = previous.ToDictionary(tool => tool.Name);
        var currentTools = current.ToDictionary(tool => tool.Name);
        var added = current
            .Where(tool => !previousTools.TryGetValue(tool.Name, out var previousTool)
                || !DeclarationsEqual(previousTool, tool))
            .Select(ToToolDeclaration)
            .ToList();
        var removed = previous
            .Where(tool => !currentTools.TryGetValue(tool.Name, out var currentTool)
                || !DeclarationsEqual(tool, currentTool))
            .Select(tool => tool.Name)
            .ToList();
        return (added, removed);
    }

    /// <summary>transcript 工具状态引用的全部定义（按首次声明顺序）。对应 TS <c>getDeclaredTools</c>。</summary>
    public static IReadOnlyList<ToolDefinition> GetDeclaredTools(IReadOnlyList<ChatMessage> messages)
    {
        var definitions = new Dictionary<string, ToolDefinition>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            if (system.ToolsAdded is null) continue;
            foreach (var tool in system.ToolsAdded) definitions[tool.Name] = tool;
        }
        return definitions.Values.ToList();
    }

    /// <summary>是否存在同名重定义：只能按名引用已声明工具的传输无法回放。对应 TS <c>hasToolRedefinitions</c>。</summary>
    public static bool HasToolRedefinitions(IReadOnlyList<ChatMessage> messages)
    {
        var declared = new Dictionary<string, ToolDefinition>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            if (system.ToolsAdded is null) continue;
            foreach (var tool in system.ToolsAdded)
            {
                if (declared.TryGetValue(tool.Name, out var previous) && !DeclarationsEqual(previous, tool))
                    return true;
                declared[tool.Name] = tool;
            }
        }
        return false;
    }

    /// <summary>工具历史是否包含 addition-only 传输无法回放的删除或同名重声明。对应 TS <c>hasNonAdditiveToolChanges</c>。</summary>
    public static bool HasNonAdditiveToolChanges(IReadOnlyList<ChatMessage> messages)
    {
        var declared = new HashSet<string>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            if (system.ToolsRemoved is { Count: > 0 }) return true;
            if (system.ToolsAdded is null) continue;
            foreach (var tool in system.ToolsAdded)
            {
                if (!declared.Add(tool.Name)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 传输层如何表达首请求之后的工具变化。对应 TS <c>TranscriptToolSupport</c>。
    /// </summary>
    public enum TranscriptToolSupport
    {
        /// <summary>只有顶层工具列表，它始终是当前工具集。</summary>
        None,

        /// <summary>工具可以在后续消息就地新增，但不能删除或重定义。</summary>
        Additions,

        /// <summary>
        /// 所有工具（含最初的）都在就地声明，同名后来者替换先前声明。删除在协议上无法表达，
        /// 传输层改用文本告知，被删除的工具仍保持声明。
        /// </summary>
        Changes,
    }

    /// <summary>
    /// 顶层请求字段与就地变化的工具声明拆分。对应 TS <c>TranscriptTools</c> + <c>resolveTranscriptTools</c>。
    /// </summary>
    /// <returns>
    /// <c>RequestTools</c>：顶层请求里的工具；<c>FoldedSystemMessages</c>：前多少条系统消息的工具变化已被
    /// 顶层列表吸收，之后的每条系统消息各自就地携带变化。
    /// </returns>
    public static (IReadOnlyList<ToolDefinition> RequestTools, int FoldedSystemMessages) ResolveTranscriptTools(
        IReadOnlyList<ChatMessage> messages, TranscriptToolSupport support)
    {
        var systemMessages = messages.OfType<SystemMessage>().ToList();
        if (support == TranscriptToolSupport.None)
        {
            return (GetCurrentTools(systemMessages), systemMessages.Count);
        }

        if (support == TranscriptToolSupport.Changes) return ([], 0);

        var folded = GetInitialSystemMessage(messages) is not null ? 1 : 0;
        // 只增不改：删除或同名重声明无法就地表达，于是顶层列表取「最后一次这类变化之后」的工具状态，
        // 之后的纯新增仍可就地锚定 —— 缓存前缀每次删除只失效一次，而不是每次后续新增都失效。
        // 同名在先即算重声明（即使中间被删过）：Kimi K3 会忽略被删除又被工具消息重新声明的工具。
        var declared = new HashSet<string>();
        for (var index = 0; index < systemMessages.Count; index++)
        {
            var message = systemMessages[index];
            var added = message.ToolsAdded ?? [];
            if ((message.ToolsRemoved?.Count ?? 0) > 0 || added.Any(tool => declared.Contains(tool.Name)))
            {
                folded = Math.Max(folded, index + 1);
            }

            foreach (var tool in added) declared.Add(tool.Name);
        }

        return (GetCurrentTools(systemMessages.Take(folded).ToList()), folded);
    }
}
