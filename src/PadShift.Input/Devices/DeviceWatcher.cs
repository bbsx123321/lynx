using HidSharp;
using PadShift.Core;
using PadShift.Input.Hid;
using PadShift.Input.XInput;

namespace PadShift.Input.Devices;

/// <summary>Event payload for <see cref="DeviceWatcher.SourceAdded"/> / <see cref="DeviceWatcher.SourceRemoved"/>.</summary>
public sealed class InputSourceEventArgs : EventArgs
{
    public InputSourceEventArgs(IInputSource source, bool added)
    {
        Source = source;
        Added = added;
    }

    /// <summary>The source that appeared or disappeared.</summary>
    public IInputSource Source { get; }

    /// <summary>True for an add event, false for a remove event.</summary>
    public bool Added { get; }
}

/// <summary>
/// Polls for controllers once per second (no WMI): XInput slots 0..3 plus the HidSharp HID device
/// list. New devices are opened and exposed as <see cref="IInputSource"/> instances; disappeared
/// devices are disposed and reported through <see cref="SourceRemoved"/>.
/// </summary>
/// <remarks>
/// The default interval is one second. Polling and events run on a thread-pool timer thread;
/// event handlers must not block that thread and should marshal UI work themselves.
/// <see cref="Sources"/> is a thread-safe snapshot. Call <see cref="Dispose"/> only after polling
/// of the exposed sources has stopped.
/// </remarks>
public sealed class DeviceWatcher : IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;
    private readonly Timer _timer;
    private readonly Dictionary<string, IInputSource> _xinputSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IInputSource> _hidSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _descriptorCache = new(StringComparer.Ordinal);
    private readonly HashSet<int> _ignoredXInputSlots = new();

    private bool _started;
    private bool _disposed;
    private int _polling;

    public DeviceWatcher()
        : this(DefaultPollInterval)
    {
    }

    public DeviceWatcher(TimeSpan pollInterval)
    {
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        }

        _pollInterval = pollInterval;
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised when a new controller is opened and added to <see cref="Sources"/>.</summary>
    public event EventHandler<InputSourceEventArgs>? SourceAdded;

    /// <summary>Raised when a controller disappears or reports a device error.</summary>
    public event EventHandler<InputSourceEventArgs>? SourceRemoved;

    /// <summary>Current sources, as a thread-safe snapshot.</summary>
    public IReadOnlyList<IInputSource> Sources
    {
        get
        {
            lock (_gate)
            {
                var snapshot = new List<IInputSource>(_xinputSources.Count + _hidSources.Count);

                for (int slot = 0; slot < 4; slot++)
                {
                    if (_xinputSources.TryGetValue(SlotKey(slot), out IInputSource? source))
                    {
                        snapshot.Add(source);
                    }
                }

                var hidIds = new List<string>(_hidSources.Keys);
                hidIds.Sort(StringComparer.Ordinal);
                foreach (string id in hidIds)
                {
                    snapshot.Add(_hidSources[id]);
                }

                return snapshot;
            }
        }
    }

    /// <summary>Starts periodic polling; performs an immediate scan synchronously.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _started = true;
        }

        PollOnce();
        _timer.Change(_pollInterval, _pollInterval);
    }

    /// <summary>Stops periodic polling. Existing sources stay open until <see cref="Dispose"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return;
            }

            _started = false;
        }

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Forces one scan on the calling thread (useful for tests and manual refresh).</summary>
    public void Refresh()
    {
        PollOnce();
    }

    /// <summary>
    /// Permanently ignores one XInput user slot for the lifetime of this watcher. The client uses
    /// this for the virtual pad it creates itself: the emulated pad shows up as a brand-new XInput
    /// controller, and accepting it as an input source feeds every remapped output straight back
    /// into the engine (a self-sustaining loop). An already-open source for the slot is dropped
    /// silently, without a <see cref="SourceRemoved"/> event, so no host rebuild is scheduled.
    /// </summary>
    public void IgnoreXInputSlot(int slot)
    {
        if (slot is < 0 or > 3)
        {
            return;
        }

        lock (_gate)
        {
            if (!_ignoredXInputSlots.Add(slot))
            {
                return;
            }

            if (_xinputSources.Remove(SlotKey(slot), out IInputSource? ignored))
            {
                SafeDispose(ignored);
            }
        }
    }

    /// <summary>Removes every XInput slot exclusion (the virtual pad is gone).</summary>
    public void ClearIgnoredXInputSlots()
    {
        lock (_gate)
        {
            _ignoredXInputSlots.Clear();
        }
    }

    /// <summary>Diagnostics: XInput slots this watcher refuses to use as input.</summary>
    public IReadOnlyList<int> IgnoredXInputSlots
    {
        get
        {
            lock (_gate)
            {
                var slots = new int[_ignoredXInputSlots.Count];
                _ignoredXInputSlots.CopyTo(slots);
                Array.Sort(slots);
                return slots;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var toDispose = new List<IInputSource>();
        bool shouldDispose = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;

            toDispose.AddRange(_xinputSources.Values);
            toDispose.AddRange(_hidSources.Values);
            _xinputSources.Clear();
            _hidSources.Clear();
            _descriptorCache.Clear();
            shouldDispose = true;
        }

        if (shouldDispose)
        {
            _timer.Dispose();

            foreach (IInputSource source in toDispose)
            {
                SafeDispose(source);
            }
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            PollOnce();
        }
        catch
        {
            // The watcher is a background service: it must never tear down the process.
        }
    }

    private void PollOnce()
    {
        // Timer ticks can overlap when an event handler is slow; only one scan at a time.
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            PollOnceCore();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void PollOnceCore()
    {
        var events = new List<InputSourceEventArgs>();

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                PollXInput(events);
                PollHid(events);
            }
        }
        catch
        {
            // Device enumeration can fail transiently (driver install, sleep/resume); ignore it
            // and try again on the next tick.
        }

        foreach (InputSourceEventArgs args in events)
        {
            try
            {
                if (args.Added)
                {
                    SourceAdded?.Invoke(this, args);
                }
                else
                {
                    SourceRemoved?.Invoke(this, args);
                }
            }
            catch
            {
                // A consumer event handler must not stop the watcher.
            }
        }
    }

    private void PollXInput(List<InputSourceEventArgs> events)
    {
        for (int slot = 0; slot < 4; slot++)
        {
            string key = SlotKey(slot);

            if (_ignoredXInputSlots.Contains(slot))
            {
                // Our own virtual pad: never an input source, and removing it must not raise an
                // event that would rebuild the host (that was the feedback loop).
                if (_xinputSources.Remove(key, out IInputSource? ignored))
                {
                    SafeDispose(ignored);
                }

                continue;
            }

            bool connected = XInputSource.IsSlotConnected(slot);

            if (!connected)
            {
                if (_xinputSources.Remove(key, out IInputSource? removed))
                {
                    events.Add(new InputSourceEventArgs(removed, added: false));
                    SafeDispose(removed);
                }

                continue;
            }

            if (_xinputSources.TryGetValue(key, out IInputSource? existing))
            {
                if (existing.IsConnected)
                {
                    continue;
                }

                _xinputSources.Remove(key);
                events.Add(new InputSourceEventArgs(existing, added: false));
                SafeDispose(existing);
            }

            var source = new XInputSource(slot);
            if (!source.IsConnected)
            {
                source.Dispose();
                continue;
            }

            _xinputSources[key] = source;
            events.Add(new InputSourceEventArgs(source, added: true));
        }
    }

    private void PollHid(List<InputSourceEventArgs> events)
    {
        List<HidDevice> devices;

        try
        {
            devices = new List<HidDevice>(DeviceList.Local.GetHidDevices());
        }
        catch
        {
            return;
        }

        var presentPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (HidDevice device in devices)
        {
            string path;

            try
            {
                path = device.DevicePath;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            presentPaths.Add(path);

            if (_hidSources.TryGetValue(path, out IInputSource? existing))
            {
                if (existing.IsConnected)
                {
                    continue;
                }

                _hidSources.Remove(path);
                events.Add(new InputSourceEventArgs(existing, added: false));
                SafeDispose(existing);
                continue;
            }

            if (!ShouldConsider(device, path))
            {
                continue;
            }

            if (!HidDeviceSource.TryCreate(device, out HidDeviceSource? source) || source is null)
            {
                continue;
            }

            _hidSources[path] = source;
            events.Add(new InputSourceEventArgs(source, added: true));
        }

        var removedPaths = new List<string>();
        foreach (string path in _hidSources.Keys)
        {
            if (!presentPaths.Contains(path))
            {
                removedPaths.Add(path);
            }
        }

        foreach (string path in removedPaths)
        {
            if (_hidSources.Remove(path, out IInputSource? source))
            {
                events.Add(new InputSourceEventArgs(source, added: false));
                SafeDispose(source);
            }
        }
    }

    private bool ShouldConsider(HidDevice device, string path)
    {
        // An Xbox 360 pad (physical, or a ViGEm pad we created) exposes both an XInput slot and a
        // HID interface. Enumerating both made the same pad tick twice — and, for our own virtual
        // pad, kept a second self-source alive even after its XInput slot was ignored. XInput is
        // the authoritative path for these devices.
        if (IsXbox360HidInterface(device))
        {
            return false;
        }

        if (ReportParserRegistry.IsKnownDevice(device))
        {
            return true;
        }

        if (_descriptorCache.TryGetValue(path, out bool cached))
        {
            return cached;
        }

        bool isGamepad = false;

        try
        {
            byte[]? rawDescriptor = device.GetRawReportDescriptor();
            if (rawDescriptor is { Length: > 0 })
            {
                isGamepad = ReportDescriptorParser.Parse(rawDescriptor).IsGamepad;
            }
        }
        catch
        {
            isGamepad = false;
        }

        _descriptorCache[path] = isGamepad;
        return isGamepad;
    }

    private static string SlotKey(int slot) => $"XInput#{slot}";

    /// <summary>Microsoft's wired/wireless Xbox 360 pad ids; always reachable through XInput.</summary>
    private static bool IsXbox360HidInterface(HidDevice device)
    {
        try
        {
            return device.VendorID == 0x045E && device.ProductID == 0x028E;
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDispose(IInputSource source)
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
