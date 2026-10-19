namespace PadShift.Output.VirtualPad;

/// <summary>
/// Latest rumble request collected from the emulated pad(s). Kept outside
/// <see cref="PadShift.Core.OutputIntent"/> on purpose: rumble travels in the opposite direction
/// (game/output report to physical device), so the engine can poll it independently of the
/// keyboard/pad submit loop.
/// </summary>
/// <remarks>
/// <see cref="Changed"/> is raised on the ViGEm notification thread; handlers must be quick and
/// must not block. <see cref="TryConsume"/> is the lock-based, allocation-free way to read it from
/// the engine's polling thread.
/// </remarks>
public sealed class RumbleState
{
    private readonly object _gate = new();
    private byte _left;
    private byte _right;
    private long _version;
    private bool _pending;

    /// <summary>Raised on every rumble report with the large (left) and small (right) motors.</summary>
    public event Action<byte, byte>? Changed;

    /// <summary>Large/left motor intensity, 0..255.</summary>
    public byte Left
    {
        get
        {
            lock (_gate)
            {
                return _left;
            }
        }
    }

    /// <summary>Small/right motor intensity, 0..255.</summary>
    public byte Right
    {
        get
        {
            lock (_gate)
            {
                return _right;
            }
        }
    }

    /// <summary>Increments on every report; lets readers detect updates without timers.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>true when a report arrived that no reader has consumed yet.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Stores a rumble request and notifies subscribers.</summary>
    public void Report(byte largeMotor, byte smallMotor)
    {
        Action<byte, byte>? handler;

        lock (_gate)
        {
            _left = largeMotor;
            _right = smallMotor;
            _version++;
            _pending = true;
            handler = Changed;
        }

        if (handler is null)
        {
            return;
        }

        try
        {
            handler(largeMotor, smallMotor);
        }
        catch
        {
            // Never let engine code tear down the ViGEm notification thread.
        }
    }

    /// <summary>
    /// Reads the latest values once. Returns false when no report arrived since the previous
    /// successful consume (the out values then hold the last known intensities).
    /// </summary>
    public bool TryConsume(out byte largeMotor, out byte smallMotor)
    {
        lock (_gate)
        {
            largeMotor = _left;
            smallMotor = _right;

            if (!_pending)
            {
                return false;
            }

            _pending = false;
            return true;
        }
    }

    /// <summary>Forces both motors to zero and marks the state as pending (stop the physical device).</summary>
    public void Reset() => Report(0, 0);
}
