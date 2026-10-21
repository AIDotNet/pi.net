using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Pi.Codemode.Runtime;

/// <summary>
/// 默认 JS 引擎：用 Jint 在独立实例里求值 prelude 与脚本，对应 TS <c>runtime/worker.ts</c> 的
/// QuickJS worker。每次执行新建一个 <see cref="Engine"/>，失控或已结束的执行不会污染下一次。
/// <para/>
/// 消息流与 TS worker 相同：<c>bridge()</c> 把工具调用/输出/完成消息排队，由独立泵按序送达
/// host；host 的 <c>result</c> 消息经 <see cref="Post"/> 送回 <c>settle</c>，随后排空 promise
/// 任务并调用 <c>stalled()</c>。脚本在后台任务里启动，使 <see cref="Run"/> 能在 host 绑定 VM
/// 句柄之前立即返回。
/// <para/>
/// 与 QuickJS 的差异：Jint 不消费 <see cref="CodemodeWorkerData.Wasm"/>（该载荷是 QuickJS 模块）；
/// <see cref="CodemodeWorkerData.MemoryLimitBytes"/> 映射为 Jint 的堆上限，中断由
/// <see cref="ICodemodeVm.StopAsync"/> 取消 Jint 的 <see cref="CancellationToken"/> 完成。
/// </para>
/// </summary>
public sealed class JintCodemodeEngine : ICodemodeJsEngine
{
    /// <summary>无状态默认实例；每次 <see cref="Run"/> 仍创建独立 VM。</summary>
    public static readonly JintCodemodeEngine Instance = new();

    private const string PreludeFile = "codemode-prelude.js";
    private const string ScriptFile = "codemode.js";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private JintCodemodeEngine()
    {
    }

    public ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
        => new JintVm(data, onMessage);

    private sealed class JintVm : ICodemodeVm
    {
        private readonly CodemodeWorkerData data;
        private readonly Func<WorkerToHostMessage, Task> onMessage;
        private readonly Channel<WorkerToHostMessage> outgoing =
            Channel.CreateUnbounded<WorkerToHostMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly Channel<HostToWorkerMessage> incoming =
            Channel.CreateUnbounded<HostToWorkerMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly CancellationTokenSource cancellation = new();
        private readonly Task pump;
        private readonly Task loop;
        private volatile bool stopping;
        private bool doneSent;

