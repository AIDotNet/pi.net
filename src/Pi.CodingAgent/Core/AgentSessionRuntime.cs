using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ExtensionNewSessionOptions = Pi.CodingAgent.Core.Extensions.NewSessionOptions;
using ManagerNewSessionOptions = Pi.CodingAgent.Core.NewSessionOptions;

namespace Pi.CodingAgent.Core;

// `AgentSessionServices` / `AgentSessionRuntimeDiagnostic` 已迁到 AgentSessionServices.cs
// （对应 TS 的 agent-session-services.ts）。

/// <summary>对应 TS <c>CreateAgentSessionRuntimeResult</c>。</summary>
/// <remarks>
/// 调用方拿到创建出的会话、绑定到该 cwd 的服务，以及装配期间收集的全部诊断。
/// </remarks>
public sealed record CreateAgentSessionRuntimeResult
{
    public required AgentSession Session { get; init; }

    public required LoadExtensionsResult ExtensionsResult { get; init; }

    public string? ModelFallbackMessage { get; init; }

    public required AgentSessionServices Services { get; init; }

    public required IReadOnlyList<AgentSessionRuntimeDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// 为一个目标 cwd + sessionManager 创建完整运行时的入参。
/// 对应 TS <c>CreateAgentSessionRuntimeFactory</c> 的入参对象。
/// </summary>
/// <remarks>
/// 工厂闭包住进程级固定输入，为生效 cwd 重建绑定服务，再对着这些服务解析会话选项，
/// 最后创建 <see cref="AgentSession"/>。
/// </remarks>
public sealed record CreateAgentSessionRuntimeOptions
{
    public required string Cwd { get; init; }

    public required string AgentDir { get; init; }

    public required SessionManager SessionManager { get; init; }

    public ExtensionEvent.SessionStart? SessionStartEvent { get; init; }

    public ProjectTrustContext? ProjectTrustContext { get; init; }
}

/// <summary>对应 TS <c>CreateAgentSessionRuntimeFactory</c>。</summary>
public delegate Task<CreateAgentSessionRuntimeResult> CreateAgentSessionRuntimeFactory(
    CreateAgentSessionRuntimeOptions options);

/// <summary><see cref="AgentSessionRuntime.SwitchSessionAsync"/> 的选项。TS 的内联类型。</summary>
public sealed record SwitchSessionRuntimeOptions
{
    public string? CwdOverride { get; init; }

    public Func<IReplacedSessionContext, Task>? WithSession { get; init; }

    public Func<string, ProjectTrustContext>? ProjectTrustContextFactory { get; init; }
}

/// <summary>对应 TS <c>SessionImportFileNotFoundError</c>。</summary>
public sealed class SessionImportFileNotFoundError : FileNotFoundException
{
    public SessionImportFileNotFoundError(string filePath)
        : base($"File not found: {filePath}", filePath)
        => FilePath = filePath;

    public string FilePath { get; }
}

/// <summary>对应 TS <c>fork</c> 解析出的目标叶子与选中文本。</summary>
public sealed record ForkTarget(string? TargetLeafId, string? SelectedText);

/// <summary>对应 TS switch/new/fork/import 的 <c>{ cancelled }</c> 结果。</summary>
public sealed record SessionReplacementResult(bool Cancelled, string? SelectedText = null)
{
    public static SessionReplacementResult CancelledResult { get; } = new(true);

    public static SessionReplacementResult Completed { get; } = new(false);
}

/// <summary>对应 TS <c>newSession</c> 的 persisted / inMemory 分支。</summary>
public enum NewSessionKind
{
    InMemory,
    Persisted,
}

/// <summary>
/// 会话运行时：持有当前 <see cref="AgentSession"/> 及其绑定到 cwd 的服务。
/// 对应 TS <c>core/agent-session-runtime.ts</c>（447 行）。
/// </summary>
/// <remarks>
/// <para>
/// 会话替换类方法先拆掉当前运行时，再创建并应用下一个；创建失败时异常直接抛给调用方，
/// 由调用方决定面向用户的错误处理。
/// </para>
/// <para>
/// 本类的静态成员是 4e-3e-1/4/5/9f 落地的纯决策叶子（沿用「叶子先行」惯例）；
/// 实例成员是本批（4e-3e-9h-4b-2）落地的编排本体。
/// </para>
/// </remarks>
public sealed class AgentSessionRuntime
{
    private readonly CreateAgentSessionRuntimeFactory _createRuntime;

