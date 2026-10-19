using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using WinForms = System.Windows.Forms;

namespace PadShift.App.ViewModels;

/// <summary>One selectable radial action shown in the 径向菜单 preferences tab.</summary>
public sealed record RadialActionOption(RadialSectorAction Action, string Label, string ArgumentHint);

/// <summary>Editable row for one of the eight radial sectors.</summary>
public sealed class RadialSectorRowViewModel : ObservableObject
{
    private string _label;
    private RadialActionOption _selectedAction;
    private string _argument;

    public RadialSectorRowViewModel(int index, RadialSectorSetting setting, IReadOnlyList<RadialActionOption> options)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Index = index;
        Caption = $"扇区 {index + 1}";
        Actions = options;
        _label = string.IsNullOrWhiteSpace(setting.Label) ? Caption : setting.Label;
        _argument = setting.Argument ?? "";
        _selectedAction = options.FirstOrDefault(option => option.Action == setting.Action) ?? options[0];
    }

    public int Index { get; }

    public string Caption { get; }

    public IReadOnlyList<RadialActionOption> Actions { get; }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value ?? "");
    }

    public RadialActionOption SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (value is not null && SetProperty(ref _selectedAction, value))
            {
                OnPropertyChanged(nameof(ArgumentHint));
            }
        }
    }

    public string Argument
    {
        get => _argument;
        set => SetProperty(ref _argument, value ?? "");
    }

    public string ArgumentHint => SelectedAction.ArgumentHint;

    public RadialSectorSetting ToModel() => new()
    {
        Label = string.IsNullOrWhiteSpace(_label) ? Caption : _label.Trim(),
        Action = _selectedAction.Action,
        Argument = (_argument ?? "").Trim(),
    };
}

/// <summary>
/// The 「径向菜单」tab: enable switch, trigger combination, hold time, global hotkey and the eight
/// sector rows. Everything is validated head-less and persisted through <see cref="PreferencesViewModel"/>.
/// </summary>
public sealed class RadialMenuPreferencesViewModel : ObservableObject
{
    public const string TriggerHintText =
        "触发按钮用 + 连接（例如 Guide 或 LB+RB），按住达到设定时长后打开；也可设置全局热键。留空按钮 = 只用热键。";

    private bool _enabled;
    private string _triggerText = "";
    private int _holdDurationMs = RadialMenuSettings.DefaultHoldMs;
    private string _hotkey = "";
    private string? _validationError;

    public RadialMenuPreferencesViewModel(RadialMenuSettings settings)
    {
        Actions =
        [
            new RadialActionOption(RadialSectorAction.Empty, "空", "无参数"),
            new RadialActionOption(RadialSectorAction.SwitchLayer, "切换层", "层编号 0–12（0 = 主层）"),
            new RadialActionOption(RadialSectorAction.SwitchSlot, "切换槽位", "槽位 1–4"),
            new RadialActionOption(RadialSectorAction.ToggleRemap, "启用/停用映射", "on / off / 留空=切换"),
            new RadialActionOption(RadialSectorAction.RunMacro, "执行宏", "按键序列，例如 Ctrl+Shift+Esc"),
            new RadialActionOption(RadialSectorAction.OpenPreferences, "打开配置", "无参数"),
        ];

        Sectors = new ObservableCollection<RadialSectorRowViewModel>();
        ResetCommand = new RelayCommand(Reset);
        Load(settings ?? new RadialMenuSettings());
    }

    public IReadOnlyList<RadialActionOption> Actions { get; }

    public ObservableCollection<RadialSectorRowViewModel> Sectors { get; }

    public RelayCommand ResetCommand { get; }

    public string TriggerHint => TriggerHintText;

    public string HoldRangeText => $"{RadialMenuSettings.MinHoldMs}–{RadialMenuSettings.MaxHoldMs} ms";

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string TriggerText
    {
        get => _triggerText;
        set
        {
            if (SetProperty(ref _triggerText, value ?? ""))
            {
                ValidationError = null;
            }
        }
    }

