using System.Runtime.InteropServices;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Rpc;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes;

// ============================================================================
// Print 模式（单发）— TS `modes/print-mode.ts`（169 行）。
//
// `pi -p "prompt"` 走 text 输出（只打最后一条 assistant 的文本）；
// `pi --mode json "prompt"` 走 JSON 事件流（stdout 逐行）。
// 与 RPC 模式共用 JsonEvent / OutputGuard / JsonL 线路层。
// ============================================================================

/// <summary>Print 模式选项。对应 TS <c>PrintModeOptions</c>。</summary>
public sealed record PrintModeOptions
{
    /// <summary>输出模式：<c>text</c> 只出最终回复，<c>json</c> 出全部事件。</summary>
    public required string Mode { get; init; }

    /// <summary>首条消息之后追加发送的提示词。</summary>
    public IReadOnlyList<string>? Messages { get; init; }

    /// <summary>首条消息（可能含 @file 内容）。</summary>
    public string? InitialMessage { get; init; }

    /// <summary>挂在首条消息上的图片。</summary>
    public IReadOnlyList<ImageContent>? InitialImages { get; init; }
}

/// <summary>Print 模式入口。对应 TS <c>runPrintMode</c>。</summary>
public static class PrintMode
{
    /// <summary>以 print（单发）模式运行，返回进程退出码。</summary>
    public static async Task<int> RunAsync(AgentSessionRuntime runtimeHost, PrintModeOptions options)
    {
        var mode = new PrintModeSession(runtimeHost, options);
        return await mode.RunAsync().ConfigureAwait(false);
    }
}

/// <summary>对应 TS <c>runPrintMode</c> 闭包内的全部状态。</summary>
internal sealed class PrintModeSession
{
    private readonly AgentSessionRuntime _runtimeHost;
    private readonly PrintModeOptions _options;
    private readonly List<Action> _signalCleanupHandlers = [];

    private AgentSession _session;
    private IDisposable? _unsubscribe;
    private IDisposable? _unsubscribeBackpressure;
    private bool _disposed;

    public PrintModeSession(AgentSessionRuntime runtimeHost, PrintModeOptions options)
    {
        _runtimeHost = runtimeHost;
        _options = options;
        _session = runtimeHost.Session;
    }

    public async Task<int> RunAsync()
    {
        var exitCode = 0;
        RegisterSignalHandlers();

        _runtimeHost.SetRebindSession(_ => RebindSessionAsync());

        try
        {
            if (_options.Mode == "json")
            {
                // TS: header 先于一切事件落行（sessionManager.getHeader()）。
                var header = _session.SessionManager.GetHeader();
                if (header is not null)
                {
                    OutputGuard.WriteRawStdout(JsonL.SerializeLine(header));
                }
            }

            await RebindSessionAsync().ConfigureAwait(false);

            if (_options.InitialMessage is { } initialMessage)
            {
                await _session.PromptAsync(initialMessage, new PromptOptions
                {
                    Images = _options.InitialImages,
                }).ConfigureAwait(false);
            }

            foreach (var message in _options.Messages ?? [])
            {
                await _session.PromptAsync(message).ConfigureAwait(false);
            }

            if (_options.Mode == "text")
            {
                exitCode = WriteFinalText();
            }

            return exitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            foreach (var cleanup in _signalCleanupHandlers)
            {
                cleanup();
            }

            await DisposeRuntimeAsync().ConfigureAwait(false);
            await OutputGuard.FlushRawStdoutAsync().ConfigureAwait(false);
        }
    }

    /// <summary>text 模式的收尾：最后一条 assistant 消息。对应 TS 的 L139-156。</summary>
    private int WriteFinalText()
    {
        var lastMessage = _session.Messages.Count > 0 ? _session.Messages[^1] : null;
        if (lastMessage is not AssistantMessage assistant)
        {
            return 0;
        }

        if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
        {
            // TS: `assistantMsg.errorMessage || \`Request ${assistantMsg.stopReason}\``——空串也回退。
            var reason = assistant.StopReason == StopReason.Error ? "error" : "aborted";
            Console.Error.WriteLine(
                string.IsNullOrEmpty(assistant.ErrorMessage) ? $"Request {reason}" : assistant.ErrorMessage);
            return 1;
        }

        foreach (var content in assistant.Content)
        {
            if (content is TextContent text)
            {
                OutputGuard.WriteRawStdout($"{text.Text}\n");
            }
        }

        return 0;
    }

