using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ServerLifetime"/> — port of experimental-server-lifecycle.test.ts (3 cases). TS drives
/// vitest's fake timers; the port drives a manual clock, so the same millisecond boundaries hold.
/// </summary>
public class ServerLifecycleTests
{
    [Fact]
    public void HoldsAForegroundServerUntilExplicitShutdown()
    {
        var clock = new ManualServerLifetimeClock();
        var lifetime = new ServerLifetime(true, new ServerLifetimeOptions { Clock = clock });
        var retires = 0;

        lifetime.Start(() => retires += 1);
        clock.Advance(60_000);
        Assert.Equal(0, retires);

        lifetime.Stop();
    }

    [Fact]
    public void RetiresAnAutomaticServerIfNoFirstClientArrives()
    {
        var clock = new ManualServerLifetimeClock();
        var lifetime = new ServerLifetime(false, new ServerLifetimeOptions { Clock = clock });
        var retires = 0;

        lifetime.Start(() => retires += 1);
        clock.Advance(10_999);
        Assert.Equal(0, retires);

        clock.Advance(1);
        Assert.Equal(1, retires);

        lifetime.Stop();
    }

    [Fact]
    public void RequiresBothClientAndWorkerDemandToDisappear()
    {
        var clock = new ManualServerLifetimeClock();
        var lifetime = new ServerLifetime(false, new ServerLifetimeOptions { Clock = clock });
        var retires = 0;

        lifetime.Start(() => retires += 1);
        lifetime.SetConnectionCount(1);
        lifetime.SetWorkerCount(1);
        lifetime.SetConnectionCount(0);
        clock.Advance(10_000);
        Assert.Equal(0, retires);

        lifetime.SetWorkerCount(0);
        clock.Advance(999);
        Assert.Equal(0, retires);

        clock.Advance(1);
        Assert.Equal(1, retires);

        lifetime.Stop();
    }

    [Fact]
    public void TheFirstClientCancelsTheStartupGrace()
    {
        var clock = new ManualServerLifetimeClock();
        var lifetime = new ServerLifetime(false, new ServerLifetimeOptions { Clock = clock });
        var retires = 0;

        lifetime.Start(() => retires += 1);
        lifetime.SetConnectionCount(1);
        // The startup grace is gone: an idle server with one client retires after the idle grace
        // once that client leaves, not after the startup window.
        clock.Advance(10_000);
        lifetime.SetConnectionCount(0);
        Assert.Equal(0, retires);

        clock.Advance(1_000);
        Assert.Equal(1, retires);

        lifetime.Stop();
    }

    /// <summary>
    /// A hand-driven clock. Timers fire when the clock advances past their due time — including
    /// ones scheduled during the advance, the way vitest's <c>advanceTimersByTime</c> behaves.
    /// </summary>
    private sealed class ManualServerLifetimeClock : IServerLifetimeClock
    {
        private readonly Lock _gate = new();
        private readonly List<Entry> _entries = [];
        private long _now;
        private long _sequence;

        public IServerLifetimeTimer StartTimer(int milliseconds, Action fire)
        {
            var entry = new Entry(Interlocked.Increment(ref _sequence), _now + milliseconds, fire);
            lock (_gate) _entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// Advance the clock, firing every timer that comes due — including ones scheduled during
        /// the advance, the way vitest's <c>advanceTimersByTime</c> behaves. Each timer fires with
        /// the clock at its own due time, so a timer scheduled inside a callback is relative to
        /// that callback's moment, not to the end of the advance.
        /// </summary>
        public void Advance(int milliseconds)
        {
            var target = _now + milliseconds;
            while (true)
            {
                Entry? due;
                lock (_gate)
                {
                    due = _entries
                        .Where(entry => !entry.Cancelled && entry.Due <= target)
                        .OrderBy(entry => entry.Due)
                        .ThenBy(entry => entry.Sequence)
                        .FirstOrDefault();
                    if (due is not null) _entries.Remove(due);
                }

                if (due is null) break;
                _now = Math.Max(_now, due.Due);
                due.Fire();
            }

            _now = target;
        }

        private sealed class Entry(long sequence, long due, Action fire) : IServerLifetimeTimer
        {
            public long Sequence { get; } = sequence;

            public long Due { get; } = due;

            public bool Cancelled { get; private set; }

            private readonly Action _fire = fire;

            public void Cancel() => Cancelled = true;

            public void Fire() => _fire();
        }
    }
}
