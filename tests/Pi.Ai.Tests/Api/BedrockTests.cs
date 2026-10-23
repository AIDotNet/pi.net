using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>P32：SigV4 / event-stream 解码 / Bedrock Converse 转换测试。</summary>
public class BedrockTests
{
    private static ModelSpec Model(string id = "anthropic.claude-sonnet-4-20250514-v1:0")
        => new()
        {
            Id = id, Name = "Claude Sonnet 4", Api = "bedrock-converse-stream", Provider = "amazon-bedrock",
            BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
            Reasoning = true, ContextWindow = 200_000, MaxTokens = 8192,
            Cost = new ModelCostRates(3, 15),
        };

    // ---------------------------------------------------------------------------
    // SigV4
    // ---------------------------------------------------------------------------

    [Fact]
    public void SigV4ProducesDeterministicSignedHeaders()
    {
        var now = new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);
        var first = AwsSigV4.Sign("POST",
            "https://bedrock-runtime.us-east-1.amazonaws.com/model/claude/converse-stream",
            null, "{}", "AKID", "secret", null, "us-east-1", now);
        var second = AwsSigV4.Sign("POST",
            "https://bedrock-runtime.us-east-1.amazonaws.com/model/claude/converse-stream",
            null, "{}", "AKID", "secret", null, "us-east-1", now);

        // 同输入同签名；credential scope 与算法前缀形状正确。
        Assert.Equal(first["authorization"], second["authorization"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20261005/us-east-1/bedrock/aws4_request",
            first["authorization"]);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", first["authorization"]);
        Assert.Equal("20261005T030000Z", first["x-amz-date"]);
        Assert.Equal(64, first["x-amz-content-sha256"].Length);
    }

    [Fact]
    public void SigV4IncludesSessionTokenHeader()
    {
        var headers = AwsSigV4.Sign("POST",
            "https://bedrock-runtime.us-east-1.amazonaws.com/model/claude/converse-stream",
            null, "{}", "AKID", "secret", "SESSION", "us-east-1",
            new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal("SESSION", headers["x-amz-security-token"]);
        Assert.Contains("x-amz-security-token", headers["authorization"]);
    }

    // ---------------------------------------------------------------------------
    // event-stream 解码
    // ---------------------------------------------------------------------------

    private static byte[] BuildFrame(string headerName, string headerValue, string payloadJson)
    {
        using var headers = new MemoryStream();
        headers.WriteByte((byte)Encoding.UTF8.GetByteCount(headerName));
        headers.Write(Encoding.UTF8.GetBytes(headerName));
        headers.WriteByte(7); // STRING
        var valueBytes = Encoding.UTF8.GetBytes(headerValue);
        headers.Write(new[] { (byte)(valueBytes.Length >> 24), (byte)(valueBytes.Length >> 16),
            (byte)(valueBytes.Length >> 8), (byte)valueBytes.Length });
        headers.Write(valueBytes);
        var headerBytes = headers.ToArray();

        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var totalLength = 12 + headerBytes.Length + payloadBytes.Length + 4;
        using var frame = new MemoryStream();
        frame.Write(new[] { (byte)(totalLength >> 24), (byte)(totalLength >> 16),
            (byte)(totalLength >> 8), (byte)totalLength });
        frame.Write(new[] { (byte)(headerBytes.Length >> 24), (byte)(headerBytes.Length >> 16),
            (byte)(headerBytes.Length >> 8), (byte)headerBytes.Length });
        frame.Write(new byte[4]); // prelude CRC（解码器当前不校验）
        frame.Write(headerBytes);
        frame.Write(payloadBytes);
        frame.Write(new byte[4]); // message CRC
        return frame.ToArray();
    }

    [Fact]
    public void EventStreamDecoderExtractsHeadersAndPayload()
    {
        using var stream = new MemoryStream();
        stream.Write(BuildFrame(":event-type", "contentBlockDelta",
            """{"contentBlockDelta":{"delta":{"text":"hi"}}}"""));
        stream.Write(BuildFrame(":event-type", "messageStop",
            """{"messageStop":{"stopReason":"end_turn"}}"""));
        stream.Position = 0;

        var messages = AwsEventStream.Decode(stream);
        Assert.Equal(2, messages.Count);
        Assert.Equal("contentBlockDelta", messages[0].Headers[":event-type"]);
        Assert.Equal("hi", messages[0].Payload?["contentBlockDelta"]?["delta"]?["text"]?.GetValue<string>());
        Assert.Equal("end_turn", messages[1].Payload?["messageStop"]?["stopReason"]?.GetValue<string>());
    }

    [Fact]
    public void EventStreamDecoderRejectsTruncatedFrame()
    {
        var frame = BuildFrame(":event-type", "messageStart", "{}");
        using var stream = new MemoryStream(frame, 0, frame.Length - 3);
        Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(stream));
    }