    public int HoldDurationMs
    {
        get => _holdDurationMs;
        set => SetProperty(ref _holdDurationMs, Math.Clamp(value, RadialMenuSettings.MinHoldMs, RadialMenuSettings.MaxHoldMs));
    }

    public string Hotkey
    {
        get => _hotkey;
        set => SetProperty(ref _hotkey, value ?? "");
    }

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(_validationError);

    public void Load(RadialMenuSettings settings)
    {
        settings.Normalize();
        Enabled = settings.Enabled;
        TriggerText = settings.TriggerText();
        HoldDurationMs = settings.HoldDurationMs;
        Hotkey = settings.Hotkey;

        Sectors.Clear();
        for (int index = 0; index < RadialMenuSettings.SectorCount; index++)
        {
            Sectors.Add(new RadialSectorRowViewModel(index, settings.Sectors[index], Actions));
        }

        ValidationError = null;
    }

    public void Reset()
    {
        Load(new RadialMenuSettings());
        ValidationError = null;
    }

    /// <summary>Validates the form; on failure <see cref="ValidationError"/> is set and false returned.</summary>
    public bool TryBuild(out RadialMenuSettings settings)
    {
        settings = new RadialMenuSettings();
        if (!RadialMenuSettings.TryParseButtons(_triggerText, out int[] buttons, out string? buttonError))
        {
            ValidationError = buttonError;
            return false;
        }

        if (_enabled && string.IsNullOrWhiteSpace(_hotkey) && buttons.Length == 0)
        {
            ValidationError = "触发按钮和热键不能同时为空（否则径向菜单无法打开）";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_hotkey) &&
            !RadialMenuService.TryParseHotkey(_hotkey, out _, out _))
        {
            ValidationError = "热键格式无法识别；示例：Ctrl+Alt+R（留空 = 只用按钮触发）";
            return false;
        }

        settings.Enabled = _enabled;
        settings.TriggerButtons = buttons;
        settings.HoldDurationMs = Math.Clamp(_holdDurationMs, RadialMenuSettings.MinHoldMs, RadialMenuSettings.MaxHoldMs);
        settings.Hotkey = _hotkey.Trim();
        settings.Sectors = Sectors.Select(row => row.ToModel()).ToList();
        settings.Normalize();

        ValidationError = null;
        return true;
    }
}

/// <summary>Crosshair shape choice for the 叠加层 tab.</summary>
public sealed record CrosshairShapeOption(CrosshairShape Shape, string Label);

/// <summary>One display in the overlay monitor picker. <see cref="DeviceName"/> is what settings.json stores.</summary>
public sealed record MonitorOption(string DeviceName, string Label, bool IsPrimary = false);

/// <summary>The 「叠加层」tab: toast switch and crosshair shape/position/size/colour/magnifier.</summary>
public sealed class OverlayPreferencesViewModel : ObservableObject
{
    private bool _enabled;
    private bool _showToast;
    private bool _showCrosshair;
    private bool _centerOnScreen;
    private int _offsetX;
    private int _offsetY;
    private MonitorOption? _selectedMonitor;
    private CrosshairShape _shape;
    private double _size = 30;
    private double _thickness = 2;
    private string _color = "#FF5CFF8A";
    private bool _useMagnifier;
    private int _magnification = 2;
    private int _magnifierSizePercent = 30;

    public OverlayPreferencesViewModel(OverlaySettings settings, IReadOnlyList<MonitorOption>? monitors = null)
    {
        Shapes =
        [
            new CrosshairShapeOption(CrosshairShape.Cross, "十字"),
            new CrosshairShapeOption(CrosshairShape.Dot, "点"),
            new CrosshairShapeOption(CrosshairShape.Circle, "圆"),
        ];

        Monitors = monitors is { Count: > 0 } ? monitors : EnumerateMonitors();
        Load(settings ?? new OverlaySettings());
    }

