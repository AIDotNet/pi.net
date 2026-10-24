using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 工具结果里的图片：各 API 都要把「tool 消息只带文本、图片另行附着」这条规则落对。
/// 这是多模态与工具调用的交叉点——纯 wire 层逻辑，端到端测试才能发现回归。
/// </summary>
/// <remarks>
/// 四个 API 的形态不同：openai-completions 把图片提成一条 user 消息；
/// Google 塞进 <c>functionResponse.parts</c>（仅 Gemini 3+）；responses 用
/// <c>input_image</c> 数组；Mistral 只在文本里留占位说明。三态（有文本 / 只有图 / 模型不吃图）
/// 必须各自正确，否则模型看到的是「空工具结果」或「不存在的图片」。
/// </remarks>
public class ToolResultImageTests
{
    private const string ImageData = "QUJD";

    private static ModelSpec Model(bool vision = true, string id = "m")
        => new()
        {
            Id = id,
            Name = "M",
            Api = "openai-completions",
            Provider = "test",
            BaseUrl = "https://test",
            Input = vision ? [ModelInput.Text, ModelInput.Image] : [ModelInput.Text],
        };

    /// <summary>一轮「用户提问 → 模型调 read → 工具回图片」的完整对话。</summary>
    private static IReadOnlyList<ChatMessage> Conversation(bool withText = true)
    {
        var content = new List<ContentBlock>();
        if (withText) content.Add(new TextContent("screenshot taken"));
        content.Add(new ImageContent(ImageData, "image/png"));
        return
        [
            Messages.UserText("what is in this file"),
            new AssistantMessage(
                [new ToolCallContent("call-1", "read", new JsonObject { ["path"] = "a.png" })],
                StopReason.ToolUse,
                Timestamp: 0),
            new ToolResultMessage("call-1", "read", content, Timestamp: 0),
        ];
    }

    // ─── openai-completions ────────────────────────────────────────────────

    private static JsonArray ConvertOpenAi(
        IReadOnlyList<ChatMessage> messages, bool vision = true, bool assistantAfterToolResult = false)
        => OpenAiCompletions.ConvertMessages(
            Model(vision),
            new TranscriptContext(messages),
            new OpenAiCompletionsCompat { RequiresAssistantAfterToolResult = assistantAfterToolResult });

    [Fact]
    public void OpenAiCompletionsKeepsToolTextAndAttachesImagesAsAUserMessage()
    {
        var wire = ConvertOpenAi(Conversation());

        var tool = Assert.Single(wire.OfType<JsonObject>(), entry => entry.Str("role") == "tool");
        Assert.Equal("screenshot taken", tool.Str("content"));
        Assert.Equal("call-1", tool.Str("tool_call_id"));

        var attachment = Assert.Single(wire.OfType<JsonObject>(), entry => entry.Str("role") == "user"
            && entry["content"] is JsonArray array && array.Count > 1);
        var content = attachment["content"]!.AsArray();
        Assert.Equal("Attached image(s) from tool result:", content[0]!.AsObject().Str("text"));
        Assert.Equal("image_url", content[1]!.AsObject().Str("type"));
        Assert.Equal(
            $"data:image/png;base64,{ImageData}",
            content[1]!.AsObject().Obj("image_url")!.Str("url"));
    }

    [Fact]
    public void OpenAiCompletionsUsesTheImagePlaceholderWhenTheResultHasOnlyImages()
    {
        var wire = ConvertOpenAi(Conversation(withText: false));

        var tool = Assert.Single(wire.OfType<JsonObject>(), entry => entry.Str("role") == "tool");
        Assert.Equal("(see attached image)", tool.Str("content"));
    }

    [Fact]
    public void OpenAiCompletionsDropsImagesForModelsWithoutVision()
    {
        var wire = ConvertOpenAi(Conversation(), vision: false);

        var tool = Assert.Single(wire.OfType<JsonObject>(), entry => entry.Str("role") == "tool");
        // 共用变换层（TransformMessages）会把被丢弃的图片写成一句提示，模型才知道有图看不见。
        Assert.Contains("screenshot taken", tool.Str("content"));
        Assert.Contains("(tool image omitted: model does not support images)", tool.Str("content"));
        // 没有附图消息，也没有 image_url 残留。
        Assert.DoesNotContain(wire.OfType<JsonObject>(), entry => entry.Str("role") == "user"
            && entry["content"] is JsonArray array && array.Count > 1);
        Assert.DoesNotContain("image_url", wire.ToJsonString());
    }

    [Fact]
    public void OpenAiCompletionsInsertsAnAssistantTurnBeforeAttachmentsWhenRequired()
    {
        var wire = ConvertOpenAi(Conversation(), assistantAfterToolResult: true);

        var roles = wire.OfType<JsonObject>().Select(entry => entry.Str("role")).ToList();
        var attachmentIndex = roles.FindLastIndex(role => role == "user");
        Assert.True(attachmentIndex > 0);
        Assert.Equal("assistant", roles[attachmentIndex - 1]);
        Assert.Equal(
            "I have processed the tool results.",
            wire[attachmentIndex - 1]!.AsObject().Str("content"));
    }

    // ─── Google ────────────────────────────────────────────────────────────

