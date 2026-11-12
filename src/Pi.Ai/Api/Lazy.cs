using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// 延迟流的旁路观察者：每个事件与终态各一次回调。对应 TS <c>LazyStreamObserver</c>，
/// 供遥测在不改动流语义的前提下收集 span 与指标。
/// </summary>
public interface ILazyStreamObserver
{
    /// <summary>转发一个事件之前调用。</summary>
    void OnEvent(AssistantMessageEvent @event);

    /// <summary>流结束：<paramref name="message"/> 为终态消息；准备阶段失败时为 <see langword="null"/>。</summary>
    void OnResult(AssistantMessage? message);
}

/// <summary>
/// 延迟流：同步返回事件流，异步准备工作（认证解析、模块加载）在其后运行；
/// 准备失败以 error 终态事件终止流。对应 TS <c>api/lazy.ts</c> 的 <c>lazyStream</c>。
/// </summary>
public static class LazyStream
{
    /// <summary>
    /// 准备失败时构造的错误终态消息（零用量 + StopReason.Error）。
    /// <paramref name="timestamp"/> 是流创建时刻——准备失败消息沿用请求开始时间，而不是失败发生的时刻。
    /// </summary>
    public static AssistantMessage SetupErrorMessage(
        ModelSpec model, Exception error, long timestamp, StopReason stopReason = StopReason.Error)
        => new(
            Content: [],
            StopReason: stopReason,
            ErrorMessage: error.Message,
            UsageStats: new Usage(0, 0),
            Model: model.Id,
            Api: model.Api,
            Provider: model.Provider,
            Timestamp: timestamp);

    /// <summary>
    /// 从 provider 选项字典取取消信号（<c>options["signal"]</c>）。对应 TS 把
    /// <c>options?.signal</c> 一路透传进 <c>lazyStream</c> 的做法（上游 #8635）。
    /// </summary>
    public static CancellationToken SignalOf(IReadOnlyDictionary<string, object?>? options)
        => options is not null
           && options.TryGetValue("signal", out var value)
           && value is CancellationToken token
            ? token
            : default;

    /// <summary>
    /// 运行延迟准备并把内层事件流转发到外层流。对应 TS <c>lazyStream</c> +
    /// <c>forwardStream</c>：逐事件 push，随后以 <c>WaitForDoneAsync</c> 取终态收尾。
    /// </summary>
    public static IAssistantMessageEventStream Run(
        ModelSpec model, Func<Task<IAssistantMessageEventStream>> setup,
        ILazyStreamObserver? observer = null, CancellationToken signal = default)
    {
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outer = new AssistantMessageEventStream();
        _ = Task.Run(async () =>
        {
            try
            {
                var inner = await setup().ConfigureAwait(false);
                await foreach (var @event in inner.ConfigureAwait(false))
                {
                    observer?.OnEvent(@event);
                    outer.Push(@event);
                }

                var final = await inner.WaitForDoneAsync().ConfigureAwait(false);
                // 先让观察者收尾（span 在此期间结算），再结束外层流。
                observer?.OnResult(final);
                outer.End(final);
            }
            catch (Exception error)
            {
                // 上游 #8635：准备阶段被取消时以 aborted 终态收尾，而不是 error（哪怕抛出的
                // 不是 OperationCanceledException，只要信号已取消就按 aborted 记）。
                var reason = signal.IsCancellationRequested || error is OperationCanceledException
                    ? StopReason.Aborted
                    : StopReason.Error;
                var message = SetupErrorMessage(model, error, startedAt, reason);
                observer?.OnResult(null);
                outer.Push(new AssistantMessageEvent.Error(message.StopReason, message.ErrorMessage!, message));
                outer.End(message);
            }
        });
        return outer;
    }
}

/// <summary>延迟 API 的能力开关。对应 TS <c>LazyApiCapabilities</c>。</summary>
public sealed record LazyApiCapabilities
{
    public bool FetchDeferred { get; init; }

    public bool CancelDeferred { get; init; }
}

/// <summary>
/// 把「按需加载的 API 实现」包装成 <see cref="ProviderStreams"/>。对应 TS
/// <c>lazyApi</c>：首次 stream 调用时才加载实现模块；加载失败以 error 终态终止流。
/// </summary>
public static class LazyApi
{
    public static ProviderStreams Create(
        Func<Task<ProviderStreams>> load, LazyApiCapabilities? capabilities = null)
    {
        var streams = new ProviderStreams
        {
            Stream = (model, context, options) => LazyStream.Run(model, async () =>
                (await load().ConfigureAwait(false)).Stream(model, context, options),
                signal: LazyStream.SignalOf(options)),
            StreamSimple = (model, context, options) => LazyStream.Run(model, async () =>
                (await load().ConfigureAwait(false)).StreamSimple(model, context, options),
                signal: LazyStream.SignalOf(options)),
        };

        if (capabilities?.FetchDeferred == true)
        {
            streams = streams with
            {
                FetchDeferred = (model, handle, options) => LazyStream.Run(model, async () =>
                {
                    var implementation = await load().ConfigureAwait(false);
                    if (implementation.FetchDeferred is null)
                    {
                        throw new ModelsError(ModelsErrorCode.Provider,
                            "API does not support deferred responses");
                    }
                    return implementation.FetchDeferred(model, handle, options);
                }, signal: LazyStream.SignalOf(options)),
            };
        }
        if (capabilities?.CancelDeferred == true)
        {
            streams = streams with
            {
                CancelDeferred = async (model, handle, options, cancellationToken) =>
                {
                    var implementation = await load().ConfigureAwait(false);
                    if (implementation.CancelDeferred is null)
                    {
                        throw new ModelsError(ModelsErrorCode.Provider,
                            "API cannot cancel deferred responses");
                    }
                    await implementation.CancelDeferred(model, handle, options, cancellationToken)
                        .ConfigureAwait(false);
                },
            };
        }

        return streams;
    }
}
