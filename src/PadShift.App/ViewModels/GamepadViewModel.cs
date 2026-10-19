using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Infrastructure;
using PadShift.Core;
using PadShift.Core.Host;

namespace PadShift.App.ViewModels;

/// <summary>
/// The gamepad diagram plus its live state. Clickable controls keep fixed geometry in a 420x300
/// design space (the view scales them with a <c>Viewbox</c>); the live properties are refreshed
/// from <see cref="IRemapHost.TryGetState"/> by a <see cref="DispatcherTimer"/> at 20 Hz (10 Hz
/// after the adaptive safety valve opens).
/// </summary>
/// <remarks>
/// <para>
/// The timer never touches the engine on the UI thread: each tick reads the host snapshot on the
/// thread pool and posts the quantized result back to the dispatcher. When remapping is enabled the
/// engine's 4 ms loop holds its state lock while it injects output (SendInput / ViGEmBus), so a
/// synchronous <c>TryGetState</c> from the UI thread can block for a long time — that is exactly the
/// "LT freezes the desktop" failure mode this design removes.
/// </para>
/// <para>
/// Analog values are snapped to 1/64 before they are compared, so sub-pixel jitter does not raise
/// <c>PropertyChanged</c> at all. The view animates them with render transforms only.
/// </para>
/// </remarks>
public sealed class GamepadViewModel : ObservableObject, IDisposable
{
    /// <summary>Design-space size of the diagram. The view scales it with a Viewbox.</summary>
    public const double CanvasWidth = 420;

    public const double CanvasHeight = 300;

    /// <summary>Maximum thumb-cap travel in design pixels for a fully deflected stick.</summary>
    public const double StickTravelPixels = 10;

    /// <summary>Normal UI refresh period (20 Hz).</summary>
    public const int PollIntervalMs = UiPollThrottle.FastIntervalMs;

    /// <summary>Refresh period used after the adaptive safety valve opens (10 Hz).</summary>
    public const int SlowPollIntervalMs = UiPollThrottle.SlowIntervalMs;

    /// <summary>Analog quantization step: sticks and triggers both snap to 1/64.</summary>
    public const double AnalogQuantum = 1.0 / 64.0;

    /// <summary>Hint shown over the diagram when the host has no controller (localized live).</summary>
    public static string NoControllerHint => Loc.T("hint.nocontroller");

    private readonly UiPollThrottle _throttle = new();

    private DispatcherTimer? _timer;
    private SynchronizationContext? _uiContext;
    private IRemapHost? _host;
    private Guid? _controllerId;
    private bool _disposed;
    private bool _slowRateLogged;

    private GamepadControlViewModel? _selectedControl;

    private bool _hasController;
    private bool _a, _b, _x, _y;
    private bool _up, _down, _left, _right;
    private bool _lb, _rb, _ls, _rs;
    private bool _view, _menu, _guide;
    private bool _p1, _p2, _p3, _p4;
    private double _leftStickX, _leftStickY, _rightStickX, _rightStickY;
    private double _lt, _rt;

    public GamepadViewModel()
    {
        // Capture the thread that created the view model (normally the WPF UI thread) so poll
        // results are posted back even if StartLive is called before the dispatcher installed a
        // SynchronizationContext for the message loop.
        _uiContext = SynchronizationContext.Current;
        CreateControls();
        Select(Controls[0]);
    }

    public ObservableCollection<GamepadControlViewModel> Controls { get; } = new();

    /// <summary>Engine façade used for live polling; assigned by <see cref="MainViewModel"/>.</summary>
    public IRemapHost? Host => _host;

    /// <summary>Controller the diagram follows; null shows the neutral pad and the hint.</summary>
    public Guid? ControllerId
    {
        get => _controllerId;
        set => _controllerId = value;
    }

    public bool IsLive => _timer?.IsEnabled == true;

    /// <summary>Current UI refresh period: 50 ms normally, 100 ms after the safety valve opens.</summary>
    public int CurrentPollIntervalMs => _throttle.IntervalMs;

