using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// 对应上游 1.5.2 #5bdf988b7 的 <c>openai-completions-tool-call-id.test.ts</c>：
/// 回放给 OpenAI / OpenRouter 的长 tool call id 必须截到 40 以内，且共享长前缀的两个 id
/// 仍彼此不同（用 hash 而非裸截断），calls 与 results 始终成对。
/// </summary>
public class OpenAiCompletionsToolCallIdTests
{
    // 83 字符：上游用例里经 OpenRouter 转发到 Responses API 时触发 "string too long" 的真实长度。
    private static readonly string LongId = "toolu_" + new string('a', 77);
    private static readonly string OtherLongId = "toolu_" + new string('a', 76) + "b";

    private static ModelSpec Spec(string provider, string baseUrl) => new()
    {
        Id = "gpt-test",
        Name = "gpt-test",
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = baseUrl,
        ContextWindow = 128_000,
        MaxTokens = 8_192,
        Input = [ModelInput.Text],
    };

    /// <summary>构造「别的模型发起的工具调用 + 结果」，返回实际发出的 tool_calls 与 tool_call_id。</summary>
    private static (List<string> Calls, List<string> Results) SentIds(ModelSpec model, params string[] ids)
    {
        var messages = new List<ChatMessage> { Messages.UserText("List files") };
        var toolCalls = ids
            .Select(id => new ToolCallContent(id, "bash", new JsonObject { ["command"] = "ls" }))
            .ToArray();
        messages.Add(new AssistantMessage(toolCalls, StopReason.ToolUse));
        foreach (var id in ids)
            messages.Add(Messages.ToolResult(id, "bash", "ok"));

        var converted = OpenAiCompletions.ConvertMessages(
            model, new TranscriptContext(messages), OpenAiCompletions.GetCompat(model));

        var calls = new List<string>();
        var results = new List<string>();
        foreach (var node in converted.OfType<JsonObject>())
        {
            var role = node["role"]?.GetValue<string>();
            if (role == "assistant" && node["tool_calls"] is JsonArray toolCallsArray)
            {
                foreach (var call in toolCallsArray.OfType<JsonObject>())
                {
                    if (call["id"]?.GetValue<string>() is { } callId) calls.Add(callId);
                }
            }
            if (role == "tool" && node["tool_call_id"]?.GetValue<string>() is { } resultId)
                results.Add(resultId);
        }
        return (calls, results);
    }

    [Fact]
    public void ShortensLongIdsReplayedToOpenRouterKeepingCallsAndResultsPaired()
    {
        var model = Spec("openrouter", "https://openrouter.ai/api/v1");
        var (calls, results) = SentIds(model, LongId);

        var call = Assert.Single(calls);
        Assert.True(call.Length <= 40, $"id 长度应 <= 40，实际 {call.Length}：{call}");
        Assert.StartsWith("toolu_", call);
        Assert.Equal(calls, results);
    }

    [Fact]
    public void KeepsIdsThatShareALongPrefixDistinct()
    {
        var model = Spec("openrouter", "https://openrouter.ai/api/v1");
        var (calls, results) = SentIds(model, LongId, OtherLongId);

        Assert.Equal(2, calls.Distinct().Count());
        Assert.All(calls, id => Assert.True(id.Length <= 40, $"id 长度应 <= 40：{id}"));
        Assert.Equal(calls, results);
    }

    [Fact]
    public void LeavesShortIdsUnchanged()
    {
        var model = Spec("openrouter", "https://openrouter.ai/api/v1");
        var (calls, results) = SentIds(model, "call_abc123");

        Assert.Equal(["call_abc123"], calls);
        Assert.Equal(calls, results);
    }

    [Fact]
    public void ShortensLongIdsReplayedToOpenAiTheSameWay()
    {
        var model = Spec("openai", "https://api.openai.com/v1");
        var (calls, results) = SentIds(model, LongId, OtherLongId);

        Assert.All(calls, id => Assert.True(id.Length <= 40, $"id 长度应 <= 40：{id}"));
        Assert.Equal(2, calls.Distinct().Count());
        Assert.Equal(calls, results);
    }

    [Fact]
    public void LongIdsOnOtherProvidersAreNotShortened()
    {
        // 只有 openai / OpenRouter 有 40 字符上限；其它 provider 保持原样（上游 return id）。
        var model = Spec("groq", "https://api.groq.com/openai/v1");
        var (calls, results) = SentIds(model, LongId);

        Assert.Equal([LongId], calls);
        Assert.Equal(calls, results);
    }

    [Fact]
    public void ShortenedIdUses31CharsPlusUnderscorePlusHash8()
    {
        // 上游 #5bdf988b7：id.slice(0, 31) + "_" + shortHash(id).slice(0, 8) → 恰好 40 字符。
        var model = Spec("openrouter", "https://openrouter.ai/api/v1");
        var (calls, _) = SentIds(model, LongId);

        var call = Assert.Single(calls);
        Assert.Equal(40, call.Length);
        Assert.Equal(LongId[..31], call[..31]);
        Assert.Equal('_', call[31]);
        Assert.Equal(8, call.Length - 32);
    }
}
