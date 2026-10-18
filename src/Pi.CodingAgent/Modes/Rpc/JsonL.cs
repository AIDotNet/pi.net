using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Modes.Rpc;

// ============================================================================
// RPC/print/json 共用的 JSONL 线路层 — TS `modes/rpc/jsonl.ts`（58 行）。
//
// 帧格式是**纯 LF**：负载字符串里可以出现 U+2028 / U+2029 等其他 Unicode 分隔符，
// 客户端必须只按 `\n` 切分（TS 注释原文）。因此这里**不用** Console.ReadLine /
// TextReader.ReadLine——它们会把孤立的 `\r` 也当作行尾。
// ============================================================================
/// <summary>JSONL 序列化 / 反序列化 / 行读取。对应 TS <c>modes/rpc/jsonl.ts</c>。</summary>
public static class JsonL
{
    /// <summary>
    /// 线路 JSON 配置。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>CamelCase</c>：TS 的字面量对象本来就是 camelCase。</item>
    /// <item><c>WhenWritingNull</c>：对齐 <c>JSON.stringify</c> 丢 undefined 键
    /// （4e-2b 已把消息类可空字段逐条钉死，这里在选项层统一收口）。</item>
    /// <item><see cref="JsonStringifyEncoder"/>：逐字符复刻 <c>JSON.stringify</c>
    /// 的转义（只转义 <c>"</c> <c>\</c> 控制字符与孤立代理，U+2028/U+2029 与非 ASCII
    /// 原样）——内置编码器无一等价，详见该嵌套类的注释。</item>
    /// <item>两个类型信息修饰器：给 <see cref="ChatMessage"/> 补 coding-agent 的 4 个自定义
    /// role（复用 <see cref="AgentMessageJson.AddCodingAgentRoles"/>），给
    /// <see cref="AssistantMessageEvent"/> 补 12 个流式事件的 type 判别式。</item>
    /// </list>
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>对应 TS <c>serializeJsonLine</c>：一条严格 JSONL 记录（含结尾 LF）。</summary>
    public static string SerializeLine(object value) => JsonSerializer.Serialize(value, Options) + "\n";

