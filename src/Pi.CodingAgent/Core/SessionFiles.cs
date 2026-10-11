using System.Text;
using System.Text.Json;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 会话头扫描超出上限（首行之前扫过了 <see cref="MaxSessionHeaderScanBytes"/> 字节仍未遇到换行）。
/// 对应 TS <c>SessionHeaderScanLimitError</c>。
/// </summary>
public sealed class SessionHeaderScanLimitException(string filePath)
    : InvalidOperationException($"Session header exceeds {SessionFiles.MaxSessionHeaderScanBytes}-byte scan limit: {filePath}");

/// <summary>
/// <c>core/session-manager.ts</c> 的文件 I/O 与目录发现（4e-2c-2）：JSONL 逐行加载与修复、
/// 有界会话头探测、最近会话发现、会话信息构建与并发目录扫描。
/// <see cref="SessionManager"/> 类本体在 4e-2c-3。
/// </summary>
public static class SessionFiles
{
    /// <summary>TS <c>SESSION_READ_BUFFER_SIZE</c>。</summary>
    public const int SessionReadBufferSize = 1024 * 1024;

    /// <summary>TS <c>SESSION_HEADER_READ_BUFFER_SIZE</c>。</summary>
    private const int SessionHeaderReadBufferSize = 4096;

    /// <summary>同步头部探测的上限，允许较大的 cwd 与自定义元数据字段。TS <c>MAX_SESSION_HEADER_SCAN_BYTES</c>。</summary>
    public const int MaxSessionHeaderScanBytes = 1024 * 1024;

    /// <summary>TS <c>MAX_CONCURRENT_SESSION_INFO_LOADS</c>。</summary>
    private const int MaxConcurrentSessionInfoLoads = 10;

    /// <summary>TS <c>CURRENT_SESSION_LIST_PUBLISH_INTERVAL</c>。</summary>
    private const int CurrentSessionListPublishInterval = 10;

    // ─────────────────────────────────────────────────────────────────────────────
    // 会话目录
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 返回 cwd 的会话目录，不存在则创建。对应 TS <c>getDefaultSessionDir</c>
    /// （目录名编码见 <see cref="SessionEntries.GetDefaultSessionDirPath"/>）。
    /// </summary>
    public static string GetDefaultSessionDir(string cwd, string? agentDir = null)
    {
        var sessionDir = SessionEntries.GetDefaultSessionDirPath(cwd, agentDir);
        if (!Directory.Exists(sessionDir))
        {
            Directory.CreateDirectory(sessionDir);
        }

        return sessionDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 逐行加载
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析单行 JSONL。空行与无法反序列化的行跳过（返回 null）。对应 TS <c>parseSessionEntryLine</c>。
    /// </summary>
    /// <remarks>
    /// 差异 2c2-1：TS 的 <c>JSON.parse</c> 不校验结构，「JSON 合法但缺必需字段」的行会被保留原样，
    /// 端口的强类型反序列化会抛 <see cref="JsonException"/> 而跳过该行。真实会话文件由两端自身写出、
    /// 字段恒全，只有手工编辑的文件会走到该差异。
    /// </remarks>
    private static FileEntry? ParseSessionEntryLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileEntry>(line, SessionEntries.JsonOptions);
        }
        catch (JsonException)
        {
            // Skip malformed lines
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 逐行加载会话文件（1 MiB 缓冲流式读，UTF-8 多字节序列可跨块）。文件缺失返回空表；
    /// 首条可解析条目不是 <see cref="SessionHeader"/>（TS 校验 <c>type === "session"</c> 且
    /// <c>id</c> 为 string）时整个文件按空处理；最后一行缺少换行符时补写一个换行修复。
    /// 对应 TS <c>loadEntriesFromFile</c>（exported for testing）。
    /// </summary>
    public static IReadOnlyList<FileEntry> LoadEntriesFromFile(string filePath)
    {
        var resolvedFilePath = Paths.NormalizePath(filePath);
        if (!File.Exists(resolvedFilePath))
        {
            return [];
        }

        var entries = new List<FileEntry>();
        var pending = new StringBuilder();
        using (var stream = new FileStream(
                   resolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, SessionReadBufferSize))
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var buffer = new byte[SessionReadBufferSize];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(SessionReadBufferSize)];

            while (true)
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                // 与 TS 一致：BOM 不剥（StringDecoder("utf8") 不处理 BOM），带 BOM 的首行会
                // JSON.parse 失败而被跳过，进而头校验失败、整个文件按空处理。
                var charsRead = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                pending.Append(chars, 0, charsRead);
                FlushCompleteLines(pending, entries);
            }

