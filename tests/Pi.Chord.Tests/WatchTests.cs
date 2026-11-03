using Pi.Chord.Delta;
using Pi.Chord.Services;
using Xunit;
using ChordContext = Pi.Chord.Context.Context;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>
/// watch 句柄的共享生命周期与两种实现（移植 <c>packages/chord/test/watch.test.ts</c> 的代表性用例）：
/// 折叠投递、慢监听器、溢出、退休标记、可恢复失败、投递后结束、终止原因，以及原生 watch 的差异。
/// </summary>
public class WatchTests
{
    private static readonly ChordContext Ctx = ChordContext.Background;

    /// <summary>让线程池上排队的投递跑完。对应 TS <c>flush()</c>。</summary>
    private static async Task FlushAsync()
    {
        for (var round = 0; round < 4; round++)
        {
            await Task.Yield();
            await Task.Delay(2).ConfigureAwait(false);
        }
    }

    private static DeltaOp Set(string key, object? value) =>
        new DeltaOp.Set(new Path([Seg.Key(key)]), value);

    private static DeltaOp Append(string key, string text) =>
        new DeltaOp.Append(new Path([Seg.Key(key)]), text);

    /// <summary>一次投递的调用；失败调用单列。</summary>
    private sealed record Delivered(object? Value, IReadOnlyList<DeltaOp>? Ops, bool Retired, Exception? Failure);

    /// <summary>记录 detach 与报告的 watch。对应 TS <c>recordedWatch</c>。</summary>
    private sealed class Recorded<T>
    {
        public int Detached;
        public List<Exception> Reported { get; } = [];

        public Watch<T> Watch { get; }

        public Recorded(T value) => Watch = new Watch<T>(value, () => Detached++, exception => Reported.Add(exception));
    }

    /// <summary>已启动、收集每次调用的 watch。对应 TS <c>collectingWatch</c>。</summary>
    private sealed class Collecting<T>
    {
        public Recorded<T> Recorded { get; }

        public List<Delivered> Frames { get; } = [];

        public Watch<T> Watch => Recorded.Watch;

        public Collecting(T value, bool start = true)
        {
            Recorded = new Recorded<T>(value);
            if (start) Start();
        }

        public void Start() => Watch.Start((call, _) =>
        {
            lock (Frames)
            {
                Frames.Add(call is WatchCall<T>.Value value
                    ? new Delivered(value.NewValue, value.Ops, value.Retired, null)
                    : new Delivered(null, null, false, ((WatchCall<T>.Failure)call).Error));
            }
            return Task.CompletedTask;
        });

        public List<Delivered> Taken()
        {
            lock (Frames) return [.. Frames];
        }
    }

    private static WatchCall<T> Frame<T>(T value, IReadOnlyList<DeltaOp> ops) => new WatchCall<T>.Value(value, ops);

    // ---------------------------------------------------------------- 投递

    [Fact]
    public async Task KeepsTheAcquisitionValueUntilStartThenDeliversTheFramesSinceInOneCall()
    {
        var collecting = new Collecting<Dictionary<string, object?>>(new() { ["n"] = 0L, ["text"] = "" }, start: false);
        var watch = collecting.Watch;
        watch.Advance(Frame(new Dictionary<string, object?> { ["n"] = 1L, ["text"] = "" }, [Set("n", 1L)]), Ctx);
        watch.Advance(Frame(new Dictionary<string, object?> { ["n"] = 2L, ["text"] = "a" },
            [Set("n", 2L), Append("text", "a")]), Ctx);
        // 启动之前不投递，持有的是获取时的值。
        Assert.Empty(collecting.Frames);
        Assert.Equal(0L, watch.Value["n"]);

        collecting.Start();
        await FlushAsync();
        var call = Assert.Single(collecting.Taken());
        Assert.Equal(2L, ((Dictionary<string, object?>)call.Value!)["n"]);
        Assert.Equal(["s", "a"], call.Ops!.Select(op => op switch
        {
            DeltaOp.Set => "s",
            DeltaOp.Append => "a",
            _ => "?",
        }));
        Assert.False(call.Retired);
        Assert.Equal(2L, watch.Value["n"]);
    }

    [Fact]
    public async Task ATightSequenceOfFramesArrivesAsOneCallWithTheFoldedOps()
    {
        var collecting = new Collecting<Dictionary<string, object?>>(new() { ["text"] = "" }, start: false);
        for (var index = 0; index < 5; index++)
            collecting.Watch.Advance(Frame(new Dictionary<string, object?> { ["text"] = new string('x', index + 1) },
                [Append("text", "x")]), Ctx);
        collecting.Start();
        await FlushAsync();
        var call = Assert.Single(collecting.Taken());
        var append = Assert.IsType<DeltaOp.Append>(Assert.Single(call.Ops!));
        Assert.Equal("xxxxx", append.Text);
    }

