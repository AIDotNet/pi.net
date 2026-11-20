using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Usage = Pi.Ai.Types.Usage;

namespace Pi.Durable.Tests;

/// <summary>
/// 上游 473515f9c 的 hook tasks：onYield 钩子启动的任务以 { continue } 结束时，
/// 运行追加该用户消息并继续下一次模型请求。
/// </summary>
public class HarnessOnYieldTaskTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    private static readonly DurableTask<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>,
        IReadOnlyDictionary<string, object?>> ContinueTask = new()
    {
        Definition = new TaskDefinition<IReadOnlyDictionary<string, object?>,
            IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>>
        {
            Name = "test.continue-run",
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
                            Result = new Dictionary<string, object?>
                            {
                                ["continue"] = new List<object?>
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["type"] = "text", ["text"] = "go on",
                                    },
                                },
                            },
                        },
                    }),
                    context)),
            },
        },
    };

    private sealed class OnYieldTaskHook : IGenerationHooks, IOnYieldTaskHooks
    {
        private int _asked;

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

        public Task<IReadOnlyList<RunEndTaskRequest>?> OnYieldTasks(
            AssistantMessage answer, IHookApi api, Context context)
            => Task.FromResult<IReadOnlyList<RunEndTaskRequest>?>(_asked++ == 0
                ? [new RunEndTaskRequest { Task = AnyDurableTask.From(ContinueTask) }]
                : null);
    }

    [Fact]
    public async Task OnYieldTaskContinuesTheRun()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("first")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("second")));
        var setup = HarnessTestSupport.NewChatSetup(provider);
        setup.Registry.Install(new HarnessTestSupport.TaskExtension(
            "task", [AnyDurableTask.From(ContinueTask)]));
        HarnessTestSupport.AddHooks(setup.Registry, Harness.Generation.TaskName, new OnYieldTaskHook());

        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new Pi.Durable.Storage.MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("start")] }, Ctx);
            await harness.WaitForIdleAsync(Ctx);

            Assert.Equal(2, provider.CallCount);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var texts = entries
                .Select(entry => entry.Model)
                .Where(model => model is not null)
                .SelectMany(model => model!)
                .OfType<UserMessage>()
                .SelectMany(message => message.Content.ToBlocks())
                .OfType<TextContent>()
                .Select(text => text.Text)
                .ToList();
            Assert.Contains("go on", texts);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
