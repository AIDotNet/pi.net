using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

// ============================================================================
// core/session-manager.ts — 4e-2c-1（类型 + 纯树/投影逻辑）
//
// TS 的会话是一棵 append-only 树，落在 JSONL 文件里：每行一个条目，条目靠 id/parentId 连成树，
// leaf 指针指向当前位置。本文件只承载**类型**与**不触碰文件系统**的纯逻辑；文件 I/O 与
// SessionManager 类留到 4e-2c-2 / 4e-2c-3。
//
// 与 TS 的两处结构性差异：
//   1) TS 的 `SessionEntry` 是按 `type` 字段判别的联合；端口用 `FileEntry`（根，含 header）
//      与 `SessionEntry`（中间抽象，11 个条目类型）两层多态表表达，两者都带
//      `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]`，线路形状与 TS 一致。
//   2) TS 的迁移函数**原地改**（`entry.id = …` / `delete comp.firstKeptEntryIndex`）；端口是
//      不可变 record，迁移改为**返回重建后的列表**，语义等价（见 SessionEntries.MigrateSessionEntries）。
// ============================================================================

/// <summary>
/// 会话文件条目联合。对应 TS <c>FileEntry = SessionHeader | SessionEntry</c>，以 <c>type</c> 字段判别。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionHeader), "session")]
[JsonDerivedType(typeof(SessionMessageEntry), "message")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinking_level_change")]
[JsonDerivedType(typeof(ModelChangeEntry), "model_change")]
[JsonDerivedType(typeof(UsageEntry), "usage")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(BranchSummaryEntry), "branch_summary")]
[JsonDerivedType(typeof(CustomEntry), "custom")]
[JsonDerivedType(typeof(CustomMessageEntry), "custom_message")]
[JsonDerivedType(typeof(ContextEditEntry), "context_edit")]
[JsonDerivedType(typeof(LabelEntry), "label")]
[JsonDerivedType(typeof(SessionInfoEntry), "session_info")]
public abstract record FileEntry
{
    protected FileEntry() { }
}

/// <summary>
/// 会话文件头（每个会话文件的第一行）。对应 TS <c>SessionHeader</c>。
/// </summary>
/// <remarks>
/// 字段声明顺序刻意与 TS 的对象字面量一致（<c>version</c> 在 <c>id</c> 之前），使端口写出的
/// JSONL 与 TS 逐字段同序。
/// </remarks>
public sealed record SessionHeader : FileEntry
{
    /// <summary>会话文件格式版本；v1 文件没有该字段。</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    /// <summary>fork 出来时指向父会话文件。对应 TS <c>parentSession?</c>。</summary>
    [JsonPropertyName("parentSession")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSession { get; init; }
}

/// <summary>
/// 会话条目基类（树结构）。对应 TS <c>SessionEntryBase</c>，以 <c>type</c> 字段判别。
/// </summary>
/// <remarks>
/// <see cref="Id"/> / <see cref="ParentId"/> 是**可空**的：v1 文件的条目没有这两个字段，
/// 由 <see cref="SessionEntries.MigrateSessionEntries"/> 补齐。迁移后一律非空。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionMessageEntry), "message")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinking_level_change")]
[JsonDerivedType(typeof(ModelChangeEntry), "model_change")]
[JsonDerivedType(typeof(UsageEntry), "usage")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(BranchSummaryEntry), "branch_summary")]
[JsonDerivedType(typeof(CustomEntry), "custom")]
[JsonDerivedType(typeof(CustomMessageEntry), "custom_message")]
[JsonDerivedType(typeof(ContextEditEntry), "context_edit")]
[JsonDerivedType(typeof(LabelEntry), "label")]
[JsonDerivedType(typeof(SessionInfoEntry), "session_info")]
public abstract record SessionEntry : FileEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>父条目 id；根条目为 <c>null</c>（TS 恒写该字段，故不加忽略）。</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "";

    /// <summary>重建一个只改 <see cref="Id"/> / <see cref="ParentId"/> 的副本（v1→v2 迁移用）。</summary>
    internal abstract SessionEntry WithTree(string id, string? parentId);
}