            // StringDecoder.end()：冲刷跨块残余的 UTF-8 状态。
            var tail = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
            pending.Append(chars, 0, tail);
        }

        var finalEntry = ParseSessionEntryLine(pending.ToString());
        if (finalEntry is not null)
        {
            entries.Add(finalEntry);
        }

        // Validate session header before repairing the file.
        if (entries.Count == 0)
        {
            return entries;
        }

        // TS 检查 type === "session" 且 id 为 string；C# 的 SessionHeader.Id 是 required，
        // 反序列化成功即满足。首条不是头部时整个文件按空处理。
        if (entries[0] is not SessionHeader)
        {
            return [];
        }

        if (pending.Length > 0)
        {
            File.AppendAllText(resolvedFilePath, "\n");
        }

        return entries;
    }

    /// <summary>把 <paramref name="pending"/> 里的完整行刷成条目，保留最后一个（可能不完整的）残余。</summary>
    private static void FlushCompleteLines(StringBuilder pending, List<FileEntry> entries)
    {
        var lineStart = 0;
        var newlineIndex = IndexOf(pending, '\n', lineStart);
        while (newlineIndex >= 0)
        {
            var entry = ParseSessionEntryLine(pending.ToString(lineStart, newlineIndex - lineStart));
            if (entry is not null)
            {
                entries.Add(entry);
            }

            lineStart = newlineIndex + 1;
            newlineIndex = IndexOf(pending, '\n', lineStart);
        }

        pending.Remove(0, lineStart);
    }

    /// <summary><see cref="StringBuilder"/> 没有带起点的 <c>IndexOf</c>，手写顺序扫描。</summary>
    private static int IndexOf(StringBuilder text, char value, int startIndex)
    {
        for (var index = startIndex; index < text.Length; index++)
        {
            if (text[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 会话头探测
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查物理行是否为首个可解析的会话条目。空行与畸形行跳过（与 <see cref="LoadEntriesFromFile"/>
    /// 一致）。返回 <c>(header, true)</c> 表示找到了头部；<c>(null, true)</c> 表示解析到的是
    /// 非头部条目（停止扫描）；<c>(null, false)</c> 表示继续扫描。对应 TS
    /// <c>parseSessionHeaderCandidate</c>（undefined=继续 / null=停止 / header）。
    /// </summary>
    private static (SessionHeader? Header, bool Resolved) ParseSessionHeaderCandidate(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, false);
        }

        var entry = ParseSessionEntryLine(line);
        if (entry is null)
        {
            return (null, false);
        }

        if (entry is not SessionHeader header)
        {
            return (null, true);
        }

        return (header, true);
    }

    /// <summary>
    /// 读取文件首行作为会话头（4 KiB 缓冲、1 MiB 有界扫描、边界处探测 EOF 以允许恰好在
    /// 上限结束的无换行头行）。对应 TS <c>readSessionHeader</c>；未找到返回 null。
    /// </summary>
    private static SessionHeader? ReadSessionHeader(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, SessionHeaderReadBufferSize);
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[SessionHeaderReadBufferSize];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(SessionHeaderReadBufferSize)];
        var lineChunks = new StringBuilder();
        var scannedBytes = 0;

        while (scannedBytes < MaxSessionHeaderScanBytes)
        {
            var readLength = Math.Min(buffer.Length, MaxSessionHeaderScanBytes - scannedBytes);
            var bytesRead = stream.Read(buffer, 0, readLength);
            if (bytesRead == 0)
            {
                var tail = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
                lineChunks.Append(chars, 0, tail);
                var (header, _) = ParseSessionHeaderCandidate(lineChunks.ToString());
                return header;
            }

            scannedBytes += bytesRead;

            // 与 TS 一致：BOM 不剥（见 LoadEntriesFromFile 的注释）。
            var charsRead = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
            var chunk = new string(chars, 0, charsRead);
            var lineStart = 0;
            var newlineIndex = chunk.IndexOf('\n', lineStart);
            while (newlineIndex != -1)
            {
                lineChunks.Append(chunk, lineStart, newlineIndex - lineStart);
                var (header, resolved) = ParseSessionHeaderCandidate(lineChunks.ToString());
                if (resolved)
                {
                    return header;
                }

                lineChunks.Clear();
                lineStart = newlineIndex + 1;
                newlineIndex = chunk.IndexOf('\n', lineStart);
            }

            lineChunks.Append(chunk, lineStart, chunk.Length - lineStart);
        }

        // Probe for EOF so a final header without a newline is allowed when it ends
        // exactly at the scan limit. Any additional byte exceeds the bounded scan.
        var probe = new byte[1];
        if (stream.Read(probe, 0, probe.Length) == 0)
        {
            var tail = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
            lineChunks.Append(chars, 0, tail);
            var (header, _) = ParseSessionHeaderCandidate(lineChunks.ToString());
            return header;
        }

        throw new SessionHeaderScanLimitException(filePath);
    }

    /// <summary>
    /// 发现用的最佳努力头部读取：不可读或超大的文件不视为会话，一个损坏文件不能挡住其他会话。
    /// 对应 TS <c>readSessionHeaderForDiscovery</c>。
    /// </summary>
    private static SessionHeader? ReadSessionHeaderForDiscovery(string filePath)
    {
        try
        {
            return ReadSessionHeader(filePath);
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 最近会话发现
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 目录内 <c>.jsonl</c> 文件按修改时间降序逐个探测头部，返回第一个（可选过滤 cwd 匹配的）
    /// 会话文件路径；目录不可访问或 stat 竞态时返回 null。对应 TS <c>findMostRecentSession</c>
    /// （exported for testing）。
    /// </summary>
    public static string? FindMostRecentSession(string sessionDir, string? cwd = null)
    {
        var resolvedSessionDir = Paths.NormalizePath(sessionDir);
        var resolvedCwd = cwd is not null ? Paths.ResolvePath(cwd) : null;
        try
        {
            var directory = new DirectoryInfo(resolvedSessionDir);
            var files = directory.GetFiles()
                .Where(file => file.Name.EndsWith(".jsonl", StringComparison.Ordinal))
                .Select(file => (Path: file.FullName, Mtime: file.LastWriteTimeUtc))
                .OrderByDescending(file => file.Mtime)
                .ToList();

            foreach (var (path, _) in files)
            {
                var header = ReadSessionHeaderForDiscovery(path);
                if (header is not null
                    && (resolvedCwd is null || SessionCwdMatches(header.Cwd, resolvedCwd)))
                {
                    return path;
                }
            }

            return null;
        }
        catch
        {
            // Directory access and stat races make recent-session discovery unavailable.
            return null;
        }
    }

    /// <summary>TS <c>sessionCwdMatches</c>：cwd 非空且 resolve 后与当前一致。</summary>
    private static bool SessionCwdMatches(string headerCwd, string resolvedCwd) =>
        headerCwd.Length > 0 && Paths.ResolvePath(headerCwd) == resolvedCwd;

    // ─────────────────────────────────────────────────────────────────────────────
    // 会话信息构建
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>TS <c>extractTextContent</c>：字符串内容原样，块内容拼接 text 块（空格连接）。</summary>
    private static string ExtractTextContent(ChatMessage message) => message switch
    {
        UserMessage user => ExtractText(user.Content),
        AssistantMessage assistant => ExtractText(MessageContent.BlocksOf(assistant.Content)),
        _ => "",
    };

    private static string ExtractText(MessageContent content) => content switch
    {
        MessageContent.Text text => text.Value,
        MessageContent.Blocks blocks => string.Join(" ", blocks.Value.OfType<TextContent>().Select(block => block.Text)),
        _ => "",
    };

    /// <summary>
    /// 消息条目的活跃时间：user/assistant 的 <c>message.timestamp</c> 优先（毫秒），否则解析
    /// 条目的 ISO <c>timestamp</c>；都不可用为 null。其他 role（toolResult / bashExecution 等）
    /// 不贡献活动时间。对应 TS <c>getMessageActivityTime</c>。
    /// </summary>
    private static long? GetMessageActivityTime(SessionMessageEntry entry) => entry.Message switch
    {
        UserMessage user => user.Timestamp,
        AssistantMessage assistant => assistant.Timestamp
            ?? (TryParseTimestampMillis(entry.Timestamp, out var millis) ? millis : null),
        _ => null,
    };

    /// <summary>
    /// ISO 时间戳 → Unix 毫秒。对应 TS <c>new Date(s).getTime()</c>；解析失败（TS 的 Invalid
    /// Date / NaN）返回 false。空串恒 false。
    /// </summary>
    private static bool TryParseTimestampMillis(string? timestamp, out long millis)
    {
        millis = 0;
        if (string.IsNullOrEmpty(timestamp))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        millis = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    /// <summary>
    /// 扫描单个会话文件，产出列表项：头部校验（首个可解析条目必须 type=<c>session</c>）、最新
    /// <c>session_info</c> 名称（trim 空串视为清除）、消息计数与活跃时间、首条有文本的 user 消息。
    /// 修改时间取最新活动时间，其次头部时间，最后文件 mtime。对应 TS <c>buildSessionInfo</c>。
    /// </summary>
    private static async Task<SessionInfo?> BuildSessionInfoAsync(
        string filePath, CancellationToken signal, FileSystemInfo? fileStats = null)
    {
        try
        {
            var stats = fileStats ?? new FileInfo(filePath);
            if (!stats.Exists)
            {
                throw new FileNotFoundException(null, filePath);
            }

            SessionHeader? header = null;
            var messageCount = 0;
            var firstMessage = "";
            var allMessages = new List<string>();
            string? name = null;
            long? lastActivityTime = null;

            // TS 用 readline 流（crlfDelay: Infinity）；StreamReader 对 \r\n / \r / \n 的处理等价。
            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(signal).ConfigureAwait(false) is { } line)
            {
                var entry = ParseSessionEntryLine(line);
                if (entry is null)
                {
                    continue;
                }

                if (header is null)
                {
                    if (entry is not SessionHeader sessionHeader)
                    {
                        return null;
                    }

                    header = sessionHeader;
                    continue;
                }

                // Extract session name (use latest, including explicit clears)
                if (entry is SessionInfoEntry info)
                {
                    name = string.IsNullOrWhiteSpace(info.Name) ? null : info.Name.Trim();
                }

                if (entry is not SessionMessageEntry messageEntry)
                {
                    continue;
                }

                messageCount++;

                var activityTime = GetMessageActivityTime(messageEntry);
                if (activityTime is { } activity)
                {
                    lastActivityTime = Math.Max(lastActivityTime ?? 0, activity);
                }

                if (messageEntry.Message is not (UserMessage or AssistantMessage))
                {
                    continue;
                }

                var textContent = ExtractTextContent(messageEntry.Message);
                if (textContent.Length == 0)
                {
                    continue;
                }

                allMessages.Add(textContent);
                if (firstMessage.Length == 0 && messageEntry.Message is UserMessage)
                {
                    firstMessage = textContent;
                }
            }

            if (header is null)
            {
                return null;
            }

            // TS：created = new Date(header.timestamp)（非法字符串是 Invalid Date）；端口的
            // DateTimeOffset 无法表示 Invalid Date，解析失败折叠为 MinValue（差异 2c2-2）。
            var created = DateTimeOffset.TryParse(
                header.Timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var createdParsed)
                ? createdParsed
                : DateTimeOffset.MinValue;
            var headerTime = TryParseTimestampMillis(header.Timestamp, out var headerParsed)
                ? headerParsed
                : null as long?;
            DateTimeOffset modified;
            if (lastActivityTime is { } lastActivity && lastActivity > 0)
            {
                modified = DateTimeOffset.FromUnixTimeMilliseconds(lastActivity);
            }
            else if (headerTime is { } fromHeader)
            {
                modified = DateTimeOffset.FromUnixTimeMilliseconds(fromHeader);
            }
            else
            {
                modified = new DateTimeOffset(stats.LastWriteTimeUtc);
            }

            return new SessionInfo
            {
                Path = filePath,
                Id = header.Id,
                Cwd = header.Cwd,
                Name = name,
                ParentSessionPath = header.ParentSession,
                Created = created,
                Modified = modified,
                MessageCount = messageCount,
                FirstMessage = firstMessage.Length > 0 ? firstMessage : "(no messages)",
                AllMessagesText = string.Join(" ", allMessages),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            signal.ThrowIfCancellationRequested();
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 目录扫描
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 有界并发 map：固定数量 worker 从共享游标取任务（TS 单线程 <c>nextIndex++</c> 的并发等价）。
    /// 对应 TS <c>mapWithConcurrency</c>。
    /// </summary>
    private static async Task<R[]> MapWithConcurrency<T, R>(
        IReadOnlyList<T> items, int limit, Func<T, int, Task<R>> map, CancellationToken signal)
    {
        var results = new R[items.Count];
        var nextIndex = 0;

        async Task Worker()
        {
            while (true)
            {
                signal.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex) - 1;
                if (index >= items.Count)
                {
                    return;
                }

                results[index] = await map(items[index], index).ConfigureAwait(false);
            }
        }

        var workers = new Task[Math.Min(limit, items.Count)];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Worker();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// 按修改时间降序（新会话在前）。对应 TS <c>sortSessionInfos</c>；TS 的 <c>Array.sort</c> 是
    /// 稳定排序（修改时间相同时保持输入序），故用稳定 LINQ 排序而非 <see cref="List{T}.Sort"/>。
    /// </summary>
    private static List<SessionInfo> SortSessionInfos(List<SessionInfo> sessions) =>
        [.. sessions.OrderByDescending(session => session.Modified)];

    /// <summary>
    /// 扫描目录内的 <c>.jsonl</c> 文件并并发构建会话信息；周期性（第 1 个、每 10 个、最后一个）
    /// 发布按活跃度排序的部分结果。目录缺失或扫描失败返回空表（取消除外）。
    /// 对应 TS <c>listSessionsFromDir</c>。
    /// </summary>
    public static async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string dir, SessionListProgress? onProgress = null, CancellationToken signal = default)
    {
        signal.ThrowIfCancellationRequested();
        if (!Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            // TS 用 localeCompare 倒序；端口以 CurrentCulture 近似（语料文件名为 ASCII，两侧一致）。
            var files = Directory.GetFileSystemEntries(dir)
                .Where(file => file.EndsWith(".jsonl", StringComparison.Ordinal))
                .OrderByDescending(file => file, StringComparer.CurrentCulture)
                .ToList();
            var total = files.Count;
            var partialSessions = new List<SessionInfo>();
            var loaded = 0;
            var gate = new object();
            var results = await MapWithConcurrency(files, MaxConcurrentSessionInfoLoads,
                async (file, _) =>
                {
                    var info = await BuildSessionInfoAsync(file, signal).ConfigureAwait(false);
                    lock (gate)
                    {
                        loaded++;
                        if (info is not null)
                        {
                            partialSessions.Add(info);
                        }

                        var publishPartial =
                            loaded == 1
                            || loaded % CurrentSessionListPublishInterval == 0
                            || loaded == files.Count;
                        onProgress?.Invoke(
                            loaded, total,
                            publishPartial ? SortSessionInfos([.. partialSessions]) : null);
                    }

                    return info;
                },
                signal).ConfigureAwait(false);
            return [.. results.OfType<SessionInfo>()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            signal.ThrowIfCancellationRequested();
            return [];
        }
    }
}
