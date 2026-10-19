using System.Linq;
using PadShift.App.Infrastructure;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Engine.Hosting;
using PadShift.Input.Devices;
using PadShift.Input.XInput;
using PadShift.Output;
using PadShift.Output.Keyboard;
using PadShift.Output.VirtualPad;

namespace PadShift.App.Services;

/// <summary>
/// Owns the live pieces of the client: the <see cref="DeviceWatcher"/> that discovers controllers,
/// the output sinks (<see cref="SendInputInjector"/> plus an optional ViGEm pad) and the
/// <see cref="RemapHost"/> that runs the engine.
/// </summary>
/// <remarks>
/// <para>
/// The watcher keeps polling for as long as the process lives, but <see cref="RemapHost"/> snapshots
/// its input sources in the constructor. Plugging a device in therefore rebuilds the host (debounced)
/// and re-applies the last profile that was applied to the current slot. The façade below keeps the
/// object identity stable for the HTTP API and the UI, so a rebuild never invalidates a caller.
/// </para>
/// <para>
/// Input sources are wrapped in <see cref="SourceHandle"/> because <see cref="RemapHost.Dispose"/>
/// disposes its sources; the watcher still owns (and reuses) the real ones.
/// </para>
/// </remarks>
public sealed class DeviceSession : IRemapHost
{
    private const int RebuildDebounceMs = 750;
    private const int SlotCount = 4;
    private const int TickMs = 4;

    private readonly object _gate = new();
    private readonly object _inputGate = new();
    private readonly DeviceWatcher _watcher;
    private readonly string _profilesRoot;
    private readonly bool _autoSwitchEnabled;
    private readonly IReadOnlyList<SlotHotkey> _slotHotkeys;
    private readonly System.Threading.Timer _rebuildTimer;
    private readonly string?[] _slotProfiles = new string?[SlotCount];
    private readonly Func<IReadOnlyList<IInputSource>>? _sourceProvider;
    private readonly bool _virtualPadEnabled = true;

    private IRemapHost? _inner;
    private int _slot;
    private bool _remapWanted;
    private bool _started;
    private bool _starting;
    private bool _disposed;
    private int _building;
    private int _rebuildPending;
    private int _deviceSetDirty;
    private string? _padProblem;
    private string? _sessionProblem;
    private ControllerSnapshot _latestInput = ControllerSnapshot.None;
    private GyroBias? _gyroBias;
    private OutputTuning _tuning;

    private SendInputInjector? _keyboard;
    private Xbox360PadSink? _pad;
    private bool _outputsCreated;
    private int _virtualPadSlot = -1;
    private int[]? _slotsBeforePad;

    private long _retiredEngineTicks;
    private long _retiredOutputSubmissions;

