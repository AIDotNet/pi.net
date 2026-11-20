using Pi.Durable.Harness;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// suspendable signals 的纯逻辑：投递/重复/到期/读取/睡眠判定。
/// 对应 TS <c>signals.ts</c>，上游测试里的 harness-signals 行为在这里以纯叶子钉住。
/// </summary>
public class SignalsTests
{
    private static TaskRecord Record(TaskState state, IReadOnlyDictionary<string, object?>? memos = null)
        => new()
        {
            Id = TaskId<object?>.From(7),
            ConversationId = ConversationId.From(1),
            Kind = "test.signal",
            Version = 1,
            Background = false,
            AbortRequested = false,
            State = state,
            Memos = memos,
        };

    [Fact]
    public void DeliversReadsAndRejectsDuplicates()
    {
        var token = SignalStore.Define<string>("test.ping", 1);
        var delivery = SignalStore.Deliver(Record(new TaskState { Status = TaskStatus.Pending }), token, "k", "hello", 0);
        Assert.Equal(SignalResult.Delivered, delivery.Result);
        Assert.NotNull(delivery.Record);

        var state = SignalStore.Read(delivery.Record!.Memos, token, "k");
        Assert.Equal("hello", Assert.IsType<SignalState<string>.Value>(state).Item);

        Assert.Equal(SignalResult.Duplicate,
            SignalStore.Deliver(delivery.Record, token, "k", "again", 0).Result);
    }

    [Fact]
    public void RejectsInvalidValuesAndClosedTasks()
    {
        var token = SignalStore.Define<int>("test.count", 1, value => value >= 0);
        Assert.Equal(SignalResult.Invalid,
            SignalStore.Deliver(Record(new TaskState { Status = TaskStatus.Pending }), token, "k", -1, 0).Result);
        Assert.Equal(SignalResult.NotFound, SignalStore.Deliver(null, token, "k", 1, 0).Result);

        var terminal = Record(new TaskState
        {
            Status = TaskStatus.Terminal,
            Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = 1 },
        });
        Assert.Equal(SignalResult.Closed, SignalStore.Deliver(terminal, token, "k", 1, 0).Result);
    }

    [Fact]
    public void SleepWakesOnTheFirstReadySignalAndConsumesIt()
    {
        var token = SignalStore.Define<string>("test.ping", 1);
        var key = token.Key("k");
        var state = new TaskState { Status = TaskStatus.Sleeping, Signals = [key] };
        var delivered = SignalStore.Deliver(Record(state), token, "k", "hello", 0).Record!;
        var sleeping = delivered.State;

        Assert.True(SignalStore.SleepDue(sleeping, delivered.Memos, 0));
        var wake = SignalStore.SleepWake(sleeping, delivered.Memos, 0);
        Assert.NotNull(wake);
        Assert.Equal("signal", wake!.Value.Reason);
        Assert.Equal(key, wake.Value.Key);

        // 唤醒写的 consumed 让同一个值不再唤醒下一次睡眠。
        var consumed = new Dictionary<string, object?>(delivered.Memos!);
        foreach (var (name, memo) in wake.Value.Memos) consumed[name] = memo;
        Assert.False(SignalStore.SleepDue(sleeping, consumed, 0));
    }

    [Fact]
    public void DeadlineExpiresPendingSignals()
    {
        var token = SignalStore.Define<string>("test.ping", 1);
        var key = token.Key("k");
        var state = new TaskState { Status = TaskStatus.Sleeping, Until = 100, Signals = [key] };
        var record = Record(state);

        var delivery = SignalStore.Deliver(record, token, "k", "late", 200);
        Assert.Equal(SignalResult.Closed, delivery.Result);
        Assert.Equal(new SignalState<string>.Expired(), SignalStore.Read(delivery.Record!.Memos, token, "k"));

        var wake = SignalStore.SleepWake(state, record.Memos, 200);
        Assert.NotNull(wake);
        Assert.Equal("expired", wake!.Value.Reason);
        Assert.NotEmpty(wake.Value.Memos);
    }

    [Fact]
    public void ParkCheckpointRoundTrips()
    {
        var token = SignalStore.Define<string>("test.ping", 1);
        var park = new ToolPark
        {
            Checkpoint = new Dictionary<string, object?> { ["step"] = "upload" },
            Until = 1234,
            Signals = [token.Key("k")],
        };
        var checkpoint = new ToolTaskCheckpoint.Park(new Dictionary<string, object?> { ["x"] = 1 }, "safe", park, "execute");

        var back = Assert.IsType<ToolTaskCheckpoint.Park>(ToolTaskCheckpoint.FromJson(checkpoint.ToJson()));
        Assert.Equal("execute", back.Stage);
        Assert.Equal("safe", back.Replay);
        Assert.Equal(1234L, back.Wait.Until);
        var key = Assert.Single(back.Wait.Signals!);
        Assert.Equal("test.ping", key.Signal);
        Assert.Equal("k", key.Key);
        var checkpointRead = Assert.IsType<Dictionary<string, object?>>(back.Wait.Checkpoint);
        Assert.Equal("upload", checkpointRead["step"]);
    }
}