    /// <summary>把任意负载写成 JSON 节点（响应 data / 事件投影用）。</summary>
    public static JsonNode? SerializeToNode(object value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>
    /// 把一行 JSON 解析成 <see cref="RpcCommand"/>。
    /// </summary>
    /// <returns>
    /// 解析成功返回命令；<paramref name="node"/> 不是对象、或缺 <c>type</c>、
    /// 或 <c>type</c> 不在 33 个已知命令内时返回 <c>null</c>（调用方走 TS 的
    /// <c>Unknown command: …</c> 分支）。
    /// </returns>
    /// <exception cref="JsonException">
    /// type 是已知命令但负载形状不符（缺必填字段等）——对应 TS 里「处理器内部抛错」
    /// 的路径，由调用方转成错误响应。
    /// </exception>
    public static RpcCommand? TryDeserializeCommand(JsonNode? node)
    {
        if (node is not JsonObject)
        {
            return null;
        }

        if (node["type"] is not JsonValue typeValue
            || !typeValue.TryGetValue(out string? typeText)
            || typeText is null)
        {
            return null;
        }

        if (!KnownCommandTypes.Contains(typeText))
        {
            return null;
        }

        // STJ 要求 type 判别式是 JSON 对象的**第一个**键（实测：位于其他键之后会抛
        // "must specify a type discriminator"）。协议不保证客户端按此顺序发送
        // （TS 客户端恰好 type 在前），故这里把 type 挪到最前再反序列化。
        var reordered = new JsonObject { ["type"] = typeText };
        foreach (var property in (JsonObject)node)
        {
            if (!string.Equals(property.Key, "type", StringComparison.Ordinal))
            {
                reordered[property.Key] = property.Value?.DeepClone();
            }
        }

        return reordered.Deserialize<RpcCommand>(Options);
    }

    /// <summary>TS <c>RpcCommandType</c>：33 个命令判别串。</summary>
    public static readonly IReadOnlySet<string> KnownCommandTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "prompt", "steer", "follow_up", "abort", "clear_queue", "new_session",
        "get_state",
        "set_model", "cycle_model", "get_available_models",
        "set_thinking_level", "cycle_thinking_level", "get_available_thinking_levels",
        "set_steering_mode", "set_follow_up_mode",
        "compact", "set_auto_compaction",
        "set_auto_retry", "abort_retry",
        "bash", "abort_bash",
        "get_session_stats", "export_html", "switch_session", "fork", "clone",
        "get_fork_messages", "get_entries", "get_tree", "get_last_assistant_text", "set_session_name",
        "get_messages",
        "get_commands",
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo => AgentMessageJson.AddCodingAgentRoles(typeInfo));
        resolver.Modifiers.Add(static typeInfo => AddAssistantMessageEventDiscriminators(typeInfo));
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JsonStringifyEncoder.Instance,
            TypeInfoResolver = resolver,
        };
    }

    /// <summary>
    /// 精确复刻 <c>JSON.stringify</c> 的字符串转义：只转义 <c>"</c>、<c>\</c>、
    /// 控制字符（U+0000–U+001F，含 <c>\b \t \n \f \r</c> 短式）与孤立代理；
    /// 其余一律原样——包括 U+2028 / U+2029 与非 ASCII。
    /// </summary>
    /// <remarks>
    /// 内置编码器没有等价物：<c>Default</c> 转义一切非 ASCII；
    /// <c>UnsafeRelaxedJsonEscaping</c> 仍转义 U+2028/U+2029（实测）；
    /// <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c> 保留 U+2028 但会转义
    /// <c>&lt; &gt; &amp; '</c>（JSON.stringify 不转义）。TS 测试
    /// <c>rpc-jsonl.test.ts</c> 钉住「行里出现原始 U+2028/U+2029」，故自制编码器。
    /// </remarks>
    private sealed class JsonStringifyEncoder : JavaScriptEncoder
    {
        public static readonly JsonStringifyEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6; // \uXXXX

        public override bool WillEncode(int unicodeScalar)
            => unicodeScalar == '"'
                || unicodeScalar == '\\'
                || unicodeScalar < 0x20
                || (unicodeScalar >= 0xD800 && unicodeScalar <= 0xDFFF);

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
            {
                var c = text[i];
                if (c < 0x20 || c == '"' || c == '\\')
                {
                    return i;
                }

                // 孤立代理要转义；合法代理对留给 TryEncodeUnicodeScalar 原样透传。
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= textLength || !char.IsLowSurrogate(text[i + 1]))
                    {
                        return i;
                    }

                    i++; // 跳过配对的低位代理
                }
                else if (char.IsLowSurrogate(c))
                {
                    return i;
                }
            }

            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(
            int unicodeScalar,
            char* buffer,
            int bufferLength,
            out int numberOfCharactersWritten)
        {
            if (!WillEncode(unicodeScalar))
            {
                // 原样透传（BMP 单字符；增补平面写成代理对）。
                if (unicodeScalar <= 0xFFFF)
                {
                    if (bufferLength < 1)
                    {
                        numberOfCharactersWritten = 0;
                        return false;
                    }

                    buffer[0] = (char)unicodeScalar;
                    numberOfCharactersWritten = 1;
                    return true;
                }

                if (bufferLength < 2)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }

                var value = unicodeScalar - 0x10000;
                buffer[0] = (char)(0xD800 + (value >> 10));
                buffer[1] = (char)(0xDC00 + (value & 0x3FF));
                numberOfCharactersWritten = 2;
                return true;
            }

            switch (unicodeScalar)
            {
                case '"':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', '"');
                    return numberOfCharactersWritten > 0;
                case '\\':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', '\\');
                    return numberOfCharactersWritten > 0;
                case '\b':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', 'b');
                    return numberOfCharactersWritten > 0;
                case '\t':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', 't');
                    return numberOfCharactersWritten > 0;
                case '\n':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', 'n');
                    return numberOfCharactersWritten > 0;
                case '\f':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', 'f');
                    return numberOfCharactersWritten > 0;
                case '\r':
                    numberOfCharactersWritten = WriteTwo(buffer, bufferLength, '\\', 'r');
                    return numberOfCharactersWritten > 0;
                default:
                    // 其余控制字符与孤立代理：\uXXXX（小写十六进制，对齐 JSON.stringify）。
                    if (bufferLength < 6)
                    {
                        numberOfCharactersWritten = 0;
                        return false;
                    }

                    buffer[0] = '\\';
                    buffer[1] = 'u';
                    buffer[2] = HexDigit((unicodeScalar >> 12) & 0xF);
                    buffer[3] = HexDigit((unicodeScalar >> 8) & 0xF);
                    buffer[4] = HexDigit((unicodeScalar >> 4) & 0xF);
                    buffer[5] = HexDigit(unicodeScalar & 0xF);
                    numberOfCharactersWritten = 6;
                    return true;
            }
        }

        private static unsafe int WriteTwo(char* buffer, int bufferLength, char first, char second)
        {
            if (bufferLength < 2)
            {
                return 0;
            }

            buffer[0] = first;
            buffer[1] = second;
            return 2;
        }

        private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
    }

    /// <summary>
    /// 给 <see cref="AssistantMessageEvent"/> 补 12 个派生类型的 type 判别式
    /// （TS 该联合本就是判别联合，判别串与 TS 的 <c>type</c> 字面量一致）。
    /// </summary>
    /// <remarks>
    /// 判别串不能在 <c>Pi.Ai</c> 的类型上加属性——那里是跨包共用的类型，
    /// 而 type 字面量属于 RPC/print 的 JSON 线路协议，故按
    /// <see cref="AgentMessageJson.AddCodingAgentRoles"/> 的同一手法在解析器上挂。
    /// </remarks>
    private static void AddAssistantMessageEventDiscriminators(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(AssistantMessageEvent))
        {
            return;
        }

        var polymorphism = typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "type",
        };
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.Start), "start"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.TextStart), "text_start"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.TextDelta), "text_delta"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.TextEnd), "text_end"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ThinkingStart), "thinking_start"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ThinkingDelta), "thinking_delta"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ThinkingEnd), "thinking_end"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ToolCallStart), "toolcall_start"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ToolCallDelta), "toolcall_delta"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.ToolCallEnd), "toolcall_end"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.Done), "done"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(AssistantMessageEvent.Error), "error"));
    }
}