    public IReadOnlyList<CrosshairShapeOption> Shapes { get; }

    /// <summary>Connected displays (one entry per <c>Screen.AllScreens</c> element).</summary>
    public IReadOnlyList<MonitorOption> Monitors { get; }

    /// <summary>The monitor whose centre (plus offset) hosts the crosshair; persisted by DeviceName.</summary>
    public MonitorOption? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedMonitor, value);
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool ShowToast
    {
        get => _showToast;
        set => SetProperty(ref _showToast, value);
    }

    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set => SetProperty(ref _showCrosshair, value);
    }

    public bool CenterOnScreen
    {
        get => _centerOnScreen;
        set
        {
            if (SetProperty(ref _centerOnScreen, value))
            {
                OnPropertyChanged(nameof(IsOffsetEnabled));
            }
        }
    }

    public bool IsOffsetEnabled => !_centerOnScreen;

    public int OffsetX
    {
        get => _offsetX;
        set => SetProperty(ref _offsetX, Math.Clamp(value, -4000, 4000));
    }

    public int OffsetY
    {
        get => _offsetY;
        set => SetProperty(ref _offsetY, Math.Clamp(value, -4000, 4000));
    }

    public CrosshairShape Shape
    {
        get => _shape;
        set => SetProperty(ref _shape, value);
    }

    public double Size
    {
        get => _size;
        set => SetProperty(ref _size, Math.Clamp(value, OverlaySettings.MinSize, OverlaySettings.MaxSize));
    }

    public double Thickness
    {
        get => _thickness;
        set => SetProperty(ref _thickness, Math.Clamp(value, OverlaySettings.MinThickness, OverlaySettings.MaxThickness));
    }

    public string Color
    {
        get => _color;
        set => SetProperty(ref _color, value ?? "");
    }

    public bool UseMagnifier
    {
        get => _useMagnifier;
        set => SetProperty(ref _useMagnifier, value);
    }

    public int Magnification
    {
        get => _magnification;
        set => SetProperty(ref _magnification, Math.Clamp(value, OverlaySettings.MinMagnification, OverlaySettings.MaxMagnification));
    }

    public int MagnifierSizePercent
    {
        get => _magnifierSizePercent;
        set => SetProperty(ref _magnifierSizePercent, Math.Clamp(value, OverlaySettings.MinMagnifierPercent, OverlaySettings.MaxMagnifierPercent));
    }

    public string SizeRangeText => $"{OverlaySettings.MinSize}–{OverlaySettings.MaxSize}";
    public string MagnificationRangeText => $"{OverlaySettings.MinMagnification}–{OverlaySettings.MaxMagnification}×";

    public void Load(OverlaySettings settings)
    {
        settings.Normalize();
        Enabled = settings.Enabled;
        ShowToast = settings.ShowToast;
        ShowCrosshair = settings.ShowCrosshair;
        CenterOnScreen = settings.CenterOnScreen;
        OffsetX = settings.OffsetX;
        OffsetY = settings.OffsetY;
        Shape = settings.Shape;
        Size = settings.Size;
        Thickness = settings.Thickness;
        Color = settings.Color;
        UseMagnifier = settings.UseMagnifier;
        Magnification = settings.Magnification;
        MagnifierSizePercent = settings.MagnifierSizePercent;

        _selectedMonitor = Monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? Monitors[0];
        OnPropertyChanged(nameof(SelectedMonitor));
    }

    public void Reset() => Load(new OverlaySettings());

    public OverlaySettings ToSettings()
    {
        var settings = new OverlaySettings
        {
            Enabled = _enabled,
            ShowToast = _showToast,
            ShowCrosshair = _showCrosshair,
            CenterOnScreen = _centerOnScreen,
            OffsetX = _offsetX,
            OffsetY = _offsetY,
            MonitorDeviceName = _selectedMonitor?.DeviceName ?? "",
            Shape = _shape,
            Size = _size,
            Thickness = _thickness,
            Color = _color,
            UseMagnifier = _useMagnifier,
            Magnification = _magnification,
            MagnifierSizePercent = _magnifierSizePercent,
        };
        settings.Normalize();
        Color = settings.Color;
        return settings;
    }

    /// <summary>Best-effort <c>Screen.AllScreens</c> read; never throws so the tab still opens.</summary>
    private static IReadOnlyList<MonitorOption> EnumerateMonitors()
    {
        try
        {
            WinForms.Screen[] screens = WinForms.Screen.AllScreens;
            if (screens.Length == 0)
            {
                return [new MonitorOption("", "主显示器", IsPrimary: true)];
            }

            var monitors = new List<MonitorOption>(screens.Length);
            for (int index = 0; index < screens.Length; index++)
            {
                WinForms.Screen screen = screens[index];
                string label = $"{index + 1}. {screen.Bounds.Width}×{screen.Bounds.Height}";
                if (screen.Primary)
                {
                    label += "（主）";
                }

                monitors.Add(new MonitorOption(screen.DeviceName, label, screen.Primary));
            }

            return monitors;
        }
        catch (Exception)
        {
            return [new MonitorOption("", "主显示器", IsPrimary: true)];
        }
    }
}

