using System.Text.Json.Nodes;

namespace Pi.Mcp.Protocol;

/// <summary>
/// MCP 协议版本常量。对应 TS <c>protocol/types.ts</c> 头部：
/// 客户端接受的服务端版本列表（旧版 SDK 服务器以自己的最新版本应答，因此保持接受）。
/// </summary>
public static class McpProtocolVersions
{
    public const string Latest = "2025-11-25";

    public static readonly IReadOnlyList<string> Supported =
    [
        Latest, "2025-06-18", "2025-03-26", "2024-11-05",
    ];

    public static bool IsSupported(string version) => Supported.Contains(version);
}

/// <summary>客户端/服务器实现信息。对应 TS <c>Implementation</c>。</summary>
public sealed record McpImplementation(string Name, string Version, string? Title = null);

/// <summary>客户端能力声明（能力集合的具体形态由服务器协商，松散承载）。对应 TS <c>ClientCapabilities</c>。</summary>
public sealed record ClientCapabilities(JsonObject? Raw = null)
{
    public bool HasRoots => Raw?.ContainsKey("roots") == true;
}

/// <summary>服务器能力声明。对应 TS <c>ServerCapabilities</c>。</summary>
public sealed record ServerCapabilities(JsonObject Raw);

/// <summary>MCP 工具描述。对应 TS <c>Tool</c>。</summary>
public sealed record McpTool(
    string Name,
    JsonObject InputSchema,
    string? Description = null,
    string? Title = null)
{
    /// <summary>工具注解（readOnlyHint 等行为提示与 title）。对应 TS <c>annotations</c>。</summary>
    public JsonObject? Annotations { get; init; }

    /// <summary>工具声明的输出 schema（<c>outputSchema</c>）。</summary>
    public JsonObject? OutputSchema { get; init; }

    /// <summary>任务执行提示（<c>execution</c>）。对应 TS <c>ToolExecution</c>。</summary>
    public JsonObject? Execution { get; init; }

    /// <summary>服务器附带的元数据（<c>_meta</c>）。</summary>
    public JsonObject? Meta { get; init; }
}

/// <summary>
/// <c>notifications/progress</c> 载荷。对应 TS <c>ProgressNotification</c>；
/// <c>progressToken</c> 由客户端以请求 id 生成。
/// </summary>
public sealed record McpProgressNotification(
    JsonRpcId ProgressToken,
    double Progress,
    double? Total = null,
    string? Message = null);

/// <summary>MCP 资源描述。对应 TS <c>Resource</c>（规范要求 name，但省略时以 uri 代替）。</summary>
public sealed record McpResource(string Uri, string? Name = null)
{
    /// <summary>
    /// 原始 JSON 条目，<c>name</c> 已按 TS <c>toResource</c> 语义补上默认值（<c>{...item, name: item.name ?? item.uri}</c>）。
    /// 扩展层把它序列化给模型时要保留 <c>title</c> / <c>description</c> / <c>mimeType</c> / <c>size</c> 等字段。
    /// </summary>
    public JsonObject? Raw { get; init; }

    /// <summary>可选标题（<c>title</c>）。</summary>
    public string? Title => (Raw?["title"] as JsonValue)?.GetValue<string>();

    /// <summary>可选描述（<c>description</c>）。</summary>
    public string? Description => (Raw?["description"] as JsonValue)?.GetValue<string>();

    /// <summary>可选 MIME 类型（<c>mimeType</c>）。</summary>
    public string? MimeType => (Raw?["mimeType"] as JsonValue)?.GetValue<string>();
}

/// <summary>MCP 资源模板。对应 TS <c>ResourceTemplate</c>。</summary>
public sealed record McpResourceTemplate(string UriTemplate, string? Name = null)
{
    /// <summary>原始 JSON 条目，<c>name</c> 已按 TS <c>toResourceTemplate</c> 语义补默认值。</summary>
    public JsonObject? Raw { get; init; }

    /// <summary>可选标题（<c>title</c>）。</summary>
    public string? Title => (Raw?["title"] as JsonValue)?.GetValue<string>();

    /// <summary>可选描述（<c>description</c>）。</summary>
    public string? Description => (Raw?["description"] as JsonValue)?.GetValue<string>();

    /// <summary>可选 MIME 类型（<c>mimeType</c>）。</summary>
    public string? MimeType => (Raw?["mimeType"] as JsonValue)?.GetValue<string>();
}

/// <summary>resources/list 的结果。对应 TS <c>ListResourcesResult</c>。</summary>
public sealed record ListResourcesResult(IReadOnlyList<McpResource> Resources, string? NextCursor = null);

