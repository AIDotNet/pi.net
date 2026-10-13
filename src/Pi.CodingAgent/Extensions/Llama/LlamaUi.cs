using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>
/// The action a user picks in the llama model manager. Port of the TS
/// <c>LlamaManagerAction</c> union.
/// </summary>
public abstract record LlamaManagerAction
{
    /// <summary>TS <c>{ type: "model"; model }</c>.</summary>
    public sealed record Model(LlamaModelInfo Value) : LlamaManagerAction;

    /// <summary>TS <c>{ type: "download" }</c>.</summary>
    public sealed record Download : LlamaManagerAction;

    /// <summary>TS <c>{ type: "close" }</c>.</summary>
    public sealed record Close : LlamaManagerAction;
}

/// <summary>
/// One progress frame shown while a model loads or downloads. Port of the TS
/// <c>ProgressState</c> (<c>LlamaProgress</c> plus the dialog title and model id).
/// </summary>
public sealed record LlamaProgressState
{
    public required string Title { get; init; }

    public required string Model { get; init; }

    public required string Message { get; init; }

    public double? Ratio { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The UI surface the llama extension drives. Port of the TS <c>LlamaUi</c> interface.
/// </summary>
/// <remarks>
/// The concrete TUI implementation (<c>LlamaView</c> / <c>HuggingFaceSearch</c> /
/// <c>showLlamaUi</c> in <c>ui.ts</c>) renders through the interactive theme and components that land
/// with 4f, so only this interface, the pure label helpers and <see cref="LlamaUi.RunWithProgressAsync{T}"/>
/// are ported here. The view is supplied through
/// <see cref="LlamaExtension.LlamaExtensionOptions.ShowUi"/>.
/// </remarks>
public interface ILlamaUi
{
    /// <summary>TS <c>showModels(serverUrl, models)</c>.</summary>
    Task<LlamaManagerAction> ShowModels(string serverUrl, IReadOnlyList<LlamaModelInfo> models);

    /// <summary>TS <c>select(title, options)</c>.</summary>
    Task<string?> Select(string title, IReadOnlyList<string> options);

    /// <summary>TS <c>confirm(title, message)</c>.</summary>
    Task<bool> Confirm(string title, string message);

    /// <summary>TS <c>connectionError(serverUrl, message)</c>; returns <c>"retry"</c> or <c>"close"</c>.</summary>
    Task<string> ConnectionError(string serverUrl, string message);

    /// <summary>TS <c>searchModels(search)</c>.</summary>
    Task<string?> SearchModels(Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> search);

    /// <summary>TS <c>showStatus(title, message)</c>.</summary>
    void ShowStatus(string title, string message);

    /// <summary>TS <c>progress(state)</c>: resolves when the user asks to stop.</summary>
    Task Progress(LlamaProgressState state);

    /// <summary>TS <c>updateProgress(state)</c>.</summary>
    void UpdateProgress(LlamaProgressState state);
}

/// <summary>The result of <see cref="LlamaUi.RunWithProgressAsync{T}"/>. Port of the TS discriminated union.</summary>
public sealed record LlamaRunResult<T>
{
    public bool Cancelled { get; init; }

    /// <summary>Present only when <see cref="Cancelled"/> is false.</summary>
    public T? Value { get; init; }
}

/// <summary>Options for <see cref="LlamaUi.RunWithProgressAsync{T}"/>. Port of the TS inline options type.</summary>
public sealed record LlamaRunOptions<T>
{
    public required string Title { get; init; }

    public required string Model { get; init; }

    public required string InitialMessage { get; init; }

    public required string CancelTitle { get; init; }

    public required string CancelMessage { get; init; }

    public required Func<CancellationToken, Action<LlamaProgress>, Task<T>> Run { get; init; }

    public required Func<Task> Cancel { get; init; }
}

/// <summary>The pure helpers and the progress driver of <c>extensions/llama/ui.ts</c>.</summary>
public static class LlamaUi
{
    /// <summary>The TS <c>contextLabel(model)</c>: a compact context-window label like <c>128k</c>.</summary>
    public static string? ContextLabel(LlamaModelInfo model)
    {
        var context = model.MetaNCtx ?? model.MetaNCtxTrain;
        if (context is not null and not 0)
        {
            return context >= 1000
                ? $"{JsNumber.ToJsString(JsMath.Round(context.Value / 1000))}k"
                : JsNumber.ToJsString(context.Value);
        }

        var args = model.StatusArgs ?? [];
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] is not ("--ctx-size" or "-c" or "-ctx"))
            {
                continue;
            }

            var value = JsNumber.Parse(args[index + 1]);
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            {
                return value >= 1000
                    ? $"{JsNumber.ToJsString(JsMath.Round(value / 1000))}k"
                    : JsNumber.ToJsString(value);
            }
        }

