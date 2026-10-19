using PadShift.Core;

namespace PadShift.Input;

/// <summary>
/// Aggregates several input sources into one <see cref="ControllerState"/>.
/// Buttons are OR-ed across all sources; each axis (and gyro channel) keeps the signed value with
/// the largest magnitude. Touch data comes from the first source reporting an active finger.
/// </summary>
/// <remarks>
/// Polling is single-threaded, matching the <see cref="IInputSource.TryPoll"/> contract. The
/// composite remembers the last good snapshot of every child, so a child that has no new report
/// this frame does not drop held buttons from the merged state.
/// </remarks>
public sealed class CompositeInputSource : IInputSource
{
    private readonly IInputSource[] _sources;
    private readonly ControllerState[] _lastStates;
    private readonly ControllerState _merged = new();
    private readonly ControllerState _scratch = new();

    private bool _disposed;

    public CompositeInputSource(params IInputSource[] sources)
        : this((IEnumerable<IInputSource>)sources)
    {
    }

    public CompositeInputSource(IEnumerable<IInputSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();

        if (_sources.Length == 0)
        {
            throw new ArgumentException("At least one input source is required.", nameof(sources));
        }

        _lastStates = new ControllerState[_sources.Length];
        var ids = new List<string>(_sources.Length);

        for (int i = 0; i < _sources.Length; i++)
        {
            IInputSource source = _sources[i]
                ?? throw new ArgumentException("Input source collection contains null.", nameof(sources));
            _lastStates[i] = new ControllerState();
            ids.Add(source.DeviceId);
        }

        DeviceId = "Composite:" + string.Join("+", ids);
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            foreach (IInputSource source in _sources)
            {
                if (source.IsConnected)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public bool TryPoll(ControllerState state)
    {
        if (_disposed)
        {
            return false;
        }

        bool anyUpdated = false;
        _merged.Clear();

        for (int i = 0; i < _sources.Length; i++)
        {
            IInputSource source = _sources[i];

            if (!source.IsConnected)
            {
                _lastStates[i].Clear();
                continue;
            }

            bool updated;
            _scratch.Clear();

            try
            {
                updated = source.TryPoll(_scratch);
            }
            catch
            {
                // Defensive: a faulty source must not break the aggregate poll.
                updated = false;
            }

            if (updated)
            {
                ControllerStateCopy.Copy(_scratch, _lastStates[i]);
                anyUpdated = true;
            }

            Merge(_lastStates[i], _merged);
        }

        if (!anyUpdated)
        {
            return false;
        }

        _merged.TimestampTicks = DateTime.UtcNow.Ticks;
        ControllerStateCopy.Copy(_merged, state);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (IInputSource source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch
            {
                // Disposal is best effort.
            }
        }
    }

    private static void Merge(ControllerState source, ControllerState destination)
    {
        for (int i = 0; i < source.Buttons.Length; i++)
        {
            if (source.Buttons[i])
            {
                destination.Buttons[i] = true;
            }
        }

        for (int i = 0; i < source.Axes.Length; i++)
        {
            long sourceMagnitude = Math.Abs((long)source.Axes[i]);
            long destinationMagnitude = Math.Abs((long)destination.Axes[i]);

            if (sourceMagnitude > destinationMagnitude)
            {
                destination.Axes[i] = source.Axes[i];
            }
        }

        MergeGyro(source, destination);

        if (!destination.TouchActive && source.TouchActive)
        {
            destination.TouchActive = true;
            destination.TouchX = source.TouchX;
            destination.TouchY = source.TouchY;
        }
    }

    private static void MergeGyro(ControllerState source, ControllerState destination)
    {
        destination.GyroX = MaxMagnitude(source.GyroX, destination.GyroX);
        destination.GyroY = MaxMagnitude(source.GyroY, destination.GyroY);
        destination.GyroZ = MaxMagnitude(source.GyroZ, destination.GyroZ);
    }

    private static float MaxMagnitude(float candidate, float current) =>
        Math.Abs(candidate) > Math.Abs(current) ? candidate : current;
}
