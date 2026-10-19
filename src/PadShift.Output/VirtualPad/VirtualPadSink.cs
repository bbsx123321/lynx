using PadShift.Core;

namespace PadShift.Output.VirtualPad;

/// <summary>
/// Implemented by output sinks that can receive rumble requests from the system (a game writing to
/// the emulated pad's output report). The event can be raised from the ViGEm notification thread.
/// </summary>
public interface IRumbleSource
{
    /// <summary>Raised with the large (left) and small (right) motor intensities, 0..255.</summary>
    event Action<byte, byte>? RumbleReceived;
}

/// <summary>
/// Base class for virtual gamepad backends. It owns the hot-path guard, change tracking helpers and
/// the lifecycle contract: <see cref="Submit"/> and <see cref="ReleaseAll"/> never throw, and
/// <see cref="Dispose"/> always disconnects the emulated pad.
/// </summary>
/// <remarks>
/// Concrete sinks diff the incoming <see cref="OutputIntent"/> against the state they last applied
/// and send exactly one report when something changed (a dirty flag), not one report per frame.
/// </remarks>
public abstract class VirtualPadSink : IOutputSink, IRumbleSource
{
    private readonly object _gate = new();
    private bool _disposed;
    private bool _faulted;
    private bool _connected;
    private Exception? _lastError;

    /// <summary>Guards sink state; subclasses may lock it for their own state too.</summary>
    protected object SyncRoot => _gate;

    /// <summary>Human-readable backend name, for diagnostics/UI.</summary>
    public abstract string BackendName { get; }

    /// <summary>
    /// true while the emulated pad is plugged in and the last submit succeeded. A false value after
    /// construction means the bus/pad went away; <see cref="LastError"/> explains why.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connected && !_disposed && !_faulted;
            }
        }
    }

    /// <summary>true when a runtime submit failed; the sink stops touching the bus until disposed.</summary>
    public bool IsFaulted
    {
        get
        {
            lock (_gate)
            {
                return _faulted;
            }
        }
    }

    /// <summary>true once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Last runtime failure, or null. Constructors throw instead (see <see cref="ViGEmNotInstalledException"/>).</summary>
    public Exception? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc />
    public event Action<byte, byte>? RumbleReceived;

    /// <inheritdoc />
    public void Submit(OutputIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        lock (_gate)
        {
            if (_disposed || _faulted)
            {
                return;
            }

            try
            {
                SubmitCore(intent);
            }
            catch (Exception ex)
            {
                // The hot path never throws: a vanished pad is a state transition, not an app crash.
                _lastError = ex;
                _faulted = true;
                _connected = false;
            }
        }
    }

    /// <inheritdoc />
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ReleaseAllCore();
            }
            catch (Exception ex)
            {
                _lastError = ex;
                _faulted = true;
                _connected = false;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                ReleaseAllCore();
            }
            catch (Exception ex)
            {
                _lastError = ex;
            }

            try
            {
                DisconnectCore();
            }
            catch (Exception ex)
            {
                _lastError = ex;
            }

            _connected = false;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Applies one frame. Called under the sink lock; exceptions are caught by <see cref="Submit"/>.</summary>
    protected abstract void SubmitCore(OutputIntent intent);

    /// <summary>Sends a neutral report. Called under the sink lock; exceptions are caught.</summary>
    protected abstract void ReleaseAllCore();

    /// <summary>Unplugs the pad and releases backend resources. Called under the sink lock.</summary>
    protected abstract void DisconnectCore();

    /// <summary>Marks the pad as connected/disconnected from the concrete constructor/teardown.</summary>
    protected void SetConnected(bool connected)
    {
        lock (_gate)
        {
            _connected = connected;
        }
    }

    /// <summary>
    /// Forwards a rumble request from the emulated pad. User handler exceptions are swallowed: this
    /// is called from the ViGEm notification thread and must not tear it down.
    /// </summary>
    protected void ReportRumble(byte largeMotor, byte smallMotor)
    {
        if (_disposed)
        {
            return;
        }

        Action<byte, byte>? handler = RumbleReceived;
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
            // An output notification must never break the ViGEm callback thread.
        }
    }
}
