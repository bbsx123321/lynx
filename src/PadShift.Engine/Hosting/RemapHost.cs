using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Core.Profile;

namespace PadShift.Engine.Hosting;

/// <summary>
/// Owns the polling loop and per-controller <see cref="RemapEngine"/> instances.
/// All state transitions are guarded by a single lock; <see cref="Tick"/>-sized work is
/// done inside that lock and kept short (the engine itself never touches I/O).
/// </summary>
public sealed class RemapHost : IRemapHost
{
    private sealed class ControllerRuntime
    {
        public Guid Id;
        public IInputSource Source = null!;
        public ControllerState State = new();
        public RemapEngine Engine = null!;
        public SlotHotkeyDetector Hotkeys = null!;
        public HostController Info = null!;
        public RemapState Current = RemapState.NothingApplied;
        public ShiftInfo[] Shifts = Array.Empty<ShiftInfo>();
        public int Slot;
        public int LoadedSlot = -1;
        public bool Announced;
        public readonly Dictionary<int, string> Applied = new();

        /// <summary>Reusable buffers filled at the end of every poll; never handed out directly.</summary>
        public readonly bool[] SnapshotButtons = new bool[256];

        /// <summary>Reusable axis buffer; see <see cref="SnapshotButtons"/>.</summary>
        public readonly int[] SnapshotAxes = new int[8];

        public long SnapshotTimestampMs;

        /// <summary>Copy of the last <see cref="RemapEngine.Tick"/> result, submitted outside the lock.</summary>
        public readonly OutputIntent Outbox = new();

        /// <summary>True while <see cref="Outbox"/> holds a frame that still has to be submitted.</summary>
        public bool PendingSubmit;

        /// <summary>Watchdog: start of the current one-second submission window.</summary>
        public long WindowStartMs;

        /// <summary>Watchdog: submissions counted in the current window.</summary>
        public int SubmitsInWindow;

        /// <summary>Watchdog: consecutive windows above the configured threshold.</summary>
        public int OverRateSeconds;
    }

    private readonly object _gate = new();
    private readonly List<IInputSource> _sources;
    private readonly IOutputSink _sink;
    private readonly HostOptions _options;
    private readonly IReadOnlySet<string> _excludedDeviceIds;
    private readonly ProfileLibrary _library;
    private readonly ProfileAutoSwitcher? _autoSwitcher;
    private readonly ProcessWatcher? _processWatcher;
    private readonly Dictionary<Guid, ControllerRuntime> _runtimes = new();
    private readonly Dictionary<string, Guid> _idByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HostEvent> _pendingEvents = new();

    /// <summary>Reusable snapshot of the runtimes that produced output in the current pump.</summary>
    private ControllerRuntime[] _submitBatch = new ControllerRuntime[8];

    private bool _remapEnabled;
    private bool _started;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _engineTicks;
    private long _outputSubmissions;
    private long _submitErrors;

    /// <summary>Diagnostics: completed poll iterations (the engine tick rate).</summary>
    public long EngineTicks => Interlocked.Read(ref _engineTicks);

    /// <summary>Diagnostics: <see cref="IOutputSink.Submit"/> calls made by this host generation.</summary>
    public long OutputSubmissions => Interlocked.Read(ref _outputSubmissions);

    /// <summary>Diagnostics: output submissions that threw and were swallowed by the poll loop.</summary>
    public long SubmitErrors => Interlocked.Read(ref _submitErrors);

    public RemapHost(IEnumerable<IInputSource> sources, IOutputSink sink, HostOptions? options = null)
        : this(sources, sink, options, null)
    {
    }

    /// <param name="processSource">
    /// Optional seam for the process watcher; <see langword="null"/> uses
    /// <see cref="SystemProcessSource"/>. Only used when <see cref="HostOptions.AutoSwitchEnabled"/> is set.
    /// </param>
    public RemapHost(
        IEnumerable<IInputSource> sources,
        IOutputSink sink,
        HostOptions? options,
        IProcessSource? processSource)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new List<IInputSource>(sources);
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new HostOptions();
        _excludedDeviceIds = _options.ExcludedDeviceIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _library = new ProfileLibrary(_options.ProfilesRoot);