/// <summary>消息条目。对应 TS <c>SessionMessageEntry</c>。</summary>
public sealed record SessionMessageEntry : SessionEntry
{
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>思考档位变更。对应 TS <c>ThinkingLevelChangeEntry</c>。</summary>
public sealed record ThinkingLevelChangeEntry : SessionEntry
{
    [JsonPropertyName("thinkingLevel")]
    public required string ThinkingLevel { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>模型切换。对应 TS <c>ModelChangeEntry</c>。</summary>
public sealed record ModelChangeEntry : SessionEntry
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>模型归因用量（不参与 LLM 上下文）。对应 TS <c>UsageEntry</c>。</summary>
public sealed record UsageEntry : SessionEntry
{
    /// <summary>任意用量分类，如 <c>cache_warm</c>。</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }

    /// <summary>用量通知里可选的人类可读限定词。对应 TS <c>note?</c>。</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>上下文压缩条目。对应 TS <c>CompactionEntry&lt;T = unknown&gt;</c>。</summary>
public sealed record CompactionEntry : SessionEntry
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>
    /// 保留区间第一条被保留条目的 id。TS 类型为必填 <c>string</c>，但 v1 遗留的
    /// <c>firstKeptEntryIndex</c> 在目标条目尚未分配 id 时会被写成 <c>undefined</c>（序列化时省略），
    /// 故端口为可空 + <c>WhenWritingNull</c>。
    /// </summary>
    [JsonPropertyName("firstKeptEntryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstKeptEntryId { get; init; }

    [JsonPropertyName("tokensBefore")]
    public required long TokensBefore { get; init; }

    /// <summary>扩展私有数据（如 ArtifactIndex / 结构化压缩的版本标记）。对应 TS <c>details?</c>。</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }

    /// <summary>生成本摘要的 LLM 调用的用量（若有）。对应 TS <c>usage?</c>。</summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? UsageStats { get; init; }

    /// <summary>true 表示由扩展生成，undefined/false 表示 pi 生成。对应 TS <c>fromHook?</c>。</summary>
    [JsonPropertyName("fromHook")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FromHook { get; init; }

    /// <summary>
    /// 该压缩边界处完整的 prompt 与工具状态。对应 TS <c>systemMessage?: SystemMessage</c>。
    /// </summary>
    /// <remarks>
    /// TS 声明为 <c>SystemMessage</c>；端口用 <see cref="ChatMessage"/> 承载，因为 STJ 只在
    /// **声明类型是多态基类**时写出 <c>role</c> 判别符，而 TS 侧 <c>role</c> 是消息自身的普通字段。
    /// 用 <see cref="ChatMessage"/> 声明可保证线路里始终带 <c>role</c>（值恒为 <c>system</c>）。
    /// </remarks>
    [JsonPropertyName("systemMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatMessage? SystemMessage { get; init; }

    /// <summary>
    /// v1 遗留字段：保留区间的**索引**。迁移时换算成 <see cref="FirstKeptEntryId"/> 并清除。
    /// TS 的 <c>CompactionEntry</c> 接口没有它（迁移里靠 cast 读），故端口为可空 + 省略写出。
    /// </summary>
    [JsonPropertyName("firstKeptEntryIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FirstKeptEntryIndex { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>分支回溯摘要条目。对应 TS <c>BranchSummaryEntry&lt;T = unknown&gt;</c>。</summary>
public sealed record BranchSummaryEntry : SessionEntry
{
    [JsonPropertyName("fromId")]
    public required string FromId { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>扩展私有数据（不发往 LLM）。对应 TS <c>details?</c>。</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }

    /// <summary>生成本摘要的 LLM 调用的用量（若有）。对应 TS <c>usage?</c>。</summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? UsageStats { get; init; }

    /// <summary>true 表示由扩展生成。对应 TS <c>fromHook?</c>。</summary>
    [JsonPropertyName("fromHook")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FromHook { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>
/// 扩展自定义条目（持久化扩展状态，**不参与** LLM 上下文）。对应 TS <c>CustomEntry&lt;T = unknown&gt;</c>。
/// </summary>
public sealed record CustomEntry : SessionEntry
{
    [JsonPropertyName("customType")]
    public required string CustomType { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>
/// 扩展自定义消息条目（**参与** LLM 上下文，转成 user 消息）。对应 TS <c>CustomMessageEntry&lt;T&gt;</c>。
/// </summary>
public sealed record CustomMessageEntry : SessionEntry
{
    [JsonPropertyName("customType")]
    public required string CustomType { get; init; }

    /// <summary>字符串或 TextContent/ImageContent 数组。对应 TS <c>content</c>。</summary>
    [JsonPropertyName("content")]
    public MessageContent? Content { get; init; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }

    /// <summary>TUI 渲染控制：false 完全隐藏，true 用区别于 user 消息的样式渲染。</summary>
    [JsonPropertyName("display")]
    public bool Display { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>
/// 上下文编辑条目的替换载荷。对应 TS <c>{ content: ContextEditableContent }</c>。
/// </summary>
/// <remarks>
/// TS 的 <c>ContextEditableContent</c> 是四条消息 content 字段的并集，端口统一用
/// <see cref="MessageContent"/> 承载（字符串或任意内容块数组）。
/// </remarks>
public sealed record ContextEditReplacement
{
    [JsonPropertyName("content")]
    public MessageContent? Content { get; init; }
}

/// <summary>
/// 对更早条目的 append-only 上下文编辑。对应 TS <c>ContextEditEntry</c>。
/// </summary>
public sealed record ContextEditEntry : SessionEntry
{
    [JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    /// <summary>null 表示把目标条目从模型上下文里剔除；有值则只替换其 content。</summary>
    [JsonPropertyName("replacement")]
    public ContextEditReplacement? Replacement { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>用户自定义书签/标记。对应 TS <c>LabelEntry</c>。</summary>
public sealed record LabelEntry : SessionEntry
{
    [JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    /// <summary>undefined（序列化时省略）表示清除该条目的标签。</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>会话元数据条目（如用户自定义显示名）。对应 TS <c>SessionInfoEntry</c>。</summary>
public sealed record SessionInfoEntry : SessionEntry
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    internal override SessionEntry WithTree(string id, string? parentId) => this with { Id = id, ParentId = parentId };
}

/// <summary>投影里携带的模型标识。对应 TS <c>{ provider: string; modelId: string }</c>。</summary>
public sealed record SessionModel(
    [property: JsonPropertyName("provider")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Provider,
    [property: JsonPropertyName("modelId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelId);

/// <summary>
/// 单条条目投影到模型上下文的结果。对应 TS <c>ProjectedSessionEntry</c>。
/// </summary>
public sealed record ProjectedSessionEntry(
    [property: JsonPropertyName("sourceEntry")] SessionEntry SourceEntry,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

/// <summary>保留溯源的、压缩感知的模型上下文。对应 TS <c>SessionProjection</c>。</summary>
public sealed record SessionProjection(
    [property: JsonPropertyName("entries")] IReadOnlyList<ProjectedSessionEntry> Entries,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel,
    [property: JsonPropertyName("model")] SessionModel? Model);

/// <summary>最终模型上下文。对应 TS <c>SessionContext</c>。</summary>
public sealed record SessionContext(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel,
    [property: JsonPropertyName("model")] SessionModel? Model);

/// <summary>会话树节点（<c>getTree()</c> 的防御性副本）。对应 TS <c>SessionTreeNode</c>。</summary>
public sealed record SessionTreeNode
{
    [JsonPropertyName("entry")]
    public required SessionEntry Entry { get; init; }

    [JsonPropertyName("children")]
    public required IReadOnlyList<SessionTreeNode> Children { get; init; }

    /// <summary>该条目解析后的标签（若有）。对应 TS <c>label?</c>。</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary>该条目最近一次标签变更的时间戳（若有）。对应 TS <c>labelTimestamp?</c>。</summary>
    [JsonPropertyName("labelTimestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LabelTimestamp { get; init; }
}

/// <summary>会话列表项。对应 TS <c>SessionInfo</c>（由 4e-2c-2 的文件发现逻辑产出）。</summary>
public sealed record SessionInfo
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>会话启动时的工作目录；旧会话为空串。对应 TS <c>cwd</c>。</summary>
    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    /// <summary>来自 session_info 条目的用户自定义显示名。对应 TS <c>name?</c>。</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>父会话文件路径（fork 出来的会话）。对应 TS <c>parentSessionPath?</c>。</summary>
    [JsonPropertyName("parentSessionPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSessionPath { get; init; }

    [JsonPropertyName("created")]
    public required DateTimeOffset Created { get; init; }

    [JsonPropertyName("modified")]
    public required DateTimeOffset Modified { get; init; }

    [JsonPropertyName("messageCount")]
    public required int MessageCount { get; init; }

    [JsonPropertyName("firstMessage")]
    public required string FirstMessage { get; init; }

    [JsonPropertyName("allMessagesText")]
    public required string AllMessagesText { get; init; }
}

/// <summary>新建会话选项。对应 TS <c>NewSessionOptions</c>。</summary>
public sealed record NewSessionOptions
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("parentSession")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSession { get; init; }
}

/// <summary>会话列表加载进度回调。对应 TS <c>SessionListProgress</c>。</summary>
/// <param name="loaded">已加载数量。</param>
/// <param name="total">总数量。</param>
/// <param name="partialSessions">目前已加载的会话（按活跃度排序），仅周期性更新时提供。</param>
public delegate void SessionListProgress(int loaded, int total, IReadOnlyList<SessionInfo>? partialSessions = null);

/// <summary>
/// <c>core/session-manager.ts</c> 的常量与纯逻辑（4e-2c-1）。
/// </summary>
/// <remarks>
/// 只覆盖不触碰文件系统的部分：迁移、解析、树遍历、上下文投影、会话目录编码。文件读写、
/// 目录发现与 <c>SessionManager</c> 类分别在 4e-2c-2 / 4e-2c-3。
/// </remarks>
public static partial class SessionEntries
{
    /// <summary>当前会话文件格式版本。对应 TS <c>CURRENT_SESSION_VERSION</c>。</summary>
    public const int CurrentVersion = 3;

    // /^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$/ — 用 \z 而非 $，因为 JS 的 `$`（无 m 标志）
    // 只匹配串尾，而 .NET 的 `$` 还会匹配末尾换行之前。
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?\z")]
    private static partial Regex SessionIdPattern();

    // resolvedCwd.replace(/^[/\\]/, "").replace(/[/\\:]/g, "-")
    [GeneratedRegex(@"[/\\:]")]
    private static partial Regex SessionDirSeparatorPattern();

    /// <summary>
    /// 会话线路的 JSON 配置：camelCase + 8 个消息 role + v2 遗留的 <c>hookMessage</c>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AgentMessageJson.Options"/> 的唯一差别是多了 <c>hookMessage</c>——那是 v2 会话
    /// 文件的历史 role，只可能在 <c>parseSessionEntries</c> 时出现，不属于 <c>AgentMessage</c>。
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo => AgentMessageJson.AddCodingAgentRoles(typeInfo));
        resolver.Modifiers.Add(static typeInfo => AddLegacyHookRole(typeInfo));
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = resolver,
        };
    }

    private static void AddLegacyHookRole(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ChatMessage))
        {
            return;
        }

        var polymorphism = typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "role",
        };
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(HookMessage), "hookMessage"));
    }

    // ------------------------------------------------------------------
    // 身份
    // ------------------------------------------------------------------

    /// <summary>
    /// 校验会话 id 只含字母数字与 <c>-</c> / <c>_</c> / <c>.</c>，且首尾为字母数字。
    /// 对应 TS <c>assertValidSessionId</c>。
    /// </summary>
    public static void AssertValidSessionId(string id)
    {
        if (!SessionIdPattern().IsMatch(id))
        {
            throw new ArgumentException(
                "Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', "
                + "and start and end with an alphanumeric character");
        }
    }

    /// <summary>
    /// 生成 8 位十六进制短 id（带碰撞检查）。对应 TS 模块私有的 <c>generateId</c>。
    /// </summary>
    /// <remarks>
    /// TS 用 <c>randomUUID().slice(0, 8)</c>；端口用 <c>Guid.NewGuid().ToString("N")[..8]</c>
    /// （同为小写十六进制）。100 次仍碰撞时退回完整 UUID（TS 为带连字符的 <c>randomUUID()</c>）。
    /// </remarks>
    internal static string GenerateId(Func<string, bool> has)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!has(id))
            {
                return id;
            }
        }

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>新建会话 id（UUIDv7）。对应 TS 模块私有的 <c>createSessionId</c>。</summary>
    public static string CreateSessionId() => Uuid.Uuidv7();

    // ------------------------------------------------------------------
    // 迁移
    // ------------------------------------------------------------------

    /// <summary>
    /// 把所有条目迁移到当前版本。对应 TS <c>migrateSessionEntries</c>。
    /// </summary>
    /// <remarks>
    /// TS 版本原地修改入参并返回 void；端口返回重建后的列表（不可变 record 无法原地改
    /// <c>message.role</c>，故统一采用重建语义）。
    /// </remarks>
    public static IReadOnlyList<FileEntry> MigrateSessionEntries(IReadOnlyList<FileEntry> entries)
        => MigrateToCurrentVersion(entries).Entries;

    /// <summary>对应 TS 模块私有的 <c>migrateToCurrentVersion</c>。</summary>
    internal static (IReadOnlyList<FileEntry> Entries, bool Migrated) MigrateToCurrentVersion(
        IReadOnlyList<FileEntry> entries)
    {
        var header = entries.OfType<SessionHeader>().FirstOrDefault();
        var version = header?.Version ?? 1;

        if (version >= CurrentVersion)
        {
            return (entries, false);
        }

        var current = entries;
        if (version < 2)
        {
            current = MigrateV1ToV2(current);
        }

        if (version < 3)
        {
            current = MigrateV2ToV3(current);
        }

        return (current, true);
    }

    /// <summary>
    /// v1 → v2：补齐 id/parentId 树结构，并把 compaction 的 <c>firstKeptEntryIndex</c> 换成
    /// <c>firstKeptEntryId</c>。对应 TS 模块私有的 <c>migrateV1ToV2</c>。
    /// </summary>
    private static IReadOnlyList<FileEntry> MigrateV1ToV2(IReadOnlyList<FileEntry> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? prevId = null;
        // TS 在遍历中读 `entries[firstKeptEntryIndex].id`——若该目标尚未被遍历到，其 id 仍是
        // undefined。用这个数组复现「此刻已分配了哪些 id」的时序语义。
        var assignedIds = new string?[entries.Count];
        var result = new List<FileEntry>(entries.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is SessionHeader header)
            {
                result.Add(header with { Version = 2 });
                continue;
            }

            var id = GenerateId(ids.Contains);
            var parentId = prevId;
            prevId = id;
            assignedIds[index] = id;

            if (entry is CompactionEntry compaction && compaction.FirstKeptEntryIndex is { } keptIndex)
            {
                var target = keptIndex >= 0 && keptIndex < entries.Count ? entries[keptIndex] : null;
                var firstKeptEntryId = target is not null and not SessionHeader
                    ? assignedIds[keptIndex]
                    : compaction.FirstKeptEntryId;
                result.Add(compaction with
                {
                    Id = id,
                    ParentId = parentId,
                    FirstKeptEntryId = firstKeptEntryId,
                    FirstKeptEntryIndex = null,
                });
                continue;
            }

            result.Add(((SessionEntry)entry).WithTree(id, parentId));
        }

        return result;
    }

    /// <summary>
    /// v2 → v3：把消息 role 从 <c>hookMessage</c> 改名为 <c>custom</c>。对应 TS 模块私有的
    /// <c>migrateV2ToV3</c>。
    /// </summary>
    /// <remarks>
    /// TS 原地改 <c>msgEntry.message.role</c>；端口的 role 是类型判别符，只能把
    /// <see cref="HookMessage"/> 换成 <see cref="CustomMessage"/>（见 <see cref="HookMessage.ToCustomMessage"/>）。
    /// </remarks>
    private static IReadOnlyList<FileEntry> MigrateV2ToV3(IReadOnlyList<FileEntry> entries)
    {
        var result = new List<FileEntry>(entries.Count);
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case SessionHeader header:
                    result.Add(header with { Version = 3 });
                    break;
                case SessionMessageEntry { Message: HookMessage hook } messageEntry:
                    result.Add(messageEntry with { Message = hook.ToCustomMessage() });
                    break;
                default:
                    result.Add(entry);
                    break;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 解析
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 JSONL 文本解析成条目列表，跳过空行与畸形行。对应 TS <c>parseSessionEntries</c>。
    /// </summary>
    /// <remarks>
    /// 已知差异：TS 把「合法 JSON 但 <c>type</c> 未知」的行也保留成普通对象，端口会因判别符未知
    /// 而跳过。pi 自己写出的会话文件只含已知类型。
    /// </remarks>
    public static IReadOnlyList<FileEntry> ParseSessionEntries(string content)
    {
        var entries = new List<FileEntry>();
        foreach (var line in content.Trim().Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<FileEntry>(line, JsonOptions) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 跳过畸形行（对齐 TS 的 try/catch）。
            }
        }

        return entries;
    }

    /// <summary>路径上最新的压缩条目（从尾到头找）。对应 TS <c>getLatestCompactionEntry</c>。</summary>
    public static CompactionEntry? GetLatestCompactionEntry(IReadOnlyList<SessionEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is CompactionEntry compaction)
            {
                return compaction;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 树遍历与投影
    // ------------------------------------------------------------------

    /// <summary>对应 TS 模块私有的 <c>buildEntryIndex</c>（端口始终按 entries 重建）。</summary>
    private static Dictionary<string, SessionEntry> BuildEntryIndex(IReadOnlyList<SessionEntry> entries)
    {
        var index = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Id is { } id)
            {
                index[id] = entry;
            }
        }

        return index;
    }

    /// <summary>
    /// 从 leaf 回溯到 root 的条目路径。对应 TS 模块私有的 <c>buildSessionPath</c>（leafId 省略）。
    /// </summary>
    internal static IReadOnlyList<SessionEntry> BuildSessionPath(IReadOnlyList<SessionEntry> entries)
        => BuildSessionPathCore(entries, leafIdSpecified: false, leafId: null);

    /// <summary>
    /// 从指定 leaf 回溯到 root 的条目路径。对应 TS <c>buildSessionPath</c>（leafId 显式给出）。
    /// </summary>
    /// <remarks>
    /// TS 的三态语义完整保留：<paramref name="leafId"/> 为 <c>null</c> 返回空路径（leaf 已重置到
    /// 起点之前）；为空串时按「未指定」处理（TS 的真值判断）；找不到时回退到最后一个条目。
    /// </remarks>
    internal static IReadOnlyList<SessionEntry> BuildSessionPath(IReadOnlyList<SessionEntry> entries, string? leafId)
        => BuildSessionPathCore(entries, leafIdSpecified: true, leafId);

    private static IReadOnlyList<SessionEntry> BuildSessionPathCore(
        IReadOnlyList<SessionEntry> entries, bool leafIdSpecified, string? leafId)
    {
        var index = BuildEntryIndex(entries);

        if (leafIdSpecified && leafId is null)
        {
            return [];
        }

        SessionEntry? leaf = null;
        if (leafIdSpecified && !string.IsNullOrEmpty(leafId))
        {
            leaf = index.GetValueOrDefault(leafId);
        }

        leaf ??= entries.Count > 0 ? entries[^1] : null;
        if (leaf is null)
        {
            return [];
        }

        var path = new List<SessionEntry>();
        SessionEntry? current = leaf;
        while (current is not null)
        {
            path.Add(current);
            current = !string.IsNullOrEmpty(current.ParentId) ? index.GetValueOrDefault(current.ParentId) : null;
        }

        path.Reverse();
        return path;
    }

    /// <summary>对应 TS 模块私有的 <c>getSessionContextSettings</c>。</summary>
    private static (string ThinkingLevel, SessionModel? Model) GetSessionContextSettings(
        IReadOnlyList<SessionEntry> path)
    {
        var thinkingLevel = "off";
        SessionModel? model = null;

        foreach (var entry in path)
        {
            switch (entry)
            {
                case ThinkingLevelChangeEntry change:
                    thinkingLevel = change.ThinkingLevel;
                    break;
                case ModelChangeEntry change:
                    model = new SessionModel(change.Provider, change.ModelId);
                    break;
                case SessionMessageEntry { Message: AssistantMessage assistant }:
                    model = new SessionModel(assistant.Provider, assistant.Model);
                    break;
            }
        }

        return (thinkingLevel, model);
    }

    /// <summary>
    /// 把一条会话条目投影成 LLM/运行时消息。对应 TS <c>sessionEntryToContextMessages</c>。
    /// </summary>
    /// <remarks>
    /// 纯展示/状态条目（custom / label / session_info / usage / model_change / thinking_level_change /
    /// context_edit）不参与上下文，返回空列表。
    /// </remarks>
    public static IReadOnlyList<ChatMessage> SessionEntryToContextMessages(SessionEntry entry)
    {
        switch (entry)
        {
            case SessionMessageEntry messageEntry:
            {
                var message = messageEntry.Message;
                return message switch
                {
                    // 会话文件解析不做校验：旧版本 / fork / 手改文件可能带 null/缺失的 content。
                    SystemMessage system when system.Content is null =>
                        [system with { Content = new MessageContent.Text("") }],
                    UserMessage user when user.Content is null =>
                        [user with { Content = MessageContent.BlocksOf([]) }],
                    AssistantMessage assistant when assistant.Content is null =>
                        [assistant with { Content = [] }],
                    ToolResultMessage toolResult when toolResult.Content is null =>
                        [toolResult with { Content = [] }],
                    _ => [message],
                };
            }
            case CustomMessageEntry customMessage:
                return
                [
                    AgentMessages.CreateCustomMessage(
                        customMessage.CustomType,
                        customMessage.Content ?? MessageContent.BlocksOf([]),
                        customMessage.Display,
                        customMessage.Details,
                        customMessage.Timestamp),
                ];
            case BranchSummaryEntry branchSummary when !string.IsNullOrEmpty(branchSummary.Summary):
                return
                [
                    AgentMessages.CreateBranchSummaryMessage(
                        branchSummary.Summary, branchSummary.FromId, branchSummary.Timestamp),
                ];
            case CompactionEntry compaction:
            {
                var summary = AgentMessages.CreateCompactionSummaryMessage(
                    compaction.Summary, compaction.TokensBefore, compaction.Timestamp);
                return compaction.SystemMessage is { } systemMessage ? [systemMessage, summary] : [summary];
            }
            default:
                return [];
        }
    }

    /// <summary>
    /// 构建压缩感知的活动条目列表。对应 TS <c>buildContextEntries</c>（leafId 省略）。
    /// </summary>
    public static IReadOnlyList<SessionEntry> BuildContextEntries(IReadOnlyList<SessionEntry> entries)
        => BuildContextEntriesCore(entries, leafIdSpecified: false, leafId: null);

    /// <summary>构建压缩感知的活动条目列表。对应 TS <c>buildContextEntries</c>。</summary>
    public static IReadOnlyList<SessionEntry> BuildContextEntries(
        IReadOnlyList<SessionEntry> entries, string? leafId)
        => BuildContextEntriesCore(entries, leafIdSpecified: true, leafId);

    private static IReadOnlyList<SessionEntry> BuildContextEntriesCore(
        IReadOnlyList<SessionEntry> entries, bool leafIdSpecified, string? leafId)
    {
        var path = BuildSessionPathCore(entries, leafIdSpecified, leafId);

        CompactionEntry? compaction = null;
        foreach (var entry in path)
        {
            if (entry is CompactionEntry found)
            {
                compaction = found;
            }
        }

        if (compaction is null)
        {
            return path;
        }

        var compactionIndex = IndexOfId(path, compaction.Id);
        if (compactionIndex < 0)
        {
            return path;
        }

        var contextEntries = new List<SessionEntry> { compaction };
        var foundFirstKept = false;
        for (var index = 0; index < compactionIndex; index++)
        {
            var entry = path[index];
            if (string.Equals(entry.Id, compaction.FirstKeptEntryId, StringComparison.Ordinal))
            {
                foundFirstKept = true;
            }

            if (foundFirstKept && entry is not SessionMessageEntry { Message: SystemMessage })
            {
                contextEntries.Add(entry);
            }
        }

        for (var index = compactionIndex + 1; index < path.Count; index++)
        {
            contextEntries.Add(path[index]);
        }

        return contextEntries;
    }

    private static int IndexOfId(IReadOnlyList<SessionEntry> entries, string? id)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (string.Equals(entries[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>把一条条目投影到上下文，并按需应用上下文编辑。对应 TS 模块私有的 <c>projectContextEntry</c>。</summary>
    private static IReadOnlyList<ChatMessage> ProjectContextEntry(SessionEntry entry, ContextEditEntry? edit)
    {
        var messages = SessionEntryToContextMessages(entry);
        if (edit is null)
        {
            return messages;
        }

        var replacement = edit.Replacement;
        if (replacement is null)
        {
            return [];
        }

        var replacementContent = replacement.Content ?? MessageContent.BlocksOf([]);
        var projected = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage or AssistantMessage or ToolResultMessage or CustomMessage:
                {
                    // assistant / toolResult 的 content 恒为块数组，字符串替换值要包成单个文本块。
                    var content = message is AssistantMessage or ToolResultMessage
                                  && replacementContent is MessageContent.Text text
                        ? MessageContent.BlocksOf([new TextContent(text.Value)])
                        : replacementContent;
                    projected.Add(ApplyContent(message, content));
                    break;
                }
                default:
                    projected.Add(message);
                    break;
            }
        }

        return projected;
    }

    /// <summary>把 content 写回消息（对齐 TS 的 <c>{ ...message, content }</c>）。</summary>
    private static ChatMessage ApplyContent(ChatMessage message, MessageContent content) => message switch
    {
        UserMessage user => user with { Content = content },
        CustomMessage custom => custom with { Content = content },
        AssistantMessage assistant => assistant with { Content = content.ToBlocks() },
        ToolResultMessage toolResult => toolResult with { Content = content.ToBlocks() },
        _ => message,
    };

    /// <summary>
    /// 构建保留溯源的、压缩感知的模型上下文。对应 TS <c>buildSessionProjection</c>（leafId 省略）。
    /// </summary>
    public static SessionProjection BuildSessionProjection(IReadOnlyList<SessionEntry> entries)
        => BuildSessionProjectionCore(entries, leafIdSpecified: false, leafId: null);

    /// <summary>构建保留溯源的、压缩感知的模型上下文。对应 TS <c>buildSessionProjection</c>。</summary>
    public static SessionProjection BuildSessionProjection(IReadOnlyList<SessionEntry> entries, string? leafId)
        => BuildSessionProjectionCore(entries, leafIdSpecified: true, leafId);

    private static SessionProjection BuildSessionProjectionCore(
        IReadOnlyList<SessionEntry> entries, bool leafIdSpecified, string? leafId)
    {
        var path = BuildSessionPathCore(entries, leafIdSpecified, leafId);
        var (thinkingLevel, model) = GetSessionContextSettings(path);
        var contextEntries = BuildContextEntriesCore(entries, leafIdSpecified, leafId);

        var edits = new Dictionary<string, ContextEditEntry>(StringComparer.Ordinal);
        foreach (var entry in contextEntries)
        {
            // TS: `edits.set(entry.targetId, entry)`——按被编辑条目的 id 索引，而不是编辑条目自己的 id。
            if (entry is ContextEditEntry edit)
            {
                edits[edit.TargetId] = edit;
            }
        }

        var projectedEntries = new List<ProjectedSessionEntry>(contextEntries.Count);
        var messages = new List<ChatMessage>();
        for (var index = 0; index < contextEntries.Count; index++)
        {
            var sourceEntry = contextEntries[index];
            // buildContextEntries() 可能因为「原始 id 落在最新保留区间内」而留下更早的 compaction 条目；
            // 只有位于索引 0 的最新 compaction 才贡献检查点与摘要。
            var entryMessages = sourceEntry is CompactionEntry && index > 0
                ? []
                : ProjectContextEntry(
                    sourceEntry,
                    sourceEntry.Id is { } sourceId ? edits.GetValueOrDefault(sourceId) : null);
            projectedEntries.Add(new ProjectedSessionEntry(sourceEntry, entryMessages));
            messages.AddRange(entryMessages);
        }

        return new SessionProjection(projectedEntries, messages, thinkingLevel, model);
    }

    /// <summary>构建最终模型上下文。对应 TS <c>buildSessionContext</c>（leafId 省略）。</summary>
    public static SessionContext BuildSessionContext(IReadOnlyList<SessionEntry> entries)
    {
        var projection = BuildSessionProjection(entries);
        return new SessionContext(projection.Messages, projection.ThinkingLevel, projection.Model);
    }

    /// <summary>构建最终模型上下文。对应 TS <c>buildSessionContext</c>。</summary>
    public static SessionContext BuildSessionContext(IReadOnlyList<SessionEntry> entries, string? leafId)
    {
        var projection = BuildSessionProjection(entries, leafId);
        return new SessionContext(projection.Messages, projection.ThinkingLevel, projection.Model);
    }

    // ------------------------------------------------------------------
    // 会话目录
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 cwd 编码成 <c>~/.pi/agent/sessions/</c> 下的安全目录名。对应 TS 模块私有的
    /// <c>getDefaultSessionDirPath</c>。
    /// </summary>
    public static string GetDefaultSessionDirPath(string cwd, string? agentDir = null)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        var resolvedAgentDir = Paths.ResolvePath(agentDir ?? Config.GetAgentDir());
        var stripped = resolvedCwd.Length > 0 && (resolvedCwd[0] == '/' || resolvedCwd[0] == '\\')
            ? resolvedCwd[1..]
            : resolvedCwd;
        var safePath = $"--{SessionDirSeparatorPattern().Replace(stripped, "-")}--";
        return NodePath.Join(resolvedAgentDir, "sessions", safePath);
    }
}
