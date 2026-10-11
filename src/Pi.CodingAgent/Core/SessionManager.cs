using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

// ============================================================================
// core/session-manager.ts — 4e-2c-3（SessionManager 类）
//
// 会话是一棵 append-only 树，落在 JSONL 文件里：每条条目带 id/parentId，leaf 指针指向当前位置。
// 追加在当前 leaf 下建子节点；branch 把 leaf 移回更早的条目，历史不可变。
// 4e-2c-1 已移植类型与纯树/投影逻辑（SessionEntries），4e-2c-2 已移植文件 I/O 与目录发现
// （SessionFiles）；本文件是 <c>SessionManager</c> 类本体：构造/文件切换/追加/树遍历/分支/
// 静态工厂（create/open/continueRecent/inMemory/forkFrom/findById/list/listAll）。
//
// 与 TS 的对齐要点：
//   * 持久化门控与 TS 一致——只有出现 user/assistant 消息才首次落盘（<c>_hasConversation</c>），
//     setup 条目（模型/思考档位）留内存，打开又关闭不留文件（TS #10000）。
//   * 首写用 <c>FileMode.CreateNew</c>（对齐 TS <c>openSync(wx)</c>，已存在则抛）。
//   * 索引字典（<c>_byId</c>/<c>_labelsById</c>）都带插入序列表，复现 TS <c>Map</c> 的迭代序——
//     <c>getChildren()</c> 与 <c>createBranchedSession()</c> 的 label 收集顺序依赖它。
// ============================================================================

/// <summary>
/// 以 append-only 树的形式管理会话（JSONL 文件）。对应 TS <c>SessionManager</c>。
/// </summary>
public sealed partial class SessionManager
{
    private string _sessionId = "";
    private string? _sessionFile;
    private readonly string _sessionDir;
    private readonly string _cwd;
    private readonly bool _persist;
    private bool _flushed;
    private List<FileEntry> _fileEntries = [];

    /// <summary>条目索引。TS <c>byId: Map</c>；伴随的 <see cref="_byIdOrder"/> 复现 Map 的插入序迭代。</summary>
    private readonly Dictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _byIdOrder = [];

    /// <summary>标签索引。TS <c>labelsById</c>/<c>labelTimestampsById</c>；同样维护插入序。</summary>
    private readonly Dictionary<string, string> _labelsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labelTimestampsById = new(StringComparer.Ordinal);
    private readonly List<string> _labelsOrder = [];

    private string? _leafId;

    // name.replace(/[\r\n]+/g, " ").trim()
    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex SessionNameNewlinePattern();

    private SessionManager(
        string cwd,
        string sessionDir,
        string? sessionFile,
        bool persist,
        NewSessionOptions? newSessionOptions = null,
        IReadOnlyList<FileEntry>? preloadedFileEntries = null)
    {
        _cwd = Paths.ResolvePath(cwd);
        _sessionDir = Paths.NormalizePath(sessionDir);
        _persist = persist;
        if (_persist && _sessionDir.Length > 0 && !Directory.Exists(_sessionDir))
        {
            Directory.CreateDirectory(_sessionDir);
        }

        if (sessionFile is not null)
        {
            SetSessionFileInternal(sessionFile, preloadedFileEntries);
        }
        else if (preloadedFileEntries is { Count: > 0 })
        {
            LoadEntries(preloadedFileEntries, newSessionOptions);
        }
        else
        {
            NewSession(newSessionOptions);
        }
    }

    // ------------------------------------------------------------------
    // 文件切换
    // ------------------------------------------------------------------

    /// <summary>切换到另一个会话文件（resume 与 branching 用）。对应 TS <c>setSessionFile</c>。</summary>
    public void SetSessionFile(string sessionFile) => SetSessionFileInternal(sessionFile);

    private void SetSessionFileInternal(string sessionFile, IReadOnlyList<FileEntry>? preloadedFileEntries = null)
    {
        _sessionFile = Paths.ResolvePath(sessionFile);
        if (File.Exists(_sessionFile))
        {
            var entries = preloadedFileEntries ?? SessionFiles.LoadEntriesFromFile(_sessionFile);

            // 文件为空：写入合法的会话头初始化。非空但不是合法 pi 会话：不改文件直接失败。
            if (entries.Count == 0)
            {
                var explicitPath = _sessionFile;
                if (new FileInfo(explicitPath).Length > 0)
                {
                    throw new InvalidOperationException(
                        $"Session file is not a valid {Config.AppName} session: {explicitPath}");
                }

                NewSession();
                _sessionFile = explicitPath;
                RewriteFile();
                _flushed = true;
                return;
            }

            LoadEntries(entries);
            _flushed = true;
        }
        else
        {
            var explicitPath = _sessionFile;
            NewSession();
            _sessionFile = explicitPath; // preserve explicit path from --session flag
        }
    }

