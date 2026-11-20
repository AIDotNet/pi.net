using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Usage = Pi.Ai.Types.Usage;

namespace Pi.Durable.Tests;

/// <summary>
/// 上游 473515f9c 的 hook tasks：afterTools 钩子可启动持久任务，任务以 { terminate: true }
/// 结束即终止本轮，不再请求模型。
/// </summary>
public class HarnessHookTaskTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    private sealed class TerminateTask
    {
        public static readonly DurableTask<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>,
            IReadOnlyDictionary<string, object?>> Definition = new()
        {
            Definition = new TaskDefinition<IReadOnlyDictionary<string, object?>,
                IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>>
            {
                Name = "test.terminate-round",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)((_, runtime, context) => runtime.CommitAsync(
                        (_, _) => Task.FromResult<TaskState?>(new TaskState
                        {
                            Status = TaskStatus.Terminal,
                            Outcome = new TaskOutcome
                            {
                                Status = TaskOutcomeStatus.Completed,
                                Result = new Dictionary<string, object?> { ["terminate"] = true },
                            },
                        }),
                        context)),
                },
            },
        };
    }

    private sealed class AfterToolsTaskHook : IGenerationHooks, IAfterToolsTaskHooks
    {
        public Task<(IReadOnlyList<ChatMessage> Messages, bool Used)?> BeforeRequest(
            IReadOnlyList<ChatMessage> messages, IHookApi api, Context context)
            => Task.FromResult<(IReadOnlyList<ChatMessage>, bool)?>(null);

        public Task<System.Text.Json.Nodes.JsonObject?> BeforeProviderRequest(
            Pi.Ai.Models.ModelSpec model, System.Text.Json.Nodes.JsonObject payload, IHookApi api, Context context)
            => Task.FromResult<System.Text.Json.Nodes.JsonObject?>(null);

        public Task AfterResponse(AssistantMessage message, IHookApi api, Context context) => Task.CompletedTask;

        public Task<(IReadOnlyList<ContentBlock> Continue, bool ContinueRun)?> OnYield(
            AssistantMessage answer, IHookApi api, Context context)
            => Task.FromResult<(IReadOnlyList<ContentBlock>, bool)?>(null);

        public Task AfterTools(EntryId assistant, IReadOnlyList<EntryId> results, IHookApi api, Context context)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RunEndTaskRequest>?> AfterToolsTasks(
            EntryId assistant, IReadOnlyList<EntryId> results, IHookApi api, Context context)
            => Task.FromResult<IReadOnlyList<RunEndTaskRequest>?>(
                [new RunEndTaskRequest { Task = AnyDurableTask.From(TerminateTask.Definition) }]);
    }

    [Fact]
    public async Task AfterToolsTaskTerminatesTheRoundWithoutAnotherModelCall()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(new AssistantMessage(
                [new ToolCallContent("c1", "hold", new Dictionary<string, object?>())])
            {
                StopReason = StopReason.ToolUse,
                Model = "faux-1",
                Provider = "faux",
                UsageStats = new Usage(0, 0),
            }),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("should not run")));
        var setup = HarnessTestSupport.NewChatSetup(provider);
        setup.Registry.Install(new HarnessTestSupport.TaskExtension(
            "task", [AnyDurableTask.From(TerminateTask.Definition)]));
        HarnessTestSupport.AddTool(setup.Registry,
            new HarnessTestSupport.ScriptedTool("hold", "hold",
                (_, _) => Task.FromResult(new ToolExecutionResult { Content = [new TextContent("ok")] })));
        HarnessTestSupport.AddHooks(setup.Registry, Harness.Generation.TaskName, new AfterToolsTaskHook());

        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new Pi.Durable.Storage.MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await harness.WaitForIdleAsync(Ctx);

            // 工具轮之后被 hook task 终止：模型只被请求一次，第二个脚本应答不会被消费。
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