/// <summary>
/// LF-only JSONL 行读取器。对应 TS <c>attachJsonlLineReader</c>。
/// </summary>
/// <remarks>
/// <para>
/// TS 明确不用 Node 的 readline（它会按更多 Unicode 分隔符切行，而 JSON 字符串里
/// 这些字符合法）；本类同样只按 <c>\n</c> 切分，并在行尾剥掉一个 <c>\r</c>
/// （容忍 CRLF 输入），流结束时把不满一行的残留作为最后一行发出
/// （TS <c>onEnd</c> 语义）。
/// </para>
/// <para>
/// UTF-8 多字节序列跨读取分片由 <see cref="StreamReader"/> 的内部缓冲处理，
/// 对应 TS 的 <c>StringDecoder</c>。
/// </para>
/// </remarks>
public sealed class JsonlLineReader : IDisposable
{
    private readonly Stream _stream;
    private readonly Action<string> _onLine;
    private readonly Action? _onEnd;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    public JsonlLineReader(Stream stream, Action<string> onLine, Action? onEnd = null)
    {
        _stream = stream;
        _onLine = onLine;
        _onEnd = onEnd;
    }

    /// <summary>开始后台读取；重复调用抛错（TS 的 reader 也只能挂一次）。</summary>
    public void Start()
    {
        if (_pump is not null)
        {
            throw new InvalidOperationException("Reader already started");
        }

        _pump = PumpAsync();
    }

    /// <summary>读取泵是否仍在运行（供测试等待排空；生产代码不需要）。</summary>
    public bool IsRunning => _pump is { IsCompleted: false };

    /// <summary>对应 TS detach：停掉读取循环（不关闭流）。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            // leaveOpen：RPC 关闭时要显式 pause stdin，不能顺带关掉。
            using var reader = new StreamReader(
                _stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var buffer = new char[4096];
            var pending = string.Empty;
            while (true)
            {
                var read = await reader.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                pending += new string(buffer, 0, read);
                while (true)
                {
                    var newline = pending.IndexOf('\n');
                    if (newline < 0)
                    {
                        break;
                    }

                    var line = pending[..newline];
                    pending = pending[(newline + 1)..];
                    EmitLine(line);
                }
            }

            if (pending.Length > 0)
            {
                EmitLine(pending);
            }

            _onEnd?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // detach：静默结束。
        }
    }

    private void EmitLine(string line) => _onLine(line.EndsWith('\r') ? line[..^1] : line);
}