/// <summary>resources/templates/list 的结果。对应 TS <c>ListResourceTemplatesResult</c>。</summary>
public sealed record ListResourceTemplatesResult(
    IReadOnlyList<McpResourceTemplate> ResourceTemplates, string? NextCursor = null);

/// <summary>initialize 的完整应答。对应 TS <c>InitializeResult</c>。</summary>
public sealed record InitializeResult(
    string ProtocolVersion,
    ServerCapabilities Capabilities,
    McpImplementation ServerInfo,
    string? Instructions = null);

/// <summary>工具调用结果内容块（松散承载）。对应 TS <c>ContentBlock</c> 联合。</summary>
public sealed record McpContentBlock(JsonObject Raw);

/// <summary>tools/call 的结果。对应 TS <c>CallToolResult</c>（仅返回 structuredContent 的服务器按规范补空 content）。</summary>
/// <param name="StructuredContent">
/// 任意 JSON 值（数组、标量等；MCP 2026-07-28，更早的修订只允许对象）。
/// 类型不再是 <see cref="JsonObject"/>：服务器可以合法返回非对象结构化内容（上游 #10416）。
/// JSON <c>null</c> 在 C# 侧与「未提供」不可区分，按无结构化内容处理。
/// </param>
public sealed record CallToolResult(IReadOnlyList<McpContentBlock> Content, JsonNode? StructuredContent = null)
{
    /// <summary>便捷判断：结果是否为错误（isError=true）。</summary>
    public bool IsError { get; init; }
}

/// <summary>
/// 资源读取结果条目：text 或 blob 二选一。对应 TS <c>contents</c> 元素
/// （<c>TextResourceContents</c> / <c>BlobResourceContents</c>，二者都可带 <c>mimeType</c>）。
/// </summary>
public sealed record McpResourceContents(string Uri, string? Text = null, string? Blob = null)
{
    /// <summary>可选 MIME 类型（<c>mimeType</c>）。blob 资源据此决定渲染成图片还是存盘。</summary>
    public string? MimeType { get; init; }
}

/// <summary>resources/read 的结果。对应 TS <c>ReadResourceResult</c>。</summary>
public sealed record ReadResourceResult(IReadOnlyList<McpResourceContents> Contents);

/// <summary>MCP 根目录声明（roots/list 应答项）。对应 TS <c>Root</c>。</summary>
public sealed record McpRoot(string Uri, string? Name = null);