/// <summary>One "不再询问" row in the 确认 preferences tab.</summary>
public sealed class ConfirmationToggleViewModel : ObservableObject
{
    private bool _suppressed;

    public ConfirmationToggleViewModel(string key, string caption, bool suppressed)
    {
        Key = key;
        Caption = caption;
        _suppressed = suppressed;
    }

    public string Key { get; }

    public string Caption { get; }

    public bool Suppressed
    {
        get => _suppressed;
        set => SetProperty(ref _suppressed, value);
    }
}

/// <summary>
/// The 确认 tab: lets the user re-enable prompts that were suppressed with "不再询问".
/// <see cref="Apply"/> rewrites <see cref="AppSettings.Confirmations"/>.
/// </summary>
public sealed class ConfirmationPreferencesViewModel : ObservableObject
{
    public ConfirmationPreferencesViewModel(AppSettings settings)
    {
        Confirmations = new ObservableCollection<ConfirmationToggleViewModel>();
        ResetAllCommand = new RelayCommand(() =>
        {
            foreach (ConfirmationToggleViewModel item in Confirmations)
            {
                item.Suppressed = false;
            }
        });
        Load(settings ?? new AppSettings());
    }

    public ObservableCollection<ConfirmationToggleViewModel> Confirmations { get; }

    public RelayCommand ResetAllCommand { get; }

    public void Load(AppSettings settings)
    {
        settings.Normalize();
        Confirmations.Clear();
        foreach ((string key, string caption) in ConfirmationKeys.All)
        {
            settings.Confirmations.TryGetValue(key, out bool suppressed);
            Confirmations.Add(new ConfirmationToggleViewModel(key, caption, suppressed));
        }
    }

    public void Apply(AppSettings settings)
    {
        // Keep keys this build does not know about (forward compatibility), but rewrite the four
        // prompts the UI exposes.
        var preserved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, bool> entry in settings.Confirmations)
        {
            if (!ConfirmationKeys.All.Any(item => string.Equals(item.Key, entry.Key, StringComparison.OrdinalIgnoreCase)))
            {
                preserved[entry.Key] = entry.Value;
            }
        }

        settings.Confirmations.Clear();
        foreach (ConfirmationToggleViewModel item in Confirmations)
        {
            settings.Confirmations[item.Key] = item.Suppressed;
        }

        foreach (KeyValuePair<string, bool> entry in preserved)
        {
            settings.Confirmations[entry.Key] = entry.Value;
        }
    }
}