        internal JintVm(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
        {
            this.data = data;
            this.onMessage = onMessage;
            this.pump = Task.Run(this.PumpAsync);
            this.loop = Task.Run(this.RunAsync);
        }

        /// <inheritdoc />
        public void Post(HostToWorkerMessage message)
        {
            if (this.stopping) return;
            this.incoming.Writer.TryWrite(message);
        }

        /// <inheritdoc />
        public async Task StopAsync()
        {
            this.stopping = true;
            this.incoming.Writer.TryComplete();
            try
            {
                this.cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已停止；取消令牌无需再次触发。
            }

            try
            {
                await this.loop.ConfigureAwait(false);
            }
            catch
            {
                // 对应 TS 的 terminate().catch(() => undefined)；执行已结束，异常不再外传。
            }

            this.outgoing.Writer.TryComplete();
            try
            {
                await this.pump.ConfigureAwait(false);
            }
            catch
            {
                // host 回调失败不影响 stop 的收敛语义。
            }
        }

        private async Task PumpAsync()
        {
            await foreach (var message in this.outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await this.onMessage(message).ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            var token = this.cancellation.Token;
            try
            {
                var engine = this.CreateEngine();
                var api = this.Setup(engine);

                JsValue script;
                try
                {
                    // 前缀与脚本共享首行，报错行号与手写脚本一致（同 TS worker）。
                    script = engine.Evaluate($"(async (tools, console) => {{{this.data.Code}\n}})", ScriptFile);
                }
                catch (JavaScriptException error)
                {
                    this.SendSyntaxError(error);
                    return;
                }

                engine.Call(api.Get("run"), api, [script]);
                this.Drain(engine, api);

                while (!this.doneSent && !token.IsCancellationRequested)
                {
                    HostToWorkerMessage message;
                    try
                    {
                        if (!await this.incoming.Reader.WaitToReadAsync(token).ConfigureAwait(false)) break;
                        if (!this.incoming.Reader.TryRead(out message!)) continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (message is not HostToWorkerMessage.Result result) continue;
                    var payload = result.Payload is null
                        ? JsValue.Undefined
                        : JsValue.FromObject(engine, result.Payload);
                    engine.Call(api.Get("settle"), api,
                    [
                        JsNumber.Create((double)result.Id),
                        result.Ok ? JsBoolean.True : JsBoolean.False,
                        payload,
                    ]);
                    this.Drain(engine, api);
                }
            }
            catch (ExecutionCanceledException)
            {
                // StopAsync 取消了正在执行的脚本；不再产生消息。
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 等待 host 结果时被停止。
            }
            catch (Exception error)
            {
                if (!this.doneSent && !this.stopping) this.SendCrash(error);
            }
            finally
            {
                this.outgoing.Writer.TryComplete();
            }
        }

        private Engine CreateEngine()
        {
            return new Engine(options =>
            {
                options.CancellationToken(this.cancellation.Token);
                // QuickJS 以 maxStackSize 把深递归变成可捕获的 RangeError；Jint 默认不限制递归，
                // 深递归会以 CLR StackOverflow 终止宿主进程。StackOverflowGuard 直接探测原生栈，
                // 是所有递归路径的安全对应物。
                options.Constraints.StackOverflowGuard = true;
                if (this.data.MemoryLimitBytes is > 0 and < long.MaxValue)
                {
                    options.LimitMemory(this.data.MemoryLimitBytes.Value);
                }
            });
        }

        private ObjectInstance Setup(Engine engine)
        {
            var bridge = new ClrFunction(engine, "bridge", (_, args) =>
            {
                this.OnBridge(args);
                return JsValue.Undefined;
            });

            var prelude = engine.Evaluate(CodemodePrelude.Source, PreludeFile);
            var api = engine.Call(prelude, JsValue.Undefined,
            [
                bridge,
                JsValue.FromObject(engine, JsonSerializer.Serialize(
                    this.data.Tools.Select(tool => new
                    {
                        name = tool.Name,
                        jsName = tool.JsName,
                        description = tool.Description,
                    }),
                    JsonOptions)),
                JsValue.FromObject(engine, JsonSerializer.Serialize(
                    this.data.Globals.Select(global => new { name = global.Name, spread = global.Spread }),
                    JsonOptions)),
                JsValue.FromObject(engine, JsonSerializer.Serialize(this.data.Store, JsonOptions)),
            ]);
            return api.AsObject();
        }

        private void Drain(Engine engine, ObjectInstance api)
        {
            engine.Advanced.ProcessTasks();
            engine.Call(api.Get("stalled"), api, []);
        }

        /// <summary>对应 worker 的 <c>bridge</c>：参数都是原语，消息按调用顺序排队。</summary>
        private void OnBridge(JsValue[] args)
        {
            var kind = Arg(args, 0).ToString();
            switch (kind)
            {
                case "call":
                case "global":
                {
                    var id = (long)Arg(args, 1).AsNumber();
                    var name = Arg(args, 2).ToString();
                    var payload = Arg(args, 3);
                    this.Send(new WorkerToHostMessage.Call(
                        id,
                        kind == "call" ? "tool" : "global",
                        name,
                        payload.IsUndefined() ? null : payload.ToString()));
                    break;
                }
                case "output":
                {
                    var outputKind = Arg(args, 1).ToString();
                    var value = Arg(args, 2).ToString();
                    if (outputKind == "image")
                    {
                        this.Send(new WorkerToHostMessage.Output(
                            new CodemodeOutputItem.Image(value, Arg(args, 3).ToString())));
                    }
                    else
                    {
                        this.Send(new WorkerToHostMessage.Output(
                            new CodemodeOutputItem.Text(value) { Console = outputKind == "console" ? true : null }));
                    }
                    break;
                }
                case "done":
                {
                    this.doneSent = true;
                    if (Arg(args, 1).AsBoolean())
                    {
                        var result = Arg(args, 2);
                        this.Send(new WorkerToHostMessage.Done(
                            true,
                            result.IsUndefined() ? null : result.ToString(),
                            Arg(args, 3).ToString(),
                            null));
                    }
                    else
                    {
                        this.Send(new WorkerToHostMessage.Done(
                            false, null, null, ParseScriptError(Arg(args, 2).ToString())));
                    }
                    break;
                }
            }
        }

        private static JsValue Arg(JsValue[] args, int index)
            => index < args.Length ? args[index] : JsValue.Undefined;

        private void Send(WorkerToHostMessage message) => this.outgoing.Writer.TryWrite(message);

        private void SendSyntaxError(JavaScriptException error)
        {
            var name = GetString(error.Error, "name") ?? "Error";
            var message = GetString(error.Error, "message") ?? error.Message;
            var stack = GetString(error.Error, "stack")?.TrimEnd();
            var head = string.IsNullOrEmpty(message) ? name : $"{name}: {message}";
            this.Send(new WorkerToHostMessage.Done(
                false,
                null,
                null,
                new CodemodeScriptError(string.IsNullOrEmpty(message) ? head : message)
                {
                    Name = name,
                    Stack = string.IsNullOrEmpty(stack) ? head : $"{head}\n{stack}",
                }));
        }

        private void SendCrash(Exception error)
        {
            var message = error is JavaScriptException js
                ? $"{GetString(js.Error, "name") ?? "Error"}: {GetString(js.Error, "message") ?? error.Message}"
                : $"{error.GetType().Name}: {error.Message}";
            this.Send(new WorkerToHostMessage.Crash(message));
        }

        private static string? GetString(JsValue value, string property)
        {
            if (value is not ObjectInstance obj) return null;
            var member = obj.Get(property);
            return member.IsUndefined() || member.IsNull() ? null : member.ToString();
        }

        private static CodemodeScriptError ParseScriptError(string json)
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject obj)
                {
                    return new CodemodeScriptError(ReadString(obj["message"]) ?? "")
                    {
                        Name = ReadString(obj["name"]),
                        Stack = ReadString(obj["stack"]),
                    };
                }
            }
            catch (JsonException)
            {
                // 非 JSON 载荷按原始文本处理（引擎侧仍会把它作为脚本错误消息上报）。
            }
            return new CodemodeScriptError(json);
        }

        private static string? ReadString(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