    /// <summary>Number of ticks dropped because the previous tick was still in flight.</summary>
    internal int DroppedTicks => _throttle.DroppedTicks;

    /// <summary>Adaptive refresh policy; exposed to the regression suite.</summary>
    internal UiPollThrottle PollThrottle => _throttle;

    /// <summary>Feeds a simulated tick duration through the same safety valve the timer uses.</summary>
    internal void SimulateTickCost(TimeSpan elapsed) => RecordTickCost(elapsed);

    // ------------------------------------------------------------------ live state

    /// <summary>True while <see cref="IRemapHost.TryGetState"/> returns a controller for the id.</summary>
    public bool HasController
    {
        get => _hasController;
        private set
        {
            if (SetProperty(ref _hasController, value))
            {
                OnPropertyChanged(nameof(ShowConnectionHint));
            }
        }
    }

    /// <summary>Bound to the hint overlay's visibility.</summary>
    public bool ShowConnectionHint => !_hasController;

    /// <summary>绑定到提示层的文案；语言切换后新值即新语言。</summary>
    public string ConnectionHint => NoControllerHint;

    public double LeftStickX
    {
        get => _leftStickX;
        set => SetStick(ref _leftStickX, value, nameof(LeftStickX), nameof(LeftStickOffsetX));
    }

    /// <summary>Normalized stick Y with screen orientation (negative is up).</summary>
    public double LeftStickY
    {
        get => _leftStickY;
        set => SetStick(ref _leftStickY, value, nameof(LeftStickY), nameof(LeftStickOffsetY));
    }

    public double RightStickX
    {
        get => _rightStickX;
        set => SetStick(ref _rightStickX, value, nameof(RightStickX), nameof(RightStickOffsetX));
    }

    /// <summary>Normalized stick Y with screen orientation (negative is up).</summary>
    public double RightStickY
    {
        get => _rightStickY;
        set => SetStick(ref _rightStickY, value, nameof(RightStickY), nameof(RightStickOffsetY));
    }

    /// <summary>Cap offset in design pixels, bound to the cap's <c>TranslateTransform.X</c>.</summary>
    public double LeftStickOffsetX => _leftStickX * StickTravelPixels;

    public double LeftStickOffsetY => _leftStickY * StickTravelPixels;

    public double RightStickOffsetX => _rightStickX * StickTravelPixels;

    public double RightStickOffsetY => _rightStickY * StickTravelPixels;

    public double LT
    {
        get => _lt;
        set => SetTrigger(ref _lt, value, nameof(LT));
    }

    public double RT
    {
        get => _rt;
        set => SetTrigger(ref _rt, value, nameof(RT));
    }

    public bool A { get => _a; set => SetProperty(ref _a, value); }
    public bool B { get => _b; set => SetProperty(ref _b, value); }
    public bool X { get => _x; set => SetProperty(ref _x, value); }
    public bool Y { get => _y; set => SetProperty(ref _y, value); }
    public bool Up { get => _up; set => SetProperty(ref _up, value); }
    public bool Down { get => _down; set => SetProperty(ref _down, value); }
    public bool Left { get => _left; set => SetProperty(ref _left, value); }
    public bool Right { get => _right; set => SetProperty(ref _right, value); }
    public bool LB { get => _lb; set => SetProperty(ref _lb, value); }
    public bool RB { get => _rb; set => SetProperty(ref _rb, value); }
    public bool LS { get => _ls; set => SetProperty(ref _ls, value); }
    public bool RS { get => _rs; set => SetProperty(ref _rs, value); }
    public bool View { get => _view; set => SetProperty(ref _view, value); }
    public bool Menu { get => _menu; set => SetProperty(ref _menu, value); }
    public bool Guide { get => _guide; set => SetProperty(ref _guide, value); }
    public bool P1 { get => _p1; set => SetProperty(ref _p1, value); }
    public bool P2 { get => _p2; set => SetProperty(ref _p2, value); }
    public bool P3 { get => _p3; set => SetProperty(ref _p3, value); }
    public bool P4 { get => _p4; set => SetProperty(ref _p4, value); }

