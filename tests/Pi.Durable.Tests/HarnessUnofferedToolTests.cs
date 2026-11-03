using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 对请求未提供的工具的调用也按工具任务跑（1c6928b30）：由任务结算 <c>tool_unavailable</c>（并过 afterTool），
/// 而不是由生成直接落结果；abort 时未开始的未提供调用同样结算 <c>tool_unavailable</c>。
/// </summary>
public class HarnessUnofferedToolTests
{
    private static readonly Context Ctx = Context.Background;

    /// <summary>一次带一个 <c>missing</c> 调用的工具轮——请求没有提供它。</summary>
    private static AssistantMessage Round(string callId) => new(
        [new ToolCallContent(callId, "missing", new Dictionary<string, object?>())],
        StopReason.ToolUse);

    [Fact]
    public async Task AnUnofferedCallRunsAsAToolTaskAndSettlesToolUnavailable()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
        var seen = new List<(string Call, bool IsError)>();
        HarnessTestSupport.AddHooks(setup.Registry, "pi.tool", new HarnessTestSupport.ToolHooksImpl(
            afterTool: (call, result) =>
            {
                seen.Add((call.Name, result.IsError));
                return null;
            }));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, record.Status);

            // 轮里的调用起了任务、结算 tool_unavailable，之后模型还被问了一次并回答。
            Assert.Equal(2, provider.CallCount);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var toolResult = entries.Single(entry => entry.Kind == DurableEntries.ToolResultEntry.Kind);
            var message = Assert.IsType<ToolResultMessage>(toolResult.Model!.First());
            Assert.Contains(
                message.Content.OfType<TextContent>(),
                content => content.Text.Contains("not available"));

            var saw = Assert.Single(seen);
            Assert.Equal("missing", saw.Call);
            Assert.True(saw.IsError);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
