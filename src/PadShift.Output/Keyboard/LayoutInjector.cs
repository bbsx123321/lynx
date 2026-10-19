using PadShift.Core;

namespace PadShift.Output.Keyboard;

/// <summary>
/// Base class for keyboard/mouse output backends. The base class owns the transition tracking:
/// subclasses only turn individual "key/button changed" and "mouse moved" events into native
/// input, and flush once per frame.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Submit"/> diffs the frame against the previous frame, so a held key produces exactly
/// one down event and one up event instead of one down event per poll. Mouse movement is the one
/// exception: <see cref="OutputIntent.MouseDeltaX"/>/<see cref="OutputIntent.MouseDeltaY"/> are
/// per-frame deltas and are forwarded verbatim (no smoothing, no accumulation inside the sink -
/// the engine owns accumulation).
/// </para>
/// <para>
/// <see cref="ReleaseAll"/> lifts every key and button the sink believes is down. Call it before
/// disabling mappings, on focus loss, on shutdown and from <see cref="Dispose"/>.
/// </para>
/// <para>
/// The hot path is allocation-free: implementations reuse per-instance buffers and never throw.
/// Native failures are recorded in counters/exposed state instead of exceptions.
/// </para>
/// </remarks>
public abstract class LayoutInjector : IOutputSink
{
    private const int MouseButtonCount = 5;

    private readonly bool[] _keysDown = new bool[256];
    private readonly object _gate = new();
    private int _mouseButtonsDown;
    private bool _disposed;

    /// <summary>Guards <see cref="Submit"/> and <see cref="ReleaseAll"/>; subclasses may lock it too.</summary>
    protected object SyncRoot => _gate;

    /// <summary>Human-readable backend name, for diagnostics/UI.</summary>
    public abstract string BackendName { get; }

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

    /// <summary>Number of keyboard keys currently believed to be held down.</summary>
    public int KeysDown
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (bool down in _keysDown)
                {
                    if (down)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>Bitmask of mouse buttons currently believed to be held down.</summary>
    public int MouseButtonsDown
    {
        get
        {
            lock (_gate)
            {
                return _mouseButtonsDown;
            }
        }
    }

    /// <summary>
    /// Creates the preferred backend: Interception when <c>Interception.dll</c> is next to the
    /// executable and a context can be opened, otherwise the SendInput backend.
    /// </summary>
    public static LayoutInjector CreateDefault(bool preferInterception = true)
    {
        if (preferInterception && InterceptionInjector.TryCreate(out InterceptionInjector? interception))
        {
            return interception;
        }

        return new SendInputInjector();
    }

    /// <inheritdoc />
    public void Submit(OutputIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            for (int key = 0; key < _keysDown.Length; key++)
            {
                bool want = intent.KeyboardKeys[key];
                if (want == _keysDown[key])
                {
                    continue;
                }

                _keysDown[key] = want;
                OnKey(key, want);
            }

            int buttons = intent.MouseButtons & ScanCode.Mouse.All;
            int changedButtons = buttons ^ _mouseButtonsDown;
            if (changedButtons != 0)
            {
                for (int bit = 0; bit < MouseButtonCount; bit++)
                {
                    int mask = 1 << bit;
                    if ((changedButtons & mask) == 0)
                    {
                        continue;
                    }

                    OnMouseButton(mask, (buttons & mask) != 0);
                }

                _mouseButtonsDown = buttons;
            }

            if (intent.MouseDeltaX != 0 || intent.MouseDeltaY != 0)
            {
                OnMouseMove(intent.MouseDeltaX, intent.MouseDeltaY);
            }

            if (intent.MouseWheel != 0)
            {
                OnMouseWheel(intent.MouseWheel);
            }

            Flush();
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

            ReleaseAllCore();
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

            try
            {
                ReleaseAllCore();
            }
            catch
            {
                // Releasing is best effort: never let shutdown fail because a backend went away.
            }

            _disposed = true;

            try
            {
                OnDispose();
            }
            catch
            {
                // Likewise, backend teardown must not throw out of Dispose.
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>true when a key index is tracked as down (diagnostics/tests).</summary>
    public bool IsKeyDown(int key)
    {
        lock (_gate)
        {
            return ScanCode.IsValid(key) && _keysDown[key];
        }
    }

    /// <summary>Called for each key transition. Implementations append one native event.</summary>
    protected abstract void OnKey(int scanCode, bool down);

    /// <summary>
    /// Called for each mouse-button transition. <paramref name="buttonMask"/> is one of
    /// <see cref="ScanCode.Mouse"/>.
    /// </summary>
    protected abstract void OnMouseButton(int buttonMask, bool down);

    /// <summary>Called once per frame when either relative delta is non-zero.</summary>
    protected abstract void OnMouseMove(int deltaX, int deltaY);

    /// <summary>Called once per frame when the wheel delta is non-zero. Units are <c>WHEEL_DELTA</c>.</summary>
    protected abstract void OnMouseWheel(int delta);

    /// <summary>Sends everything queued since the previous call. Must be cheap when nothing is queued.</summary>
    protected abstract void Flush();

    /// <summary>Releases any additional backend resources (driver context, handles, ...).</summary>
    protected virtual void OnDispose()
    {
    }

    private void ReleaseAllCore()
    {
        for (int key = 0; key < _keysDown.Length; key++)
        {
            if (!_keysDown[key])
            {
                continue;
            }

            _keysDown[key] = false;
            OnKey(key, false);
        }

        if (_mouseButtonsDown != 0)
        {
            int down = _mouseButtonsDown;
            _mouseButtonsDown = 0;

            for (int bit = 0; bit < MouseButtonCount; bit++)
            {
                int mask = 1 << bit;
                if ((down & mask) != 0)
                {
                    OnMouseButton(mask, false);
                }
            }
        }

        Flush();
    }
}