    public DeviceSession(
        string profilesRoot,
        bool autoSwitchEnabled = false,
        IReadOnlyList<SlotHotkey>? slotHotkeys = null,
        GyroBias? gyroBias = null,
        bool createVirtualPad = true,
        OutputTuning outputTuning = default)
    {
        _tuning = outputTuning.TickMs == 0 ? OutputTuning.Default : outputTuning;
        _profilesRoot = profilesRoot;
        _autoSwitchEnabled = autoSwitchEnabled;
        _slotHotkeys = slotHotkeys ?? Array.Empty<SlotHotkey>();
        _gyroBias = gyroBias?.Clone();
        _virtualPadEnabled = createVirtualPad;
        _watcher = new DeviceWatcher();
        _watcher.SourceAdded += OnSourceChanged;
        _watcher.SourceRemoved += OnSourceChanged;
        _rebuildTimer = new System.Threading.Timer(OnRebuildTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Test seam: a session backed by explicit sources instead of the hardware watcher. Used by the
    /// regression tests that simulate a hot-plug rebuild without touching real devices or ViGEm.
    /// </summary>
    internal DeviceSession(
        string profilesRoot,
        Func<IReadOnlyList<IInputSource>> sourceProvider,
        bool createVirtualPad)
        : this(profilesRoot)
    {
        _sourceProvider = sourceProvider ?? throw new ArgumentNullException(nameof(sourceProvider));
        _virtualPadEnabled = createVirtualPad;
    }

    public event EventHandler<HostEvent>? Event;

    /// <summary>Non-blocking message for the UI (missing ViGEmBus, host rebuild failure, ...).</summary>
    public string? Banner { get; private set; }

    /// <summary>True when an Xbox 360 pad could be created (ViGEmBus present).</summary>
    public bool VirtualPadAvailable { get; private set; }

    /// <summary>
    /// Diagnostics: completed poll iterations across every host generation this session built.
    /// </summary>
    public long EngineTicks =>
        Volatile.Read(ref _retiredEngineTicks) + ((Inner as RemapHost)?.EngineTicks ?? 0);

    /// <summary>Diagnostics: output submissions across every host generation.</summary>
    public long OutputSubmissions =>
        Volatile.Read(ref _retiredOutputSubmissions) + ((Inner as RemapHost)?.OutputSubmissions ?? 0);

    /// <summary>Diagnostics: the device ids the watcher currently considers valid inputs.</summary>
    public IReadOnlyList<string> InputSourceIds
    {
        get
        {
            try
            {
                IReadOnlyList<IInputSource> sources = _sourceProvider is null
                    ? _watcher.Sources
                    : _sourceProvider();
                var ids = new string[sources.Count];
                for (int i = 0; i < sources.Count; i++)
                {
                    ids[i] = sources[i].DeviceId;
                }

                return ids;
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }

    public bool RemapEnabled => Inner?.RemapEnabled ?? false;

    /// <summary>
    /// Latest controller poll, refreshed by the host's input path. Read by the radial menu (trigger
    /// buttons + right stick) and the gyro wizard without polling the device twice.
    /// </summary>
    public ControllerSnapshot LatestInput
    {
        get
        {
            lock (_inputGate)
            {
                return _latestInput;
            }
        }
    }

    /// <summary>
    /// Gyro zero-bias currently subtracted from every sample before the engine sees it. Set by the
    /// calibration wizard through <see cref="SetGyroBias"/>.
    /// </summary>
    public GyroBias? GyroBias
    {
        get
        {
            lock (_inputGate)
            {
                return _gyroBias?.Clone();
            }
        }
    }

    /// <summary>
    /// Applies a new gyro bias immediately: the next poll subtracts it. Persisting it for the next
    /// start is the caller's job (<see cref="AppSettings.GyroBias"/>).
    /// </summary>
    public void SetGyroBias(GyroBias? bias)
    {
        lock (_inputGate)
        {
            _gyroBias = bias?.Clone();
        }
    }

    /// <summary>Fallback for hosts/UI that only need the right stick.</summary>
    public RadialStickState RightStick => LatestInput.RightStick;

    public IReadOnlyList<HostController> Controllers => Inner?.Controllers ?? Array.Empty<HostController>();

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        // Output sinks live for the whole session: creating a new ViGEm pad on every host rebuild
        // made the watcher discover the pad again, which rebuilt the host again (a live loop).
        EnsureOutputs();

        try
        {
            if (_sourceProvider is null)
            {
                // The watcher raises SourceAdded synchronously for the devices it finds right now;
                // those are already picked up by the BuildHost() below, so they must not schedule a
                // second (identical) host generation.
                _starting = true;
                try
                {
                    _watcher.Start();
                }
                finally
                {
                    _starting = false;
                }
            }
        }
        catch (Exception ex)
        {
            _sessionProblem = "手柄检测启动失败: " + ex.Message;
        }

        BuildHost();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
        }

        try
        {
            _watcher.Stop();
        }
        catch (Exception)
        {
            // Stopping the watcher is best effort.
        }

        IRemapHost? inner;
        lock (_gate)
        {
            inner = _inner;
            _inner = null;
        }

        if (inner is not null)
        {
            inner.Event -= OnInnerEvent;
            try
            {
                // Stop() releases every held key/button before the sinks go away.
                inner.Stop();
            }
            catch (Exception)
            {
                // Shutdown must not throw.
            }

            try
            {
                inner.Dispose();
            }
            catch (Exception)
            {
                // Likewise.
            }
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
            _rebuildTimer.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _watcher.SourceAdded -= OnSourceChanged;
            _watcher.SourceRemoved -= OnSourceChanged;
            _watcher.Dispose();
        }
        catch (Exception)
        {
        }

        IRemapHost? inner;
        lock (_gate)
        {
            inner = _inner;
            _inner = null;
        }

        if (inner is not null)
        {
            inner.Event -= OnInnerEvent;
            try
            {
                inner.Dispose();
            }
            catch (Exception)
            {
            }
        }

        Event = null;
        DisposeOutputs();
    }

    // ------------------------------------------------------------------ IRemapHost

    public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) =>
        Inner?.GetShifts(controllerId) ?? Array.Empty<ShiftInfo>();

    public RemapState GetRemapState(Guid controllerId) =>
        Inner?.GetRemapState(controllerId) ?? RemapState.NothingApplied;

    public async Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot)
    {
        IRemapHost? inner = Inner;
        if (inner is null)
        {
            return new ApplyConfigResult(false, "没有可用的手柄", slot);
        }

        ApplyConfigResult result = await inner.ApplyConfigAsync(controllerId, profilePath, slot).ConfigureAwait(false);
        if (result.Success)
        {
            lock (_gate)
            {
                if (slot is >= 0 and < SlotCount)
                {
                    _slotProfiles[slot] = profilePath;
                    _slot = slot;
                }

                _remapWanted = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Enables remapping on the current host generation. <see cref="RemapHost"/> derives controller
    /// ids from the device id, so the Guid the UI holds before a hot-plug rebuild still names the
    /// same controller afterwards and keeps working here.
    /// </summary>
    public EnableRemapResult EnableRemap(Guid? controllerId)
    {
        IRemapHost? inner = Inner;
        if (inner is null)
        {
            return new EnableRemapResult(false, "没有可用的手柄");
        }

        EnableRemapResult result = inner.EnableRemap(controllerId);
        if (result.Success)
        {
            lock (_gate)
            {
                _remapWanted = true;
            }

            // From here on the current input set is the frozen set.
            Interlocked.Exchange(ref _deviceSetDirty, 0);
        }

        return result;
    }

    public void DisableRemap(Guid? controllerId)
    {
        lock (_gate)
        {
            _remapWanted = false;
        }

        Inner?.DisableRemap(controllerId);

        // Apply any device change that was frozen while remapping was ON. Other controllers may
        // still be remapping, in which case the rebuild stays queued until the last one is off.
        if (Interlocked.Exchange(ref _deviceSetDirty, 0) != 0 && Inner?.RemapEnabled != true)
        {
            ScheduleRebuild();
        }
    }

    public void SelectSlot(Guid controllerId, int slot)
    {
        if (slot is >= 0 and < SlotCount)
        {
            lock (_gate)
            {
                _slot = slot;
            }
        }

        Inner?.SelectSlot(controllerId, slot);
    }

    public int GetCurrentSlot(Guid controllerId) => Inner?.GetCurrentSlot(controllerId) ?? 0;

    public bool SetLayer(Guid controllerId, int layer) => Inner?.SetLayer(controllerId, layer) ?? false;

    public int GetLayer(Guid controllerId) => Inner?.GetLayer(controllerId) ?? 0;

    public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) =>
        Inner?.GetAppliedConfigs(controllerId, slot) ?? Array.Empty<string>();

    public bool IsKeyPressed(Guid controllerId, int scanCode) =>
        Inner?.IsKeyPressed(controllerId, scanCode) ?? false;

    /// <summary>
    /// Delegates to the active <see cref="RemapHost"/> so the UI can draw the live pad. A host
    /// rebuilt after a device change simply serves the newest snapshot.
    /// </summary>
    public bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
    {
        IRemapHost? inner = Inner;
        if (inner is null)
        {
            state = default;
            return false;
        }

        try
        {
            return inner.TryGetState(controllerId, out state);
        }
        catch (Exception)
        {
            // The host may be disposed between the read above and the delegation.
            state = default;
            return false;
        }
    }

    public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs) =>
        Inner?.Vibrate(controllerId, leftMotor, rightMotor, durationMs);

    /// <inheritdoc />
    public string? OutputProblem
    {
        get
        {
            lock (_gate)
            {
                return _padProblem;
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Test seam: performs the same host rebuild a hot-plug debounce tick would perform.</summary>
    internal void RebuildHostForTest() => BuildHost();

    /// <summary>Test seam: raises the same notification the watcher raises on hot-plug.</summary>
    internal void NotifySourceChangedForTest() => OnSourceChangedCore();

    /// <summary>Test seam: true while a debounced hot-plug rebuild is waiting for its timer.</summary>
    internal bool RebuildPendingForTest => Volatile.Read(ref _rebuildPending) != 0;

    /// <summary>Test seam: true when a hot-plug event arrived while remap was ON and was frozen.</summary>
    internal bool DeviceSetDirtyForTest => Volatile.Read(ref _deviceSetDirty) != 0;

    /// <summary>Test seam: runs the debounce timer callback synchronously.</summary>
    internal void RunPendingRebuildForTest() => OnRebuildTimer(null);

    /// <summary>Number of host generations built; the regression test asserts a rebuild really ran.</summary>
    public int HostGeneration { get; private set; }

    private IRemapHost? Inner
    {
        get
        {
            lock (_gate)
            {
                return _inner;
            }
        }
    }

    private void OnInnerEvent(object? sender, HostEvent hostEvent) => Event?.Invoke(this, hostEvent);

    private void OnSourceChanged(object? sender, InputSourceEventArgs args) => OnSourceChangedCore();

    private void OnSourceChangedCore()
    {
        if (_disposed || _starting)
        {
            return;
        }

        // While remapping is ON the input set is frozen: accepting a device that appeared while
        // the engine is running is what allowed a (real or emulated) pad to join the loop. The
        // change is remembered and applied once remapping is disabled.
        if (Inner?.RemapEnabled == true)
        {
            Interlocked.Exchange(ref _deviceSetDirty, 1);
            return;
        }

        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        if (Interlocked.Exchange(ref _rebuildPending, 1) != 0)
        {
            return;
        }

        try
        {
            _rebuildTimer.Change(RebuildDebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void OnRebuildTimer(object? state)
    {
        Interlocked.Exchange(ref _rebuildPending, 0);
        if (_disposed)
        {
            return;
        }

        if (Inner?.RemapEnabled == true)
        {
            Interlocked.Exchange(ref _deviceSetDirty, 1);
            return;
        }

        try
        {
            BuildHost();
        }
        catch (Exception ex)
        {
            _sessionProblem = "刷新设备失败: " + ex.Message;
            UpdateBanner();
        }
    }

    private void BuildHost()
    {
        // A debounced tick can overlap a slow rebuild; only one host generation at a time.
        if (Interlocked.Exchange(ref _building, 1) != 0)
        {
            return;
        }

        try
        {
            BuildHostCore();
        }
        finally
        {
            Interlocked.Exchange(ref _building, 0);
        }
    }

    /// <summary>
    /// Creates the keyboard/mouse and ViGEm outputs once per session. Rebuilding them alongside the
    /// host made the freshly created pad look like a hot-plugged controller on every rebuild.
    /// </summary>
    private void EnsureOutputs()
    {
        if (Volatile.Read(ref _outputsCreated))
        {
            return;
        }

        lock (_gate)
        {
            if (_outputsCreated || _disposed)
            {
                return;
            }

            _outputsCreated = true;
        }

        try
        {
            _keyboard = new SendInputInjector();
        }
        catch (Exception ex)
        {
            _sessionProblem = "键盘/鼠标输出初始化失败: " + ex.Message;
        }

        if (!_virtualPadEnabled)
        {
            VirtualPadAvailable = false;
            UpdateBanner();
            return;
        }

        int[] before = XInputSource.ConnectedSlots();
        _slotsBeforePad = before;
        Xbox360PadSink? created = null;
        string? problem = null;

        try
        {
            if (Xbox360PadSink.TryCreate(out Xbox360PadSink? pad))
            {
                created = pad;
            }
            else
            {
                problem = "未检测到 ViGEmBus：虚拟手柄输出已停用，键盘/鼠标映射仍可用。安装 ViGEmBus 后重启程序即可启用。";
            }
        }
        catch (Exception ex)
        {
            problem = $"虚拟手柄不可用（{ex.Message}）：继续使用键盘/鼠标映射。";
        }

        _pad = created;
        VirtualPadAvailable = created is not null;
        _padProblem = problem;

        if (created is not null)
        {
            int slot = ResolveVirtualPadSlot(created, before);
            if (slot is >= 0 and <= 3)
            {
                _virtualPadSlot = slot;
                _watcher.IgnoreXInputSlot(slot);
            }
        }

        UpdateBanner();
    }

    /// <summary>
    /// The ViGEm user index is reported asynchronously; re-read it before every rebuild so a pad
    /// that became visible late is still excluded from the input set.
    /// </summary>
    private void SyncVirtualPadSlot()
    {
        Xbox360PadSink? pad = _pad;
        if (pad is null)
        {
            return;
        }

        int? reported = pad.XInputUserIndex;
        if (reported is int slot && slot != _virtualPadSlot)
        {
            _virtualPadSlot = slot;
            _watcher.IgnoreXInputSlot(slot);
            return;
        }

        if (_virtualPadSlot < 0 && _slotsBeforePad is { } before)
        {
            int resolved = ResolveVirtualPadSlot(pad, before);
            if (resolved is >= 0 and <= 3)
            {
                _virtualPadSlot = resolved;
                _watcher.IgnoreXInputSlot(resolved);
            }
        }
    }

    /// <summary>
    /// Slot the freshly created pad took. ViGEm only reports <see cref="Xbox360PadSink.XInputUserIndex"/>
    /// after the first report, so fall back to the XInput slot that appeared between the snapshots.
    /// </summary>
    private static int ResolveVirtualPadSlot(Xbox360PadSink pad, int[] before)
    {
        if (pad.XInputUserIndex is int reported)
        {
            return reported;
        }

        int[] after = XInputSource.ConnectedSlots();
        foreach (int slot in after)
        {
            if (Array.IndexOf(before, slot) < 0)
            {
                return slot;
            }
        }

        return -1;
    }

    private bool IsOwnVirtualOutput(string deviceId) =>
        _virtualPadSlot is >= 0 and <= 3 &&
        string.Equals(deviceId, "XInput#" + _virtualPadSlot.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private void DisposeOutputs()
    {
        SendInputInjector? keyboard;
        Xbox360PadSink? pad;

        lock (_gate)
        {
            keyboard = _keyboard;
            pad = _pad;
            _keyboard = null;
            _pad = null;
            _virtualPadSlot = -1;
        }

        try
        {
            keyboard?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown must not throw.
        }

        try
        {
            pad?.Dispose();
        }
        catch (Exception)
        {
            // The driver may already be gone.
        }

        try
        {
            _watcher.ClearIgnoredXInputSlots();
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private void BuildHostCore()
    {
        EnsureOutputs();
        SyncVirtualPadSlot();

        IReadOnlyList<IInputSource> discovered = _sourceProvider is null ? _watcher.Sources : _sourceProvider();
        IInputSource[] sources = discovered
            .Where(source => !IsOwnVirtualOutput(source.DeviceId))
            .Select(source => (IInputSource)new SourceHandle(source, this))
            .ToArray();
        HostGeneration++;

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_virtualPadSlot is >= 0 and <= 3)
        {
            excluded.Add("XInput#" + _virtualPadSlot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        IOutputSink composite = new CompositeSink(_keyboard, _pad, ownsSinks: false);
        if (_tuning.UdpEnabled)
        {
            composite = new MirroringSink(composite, new UdpMirrorSink(_tuning.UdpPort));
        }
        var host = new RemapHost(sources, composite, new HostOptions
        {
            ProfilesRoot = _profilesRoot,
            TickMs = _tuning.TickMs,
            MaskPaddles = _tuning.MaskPaddles,
            AutoSwitchEnabled = _autoSwitchEnabled,
            SlotHotkeys = _slotHotkeys,
            ExcludedDeviceIds = excluded,
        });
        host.Event += OnInnerEvent;

        IRemapHost? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                host.Dispose();
                return;
            }

            previous = _inner;
            _inner = host;

            if (previous is RemapHost retired)
            {
                _retiredEngineTicks += retired.EngineTicks;
                _retiredOutputSubmissions += retired.OutputSubmissions;
            }
        }

        host.Start();

        if (previous is not null)
        {
            previous.Event -= OnInnerEvent;
            try
            {
                previous.Dispose();
            }
            catch (Exception)
            {
                // The old host is already detached; a failing sink must not block the new one.
            }
        }

        Restore(host);
    }

    /// <summary>Re-applies the profile that was active before a device rebuild.</summary>
    private void Restore(RemapHost host)
    {
        string? path;
        int slot;
        bool remap;
        lock (_gate)
        {
            slot = _slot;
            path = _slotProfiles[slot];
            remap = _remapWanted;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            foreach (HostController controller in host.Controllers)
            {
                host.ApplyConfigAsync(controller.Id, path, slot).GetAwaiter().GetResult();
                if (remap)
                {
                    host.EnableRemap(controller.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _sessionProblem = "恢复上次配置失败: " + ex.Message;
            UpdateBanner();
        }
    }

    private void UpdateBanner()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(_sessionProblem))
        {
            parts.Add(_sessionProblem!);
        }

        if (!string.IsNullOrWhiteSpace(_padProblem))
        {
            parts.Add(_padProblem!);
        }

        Banner = parts.Count == 0 ? null : string.Join("  ", parts);
    }

    /// <summary>Copies the freshly polled state for the UI and subtracts the active gyro bias.</summary>
    private void PublishState(ControllerState state)
    {
        GyroBias? bias;
        lock (_inputGate)
        {
            bias = _gyroBias;
        }

        if (bias is not null && bias.IsValid)
        {
            state.GyroX -= (float)bias.X;
            state.GyroY -= (float)bias.Y;
            state.GyroZ -= (float)bias.Z;
        }

        ulong buttons = 0;
        int buttonLimit = Math.Min(state.Buttons.Length, 64);
        for (int button = 0; button < buttonLimit; button++)
        {
            if (state.Buttons[button])
            {
                buttons |= 1UL << button;
            }
        }

        var snapshot = new ControllerSnapshot(
            HasState: true,
            Buttons: buttons,
            RightStickX: state.Axes.Length > Axes.RightStickX ? state.Axes[Axes.RightStickX] : 0,
            RightStickY: state.Axes.Length > Axes.RightStickY ? state.Axes[Axes.RightStickY] : 0,
            GyroX: state.GyroX,
            GyroY: state.GyroY,
            GyroZ: state.GyroZ,
            CapturedAt: DateTimeOffset.UtcNow);

        lock (_inputGate)
        {
            _latestInput = snapshot;
        }
    }

    /// <summary>
    /// Pass-through input source whose <see cref="Dispose"/> is a no-op: the watcher owns the real
    /// source and must be able to hand the same device to the next host generation.
    /// </summary>
    private sealed class SourceHandle : IInputSource
    {
        private readonly IInputSource _source;
        private readonly DeviceSession _owner;

        public SourceHandle(IInputSource source, DeviceSession owner)
        {
            _source = source;
            _owner = owner;
        }

        public string DeviceId => _source.DeviceId;

        public bool IsConnected => _source.IsConnected;

        public bool TryPoll(ControllerState state)
        {
            if (!_source.TryPoll(state))
            {
                return false;
            }

            _owner.PublishState(state);
            return true;
        }

        public void Dispose()
        {
            // Intentionally empty; see the type summary.
        }
    }
}