/// <summary>
/// 协议类型与结果校验。对应 TS <c>client.ts</c> 里的 validateInitializeResult /
/// validateCallToolResult / validateReadResourceResult 等函数。
/// </summary>
public static class McpProtocol
{
    /// <summary>校验 initialize 应答的结构与字段类型。</summary>
    public static InitializeResult ValidateInitializeResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("protocolVersion", out var versionNode) && versionNode is JsonValue v1
            && v1.TryGetValue<string>(out var protocolVersion)
            && value.TryGetPropertyValue("capabilities", out var caps) && caps is JsonObject capabilities
            && value.TryGetPropertyValue("serverInfo", out var info) && info is JsonObject serverInfo
            && serverInfo.TryGetPropertyValue("name", out var nameNode) && nameNode is JsonValue { } nv
            && nv.TryGetValue<string>(out var serverName)
            && serverInfo.TryGetPropertyValue("version", out var versionNode2) && versionNode2 is JsonValue { } vv
            && vv.TryGetValue<string>(out var serverVersion))
        {
            string? instructions = null;
            if (value.TryGetPropertyValue("instructions", out var instr) && instr is JsonValue iv)
                iv.TryGetValue(out instructions);
            return new InitializeResult(
                protocolVersion,
                new ServerCapabilities(capabilities),
                new McpImplementation(serverName, serverVersion),
                instructions);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP initialize result");
    }

    /// <summary>校验 tools/call 结果；仅返回 structuredContent 的服务器补空 content。</summary>
    public static CallToolResult ValidateCallToolResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("content", out var content) && content is not JsonArray)
            throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP tools/call result");
        // structuredContent 可以是任意 JSON 值（数组、null、标量），不再要求对象（上游 #10416）。

        var blocks = (content as JsonArray)?.OfType<JsonObject>()
            .Select(b => new McpContentBlock(b)).ToList() ?? [];
        var isError = value.TryGetPropertyValue("isError", out var isErr) && isErr is JsonValue { } isErrV
            && isErrV.TryGetValue<bool>(out var isErrBool) && isErrBool;
        // 深拷贝：结果要能脱离原始 JSON-RPC 报文独立存活。
        var structuredContent = value["structuredContent"]?.DeepClone();
        return new CallToolResult(blocks, structuredContent) { IsError = isError };
    }

    /// <summary>校验 resources/read 结果。</summary>
    public static ReadResourceResult ValidateReadResourceResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("contents", out var contents) && contents is JsonArray array)
        {
            var items = new List<McpResourceContents>();
            foreach (var item in array.OfType<JsonObject>())
            {
                if (item.TryGetPropertyValue("uri", out var uriNode) && uriNode is JsonValue { } uv
                    && uv.TryGetValue<string>(out var uri))
                {
                    var text = item["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
                    var blob = item["blob"] is JsonValue bv && bv.TryGetValue<string>(out var b) ? b : null;
                    var mimeType = item["mimeType"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
                    if (text is not null || blob is not null)
                    {
                        items.Add(new McpResourceContents(uri, text, blob) { MimeType = mimeType });
                        continue;
                    }
                }
                throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid contents in MCP resources/read result");
            }
            return new ReadResourceResult(items);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP resources/read result");
    }

    /// <summary>校验并提取一页列表结果（items 数组 + 可选 nextCursor；null/"" 视为无游标）。</summary>
    public static (IReadOnlyList<JsonObject> Items, string? NextCursor) ValidateListPage(
        string method, string key, JsonObject value, Func<JsonObject, bool> isItem)
    {
        if (value.TryGetPropertyValue(key, out var itemsNode) && itemsNode is JsonArray array)
        {
            var items = new List<JsonObject>();
            foreach (var item in array.OfType<JsonObject>())
            {
                if (!isItem(item))
                    throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid entry in MCP {method} result");
                items.Add(item);
            }
            string? nextCursor = null;
            if (value.TryGetPropertyValue("nextCursor", out var cursorNode))
            {
                // 部分服务器以 null 或 "" 结束分页：视为无游标。
                if (cursorNode is JsonValue { } cv && cv.TryGetValue<string>(out var c) && c.Length > 0)
                    nextCursor = c;
            }
            return (items, nextCursor);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid MCP {method} result");
    }

    /// <summary>工具条目校验：name 必填 + inputSchema 为对象。</summary>
    public static bool IsTool(JsonObject tool)
        => tool.TryGetPropertyValue("name", out var name) && name is JsonValue
            && tool.TryGetPropertyValue("inputSchema", out var schema) && schema is JsonObject;

    /// <summary>资源条目校验：uri 必填，name 可省略。</summary>
    public static bool IsResource(JsonObject resource)
        => resource.TryGetPropertyValue("uri", out var uri) && uri is JsonValue;

    /// <summary>资源模板条目校验：uriTemplate 必填。</summary>
    public static bool IsResourceTemplate(JsonObject template)
        => template.TryGetPropertyValue("uriTemplate", out var uriTemplate) && uriTemplate is JsonValue;

    /// <summary>把JsonObject 转为 McpTool。</summary>
    public static McpTool ToTool(JsonObject tool)
    {
        var name = (tool["name"] as JsonValue)?.GetValue<string>() ?? "";
        var description = (tool["description"] as JsonValue)?.GetValue<string>();
        var title = (tool["title"] as JsonValue)?.GetValue<string>();
        return new McpTool(name, (tool["inputSchema"] as JsonObject)!, description, title)
        {
            OutputSchema = tool["outputSchema"] as JsonObject,
            Annotations = tool["annotations"] as JsonObject,
            Execution = tool["execution"] as JsonObject,
            Meta = tool["_meta"] as JsonObject,
        };
    }

    /// <summary>把 JsonObject 转为 McpResource（name 缺省取 uri；Raw 保留其余字段）。</summary>
    public static McpResource ToResource(JsonObject resource)
    {
        var uri = (resource["uri"] as JsonValue)?.GetValue<string>() ?? "";
        var name = (resource["name"] as JsonValue)?.GetValue<string>() ?? uri;
        var raw = (JsonObject)resource.DeepClone();
        raw["name"] = name;
        return new McpResource(uri, name) { Raw = raw };
    }

    /// <summary>把 JsonObject 转为 McpResourceTemplate（name 缺省取 uriTemplate；Raw 保留其余字段）。</summary>
    public static McpResourceTemplate ToResourceTemplate(JsonObject template)
    {
        var uriTemplate = (template["uriTemplate"] as JsonValue)?.GetValue<string>() ?? "";
        var name = (template["name"] as JsonValue)?.GetValue<string>() ?? uriTemplate;
        var raw = (JsonObject)template.DeepClone();
        raw["name"] = name;
        return new McpResourceTemplate(uriTemplate, name) { Raw = raw };
    }
}