    private AgentSession _session;
    private AgentSessionServices _services;
    private IReadOnlyList<AgentSessionRuntimeDiagnostic> _diagnostics;
    private string? _modelFallbackMessage;

    private Func<AgentSession, Task>? _rebindSession;
    private Action? _beforeSessionInvalidate;

    public AgentSessionRuntime(
        AgentSession session,
        AgentSessionServices services,
        CreateAgentSessionRuntimeFactory createRuntime,
        IReadOnlyList<AgentSessionRuntimeDiagnostic>? diagnostics = null,
        string? modelFallbackMessage = null)
    {
        _session = session;
        _services = services;
        _createRuntime = createRuntime;
        _diagnostics = diagnostics ?? [];
        _modelFallbackMessage = modelFallbackMessage;
    }

    public AgentSessionServices Services => _services;

    public AgentSession Session => _session;

    public string Cwd => _services.Cwd;

    public IReadOnlyList<AgentSessionRuntimeDiagnostic> Diagnostics => _diagnostics;

    public string? ModelFallbackMessage => _modelFallbackMessage;

    public void SetRebindSession(Func<AgentSession, Task>? rebindSession) => _rebindSession = rebindSession;

    /// <summary>
    /// 设置一个同步回调：在 <c>session_shutdown</c> 处理器跑完之后、当前会话被作废之前执行。
    /// </summary>
    /// <remarks>
    /// 供宿主做「不能让出事件循环」的 UI 拆除（例如在旧扩展上下文变陈旧前，
    /// 解绑扩展提供的 TUI 组件）。对应 TS <c>setBeforeSessionInvalidate</c>。
    /// </remarks>
    public void SetBeforeSessionInvalidate(Action? beforeSessionInvalidate)
        => _beforeSessionInvalidate = beforeSessionInvalidate;

    // =========================================================================
    // 编排本体
    // =========================================================================