    // ---------------------------------------------------------------------------
    // 消息/工具转换
    // ---------------------------------------------------------------------------

    [Fact]
    public void ConvertMessagesAddsCachePointsAndMergesToolResults()
    {
        var model = Model();
        var context = new TranscriptContext(
        [
            new SystemMessage("be brief", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1),
            new AssistantMessage(
                [new ToolCallContent("call-1", "search", new JsonObject { ["q"] = "x" })],
                StopReason.ToolUse, Provider: "amazon-bedrock", Model: model.Id),
            new ToolResultMessage("call-1", "search", [new TextContent("result")], Timestamp: 3),
            new ToolResultMessage("call-2", "read", [new TextContent("file")], Timestamp: 4),
        ]);
        var messages = BedrockConverseStream.ConvertMessages(context, model, "short", null);

        // user / assistant(toolUse) / user(两条 toolResult 合并 + 缓存点)。
        Assert.Equal(3, messages.Count);
        var toolResultTurn = (JsonObject)messages[2]!;
        Assert.Equal("user", toolResultTurn.Str("role"));
        var toolResults = (JsonArray)toolResultTurn["content"]!;
        Assert.Equal(3, toolResults.Count); // 2 条 toolResult + 1 个缓存点
        Assert.Equal("call-1", ((JsonObject)toolResults[0]!).Obj("toolResult")!.Str("toolUseId"));
        Assert.Equal("success", ((JsonObject)toolResults[0]!).Obj("toolResult")!.Str("status"));
        Assert.Equal("call-2", ((JsonObject)toolResults[1]!).Obj("toolResult")!.Str("toolUseId"));
        // 最后一条 user 消息附加缓存点（Claude 模型）。
        var lastContent = (JsonArray)((JsonObject)messages[^1]!)["content"]!;
        var cachePoint = (JsonObject)lastContent[^1]!;
        Assert.Equal("default", cachePoint.Obj("cachePoint")?.Str("type"));
    }

