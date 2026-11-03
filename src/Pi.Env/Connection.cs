using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Env;

/// <summary>
/// 启动期错误的 stderr 尾部长度。上游 <c>479d6e5e2</c>：远端写的是任意文本，可能带终端转义，
/// 而且工具错误会进模型上下文，所以只留很短一截。
/// </summary>
public static partial class StartupErrorText
{
    public const int StderrTailChars = 2000;

    public const int StderrTailLines = 20;

    /// <summary>CSI / OSC / 其他单字符转义。近似 Node 的 <c>stripVTControlCharacters</c>。</summary>
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex VtRegex();

    /// <summary>
    /// <paramref name="message"/> 加上 stderr 的最后几行。控制字符与终端转义一律去掉，空行过滤。
    /// 对应 TS <c>withStderr</c>。
    /// </summary>
    public static string WithStderr(string message, string stderr)
    {
        var normalized = stderr.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = normalized.Replace('\r', '\n');
        var stripped = VtRegex().Replace(normalized, string.Empty);
        var text = new StringBuilder(stripped.Length);
        foreach (var character in stripped)
        {
            var code = character;
            if (character == '\n' || character == '\t' || (code >= 0x20 && (code < 0x7f || code > 0x9f)))
            {
                text.Append(character);
            }
        }

        var lines = text.ToString()
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return message;
        }

        var tail = lines.Count > StderrTailLines ? lines.Skip(lines.Count - StderrTailLines) : lines;
        return message + "\nremote stderr:\n" + string.Join("\n", tail);
    }
}

/// <summary>
/// 守护进程报告的失败，带 Node 风格错误码。对应 TS <c>RemoteError</c>。
/// <para><see cref="Fields"/> 保留整帧 JSON：<c>lost</c> 标记连接已断（请求可重试），
/// <c>spillPath</c> 等额外字段由调用方按需读取。</para>
/// </summary>
public sealed class RemoteError : Exception
{
    public RemoteError(JsonObject fields)
        : base(EnvJson.Str(fields, "message") ?? "Remote operation failed")
    {
        Fields = fields;
        Code = EnvJson.Str(fields, "code") ?? "unknown";
        Path = EnvJson.Str(fields, "path");
    }

    /// <summary>Node 风格错误码，或 <c>shell_unavailable</c> / <c>spawn_error</c> / <c>timeout</c> / <c>aborted</c>。</summary>
    public string Code { get; }

    /// <summary>守护进程报告的相关路径（若有）。</summary>
    public string? Path { get; }

    /// <summary>整帧字段，保留未建模的键。</summary>
    public JsonObject Fields { get; }

    /// <summary>连接已丢失，而非守护进程拒绝了请求。对应 TS <c>isConnectionLost</c> 的判据。</summary>
    public bool Lost => EnvJson.Flag(Fields, "lost");
}

/// <summary><c>hello</c> 报告的远端机器信息。对应 TS <c>RemoteInfo</c>。</summary>
public sealed record RemoteInfo(
    long Protocol,
    string Version,
    string Os,
    string Arch,
    string Home,
    string TmpDir,
    string Separator,
    string Cwd,
    IReadOnlyDictionary<string, string> DriveCwds,
    long Pid);

/// <summary>一次请求的应答。对应 TS <c>Reply</c>。</summary>
public sealed class Reply
{
    public required JsonObject Json { get; init; }

    public required byte[] Payload { get; init; }

    /// <summary>应答的守护进程会话；由它打开的句柄只在该会话存活期间有效。</summary>
    public required long Session { get; init; }
}

/// <summary>启动守护进程的命令。对应 TS <c>ConnectionOptions</c>。</summary>
public sealed class ConnectionOptions
{
    /// <summary>
    /// 在 <c>serve --token &lt;hex&gt;</c> 之前启动守护进程的命令，例如
    /// <c>["ssh","-T","--","host","~/.pi/mobile/tools/pi-env"]</c>。每次启动都会重新求值；
    /// 求值失败即该次启动失败，下一次请求重试。
    /// </summary>
    public required Func<Task<IReadOnlyList<string>>> Command { get; init; }

