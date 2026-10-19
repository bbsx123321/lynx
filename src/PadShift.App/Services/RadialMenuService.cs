using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PadShift.App.Infrastructure;
using PadShift.App.Views;

namespace PadShift.App.Services;

/// <summary>
/// Opens the radial menu on a held controller button combination or on a global hotkey, polls the
/// right stick while it is open, and dispatches the selected sector. No low-level hooks are
/// installed: the controller path reads the snapshot <see cref="DeviceSession"/> already publishes
/// and the hotkey path uses <c>RegisterHotKey</c> on a message-only window.
/// </summary>
public sealed class RadialMenuService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyId = 0x5044;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly DeviceSession _session;
    private readonly AppSettings _settings;
    private readonly RadialMenuDispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private HwndSource? _hotkeySource;
    private bool _hotkeyRegistered;
    private string _registeredHotkey = "";
    private bool _triggerHeld;
    private DateTimeOffset _triggerStarted;
    private RadialMenuWindow? _window;
    private bool _openedByController;
    private bool _started;
    private bool _disposed;

    public RadialMenuService(DeviceSession session, AppSettings settings, RadialMenuDispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised after the menu closed and a sector action ran (or with an error).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the "打开配置" sector is selected; the app opens the preferences dialog.</summary>
    public event Action? PreferencesRequested;

    public bool IsOpen => _window is not null;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _timer.Start();
        RegisterHotkey();
    }

    /// <summary>A hotkey pressed while the menu is open applies the highlighted sector.</summary>
    public void ToggleByHotkey()
    {
        if (!_settings.RadialMenu.Enabled)
        {
            return;
        }

        if (_window is { } open)
        {
            open.ApplyAndClose();
        }
        else
        {
            Open(byController: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;

        try
        {
            _window?.CancelAndClose();
        }
        catch (Exception)
        {
            // The window may already be gone during shutdown.
        }

        _window = null;

        UnregisterHotkey();
        StatusChanged = null;
        PreferencesRequested = null;
    }

    /// <summary>Re-reads the hotkey after the preferences window saved.</summary>
    public void ReloadHotkey()
    {
        if (_disposed || !_started)
        {
            return;
        }

        UnregisterHotkey();
        RegisterHotkey();
    }

    private void UnregisterHotkey()
    {
        if (_hotkeySource is null)
        {
            return;
        }

        if (_hotkeyRegistered)
        {
            try
            {
                UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            }
            catch (Exception)
            {
                // Session teardown.
            }
        }

        _hotkeyRegistered = false;
        _registeredHotkey = "";
        _hotkeySource.RemoveHook(WndProc);
        _hotkeySource.Dispose();
        _hotkeySource = null;
    }

    // ------------------------------------------------------------------ controller trigger

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RadialMenuSettings settings = _settings.RadialMenu;
        if (!settings.Enabled)
        {
            if (_window is { } disabled)
            {
                disabled.CancelAndClose();
            }

            _triggerHeld = false;
            return;
        }

        ControllerSnapshot snapshot = _session.LatestInput;
        bool held = snapshot.AreButtonsDown(settings.TriggerButtons);

        if (held)
        {
            if (!_triggerHeld)
            {
                _triggerHeld = true;
                _triggerStarted = DateTimeOffset.UtcNow;
            }
            else if (_window is null &&
                     (DateTimeOffset.UtcNow - _triggerStarted).TotalMilliseconds >= settings.HoldDurationMs)
            {
                Open(byController: true);
            }

            return;
        }

        if (!_triggerHeld)
        {
            return;
        }

        _triggerHeld = false;
        if (_window is not null && _openedByController)
        {
            // Releasing the trigger applies the highlighted sector.
            _window.ApplyAndClose();
        }
    }

    // ------------------------------------------------------------------ window lifecycle

    private void Open(bool byController)
    {
        if (_window is not null || _disposed || !_settings.RadialMenu.Enabled)
        {
            return;
        }

        RadialMenuSettings settings = _settings.RadialMenu;
        settings.Normalize();

        var window = new RadialMenuWindow(settings.Sectors, () => _session.LatestInput.RightStick)
        {
            Topmost = true,
        };
        _window = window;
        _openedByController = byController;
        window.ResultReady += (_, result) => OnWindowClosed(window, result);

        try
        {
            window.PositionAtCursor();
            window.Show();
            window.StartStickPolling();
        }
        catch (Exception ex)
        {
            _window = null;
            StatusChanged?.Invoke("径向菜单打开失败: " + ex.Message);
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }

            return;
        }
    }

    private void OnWindowClosed(RadialMenuWindow window, RadialMenuResult result)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }

        if (_disposed || !result.Applied || result.Sector is null)
        {
            return;
        }

        bool ok = _dispatcher.Execute(result.Sector);
        string message = ok
            ? $"径向菜单: {result.Sector.Label}"
            : _dispatcher.LastError ?? "动作未执行";

        if (result.Sector.Action == RadialSectorAction.OpenPreferences && ok)
        {
            PreferencesRequested?.Invoke();
        }
        else
        {
            StatusChanged?.Invoke(message);
        }
    }

    // ------------------------------------------------------------------ hotkey

    private void RegisterHotkey()
    {
        string hotkey = (_settings.RadialMenu.Hotkey ?? "").Trim();
        if (hotkey.Length == 0 || !_settings.RadialMenu.Enabled)
        {
            return;
        }

        if (_hotkeyRegistered && string.Equals(hotkey, _registeredHotkey, StringComparison.Ordinal))
        {
            return;
        }

        if (!TryParseHotkey(hotkey, out uint modifiers, out uint virtualKey))
        {
            StatusChanged?.Invoke($"径向菜单热键“{hotkey}”无法解析（示例：Ctrl+Alt+R）");
            return;
        }

        try
        {
            _hotkeySource = new HwndSource(new HwndSourceParameters("PadShiftRadialHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = HwndMessage,
            });
            _hotkeySource.AddHook(WndProc);
            _hotkeyRegistered = RegisterHotKey(_hotkeySource.Handle, HotkeyId, modifiers | ModNoRepeat, virtualKey);
            if (!_hotkeyRegistered)
            {
                StatusChanged?.Invoke($"径向菜单热键“{hotkey}”被其他程序占用");
            }
            else
            {
                _registeredHotkey = hotkey;
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("径向菜单热键注册失败: " + ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleByHotkey();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>Parses "Ctrl+Shift+Alt+Win+Key" into Win32 modifier flags and a virtual key.</summary>
    public static bool TryParseHotkey(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        string[] tokens = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= 0x0002; // MOD_CONTROL
                    continue;
                case "shift":
                    modifiers |= 0x0004; // MOD_SHIFT
                    continue;
                case "alt":
                    modifiers |= 0x0001; // MOD_ALT
                    continue;
                case "win":
                case "windows":
                    modifiers |= 0x0008; // MOD_WIN
                    continue;
            }

            if (virtualKey != 0 || !TryParseKey(token, out Key key))
            {
                return false;
            }

            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        return virtualKey != 0;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        string text = token.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Equals("Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }

        if (text.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Return;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