        return null;
    }

    /// <summary>The TS <c>modelDescription(model)</c>.</summary>
    public static string ModelDescription(LlamaModelInfo model)
    {
        var details = new List<string>();
        var loaded = model.StatusValue is LlamaModelStatusValue.Loaded or LlamaModelStatusValue.Sleeping;
        if (loaded)
        {
            details.Add("loaded");
        }
        else if (model.StatusValue != LlamaModelStatusValue.Unloaded)
        {
            details.Add(model.StatusValue);
        }

        var context = loaded ? ContextLabel(model) : null;
        if (!string.IsNullOrEmpty(context))
        {
            details.Add($"{context} context");
        }

        return string.Join(" · ", details);
    }

    /// <summary>The TS <c>compactCount(value)</c>: <c>1.2M</c> / <c>340k</c> style download counts.</summary>
    public static string CompactCount(double value)
    {
        if (value >= 1_000_000)
        {
            return $"{JsNumber.ToFixed(value / 1_000_000, value >= 10_000_000 ? 0 : 1)}M";
        }

        if (value >= 1_000)
        {
            return $"{JsNumber.ToFixed(value / 1_000, value >= 100_000 ? 0 : 1)}k";
        }

        return JsNumber.ToJsString(value);
    }

    /// <summary>
    /// The TS <c>runWithProgress(ui, options)</c>: run the work while racing it against the user's
    /// stop request, and return whether the run was cancelled.
    /// </summary>
    /// <remarks>
    /// Difference C171: TS uses a <c>Promise.race</c> between the settled run and <c>ui.progress()</c>;
    /// C# uses <see cref="Task.WhenAny(Task[])"/> with the same two tasks. The <c>completed</c> flag is
    /// written from a continuation, so it is guarded with <see cref="Volatile"/> (JS reads it from the
    /// single-threaded event loop).
    /// </remarks>
    public static async Task<LlamaRunResult<T>> RunWithProgressAsync<T>(ILlamaUi ui, LlamaRunOptions<T> options)
    {
        using var controller = new CancellationTokenSource();
        var state = new LlamaProgressState
        {
            Title = options.Title,
            Model = options.Model,
            Message = options.InitialMessage,
        };

        async Task<(bool Ok, T? Value, Exception? Error)> RunSettledAsync()
        {
            try
            {
                var value = await options.Run(
                    controller.Token,
                    progress =>
                    {
                        state = state with { Message = progress.Message, Ratio = progress.Ratio, Detail = progress.Detail };
                        ui.UpdateProgress(state);
                    }).ConfigureAwait(false);
                return (true, value, null);
            }
            catch (Exception error)
            {
                return (false, default, error);
            }
        }

        var settled = RunSettledAsync();
        var completed = 0;
        _ = settled.ContinueWith(
            _ => Volatile.Write(ref completed, 1),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        while (Volatile.Read(ref completed) == 0)
        {
            var settledFirst = settled.ContinueWith(
                _ => "settled", CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var stopFirst = ui.Progress(state).ContinueWith(
                _ => "stop", CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var winner = await Task.WhenAny(settledFirst, stopFirst).ConfigureAwait(false);
            var outcome = await ((Task<string>)winner).ConfigureAwait(false);
            if (outcome == "settled")
            {
                break;
            }

            var stop = await ui.Confirm(options.CancelTitle, options.CancelMessage).ConfigureAwait(false);
            if (!stop || Volatile.Read(ref completed) != 0)
            {
                continue;
            }

            try
            {
                await options.Cancel().ConfigureAwait(false);
            }
            finally
            {
                await controller.CancelAsync().ConfigureAwait(false);
            }

            await settled.ConfigureAwait(false);
            return new LlamaRunResult<T> { Cancelled = true };
        }

        var result = await settled.ConfigureAwait(false);
        if (!result.Ok)
        {
            throw result.Error!;
        }

        return new LlamaRunResult<T> { Cancelled = false, Value = result.Value };
    }
}
