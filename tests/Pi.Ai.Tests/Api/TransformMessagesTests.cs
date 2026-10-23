using Pi.Ai.Auth.OAuth;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>跨 provider 消息变换测试。对应 TS transform-messages 系列测试的核心语义。</summary>
public class TransformMessagesTests
{
    private static ModelSpec VisionModel() => new()
    {
        Id = "vision-1", Name = "Vision", Api = "openai-completions", Provider = "openai",
        BaseUrl = "https://api.openai.com/v1", Input = ["text", "image"],
    };

    private static ModelSpec TextOnlyModel() => new()
    {
        Id = "text-1", Name = "Text", Api = "openai-completions", Provider = "openai",
        BaseUrl = "https://api.openai.com/v1", Input = ["text"],
    };

    private static ModelSpec OtherProviderModel() => new()
    {
        Id = "claude-3", Name = "Claude", Api = "anthropic-messages", Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com", Input = ["text", "image"],
    };

    private static long Ts => 1_000;

    [Fact]
    public void DowngradesImagesForNonVisionModels()
    {
        var messages = new List<ChatMessage>
        {
            new UserMessage(
                [new TextContent("look"), new ImageContent("imgdata", "image/png"), new ImageContent("img2", "image/png")],
                Ts),
            new ToolResultMessage("call-1", "shot",
                [new ImageContent("toolimg", "image/png"), new TextContent("after")],
                IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, TextOnlyModel());

        var user = Assert.IsType<UserMessage>(result[0]);
        var userBlocks = user.Content.ToBlocks().OfType<TextContent>().ToList();
        Assert.Equal(["look", "(image omitted: model does not support images)"],
            userBlocks.Select(t => t.Text).ToArray()); // 连续图片折叠成一个占位

        var toolResult = Assert.IsType<ToolResultMessage>(result[1]);
        Assert.Equal("(tool image omitted: model does not support images)",
            toolResult.Content.OfType<TextContent>().First().Text);
        Assert.Equal("after", toolResult.Content.OfType<TextContent>().Last().Text);
    }

    [Fact]
    public void KeepsImagesForVisionModels()
    {
        var messages = new List<ChatMessage>
        {
            new UserMessage([new ImageContent("imgdata", "image/png")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());
        Assert.IsType<ImageContent>(Assert.IsType<UserMessage>(result[0]).Content.ToBlocks()[0]);
    }

    [Fact]
    public void CrossModelDropsThinkingAndStripsSignatures()
    {
        var assistant = new AssistantMessage(
            [
                new ThinkingContent("secret reasoning", Signature: "sig-1"),
                new ThinkingContent("", Signature: "enc"),
                new ThinkingContent("plain thought"),
                new TextContent("answer"),
                new ToolCallContent("call|x|y", "read", null) { ThoughtSignature = "ts" },
            ],
            StopReason.ToolUse, Model: "claude-3", Api: "anthropic-messages", Provider: "anthropic",
            Timestamp: Ts);

        var result = TransformMessages.Transform([assistant], TextOnlyModel());

        var transformed = Assert.IsType<AssistantMessage>(result[0]);
        // 跨模型：思维块一律丢弃——签名与加密推理只对产出它的模型有效，可读思维当纯文本发过去
        // 会让新模型把它当成自身回复的一部分（上游 #10995）。文本与工具调用保留。
        Assert.Empty(transformed.Content.OfType<ThinkingContent>());
        Assert.Equal(["answer"], transformed.Content.OfType<TextContent>().Select(t => t.Text).ToArray());
        var toolCall = transformed.Content.OfType<ToolCallContent>().Single();
        Assert.Null(toolCall.ThoughtSignature);
        Assert.Equal(2, transformed.Content.Count); // text(answer) + toolCall
    }

    [Fact]
    public void SameModelKeepsSignedAndEncryptedThinking()
    {
        var assistant = new AssistantMessage(
            [
                new ThinkingContent("secret reasoning", Signature: "sig-1"),
                new ThinkingContent("", Signature: "enc"),
                new ThinkingContent("plain thought"),
                new TextContent("answer"),
            ],
            StopReason.Stop, Model: "vision-1", Api: "openai-completions", Provider: "openai",
            Timestamp: Ts);

        var transformed = Assert.IsType<AssistantMessage>(
            TransformMessages.Transform([assistant], VisionModel())[0]);

        // 同模型：带签名的块原样保留（回放需要，含思考文本为空的那种），
        // 无签名但有文本的也保留，空且无签名的丢弃。
        Assert.Equal(
            ["secret reasoning", "", "plain thought"],
            transformed.Content.OfType<ThinkingContent>().Select(t => t.Thinking).ToArray());
        Assert.Equal(
            new string?[] { "sig-1", "enc", null },
            transformed.Content.OfType<ThinkingContent>().Select(t => t.Signature).ToArray());
    }

    [Fact]
    public void KeepsThinkingOnlyFromTheTargetModelWhenModelsAlternate()
    {
        AssistantMessage Turn(string model, string reasoning, string answer) => new(
            [new ThinkingContent(reasoning, Signature: "reasoning_content"), new TextContent(answer)],
            StopReason.Stop, Model: model, Api: "openai-completions", Provider: "openai", Timestamp: Ts);

        var result = TransformMessages.Transform(
            [
                new UserMessage([new TextContent("1")], Ts),
                Turn("vision-1", "self 1", "a"),
                new UserMessage([new TextContent("2")], Ts),
                Turn("other-1", "other 1", "b"),
                new UserMessage([new TextContent("3")], Ts),
                Turn("vision-1", "self 2", "c"),
            ],
            VisionModel());

        var contents = result.OfType<AssistantMessage>().Select(m => m.Content).ToList();
        Assert.Equal(
            ["self 1", "self 2"],
            new[] { contents[0], contents[2] }
                .SelectMany(c => c.OfType<ThinkingContent>())
                .Select(t => t.Thinking)
                .ToArray());
        Assert.Empty(contents[1].OfType<ThinkingContent>());
        Assert.Equal(["b"], contents[1].OfType<TextContent>().Select(t => t.Text).ToArray());
    }

    [Fact]
    public void DropsRedactedThinkingAcrossModelsAndKeepsForSameModel()
    {
        var redacted = new ThinkingContent("encrypted", Signature: null) { Redacted = true };
        var crossModel = TransformMessages.Transform(
            [new AssistantMessage([redacted], StopReason: Types.StopReason.Stop,
                Model: "a", Api: "x", Provider: "p", Timestamp: Ts)],
            VisionModel());
        Assert.Empty(crossModel.OfType<AssistantMessage>().First().Content.OfType<ThinkingContent>());

        var sameModel = TransformMessages.Transform(
            [new AssistantMessage([redacted], Model: "vision-1", Api: "openai-completions",
                Provider: "openai", Timestamp: Ts)],
            VisionModel());
        Assert.Single(sameModel.OfType<AssistantMessage>().First().Content.OfType<ThinkingContent>());
    }

    [Fact]
    public void NormalizesToolCallIdsAndRemapsResults()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage(
                [new ToolCallContent("long-id|with|pipes", "search", null)],
                StopReason.ToolUse, Model: "claude-3", Api: "anthropic-messages", Provider: "anthropic",
                Timestamp: Ts),
            new ToolResultMessage("long-id|with|pipes", "search",
                [new TextContent("found")], IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel(),
            (id, _model, _assistant) => $"norm-{id.Length}");

        var toolCall = Assert.IsType<AssistantMessage>(result[0]).ToolCalls.Single();
        Assert.Equal($"norm-{"long-id|with|pipes".Length}", toolCall.Id);
        var toolResult = Assert.IsType<ToolResultMessage>(result[1]);
        Assert.Equal(toolCall.Id, toolResult.ToolCallId); // 结果的 ID 同步归一化
    }

    [Fact]
    public void SkipsErroredAssistantTurns()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage([new TextContent("partial")], StopReason.Error,
                ErrorMessage: "boom", Timestamp: Ts),
            new UserMessage([new TextContent("retry")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());
        Assert.Single(result);
        Assert.IsType<UserMessage>(result[0]);
    }

    [Fact]
    public void SynthesizesResultsForOrphanedToolCalls()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage(
                [new ToolCallContent("call-1", "search", null), new ToolCallContent("call-2", "open", null)],
                StopReason.ToolUse, Timestamp: Ts),
            // 只有 call-1 有结果
            new ToolResultMessage("call-1", "search", [new TextContent("ok")], IsError: false, Timestamp: Ts),
            new UserMessage([new TextContent("next")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var synthetic = result.OfType<ToolResultMessage>().First(r => r.ToolCallId == "call-2");
        Assert.True(synthetic.IsError);
        Assert.Equal("No result provided", synthetic.Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public void HoldsSystemMessagesBetweenToolCallAndResult()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt"),
            new AssistantMessage([new ToolCallContent("call-1", "search", null)],
                StopReason.ToolUse, Timestamp: Ts),
            new SystemMessage("mid-turn instructions"), // 应被扣到结果之后
            new ToolResultMessage("call-1", "search", [new TextContent("ok")], IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var roles = result.Select(m => m.GetType().Name).ToList();
        Assert.Equal(["SystemMessage", "AssistantMessage", "ToolResultMessage", "SystemMessage"], roles);
    }

    [Fact]
    public void InterruptsPendingToolCallsOnUserTurn()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage([new ToolCallContent("call-1", "search", null)],
                StopReason.ToolUse, Timestamp: Ts),
            new UserMessage([new TextContent("stop that")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var synthetic = result.OfType<ToolResultMessage>().Single();
        Assert.Equal("call-1", synthetic.ToolCallId);
        Assert.True(synthetic.IsError);
        // user 在合成结果之后收尾。
        Assert.IsType<UserMessage>(result[^1]);
    }
}
