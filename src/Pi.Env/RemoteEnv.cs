using System.Text;
using System.Text.Json.Nodes;
using Pi.Chord.Context;
using Pi.Durable.Env;
using FileInfo = Pi.Durable.Env.FileInfo;

namespace Pi.Env;

/// <summary><see cref="RemoteExecutionEnv"/> 的选项。对应 TS <c>RemoteExecutionEnvOptions</c>。</summary>
public sealed record RemoteExecutionEnvOptions
{
    public required Connection Connection { get; init; }

    /// <summary>文件命名空间：相同 id 看到相同文件（<c>FileSystem.id</c>），例如 <c>pi-env:&lt;env name&gt;</c>。</summary>
    public required string Id { get; init; }

    public required string Cwd { get; init; }

    public string? ShellPath { get; init; }

    /// <summary>加到每条继承环境的命令上，像 <c>NodeExecutionEnv</c> 的 <c>shellEnv</c>。</summary>
    public IReadOnlyDictionary<string, string>? ShellEnv { get; init; }

    /// <summary>守护进程如何观察，像 <c>NodeExecutionEnv</c> 的 <c>watch</c> 选项。</summary>
    public RemoteWatchOptions? Watch { get; init; }
}

/// <summary>
/// 另一台机器上的 <see cref="IExecutionEnv"/>，经 pi-env 守护进程到达。结果与跑在那台机器上的
/// <c>NodeExecutionEnv</c> 一致：系统调用由守护进程执行，本类施加 Node 的路径、错误与结果规则。
/// 对应 TS <c>RemoteExecutionEnv</c>。
/// </summary>
public sealed class RemoteExecutionEnv : IExecutionEnv
{
    private const long MaxTimeoutMs = 2_147_483_647;
    private const double MaxTimeoutSeconds = MaxTimeoutMs / 1000.0;

    /// <summary>二进制读取器每次 <c>pread</c> 的字节数；同时有多个在途。</summary>
    private const int ReadChunk = 256 * 1024;

    private const int ReadDepth = 8;

    /// <summary>Node 的 <c>writeFile</c> 每次最多写这么多，并在每次之前检查中止。</summary>
    private const int WriteChunk = 512 * 1024;

    private const int WriteDepth = 8;

    /// <summary>Node 的 <c>readFile</c>：已知大小的读块、未知大小的读块，以及它能读的最大文件。</summary>
    private const int ReadFileChunk = 512 * 1024;

    private const int ReadFileUnknownChunk = 64 * 1024;

    private const long ReadFileMax = int.MaxValue;

    /// <summary><c>NodeTextLineReader</c> 的读取量。</summary>
    private const int LineChunk = 64 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _shellPath;
    private readonly IReadOnlyDictionary<string, string>? _shellEnv;
    private readonly RemoteWatchOptions _watchOptions;

    /// <summary>本环境启动且仍在运行的命令，供 <see cref="CleanupAsync"/> 使用。</summary>
    private readonly Dictionary<long, long> _running = new();

    private readonly Lock _runningGate = new();

    public RemoteExecutionEnv(RemoteExecutionEnvOptions options)
    {
        Id = options.Id;
        Cwd = options.Cwd;
        Connection = options.Connection;
        _shellPath = options.ShellPath;
        _shellEnv = options.ShellEnv;
        _watchOptions = options.Watch ?? new RemoteWatchOptions();
    }

    public string Id { get; }

    public string Cwd { get; set; }

    public Connection Connection { get; }

    // ---------- 路径 ----------

    /// <summary>Node 在远端系统上的 <c>resolvePath</c>，使用远端的 home 与工作目录。</summary>
    private async Task<string> ResolveAsync(string path)
    {
        var info = await Connection.InfoAsync().ConfigureAwait(false);
        var windows = info.Os == "windows";
        var normalized = path;
        if (normalized == "~")
        {
            normalized = info.Home;
        }
        else if (normalized.StartsWith("~/", StringComparison.Ordinal)
                 || (windows && normalized.StartsWith("~\\", StringComparison.Ordinal)))
        {
            normalized = NodePath.Join(windows, info.Home, normalized[2..]);
        }
        else if (normalized.StartsWith("file://", StringComparison.Ordinal))
        {
            // 畸形 URL 保留为普通路径，如 Node 所为。
            normalized = FileUrlToPath(normalized, windows) ?? normalized;
        }

        if (NodePath.IsAbsolute(normalized, windows)) return NodePath.Resolve(windows, normalized);
        if (windows)
        {
            // 另一个盘上的盘符相对路径：Node 在 Windows 上按该盘的工作目录（`=D:`）解析，否则按自己的工作目录，
            // 再否则按该盘根目录。
            var drive = DriveRelative(normalized);
            if (drive is not null
                && !string.Equals(drive, Cwd.Length >= 2 ? Cwd[..2] : "", StringComparison.OrdinalIgnoreCase))
            {
                var basePath = info.DriveCwds.TryGetValue(drive.ToUpperInvariant(), out var known) ? known : info.Cwd;
                if (!string.Equals(basePath.Length >= 2 ? basePath[..2] : "", drive, StringComparison.OrdinalIgnoreCase)
                    && basePath.Length > 2
                    && basePath[2] == '\\')
                {
                    basePath = drive + "\\";
                }

                return NodePath.Resolve(true, basePath, normalized);
            }
        }

        return NodePath.Resolve(windows, Cwd, normalized);
    }