    private async Task RebindSessionAsync()
    {
        _session = _runtimeHost.Session;
        await _session.BindExtensionsAsync(new ExtensionBindings
        {
            // print / json 模式没有 UI 上下文（TS 同样不传 uiContext）。
            UiContext = null,
            Mode = _options.Mode == "json" ? ExtensionMode.Json : ExtensionMode.Print,
            CommandContextActions = new ExtensionCommandContextActions
            {
                WaitForIdle = () => _session.WaitForIdleAsync(),
                NewSession = async options => (await _runtimeHost.NewSessionAsync(options).ConfigureAwait(false)).Cancelled,
                Fork = async (entryId, forkOptions) =>
                    (await _runtimeHost.ForkAsync(entryId, forkOptions).ConfigureAwait(false)).Cancelled,
                NavigateTree = async (targetId, navigateOptions) =>
                {
                    var result = await _session.NavigateTreeAsync(targetId, new AgentSession.NavigateTreeSessionOptions
                    {
                        Summarize = navigateOptions?.Summarize,
                        CustomInstructions = navigateOptions?.CustomInstructions,
                        ReplaceInstructions = navigateOptions?.ReplaceInstructions,
                        Label = navigateOptions?.Label,
                    }).ConfigureAwait(false);
                    return result.Cancelled;
                },
                SwitchSession = async (sessionPath, switchOptions) =>
                {
                    var result = await _runtimeHost.SwitchSessionAsync(sessionPath, new SwitchSessionRuntimeOptions
                    {
                        WithSession = switchOptions?.WithSession,
                    }).ConfigureAwait(false);
                    return result.Cancelled;
                },
                Reload = async () => await _session.ReloadAsync().ConfigureAwait(false),
            },
            OnError = error => Console.Error.WriteLine($"Extension error ({error.ExtensionPath}): {error.Error}"),
        }).ConfigureAwait(false);

        _unsubscribe?.Dispose();
        _unsubscribeBackpressure?.Dispose();
        _unsubscribe = _session.Subscribe(@event =>
        {
            if (_options.Mode == "json")
            {
                OutputGuard.WriteRawStdout(JsonL.SerializeLine(JsonEvent.ToJsonEvent(@event)));
            }
        });
        _unsubscribeBackpressure = _options.Mode == "json"
            ? _session.Agent.Subscribe(async (_, _) =>
            {
                await OutputGuard.WaitForRawStdoutBackpressureAsync().ConfigureAwait(false);
            })
            : null;
    }

    private async Task DisposeRuntimeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unsubscribe?.Dispose();
        _unsubscribeBackpressure?.Dispose();
        await _runtimeHost.DisposeAsync().ConfigureAwait(false);
    }

    private void RegisterSignalHandlers()
    {
        if (!OperatingSystem.IsWindows())
        {
            RegisterPosixSignal(PosixSignal.SIGHUP);
        }

        RegisterPosixSignal(PosixSignal.SIGTERM);
    }

    private void RegisterPosixSignal(PosixSignal signal)
    {
        var name = signal == PosixSignal.SIGHUP ? "SIGHUP" : "SIGTERM";
        void HandleSignal(PosixSignalContext context)
        {
            Shell.KillTrackedDetachedChildren();
            _ = Task.Run(async () =>
            {
                await DisposeRuntimeAsync().ConfigureAwait(false);
                Environment.Exit(name == "SIGHUP" ? 129 : 143);
            });
        }

        var registration = PosixSignalRegistration.Create(signal, HandleSignal);
        _signalCleanupHandlers.Add(registration.Dispose);
    }
}