    /// <summary>接收守护进程与传输层的诊断输出。</summary>
    public Action<string>? OnLog { get; init; }

    /// <summary>固定命令（测试与本地直连用）。</summary>
    public static ConnectionOptions FromCommand(IReadOnlyList<string> command, Action<string>? onLog = null) =>
        new() { Command = () => Task.FromResult(command), OnLog = onLog };
}

/// <summary>单个请求的选项。对应 TS <c>RequestOptions</c>。</summary>
public sealed class RequestOptions
{
    /// <summary>随请求发送的原始负载（文件内容、写入块）。</summary>
    public byte[]? Payload { get; init; }

    /// <summary>取消时发送 <c>cancel</c>；请求仍以守护进程的结果或错误落定。</summary>
    public CancellationToken? Signal { get; init; }

    /// <summary>请求的进度事件，例如 <c>exec</c> 输出。</summary>
    public Action<JsonObject, byte[]>? OnEvent { get; init; }

    /// <summary>请求的 id 与会话，供 <see cref="Connection.Kill"/> 使用。</summary>
    public Action<long, long>? OnStart { get; init; }

    /// <summary>
    /// 只在该守护进程会话内运行，用于该会话打开的句柄上的请求。连接丢失并重启后，
    /// 这类请求失败，而不是发到从未打开该句柄的新守护进程。
    /// </summary>
    public long? Session { get; init; }
}

/// <summary>
/// 与一个 pi-env 守护进程的连接：首个请求时惰性启动，丢失后再次启动。
/// 丢失时在途请求以码 <c>unknown</c> 失败；对写操作而言结果未知。对应 TS <c>Connection</c>。
/// </summary>
public sealed partial class Connection : IDisposable
{
    private const byte Request = 1;
    private const byte Result = 2;
    private const byte Error = 3;
    private const byte Event = 4;
    private const byte Cancel = 5;
    private const byte Ping = 6;

    private const uint MaxFrame = 16 * 1024 * 1024;
    private const int PingIntervalMs = 5000;

    /// <summary>守护进程每五秒 ping 一次；静默超过此值即视为连接已断。</summary>
    private const long SilenceLimitMs = 30_000;

    /// <summary>启动守护进程并收到 <c>hello</c> 的时限。</summary>
    private const int StartTimeoutMs = 60_000;

    private readonly ConnectionOptions _options;
    private Session? _session;
    private Task<Ready>? _ready;
    private long _sessions;
    private long _nextId = 1;
    private bool _closed;

    public Connection(ConnectionOptions options) => _options = options;

    /// <summary>固定命令的便捷构造。</summary>
    public Connection(IReadOnlyList<string> command, Action<string>? onLog = null)
        : this(ConnectionOptions.FromCommand(command, onLog))
    {
    }

    /// <summary>单个可执行文件的便捷构造（本地直连守护进程）。</summary>
    public Connection(string program, Action<string>? onLog = null)
        : this([program], onLog)
    {
    }

    /// <summary>按需连接并返回守护进程报告的机器信息。</summary>
    public async Task<RemoteInfo> InfoAsync() => (await ConnectAsync().ConfigureAwait(false)).Info;

    /// <summary>按需连接并返回存活会话的 id。</summary>
    public async Task<long> SessionIdAsync() => (await ConnectAsync().ConfigureAwait(false)).Session.Id;

    public async Task<Reply> RequestAsync(string op, JsonObject? json = null, RequestOptions? options = null)
    {
        options ??= new RequestOptions();
        if (options.Session is { } sessionId)
        {
            var current = _session;
            if (current is null || current.Id != sessionId || !current.Live) throw Lost("pi-env connection lost");
            return await SendAsync(current, op, json ?? new JsonObject(), options).ConfigureAwait(false);
        }

        var ready = await ConnectAsync().ConfigureAwait(false);
        return await SendAsync(ready.Session, op, json ?? new JsonObject(), options).ConfigureAwait(false);
    }