    [Fact]
    public async Task ASlowListenerTakesOneCallAtATimeAndFoldsWhatArrivesMeanwhile()
    {
        var collecting = new Collecting<Dictionary<string, object?>>(new() { ["text"] = "" });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var watch = new Watch<Dictionary<string, object?>>(new() { ["text"] = "" }, () => { }, _ => { });
        watch.Start(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            }
        });
        watch.Advance(Frame(new Dictionary<string, object?> { ["text"] = "a" }, [Append("text", "a")]), Ctx);
        await entered.Task;
        watch.Advance(Frame(new Dictionary<string, object?> { ["text"] = "ab" }, [Append("text", "b")]), Ctx);
        watch.Advance(Frame(new Dictionary<string, object?> { ["text"] = "abc" }, [Append("text", "c")]), Ctx);
        gate.TrySetResult();
        await FlushAsync();
        // 两次调用：第一次是 "a"，第二次折叠了 "b" 与 "c"。
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal("abc", watch.Value["text"]);
        _ = collecting;
    }

    [Fact]
    public async Task OverflowPastTheCapDeliversARootReplacement()
    {
        var watch = new Collecting<long>(0L, start: false);
        // 每个值一条互不相同的路径（同路径的 s 会被折叠掉，撑不到上限）：超过上限后队列溢出，投递一次根替换。
        var total = PendingOps.MaxPendingOps + 10;
        for (var index = 0; index < total; index++)
        {
            watch.Watch.Advance(Frame((long)index,
                [new DeltaOp.Set(new Path([Seg.Key("k"), Seg.Index(index)]), (long)index)]), Ctx);
        }
        watch.Start();
        await FlushAsync();
        var call = Assert.Single(watch.Taken());
        var replace = Assert.IsType<DeltaOp.Replace>(Assert.Single(call.Ops!));
        Assert.Equal((long)total - 1, replace.Value);
    }

    [Fact]
    public async Task ARetirementIsAFlagOnTheCallAndDoesNotEndTheWatch()
    {
        var watch = new Collecting<object?>(1, start: false);
        watch.Watch.Advance(new WatchCall<object?>.Value(null, [new DeltaOp.Replace(null)], Retired: true), Ctx);
        watch.Start();
        await FlushAsync();
        var retired = Assert.Single(watch.Taken());
        Assert.True(retired.Retired);
        Assert.Null(retired.Value);
        Assert.False(watch.Watch.Closed.IsCompleted);

        // 该地址上新出现的文档以根替换到达，watch 仍在。
        watch.Watch.Advance(Frame<object?>(2, [new DeltaOp.Replace(2)]), Ctx);
        await FlushAsync();
        Assert.Equal(2, watch.Taken().Count);
        Assert.False(watch.Watch.Closed.IsCompleted);
        await watch.Watch.Stop();
    }

    // ---------------------------------------------------------------- 可恢复失败

    [Fact]
    public async Task AFailureDropsPendingFramesAndTheNextValueReplacesTheRoot()
    {
        var watch = new Collecting<object?>(0, start: false);
        var boom = new InvalidOperationException("boom");
        watch.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        watch.Watch.Fail(boom, Ctx);
        watch.Start();
        await FlushAsync();
        var call = Assert.Single(watch.Taken());
        Assert.Same(boom, call.Failure);
        // 失败的调用不改 Value：保留最后交付的值。
        Assert.Equal(0, watch.Watch.Value);

        watch.Watch.Advance(Frame<object?>(2, [Set("n", 2L)]), Ctx);
        await FlushAsync();
        var after = watch.Taken()[1];
        Assert.IsType<DeltaOp.Replace>(Assert.Single(after.Ops!));
        Assert.Equal(2, after.Value);
    }

    // ---------------------------------------------------------------- 投递后结束

    [Fact]
    public async Task EndAfterDeliveryDeliversWhatIsPendingThenEnds()
    {
        var watch = new Collecting<object?>(0, start: false);
        watch.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        watch.Watch.EndAfterDelivery();
        // 未启动的 watch 把待发内容留到启动。
        Assert.False(watch.Watch.Closed.IsCompleted);
        watch.Start();
        await FlushAsync();
        Assert.Single(watch.Taken());
        Assert.IsType<WatchEnd.Ended>(await watch.Watch.Closed);
    }

    [Fact]
    public async Task EndAfterDeliveryWithNothingPendingEndsAtOnce()
    {
        var watch = new Collecting<object?>(0);
        watch.Watch.EndAfterDelivery();
        Assert.IsType<WatchEnd.Ended>(await watch.Watch.Closed);
    }

    [Fact]
    public async Task StopAndEndWinOverEndAfterDelivery()
    {
        // 有待发内容时 EndAfterDelivery 不会立刻结束，Stop 仍抢先。
        var stopped = new Collecting<object?>(0, start: false);
        stopped.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        stopped.Watch.EndAfterDelivery();
        Assert.IsType<WatchEnd.Stopped>(await stopped.Watch.Stop());
        Assert.Empty(stopped.Taken());

        var ended = new Collecting<object?>(0);
        ended.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        ended.Watch.EndAfterDelivery();
        ended.Watch.End();
        Assert.IsType<WatchEnd.Ended>(await ended.Watch.Closed);
    }

    // ---------------------------------------------------------------- 终止原因

    [Fact]
    public async Task EndsOnceForEachReasonAndDropsItsReferences()
    {
        var stopped = new Collecting<object?>(0);
        Assert.IsType<WatchEnd.Stopped>(await stopped.Watch.Stop());
        Assert.Equal(1, stopped.Recorded.Detached);
        Assert.IsType<WatchEnd.Stopped>(await stopped.Watch.Stop());
        Assert.Equal(1, stopped.Recorded.Detached);

        var aborted = new Collecting<object?>(0);
        aborted.Watch.Abort();
        Assert.IsType<WatchEnd.Aborted>(await aborted.Watch.Closed);

        var failed = new Collecting<object?>(0);
        var boom = new IOException("disk");
        failed.Watch.Fail(boom);
        var end = Assert.IsType<WatchEnd.Failed>(await failed.Watch.Closed);
        Assert.Same(boom, end.Error);

        var ended = new Collecting<object?>(0);
        ended.Watch.End();
        Assert.IsType<WatchEnd.Ended>(await ended.Watch.Closed);
    }

    [Fact]
    public async Task AThrowingListenerEndsTheWatchAndReportsTheError()
    {
        var recorded = new Recorded<object?>(0);
        var boom = new InvalidOperationException("listener boom");
        recorded.Watch.Start((_, _) => throw boom);
        recorded.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        var end = Assert.IsType<WatchEnd.ListenerError>(await recorded.Watch.Closed);
        Assert.Same(boom, end.Error);
        Assert.Same(boom, Assert.Single(recorded.Reported));
    }

    [Fact]
    public async Task FramesAfterTerminationAreIgnored()
    {
        var watch = new Collecting<object?>(0);
        await watch.Watch.Stop();
        watch.Watch.Advance(Frame<object?>(1, [new DeltaOp.Replace(1)]), Ctx);
        watch.Watch.Fail(new IOException("late"), Ctx);
        await FlushAsync();
        Assert.Empty(watch.Taken());
        Assert.Equal(1, watch.Recorded.Detached);
    }

    [Fact]
    public async Task AbortFollowsTheAcquisitionSignal()
    {
        using var source = new CancellationTokenSource();
        var watch = new Collecting<object?>(0);
        watch.Watch.ObserveAbort(source.Token);
        source.Cancel();
        Assert.IsType<WatchEnd.Aborted>(await watch.Watch.Closed);

        // 已经取消的信号在安装时立刻生效。
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var late = new Collecting<object?>(0);
        late.Watch.ObserveAbort(cancelled.Token);
        Assert.IsType<WatchEnd.Aborted>(await late.Watch.Closed);
    }

    // ---------------------------------------------------------------- 原生 watch

    [Fact]
    public async Task NativeWatchDeliversEveryValueInItsOwnCallInOrder()
    {
        var frames = new List<int>();
        var watch = new NativeWatch<int>(0, () => { }, _ => { });
        watch.Start((call, _) =>
        {
            lock (frames) frames.Add(((NativeWatchCall<int>.Value)call).NewValue);
            return Task.CompletedTask;
        });
        for (var index = 1; index <= 3; index++) watch.Advance(index, Ctx);
        await FlushAsync();
        lock (frames) Assert.Equal([1, 2, 3], frames);
        Assert.Equal(3, watch.Value);
    }

    [Fact]
    public async Task NativeWatchCollapsesPastOneHundredPendingValues()
    {
        var frames = new List<int>();
        var watch = new NativeWatch<int>(0, () => { }, _ => { });
        for (var index = 1; index <= 150; index++) watch.Advance(index, Ctx);
        watch.Start((call, _) =>
        {
            lock (frames) frames.Add(((NativeWatchCall<int>.Value)call).NewValue);
            return Task.CompletedTask;
        });
        await FlushAsync();
        lock (frames)
        {
            // 第 101 个值到达时把前 100 个折叠成一个（缺省取最新值），其余按序各自一帧。
            Assert.Equal([101, 102, 103, 104, 105], frames.Take(5).ToArray());
            Assert.Equal(50, frames.Count);
            Assert.Equal(150, frames[^1]);
        }
    }

    [Fact]
    public async Task NativeWatchFailureDropsPendingValuesAndKeepsTheDeliveredOne()
    {
        var calls = new List<object?>();
        var watch = new NativeWatch<int>(0, () => { }, _ => { });
        watch.Advance(1, Ctx);
        watch.Fail(new IOException("boom"), Ctx);
        watch.Start((call, _) =>
        {
            lock (calls) calls.Add(call is NativeWatchCall<int>.Failure failure ? failure.Error : ((NativeWatchCall<int>.Value)call).NewValue);
            return Task.CompletedTask;
        });
        await FlushAsync();
        lock (calls)
        {
            var failure = Assert.IsType<IOException>(Assert.Single(calls));
            Assert.Equal("boom", failure.Message);
        }
        Assert.Equal(0, watch.Value);
    }
}