    [Fact]
    public void ConvertMessagesMovesErrorToolResultImagesAfterTheToolResultsForClaude()
    {
        // 回归 #10794：Bedrock 上的 Claude 同样拒收「错误工具结果里带图片」。
        var model = Model() with { Input = [ModelInput.Text, ModelInput.Image] };
        var context = new TranscriptContext(
        [
            new AssistantMessage(
                [
                    new ToolCallContent("call-1", "codemode", new JsonObject()),
                    new ToolCallContent("call-2", "read", new JsonObject()),
                ],
                StopReason.ToolUse, Provider: "amazon-bedrock", Model: model.Id),
            new ToolResultMessage(
                "call-1",
                "codemode",
                [
                    new TextContent("Script failed"),
                    new ImageContent("AAAA", "image/png"),
                    new TextContent("Script error"),
                ],
                IsError: true, Timestamp: 3),
            new ToolResultMessage(
                "call-2", "read", [new ImageContent("BBBB", "image/png")], Timestamp: 4),
        ]);
        var messages = BedrockConverseStream.ConvertMessages(context, model, "none", null);

        // assistant(toolUse) / user(两条 toolResult + 说明 + 图片)。
        Assert.Equal(2, messages.Count);
        var content = (JsonArray)((JsonObject)messages[1]!)["content"]!;
        Assert.Equal(4, content.Count);

        var errorResult = ((JsonObject)content[0]!).Obj("toolResult")!;
        Assert.Equal("error", errorResult.Str("status"));
        var errorContent = (JsonArray)errorResult["content"]!;
        Assert.Equal(3, errorContent.Count);
        Assert.Equal("Script failed", ((JsonObject)errorContent[0]!).Str("text"));
        Assert.Equal("(image attached after the tool results)", ((JsonObject)errorContent[1]!).Str("text"));
        Assert.Equal("Script error", ((JsonObject)errorContent[2]!).Str("text"));

        // 成功的工具结果图片留在原位。
        var okResult = ((JsonObject)content[1]!).Obj("toolResult")!;
        Assert.Equal("success", okResult.Str("status"));
        var okContent = (JsonArray)okResult["content"]!;
        Assert.Single(okContent);
        Assert.NotNull(((JsonObject)okContent[0]!).Obj("image"));

        Assert.Equal(
            "Images from the error result of tool call call-1 (codemode):",
            ((JsonObject)content[2]!).Str("text"));
        Assert.NotNull(((JsonObject)content[3]!).Obj("image"));
    }

    [Fact]
    public void ConvertMessagesKeepsErrorToolResultImagesInPlaceForNonClaude()
    {
        // 非 Claude 模型：Model(id) 会把 Name 固定成 Claude，所以这里直接给出完整 spec。
        var gpt = new ModelSpec
        {
            Id = "openai.gpt-6.1-sol-v1:0", Name = "GPT-6.1 Sol", Api = "bedrock-converse-stream",
            Provider = "amazon-bedrock", BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
            Reasoning = true, ContextWindow = 200_000, MaxTokens = 8192, Cost = new ModelCostRates(3, 15),
            Input = [ModelInput.Text, ModelInput.Image],
        };
        var context = new TranscriptContext(
        [
            new AssistantMessage(
                [new ToolCallContent("call-1", "codemode", new JsonObject())],
                StopReason.ToolUse, Provider: "amazon-bedrock", Model: gpt.Id),
            new ToolResultMessage(
                "call-1",
                "codemode",
                [new TextContent("Script failed"), new ImageContent("AAAA", "image/png")],
                IsError: true, Timestamp: 3),
        ]);
        var messages = BedrockConverseStream.ConvertMessages(context, gpt, "none", null);

        var content = (JsonArray)((JsonObject)messages[1]!)["content"]!;
        Assert.Single(content);
        var errorContent = (JsonArray)((JsonObject)content[0]!).Obj("toolResult")!["content"]!;
        Assert.Equal(2, errorContent.Count);
        Assert.Equal("Script failed", ((JsonObject)errorContent[0]!).Str("text"));
        Assert.NotNull(((JsonObject)errorContent[1]!).Obj("image"));
    }

