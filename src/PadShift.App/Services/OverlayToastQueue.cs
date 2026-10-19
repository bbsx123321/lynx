namespace PadShift.App.Services;

/// <summary>One mapping-status message shown by the overlay toast.</summary>
public sealed record OverlayToast(string Message, DateTimeOffset ShownAt)
{
    public TimeSpan Age(DateTimeOffset now) => now - ShownAt;
}

/// <summary>
/// Single-slot toast queue for the overlay. Mapping state changes arrive in bursts (remap off →
/// keys released → state changed), so only the newest message is kept: the overlay must never show
/// a backlog. A message lives for <see cref="Lifetime"/> and then disappears (the window fades it
/// out over <see cref="FadeDuration"/>).
/// </summary>
public sealed class OverlayToastQueue
{
    public const int DefaultLifetimeMs = 1500;
    public static readonly TimeSpan DefaultFade = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private OverlayToast? _current;

    public OverlayToastQueue(int lifetimeMs = DefaultLifetimeMs)
    {
        Lifetime = TimeSpan.FromMilliseconds(Math.Max(100, lifetimeMs));
    }

    public TimeSpan Lifetime { get; }

    public TimeSpan FadeDuration { get; } = DefaultFade;

    /// <summary>Replaces the visible message; older messages are dropped immediately.</summary>
    public void Enqueue(string? message, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_gate)
        {
            _current = new OverlayToast(message.Trim(), now);
        }
    }

    /// <summary>
    /// The message that should be visible at <paramref name="now"/>, or null once it expired.
    /// It stays available during <see cref="FadeDuration"/> so the window can fade it out.
    /// </summary>
    public string? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_current is null || _current.Age(now) > Lifetime + FadeDuration)
            {
                return null;
            }

            return _current.Message;
        }
    }

    /// <summary>Opacity for the toast at <paramref name="now"/>: 1 while alive, then a linear fade.</summary>
    public double Opacity(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return 0;
            }

            TimeSpan age = _current.Age(now);
            if (age < TimeSpan.Zero)
            {
                return 1;
            }

            if (age <= Lifetime)
            {
                return 1;
            }

            double fadeMs = FadeDuration.TotalMilliseconds;
            double over = (age - Lifetime).TotalMilliseconds;
            return over >= fadeMs ? 0 : Math.Clamp(1 - (over / fadeMs), 0, 1);
        }
    }

    /// <summary>Drops the current message (used when the overlay is disabled).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _current is null;
            }
        }
    }
}