    /// <summary>开新会话。返回会话文件路径（不持久化时为 null）。对应 TS <c>newSession</c>。</summary>
    public string? NewSession(NewSessionOptions? options = null)
    {
        if (options?.Id is not null)
        {
            SessionEntries.AssertValidSessionId(options.Id);
        }

        _sessionId = options?.Id ?? SessionEntries.CreateSessionId();
        var timestamp = NowIso();
        var header = new SessionHeader
        {
            Version = SessionEntries.CurrentVersion,
            Id = _sessionId,
            Timestamp = timestamp,
            Cwd = _cwd,
            ParentSession = options?.ParentSession,
        };
        _fileEntries = [header];
        _byId.Clear();
        _byIdOrder.Clear();
        _labelsById.Clear();
        _labelTimestampsById.Clear();
        _labelsOrder.Clear();
        _leafId = null;
        _flushed = false;

        if (_persist)
        {
            var fileTimestamp = timestamp.Replace(":", "-").Replace(".", "-");
            _sessionFile = NodePath.Join(GetSessionDir(), $"{fileTimestamp}_{_sessionId}.jsonl");
        }

        return _sessionFile;
    }

    private void LoadEntries(IReadOnlyList<FileEntry> entries, NewSessionOptions? options = null)
    {
        var header = entries.OfType<SessionHeader>().FirstOrDefault();

        if (header is not null)
        {
            _fileEntries = [.. entries];
            _sessionId = header.Id;

            // TS 迁移原地改 this.fileEntries；端口返回重建后的列表，迁移了才重写文件。
            var (migratedEntries, migrated) = SessionEntries.MigrateToCurrentVersion(_fileEntries);
            if (migrated)
            {
                _fileEntries = [.. migratedEntries];
                RewriteFile();
            }
        }
        else
        {
            NewSession(options);
            _fileEntries = [.. _fileEntries, .. entries];
        }

        BuildIndex();
    }

    private void BuildIndex()
    {
        _byId.Clear();
        _byIdOrder.Clear();
        _labelsById.Clear();
        _labelTimestampsById.Clear();
        _labelsOrder.Clear();
        _leafId = null;
        foreach (var entry in _fileEntries)
        {
            if (entry is not SessionEntry sessionEntry)
            {
                continue;
            }

            // 差异：TS 对无 id 的条目（手写无 header 文件）会以 "undefined" 为键入索引并把
            // leafId 置 undefined；端口跳过该条目。真实会话文件由 pi 写出，条目恒有 id。
            if (sessionEntry.Id is not { } id)
            {
                continue;
            }

            // TS Map.set 对已存在的键不改变迭代位置：覆盖值、插入序表不加。
            if (!_byId.ContainsKey(id))
            {
                _byIdOrder.Add(id);
            }

            _byId[id] = sessionEntry;
            _leafId = id;
            if (sessionEntry is LabelEntry label)
            {
                if (label.Label is { Length: > 0 } labelValue)
                {
                    SetLabelIndex(label.TargetId, labelValue, sessionEntry.Timestamp);
                }
                else
                {
                    ClearLabelIndex(label.TargetId);
                }
            }
        }
    }

    private void SetLabelIndex(string targetId, string label, string timestamp)
    {
        if (!_labelsById.ContainsKey(targetId))
        {
            _labelsOrder.Add(targetId);
        }

        _labelsById[targetId] = label;
        _labelTimestampsById[targetId] = timestamp;
    }

    private void ClearLabelIndex(string targetId)
    {
        _labelsById.Remove(targetId);
        _labelTimestampsById.Remove(targetId);
        _labelsOrder.Remove(targetId);
    }

    // ------------------------------------------------------------------
    // 持久化
    // ------------------------------------------------------------------