    [Fact]
    public void ConvertMessagesReplaysRedactedReasoning()
    {
        var model = Model();
        var context = new TranscriptContext(
        [
            new UserMessage([new TextContent("hi")], 1),
            new AssistantMessage(
            [
                new ThinkingContent("[Reasoning redacted]")
                {
                    Redacted = true,
                    Signature = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                },
                new TextContent("answer"),
            ], StopReason.Stop, Api: "bedrock-converse-stream",
                Provider: "amazon-bedrock", Model: model.Id),
        ]);
        var messages = BedrockConverseStream.ConvertMessages(context, model, "short", null);
        var assistantTurn = (JsonObject)messages[1]!;
        var parts = (JsonArray)assistantTurn["content"]!;
        // 加密 reasoning 以 redactedContent（base64 字符串）原样重放。
        Assert.Equal("reasoningContent", ((JsonObject)parts[0]!).First().Key);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            ((JsonObject)parts[0]!).Obj("reasoningContent")!.Str("redactedContent"));
    }

    [Fact]
    public void ConvertMessagesDropsSignatureForNonClaude()
    {
        var gpt = new ModelSpec
        {
            Id = "openai.gpt-5.6", Name = "GPT", Api = "bedrock-converse-stream", Provider = "amazon-bedrock",
            BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", Reasoning = true,
        };
        Assert.False(BedrockConverseStream.IsAnthropicClaudeModel(gpt));
        var context = new TranscriptContext(
        [
            new UserMessage([new TextContent("hi")], 1),
            new AssistantMessage(
            [
                new ThinkingContent("thought") { Signature = "sig" },
                new TextContent("answer"),
            ], StopReason.Stop, Api: "bedrock-converse-stream",
                Provider: "amazon-bedrock", Model: gpt.Id),
        ]);
        var messages = BedrockConverseStream.ConvertMessages(context, gpt, "short", null);
        var assistantTurn = (JsonObject)messages[1]!;
        var parts = (JsonArray)assistantTurn["content"]!;
        var reasoning = (JsonObject)((JsonObject)parts[0]!).Obj("reasoningContent")!.Obj("reasoningText")!;
        // 非 Claude：签名省略（字段会报错）。
        Assert.Null(reasoning.Str("signature"));
        Assert.Equal("thought", reasoning.Str("text"));
    }

    [Fact]
    public void BuildAdditionalModelRequestFieldsAdaptiveVsBudget()
    {
        var adaptive = Model("us.anthropic.claude-opus-4-6-20260301-v1:0");
        adaptive = adaptive with { Name = "Claude Opus 4.6" };
        var fields = BedrockConverseStream.BuildAdditionalModelRequestFields(adaptive,
            new BedrockOptions { Reasoning = "medium" })!;
        Assert.Equal("adaptive", fields!.Obj("thinking")!.Str("type"));
        Assert.Equal("medium", fields!.Obj("output_config")!.Str("effort"));
        // 非 adaptive Claude：预算型 + interleaved beta。
        var budgetModel = Model();
        var budgetFields = BedrockConverseStream.BuildAdditionalModelRequestFields(budgetModel,
            new BedrockOptions { Reasoning = "medium" })!;
        Assert.Equal("enabled", budgetFields!.Obj("thinking")!.Str("type"));
        Assert.Equal(8192, budgetFields.Obj("thinking")!.Num("budget_tokens"));
        Assert.Equal("interleaved-thinking-2025-05-14", ((JsonArray)budgetFields["anthropic_beta"]!)[0]?.GetValue<string>());
        // 既非 Claude 也非 OpenAI GPT 的模型无扩展字段。
        // 注：`openai.gpt-*` 在 R2 漂移后已改走 reasoning.effort 分支，不能再当「无扩展字段」的样本。
        Assert.Null(BedrockConverseStream.BuildAdditionalModelRequestFields(
            new ModelSpec
            {
                Id = "meta.llama3-70b-instruct-v1:0", Name = "Llama 3 70B", Api = "bedrock-converse-stream",
                Provider = "amazon-bedrock", BaseUrl = "", Reasoning = true,
            }, new BedrockOptions { Reasoning = "medium" }));
    }

    /// <summary>
    /// 上游 <c>2989eb581</c>（R2 漂移，#9331）：配置的思考档位此前从未传给 Bedrock 上的 OpenAI 模型。
    /// </summary>
    [Fact]
    public void BuildAdditionalModelRequestFieldsOpenAiReasoning()
    {
        static ModelSpec Gpt(string id, string name = "GPT-6 Sol", ThinkingLevelMap? map = null)
            => new()
            {
                Id = id, Name = name, Api = "bedrock-converse-stream", Provider = "amazon-bedrock",
                BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", Reasoning = true,
                ThinkingLevelMap = map,
            };

        // GPT-6 / GPT-5.x：嵌套 reasoning.effort，minimal 收敛到 low。
        (string Level, string Effort)[] gptCases =
        [
            ("minimal", "low"), ("low", "low"), ("medium", "medium"),
            ("high", "high"), ("xhigh", "xhigh"), ("max", "max"),
        ];
        foreach (var (level, effort) in gptCases)
        {
            foreach (var id in (string[])["global.openai.gpt-6-sol", "us.openai.gpt-6-luna", "global.openai.gpt-5.6-sol"])
            {
                var fields = BedrockConverseStream.BuildAdditionalModelRequestFields(Gpt(id),
                    new BedrockOptions { Reasoning = level })!;
                Assert.Equal(effort, fields.Obj("reasoning")!.Str("effort"));
                // GPT 分支不得混入 Claude 的字段。
                Assert.Null(fields.Obj("thinking"));
                Assert.Null(fields.Obj("output_config"));
            }
        }

        // 仅凭 name 识别 GPT 模型（id 是 ARN 时）。
        var arn = BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("arn:aws:bedrock:us-east-1:123456789012:application-inference-profile/my-profile"),
            new BedrockOptions { Reasoning = "medium" })!;
        Assert.Equal("medium", arn.Obj("reasoning")!.Str("effort"));

        // thinkingLevelMap 的字符串映射优先于内置档位表。
        var mapped = BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("global.openai.gpt-6-sol", map: ThinkingLevelMap.FromJsonObject(
                new JsonObject { ["medium"] = "high" })),
            new BedrockOptions { Reasoning = "medium" })!;
        Assert.Equal("high", mapped.Obj("reasoning")!.Str("effort"));

        // gpt-oss：扁平 reasoning_effort，xhigh/max 夹到 high；且必须先于 "gpt-" 命中。
        var oss = BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("openai.gpt-oss-120b-1:0", "gpt-oss 120B"),
            new BedrockOptions { Reasoning = "minimal" })!;
        Assert.Equal("low", oss.Str("reasoning_effort"));
        Assert.Null(oss.Obj("reasoning"));

        Assert.Equal("medium", BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("openai.gpt-oss-120b-1:0", "gpt-oss 120B"),
            new BedrockOptions { Reasoning = "medium" })!.Str("reasoning_effort"));
        Assert.Equal("high", BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("openai.gpt-oss-120b-1:0", "gpt-oss 120B"),
            new BedrockOptions { Reasoning = "xhigh" })!.Str("reasoning_effort"));

        // reasoning 未开 → 无扩展字段（与 Claude 分支同一前置判定）。
        Assert.Null(BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("global.openai.gpt-6-sol"), new BedrockOptions()));
        // 模型不支持推理 → 同样无扩展字段。
        Assert.Null(BedrockConverseStream.BuildAdditionalModelRequestFields(
            Gpt("global.openai.gpt-6-sol") with { Reasoning = false },
            new BedrockOptions { Reasoning = "medium" }));
    }

    /// <summary>
    /// TS 用 <c>undefined</c> 表示「不下发该字段」，<c>JSON.stringify</c> 会把键整个丢掉；
    /// C# 的 <c>JsonObject</c> 会把 null 序列化成 <c>"k":null</c>。载荷组装必须逐键省略。
    /// </summary>
    [Fact]
    public void BuildCommandInputOmitsUnsetFieldsInsteadOfEmittingNull()
    {
        var model = Model("meta.llama3-70b-instruct-v1:0") with { Name = "Llama 3 70B", Reasoning = false };
        var context = new TranscriptContext([new UserMessage([new TextContent("hi")], 1)]);

        var bare = BedrockConverseStream.BuildCommandInput(
            model, context, new BedrockOptions(), "short",
            isClaude: false, inferenceMaxTokens: null, initialSystemPrompt: null);

        var json = bare.ToJsonString();
        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("\"system\"", json);
        Assert.DoesNotContain("\"toolConfig\"", json);
        Assert.DoesNotContain("\"additionalModelRequestFields\"", json);
        Assert.DoesNotContain("\"maxTokens\"", json);
        Assert.DoesNotContain("\"temperature\"", json);
        // inferenceConfig 本身恒在（TS 里也是空对象）。
        Assert.Equal("{}", bare.Obj("inferenceConfig")!.ToJsonString());

        // 设置了就照常出现。
        var full = BedrockConverseStream.BuildCommandInput(
            model, context, new BedrockOptions { Temperature = 0.5 }, "short",
            isClaude: false, inferenceMaxTokens: 128, initialSystemPrompt: "be brief");
        Assert.Equal(0.5, full.Obj("inferenceConfig")!.Num("temperature"));
        Assert.Equal(128, full.Obj("inferenceConfig")!.Num("maxTokens"));
        Assert.NotNull(full["system"]);
    }

    [Fact]
    public void RegionAndEndpointResolution()
    {
        var options = new BedrockOptions { Env = new Dictionary<string, string> { ["AWS_REGION"] = "eu-west-1" } };
        Assert.Equal("eu-west-1", BedrockConverseStream.GetConfiguredBedrockRegion(options));
        Assert.Equal("us-gov-west-1", BedrockConverseStream.GetStandardBedrockEndpointRegion(
            "https://bedrock-runtime.us-gov-west-1.amazonaws.com"));
        Assert.Null(BedrockConverseStream.GetStandardBedrockEndpointRegion("https://custom.example.com"));
        // 标准端点 + 无 region/profile → 显式端点；有 ambient profile → 不钉端点。
        Assert.True(BedrockConverseStream.ShouldUseExplicitBedrockEndpoint(
            "https://bedrock-runtime.us-east-1.amazonaws.com", null, hasAmbientConfiguredProfile: false));
        Assert.False(BedrockConverseStream.ShouldUseExplicitBedrockEndpoint(
            "https://bedrock-runtime.us-east-1.amazonaws.com", "us-east-1", hasAmbientConfiguredProfile: true));
        // 自定义端点恒显式。
        Assert.True(BedrockConverseStream.ShouldUseExplicitBedrockEndpoint(
            "https://custom.example.com", "us-east-1", hasAmbientConfiguredProfile: true));
    }

    [Fact]
    public void MapStopReasonCoversBedrockVerbs()
    {
        Assert.Equal(StopReason.Stop, BedrockConverseStream.MapStopReason("end_turn").StopReason);
        Assert.Equal(StopReason.Stop, BedrockConverseStream.MapStopReason("stop_sequence").StopReason);
        Assert.Equal(StopReason.Length, BedrockConverseStream.MapStopReason("max_tokens").StopReason);
        Assert.Equal(StopReason.Length, BedrockConverseStream.MapStopReason("model_context_window_exceeded").StopReason);
        Assert.Equal(StopReason.ToolUse, BedrockConverseStream.MapStopReason("tool_use").StopReason);
        Assert.Equal(StopReason.Error, BedrockConverseStream.MapStopReason("unknown").StopReason);
        Assert.Equal("Provider stopped with: unknown", BedrockConverseStream.MapStopReason("unknown").ErrorMessage);
    }

    [Fact]
    public void FormatBedrockErrorAddsDataRetentionHint()
    {
        var message = BedrockConverseStream.FormatBedrockError(
            new InvalidOperationException("data retention mode 'default' is not available for this model"));
        Assert.Contains("data-retention.html", message);
        var plain = BedrockConverseStream.FormatBedrockError(
            new InvalidOperationException("boom"));
        Assert.DoesNotContain("data-retention.html", plain);
    }
}