    /// <summary>停止守护进程；它会杀死自己启动的一切。</summary>
    public void Close()
    {
        _closed = true;
        if (_session is { } session) Teardown(session, Closed());
    }

    /// <summary>杀死一个 <c>exec</c> 而不中止它。</summary>
    public void Kill(long id, long session)
    {
        var current = _session;
        if (current is not null && current.Id == session && current.Live)
        {
            Write(current, Frame(Cancel, id, new JsonObject { ["mode"] = "kill" }));
        }
    }

    public void Dispose() => Close();

    private Task<Ready> ConnectAsync()
    {
        if (_closed) return Task.FromException<Ready>(Closed());
        if (_ready is null)
        {
            var ready = StartAsync();
            _ready = ready;
            // 失败的启动不被记住：下一次请求重试。
            _ = ready.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        _ = task.Exception;
                        if (ReferenceEquals(_ready, ready)) _ready = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // 续体可能已把 _ready 清空（同步失败时立即执行），本次调用仍返回这一轮的任务。
            return ready;
        }

        return _ready;
    }

    private async Task<Reply> SendAsync(Session session, string op, JsonObject json, RequestOptions options)
    {
        if (!session.Live) throw Lost("pi-env connection lost");
        var id = _nextId++;
        var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = new Pending { Completion = completion, OnEvent = options.OnEvent };
        options.OnStart?.Invoke(id, session.Id);
        var body = EnvJson.Normalize(json);
        // 与 TS 的 `{ ...json, op }` 一致：op 最后写入，覆盖调用方同名字段。
        body["op"] = op;
        Write(session, Frame(Request, id, body, options.Payload));
        CancellationTokenRegistration registration = default;
        if (options.Signal is { } signal)
        {
            void OnAbort() => Write(session, Frame(Cancel, id, new JsonObject()));
            // 请求帧先落盘再登记：TS 在单线程里也是这个顺序，否则取消帧会先于请求帧到达而丢失。
            if (signal.IsCancellationRequested) OnAbort();
            else registration = signal.Register(OnAbort);
        }

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void Write(Session session, byte[] data)
    {
        if (!session.Live) return;
        try
        {
            var stdin = session.Process.StandardInput.BaseStream;
            stdin.Write(data, 0, data.Length);
            stdin.Flush();
        }
        catch (IOException)
        {
            // 与 TS 的 `child.stdin.on("error", () => {})` 一致：写失败由退出事件统一处理。
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<Ready> StartAsync()
    {
        IReadOnlyList<string> command;
        try
        {
            command = await _options.Command().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new RemoteError(new JsonObject
            {
                ["code"] = "spawn_error",
                ["message"] = error.Message,
                ["lost"] = true,
            });
        }

        if (_closed) throw Closed();
        if (command.Count == 0)
        {
            throw new RemoteError(new JsonObject { ["code"] = "spawn_error", ["message"] = "No daemon command" });
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var startInfo = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < command.Count; i++) startInfo.ArgumentList.Add(command[i]);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("the daemon process did not start");
        }
        catch (Exception error)
        {
            throw new RemoteError(new JsonObject
            {
                ["code"] = "spawn_error",
                ["message"] = error.Message,
                ["lost"] = true,
            });
        }

        var session = new Session(++_sessions, process, token);
        _session = session;
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startTimer = new Timer(
            _ => failure.TrySetResult(Lost($"pi-env did not answer within {StartTimeoutMs / 1000} s")),
            null,
            StartTimeoutMs,
            Timeout.Infinite);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var code = process.HasExited ? process.ExitCode : -1;
            // 已就绪的会话一旦守护进程退出就判失败，即使它派生的进程还占着管道（上游 479d6e5e2）。
            if (!session.Ready)
            {
                failure.TrySetResult(Lost($"pi-env exited with code {code} before it was ready"));
            }

            Teardown(session, Lost("pi-env connection lost"));
        };
        _ = Task.Run(() => PumpStdoutAsync(session));
        _ = Task.Run(() => PumpStderrAsync(session));
        session.Timer = new Timer(
            _ =>
            {
                Write(session, Frame(Ping, 0, new JsonObject()));
                if (Environment.TickCount64 - session.LastSeen > SilenceLimitMs)
                {
                    Teardown(session, Lost("pi-env connection timed out"));
                }
            },
            null,
            PingIntervalMs,
            PingIntervalMs);

        try
        {
            var hello = SendAsync(session, "hello", new JsonObject { ["protocol"] = 1 }, new RequestOptions());
            var completed = await Task.WhenAny(hello, failure.Task).ConfigureAwait(false);
            if (ReferenceEquals(completed, failure.Task))
            {
                var error = await failure.Task.ConfigureAwait(false);
                // 就绪前退出时先等 stderr 排空，尾部才完整（TS 用 `close` 事件达到同一效果）。
                await WaitForStderrDrainAsync(session).ConfigureAwait(false);
                error = WithStartupStderr(error, session);
                Teardown(session, error);
                try
                {
                    await hello.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 已被失败原因取代，此处只为观察异常。
                }

                throw error;
            }

            var reply = await hello.ConfigureAwait(false);
            var protocol = EnvJson.Number(reply.Json, "protocol");
            if (protocol != 1) throw Lost($"Unsupported protocol {protocol}");
            session.Ready = true;
            session.StderrTail = "";
            return new Ready(ParseInfo(reply.Json), session);
        }
        catch (Exception error)
        {
            var failureResult = WithStartupStderr(error, session);
            Teardown(session, failureResult);
            throw failureResult;
        }
    }

    /// <summary>
    /// 把启动期 stderr 的尾部并进失败文案（<see cref="RemoteError"/> 改文案，其余原样）。
    /// 对应 TS <c>479d6e5e2</c> 的 catch 分支。
    /// </summary>
    private static Exception WithStartupStderr(Exception error, Session session)
    {
        if (error is not RemoteError remote || session.StderrTail.Length == 0)
        {
            return error;
        }

        var fields = remote.Fields.DeepClone().AsObject();
        fields["message"] = StartupErrorText.WithStderr(remote.Message, session.StderrTail);
        return new RemoteError(fields);
    }

    private void Teardown(Session session, Exception error)
    {
        if (!session.Live) return;
        session.Live = false;
        session.Timer?.Dispose();
        session.Timer = null;
        if (ReferenceEquals(_session, session))
        {
            _session = null;
            _ready = null;
        }

        try
        {
            session.Process.StandardInput.Close();
        }
        catch (Exception)
        {
            // 进程可能已退出。
        }

        try
        {
            if (!session.Process.HasExited) session.Process.Kill();
        }
        catch (Exception)
        {
            // 进程可能已退出。
        }

        var pending = session.Pending.Values.ToArray();
        session.Pending.Clear();
        foreach (var request in pending) request.Completion.TrySetException(error);
    }

    private async Task PumpStdoutAsync(Session session)
    {
        var stream = session.Process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0) return;
                OnData(session, buffer.AsSpan(0, read));
            }
        }
        catch (Exception)
        {
            // 读失败等价于进程结束：由退出事件收尾。
        }
    }

    private async Task PumpStderrAsync(Session session)
    {
        var stream = session.Process.StandardError.BaseStream;
        var buffer = new byte[64 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0) return;
                // 按字符解码，跨块的多字节序列不会变成替换字符。
                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                var text = new string(characters, 0, count);
                if (!session.Ready)
                {
                    var combined = session.StderrTail + text;
                    session.StderrTail = combined.Length > StartupErrorText.StderrTailChars
                        ? combined[^StartupErrorText.StderrTailChars..]
                        : combined;
                }

                _options.OnLog?.Invoke(text);
            }
        }
        catch (Exception)
        {
            // 诊断输出丢失不影响协议。
        }
        finally
        {
            session.StderrEnded.TrySetResult();
        }
    }

    /// <summary>等 stderr 排空，最多 <paramref name="milliseconds"/>；守护进程被杀时管道会很快关闭。</summary>
    private static async Task WaitForStderrDrainAsync(Session session, int milliseconds = 200)
    {
        await Task.WhenAny(session.StderrEnded.Task, Task.Delay(milliseconds)).ConfigureAwait(false);
    }

    private void OnData(Session session, ReadOnlySpan<byte> chunk)
    {
        if (!session.Live) return;
        session.LastSeen = Environment.TickCount64;
        session.Buffer.Append(chunk);
        if (!session.Synced)
        {
            // 登录 shell 的启动文件可能在守护进程之前打印；跳过同步行之前的一切。
            var marker = Encoding.UTF8.GetBytes($"PI-ENV {session.Token}\n");
            var index = session.Buffer.IndexOf(marker);
            if (index < 0)
            {
                if (session.Buffer.Count > 1024 * 1024) session.Buffer.Consume(session.Buffer.Count - marker.Length);
                return;
            }

            var noise = Encoding.UTF8.GetString(session.Buffer.Span[..index]).Trim();
            if (noise.Length != 0) _options.OnLog?.Invoke(noise);
            session.Buffer.Consume(index + marker.Length);
            session.Synced = true;
        }

        while (session.Live && session.Buffer.Count >= 4)
        {
            var span = session.Buffer.Span;
            var length = BinaryPrimitives.ReadUInt32BigEndian(span);
            if (length < 9 || length > MaxFrame)
            {
                Teardown(session, Lost("Corrupt frame from pi-env"));
                return;
            }

            if (session.Buffer.Count < 4 + length) return;
            var body = session.Buffer.Span.Slice(4, (int)length);
            var type = body[0];
            var id = BinaryPrimitives.ReadUInt32BigEndian(body[1..]);
            var jsonLength = BinaryPrimitives.ReadUInt32BigEndian(body[5..]);
            JsonObject json;
            try
            {
                if (9u + jsonLength > length) throw new InvalidOperationException("JSON length out of range");
                var parsed = JsonNode.Parse(body.Slice(9, (int)jsonLength));
                json = parsed as JsonObject ?? throw new InvalidOperationException("not an object");
            }
            catch (Exception)
            {
                Teardown(session, Lost("Corrupt frame from pi-env"));
                return;
            }

            // 与 NodeExecutionEnv 的读取结果一样是独立的字节数组，而不是接收缓冲区的视图。
            var payload = body[(9 + (int)jsonLength)..].ToArray();
            session.Buffer.Consume(4 + (int)length);
            Dispatch(session, type, id, json, payload);
        }
    }

    private void Dispatch(Session session, byte type, long id, JsonObject json, byte[] payload)
    {
        session.Pending.TryGetValue(id, out var pending);
        if (type == Event)
        {
            pending?.OnEvent?.Invoke(json, payload);
            return;
        }

        if (type != Result && type != Error) return;
        if (pending is null) return;
        session.Pending.Remove(id);
        if (type == Result)
        {
            pending.Completion.TrySetResult(new Reply { Json = json, Payload = payload, Session = session.Id });
        }
        else
        {
            pending.Completion.TrySetException(new RemoteError(json));
        }
    }

    private static byte[] Frame(byte type, long id, JsonObject json, byte[]? payload = null)
    {
        var body = EnvJson.Encode(json);
        payload ??= [];
        var frame = new byte[13 + body.Length + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(9 + body.Length + payload.Length));
        frame[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)id);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(9), (uint)body.Length);
        body.CopyTo(frame, 13);
        payload.CopyTo(frame, 13 + body.Length);
        return frame;
    }

    private static RemoteInfo ParseInfo(JsonObject json)
    {
        var drives = new Dictionary<string, string>(StringComparer.Ordinal);
        if (EnvJson.Obj(json, "driveCwds") is { } source)
        {
            foreach (var (key, value) in source)
            {
                if (value is JsonValue entry && entry.TryGetValue<string>(out var path)) drives[key] = path;
            }
        }

        return new RemoteInfo(
            EnvJson.Long(json, "protocol") ?? 0,
            EnvJson.Str(json, "version") ?? "",
            EnvJson.Str(json, "os") ?? "",
            EnvJson.Str(json, "arch") ?? "",
            EnvJson.Str(json, "home") ?? "",
            EnvJson.Str(json, "tmpdir") ?? "",
            EnvJson.Str(json, "separator") ?? "/",
            EnvJson.Str(json, "cwd") ?? "",
            drives,
            EnvJson.Long(json, "pid") ?? 0);
    }

    /// <summary>连接失败或已结束；重新启动后请求可能成功。对应 TS <c>lost</c>。</summary>
    private static RemoteError Lost(string message) =>
        new(new JsonObject { ["code"] = "unknown", ["message"] = message, ["lost"] = true });

    private static RemoteError Closed() =>
        new(new JsonObject { ["code"] = "unknown", ["message"] = "Connection closed" });

    private sealed record Ready(RemoteInfo Info, Session Session);

    private sealed class Pending
    {
        public required TaskCompletionSource<Reply> Completion { get; init; }

        public Action<JsonObject, byte[]>? OnEvent { get; init; }
    }

    /// <summary>一个运行中的守护进程。</summary>
    private sealed class Session
    {
        public Session(long id, Process process, string token)
        {
            Id = id;
            Process = process;
            Token = token;
        }

        public long Id { get; }

        public Process Process { get; }

        public string Token { get; }

        public Dictionary<long, Pending> Pending { get; } = new();

        /// <summary>已收到但尚未解析的字节。</summary>
        public ByteQueue Buffer { get; } = new();

        public bool Synced { get; set; }

        public long LastSeen { get; set; } = Environment.TickCount64;

        public Timer? Timer { get; set; }

        public bool Live { get; set; } = true;

        /// <summary><c>hello</c> 已成功；此后 stderr 不再作为启动错误的上下文收集。对应 TS <c>ready</c>。</summary>
        public bool Ready { get; set; }

        /// <summary>启动期 stderr 的尾部，长度上限 <see cref="StartupErrorText.StderrTailChars"/>。</summary>
        public string StderrTail { get; set; } = "";

        /// <summary>stderr 读到 EOF 时完成，供启动失败时等待尾部排空。</summary>
        public TaskCompletionSource StderrEnded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>可增长的接收缓冲：追加、查找、消费前缀，必要时合并为连续区间。</summary>
    private sealed class ByteQueue
    {
        private byte[] _buffer = new byte[8192];
        private int _start;
        private int _count;

        public int Count => _count;

        public ReadOnlySpan<byte> Span => _buffer.AsSpan(_start, _count);

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_start + _count + data.Length > _buffer.Length)
            {
                var needed = _count + data.Length;
                var size = Math.Max(_buffer.Length, 8192);
                while (size < needed) size *= 2;
                var next = new byte[size];
                Buffer.BlockCopy(_buffer, _start, next, 0, _count);
                _buffer = next;
                _start = 0;
            }

            data.CopyTo(_buffer.AsSpan(_start + _count));
            _count += data.Length;
        }

        public void Consume(int length)
        {
            if (length >= _count)
            {
                _start = 0;
                _count = 0;
                return;
            }

            _start += length;
            _count -= length;
        }

        public int IndexOf(ReadOnlySpan<byte> needle) => Span.IndexOf(needle);
    }
}
