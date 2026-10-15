// ============================================================================
// Reusable countdown timer for dialog components.
// Port of modes/interactive/components/countdown-timer.ts
// ============================================================================

using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Reusable countdown timer for dialog components. Port of <c>CountdownTimer</c>.
/// </summary>
/// <remarks>
/// Deviation from TS: <c>setInterval</c> becomes a <see cref="System.Threading.Timer"/>, so
/// <see cref="OnTick"/> and the TUI render request can run on a thread-pool thread (same contract
/// as the Loader animation timer, T13). The initial tick is synchronous, exactly as in TS.
/// </remarks>
public sealed class CountdownTimer : IDisposable
{
    private readonly ITui? _tui;
    private readonly Action<int> _onTick;
    private readonly Action _onExpire;
    private int _remainingSeconds;
    private Timer? _interval;

    public CountdownTimer(int timeoutMs, ITui? tui, Action<int> onTick, Action onExpire)
    {
        _tui = tui;
        _onTick = onTick;
        _onExpire = onExpire;
        _remainingSeconds = (int)Math.Ceiling(timeoutMs / 1000d);
        _onTick(_remainingSeconds);

        _interval = new Timer(_ =>
        {
            _remainingSeconds--;
            _onTick(_remainingSeconds);
            _tui?.RequestRender();

            if (_remainingSeconds <= 0)
            {
                Dispose();
                _onExpire();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        var interval = Interlocked.Exchange(ref _interval, null);
        interval?.Dispose();
    }
}