    // ------------------------------------------------------------------ selection

    public GamepadControlViewModel? SelectedControl
    {
        get => _selectedControl;
        private set
        {
            if (SetProperty(ref _selectedControl, value))
            {
                OnPropertyChanged(nameof(SelectedControlCaption));
            }
        }
    }

    public string SelectedControlCaption => SelectedControl?.Caption ?? Loc.T("cap.none");

    public RelayCommand<GamepadControlViewModel> SelectCommand { get; private set; } = null!;

    /// <summary>Raised whenever the user (or the code) selects a control.</summary>
    public event EventHandler<GamepadControlViewModel>? ControlSelected;

    /// <summary>Selects a control and notifies the binding panel. Null is ignored.</summary>
    public void Select(GamepadControlViewModel? control)
    {
        if (control is null)
        {
            return;
        }

        foreach (GamepadControlViewModel candidate in Controls)
        {
            candidate.IsSelected = ReferenceEquals(candidate, control);
        }

        SelectedControl = control;
        ControlSelected?.Invoke(this, control);
    }

    /// <summary>Selects by the v2.6 protocol button id; returns false when the pad has no such control.</summary>
    public bool SelectByButtonId(long buttonId)
    {
        foreach (GamepadControlViewModel candidate in Controls)
        {
            if (candidate.ButtonId == buttonId)
            {
                Select(candidate);
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ live plumbing

    /// <summary>Attaches the engine façade. The timer is not started until the window is visible.</summary>
    public void AttachHost(IRemapHost? host) => _host = host;

    /// <summary>Starts the 20 Hz poll; safe to call repeatedly.</summary>
    public void StartLive()
    {
        if (_disposed)
        {
            return;
        }

        // Captured on the UI thread so the pool-side read can post its result back safely. If the
        // caller runs during startup (Application.OnStartup, before Dispatcher.Run installs its
        // context), fall back to the current dispatcher instead of leaving the context null — a
        // null context applies snapshots on the thread pool, where WPF Freezable writes throw and
        // the 3D view silently freezes (sticks stop following the physical stick).
        _uiContext = SynchronizationContext.Current ?? _uiContext;
        _uiContext ??= new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);

        DispatcherTimer timer = _timer ??= CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(_throttle.IntervalMs);
        if (!timer.IsEnabled)
        {
            timer.Start();
        }

        BeginPoll();
    }

    /// <summary>Stops polling (window hidden to the tray or closing).</summary>
    public void StopLive()
    {
        if (_timer is { IsEnabled: true })
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// One synchronous poll of the host snapshot. Public so tests and the offscreen renderer can
    /// drive the view model without a dispatcher; the live timer uses the offloaded
    /// <see cref="BeginPoll"/> path instead. Never throws, even when the host is disposed.
    /// </summary>
    public void PollOnce()
    {
        if (_disposed || !_throttle.TryEnter())
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            bool hasState = TryReadSnapshot(out ControllerStateSnapshot snapshot);
            ApplySnapshot(hasState, snapshot);
        }
        catch (Exception)
        {
            // A failing poll must never take the timer (or the test run) down.
        }
        finally
        {
            CompleteTick(startedAt);
        }
    }

    /// <summary>
    /// Starts one asynchronous poll: the host read runs on the pool, the property updates are posted
    /// back to the captured dispatcher. Overlapping ticks are dropped by the re-entrancy guard.
    /// </summary>
    private void BeginPoll()
    {
        if (_disposed || !_throttle.TryEnter())
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        IRemapHost? host = _host;
        Guid? controllerId = _controllerId;
        if (host is null || controllerId is not { } id)
        {
            try
            {
                ApplySnapshot(false, default);
            }
            catch (Exception)
            {
            }
            finally
            {
                CompleteTick(startedAt);
            }

            return;
        }

        try
        {
            _ = Task.Run(() => ReadSnapshotAndPost(host, id, startedAt));
        }
        catch (Exception)
        {
            // Task.Run itself can only fail while shutting down; the timer must survive it.
            CompleteTick(startedAt);
        }
    }

    private void ReadSnapshotAndPost(IRemapHost host, Guid controllerId, long startedAt)
    {
        ControllerStateSnapshot snapshot = default;
        bool hasState;
        try
        {
            hasState = host.TryGetState(controllerId, out snapshot);
        }
        catch (Exception)
        {
            hasState = false;
            snapshot = default;
        }

        bool posted = RunOnUi(() =>
        {
            try
            {
                if (!_disposed)
                {
                    try
                    {
                        ApplySnapshot(hasState, snapshot);
                    }
                    catch (Exception)
                    {
                        // A broken binding must not leave the guard latched.
                    }
                }
            }
            finally
            {
                CompleteTick(startedAt);
            }
        });

        if (!posted)
        {
            // The dispatcher is gone; just release the guard.
            _throttle.Exit();
        }
    }

    private bool RunOnUi(Action action)
    {
        SynchronizationContext? context = _uiContext;
        if (context is null || SynchronizationContext.Current == context)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // Callers already guard their own bodies; the dispatcher must not see the exception.
            }

            return true;
        }

        try
        {
            context.Post(
                _ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception)
                    {
                    }
                },
                null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryReadSnapshot(out ControllerStateSnapshot snapshot)
    {
        snapshot = default;
        IRemapHost? host = _host;
        if (host is null || _controllerId is not { } controllerId)
        {
            return false;
        }

        try
        {
            return host.TryGetState(controllerId, out snapshot);
        }
        catch (Exception)
        {
            // A host being disposed must not kill the UI timer.
            snapshot = default;
            return false;
        }
    }

    /// <summary>Measures the tick and feeds the adaptive valve; always releases the guard.</summary>
    private void CompleteTick(long startedAt)
    {
        try
        {
            RecordTickCost(Stopwatch.GetElapsedTime(startedAt));
        }
        catch (Exception)
        {
            // Measuring/logging must never take the timer down.
        }
        finally
        {
            _throttle.Exit();
        }
    }

    private void RecordTickCost(TimeSpan elapsed)
    {
        if (!_throttle.Record(elapsed))
        {
            return;
        }

        try
        {
            if (_timer is { } timer)
            {
                timer.Interval = TimeSpan.FromMilliseconds(_throttle.IntervalMs);
            }

            if (_slowRateLogged)
            {
                return;
            }

            _slowRateLogged = true;
            const string line = "[PadShift][UI] dropped to 10Hz";
            Debug.WriteLine(line);
            Console.Error.WriteLine(line);
        }
        catch (Exception)
        {
            // A closed stderr / disposed timer only costs the diagnostic line.
        }
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };

        timer.Tick += OnTimerTick;
        return timer;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            BeginPoll();
        }
        catch (Exception)
        {
            // The timer must never throw; a failed poll simply keeps the last frame.
            _throttle.Exit();
        }
    }

    // ------------------------------------------------------------------ normalization

    /// <summary>Maps a signed 16-bit stick axis to -1..1, clamping defensive out-of-range values.</summary>
    public static double NormalizeStick(int raw) => Math.Clamp(raw / 32767.0, -1.0, 1.0);

    /// <summary>
    /// Maps a trigger axis to 0..1. The input layer stores 0..65535, but some devices/hosts still
    /// report 0..255, so values above 255 are divided by 65535 and the rest by 255.
    /// </summary>
    public static double NormalizeTrigger(int raw)
    {
        if (raw <= 0)
        {
            return 0;
        }

        double value = raw > 255 ? raw / 65535.0 : raw / 255.0;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static int Axis(int[] axes, int index) =>
        index >= 0 && index < axes.Length ? axes[index] : 0;

    private static bool Pressed(bool[] buttons, int id) =>
        id >= 0 && id < buttons.Length && buttons[id];

    private void ApplySnapshot(bool hasState, ControllerStateSnapshot snapshot)
    {
        if (!hasState)
        {
            ResetNeutral();
            return;
        }

        bool[] buttons = snapshot.Buttons ?? Array.Empty<bool>();
        int[] axes = snapshot.Axes ?? Array.Empty<int>();

        HasController = true;

        A = Pressed(buttons, ButtonIds.A);
        B = Pressed(buttons, ButtonIds.B);
        X = Pressed(buttons, ButtonIds.X);
        Y = Pressed(buttons, ButtonIds.Y);

        Up = Pressed(buttons, ButtonIds.DpadUp);
        Down = Pressed(buttons, ButtonIds.DpadDown);
        Left = Pressed(buttons, ButtonIds.DpadLeft);
        Right = Pressed(buttons, ButtonIds.DpadRight);

        LB = Pressed(buttons, ButtonIds.LeftBumper);
        RB = Pressed(buttons, ButtonIds.RightBumper);
        LS = Pressed(buttons, ButtonIds.LeftStickClick);
        RS = Pressed(buttons, ButtonIds.RightStickClick);

        View = Pressed(buttons, ButtonIds.Back);
        Menu = Pressed(buttons, ButtonIds.Start);
        Guide = Pressed(buttons, ButtonIds.Guide);

        P1 = Pressed(buttons, ButtonIds.Paddle1);
        P2 = Pressed(buttons, ButtonIds.Paddle2);
        P3 = Pressed(buttons, ButtonIds.Paddle3);
        P4 = Pressed(buttons, ButtonIds.Paddle4);

        LeftStickX = NormalizeStick(Axis(axes, Axes.LeftStickX));
        LeftStickY = -NormalizeStick(Axis(axes, Axes.LeftStickY));
        RightStickX = NormalizeStick(Axis(axes, Axes.RightStickX));
        RightStickY = -NormalizeStick(Axis(axes, Axes.RightStickY));

        LT = NormalizeTrigger(Axis(axes, Axes.LeftTrigger));
        RT = NormalizeTrigger(Axis(axes, Axes.RightTrigger));

        UpdateControlPresses();
    }

    private void ResetNeutral()
    {
        HasController = false;

        A = B = X = Y = false;
        Up = Down = Left = Right = false;
        LB = RB = LS = RS = false;
        View = Menu = Guide = false;
        P1 = P2 = P3 = P4 = false;

        LeftStickX = LeftStickY = RightStickX = RightStickY = 0;
        LT = RT = 0;

        UpdateControlPresses();
    }

    /// <summary>Mirrors the live buttons onto the clickable controls so the hit zones light up.</summary>
    private void UpdateControlPresses()
    {
        foreach (GamepadControlViewModel control in Controls)
        {
            control.IsPressed = IsControlPressed(control.ButtonId);
        }
    }

    private bool IsControlPressed(long buttonId) => buttonId switch
    {
        GamepadButtons.A => A,
        GamepadButtons.B => B,
        GamepadButtons.X => X,
        GamepadButtons.Y => Y,
        GamepadButtons.LeftBumper => LB,
        GamepadButtons.RightBumper => RB,
        GamepadButtons.Back => View,
        GamepadButtons.Start => Menu,
        GamepadButtons.Guide => Guide,
        GamepadButtons.LeftStickClick => LS,
        GamepadButtons.RightStickClick => RS,
        GamepadButtons.DpadUp => Up,
        GamepadButtons.DpadDown => Down,
        GamepadButtons.DpadLeft => Left,
        GamepadButtons.DpadRight => Right,
        GamepadButtons.LeftTrigger => LT > 0.02,
        GamepadButtons.RightTrigger => RT > 0.02,
        GamepadButtons.Paddle1 => P1,
        GamepadButtons.Paddle2 => P2,
        GamepadButtons.Paddle3 => P3,
        GamepadButtons.Paddle4 => P4,
        _ => false,
    };

    // ------------------------------------------------------------------ quantization

    private void SetStick(ref double field, double value, string name, string offsetName)
    {
        value = Quantize(value, -1.0, 1.0);
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(offsetName);
    }

    private void SetTrigger(ref double field, double value, string name)
    {
        value = Quantize(value, 0.0, 1.0);
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    /// <summary>
    /// Snaps a normalized analog value to <see cref="AnalogQuantum"/> (1/64). Values are exact
    /// multiples of 1/64, so comparing the doubles afterwards is stable and jitter-free.
    /// </summary>
    private static double Quantize(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min > 0 ? min : 0;
        }

        value = Math.Clamp(value, min, max);
        double snapped = Math.Round(value / AnalogQuantum, MidpointRounding.AwayFromZero) * AnalogQuantum;
        return Math.Clamp(snapped, min, max);
    }

    // ------------------------------------------------------------------ controls

    /// <summary>语言切换后由 App 调用：用新语言重建控件名称并清空选中。</summary>
    public void RefreshLanguage()
    {
        Controls.Clear();
        CreateControls();
        Select(Controls.FirstOrDefault());
        OnPropertyChanged(nameof(SelectedControlCaption));
    }

    private void CreateControls()
    {
        SelectCommand = new RelayCommand<GamepadControlViewModel>(Select);

        // Triggers and bumpers (top edge).
        Add(GamepadButtons.LeftTrigger, "LT", Loc.T("cap.lt"), 78, 12, 96, 26, 13);
        Add(GamepadButtons.RightTrigger, "RT", Loc.T("cap.rt"), 246, 12, 96, 26, 13);
        Add(GamepadButtons.LeftBumper, "LB", Loc.T("cap.lb"), 72, 42, 108, 20, 9);
        Add(GamepadButtons.RightBumper, "RB", Loc.T("cap.rb"), 240, 42, 108, 20, 9);

        // Sticks: left is upper-left, right is lower-right of centre.
        Add(GamepadButtons.LeftStickClick, "L3", Loc.T("cap.ls"), 86, 88, 84, 84, 42);
        Add(GamepadButtons.RightStickClick, "R3", Loc.T("cap.rs"), 252, 156, 80, 80, 40);

        // D-pad (left, below the left stick).
        Add(GamepadButtons.DpadUp, "▲", Loc.T("cap.up"), 99, 182, 22, 22, 3);
        Add(GamepadButtons.DpadDown, "▼", Loc.T("cap.down"), 99, 204, 22, 22, 3);
        Add(GamepadButtons.DpadLeft, "◀", Loc.T("cap.left"), 88, 193, 22, 22, 3);
        Add(GamepadButtons.DpadRight, "▶", Loc.T("cap.right"), 110, 193, 22, 22, 3);

        // Face buttons (diamond, right cluster).
        Add(GamepadButtons.Y, "Y", Loc.T("cap.y"), 297, 80, 26, 26, 13);
        Add(GamepadButtons.A, "A", Loc.T("cap.a"), 297, 130, 26, 26, 13);
        Add(GamepadButtons.X, "X", Loc.T("cap.x"), 272, 105, 26, 26, 13);
        Add(GamepadButtons.B, "B", Loc.T("cap.b"), 322, 105, 26, 26, 13);

        // Menu cluster.
        Add(GamepadButtons.Back, "View", Loc.T("cap.view"), 166, 133, 28, 18, 9);
        Add(GamepadButtons.Start, "Menu", Loc.T("cap.start"), 226, 133, 28, 18, 9);
        Add(GamepadButtons.Guide, "Guide", Loc.T("cap.guide"), 190, 90, 40, 40, 20);

        // Back paddles.
        Add(GamepadButtons.Paddle1, "P1", Loc.T("cap.p1"), 120, 258, 42, 16, 5);
        Add(GamepadButtons.Paddle2, "P2", Loc.T("cap.p2"), 166, 264, 42, 16, 5);
        Add(GamepadButtons.Paddle3, "P3", Loc.T("cap.p3"), 212, 264, 42, 16, 5);
        Add(GamepadButtons.Paddle4, "P4", Loc.T("cap.p4"), 258, 258, 42, 16, 5);
    }

    private void Add(long buttonId, string label, string caption, double x, double y, double width, double height, double radius) =>
        Controls.Add(new GamepadControlViewModel(buttonId, label, caption, x, y, width, height, radius));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        _throttle.Exit();
        _uiContext = null;
        _host = null;
    }
}