    [Fact]
    public void GooglePutsToolImagesIntoTheFunctionResponseParts()
    {
        var (_, contents) = GoogleShared.ConvertMessages(Model(id: "gemini-3-pro"), new TranscriptContext(Conversation()));

        var part = contents
            .OfType<JsonObject>()
            .Select(entry => entry["parts"] as JsonArray)
            .Where(parts => parts is not null)
            .SelectMany(parts => parts!.OfType<JsonObject>())
            .Single(candidate => candidate.Has("functionResponse"));
        var response = part.Obj("functionResponse")!;
        Assert.Equal("screenshot taken", response.Obj("response")!.Str("output"));

        var inline = response["parts"]!.AsArray().Single()!.AsObject().Obj("inlineData")!;
        Assert.Equal("image/png", inline.Str("mimeType"));
        Assert.Equal(ImageData, inline.Str("data"));
    }

    [Fact]
    public void GoogleUsesThePlaceholderForImageOnlyResults()
    {
        var (_, contents) = GoogleShared.ConvertMessages(
            Model(id: "gemini-3-pro"), new TranscriptContext(Conversation(withText: false)));

        var response = contents
            .OfType<JsonObject>()
            .SelectMany(entry => (entry["parts"] as JsonArray ?? []).OfType<JsonObject>())
            .Single(candidate => candidate.Has("functionResponse"))
            .Obj("functionResponse")!;
        Assert.Equal("(see attached image)", response.Obj("response")!.Str("output"));
    }

    /// <summary>Gemini 2.x 不接受 functionResponse 里的图片：只留占位文本，靠诊断提示模型。</summary>
    [Fact]
    public void GoogleKeepsImagesOutOfFunctionResponsesForOlderModels()
    {
        var (_, contents) = GoogleShared.ConvertMessages(Model(id: "gemini-2.5-pro"), new TranscriptContext(Conversation()));

        var response = contents
            .OfType<JsonObject>()
            .SelectMany(entry => (entry["parts"] as JsonArray ?? []).OfType<JsonObject>())
            .Single(candidate => candidate.Has("functionResponse"))
            .Obj("functionResponse")!;
        Assert.Null(response["parts"]);
        Assert.Equal("screenshot taken", response.Obj("response")!.Str("output"));
    }

    // ─── openai-responses ─────────────────────────────────────────────────

    [Fact]
    public void ResponsesReturnsInputImageParts()
    {
        var toolResult = Assert.IsType<ToolResultMessage>(Conversation()[2]);
        var output = OpenAiResponsesShared.ConvertToolResultOutput(Model(), toolResult.Content).AsArray();

        Assert.Equal(2, output.Count);
        Assert.Equal("input_text", output[0]!.AsObject().Str("type"));
        Assert.Equal("screenshot taken", output[0]!.AsObject().Str("text"));
        Assert.Equal("input_image", output[1]!.AsObject().Str("type"));
        Assert.Equal($"data:image/png;base64,{ImageData}", output[1]!.AsObject().Str("image_url"));
    }

    /// <summary>只有图片、没有文本时：给模型的就只是那张图（不再补 input_text）。</summary>
    [Fact]
    public void ResponsesSendsImageOnlyResultsWithoutAPlaceholder()
    {
        var output = OpenAiResponsesShared.ConvertToolResultOutput(
            Model(), new ToolResultMessage("call-1", "read", [new ImageContent(ImageData, "image/png")]).Content)
            .AsArray();

        var image = Assert.Single(output);
        Assert.Equal("input_image", image!.AsObject().Str("type"));
    }

    /// <summary>模型不吃图时退回文本占位，而不是发一个空的 output 数组。</summary>
    [Fact]
    public void ResponsesFallsBackToAPlaceholderWhenTheModelHasNoVision()
    {
        var output = OpenAiResponsesShared.ConvertToolResultOutput(
            Model(vision: false),
            new ToolResultMessage("call-1", "read", [new ImageContent(ImageData, "image/png")]).Content);

        Assert.Equal("(see attached image)", output.GetValue<string>());
    }

    [Fact]
    public void ResponsesDropsImagesForModelsWithoutVision()
    {
        var toolResult = Assert.IsType<ToolResultMessage>(Conversation()[2]);
        var output = OpenAiResponsesShared.ConvertToolResultOutput(Model(vision: false), toolResult.Content);

        Assert.Equal("screenshot taken", output.GetValue<string>());
    }

    // ─── Mistral ──────────────────────────────────────────────────────────

    /// <summary>Mistral 只发文本：图片要么留占位，要么明确告诉模型被省略了。</summary>
    [Theory]
    [InlineData("done", true, true, false, "done")]
    [InlineData("done", true, false, false, "done\n[tool image omitted: model does not support images]")]
    [InlineData("", true, true, false, "(see attached image)")]
    [InlineData("", true, false, false, "(image omitted: model does not support images)")]
    [InlineData("", false, true, false, "(no tool output)")]
    [InlineData("", true, true, true, "[tool error] (see attached image)")]
    public void MistralToolResultTextCoversEveryCombination(
        string text, bool hasImages, bool supportsImages, bool isError, string expected)
    {
        Assert.Equal(expected, MistralConversations.BuildToolResultText(text, hasImages, supportsImages, isError));
    }
}