    /// <summary>切到另一个会话文件。对应 TS <c>switchSession</c>。</summary>
    public async Task<SessionReplacementResult> SwitchSessionAsync(
        string sessionPath,
        SwitchSessionRuntimeOptions? options = null)
    {
        if ((await EmitBeforeSwitchAsync(SessionEventReason.Resume, sessionPath)).Cancelled)
        {
            return SessionReplacementResult.CancelledResult;
        }

        var previousSessionFile = Session.SessionFile;
        var sessionManager = SessionManager.Open(sessionPath, null, options?.CwdOverride);
        SessionCwd.AssertSessionCwdExists(sessionManager, Cwd);
        await TeardownCurrentAsync(SessionEventReason.Resume, sessionManager.GetSessionFile());
        Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
        {
            Cwd = sessionManager.GetCwd(),
            AgentDir = Services.AgentDir,
            SessionManager = sessionManager,
            SessionStartEvent = BuildSessionStartEvent(SessionEventReason.Resume, previousSessionFile),
            ProjectTrustContext = options?.ProjectTrustContextFactory?.Invoke(sessionManager.GetCwd()),
        }));
        await FinishSessionReplacementAsync(options?.WithSession);
        return SessionReplacementResult.Completed;
    }

    /// <summary>开新会话。对应 TS <c>newSession</c>。</summary>
    public async Task<SessionReplacementResult> NewSessionAsync(ExtensionNewSessionOptions? options = null)
    {
        if ((await EmitBeforeSwitchAsync(SessionEventReason.New)).Cancelled)
        {
            return SessionReplacementResult.CancelledResult;
        }

        var previousSessionFile = Session.SessionFile;
        var sessionDir = Session.SessionManager.GetSessionDir();
        var sessionManager = Session.SessionManager.IsPersisted()
            ? SessionManager.Create(Cwd, sessionDir)
            : SessionManager.InMemory(Cwd);
        if (!string.IsNullOrEmpty(options?.ParentSession))
        {
            sessionManager.NewSession(new ManagerNewSessionOptions { ParentSession = options.ParentSession });
        }

        await TeardownCurrentAsync(SessionEventReason.New, sessionManager.GetSessionFile());
        Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
        {
            Cwd = Cwd,
            AgentDir = Services.AgentDir,
            SessionManager = sessionManager,
            SessionStartEvent = BuildSessionStartEvent(SessionEventReason.New, previousSessionFile),
        }));
        if (options?.Setup is { } setup)
        {
            await setup(Session.SessionManager);
            Session.RefreshContext();
        }

        await FinishSessionReplacementAsync(options?.WithSession);
        return SessionReplacementResult.Completed;
    }

    /// <summary>从某个条目 fork。对应 TS <c>fork</c>。</summary>
    public async Task<SessionReplacementResult> ForkAsync(string entryId, ForkOptions? options = null)
    {
        var position = ResolveForkPosition(options?.Position);
        if ((await EmitBeforeForkAsync(entryId, position)).Cancelled)
        {
            return SessionReplacementResult.CancelledResult;
        }

        var selectedEntry = Session.SessionManager.GetEntry(entryId);
        var target = ResolveForkTarget(selectedEntry, position);

        var previousSessionFile = Session.SessionFile;
        if (Session.SessionManager.IsPersisted())
        {
            var currentSessionFile = RequirePersistedSessionFile(Session.SessionFile, persisted: true);
            var sessionDir = Session.SessionManager.GetSessionDir();
            if (target.TargetLeafId is null)
            {
                var freshManager = SessionManager.Create(Cwd, sessionDir);
                freshManager.NewSession(new ManagerNewSessionOptions { ParentSession = currentSessionFile });
                await TeardownCurrentAsync(SessionEventReason.Fork, freshManager.GetSessionFile());
                Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
                {
                    Cwd = Cwd,
                    AgentDir = Services.AgentDir,
                    SessionManager = freshManager,
                    SessionStartEvent = BuildSessionStartEvent(SessionEventReason.Fork, previousSessionFile),
                }));
                await FinishSessionReplacementAsync(options?.WithSession);
                return new SessionReplacementResult(false, target.SelectedText);
            }

            RequireSavedSessionFile(currentSessionFile);
            var branchedManager = SessionManager.Open(currentSessionFile, sessionDir);
            RequireForkedSessionPath(branchedManager.CreateBranchedSession(target.TargetLeafId));
            await TeardownCurrentAsync(SessionEventReason.Fork, branchedManager.GetSessionFile());
            Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
            {
                Cwd = branchedManager.GetCwd(),
                AgentDir = Services.AgentDir,
                SessionManager = branchedManager,
                SessionStartEvent = BuildSessionStartEvent(SessionEventReason.Fork, previousSessionFile),
            }));
            await FinishSessionReplacementAsync(options?.WithSession);
            return new SessionReplacementResult(false, target.SelectedText);
        }

        var sessionManager = Session.SessionManager;
        await TeardownCurrentAsync(SessionEventReason.Fork, sessionManager.GetSessionFile());
        if (target.TargetLeafId is null)
        {
            sessionManager.NewSession(new ManagerNewSessionOptions { ParentSession = previousSessionFile });
        }
        else
        {
            sessionManager.CreateBranchedSession(target.TargetLeafId);
        }

        Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
        {
            Cwd = Cwd,
            AgentDir = Services.AgentDir,
            SessionManager = sessionManager,
            SessionStartEvent = BuildSessionStartEvent(SessionEventReason.Fork, previousSessionFile),
        }));
        await FinishSessionReplacementAsync(options?.WithSession);
        return new SessionReplacementResult(false, target.SelectedText);
    }

    /// <summary>
    /// 导入一个会话 JSONL 文件并把运行时切到导入的会话。对应 TS <c>importFromJsonl</c>。
    /// </summary>
    /// <returns>被 <c>session_before_switch</c> 取消时为 <see cref="SessionReplacementResult.Cancelled"/>。</returns>
    /// <exception cref="SessionImportFileNotFoundError">输入路径不存在。</exception>
    /// <exception cref="MissingSessionCwdException">导入会话的 cwd 解析不出且未给 override。</exception>
    public async Task<SessionReplacementResult> ImportFromJsonlAsync(string inputPath, string? cwdOverride = null)
    {
        var resolvedPath = RequireImportSource(inputPath);
        var sessionDir = Session.SessionManager.GetSessionDir();
        var destination = ResolveImportDestination(sessionDir, resolvedPath);
        if ((await EmitBeforeSwitchAsync(SessionEventReason.Resume, destination.DestinationPath)).Cancelled)
        {
            return SessionReplacementResult.CancelledResult;
        }

        var previousSessionFile = Session.SessionFile;
        CopyImportedJsonl(resolvedPath, destination.DestinationPath, destination.SourceAlreadyStored);

        var sessionManager = SessionManager.Open(destination.DestinationPath, sessionDir, cwdOverride);
        SessionCwd.AssertSessionCwdExists(sessionManager, Cwd);
        await TeardownCurrentAsync(SessionEventReason.Resume, sessionManager.GetSessionFile());
        Apply(await _createRuntime(new CreateAgentSessionRuntimeOptions
        {
            Cwd = sessionManager.GetCwd(),
            AgentDir = Services.AgentDir,
            SessionManager = sessionManager,
            SessionStartEvent = BuildSessionStartEvent(SessionEventReason.Resume, previousSessionFile),
        }));
        await FinishSessionReplacementAsync();
        return SessionReplacementResult.Completed;
    }

    /// <summary>关闭运行时。对应 TS <c>dispose</c>。</summary>
    public async Task DisposeAsync()
    {
        await ExtensionRunner.EmitSessionShutdownEventAsync(
            Session.ExtensionRunner,
            BuildSessionShutdownEvent(SessionEventReason.Quit));
        _beforeSessionInvalidate?.Invoke();
        Session.Dispose();
    }

    private async Task<SessionReplacementResult> EmitBeforeSwitchAsync(
        string reason,
        string? targetSessionFile = null)
    {
        var runner = Session.ExtensionRunner;
        if (!runner.HasHandlers(ExtensionEventNames.SessionBeforeSwitch))
        {
            return SessionReplacementResult.Completed;
        }

        var result = await runner.EmitAsync(new ExtensionEvent.SessionBeforeSwitch(reason, targetSessionFile));
        return new SessionReplacementResult(ResolveBeforeHookCancellation(true, result?.Cancel));
    }

    private async Task<SessionReplacementResult> EmitBeforeForkAsync(string entryId, string position)
    {
        var runner = Session.ExtensionRunner;
        if (!runner.HasHandlers(ExtensionEventNames.SessionBeforeFork))
        {
            return SessionReplacementResult.Completed;
        }

        var result = await runner.EmitAsync(new ExtensionEvent.SessionBeforeFork(entryId, position));
        return new SessionReplacementResult(ResolveBeforeHookCancellation(true, result?.Cancel));
    }

    private async Task TeardownCurrentAsync(string reason, string? targetSessionFile = null)
    {
        // 先把正在进行的回复结算掉，好让被中止的回合（含工具结果）落到即将被替换的会话里。
        await Session.AbortAsync();
        await ExtensionRunner.EmitSessionShutdownEventAsync(
            Session.ExtensionRunner,
            BuildSessionShutdownEvent(reason, targetSessionFile));
        _beforeSessionInvalidate?.Invoke();
        Session.Dispose();
    }

    private void Apply(CreateAgentSessionRuntimeResult result)
    {
        _session = result.Session;
        _services = result.Services;
        _diagnostics = result.Diagnostics;
        _modelFallbackMessage = result.ModelFallbackMessage;
    }

    private async Task FinishSessionReplacementAsync(Func<IReplacedSessionContext, Task>? withSession = null)
    {
        if (_rebindSession is not null)
        {
            await _rebindSession(Session);
        }

        if (withSession is not null)
        {
            await withSession(Session.CreateReplacedSessionContext());
        }
    }

    // =========================================================================
    // 纯决策叶子（4e-3e-1 / 4 / 5 / 9f）
    // =========================================================================

    /// <summary>对应 TS <c>extractUserMessageText</c>。</summary>
    public static string ExtractUserMessageText(string content) => content;

    /// <summary>对应 TS <c>extractUserMessageText</c> 的块数组分支。</summary>
    public static string ExtractUserMessageText(IEnumerable<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextContent>().Select(block => block.Text));

    /// <summary>JSON 节点形态，便于对照 TS <c>string | Array</c>。</summary>
    public static string ExtractUserMessageText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is not JsonArray array)
        {
            return string.Empty;
        }

        return string.Concat(
            array.OfType<JsonObject>()
                .Where(part => part["type"]?.GetValue<string>() == "text" && part["text"] is JsonValue)
                .Select(part => part["text"]!.GetValue<string>()));
    }

    /// <summary>对应 TS <c>extractUserMessageText</c> 的 <c>UserMessage.content</c> 分支。</summary>
    public static string ExtractUserMessageText(MessageContent content)
        => content switch
        {
            MessageContent.Text text => text.Value,
            MessageContent.Blocks blocks => ExtractUserMessageText(blocks.Value),
            _ => string.Empty,
        };

    /// <summary>对应 TS <c>importFromJsonl</c> 的目标文件去重。</summary>
    public static string UniqueJsonlDestinationPath(string sessionDir, string sourcePath)
    {
        var destination = Path.Combine(sessionDir, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        if (!File.Exists(destination))
        {
            return destination;
        }

        var name = Path.GetFileNameWithoutExtension(destination);
        var ext = Path.GetExtension(destination);
        var suffix = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(sessionDir, $"{name}-{suffix++}{ext}");
        }

        return destination;
    }

    /// <summary>对应 TS <c>importFromJsonl</c> 的存在性检查。</summary>
    public static string RequireImportSource(string inputPath)
    {
        var resolved = Paths.ResolvePath(inputPath);
        if (!File.Exists(resolved))
        {
            throw new SessionImportFileNotFoundError(resolved);
        }

        return resolved;
    }

    /// <summary>对应 TS <c>fork</c> 的 entry/position 解析。</summary>
    public static ForkTarget ResolveForkTarget(SessionEntry? selectedEntry, string position = "before")
    {
        if (selectedEntry is null)
        {
            throw new InvalidOperationException("Invalid entry ID for forking");
        }

        if (position == "at")
        {
            return new ForkTarget(selectedEntry.Id, null);
        }

        if (selectedEntry is not SessionMessageEntry { Message: UserMessage user })
        {
            throw new InvalidOperationException("Invalid entry ID for forking");
        }

        return new ForkTarget(selectedEntry.ParentId, ExtractUserMessageText(user.Content));
    }

    /// <summary>对应 TS <c>fork</c>：已持久化会话必须有 sessionFile。</summary>
    public static string RequirePersistedSessionFile(string? sessionFile, bool persisted)
    {
        if (!persisted)
        {
            return sessionFile ?? string.Empty;
        }

        if (string.IsNullOrEmpty(sessionFile))
        {
            throw new InvalidOperationException("Persisted session is missing a session file");
        }

        return sessionFile;
    }

    /// <summary>对应 TS <c>fork</c>：克隆/fork 前会话文件必须已落盘。</summary>
    public static string RequireSavedSessionFile(string sessionFile)
    {
        if (!File.Exists(sessionFile))
        {
            throw new InvalidOperationException(
                "This session has not been saved yet. Send a message before cloning or forking it.");
        }

        return sessionFile;
    }

    /// <summary>对应 TS <c>newSession</c>：已持久化则 create，否则 inMemory。</summary>
    public static NewSessionKind ResolveNewSessionKind(bool persisted)
        => persisted ? NewSessionKind.Persisted : NewSessionKind.InMemory;

    /// <summary>对应 TS <c>importFromJsonl</c> 的 sourceAlreadyStored。</summary>
    public static bool IsSourceAlreadyStored(string sourcePath, string destinationPath)
        => string.Equals(
            Path.GetFullPath(destinationPath),
            Path.GetFullPath(sourcePath),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>对应 TS <c>importFromJsonl</c> 的 sessionDir mkdir。</summary>
    public static void EnsureSessionDirectory(string sessionDir)
        => Directory.CreateDirectory(sessionDir);

    /// <summary>对应 TS <c>copyFileSync(..., COPYFILE_EXCL)</c>。</summary>
    public static void CopyImportedJsonl(string sourcePath, string destinationPath, bool sourceAlreadyStored)
    {
        if (sourceAlreadyStored)
        {
            return;
        }

        File.Copy(sourcePath, destinationPath);
    }

    /// <summary>对应 TS <c>importFromJsonl</c> 的目标文件段：建目录 → 去重 → 判定是否已存。</summary>
    /// <remarks>
    /// TS 先算 <c>sourceAlreadyStored</c>（用未去重的目标路径），再去重；
    /// <see cref="UniqueJsonlDestinationPath"/> 在「源即目标」时原样返回，故组合顺序等价。
    /// </remarks>
    public static ImportDestination ResolveImportDestination(string sessionDir, string resolvedSourcePath)
    {
        EnsureSessionDirectory(sessionDir);
        var destination = UniqueJsonlDestinationPath(sessionDir, resolvedSourcePath);
        return new ImportDestination(destination, IsSourceAlreadyStored(resolvedSourcePath, destination));
    }

    /// <summary>
    /// <c>session_before_switch</c> / <c>session_before_fork</c> 的取消判定。
    /// </summary>
    /// <remarks>
    /// 对应 TS：没有处理器直接 <c>{ cancelled: false }</c>；有处理器时取
    /// <c>result?.cancel === true</c>（缺省 / <c>false</c> / 非布尔一律视为未取消）。
    /// </remarks>
    public static bool ResolveBeforeHookCancellation(bool hasHandlers, bool? cancel)
        => hasHandlers && cancel == true;

    /// <summary>对应 TS <c>options?.position ?? "before"</c>。</summary>
    public static string ResolveForkPosition(string? position) => position ?? "before";

    /// <summary>对应 TS <c>fork</c>：<c>createBranchedSession</c> 必须返回路径。</summary>
    public static string RequireForkedSessionPath(string? forkedSessionPath)
        => forkedSessionPath ?? throw new InvalidOperationException("Failed to create forked session");

    /// <summary>
    /// 会话替换时的 <c>session_start</c> 事件。对应 TS <c>switchSession</c> / <c>newSession</c> /
    /// <c>fork</c> / <c>importFromJsonl</c> 四处 <c>{ type: "session_start", reason, previousSessionFile }</c>。
    /// </summary>
    /// <remarks>
    /// <paramref name="reason"/> 取 <see cref="SessionEventReason"/> 的常量。
    /// <c>newSession</c> 与 <c>importFromJsonl</c> 不带 <c>previousSessionFile</c> 之外的区别都在调用点。
    /// </remarks>
    public static ExtensionEvent.SessionStart BuildSessionStartEvent(
        string reason,
        string? previousSessionFile = null)
        => new(reason, previousSessionFile);

    /// <summary>会话替换 / 退出时的 <c>session_shutdown</c> 事件。</summary>
    public static ExtensionEvent.SessionShutdown BuildSessionShutdownEvent(
        string reason,
        string? targetSessionFile = null)
        => new(reason, targetSessionFile);

    // =========================================================================
    // 模块级工厂
    // =========================================================================

    /// <summary>
    /// 从运行时工厂与初始会话目标创建运行时。对应 TS <c>createAgentSessionRuntime</c>。
    /// </summary>
    /// <remarks>同一个工厂会存进返回的运行时，供后续 <c>/new</c>、<c>/resume</c>、<c>/fork</c> 与导入复用。</remarks>
    public static async Task<AgentSessionRuntime> CreateAgentSessionRuntimeAsync(
        CreateAgentSessionRuntimeFactory createRuntime,
        CreateAgentSessionRuntimeOptions options)
    {
        SessionCwd.AssertSessionCwdExists(options.SessionManager, options.Cwd);
        var result = await createRuntime(options);
        return new AgentSessionRuntime(
            result.Session,
            result.Services,
            createRuntime,
            result.Diagnostics,
            result.ModelFallbackMessage);
    }
}