        if (_options.AutoSwitchEnabled)
        {
            _autoSwitcher = new ProfileAutoSwitcher(this, _library);
            _autoSwitcher.Applied += OnAutoSwitchApplied;
            _processWatcher = new ProcessWatcher(processSource ?? new SystemProcessSource(), _options.AutoSwitchPollMs);
            _autoSwitcher.Attach(_processWatcher);
        }

        lock (_gate)
        {
            foreach (IInputSource source in _sources)
            {
                if (IsExcluded(source.DeviceId))
                {
                    continue;
                }

                Guid id = IdFor(source.DeviceId);
                if (!_runtimes.ContainsKey(id))
                {
                    _runtimes[id] = CreateRuntimeLocked(id, source);
                }
            }
        }
    }

    public event EventHandler<HostEvent>? Event;

    public bool RemapEnabled
    {
        get
        {
            lock (_gate)
            {
                return _remapEnabled;
            }
        }
    }

    public IReadOnlyList<HostController> Controllers
    {
        get
        {
            lock (_gate)
            {
                if (_runtimes.Count == 0)
                {
                    return Array.Empty<HostController>();
                }

                var list = new List<HostController>(_runtimes.Count);
                foreach (ControllerRuntime runtime in _runtimes.Values)
                {
                    if (runtime.Source.IsConnected)
                    {
                        list.Add(runtime.Info);
                    }
                }

                return list;
            }
        }
    }

    /// <summary>Profiles discovered under <see cref="HostOptions.ProfilesRoot"/>.</summary>
    public IReadOnlyList<string> Profiles => _library.Profiles;

    /// <summary>
    /// Rescan <see cref="HostOptions.ProfilesRoot"/>. The auto-switch process index notices the
    /// new <see cref="ProfileLibrary.Revision"/> and rebuilds itself on the next lookup.
    /// </summary>
    public void RefreshProfiles() => _library.Refresh();

    /// <summary>Process watcher, or <see langword="null"/> when auto-switching is disabled.</summary>
    public ProcessWatcher? Watcher => _processWatcher;

    /// <summary>Process → profile auto-switcher, or <see langword="null"/> when disabled.</summary>
    public ProfileAutoSwitcher? AutoSwitcher => _autoSwitcher;

    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _cts = new CancellationTokenSource();
            cts = _cts;
        }

        _loop = Task.Run(() => RunAsync(cts.Token));

        if (_processWatcher is not null)
        {
            try
            {
                _processWatcher.Start();
            }
            catch (Exception)
            {
                // Auto-switching is optional: a failing watcher must not stop remapping.
            }
        }
    }

    public void Stop()
    {
        try
        {
            _processWatcher?.Stop();
        }
        catch (Exception)
        {
            // Shutting down.
        }

        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            _started = false;
            cts = _cts;
            _cts = null;
            loop = _loop;
            _loop = null;
        }

        cts?.Cancel();
        if (loop is not null)
        {
            try
            {
                loop.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException)
            {
                // Cancellation is expected.
            }
        }

        cts?.Dispose();

        lock (_gate)
        {
            foreach (ControllerRuntime runtime in _runtimes.Values)
            {
                runtime.Engine.Reset();
            }
        }

        try
        {
            _sink.ReleaseAll();
        }
        catch (Exception)
        {
            // Releasing keys is best-effort during shutdown.
        }
    }

    public RemapState GetRemapState(Guid controllerId)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime)
                ? runtime.Current
                : RemapState.NothingApplied;
        }
    }

    public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime)
                ? runtime.Shifts
                : Array.Empty<ShiftInfo>();
        }
    }

    public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot)
    {
        if (_disposed)
        {
            Log($"ApplyConfigAsync: host already disposed (controller {controllerId}, slot {slot}).");
            return Task.FromResult(new ApplyConfigResult(false, "host disposed", slot));
        }

        if (slot < 0 || slot >= _options.MaxSlots)
        {
            return Task.FromResult(new ApplyConfigResult(false, $"slot must be 0..{_options.MaxSlots - 1}", slot));
        }

        string? resolved = _library.Resolve(profilePath);
        if (resolved is null)
        {
            return Task.FromResult(new ApplyConfigResult(false, $"profile not found: {profilePath}", slot));
        }

        GamepadProfile profile;
        try
        {
            profile = ProfileIo.Load(resolved);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Task.FromResult(new ApplyConfigResult(false, ex.Message, slot));
        }

        // Compile outside the lock so an in-flight tick is not delayed by profile parsing.
        var engine = NewEngine();

        ShiftInfo[] shifts;
        try
        {
            engine.LoadProfile(profile);
            shifts = BuildShifts(profile);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ApplyConfigResult(false, ex.Message, slot));
        }

        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime))
            {
                return Task.FromResult(new ApplyConfigResult(false, "unknown controller", slot));
            }

            AttachLayerEvents(runtime, engine);
            runtime.Engine = engine;
            runtime.Applied[slot] = resolved;
            runtime.Slot = slot;
            runtime.LoadedSlot = slot;
            runtime.Shifts = shifts;
            runtime.Current = RemapState.RemapOn;
            ResetWatchdog(runtime);
            _remapEnabled = true;

            DateTimeOffset at = DateTimeOffset.UtcNow;
            _pendingEvents.Add(new ConfigAppliedEvent(controllerId, resolved, slot, at));
            _pendingEvents.Add(new SlotChangedEvent(controllerId, slot, at));
            _pendingEvents.Add(new RemapStateChangedEvent(controllerId, RemapState.RemapOn, at));
        }

        DrainEvents();
        return Task.FromResult(new ApplyConfigResult(true, null, slot));
    }

    public EnableRemapResult EnableRemap(Guid? controllerId)
    {
        if (_disposed)
        {
            Log("EnableRemap: host already disposed; refusing to enable.");
            return new EnableRemapResult(false, "host disposed");
        }

        EnableRemapResult result;
        lock (_gate)
        {
            if (controllerId is not { } id)
            {
                _remapEnabled = true;
                int enabled = 0;
                foreach (ControllerRuntime runtime in _runtimes.Values)
                {
                    if (runtime.Applied.Count == 0)
                    {
                        continue;
                    }

                    runtime.Current = RemapState.RemapOn;
                    ResetWatchdog(runtime);
                    enabled++;
                    _pendingEvents.Add(new RemapStateChangedEvent(runtime.Id, runtime.Current, DateTimeOffset.UtcNow));
                }

                if (enabled == 0)
                {
                    Log("EnableRemap: global enable requested but no controller has an applied config; nothing is remapped.");
                }

                result = new EnableRemapResult(true, null);
            }
            else if (!_runtimes.TryGetValue(id, out ControllerRuntime? target))
            {
                Log($"EnableRemap: unknown controller {id} (known runtimes: {_runtimes.Count}).");
                result = new EnableRemapResult(false, "unknown controller");
            }
            else if (target.Applied.Count == 0)
            {
                Log($"EnableRemap: controller {id} has no applied config (current slot {target.Slot}); " +
                    "apply a profile to a slot before enabling remap.");
                result = new EnableRemapResult(false, "no profile applied");
            }
            else
            {
                _remapEnabled = true;
                target.Current = RemapState.RemapOn;
                ResetWatchdog(target);
                _pendingEvents.Add(new RemapStateChangedEvent(id, RemapState.RemapOn, DateTimeOffset.UtcNow));
                result = new EnableRemapResult(true, null);
            }
        }

        DrainEvents();
        return result;
    }

    public void DisableRemap(Guid? controllerId)
    {
        lock (_gate)
        {
            if (controllerId is not { } id)
            {
                _remapEnabled = false;
                foreach (ControllerRuntime runtime in _runtimes.Values)
                {
                    if (runtime.Applied.Count == 0)
                    {
                        continue;
                    }

                    runtime.Current = RemapState.RemapOff;
                    runtime.Engine.Reset();
                    ResetWatchdog(runtime);
                    _pendingEvents.Add(new RemapStateChangedEvent(runtime.Id, RemapState.RemapOff, DateTimeOffset.UtcNow));
                }
            }
            else if (_runtimes.TryGetValue(id, out ControllerRuntime? target))
            {
                target.Current = target.Applied.Count == 0 ? RemapState.NothingApplied : RemapState.RemapOff;
                target.Engine.Reset();
                ResetWatchdog(target);
                _pendingEvents.Add(new RemapStateChangedEvent(id, target.Current, DateTimeOffset.UtcNow));

                // The global flag is what the UI/tray/DeviceSession read: turning off the last
                // remapping controller must really report "remap disabled".
                RecomputeRemapEnabledLocked();
            }
        }

        try
        {
            _sink.ReleaseAll();
        }
        catch (Exception)
        {
            // Disabling remap must not throw just because the sink is gone.
        }

        DrainEvents();
    }

    public void SelectSlot(Guid controllerId, int slot)
    {
        if (slot < 0 || slot >= _options.MaxSlots)
        {
            return;
        }

        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime))
            {
                return;
            }

            runtime.Slot = slot;
            _pendingEvents.Add(new SlotChangedEvent(controllerId, slot, DateTimeOffset.UtcNow));
        }

        DrainEvents();
    }

    public int GetCurrentSlot(Guid controllerId)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime) ? runtime.Slot : 0;
        }
    }

    public bool SetLayer(Guid controllerId, int layer)
    {
        bool changed;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime))
            {
                return false;
            }

            changed = runtime.Engine.TrySetLayer(layer);
            if (!changed)
            {
                return false;
            }
        }

        // The engine raises LayerChanged from inside the call above; drain it outside the lock.
        DrainEvents();
        return true;
    }

    public int GetLayer(Guid controllerId)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime)
                ? runtime.Engine.CurrentLayer
                : 0;
        }
    }

    public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot)
    {
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime))
            {
                return Array.Empty<string>();
            }

            return runtime.Applied.TryGetValue(slot, out string? path) ? new[] { path } : Array.Empty<string>();
        }
    }

    public void ClearSlot(Guid controllerId, int slot)
    {
        if (slot < 0 || slot >= _options.MaxSlots)
        {
            return;
        }

        bool unloaded = false;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime))
            {
                return;
            }

            if (!runtime.Applied.Remove(slot))
            {
                return;
            }

            DateTimeOffset at = DateTimeOffset.UtcNow;
            if (runtime.Slot == slot || runtime.LoadedSlot == slot)
            {
                // The engine currently running belongs to the cleared slot: unload its profile but
                // keep the same engine instance so the layer-event wiring stays valid.
                runtime.Engine.LoadProfile(new GamepadProfile());
                runtime.Shifts = Array.Empty<ShiftInfo>();
                runtime.LoadedSlot = -1;
                unloaded = true;
            }

            _pendingEvents.Add(new SlotClearedEvent(controllerId, slot, at));

            if (runtime.Applied.Count == 0)
            {
                runtime.Current = RemapState.NothingApplied;
                runtime.Engine.Reset();
                _pendingEvents.Add(new RemapStateChangedEvent(controllerId, RemapState.NothingApplied, at));
                unloaded = true;

                bool anyApplied = false;
                foreach (ControllerRuntime other in _runtimes.Values)
                {
                    if (other.Applied.Count > 0)
                    {
                        anyApplied = true;
                        break;
                    }
                }

                if (!anyApplied)
                {
                    _remapEnabled = false;
                }
            }
        }

        if (unloaded)
        {
            try
            {
                _sink.ReleaseAll();
            }
            catch (Exception)
            {
                // Releasing keys is best-effort; the state has already been cleared.
            }
        }

        DrainEvents();
    }

    public bool IsKeyPressed(Guid controllerId, int scanCode)
    {
        if (scanCode < 0 || scanCode >= 256)
        {
            return false;
        }

        lock (_gate)
        {
            return _runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime)
                && runtime.State.Buttons[scanCode];
        }
    }

    /// <summary>
    /// Returns the last polled state for <paramref name="controllerId"/>. This is the copy taken at
    /// the end of the poll (not the live engine buffer), and the returned arrays are freshly copied
    /// from that reusable buffer so callers can never mutate engine state.
    /// </summary>
    public bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
    {
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(controllerId, out ControllerRuntime? runtime) ||
                !runtime.Source.IsConnected)
            {
                state = default;
                return false;
            }

            // Only two small arrays per request; the per-runtime buffers keep the poll path
            // allocation-free and the caller gets an unshared copy.
            var buttons = new bool[runtime.SnapshotButtons.Length];
            var axes = new int[runtime.SnapshotAxes.Length];
            Array.Copy(runtime.SnapshotButtons, buttons, buttons.Length);
            Array.Copy(runtime.SnapshotAxes, axes, axes.Length);
            state = new ControllerStateSnapshot(buttons, axes, Connected: true, runtime.SnapshotTimestampMs);
            return true;
        }
    }

    public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
    {
        lock (_gate)
        {
            if (!_runtimes.ContainsKey(controllerId))
            {
                return;
            }
        }

        // The sink interface has no duration parameter; duration is acknowledged but the sink
        // decides how long the motors stay on. Build the intent outside the gate and never hold
        // the gate across I/O: a slow rumble write must not freeze the UI/toggle.
        var intent = new OutputIntent
        {
            RumbleLeft = leftMotor,
            RumbleRight = rightMotor,
        };

        _ = durationMs;
        try
        {
            _sink.Submit(intent);
        }
        catch (Exception)
        {
            // Rumble is cosmetic.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch (Exception)
        {
            // Shutting down: nothing sensible to do with a failing sink.
        }

        try
        {
            _autoSwitcher?.Dispose();
        }
        catch (Exception)
        {
            // Auto-switching teardown is best-effort.
        }

        try
        {
            _processWatcher?.Dispose();
        }
        catch (Exception)
        {
        }

        foreach (IInputSource source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            _sink.Dispose();
        }
        catch (Exception)
        {
        }
    }

    // --------------------------------------------------------------- internals

    /// <summary>
    /// Diagnostics for the "why did the toggle fail?" report: written to the debugger output and to
    /// stderr instead of being swallowed into a generic UI message.
    /// </summary>
    private static void Log(string message)
    {
        string line = "[PadShift][RemapHost] " + message;
        Debug.WriteLine(line);
        Console.Error.WriteLine(line);
    }

    /// <summary>Test seam: runs one pump with an explicit clock, without waiting for the timer.</summary>
    internal void PumpForTest(long nowMs) => Pump(nowMs);

    /// <summary>Test seam: drains queued host events synchronously.</summary>
    internal void DrainEventsForTest() => DrainEvents();

    private async Task RunAsync(CancellationToken token)
    {
        int period = Math.Clamp(_options.TickMs, 1, 1000);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(period));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Pump(Environment.TickCount64);
                DrainEvents();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Pump(long nowMs)
    {
        int submitCount;

        // Phase 1: poll, tick and snapshot the intent under the gate. No output I/O happens while
        // the lock is held, so a slow Submit (ViGEm, SendInput) can never block Enable/Disable/UI.
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return;
            }

            Interlocked.Increment(ref _engineTicks);

            for (int i = 0; i < _sources.Count; i++)
            {
                IInputSource source = _sources[i];
                if (IsExcluded(source.DeviceId))
                {
                    continue;
                }

                Guid id = IdFor(source.DeviceId);

                if (!_runtimes.TryGetValue(id, out ControllerRuntime? runtime))
                {
                    if (!source.IsConnected)
                    {
                        continue;
                    }

                    runtime = CreateRuntimeLocked(id, source);
                    _runtimes[id] = runtime;
                }

                if (!runtime.Announced)
                {
                    runtime.Announced = true;
                    _pendingEvents.Add(new ControllerConnectedEvent(runtime.Info, DateTimeOffset.UtcNow));
                }

                try
                {
                    runtime.Source.TryPoll(runtime.State);
                }
                catch (Exception)
                {
                    // A broken device must not stop the other controllers.
                }

                CaptureSnapshotLocked(runtime, nowMs, _options.MaskPaddles);

                if (!source.IsConnected)
                {
                    _runtimes.Remove(id);
                    _pendingEvents.Add(new ControllerDisconnectedEvent(id, DateTimeOffset.UtcNow));
                }
            }

            submitCount = 0;
            foreach (ControllerRuntime runtime in _runtimes.Values)
            {
                // Slot hotkeys work even while remapping is disabled: they only change the slot.
                int hotkeySlot = runtime.Hotkeys.Detect(runtime.State, nowMs);
                if (hotkeySlot > 0)
                {
                    int index = hotkeySlot - 1;
                    if (index < _options.MaxSlots && index != runtime.Slot)
                    {
                        runtime.Slot = index;
                        _pendingEvents.Add(new SlotChangedEvent(runtime.Id, index, DateTimeOffset.UtcNow));
                    }
                }

                runtime.PendingSubmit = false;
                if (runtime.Current != RemapState.RemapOn)
                {
                    continue;
                }

                OutputIntent intent = runtime.Engine.Tick(runtime.State, nowMs);
                CopyIntent(intent, runtime.Outbox);
                runtime.PendingSubmit = true;

                if (submitCount == _submitBatch.Length)
                {
                    Array.Resize(ref _submitBatch, _submitBatch.Length * 2);
                }

                _submitBatch[submitCount++] = runtime;
            }
        }

        // Phase 2: submit outside the lock. The engine may be swapped by ApplyConfigAsync while
        // this runs; the outbox copy is already detached from engine state.
        for (int i = 0; i < submitCount; i++)
        {
            ControllerRuntime runtime = _submitBatch[i];
            _submitBatch[i] = null!;
            if (!runtime.PendingSubmit)
            {
                continue;
            }

            runtime.PendingSubmit = false;
            Interlocked.Increment(ref _outputSubmissions);
            try
            {
                _sink.Submit(runtime.Outbox);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _submitErrors);
                // A failing output backend must not kill the polling loop.
            }

            RecordSubmission(runtime, nowMs);
        }
    }

    /// <summary>Copies one engine intent into a runtime's reusable outbox (no allocations).</summary>
    private static void CopyIntent(OutputIntent source, OutputIntent destination)
    {
        Array.Copy(source.KeyboardKeys, destination.KeyboardKeys, source.KeyboardKeys.Length);
        Array.Copy(source.PadButtons, destination.PadButtons, source.PadButtons.Length);
        destination.MouseButtons = source.MouseButtons;
        destination.MouseDeltaX = source.MouseDeltaX;
        destination.MouseDeltaY = source.MouseDeltaY;
        destination.MouseWheel = source.MouseWheel;
        destination.PadLeftX = source.PadLeftX;
        destination.PadLeftY = source.PadLeftY;
        destination.PadRightX = source.PadRightX;
        destination.PadRightY = source.PadRightY;
        destination.PadLeftTrigger = source.PadLeftTrigger;
        destination.PadRightTrigger = source.PadRightTrigger;
        destination.RumbleLeft = source.RumbleLeft;
        destination.RumbleRight = source.RumbleRight;
    }

    /// <summary>Recomputes the global remap flag from the per-controller state. Caller holds the gate.</summary>
    private void RecomputeRemapEnabledLocked()
    {
        bool anyOn = false;
        foreach (ControllerRuntime runtime in _runtimes.Values)
        {
            if (runtime.Current == RemapState.RemapOn)
            {
                anyOn = true;
                break;
            }
        }

        _remapEnabled = anyOn;
    }

    /// <summary>Resets the per-controller watchdog window (called whenever remap is re-enabled).</summary>
    private static void ResetWatchdog(ControllerRuntime runtime)
    {
        runtime.WindowStartMs = 0;
        runtime.SubmitsInWindow = 0;
        runtime.OverRateSeconds = 0;
    }

    /// <summary>
    /// Counts one submitted frame and trips the runaway-output watchdog after
    /// <see cref="HostOptions.OutputLoopSeconds"/> consecutive one-second windows above
    /// <see cref="HostOptions.OutputLoopSubmissionsPerSecond"/>.
    /// </summary>
    private void RecordSubmission(ControllerRuntime runtime, long nowMs)
    {
        if (runtime.WindowStartMs == 0)
        {
            runtime.WindowStartMs = nowMs;
        }

        runtime.SubmitsInWindow++;

        long elapsed = nowMs - runtime.WindowStartMs;
        if (elapsed < 1000)
        {
            return;
        }

        int threshold = Math.Max(1, _options.OutputLoopSubmissionsPerSecond);
        if (runtime.SubmitsInWindow > threshold)
        {
            runtime.OverRateSeconds++;
        }
        else
        {
            runtime.OverRateSeconds = 0;
        }

        int submissions = runtime.SubmitsInWindow;
        runtime.WindowStartMs = nowMs;
        runtime.SubmitsInWindow = 0;

        if (runtime.OverRateSeconds >= Math.Max(1, _options.OutputLoopSeconds))
        {
            TripWatchdog(runtime, submissions);
        }
    }

    /// <summary>Disables remapping for one controller and tells the UI why.</summary>
    private void TripWatchdog(ControllerRuntime runtime, int submissions)
    {
        bool tripped = false;
        lock (_gate)
        {
            if (runtime.Current != RemapState.RemapOn)
            {
                return;
            }

            runtime.Current = RemapState.RemapOff;
            runtime.Engine.Reset();
            runtime.OverRateSeconds = 0;
            runtime.SubmitsInWindow = 0;
            runtime.WindowStartMs = 0;
            runtime.PendingSubmit = false;

            _pendingEvents.Add(new RemapStateChangedEvent(runtime.Id, RemapState.RemapOff, DateTimeOffset.UtcNow));
            _pendingEvents.Add(new OutputLoopDetectedEvent(runtime.Id, submissions, DateTimeOffset.UtcNow));

            bool anyOn = false;
            foreach (ControllerRuntime other in _runtimes.Values)
            {
                if (other.Current == RemapState.RemapOn)
                {
                    anyOn = true;
                    break;
                }
            }

            _remapEnabled = anyOn;
            tripped = true;
        }

        if (!tripped)
        {
            return;
        }

        WatchdogLog(
            $"controller {runtime.Id} submitted {submissions} outputs/s for " +
            $"{Math.Max(1, _options.OutputLoopSeconds)}s (threshold {_options.OutputLoopSubmissionsPerSecond}); " +
            "auto-disabling remap for this controller.");

        try
        {
            _sink.ReleaseAll();
        }
        catch (Exception)
        {
            // Releasing keys is best effort; the state has already been disabled.
        }
    }

    private static void WatchdogLog(string message)
    {
        string line = "[PadShift][watchdog] " + message;
        Debug.WriteLine(line);
        Console.Error.WriteLine(line);
    }

    private RemapEngine NewEngine() => new(new RemapOptions
    {
        MaxLayers = _options.MaxLayers,
        TickMs = _options.TickMs,
        DeviceId = _options.DeviceId,
    });

    /// <summary>Copies the just-polled state into the runtime's reusable public snapshot buffers.</summary>
    private static void CaptureSnapshotLocked(ControllerRuntime runtime, long nowMs, bool maskPaddles)
    {
        Array.Copy(runtime.State.Buttons, runtime.SnapshotButtons, runtime.SnapshotButtons.Length);
        if (maskPaddles)
        {
            runtime.SnapshotButtons[PadShift.Core.ButtonIds.Paddle1] = false;
            runtime.SnapshotButtons[PadShift.Core.ButtonIds.Paddle2] = false;
            runtime.SnapshotButtons[PadShift.Core.ButtonIds.Paddle3] = false;
            runtime.SnapshotButtons[PadShift.Core.ButtonIds.Paddle4] = false;
        }
        Array.Copy(runtime.State.Axes, runtime.SnapshotAxes, runtime.SnapshotAxes.Length);
        runtime.SnapshotTimestampMs = nowMs;
    }

    private ControllerRuntime CreateRuntimeLocked(Guid id, IInputSource source)
    {
        var runtime = new ControllerRuntime
        {
            Id = id,
            Source = source,
            Engine = NewEngine(),
            Hotkeys = new SlotHotkeyDetector(_options.SlotHotkeys),
            Info = new HostController(
                id,
                source.DeviceId,
                "gamepad",
                "usb",
                0,
                0,
                HasGyro: true,
                HasTouchpad: false,
                HasRumble: true,
                BatteryPercent: 100),
        };

        AttachLayerEvents(runtime, runtime.Engine);
        return runtime;
    }

    private void AttachLayerEvents(ControllerRuntime runtime, RemapEngine engine)
    {
        engine.LayerChanged += (layer, reason) =>
        {
            lock (_gate)
            {
                _pendingEvents.Add(new ShiftChangedEvent(runtime.Id, layer, LayerName(runtime, layer, reason), DateTimeOffset.UtcNow));
            }
        };
    }

    private static string LayerName(ControllerRuntime runtime, int layer, string reason)
    {
        for (int i = 0; i < runtime.Shifts.Length; i++)
        {
            if (runtime.Shifts[i].Shift == layer)
            {
                return string.IsNullOrWhiteSpace(runtime.Shifts[i].Name) ? reason : runtime.Shifts[i].Name;
            }
        }

        return layer == 0 ? "主层" : reason;
    }

    private bool IsExcluded(string deviceId) =>
        _excludedDeviceIds.Count > 0 && _excludedDeviceIds.Contains(deviceId);

    private Guid IdFor(string deviceId)
    {        if (_idByDevice.TryGetValue(deviceId, out Guid id))
        {
            return id;
        }

        // Deterministic id: DeviceSession rebuilds the host on hot-plug, and Guid.NewGuid() gave the
        // same physical device a new id each time. The UI (and the HTTP API) keep the old id, so
        // EnableRemap/GetAppliedConfigs were answered with "unknown controller" after a rebuild.
        // Hashing the stable device id keeps the controller's identity across host generations.
        id = StableControllerId(deviceId);
        _idByDevice[deviceId] = id;
        return id;
    }

    private static Guid StableControllerId(string deviceId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static ShiftInfo[] BuildShifts(GamepadProfile profile)
    {
        var list = new List<ShiftInfo>();
        foreach (ShiftRule? shift in profile.Shifts ?? new List<ShiftRule>())
        {
            if (shift is null || shift.Id is < 0 or > int.MaxValue)
            {
                continue;
            }

            list.Add(new ShiftInfo((int)shift.Id, shift.Description ?? $"Layer {shift.Id}"));
        }

        return list.ToArray();
    }

    private void OnAutoSwitchApplied(object? sender, AutoSwitchAppliedEvent applied)
    {
        lock (_gate)
        {
            _pendingEvents.Add(applied);
        }

        DrainEvents();
    }

    private void DrainEvents()
    {
        HostEvent[] batch;
        lock (_gate)
        {
            if (_pendingEvents.Count == 0)
            {
                return;
            }

            batch = _pendingEvents.ToArray();
            _pendingEvents.Clear();
        }

        EventHandler<HostEvent>? handler = Event;
        if (handler is null)
        {
            return;
        }

        foreach (HostEvent hostEvent in batch)
        {
            handler(this, hostEvent);
        }
    }
}