    /// <summary><c>^([a-zA-Z]:)(?![\\/])</c>：盘符后紧跟的不是分隔符。</summary>
    private static string? DriveRelative(string path)
    {
        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':') return null;
        if (path.Length > 2 && (path[2] == '\\' || path[2] == '/')) return null;
        return path[..2];
    }

    /// <summary><c>url.fileURLToPath</c>；无法转换时返回 null（调用方保留原字符串，如 Node 的 try/catch）。</summary>
    private static string? FileUrlToPath(string url, bool windows)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "file") return null;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!windows) return decoded;
        var host = uri.Host;
        var path = decoded.Replace('/', '\\');
        if (host.Length != 0 && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"\\\\{host}{path}";
        }

        // `/C:/x` → `C:\x`
        return path.Length > 0 && path[0] == '\\' ? path[1..] : path;
    }

    private async Task<bool> IsWindowsRemoteAsync() =>
        (await Connection.InfoAsync().ConfigureAwait(false)).Os == "windows";

    private static Result<FileInfo, FileError> ToInfo(string path, JsonObject remote, bool windows)
    {
        var kind = EnvJson.Str(remote, "kind") ?? "other";
        if (kind == "other")
        {
            return Result<FileInfo, FileError>.Err(new FileError(FileErrorCode.Invalid, "Unsupported file type", path));
        }

        var size = EnvJson.Long(remote, "size") ?? 0;
        var mtimeSec = EnvJson.Long(remote, "mtimeSec") ?? 0;
        var mtimeNsec = EnvJson.Long(remote, "mtimeNsec") ?? 0;
        return Result<FileInfo, FileError>.Ok(new FileInfo(
            NodePath.Basename(path, null, windows),
            path,
            kind switch
            {
                "file" => FileKind.File,
                "directory" => FileKind.Directory,
                _ => FileKind.Symlink,
            },
            size,
            mtimeSec * 1000 + mtimeNsec / 1e6));
    }

    // ---------- 分块读 ----------

    /// <summary>
    /// 从 <paramref name="offset"/> 读 <paramref name="length"/> 字节，按不超过 <paramref name="chunk"/> 的连续读进行，
    /// 同时有若干个在途。文件结束时提前收尾。短读从它结束处继续，与顺序读一致。每次读之后检查中止；null 表示已中止。
    /// </summary>
    private static async Task<(List<byte[]> Chunks, long Total)?> ReadRangeAsync(
        Func<long, long, Task<byte[]>> read,
        long offset,
        long length,
        int chunk,
        Func<bool> aborted)
    {
        var chunks = new List<byte[]>();
        long total = 0;
        var next = offset;
        var inFlight = new Queue<(long Length, Task<byte[]> Bytes)>();
        while (total < length)
        {
            while (inFlight.Count < ReadDepth && next < offset + length)
            {
                var size = Math.Min(offset + length - next, chunk);
                var bytes = read(next, size);
                Observe(bytes);
                inFlight.Enqueue((size, bytes));
                next += size;
            }

            var head = inFlight.Dequeue();
            var chunkBytes = await head.Bytes.ConfigureAwait(false);
            if (aborted()) return null;
            if (chunkBytes.Length == 0) break;
            chunks.Add(chunkBytes);
            total += chunkBytes.Length;
            if (chunkBytes.Length < head.Length)
            {
                // 已在途的读都假设读满；从这次读之后继续。
                inFlight.Clear();
                next = offset + total;
            }
        }

        return (chunks, total);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(
        static finished => _ = finished.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static byte[] Concat(IReadOnlyList<byte[]> chunks, long total)
    {
        if (chunks.Count == 1) return chunks[0];
        var bytes = new byte[total];
        long position = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(bytes, position);
            position += chunk.Length;
        }

        return bytes;
    }

    /// <summary>Node 的 <c>StringDecoder</c>：扣住不完整的字符，<b>不</b>剥离字节序标记。</summary>
    private sealed class NodeStringDecoder
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();

        public string Write(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            var chars = new char[bytes.Length + 2];
            _decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false, out _, out var used, out _);
            return new string(chars, 0, used);
        }

        public string End()
        {
            var chars = new char[4];
            _decoder.Convert([], 0, 0, chars, 0, chars.Length, flush: true, out _, out var used, out _);
            return new string(chars, 0, used);
        }
    }

    // ---------- 句柄 ----------

    /// <summary>守护进程句柄：只在打开它的会话中有效。</summary>
    private sealed class Handle
    {
        public Handle(RemoteExecutionEnv env, long id, long session, string path)
        {
            Env = env;
            Id = id;
            Session = session;
            Path = path;
        }

        public RemoteExecutionEnv Env { get; }

        public long Id { get; }

        public long Session { get; }

        public string Path { get; }

        public Task<Reply> RequestAsync(
            string op,
            JsonObject? json = null,
            CancellationToken? signal = null,
            byte[]? payload = null)
        {
            var body = EnvJson.Normalize(json ?? new JsonObject());
            body["handle"] = Id;
            return Env.Connection.RequestAsync(op, body, new RequestOptions
            {
                Session = Session,
                Signal = signal,
                Payload = payload,
            });
        }

        public async Task<byte[]> PreadAsync(long? offset, long length, CancellationToken? signal = null)
        {
            var json = new JsonObject { ["length"] = length };
            if (offset is { } value) json["offset"] = value;
            return (await RequestAsync("pread", json, signal).ConfigureAwait(false)).Payload;
        }

        public async Task CloseAsync()
        {
            try
            {
                await RequestAsync("close").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭失败无所谓。
            }
        }
    }

    private sealed class RemoteBinaryReader : IBinaryReader
    {
        private readonly Handle _handle;
        private bool _closed;

        public RemoteBinaryReader(Handle handle) => _handle = handle;

        private string Path => _handle.Path;

        private Result<T, FileError>? ClosedResult<T>() =>
            _closed ? Result<T, FileError>.Err(new FileError(FileErrorCode.Invalid, "Binary reader is closed", Path)) : null;

        public async Task<Result<FileInfo, FileError>> InfoAsync(Context context)
        {
            if (RemoteErrors.AbortResult<FileInfo>(context, Path) is { } early) return early;
            if (ClosedResult<FileInfo>() is { } closed) return closed;
            try
            {
                var reply = await _handle.RequestAsync("fstat").ConfigureAwait(false);
                return ToInfo(Path, reply.Json, await _handle.Env.IsWindowsRemoteAsync().ConfigureAwait(false));
            }
            catch (Exception error)
            {
                return Result<FileInfo, FileError>.Err(RemoteErrors.ToFileError(error, Path));
            }
        }

        public async Task<Result<byte[], FileError>> ReadAsync(long offset, long length, Context context)
        {
            if (RemoteErrors.AbortResult<byte[]>(context, Path) is { } early) return early;
            if (ClosedResult<byte[]>() is { } closed) return closed;
            if (offset < 0 || length < 0)
            {
                return Result<byte[], FileError>.Err(
                    new FileError(FileErrorCode.Invalid, "Offset and length must be non-negative safe integers", Path));
            }

            try
            {
                var read = await ReadRangeAsync(
                    (at, size) => _handle.PreadAsync(at, size),
                    offset,
                    length,
                    ReadChunk,
                    () => context.AbortSignal is { IsCancellationRequested: true }).ConfigureAwait(false);
                if (read is null) return Result<byte[], FileError>.Err(new FileError(FileErrorCode.Aborted, "aborted", Path));
                return Result<byte[], FileError>.Ok(Concat(read.Value.Chunks, read.Value.Total));
            }
            catch (Exception error)
            {
                return Result<byte[], FileError>.Err(RemoteErrors.ToFileError(error, Path));
            }
        }

        public async Task<Result<LineScan, FileError>> ScanLinesAsync(LineScanOptions options, Context context)
        {
            if (RemoteErrors.AbortResult<LineScan>(context, Path) is { } early) return early;
            if (ClosedResult<LineScan>() is { } closed) return closed;
            if (options.StartLine < 0 || (options.EndLine is { } end && end <= options.StartLine))
            {
                return Result<LineScan, FileError>.Err(new FileError(FileErrorCode.Invalid, "Invalid line range", Path));
            }

            try
            {
                var json = new JsonObject { ["startLine"] = options.StartLine };
                if (options.EndLine is { } endLine) json["endLine"] = endLine;
                var reply = await _handle.RequestAsync("scanLines", json, context.AbortSignal).ConfigureAwait(false);
                return Result<LineScan, FileError>.Ok(ParseLineScan(reply.Json));
            }
            catch (Exception error)
            {
                return Result<LineScan, FileError>.Err(RemoteErrors.ToFileError(error, Path));
            }
        }

        public async Task CloseAsync(Context context)
        {
            if (_closed) return;
            _closed = true;
            await _handle.CloseAsync().ConfigureAwait(false);
        }
    }

    private static LineScan ParseLineScan(JsonObject json) => new(
        EnvJson.Long(json, "newlines") ?? 0,
        EnvJson.Long(json, "start") ?? 0,
        EnvJson.Long(json, "end") ?? 0,
        EnvJson.Long(json, "firstLineEnd") ?? 0,
        EnvJson.Long(json, "lastLineStart") ?? 0,
        EnvJson.Long(json, "selectedBytes") ?? 0,
        EnvJson.Long(json, "firstLineBytes") ?? 0);

    private sealed class RemoteDirReader : IDirReader
    {
        private readonly Handle _handle;
        private bool _done;
        private bool _closed;

        public RemoteDirReader(Handle handle) => _handle = handle;

        public async Task<Result<DirPage, FileError>> NextAsync(int maxEntries, Context context)
        {
            var path = _handle.Path;
            if (RemoteErrors.AbortResult<DirPage>(context, path) is { } aborted) return aborted;
            if (_closed)
            {
                return Result<DirPage, FileError>.Err(new FileError(FileErrorCode.Invalid, "Directory reader is closed", path));
            }

            if (maxEntries <= 0)
            {
                return Result<DirPage, FileError>.Err(
                    new FileError(FileErrorCode.Invalid, "maxEntries must be a positive safe integer", path));
            }

            var entries = new List<FileInfo>();
            try
            {
                var windows = await _handle.Env.IsWindowsRemoteAsync().ConfigureAwait(false);
                // 与 NodeDirReader 一样，只计属于该列表的条目。
                while (!_done && entries.Count < maxEntries)
                {
                    var reply = await _handle.RequestAsync(
                        "readdir",
                        new JsonObject { ["max"] = maxEntries - entries.Count }).ConfigureAwait(false);
                    if (RemoteErrors.AbortResult<DirPage>(context, path) is { } loopAbort) return loopAbort;
                    if (EnvJson.Arr(reply.Json, "entries") is { } list)
                    {
                        foreach (var item in list)
                        {
                            if (item is not JsonObject entry) continue;
                            var entryPath = NodePath.Resolve(windows, path, EnvJson.Str(entry, "name") ?? "");
                            if (EnvJson.Obj(entry, "error") is { } failure)
                            {
                                // 枚举与 lstat 之间被删除：已不属于该列表。
                                if (EnvJson.Str(failure, "code") == "ENOENT") continue;
                                return Result<DirPage, FileError>.Err(
                                    RemoteErrors.ToFileError(new RemoteError(failure), entryPath));
                            }

                            if (EnvJson.Obj(entry, "info") is { } info)
                            {
                                var mapped = ToInfo(entryPath, info, windows);
                                if (mapped.IsOk) entries.Add(mapped.Value);
                            }
                        }
                    }

                    _done = EnvJson.Flag(reply.Json, "done");
                }

                return Result<DirPage, FileError>.Ok(new DirPage(entries, _done));
            }
            catch (Exception error)
            {
                return Result<DirPage, FileError>.Err(RemoteErrors.ToFileError(error, path));
            }
        }

        public async Task CloseAsync(Context context)
        {
            if (_closed) return;
            _closed = true;
            await _handle.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary><c>NodeTextLineReader</c>：对一个像 <c>open(path, "r")</c> 打开的文件做位置读。</summary>
    private sealed class RemoteTextLineReader : ITextLineReader
    {
        private readonly Handle _handle;
        private readonly StreamDecoder _decoder = new();
        private long _offset;
        private string _buffered = "";
        private bool _ended;
        private bool _closed;

        public RemoteTextLineReader(Handle handle) => _handle = handle;

        public async Task<Result<TextLine?, FileError>> ReadLineAsync(Context context)
        {
            var path = _handle.Path;
            if (RemoteErrors.AbortResult<TextLine?>(context, path) is { } aborted) return aborted;
            if (_closed)
            {
                return Result<TextLine?, FileError>.Err(new FileError(FileErrorCode.Invalid, "Text line reader is closed", path));
            }

            try
            {
                while (true)
                {
                    var newline = _buffered.IndexOf('\n');
                    if (newline != -1)
                    {
                        var text = _buffered[..newline];
                        _buffered = _buffered[(newline + 1)..];
                        return Result<TextLine?, FileError>.Ok(new TextLine(text, Terminated: true));
                    }

                    if (_ended)
                    {
                        if (_buffered.Length == 0) return Result<TextLine?, FileError>.Ok(null);
                        var tail = _buffered;
                        _buffered = "";
                        return Result<TextLine?, FileError>.Ok(new TextLine(tail, Terminated: false));
                    }

                    var bytes = await _handle.PreadAsync(_offset, LineChunk).ConfigureAwait(false);
                    if (RemoteErrors.AbortResult<TextLine?>(context, path) is { } afterRead) return afterRead;
                    _offset += bytes.Length;
                    if (bytes.Length == 0)
                    {
                        _buffered += _decoder.Decode(null);
                        _ended = true;
                    }
                    else
                    {
                        _buffered += _decoder.Decode(bytes);
                    }
                }
            }
            catch (Exception error)
            {
                return Result<TextLine?, FileError>.Err(RemoteErrors.ToFileError(error, path));
            }
        }

        public async Task CloseAsync(Context context)
        {
            if (_closed) return;
            _closed = true;
            _buffered = "";
            await _handle.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---------- 文件操作 ----------

    private async Task<Result<T, FileError>> FileOpAsync<T>(
        string path,
        Context context,
        Func<string, Task<T>> run,
        bool checkAfter = false)
    {
        string resolved;
        try
        {
            resolved = await ResolveAsync(path).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result<T, FileError>.Err(RemoteErrors.ToFileError(error, path));
        }

        if (RemoteErrors.AbortResult<T>(context, resolved) is { } aborted) return aborted;
        try
        {
            var value = await run(resolved).ConfigureAwait(false);
            if (checkAfter && RemoteErrors.AbortResult<T>(context, resolved) is { } after) return after;
            return Result<T, FileError>.Ok(value);
        }
        catch (Exception error)
        {
            return Result<T, FileError>.Err(RemoteErrors.ToFileError(error, resolved));
        }
    }

    private async Task<(Handle Handle, JsonObject Json)> OpenAsync(string resolved, JsonObject json)
    {
        var body = new JsonObject { ["path"] = resolved };
        foreach (var (key, value) in json) body[key] = value?.DeepClone();
        var reply = await Connection.RequestAsync("open", body).ConfigureAwait(false);
        var handle = new Handle(this, EnvJson.Long(reply.Json, "handle") ?? 0, reply.Session, resolved);
        return (handle, reply.Json);
    }

    public async Task<Result<string, FileError>> AbsolutePathAsync(string path, Context context)
    {
        try
        {
            return Result<string, FileError>.Ok(await ResolveAsync(path).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(RemoteErrors.ToFileError(error, path));
        }
    }

    public async Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, Context context)
    {
        try
        {
            var windows = await IsWindowsRemoteAsync().ConfigureAwait(false);
            return Result<string, FileError>.Ok(NodePath.Join(windows, [..parts]));
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(RemoteErrors.ToFileError(error));
        }
    }

    /// <summary>Node 的 <c>readFile</c>，带或不带 UTF-8 解码，作用在一个像 <c>open(path, "r")</c> 打开的文件上。</summary>
    private Task<Result<FileRead, FileError>> ReadFileAsync(string path, bool decode, Context context) =>
        FileOpAsync<FileRead>(path, context, async resolved =>
        {
            var (handle, json) = await OpenAsync(resolved, new JsonObject { ["mode"] = "read" }).ConfigureAwait(false);
            var signal = context.AbortSignal;
            bool Aborted() => signal is { IsCancellationRequested: true };
            FileError AbortError() => new(FileErrorCode.Aborted, "The operation was aborted", resolved);
            try
            {
                if (EnvJson.Obj(json, "statError") is { } statError) throw new RemoteError(statError);
                if (Aborted()) throw AbortError();
                var stat = EnvJson.Obj(json, "stat") ?? new JsonObject();
                var size = EnvJson.Str(stat, "kind") == "file" ? EnvJson.Long(stat, "size") ?? 0 : 0;
                if (size > ReadFileMax)
                {
                    throw new FileError(FileErrorCode.Unknown, $"File size ({size}) is greater than 2 GiB", resolved);
                }

                if (size == 0)
                {
                    // 大小未知：顺序读到结尾，如 FIFO 与设备。
                    var decoder = new NodeStringDecoder();
                    var chunks = new List<byte[]>();
                    long total = 0;
                    var text = "";
                    while (true)
                    {
                        if (Aborted()) throw AbortError();
                        var bytes = await handle.PreadAsync(null, ReadFileUnknownChunk).ConfigureAwait(false);
                        if (bytes.Length == 0) break;
                        total += bytes.Length;
                        chunks.Add(bytes);
                        if (decode) text += decoder.Write(bytes);
                    }

                    if (!decode) return new FileRead(Concat(chunks, total), null);
                    return new FileRead(null, total == 0 ? "" : text + decoder.End());
                }

                if (!decode)
                {
                    // 整块一次读，或按 512 KiB 读到大小或文件末尾。
                    if (size <= ReadFileChunk)
                    {
                        return new FileRead(await handle.PreadAsync(0, size).ConfigureAwait(false), null);
                    }

                    var read = await ReadRangeAsync(
                        (at, length) => handle.PreadAsync(at, length),
                        0,
                        size,
                        ReadFileChunk,
                        Aborted).ConfigureAwait(false);
                    if (read is null) throw AbortError();
                    return new FileRead(Concat(read.Value.Chunks, read.Value.Total), null);
                }

                // 解码：按 min(size, 512 KiB) 读到大小、短读或文件末尾。
                var length2 = Math.Min(size, ReadFileChunk);
                var textDecoder = new NodeStringDecoder();
                var accumulated = "";
                long readTotal = 0;
                var first = true;
                var pending = new Queue<Task<byte[]>>();
                long next = 0;
                while (true)
                {
                    while (pending.Count < ReadDepth)
                    {
                        var bytes = handle.PreadAsync(next, length2);
                        Observe(bytes);
                        pending.Enqueue(bytes);
                        next += length2;
                    }

                    var bytes2 = await pending.Dequeue().ConfigureAwait(false);
                    if (Aborted()) throw AbortError();
                    readTotal += bytes2.Length;
                    if (bytes2.Length == 0 || readTotal == size || bytes2.Length != length2)
                    {
                        return new FileRead(
                            null,
                            first ? Utf8.GetString(bytes2) : accumulated + textDecoder.Write(bytes2) + textDecoder.End());
                    }

                    accumulated += textDecoder.Write(bytes2);
                    first = false;
                }
            }
            finally
            {
                await handle.CloseAsync().ConfigureAwait(false);
            }
        });

    public async Task<Result<string, FileError>> ReadTextFileAsync(string path, Context context)
    {
        var result = await ReadFileAsync(path, decode: true, context).ConfigureAwait(false);
        return result.IsOk
            ? Result<string, FileError>.Ok(result.Value.Text!)
            : Result<string, FileError>.Err(result.Error);
    }

    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, Context context)
    {
        var result = await ReadFileAsync(path, decode: false, context).ConfigureAwait(false);
        return result.IsOk
            ? Result<byte[], FileError>.Ok(result.Value.Bytes!)
            : Result<byte[], FileError>.Err(result.Error);
    }

    public async Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(
        string path,
        OpenBinaryOptions? options,
        Context context)
    {
        var opened = await FileOpAsync(
            path,
            context,
            async resolved => (await OpenAsync(
                resolved,
                new JsonObject { ["noFollow"] = options?.NoFollow == true }).ConfigureAwait(false)).Handle).ConfigureAwait(false);
        if (!opened.IsOk) return Result<IBinaryReader, FileError>.Err(opened.Error);
        if (RemoteErrors.AbortResult<IBinaryReader>(context, opened.Value.Path) is { } aborted)
        {
            await opened.Value.CloseAsync().ConfigureAwait(false);
            return aborted;
        }

        return Result<IBinaryReader, FileError>.Ok(new RemoteBinaryReader(opened.Value));
    }

    public async Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, Context context)
    {
        var opened = await FileOpAsync(
            path,
            context,
            async resolved => (await OpenAsync(
                resolved,
                new JsonObject { ["mode"] = "read" }).ConfigureAwait(false)).Handle).ConfigureAwait(false);
        if (!opened.IsOk) return Result<ITextLineReader, FileError>.Err(opened.Error);
        if (RemoteErrors.AbortResult<ITextLineReader>(context, opened.Value.Path) is { } aborted)
        {
            await opened.Value.CloseAsync().ConfigureAwait(false);
            return aborted;
        }

        return Result<ITextLineReader, FileError>.Ok(new RemoteTextLineReader(opened.Value));
    }

    public async Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path,
        ReadTextLinesOptions? options,
        Context context)
    {
        if (options?.MaxLines is { } maxLines && maxLines <= 0)
        {
            return Result<IReadOnlyList<string>, FileError>.Ok(Array.Empty<string>());
        }

        var opened = await OpenTextLineReaderAsync(path, context).ConfigureAwait(false);
        if (!opened.IsOk) return Result<IReadOnlyList<string>, FileError>.Err(opened.Error);
        var lines = new List<string>();
        try
        {
            while (options?.MaxLines is not { } limit || lines.Count < limit)
            {
                var line = await opened.Value.ReadLineAsync(context).ConfigureAwait(false);
                if (!line.IsOk) return Result<IReadOnlyList<string>, FileError>.Err(line.Error);
                if (line.Value is null) break;
                lines.Add(line.Value.Text);
            }

            return Result<IReadOnlyList<string>, FileError>.Ok(lines);
        }
        finally
        {
            await opened.Value.CloseAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Node 的 <c>writeFile</c>（每次 512 KiB 之前检查中止）或 <c>appendFile</c>（不检查，最后一次之后检查）：首个请求创建父目录、
    /// 打开并写第一块；后续块写同一个打开的文件。
    /// </summary>
    private Task<Result<Unit, FileError>> WriteAsync(string path, FileContent content, bool append, Context context)
    {
        var bytes = content.Bytes ?? Utf8.GetBytes(content.Text!);
        var signal = append ? null : context.AbortSignal;
        return FileOpAsync<Unit>(
            path,
            context,
            async resolved =>
            {
                var first = bytes.AsSpan(0, Math.Min(bytes.Length, WriteChunk)).ToArray();
                var keep = bytes.Length > WriteChunk;
                var reply = await Connection.RequestAsync(
                    "write",
                    new JsonObject { ["path"] = resolved, ["append"] = append, ["keep"] = keep },
                    new RequestOptions { Payload = first }).ConfigureAwait(false);
                if (!keep) return Unit.Value;
                var handle = new Handle(this, EnvJson.Long(reply.Json, "handle") ?? 0, reply.Session, resolved);
                var inFlight = new List<Task<Reply>>();
                try
                {
                    for (var offset = WriteChunk; offset < bytes.Length; offset += WriteChunk)
                    {
                        if (signal is { IsCancellationRequested: true })
                        {
                            // 早于该检查失败的块让整个写入失败，如同顺序执行时那样。
                            foreach (var written in inFlight) await written.ConfigureAwait(false);

                            throw new FileError(FileErrorCode.Aborted, "The operation was aborted", resolved);
                        }

                        var chunk = bytes.AsSpan(offset, Math.Min(WriteChunk, bytes.Length - offset)).ToArray();
                        var pending = handle.RequestAsync("writeChunk", null, null, chunk);
                        Observe(pending);
                        inFlight.Add(pending);
                        if (inFlight.Count >= WriteDepth)
                        {
                            await inFlight[0].ConfigureAwait(false);
                            inFlight.RemoveAt(0);
                        }
                    }

                    // 第一个失败；后续块被守护进程拒绝，所以文件没有空洞。
                    foreach (var written in inFlight) await written.ConfigureAwait(false);
                    return Unit.Value;
                }
                finally
                {
                    foreach (var written in inFlight) Observe(written);
                    await handle.CloseAsync().ConfigureAwait(false);
                }
            },
            checkAfter: append);
    }

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, FileContent content, Context context) =>
        WriteAsync(path, content, append: false, context);

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, FileContent content, Context context) =>
        WriteAsync(path, content, append: true, context);

    public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, Context context) =>
        FileOpAsync<Unit>(
            path,
            context,
            async resolved =>
            {
                if (size < 0)
                {
                    throw new FileError(FileErrorCode.Invalid, "File size must be a non-negative safe integer", resolved);
                }

                await Connection.RequestAsync(
                    "truncate",
                    new JsonObject { ["path"] = resolved, ["size"] = size }).ConfigureAwait(false);
                return Unit.Value;
            },
            checkAfter: true);

    public Task<Result<Unit, FileError>> FlushFileAsync(string path, Context context) =>
        FileOpAsync<Unit>(
            path,
            context,
            async resolved =>
            {
                await Connection.RequestAsync("fsync", new JsonObject { ["path"] = resolved }).ConfigureAwait(false);
                return Unit.Value;
            },
            checkAfter: true);

    public async Task<Result<Unit, FileError>> RenameFileAsync(
        string sourcePath,
        string destinationPath,
        Context context)
    {
        string source;
        string destination;
        try
        {
            source = await ResolveAsync(sourcePath).ConfigureAwait(false);
            destination = await ResolveAsync(destinationPath).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(RemoteErrors.ToFileError(error, sourcePath));
        }

        if (RemoteErrors.AbortResult<Unit>(context, destination) is { } aborted) return aborted;
        try
        {
            await Connection.RequestAsync(
                "rename",
                new JsonObject { ["path"] = source, ["to"] = destination }).ConfigureAwait(false);
            return Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(RemoteErrors.ToFileError(error, source));
        }
    }

    public async Task<Result<FileInfo, FileError>> FileInfoAsync(string path, Context context)
    {
        var result = await FileOpAsync(
            path,
            context,
            async resolved =>
            {
                var reply = await Connection.RequestAsync("lstat", new JsonObject { ["path"] = resolved })
                    .ConfigureAwait(false);
                return ToInfo(resolved, reply.Json, await IsWindowsRemoteAsync().ConfigureAwait(false));
            }).ConfigureAwait(false);
        return result.IsOk ? result.Value : Result<FileInfo, FileError>.Err(result.Error);
    }

    public Task<Result<IReadOnlyList<FileInfo>, FileError>> ListDirAsync(string path, Context context) =>
        FileOpAsync<IReadOnlyList<FileInfo>>(path, context, async resolved =>
        {
            var (handle, _) = await OpenDirAsync(resolved).ConfigureAwait(false);
            var entries = new List<JsonObject>();
            try
            {
                for (var done = false; !done;)
                {
                    var reply = await handle.RequestAsync("readdir", new JsonObject { ["max"] = 1000 })
                        .ConfigureAwait(false);
                    if (EnvJson.Arr(reply.Json, "entries") is { } list)
                    {
                        foreach (var item in list)
                        {
                            if (item is JsonObject entry) entries.Add(entry);
                        }
                    }

                    done = EnvJson.Flag(reply.Json, "done");
                }
            }
            finally
            {
                await handle.CloseAsync().ConfigureAwait(false);
            }

            // Node 的 readdir 在任一条目 lstat 失败时失败。libuv 在 POSIX 上按字节排序；Windows 保持文件系统的顺序。
            var windows = await IsWindowsRemoteAsync().ConfigureAwait(false);
            if (!windows)
            {
                entries.Sort((left, right) => CompareBytes(RawName(left), RawName(right)));
            }

            var infos = new List<FileInfo>();
            foreach (var entry in entries)
            {
                if (context.AbortSignal is { IsCancellationRequested: true })
                {
                    throw new FileError(FileErrorCode.Aborted, "aborted", resolved);
                }

                var entryPath = NodePath.Resolve(windows, resolved, EnvJson.Str(entry, "name") ?? "");
                if (EnvJson.Obj(entry, "error") is { } failure) throw RemoteErrors.ToFileError(new RemoteError(failure), entryPath);
                if (EnvJson.Obj(entry, "info") is { } info)
                {
                    var mapped = ToInfo(entryPath, info, windows);
                    if (mapped.IsOk) infos.Add(mapped.Value);
                }
            }

            return (IReadOnlyList<FileInfo>)infos;
        });

    private static byte[] RawName(JsonObject entry)
    {
        if (EnvJson.Arr(entry, "raw") is { } raw)
        {
            var bytes = new byte[raw.Count];
            for (var i = 0; i < raw.Count; i++)
            {
                bytes[i] = (byte)(raw[i]?.GetValue<long>() ?? 0);
            }

            return bytes;
        }

        return Utf8.GetBytes(EnvJson.Str(entry, "name") ?? "");
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            var diff = left[i] - right[i];
            if (diff != 0) return diff;
        }

        return left.Length - right.Length;
    }

    private async Task<(Handle Handle, JsonObject Json)> OpenDirAsync(string resolved)
    {
        var reply = await Connection.RequestAsync("opendir", new JsonObject { ["path"] = resolved }).ConfigureAwait(false);
        return (new Handle(this, EnvJson.Long(reply.Json, "handle") ?? 0, reply.Session, resolved), reply.Json);
    }

    public async Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, Context context)
    {
        var opened = await FileOpAsync(
            path,
            context,
            async resolved => (await OpenDirAsync(resolved).ConfigureAwait(false)).Handle).ConfigureAwait(false);
        if (!opened.IsOk) return Result<IDirReader, FileError>.Err(opened.Error);
        if (RemoteErrors.AbortResult<IDirReader>(context, opened.Value.Path) is { } aborted)
        {
            await opened.Value.CloseAsync().ConfigureAwait(false);
            return aborted;
        }

        return Result<IDirReader, FileError>.Ok(new RemoteDirReader(opened.Value));
    }

    public Task<Result<string, FileError>> CanonicalPathAsync(string path, Context context) =>
        FileOpAsync(path, context, async resolved =>
        {
            var reply = await Connection.RequestAsync("realpath", new JsonObject { ["path"] = resolved })
                .ConfigureAwait(false);
            return EnvJson.Str(reply.Json, "path") ?? resolved;
        });

    public async Task<Result<bool, FileError>> ExistsAsync(string path, Context context)
    {
        var result = await FileInfoAsync(path, context).ConfigureAwait(false);
        if (result.IsOk) return Result<bool, FileError>.Ok(true);
        if (result.Error.Code == FileErrorCode.NotFound) return Result<bool, FileError>.Ok(false);
        return Result<bool, FileError>.Err(result.Error);
    }

    public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, Context context) =>
        FileOpAsync<Unit>(path, context, async resolved =>
        {
            await Connection.RequestAsync(
                "mkdir",
                new JsonObject { ["path"] = resolved, ["recursive"] = options?.Recursive ?? true }).ConfigureAwait(false);
            return Unit.Value;
        });

    public Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, Context context) =>
        FileOpAsync<Unit>(path, context, async resolved =>
        {
            await Connection.RequestAsync(
                "rm",
                new JsonObject
                {
                    ["path"] = resolved,
                    ["recursive"] = options?.Recursive ?? false,
                    ["force"] = options?.Force ?? false,
                }).ConfigureAwait(false);
            return Unit.Value;
        });

    public async Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, Context context)
    {
        if (RemoteErrors.AbortResult<string>(context) is { } aborted) return aborted;
        try
        {
            var info = await Connection.InfoAsync().ConfigureAwait(false);
            var windows = info.Os == "windows";
            var reply = await Connection.RequestAsync(
                "mkdtemp",
                new JsonObject { ["path"] = NodePath.Join(windows, info.TmpDir, prefix ?? "tmp-") }).ConfigureAwait(false);
            return Result<string, FileError>.Ok(EnvJson.Str(reply.Json, "path") ?? "");
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(RemoteErrors.ToFileError(error));
        }
    }

    public async Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, Context context)
    {
        var directory = await CreateTempDirAsync("tmp-", context).ConfigureAwait(false);
        if (!directory.IsOk) return Result<string, FileError>.Err(directory.Error);
        var filePath = "";
        try
        {
            var windows = await IsWindowsRemoteAsync().ConfigureAwait(false);
            filePath = NodePath.Join(
                windows,
                directory.Value,
                $"{options?.Prefix ?? ""}{Guid.NewGuid()}{options?.Suffix ?? ""}");
            // Node 的 writeFile(filePath, "")，不创建父目录。
            await Connection.RequestAsync(
                "write",
                new JsonObject { ["path"] = filePath, ["append"] = false, ["parents"] = false },
                new RequestOptions { Payload = [] }).ConfigureAwait(false);
            return Result<string, FileError>.Ok(filePath);
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(RemoteErrors.ToFileError(error, filePath));
        }
    }

    public async Task<Result<IFileWatcher, FileError>> WatchAsync(
        IReadOnlyList<WatchTarget> targets,
        Action<WatchChange> onChange,
        Context context)
    {
        if (RemoteErrors.AbortResult<IFileWatcher>(context) is { } aborted) return aborted;
        try
        {
            var resolved = new List<WatchTarget>(targets.Count);
            foreach (var target in targets)
            {
                resolved.Add(target with { Path = await ResolveAsync(target.Path).ConfigureAwait(false) });
            }

            if (RemoteErrors.AbortResult<IFileWatcher>(context) is { } afterResolve) return afterResolve;
            var watcher = await RemoteWatcher.OpenAsync(Connection, resolved, onChange, _watchOptions).ConfigureAwait(false);
            if (RemoteErrors.AbortResult<IFileWatcher>(context) is { } afterOpen)
            {
                await watcher.CloseAsync(context).ConfigureAwait(false);
                return afterOpen;
            }

            return Result<IFileWatcher, FileError>.Ok(watcher);
        }
        catch (Exception error)
        {
            return Result<IFileWatcher, FileError>.Err(RemoteErrors.ToFileError(error));
        }
    }

    // ---------- 命令 ----------

    public async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        ShellCommand command,
        ShellExecOptions? options,
        Context context)
    {
        var signal = context.AbortSignal;
        if (signal is { IsCancellationRequested: true })
        {
            return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Aborted, "aborted"));
        }

        var timeout = options?.Timeout;
        if (timeout is { } seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            {
                return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(
                    ExecutionErrorCode.Timeout,
                    "Invalid timeout: must be a finite number of seconds"));
            }

            if (seconds * 1000 > MaxTimeoutMs)
            {
                return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(
                    ExecutionErrorCode.Timeout,
                    FormattableString.Invariant($"Invalid timeout: maximum is {MaxTimeoutSeconds} seconds")));
            }
        }

        var inheritEnv = options?.InheritEnv ?? true;
        string cwd;
        try
        {
            cwd = options?.Cwd is { } requested ? await ResolveAsync(requested).ConfigureAwait(false) : Cwd;
        }
        catch (Exception error)
        {
            return Result<ShellExecResult, ExecutionError>.Err(
                new ExecutionError(ExecutionErrorCode.Unknown, error.Message));
        }

        if (signal is { IsCancellationRequested: true })
        {
            return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Aborted, "aborted"));
        }

        var env = new JsonObject();
        if (inheritEnv)
        {
            foreach (var (key, value) in _shellEnv ?? new Dictionary<string, string>())
            {
                env[EnvJson.WellFormed(key)] = value;
            }
        }

        foreach (var (key, value) in options?.Env ?? new Dictionary<string, string>())
        {
            env[EnvJson.WellFormed(key)] = value;
        }

        ExecutionError? callbackError = null;
        var settled = false;
        long? running = null;
        using var controller = new CancellationTokenSource();
        void OnAbort() => controller.Cancel();
        if (signal is { } token)
        {
            token.Register(OnAbort);
        }

        try
        {
            var json = new JsonObject
            {
                ["cwd"] = cwd,
                ["env"] = env,
                ["inheritEnv"] = inheritEnv,
            };
            if (command.Line is { } line) json["command"] = line;
            else
            {
                var argv = new JsonArray();
                foreach (var argument in command.Argv ?? []) argv.Add(argument);
                json["argv"] = argv;
            }

            if (_shellPath is { } shellPath) json["shellPath"] = shellPath;
            if (timeout is { } timeoutSeconds) json["timeoutMs"] = timeoutSeconds * 1000;
            if (options?.Spill is { } spill)
            {
                json["spill"] = new JsonObject { ["afterBytes"] = spill.AfterBytes, ["afterLines"] = spill.AfterLines };
            }

            if (options?.Window is { } window)
            {
                json["window"] = new JsonObject
                {
                    ["maxBytes"] = window.MaxBytes,
                    ["maxLines"] = window.MaxLines,
                    ["minIntervalMs"] = window.MinIntervalMs,
                    ["bytesPerSecond"] = window.BytesPerSecond,
                };
            }

            var reply = await Connection.RequestAsync(
                "exec",
                json,
                new RequestOptions
                {
                    Signal = controller.Token,
                    OnStart = (requestId, session) =>
                    {
                        running = requestId;
                        lock (_runningGate) _running[requestId] = session;
                    },
                    OnEvent = (eventJson, payload) =>
                    {
                        if (settled || callbackError is not null) return;
                        if (EnvJson.Str(eventJson, "kind") != "output") return;
                        var text = Encoding.UTF8.GetString(payload);
                        if (text.Length == 0 || options?.OnOutput is null) return;
                        var info = new ShellOutputInfo
                        {
                            Stream = EnvJson.Str(eventJson, "stream") == "stderr"
                                ? ShellOutputStream.Stderr
                                : ShellOutputStream.Stdout,
                            Skipped = EnvJson.Obj(eventJson, "skipped") is { } skipped
                                ? new ShellOutputSkip(
                                    EnvJson.Long(skipped, "bytes") ?? 0,
                                    EnvJson.Long(skipped, "newlines") ?? 0,
                                    EnvJson.Flag(skipped, "endsWithNewline"))
                                : null,
                        };
                        try
                        {
                            options.OnOutput(text, context, info);
                        }
                        catch (Exception error)
                        {
                            callbackError = new ExecutionError(ExecutionErrorCode.CallbackError, error.Message, error);
                            controller.Cancel();
                        }
                    },
                }).ConfigureAwait(false);

            if (callbackError is not null) return Result<ShellExecResult, ExecutionError>.Err(callbackError);
            return Result<ShellExecResult, ExecutionError>.Ok(new ShellExecResult(
                EnvJson.Long(reply.Json, "exitCode") ?? 0,
                EnvJson.Str(reply.Json, "spillPath")));
        }
        catch (Exception error)
        {
            if (callbackError is not null) return Result<ShellExecResult, ExecutionError>.Err(callbackError);
            if (error is not RemoteError remote)
            {
                return Result<ShellExecResult, ExecutionError>.Err(
                    new ExecutionError(ExecutionErrorCode.Unknown, error.Message));
            }

            var spillPath = EnvJson.Str(remote.Fields, "spillPath");
            var failure = remote.Code switch
            {
                "timeout" => new ExecutionError(
                    ExecutionErrorCode.Timeout,
                    FormattableString.Invariant($"timeout:{timeout}"),
                    spillPath: spillPath),
                "aborted" => new ExecutionError(ExecutionErrorCode.Aborted, "aborted", spillPath: spillPath),
                "shell_unavailable" => new ExecutionError(
                    ExecutionErrorCode.ShellUnavailable,
                    remote.Message,
                    spillPath: spillPath),
                "spawn_error" => new ExecutionError(ExecutionErrorCode.SpawnError, remote.Message, spillPath: spillPath),
                _ => new ExecutionError(ExecutionErrorCode.Unknown, remote.Message, spillPath: spillPath),
            };
            return Result<ShellExecResult, ExecutionError>.Err(failure);
        }
        finally
        {
            settled = true;
            if (running is { } id)
            {
                lock (_runningGate) _running.Remove(id);
            }
        }
    }

    public Task CleanupAsync(Context context)
    {
        // 杀而不中止，让命令以被杀的退出状态落定，如 NodeExecutionEnv。
        KeyValuePair<long, long>[] running;
        lock (_runningGate)
        {
            running = [.._running];
            _running.Clear();
        }

        foreach (var (id, session) in running) Connection.Kill(id, session);
        return Task.CompletedTask;
    }

    private readonly record struct FileRead(byte[]? Bytes, string? Text);
}