    private void RewriteFile()
    {
        if (!_persist || _sessionFile is not { } file)
        {
            return;
        }

        // TS openSync("w")：截断重写。按声明类型序列化，条目才带 `type` discriminator
        // （与 TS 的 JSON.stringify 形状一致）。
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var entry in _fileEntries)
        {
            writer.Write(JsonSerializer.Serialize(entry, typeof(FileEntry), SessionEntries.JsonOptions));
            writer.Write('\n');
        }
    }

    /// <summary>
    /// 只有会话包含 user 或 assistant 消息才创建新会话文件。setup 条目（模型、思考档位、
    /// system prompt）单独留在内存——打开又关闭 pi 而没有对话就不会留下文件。从 user 消息
    /// （而非第一条 assistant 回复）开始落盘，保证首轮未完成时 prompt 已在磁盘上（TS #10000）。
    /// </summary>
    private bool HasConversation() =>
        _fileEntries.Any(e => e is SessionMessageEntry { Message: UserMessage or AssistantMessage });

    private void Persist(SessionEntry entry)
    {
        if (!_persist || _sessionFile is not { } file)
        {
            return;
        }

        if (!_flushed)
        {
            if (!HasConversation())
            {
                return;
            }

            // TS openSync("wx")：首写独占，文件已存在则抛（与 EEXIST 对应）。
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var e in _fileEntries)
                {
                    writer.Write(JsonSerializer.Serialize(e, typeof(FileEntry), SessionEntries.JsonOptions));
                    writer.Write('\n');
                }
            }

            _flushed = true;
        }
        else
        {
            File.AppendAllText(
                file, JsonSerializer.Serialize(entry, typeof(FileEntry), SessionEntries.JsonOptions) + "\n", new UTF8Encoding(false));
        }
    }

    private void AppendEntry(SessionEntry entry)
    {
        _fileEntries.Add(entry);
        if (entry.Id is { } id)
        {
            if (!_byId.ContainsKey(id))
            {
                _byIdOrder.Add(id);
            }

            _byId[id] = entry;
            _leafId = id;
        }

        Persist(entry);
    }

    // ------------------------------------------------------------------
    // 基本读取
    // ------------------------------------------------------------------

    public bool IsPersisted() => _persist;

    public string GetCwd() => _cwd;

    public string GetSessionDir() => _sessionDir;

    public bool UsesDefaultSessionDir() => _sessionDir == SessionEntries.GetDefaultSessionDirPath(_cwd);

    public string GetSessionId() => _sessionId;

    public string? GetSessionFile() => _sessionFile;

    /// <summary>当前条目数（不含头部）。对应 TS <c>getEntryCount</c>。</summary>
    public int GetEntryCount() => _byId.Count;

    /// <summary>会话头；新会话（尚无条目）恒有。对应 TS <c>getHeader</c>。</summary>
    public SessionHeader? GetHeader() => _fileEntries.OfType<SessionHeader>().FirstOrDefault();

    /// <summary>全部会话条目（不含头部的浅拷贝）。对应 TS <c>getEntries</c>。</summary>
    public IReadOnlyList<SessionEntry> GetEntries() => [.. _fileEntries.OfType<SessionEntry>()];

    /// <summary>从最新 <c>session_info</c> 条目取会话名；空名显式清除标题。对应 TS <c>getSessionName</c>。</summary>
    public string? GetSessionName()
    {
        // 逆序找最新的 session_info 条目；直接读 fileEntries（footer 每帧调用，
        // getEntries() 会复制整个会话）。
        for (var index = _fileEntries.Count - 1; index >= 0; index--)
        {
            if (_fileEntries[index] is SessionInfoEntry info)
            {
                return string.IsNullOrWhiteSpace(info.Name) ? null : info.Name.Trim();
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 追加
    // ------------------------------------------------------------------

    /// <summary>
    /// 在当前 leaf 下追加一条消息并推进 leaf，返回条目 id。对应 TS <c>appendMessage</c>。
    /// </summary>
    /// <remarks>
    /// TS 参数类型是 <c>Message | CustomMessage | BashExecutionMessage</c>（编译期排除
    /// compaction/branch 摘要消息）；端口的 <see cref="ChatMessage"/> 是全部消息的基类，无法在
    /// 类型层排除，与 TS 一致不做运行时检查——摘要消息应通过
    /// <see cref="AppendCompaction"/> / <see cref="AppendBranchSummary"/> 追加。
    /// </remarks>
    public string AppendMessage(ChatMessage message)
    {
        var entry = new SessionMessageEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            Message = message,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>在当前 leaf 下追加思考档位变更并推进 leaf，返回条目 id。对应 TS <c>appendThinkingLevelChange</c>。</summary>
    public string AppendThinkingLevelChange(string thinkingLevel)
    {
        var entry = new ThinkingLevelChangeEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            ThinkingLevel = thinkingLevel,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>在当前 leaf 下追加模型切换并推进 leaf，返回条目 id。对应 TS <c>appendModelChange</c>。</summary>
    public string AppendModelChange(string provider, string modelId)
    {
        var entry = new ModelChangeEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            Provider = provider,
            ModelId = modelId,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>
    /// 追加不参与 LLM 上下文的模型归因用量，返回条目。对应 TS <c>appendUsage</c>。
    /// </summary>
    /// <remarks>
    /// TS 仅在 note 为真值时写 <c>note</c> 字段（<c>...(note ? { note } : {})</c>）；空串折叠为
    /// null（配合 <c>WhenWritingNull</c> 省略），保持线路同形。
    /// </remarks>
    public UsageEntry AppendUsage(string kind, string provider, string model, Usage usage, string? note = null)
    {
        var entry = new UsageEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            Kind = kind,
            Provider = provider,
            Model = model,
            Usage = usage,
            Note = string.IsNullOrEmpty(note) ? null : note,
        };
        AppendEntry(entry);
        return entry;
    }

    /// <summary>在当前 leaf 下追加压缩摘要并推进 leaf，返回条目 id。对应 TS <c>appendCompaction</c>。</summary>
    public string AppendCompaction(
        string summary,
        string? firstKeptEntryId,
        long tokensBefore,
        object? details = null,
        bool? fromHook = null,
        Usage? usage = null)
    {
        var timestamp = NowIso();
        var systemMessage = Transcript.GetCurrentSystemMessage(BuildSessionProjection().Messages);
        var id = SessionEntries.GenerateId(_byId.ContainsKey);
        var entry = new CompactionEntry
        {
            Id = id,
            ParentId = _leafId,
            Timestamp = timestamp,
            Summary = summary,
            FirstKeptEntryId = firstKeptEntryId ?? id,
            TokensBefore = tokensBefore,
            Details = details,
            UsageStats = usage,
            FromHook = fromHook,
            SystemMessage = systemMessage is null
                ? null
                : systemMessage with { Timestamp = TryParseTimestampMillis(timestamp, out var millis) ? millis : null },
        };
        AppendEntry(entry);
        return id;
    }

    /// <summary>在当前 leaf 下追加扩展自定义条目并推进 leaf，返回条目 id。对应 TS <c>appendCustomEntry</c>。</summary>
    public string AppendCustomEntry(string customType, object? data = null)
    {
        var entry = new CustomEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            CustomType = customType,
            Data = data,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>追加会话元数据条目（如显示名），返回条目 id。对应 TS <c>appendSessionInfo</c>。</summary>
    public string AppendSessionInfo(string name)
    {
        var sanitizedName = SessionNameNewlinePattern().Replace(name, " ").Trim();
        var entry = new SessionInfoEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            Name = sanitizedName,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>
    /// 追加参与 LLM 上下文的扩展自定义消息条目，返回条目 id。对应 TS <c>appendCustomMessageEntry</c>。
    /// </summary>
    /// <param name="customType">扩展标识，重载时用于过滤。</param>
    /// <param name="content">消息内容（字符串或 TextContent/ImageContent 数组）。</param>
    /// <param name="display">是否在 TUI 显示（true = 有样式展示，false = 隐藏）。</param>
    /// <param name="details">可选扩展私有元数据（不发给 LLM）。</param>
    public string AppendCustomMessageEntry(string customType, MessageContent content, bool display, object? details = null)
    {
        var entry = new CustomMessageEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            CustomType = customType,
            Content = content,
            Display = display,
            Details = details,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>对更早的模型可见条目追加分支局部编辑。对应 TS <c>appendContextEdit</c>。</summary>
    public string AppendContextEdit(string targetId, ContextEditReplacement? replacement)
    {
        // TS 校验：replacement 非 null 时 content 必须是 string 或数组——JSON null、缺失与其他
        // 标量一律抛错。（<see cref="MessageContent"/> 的转换器把 null 归一化为空 Blocks，那是
        // 消息线路对旧文件的语义；replacement 的入参校验按原始 JsonNode 判断。）
        if (replacement is not null)
        {
            var contentIsString = replacement.Content is JsonValue { } contentValue
                                  && contentValue.TryGetValue<string>(out _);
            if (!contentIsString && replacement.Content is not JsonArray)
            {
                throw new InvalidOperationException(
                    "Context edit replacement must be null or contain string/array content");
            }
        }

        if (!_byId.TryGetValue(targetId, out var target))
        {
            throw new InvalidOperationException($"Entry {targetId} not found");
        }

        if (!GetBranch().Any(entry => entry.Id == targetId))
        {
            throw new InvalidOperationException($"Entry {targetId} is not on the active branch");
        }

        var editable = target is CustomMessageEntry
            || target is SessionMessageEntry { Message: UserMessage or AssistantMessage or ToolResultMessage };
        if (!editable)
        {
            throw new InvalidOperationException($"Entry {targetId} does not contribute editable model content");
        }

        var targetRole = target switch
        {
            SessionMessageEntry { Message: UserMessage } => "user",
            SessionMessageEntry { Message: AssistantMessage } => "assistant",
            SessionMessageEntry { Message: ToolResultMessage } => "toolResult",
            _ => "custom",
        };
        ContextEditReplacement? normalizedReplacement = replacement;
        if (replacement?.Content is JsonValue { } value && value.TryGetValue<string>(out var text)
            && targetRole is "assistant" or "toolResult")
        {
            // TS：assistant/toolResult 的 content 恒为块数组，字符串替换值包成单个文本块。
            normalizedReplacement = new ContextEditReplacement
            {
                Content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            };
        }

        var entry = new ContextEditEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            TargetId = targetId,
            Replacement = normalizedReplacement,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    // ------------------------------------------------------------------
    // 树遍历
    // ------------------------------------------------------------------

    public string? GetLeafId() => _leafId;

    public SessionEntry? GetLeafEntry() =>
        _leafId is { } leafId ? _byId.GetValueOrDefault(leafId) : null;

    public SessionEntry? GetEntry(string id) => _byId.GetValueOrDefault(id);

    /// <summary>取某条目的全部直接子条目（按索引插入序）。对应 TS <c>getChildren</c>。</summary>
    public IReadOnlyList<SessionEntry> GetChildren(string parentId)
    {
        var children = new List<SessionEntry>();
        foreach (var id in _byIdOrder)
        {
            if (_byId[id] is { } entry && string.Equals(entry.ParentId, parentId, StringComparison.Ordinal))
            {
                children.Add(entry);
            }
        }

        return children;
    }

    /// <summary>取条目的标签（若有）。对应 TS <c>getLabel</c>。</summary>
    public string? GetLabel(string id) => _labelsById.GetValueOrDefault(id);

    /// <summary>
    /// 设置或清除条目标签，返回条目 id。标签是书签/导航用的用户标记；传 null 或空串清除。
    /// 对应 TS <c>appendLabelChange</c>。
    /// </summary>
    public string AppendLabelChange(string targetId, string? label)
    {
        if (!_byId.ContainsKey(targetId))
        {
            throw new InvalidOperationException($"Entry {targetId} not found");
        }

        var entry = new LabelEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = _leafId,
            Timestamp = NowIso(),
            TargetId = targetId,
            Label = label,
        };
        AppendEntry(entry);
        if (!string.IsNullOrEmpty(label))
        {
            SetLabelIndex(targetId, label!, entry.Timestamp!);
        }
        else
        {
            ClearLabelIndex(targetId);
        }

        return entry.Id!;
    }

    /// <summary>
    /// 从指定条目（缺省为当前 leaf）回溯到根，按路径顺序返回全部条目（含所有类型）。
    /// 对应 TS <c>getBranch</c>。
    /// </summary>
    public IReadOnlyList<SessionEntry> GetBranch(string? fromId = null)
    {
        var path = new List<SessionEntry>();
        var startId = fromId ?? _leafId;
        var current = !string.IsNullOrEmpty(startId) ? _byId.GetValueOrDefault(startId) : null;
        while (current is not null)
        {
            path.Add(current);
            current = !string.IsNullOrEmpty(current.ParentId) ? _byId.GetValueOrDefault(current.ParentId) : null;
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// 构建压缩感知的活动条目列表（渲染用），从当前 leaf 按树遍历。对应 TS <c>buildContextEntries</c>。
    /// </summary>
    public IReadOnlyList<SessionEntry> BuildContextEntries() =>
        SessionEntries.BuildContextEntries(GetEntries(), _leafId);

    /// <summary>
    /// 构建会话投影（发给 LLM 的内容），从当前 leaf 按树遍历。对应 TS <c>buildSessionProjection</c>。
    /// </summary>
    public SessionProjection BuildSessionProjection() =>
        SessionEntries.BuildSessionProjection(GetEntries(), _leafId);

    /// <summary>构建最终模型上下文。对应 TS <c>buildSessionContext</c>。</summary>
    public SessionContext BuildSessionContext()
    {
        var projection = BuildSessionProjection();
        return new SessionContext(projection.Messages, projection.ThinkingLevel, projection.Model);
    }

    /// <summary>
    /// 以树结构返回全部条目的浅防御副本。良构会话恰有一个根（首个 <c>parentId === null</c> 的
    /// 条目）；断链的孤儿条目也作为根返回。对应 TS <c>getTree</c>。
    /// </summary>
    public IReadOnlyList<SessionTreeNode> GetTree()
    {
        var entries = GetEntries();
        var nodeMap = new Dictionary<string, SessionTreeNode>(StringComparer.Ordinal);
        // record 的 Children 是 IReadOnlyList（collection expression 构造出数组）；排序需要可变
        // 列表引用，与节点并行保存。
        var childrenMap = new Dictionary<string, List<SessionTreeNode>>(StringComparer.Ordinal);
        var roots = new List<SessionTreeNode>();

        // 建节点并解析标签。Children 属性是 IReadOnlyList（collection expression 会构造数组），
        // 排序需要可变列表，先建 List 再同时赋给属性与 childrenMap，保证同一引用。
        foreach (var entry in entries)
        {
            var label = entry.Id is { } id ? _labelsById.GetValueOrDefault(id) : null;
            var labelTimestamp = entry.Id is { } labelId ? _labelTimestampsById.GetValueOrDefault(labelId) : null;
            var children = new List<SessionTreeNode>();
            var node = new SessionTreeNode
            {
                Entry = entry,
                Children = children,
                Label = label,
                LabelTimestamp = labelTimestamp,
            };
            if (entry.Id is { } entryId)
            {
                nodeMap[entryId] = node;
                childrenMap[entryId] = children;
            }
        }

        // 建树
        foreach (var entry in entries)
        {
            if (entry.Id is not { } entryId)
            {
                // 差异：TS 会以 "undefined" 为键建节点并按 parentId 匹配；端口把无 id 条目当根。
                roots.Add(new SessionTreeNode { Entry = entry, Children = [] });
                continue;
            }

            var node = nodeMap[entryId];
            if (entry.ParentId is null || entry.ParentId == entry.Id)
            {
                roots.Add(node);
            }
            else if (nodeMap.TryGetValue(entry.ParentId, out var parent))
            {
                childrenMap[parent.Entry.Id!].Add(node);
            }
            else
            {
                // Orphan - treat as root
                roots.Add(node);
            }
        }

        // 子节点按时间戳升序排（旧的在前）。用迭代避免深树栈溢出。
        // TS 的比较器对新 Date(NaN) 产生 NaN（视为 0，保持原位）；端口用稳定插入排序复现。
        var stack = new List<SessionTreeNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            // 无 id 条目作为根时没有 childrenMap 条目，直接跳过（其 Children 恒为空）。
            if (node.Entry.Id is not { } nodeId || !childrenMap.TryGetValue(nodeId, out var children))
            {
                continue;
            }

            SortChildrenStable(children);
            stack.AddRange(children);
        }

        return roots;
    }

    /// <summary>
    /// 稳定插入排序，比较器对齐 TS <c>new Date(a).getTime() - new Date(b).getTime()</c>：
    /// 任一侧解析失败（TS 的 NaN）视为相等不交换。
    /// </summary>
    private static void SortChildrenStable(List<SessionTreeNode> children)
    {
        for (var index = 1; index < children.Count; index++)
        {
            var node = children[index];
            var key = TryParseTimestampMillis(node.Entry.Timestamp, out var keyMillis) ? keyMillis : (long?)null;
            var insertAt = index;
            while (insertAt > 0)
            {
                var previous = children[insertAt - 1];
                var previousKey = TryParseTimestampMillis(previous.Entry.Timestamp, out var previousMillis)
                    ? previousMillis
                    : (long?)null;
                // TS comparator (previous - key) > 0 才后移；NaN 或相等都停（稳定）。
                if (key is null || previousKey is null || previousKey <= key)
                {
                    break;
                }

                children[insertAt] = previous;
                insertAt--;
            }

            children[insertAt] = node;
        }
    }

    // ------------------------------------------------------------------
    // 分支
    // ------------------------------------------------------------------

    /// <summary>
    /// 从更早的条目开新分支：把 leaf 移到该条目，下一次追加会在其下建子节点；既有条目不改不删。
    /// 对应 TS <c>branch</c>。
    /// </summary>
    public void Branch(string branchFromId)
    {
        if (!_byId.ContainsKey(branchFromId))
        {
            throw new InvalidOperationException($"Entry {branchFromId} not found");
        }

        _leafId = branchFromId;
    }

    /// <summary>把 leaf 指针重置为 null（任何条目之前）。对应 TS <c>resetLeaf</c>。</summary>
    public void ResetLeaf() => _leafId = null;

    /// <summary>
    /// 开新分支并追加一条 <c>branch_summary</c> 条目记录被放弃路径的上下文。对应 TS
    /// <c>branchWithSummary</c>。
    /// </summary>
    public string BranchWithSummary(
        string? branchFromId,
        string summary,
        object? details = null,
        bool? fromHook = null,
        Usage? usage = null)
    {
        if (branchFromId is not null && !_byId.ContainsKey(branchFromId))
        {
            throw new InvalidOperationException($"Entry {branchFromId} not found");
        }

        var fromId = _leafId ?? "root";
        _leafId = branchFromId;
        var entry = new BranchSummaryEntry
        {
            Id = SessionEntries.GenerateId(_byId.ContainsKey),
            ParentId = branchFromId,
            Timestamp = NowIso(),
            FromId = fromId,
            Summary = summary,
            Details = details,
            UsageStats = usage,
            FromHook = fromHook,
        };
        AppendEntry(entry);
        return entry.Id!;
    }

    /// <summary>
    /// 新建只含从根到指定 leaf 路径的会话文件（从分支会话里抽出单条对话路径），返回新文件
    /// 路径；不持久化时返回 null。对应 TS <c>createBranchedSession</c>。
    /// </summary>
    /// <remarks>
    /// 路径里的 label 条目被剔除、后续条目重链父指针（label 是真实树条目，后续条目可能是其
    /// 子节点），保留条目的 <c>firstKeptEntryId</c> 若指向被剔除的 label 也同步替换。
    /// </remarks>
    public string? CreateBranchedSession(string leafId)
    {
        var previousSessionFile = _sessionFile;
        var path = GetBranch(leafId);
        if (path.Count == 0)
        {
            throw new InvalidOperationException($"Entry {leafId} not found");
        }

        // 剔除路径中的 LabelEntry——之后从解析出的标签映射重建。
        var pathWithoutLabels = new List<SessionEntry>();
        var replacementByLabelId = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingLabelIds = new List<string>();
        string? pathParentId = null;
        foreach (var entry in path)
        {
            if (entry is LabelEntry)
            {
                pendingLabelIds.Add(entry.Id!);
                continue;
            }

            foreach (var labelId in pendingLabelIds)
            {
                replacementByLabelId[labelId] = entry.Id!;
            }

            pendingLabelIds.Clear();
            pathWithoutLabels.Add(
                entry is CompactionEntry compaction
                    ? compaction with
                    {
                        ParentId = pathParentId,
                        FirstKeptEntryId =
                            compaction.FirstKeptEntryId == entry.Id
                                ? entry.Id
                                : compaction.FirstKeptEntryId is { } keptEntryId
                                    && replacementByLabelId.TryGetValue(keptEntryId, out var replacedEntryId)
                                    ? replacedEntryId
                                    : compaction.FirstKeptEntryId,
                    }
                    : entry with { ParentId = pathParentId });
            pathParentId = entry.Id;
        }

        var newSessionId = SessionEntries.CreateSessionId();
        var timestamp = NowIso();
        var fileTimestamp = timestamp.Replace(":", "-").Replace(".", "-");
        var newSessionFile = NodePath.Join(GetSessionDir(), $"{fileTimestamp}_{newSessionId}.jsonl");

        var header = new SessionHeader
        {
            Version = SessionEntries.CurrentVersion,
            Id = newSessionId,
            Timestamp = timestamp,
            Cwd = _cwd,
            ParentSession = _persist ? previousSessionFile : null,
        };

        // 收集路径内条目的标签
        var pathEntryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in pathWithoutLabels)
        {
            pathEntryIds.Add(entry.Id!);
        }

        var labelsToWrite = new List<(string TargetId, string Label, string Timestamp)>();
        // TS 按 labelsById 的 Map 迭代序收集；端口按 _labelsOrder 复现插入序。
        foreach (var targetId in _labelsOrder)
        {
            if (pathEntryIds.Contains(targetId))
            {
                labelsToWrite.Add((targetId, _labelsById[targetId], _labelTimestampsById[targetId]));
            }
        }

        if (_persist)
        {
            // 构建标签条目，链在保留路径之后
            var parentId = pathWithoutLabels.Count > 0 ? pathWithoutLabels[^1].Id : null;
            var labelEntries = new List<LabelEntry>();
            foreach (var (targetId, label, labelTimestamp) in labelsToWrite)
            {
                var labelEntry = new LabelEntry
                {
                    Id = SessionEntries.GenerateId(pathEntryIds.Contains),
                    ParentId = parentId,
                    Timestamp = labelTimestamp,
                    TargetId = targetId,
                    Label = label,
                };
                pathEntryIds.Add(labelEntry.Id!);
                labelEntries.Add(labelEntry);
                parentId = labelEntry.Id;
            }

            _fileEntries = [header, .. pathWithoutLabels, .. labelEntries];
            _sessionId = newSessionId;
            _sessionFile = newSessionFile;
            BuildIndex();

            // 与 _persist 同规则：分支路径已含对话就立即写，否则留给 _persist 之后建文件。
            if (HasConversation())
            {
                RewriteFile();
                _flushed = true;
            }
            else
            {
                _flushed = false;
            }

            return newSessionFile;
        }

        // 内存模式：用路径 + 标签替换当前会话
        var inMemoryLabelEntries = new List<LabelEntry>();
        var inMemoryIds = new HashSet<string>(pathEntryIds, StringComparer.Ordinal);
        var inMemoryParentId = pathWithoutLabels.Count > 0 ? pathWithoutLabels[^1].Id : null;
        foreach (var (targetId, label, labelTimestamp) in labelsToWrite)
        {
            var labelEntry = new LabelEntry
            {
                Id = SessionEntries.GenerateId(inMemoryIds.Contains),
                ParentId = inMemoryParentId,
                Timestamp = labelTimestamp,
                TargetId = targetId,
                Label = label,
            };
            inMemoryIds.Add(labelEntry.Id!);
            inMemoryLabelEntries.Add(labelEntry);
            inMemoryParentId = labelEntry.Id;
        }

        _fileEntries = [header, .. pathWithoutLabels, .. inMemoryLabelEntries];
        _sessionId = newSessionId;
        BuildIndex();
        return null;
    }

    // ------------------------------------------------------------------
    // 静态工厂
    // ------------------------------------------------------------------

    /// <summary>新建会话。对应 TS <c>SessionManager.create</c>。</summary>
    /// <param name="cwd">工作目录（存入会话头）。</param>
    /// <param name="sessionDir">可选会话目录；缺省用默认目录（会自动创建）。</param>
    public static SessionManager Create(string cwd, string? sessionDir = null, NewSessionOptions? options = null)
    {
        var dir = sessionDir is not null ? Paths.NormalizePath(sessionDir) : SessionFiles.GetDefaultSessionDir(cwd);
        return new SessionManager(cwd, dir, null, true, options);
    }

    /// <summary>打开指定会话文件。对应 TS <c>SessionManager.open</c>。</summary>
    /// <param name="path">会话文件路径。</param>
    /// <param name="sessionDir">可选会话目录（/new 或 /branch 用）；缺省取文件父目录。</param>
    /// <param name="cwdOverride">可选 cwd 覆盖（否则取会话头的 cwd）。</param>
    public static SessionManager Open(string path, string? sessionDir = null, string? cwdOverride = null)
    {
        var resolvedPath = Paths.ResolvePath(path);
        SessionHeader? header = null;
        IReadOnlyList<FileEntry>? preloadedFileEntries = null;
        if (cwdOverride is null && File.Exists(resolvedPath))
        {
            try
            {
                header = SessionFiles.ReadSessionHeader(resolvedPath);
            }
            catch (SessionHeaderScanLimitException)
            {
                // 有界扫描只是发现优化。对头部或前缀超大的遗留文件，全量加载仍是权威来源。
                preloadedFileEntries = SessionFiles.LoadEntriesFromFile(resolvedPath);
                var firstEntry = preloadedFileEntries.Count > 0 ? preloadedFileEntries[0] : null;
                header = firstEntry as SessionHeader;
            }
        }

        var cwd = cwdOverride
            ?? header?.Cwd
            ?? Directory.GetCurrentDirectory();
        // 未提供 sessionDir 时从文件父目录推导
        var dir = sessionDir is not null ? Paths.NormalizePath(sessionDir) : NodePath.Resolve(resolvedPath, "..");
        return new SessionManager(cwd, dir, resolvedPath, true, null, preloadedFileEntries);
    }

    /// <summary>继续最近的会话，没有则新建。对应 TS <c>SessionManager.continueRecent</c>。</summary>
    public static SessionManager ContinueRecent(string cwd, string? sessionDir = null)
    {
        var dir = sessionDir is not null ? Paths.NormalizePath(sessionDir) : SessionFiles.GetDefaultSessionDir(cwd);
        var filterCwd = sessionDir is not null && dir != SessionEntries.GetDefaultSessionDirPath(cwd);
        var mostRecent = SessionFiles.FindMostRecentSession(dir, filterCwd ? cwd : null);
        if (mostRecent is not null)
        {
            return new SessionManager(cwd, dir, mostRecent, true);
        }

        return new SessionManager(cwd, dir, null, true);
    }

    /// <summary>创建内存会话（不落盘），可选从外部持有的条目构建。对应 TS <c>SessionManager.inMemory</c>。</summary>
    public static SessionManager InMemory(
        string? cwd = null, NewSessionOptions? options = null, IReadOnlyList<FileEntry>? entries = null)
    {
        return new SessionManager(cwd ?? Directory.GetCurrentDirectory(), "", null, false, options, entries);
    }

    /// <summary>
    /// 从另一个项目目录 fork 会话到当前项目：在目标 cwd 新建会话文件，头指向源会话，内容为
    /// 源会话的完整历史。对应 TS <c>SessionManager.forkFrom</c>。
    /// </summary>
    public static SessionManager ForkFrom(
        string sourcePath, string targetCwd, string? sessionDir = null, NewSessionOptions? options = null)
    {
        var resolvedSourcePath = Paths.ResolvePath(sourcePath);
        var resolvedTargetCwd = Paths.ResolvePath(targetCwd);
        var sourceEntries = SessionFiles.LoadEntriesFromFile(resolvedSourcePath);
        if (sourceEntries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot fork: source session file is empty or invalid: {resolvedSourcePath}");
        }

        var sourceHeader = sourceEntries.OfType<SessionHeader>().FirstOrDefault();
        if (sourceHeader is null)
        {
            throw new InvalidOperationException(
                $"Cannot fork: source session has no header: {resolvedSourcePath}");
        }

        var dir = sessionDir is not null
            ? Paths.NormalizePath(sessionDir)
            : SessionFiles.GetDefaultSessionDir(resolvedTargetCwd);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 新 id 但内容 fork
        if (options?.Id is not null)
        {
            SessionEntries.AssertValidSessionId(options.Id);
        }

        var newSessionId = options?.Id ?? SessionEntries.CreateSessionId();
        var timestamp = NowIso();
        var fileTimestamp = timestamp.Replace(":", "-").Replace(".", "-");
        var newSessionFile = NodePath.Join(dir, $"{fileTimestamp}_{newSessionId}.jsonl");

        // 写新头（指向源为父会话、更新 cwd），再复制源会话的非头部条目。
        // TS 先 wx 写头、再逐条 append；端口一次打开独占写，失败时同样留下部分文件。
        using (var stream = new FileStream(newSessionFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            var newHeader = new SessionHeader
            {
                Version = SessionEntries.CurrentVersion,
                Id = newSessionId,
                Timestamp = timestamp,
                Cwd = resolvedTargetCwd,
                ParentSession = resolvedSourcePath,
            };
            writer.Write(JsonSerializer.Serialize(newHeader, typeof(FileEntry), SessionEntries.JsonOptions));
            writer.Write('\n');
            foreach (var entry in sourceEntries)
            {
                if (entry is SessionHeader)
                {
                    continue;
                }

                writer.Write(JsonSerializer.Serialize(entry, typeof(FileEntry), SessionEntries.JsonOptions));
                writer.Write('\n');
            }
        }

        return new SessionManager(resolvedTargetCwd, dir, newSessionFile, true);
    }

    /// <summary>
    /// 精确按会话 id 查找文件（不加载会话正文）。对应 TS <c>SessionManager.findById</c>。
    /// </summary>
    public static string? FindById(string cwd, string id, string? sessionDir = null) =>
        SessionFiles.FindSessionById(cwd, id, sessionDir);

    /// <summary>列出一个目录的全部会话。对应 TS <c>SessionManager.list</c>。</summary>
    /// <param name="cwd">工作目录（用于计算默认会话目录）。</param>
    /// <param name="sessionDir">可选会话目录；缺省用默认目录。</param>
    /// <param name="onProgress">可选进度回调（loaded, total）。</param>
    public static Task<IReadOnlyList<SessionInfo>> ListAsync(
        string cwd,
        string? sessionDir = null,
        SessionListProgress? onProgress = null,
        CancellationToken signal = default)
    {
        return SessionFiles.ListSessionsForCwdAsync(cwd, sessionDir, onProgress, signal);
    }

    /// <summary>列出全部项目目录的会话。对应 TS <c>SessionManager.listAll</c>（双载合一）。</summary>
    /// <param name="sessionDir">指定目录时只列该目录（不走全库扫描）。</param>
    /// <param name="onProgress">可选进度回调（loaded, total）。</param>
    public static Task<IReadOnlyList<SessionInfo>> ListAllAsync(
        string? sessionDir = null,
        SessionListProgress? onProgress = null,
        CancellationToken signal = default)
    {
        return SessionFiles.ListAllSessionsAsync(sessionDir, onProgress, signal);
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    /// <summary>当前 UTC 时间的 ISO 字符串（对齐 TS <c>new Date().toISOString()</c>，毫秒精度 + Z 后缀）。</summary>
    private static string NowIso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ISO 时间戳 → Unix 毫秒（对齐 TS <c>new Date(s).getTime()</c>；解析失败返回 false）。</summary>
    private static bool TryParseTimestampMillis(string? timestamp, out long millis)
    {
        millis = 0;
        if (string.IsNullOrEmpty(timestamp))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                timestamp, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        millis = parsed.ToUnixTimeMilliseconds();
        return true;
    }
}
