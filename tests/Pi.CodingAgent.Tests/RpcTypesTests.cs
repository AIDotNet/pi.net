// ============================================================================
// Tests for the RPC protocol type layer (TS `modes/rpc/rpc-types.ts`) and the
// JSONL wire layer (`modes/rpc/jsonl.ts` + `modes/json-event.ts`), landed with
// 4f-5a. Expected shapes are read off the TS sources; the framing cases mirror
// TS `test/rpc-jsonl.test.ts` one-for-one.
// ============================================================================

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Rpc;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class RpcJsonlTests
{
    [Fact]
    public void SerializeLine_WritesStrictJsonl_WithoutEscapingUnicodeSeparators()
    {
        var line = JsonL.SerializeLine(new { text = "a\u2028b\u2029c" });

        // JSON.stringify keeps U+2028/U+2029 raw; JsonStringifyEncoder 对齐该行为。
        Assert.Contains("a\u2028b\u2029c", line, StringComparison.Ordinal);
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        Assert.Equal("{\"text\":\"a\u2028b\u2029c\"}\n", line);
        Assert.Equal("a\u2028b\u2029c", JsonNode.Parse(line.TrimEnd())!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SerializeLine_EscapesExactlyWhatJsonStringifyEscapes()
    {
        // JSON.stringify 只转义 " \ 控制字符（含 \b \t \n \f \r 短式）；
        // < > & ' 与非 ASCII 一律原样。
        var line = JsonL.SerializeLine(new { text = "<>&'\u4e16\u754c\u0007\u001b\b\t\n\f\r\"\\" });

        Assert.Equal(
            "{\"text\":\"<>&'\u4e16\u754c\\u0007\\u001b\\b\\t\\n\\f\\r\\\"\\\\\"}\n",
            line);
    }

    [Fact]
    public void SerializeLine_LoneSurrogateBecomesReplacementChar_AsStjDoes()
    {
        // 差异记录：JSON.stringify 会把孤立代理转义成 \ud83d，而 STJ 的 Utf8JsonWriter
        // 在写 UTF-8 前就把孤立代理替换成 U+FFFD，编码器层管不到。RPC 负载来自合法
        // JSON 输入，不会产生孤立代理，故不影响协议。
        var line = JsonL.SerializeLine(new { text = "a\ud83db" });

        Assert.Equal("{\"text\":\"a\ufffdb\"}\n", line);
    }

    [Fact]
    public void SerializeLine_UsesCamelCaseAndOmitsNulls()
    {
        var line = JsonL.SerializeLine(new RpcSlashCommand
        {
            Name = "n",
            Description = null,
            Source = "extension",
            SourceInfo = new SourceInfo
            {
                Path = "p",
                Source = "local",
                Scope = SourceScope.Project,
                Origin = SourceOrigin.TopLevel,
            },
        });

        // camelCase + WhenWritingNull：description 省略（TS undefined 丢键）。
        Assert.DoesNotContain("description", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Description", line, StringComparison.Ordinal);
        var node = JsonNode.Parse(line.TrimEnd())!;
        Assert.Equal("n", node["name"]!.GetValue<string>());
        Assert.Equal("extension", node["source"]!.GetValue<string>());
        Assert.Equal("p", node["sourceInfo"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reader_SplitsOnLfOnly_AndPreservesUnicodeSeparators()
    {
        var lines = new List<string>();
        using var reader = new JsonlLineReader(
            Stream("{\"text\":\"a\u2028b\u2029c\"}\n"),
            lines.Add);
        reader.Start();
        await DrainAsync(reader);

        Assert.Equal(new[] { "{\"text\":\"a\u2028b\u2029c\"}" }, lines);
    }

    [Fact]
    public async Task Reader_HandlesCrlfDelimitedInput()
    {
        var lines = new List<string>();
        using var reader = new JsonlLineReader(Stream("{\"a\":1}\r\n{\"b\":2}\r\n"), lines.Add);
        reader.Start();
        await DrainAsync(reader);

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, lines);
    }

    [Fact]
    public async Task Reader_EmitsFinalLineWithoutTrailingLf()
    {
        var lines = new List<string>();
        using var reader = new JsonlLineReader(Stream("{\"a\":1}"), lines.Add);
        reader.Start();
        await DrainAsync(reader);

        Assert.Equal(new[] { "{\"a\":1}" }, lines);
    }

    [Fact]
    public async Task Reader_ReassemblesMultiByteUtf8AcrossChunks()
    {
        // 「世」= E4 B8 96，故意按字节切开，验证 StreamReader 的分片重组（TS 的 StringDecoder）。
        var bytes = Encoding.UTF8.GetBytes("{\"t\":\"\u4e16\"}\n{\"t\":\"\u754c\"}\n");
        var lines = new List<string>();
        using var reader = new JsonlLineReader(new CombinedStream(bytes[..9], bytes[9..]), lines.Add);
        reader.Start();
        await DrainAsync(reader);

        Assert.Equal(new[] { "{\"t\":\"\u4e16\"}", "{\"t\":\"\u754c\"}" }, lines);
    }

    private static Stream Stream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    /// <summary>按两段依次吐出的流：先 <paramref name="first"/> 再 <paramref name="second"/>，模拟跨读取分片。</summary>
    private sealed class CombinedStream(byte[] first, byte[] second) : Stream
    {
        private int _segment;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_segment < 2)
            {
                var source = _segment == 0 ? first : second;
                _segment++;
                if (source.Length > 0)
                {
                    var take = Math.Min(count, source.Length);
                    Array.Copy(source, 0, buffer, offset, take);
                    return take;
                }
            }

            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>等到读取泵把流读完（无帧可读时 ReadAsync 返回 0，循环退出）。</summary>
    private static async Task DrainAsync(JsonlLineReader reader)
    {
        for (var i = 0; i < 100 && reader.IsRunning; i++)
        {
            await Task.Delay(10);
        }
    }
}

public sealed class RpcTypesTests
{
    [Fact]
    public void Deserialize_PromptCommand_RoundTripsEveryField()
    {
        var node = JsonNode.Parse("""
        {"id":"b1","type":"prompt","message":"Hello","images":[{"type":"image","data":"abc","mimeType":"image/png"}],"streamingBehavior":"steer"}
        """)!;

        var command = JsonL.TryDeserializeCommand(node);

        var prompt = Assert.IsType<RpcPromptCommand>(command);
        Assert.Equal("b1", prompt.Id);
        Assert.Equal("Hello", prompt.Message);
        Assert.Equal("steer", prompt.StreamingBehavior);
        var image = Assert.Single(prompt.Images!);
        Assert.Equal("abc", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public void Serialize_PromptCommand_OmitsAbsentOptionalFields()
    {
        var line = JsonL.SerializeLine(new RpcPromptCommand { Id = "r1", Message = "hi" });

        // STJ 先写判别式、再写派生类型属性、最后写基类 Id；键序不影响语义。
        Assert.Equal("{\"type\":\"prompt\",\"message\":\"hi\",\"id\":\"r1\"}\n", line);
    }

    [Fact]
    public void Deserialize_AllThirtyThreeCommands_BindTheirFields()
    {
        // 逐条对应 TS RpcCommand 联合；期望值取自 TS 字段名（camelCase）。
        (string Json, Action<RpcCommand> Assertion)[] cases =
        [
            ("""{"type":"prompt","message":"hi"}""", c => Assert.Equal("hi", Assert.IsType<RpcPromptCommand>(c).Message)),
            ("""{"type":"steer","message":"s"}""", c => Assert.Equal("s", Assert.IsType<RpcSteerCommand>(c).Message)),
            ("""{"type":"follow_up","message":"f"}""", c => Assert.Equal("f", Assert.IsType<RpcFollowUpCommand>(c).Message)),
            ("""{"type":"abort"}""", c => Assert.IsType<RpcAbortCommand>(c)),
            ("""{"type":"clear_queue"}""", c => Assert.IsType<RpcClearQueueCommand>(c)),
            ("""{"type":"new_session","parentSession":"/tmp/p.jsonl"}""", c =>
                Assert.Equal("/tmp/p.jsonl", Assert.IsType<RpcNewSessionCommand>(c).ParentSession)),
            ("""{"type":"get_state"}""", c => Assert.IsType<RpcGetStateCommand>(c)),
            ("""{"type":"set_model","provider":"anthropic","modelId":"claude-sonnet-4-5"}""", c =>
            {
                var set = Assert.IsType<RpcSetModelCommand>(c);
                Assert.Equal("anthropic", set.Provider);
                Assert.Equal("claude-sonnet-4-5", set.ModelId);
            }),
            ("""{"type":"cycle_model"}""", c => Assert.IsType<RpcCycleModelCommand>(c)),
            ("""{"type":"get_available_models"}""", c => Assert.IsType<RpcGetAvailableModelsCommand>(c)),
            ("""{"type":"set_thinking_level","level":"high"}""", c =>
                Assert.Equal(ThinkingLevel.High, Assert.IsType<RpcSetThinkingLevelCommand>(c).Level)),
            ("""{"type":"cycle_thinking_level"}""", c => Assert.IsType<RpcCycleThinkingLevelCommand>(c)),
            ("""{"type":"get_available_thinking_levels"}""", c => Assert.IsType<RpcGetAvailableThinkingLevelsCommand>(c)),
            ("""{"type":"set_steering_mode","mode":"one-at-a-time"}""", c =>
                Assert.Equal("one-at-a-time", Assert.IsType<RpcSetSteeringModeCommand>(c).Mode)),
            ("""{"type":"set_follow_up_mode","mode":"all"}""", c =>
                Assert.Equal("all", Assert.IsType<RpcSetFollowUpModeCommand>(c).Mode)),
            ("""{"type":"compact","customInstructions":"keep it short"}""", c =>
                Assert.Equal("keep it short", Assert.IsType<RpcCompactCommand>(c).CustomInstructions)),
            ("""{"type":"set_auto_compaction","enabled":false}""", c =>
                Assert.False(Assert.IsType<RpcSetAutoCompactionCommand>(c).Enabled)),
            ("""{"type":"set_auto_retry","enabled":true}""", c =>
                Assert.True(Assert.IsType<RpcSetAutoRetryCommand>(c).Enabled)),
            ("""{"type":"abort_retry"}""", c => Assert.IsType<RpcAbortRetryCommand>(c)),
            ("""{"type":"bash","command":"echo hi","excludeFromContext":true}""", c =>
            {
                var bash = Assert.IsType<RpcBashCommand>(c);
                Assert.Equal("echo hi", bash.Command);
                Assert.True(bash.ExcludeFromContext);
            }),
            ("""{"type":"abort_bash"}""", c => Assert.IsType<RpcAbortBashCommand>(c)),
            ("""{"type":"get_session_stats"}""", c => Assert.IsType<RpcGetSessionStatsCommand>(c)),
            ("""{"type":"export_html","outputPath":"/tmp/x.html"}""", c =>
                Assert.Equal("/tmp/x.html", Assert.IsType<RpcExportHtmlCommand>(c).OutputPath)),
            ("""{"type":"switch_session","sessionPath":"/tmp/s.jsonl"}""", c =>
                Assert.Equal("/tmp/s.jsonl", Assert.IsType<RpcSwitchSessionCommand>(c).SessionPath)),
            ("""{"type":"fork","entryId":"e1"}""", c =>
                Assert.Equal("e1", Assert.IsType<RpcForkCommand>(c).EntryId)),
            ("""{"type":"clone"}""", c => Assert.IsType<RpcCloneCommand>(c)),
            ("""{"type":"get_fork_messages"}""", c => Assert.IsType<RpcGetForkMessagesCommand>(c)),
            ("""{"type":"get_entries","since":"e0"}""", c =>
                Assert.Equal("e0", Assert.IsType<RpcGetEntriesCommand>(c).Since)),
            ("""{"type":"get_tree"}""", c => Assert.IsType<RpcGetTreeCommand>(c)),
            ("""{"type":"get_last_assistant_text"}""", c => Assert.IsType<RpcGetLastAssistantTextCommand>(c)),
            ("""{"type":"set_session_name","name":"my session"}""", c =>
                Assert.Equal("my session", Assert.IsType<RpcSetSessionNameCommand>(c).Name)),
            ("""{"type":"get_messages"}""", c => Assert.IsType<RpcGetMessagesCommand>(c)),
            ("""{"type":"get_commands"}""", c => Assert.IsType<RpcGetCommandsCommand>(c)),
        ];

        Assert.Equal(33, cases.Length);
        Assert.Equal(33, JsonL.KnownCommandTypes.Count);
        foreach (var (json, assertion) in cases)
        {
            var command = JsonL.TryDeserializeCommand(JsonNode.Parse(json));
            Assert.NotNull(command);
            assertion(command);
        }
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsNull_ForTheUnknownCommandBranch()
    {
        // TS 的 default 分支：`Unknown command: ${command.type}`。
        Assert.Null(JsonL.TryDeserializeCommand(JsonNode.Parse("""{"type":"nope"}""")));
        Assert.Null(JsonL.TryDeserializeCommand(JsonNode.Parse("""{"id":"x"}""")));
        Assert.Null(JsonL.TryDeserializeCommand(JsonNode.Parse("[1,2]")));
        Assert.Null(JsonL.TryDeserializeCommand(JsonNode.Parse("\"hello\"")));
    }

    [Fact]
    public void Deserialize_KnownTypeWithMissingField_Throws()
    {
        // TS 侧同样会炸（handler 里读 undefined），由 handleInputLine 兜成错误响应。
        Assert.Throws<JsonException>(() => JsonL.TryDeserializeCommand(JsonNode.Parse("""{"type":"prompt"}""")));
    }

    [Fact]
    public void Deserialize_ToleratesDiscriminatorNotFirst()
    {
        // STJ 要求 type 判别式是第一个键；协议不保证客户端键序，故实现里会把 type 挪到最前。
        var command = JsonL.TryDeserializeCommand(
            JsonNode.Parse("""{"id":"z9","message":"hi","type":"prompt"}"""));

        var prompt = Assert.IsType<RpcPromptCommand>(command);
        Assert.Equal("z9", prompt.Id);
        Assert.Equal("hi", prompt.Message);
    }

    [Fact]
    public void Responses_Success_OmitsIdAndDataWhenAbsent()
    {
        Assert.Equal(
            """{"type":"response","command":"abort","success":true}""",
            RpcResponses.Success(null, "abort").ToJsonString());
        Assert.Equal(
            """{"id":"r1","type":"response","command":"abort","success":true}""",
            RpcResponses.Success("r1", "abort").ToJsonString());
    }

    [Fact]
    public void Responses_SuccessNull_WritesExplicitDataNull()
    {
        // TS success(id, cmd, null)：data 键存在且为 null（cycle_model 空结果分支）。
        Assert.Equal(
            """{"id":"c1","type":"response","command":"cycle_model","success":true,"data":null}""",
            RpcResponses.SuccessNull("c1", "cycle_model").ToJsonString());
    }

    [Fact]
    public void Responses_Error_CarriesTheMessage()
    {
        Assert.Equal(
            """{"id":"e1","type":"response","command":"get_entries","success":false,"error":"Entry not found: x"}""",
            RpcResponses.Error("e1", "get_entries", "Entry not found: x").ToJsonString());
    }

    [Fact]
    public void ExtensionUiRequest_OmitsFieldsTheMethodDoesNotUse()
    {
        var request = new RpcExtensionUIRequest
        {
            Id = "u1",
            Method = "select",
            Title = "Pick",
            Options = ["a", "b"],
            Timeout = 5000,
        };

        var json = JsonSerializer.Serialize(request, JsonL.Options);
        var node = JsonNode.Parse(json)!;
        Assert.Equal("extension_ui_request", node["type"]!.GetValue<string>());
        Assert.Equal("select", node["method"]!.GetValue<string>());
        Assert.Equal(5000, node["timeout"]!.GetValue<int>());
        Assert.Null(node["message"]);
        Assert.Null(node["notifyType"]);
        Assert.Null(node["statusText"]);
        Assert.Null(node["widgetLines"]);
    }

    [Fact]
    public void ExtensionUiRequest_SetStatus_ClearsTextByOmittingTheKey()
    {
        // TS: `statusText: text | undefined`——undefined 丢键（不清空时键不存在）。
        var request = new RpcExtensionUIRequest
        {
            Id = "u2",
            Method = "setStatus",
            StatusKey = "k",
            StatusText = null,
        };

        Assert.DoesNotContain("statusText", JsonSerializer.Serialize(request, JsonL.Options), StringComparison.Ordinal);
    }
}

public sealed class RpcJsonEventTests
{
    private static AssistantMessage Assistant(string text) => new(
        [new TextContent(text)],
        StopReason.Stop,
        UsageStats: new Usage(Input: 3, Output: 5),
        Model: "stub-chat",
        Api: "stub-api",
        Provider: "stub");

    [Fact]
    public void NonMessageUpdateEvents_PassThrough()
    {
        var @event = new AgentSessionEvent.AgentStartEvent();

        Assert.Same(@event, JsonEvent.ToJsonEvent(@event));
    }

    [Fact]
    public void MessageUpdate_StripsPartialAndKeepsTypeDiscriminator()
    {
        var assistant = Assistant("hello");
        var delta = new AssistantMessageEvent.TextDelta(0, "hel", 3, assistant);

        var json = JsonSerializer.Serialize(JsonEvent.ToJsonEvent(
            new AgentSessionEvent.MessageUpdateEvent(delta, assistant)), JsonL.Options);
        var node = JsonNode.Parse(json)!;

        Assert.Equal("message_update", node["type"]!.GetValue<string>());
        var deltaEvent = node["assistantMessageEvent"]!;
        Assert.Equal("text_delta", deltaEvent["type"]!.GetValue<string>());
        Assert.Equal(0, deltaEvent["contentIndex"]!.GetValue<int>());
        Assert.Equal("hel", deltaEvent["delta"]!.GetValue<string>());
        Assert.Null(deltaEvent["partial"]);
        Assert.Equal(3, node["usage"]!["input"]!.GetValue<long>());
        Assert.Equal(5, node["usage"]!["output"]!.GetValue<long>());
    }

    [Fact]
    public void MessageUpdate_ToolCallStart_AddsIdAndToolName()
    {
        var partial = new AssistantMessage(
            [new ToolCallContent("call-1", "bash", null)],
            StopReason.Stop,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub");
        var delta = new AssistantMessageEvent.ToolCallStart(0, partial);

        var json = JsonSerializer.Serialize(JsonEvent.ToJsonEvent(
            new AgentSessionEvent.MessageUpdateEvent(delta, partial)), JsonL.Options);
        var deltaEvent = JsonNode.Parse(json)!["assistantMessageEvent"]!;

        Assert.Equal("toolcall_start", deltaEvent["type"]!.GetValue<string>());
        Assert.Equal("call-1", deltaEvent["id"]!.GetValue<string>());
        Assert.Equal("bash", deltaEvent["toolName"]!.GetValue<string>());
        Assert.Null(deltaEvent["partial"]);
    }

    [Fact]
    public void MessageUpdate_ToolCallStart_WithNonToolCallContent_ThrowsTheTsMessage()
    {
        var partial = new AssistantMessage(
            [new TextContent("not a tool call")],
            StopReason.Stop,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub");
        var delta = new AssistantMessageEvent.ToolCallStart(0, partial);

        var error = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(
            JsonEvent.ToJsonEvent(new AgentSessionEvent.MessageUpdateEvent(delta, partial)), JsonL.Options));

        Assert.Equal("toolcall_start content at index 0 is not a tool call", error.Message);
    }

    [Fact]
    public void MessageUpdate_DoneEvent_HasNoPartialToStrip()
    {
        var assistant = Assistant("done");
        var done = new AssistantMessageEvent.Done(StopReason.Stop, assistant);

        var deltaEvent = JsonNode.Parse(
            JsonSerializer.Serialize(
                JsonEvent.ToJsonEvent(new AgentSessionEvent.MessageUpdateEvent(done, assistant)),
                JsonL.Options))!["assistantMessageEvent"]!;

        Assert.Equal("done", deltaEvent["type"]!.GetValue<string>());
        Assert.Null(deltaEvent["partial"]);
    }
}
