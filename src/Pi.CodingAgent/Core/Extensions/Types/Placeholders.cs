using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core.Extensions.Types;

// ============================================================================
// 4e/4f 占位类型（placeholder types）
// ============================================================================
//
// TS `core/extensions/types.ts` 引用了一批尚未移植的模块类型（session-manager / compaction /
// bash-executor / cache-warmer / messages / slash-commands / system-prompt /
// footer-data-provider / theme）。按 4d 执行方案（docs/4d-extension-system-plan.md
// 批次 4d-1），这里为每个类型定义**最小占位**，使契约层可以独立编译；4e/4f 落地时把这些记录
// 换成真实实现并统一收敛（每处均有 `// 4e/4f 接入后替换` 标记）。
//
// 已随 4d-2a/4d-2b 移除的占位：`EventBus`（→ Core/EventBus.cs）、`SourceInfo`
// （→ Core/SourceInfo.cs）、`ExecOptions` / `ExecResult`（→ Core/Exec.cs）。
//
// 占位原则：
// - 只保留契约面用到的成员（事件负载的字段、回调的签名）；
// - 不实现任何行为；
// - 命名空间固定在 Pi.CodingAgent.Core.Extensions.Types，真实类型落地后删除对应占位即可。

// ---------------------------------------------------------------------------
// bash-executor.ts / tools/bash.ts（4e）—— 占位已移除：
//   `BashResult` 真实实现见 Core/BashExecutor.cs；
//   `BashOperations` 真实实现为 Core/Tools/BashTool.cs 的 IBashToolOperations。
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// footer-data-provider.ts（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// 自定义 footer 的数据来源（TS <c>ReadonlyFooterDataProvider</c>）占位。// 4e/4f 接入后替换
/// </summary>
public interface IReadonlyFooterDataProvider
{
}

// ---------------------------------------------------------------------------
// compaction/index.ts（4e）
// ---------------------------------------------------------------------------

// `CompactionPreparation` / `CompactionResult` 占位已移除：真实实现见 Core/Compaction.cs，
// 且它们在 Pi.CodingAgent.Core.Extensions 内一律被父命名空间遮蔽，占位版本不可达。

// ---------------------------------------------------------------------------
// cache-warmer.ts（4e）—— 占位已移除：`CacheWarmingDecisionEvent` 现在是
// ExtensionEvent 的嵌套变体、`CacheWarmingDecisionEventResult` 与它同处
// Core/Extensions/ExtensionEvents.cs；`CacheWarmingAction` 见 Core/CacheWarmer.cs。
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// messages.ts（4e-2b）—— CustomMessage / UserMessageContent 占位已移除，
// 真实类型见 Core/Messages.cs（CustomMessage 派生自 Pi.Ai 的 ChatMessage）。
//
// session-manager.ts（4e-2c-1）—— CustomEntry<T> / SessionEntry / ContextEditEntry /
// CompactionEntry / BranchSummaryEntry / ProjectedSessionEntry 占位已移除，
// 真实类型见 Core/SessionEntries.cs（在父命名空间 Pi.CodingAgent.Core 下，无需 using）。
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// session-manager.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>ReadonlySessionManager</c> 占位（扩展上下文里的只读会话视图）。// 4e/4f 接入后替换
/// </summary>
public interface IReadonlySessionManager
{
    /// <summary>TS <c>getSessionId()</c>。</summary>
    string SessionId { get; }

    /// <summary>TS <c>getSessionFile()</c>。</summary>
    string? SessionFile { get; }

    /// <summary>TS <c>getEntries()</c>。</summary>
    IReadOnlyList<SessionEntry> Entries { get; }
}

/// <summary>TS <c>SessionManager</c> 占位（命令上下文里的可写会话管理器）。// 4e/4f 接入后替换</summary>
public interface ISessionManager : IReadonlySessionManager
{
}

// ---------------------------------------------------------------------------
// slash-commands.ts（4e-1）—— 占位已移除，真实类型见 Core/SlashCommands.cs
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// system-prompt.ts（4e）—— 占位已移除：真实类型见 Core/SystemPrompt.cs 的
// BuildSystemPromptOptions / NormalizedBuildSystemPromptOptions。此前的空占位与真实类型同名，
// 在 Pi.CodingAgent.Core.Extensions 内被父命名空间遮蔽（不可达），在测试命名空间里则造成歧义。
//
// keybindings.ts（4e）—— `AppKeybinding` 占位已移除：本移植把 app 级 keybinding id 建模为
// Core/Keybindings.cs 的 AppKeybindings 字符串常量（对齐 TuiKeybindingIds 的既有做法），
// 该记录无任何消费者。
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// pi-tui 中尚未落地的类型（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>OverlayHandle</c>（pi-tui）占位。// 4e/4f 接入后替换
/// </summary>
public sealed class OverlayHandle
{
}

/// <summary>
/// TS pi-ai <c>ProviderHeaders</c> 占位：可变的请求头表，null 值表示删除该头。// 4e/4f 接入后替换
/// </summary>
public sealed class ProviderHeaders
{
    private readonly Dictionary<string, string?> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key]
    {
        // TS semantics: assigning null deletes the header.
        get => _headers.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value is null)
            {
                _headers.Remove(key);
            }
            else
            {
                _headers[key] = value;
            }
        }
    }

    public IReadOnlyDictionary<string, string?> AsDictionary() => _headers;
}

// TS pi-ai `Provider` 占位已移除：真实类型是 Pi.Ai.Models.IProvider
// （ModelRegistry.RegisterProvider 与 ModelRuntime.RegisterNativeProvider 都收它）。

// ---------------------------------------------------------------------------
// modes/interactive/theme/theme.ts（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>Theme</c> 占位。渲染器（renderCall / renderResult / setWidget 组件工厂）与
/// <c>ctx.ui.theme</c> 都消费它；4f 落地后替换为真实 Theme。// 4e/4f 接入后替换
/// </summary>
public sealed class Theme
{
}
