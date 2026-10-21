// ============================================================================
// Shared coordinator for interactive model-catalog refreshes.
// Port of modes/interactive/model-catalog-refresh.ts
// ============================================================================

using System.Runtime.CompilerServices;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// Port of <c>refreshModelCatalogs</c>: concurrent interactive all-catalog refreshes share one
/// underlying <c>modelRuntime.refresh()</c> operation per runtime instance, while every caller
/// keeps an independent cancellation race. When the last waiter leaves, the shared operation is
/// aborted so a background refresh does not outlive its consumers.
/// </summary>
/// <remarks>
/// TS keys the active refresh in a <c>WeakMap&lt;ModelCatalogRuntime, …&gt;</c>; the port uses a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by the runtime instance (same lifetime
/// semantics). The <c>finally</c> bookkeeping (waiter count, last-one-aborts, entry cleanup) is
/// expressed with <c>try/finally</c> continuations instead of promise chains.
/// </remarks>
public static class ModelCatalogRefresh
{
    private static readonly object Sync = new();

    private static readonly ConditionalWeakTable<object, ActiveRefresh> ActiveByRuntime = new();

    /// <summary>
    /// Test seam standing in for TS's <c>vi.spyOn(modelRuntime, "refresh")</c>: when set, the
    /// coordinator's underlying operation comes from here instead of
    /// <see cref="ModelRuntime.RefreshAsync"/>. C#'s <see cref="ModelRuntime"/> is <c>sealed</c>,
    /// so the spy must live at this boundary.
    /// </summary>
    /// <remarks>
    /// 值放在 <see cref="AsyncLocal{T}"/> 中：xUnit 会并行运行测试类，静态槽位会让一个用例的
    /// 刷新桩泄漏到另一个用例（例如错误结果覆盖 model selector 的 “No matching models” 路径）。
    /// </remarks>
    private static readonly AsyncLocal<Func<ModelRuntime, CancellationToken, Task<ModelsRefreshResult>>?> RefreshOverrideSlot = new();

    internal static Func<ModelRuntime, CancellationToken, Task<ModelsRefreshResult>>? RefreshOverride
    {
        get => RefreshOverrideSlot.Value;
        set => RefreshOverrideSlot.Value = value;
    }

    private sealed class ActiveRefresh(CancellationTokenSource controller, Task<ModelsRefreshResult> promise)
    {
        public CancellationTokenSource Controller { get; } = controller;

        public Task<ModelsRefreshResult> Promise { get; } = promise;

        public int Waiters;
    }

    /// <summary>Port of <c>ModelCatalogRefreshCoordinator.refresh</c>.</summary>
    public static async Task<ModelsRefreshResult> RefreshAsync(ModelRuntime modelRuntime, CancellationToken signal)
    {
        // TS `signal.throwIfAborted()`.
        signal.ThrowIfCancellationRequested();
        var active = GetOrCreate(modelRuntime);
        active.Waiters++;
        try
        {
            // TS: `raceWithAbortSignal(active.promise, signal)` — the caller's abort rejects only
            // this await; the shared operation keeps running until the last waiter leaves.
            return await Abort.RaceWithAbortSignalAsync(active.Promise, signal).ConfigureAwait(false);
        }
        finally
        {
            active.Waiters--;
            if (active.Waiters == 0
                && ActiveByRuntime.TryGetValue(modelRuntime, out var current)
                && ReferenceEquals(current, active))
            {
                // TS: `active.controller.abort()` — only when this entry is still the registered one.
                active.Controller.Cancel();
            }
        }
    }

    private static ActiveRefresh GetOrCreate(ModelRuntime runtime)
    {
        lock (Sync)
        {
            if (ActiveByRuntime.TryGetValue(runtime, out var existing))
            {
                return existing;
            }

            var controller = new CancellationTokenSource();
            // TS: `modelRuntime.refresh({ signal: controller.signal })` then
            // `raceWithAbortSignal(operation, controller.signal)`.
            var operation = RefreshOverride is { } @override
                ? @override(runtime, controller.Token)
                : runtime.RefreshAsync(new ModelsRefreshOptions { Signal = controller.Token });
            var entry = new ActiveRefresh(controller, Abort.RaceWithAbortSignalAsync(operation, controller.Token));
            ActiveByRuntime.Add(runtime, entry);

            // TS: `.finally(() => { if (this.activeByRuntime.get(runtime) === created) delete })`.
            _ = entry.Promise.ContinueWith(
                _ =>
                {
                    lock (Sync)
                    {
                        if (ActiveByRuntime.TryGetValue(runtime, out var current)
                            && ReferenceEquals(current, entry))
                        {
                            ActiveByRuntime.Remove(runtime);
                        }
                    }
                },
                TaskScheduler.Default);
            return entry;
        }
    }
}
