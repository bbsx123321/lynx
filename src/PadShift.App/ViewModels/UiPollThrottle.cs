using System.Diagnostics;

namespace PadShift.App.ViewModels;

/// <summary>
/// Refresh policy for the live gamepad diagram. It owns the re-entrancy guard and the adaptive
/// safety valve: normal rate is 20 Hz, but five consecutive ticks that take longer than 30 ms drop
/// the timer to 10 Hz so a software-rendered / remote session cannot saturate the UI thread.
/// </summary>
internal sealed class UiPollThrottle
{
    /// <summary>Normal refresh period (20 Hz).</summary>
    public const int FastIntervalMs = 50;

    /// <summary>Reduced refresh period used by the safety valve (10 Hz).</summary>
    public const int SlowIntervalMs = 100;

    /// <summary>A tick longer than this counts as slow.</summary>
    public const int SlowTickThresholdMs = 30;

    /// <summary>Number of consecutive slow ticks before the rate is dropped.</summary>
    public const int SlowTicksBeforeDrop = 5;

    private int _consecutiveSlow;
    private bool _entered;
    private int _dropped;

    /// <summary>Current timer interval in milliseconds (50, or 100 after the valve opened).</summary>
    public int IntervalMs { get; private set; } = FastIntervalMs;

    /// <summary>True once the safety valve has dropped the rate to 10 Hz.</summary>
    public bool IsThrottled => IntervalMs == SlowIntervalMs;

    /// <summary>Ticks dropped because the previous tick was still in flight.</summary>
    public int DroppedTicks => _dropped;

    /// <summary>Consecutive slow ticks seen so far (reset by a tick under the threshold).</summary>
    public int ConsecutiveSlowTicks => _consecutiveSlow;

    /// <summary>
    /// Re-entrancy guard. Returns false (and counts a dropped tick) when a poll is already running;
    /// the caller must pair a true result with <see cref="Exit"/>.
    /// </summary>
    public bool TryEnter()
    {
        if (_entered)
        {
            _dropped++;
            return false;
        }

        _entered = true;
        return true;
    }

    /// <summary>Leaves the critical section opened by <see cref="TryEnter"/>.</summary>
    public void Exit() => _entered = false;

    /// <summary>
    /// Records the wall-clock cost of one poll+render tick. Returns true exactly when this tick
    /// opened the safety valve (the interval changed to <see cref="SlowIntervalMs"/>).
    /// </summary>
    public bool Record(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMilliseconds > SlowTickThresholdMs)
        {
            _consecutiveSlow++;
            if (_consecutiveSlow >= SlowTicksBeforeDrop && !IsThrottled)
            {
                IntervalMs = SlowIntervalMs;
                return true;
            }
        }
        else
        {
            _consecutiveSlow = 0;
        }

        return false;
    }

    /// <summary>Returns the policy to its initial 20 Hz state (used when the diagram is re-shown).</summary>
    public void Reset()
    {
        _consecutiveSlow = 0;
        IntervalMs = FastIntervalMs;
    }
}
