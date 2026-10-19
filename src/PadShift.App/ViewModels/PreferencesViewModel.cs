using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Infrastructure;
using PadShift.App.Services;

namespace PadShift.App.ViewModels;

/// <summary>
/// 偏好设置窗口的 ViewModel：常规、手柄/Slots、配置、径向菜单、叠加层、确认。It edits a copy of
/// <see cref="AppSettings"/> and persists it to <c>settings.json</c> on save, so the window can be
/// opened, cancelled and reopened without touching the file.
/// </summary>
public sealed class PreferencesViewModel : ObservableObject
{
    /// <summary>Shown in the slots tab; the combination is applied through the engine's public API.</summary>
    public const string SlotHotkeyNote =
        "每个 Slot 可设置手柄组合键与长按时长；组合键通过引擎公开的 HostOptions.SlotHotkeys 生效，保存后下次启动 PadShift 时应用。";

    /// <summary>Syntax help for the combination field.</summary>
    public const string SlotComboHint =
        "组合键写法：按钮名用 + 连接（例如 LB+RB 或 Back+Start），也可直接写按钮编号（如 4+5）。留空 = 不抢占按钮。";

    private readonly AppSettings _settings;
    private readonly string? _settingsPath;

    private bool _minimizeToTrayOnStart;
    private bool _closeToTray;
    private bool _use3DGamepad;
    private string _gamepadModelId;
    private string _language;
    private string _profilesRoot = "";
    private bool _autoSwitchEnabled;
    private bool _turnRemapOffOnFocusLoss;
    private string _stopMacroHotkey = "";
    private string _virtualPadPerformance = "high";
    private bool _udpOutputEnabled;
    private int _udpOutputPort = 26760;
    private bool _maskPaddleButtons;
    private string _mappingsOverlayHotkey = "";
    private string? _message;

    public PreferencesViewModel(AppSettings settings, string? settingsPath = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsPath = settingsPath;
        _settings.Normalize();

        Slots = new ObservableCollection<SlotHotkeyViewModel>(
            _settings.Slots.Select(setting => new SlotHotkeyViewModel(setting)));
        LanguageOptions =
        [
            new LanguageOption("zh-CN", Loc.T("lang.zh")),
            new LanguageOption("en-US", Loc.T("lang.en")),
        ];

        RadialMenu = new RadialMenuPreferencesViewModel(_settings.RadialMenu);
        Overlay = new OverlayPreferencesViewModel(_settings.Overlay);
        Confirmations = new ConfirmationPreferencesViewModel(_settings);

        _minimizeToTrayOnStart = _settings.MinimizeToTrayOnStart;
        _closeToTray = _settings.CloseToTray;
        _use3DGamepad = _settings.Use3DGamepad;
        _gamepadModelId = GamepadModelCatalog.Find(_settings.GamepadModelId).Id;
        _language = _settings.Language;
        _profilesRoot = _settings.ProfilesRoot;
        _autoSwitchEnabled = _settings.AutoSwitchEnabled;
        _turnRemapOffOnFocusLoss = _settings.TurnRemapOffOnFocusLoss;
        _stopMacroHotkey = _settings.StopMacroHotkey;
        _virtualPadPerformance = PerformanceOptions.Contains(_settings.VirtualPadPerformance)
            ? _settings.VirtualPadPerformance
            : "high";
        _udpOutputEnabled = _settings.UdpOutputEnabled;
        _udpOutputPort = Math.Clamp(_settings.UdpOutputPort, 1, 65535);
        _maskPaddleButtons = _settings.MaskPaddleButtons;
        _mappingsOverlayHotkey = _settings.MappingsOverlayHotkey;

        SaveCommand = new RelayCommand(() => Save());
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
        OpenWebsiteCommand = new RelayCommand(OpenWebsite);
    }

    // ------------------------------------------------------------------ state

    public ObservableCollection<SlotHotkeyViewModel> Slots { get; }

    /// <summary>「径向菜单」tab state (trigger, hotkey, eight sectors).</summary>
    public RadialMenuPreferencesViewModel RadialMenu { get; }

    /// <summary>「叠加层」tab state (toast, crosshair, magnifier).</summary>
    public OverlayPreferencesViewModel Overlay { get; }

    /// <summary>「确认」tab state ("不再询问" flags).</summary>
    public ConfirmationPreferencesViewModel Confirmations { get; }

    /// <summary>The settings object edited by this window (shared with the running app services).</summary>
    public AppSettings Settings => _settings;

    /// <summary>可选择的界面语言（代码 + 本地化显示名）。</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public string SlotHotkeysExplanation => SlotHotkeyNote;

    public string SlotComboExplanation => SlotComboHint;

    public RelayCommand SaveCommand { get; }

    /// <summary>查询 GitHub 最新 Release 并把结果写进 <see cref="Message"/>。</summary>
    public AsyncRelayCommand CheckUpdateCommand { get; }

    /// <summary>打开官网（含直链与国内加速镜像的下载按钮）。</summary>
    public RelayCommand OpenWebsiteCommand { get; }

    public RelayCommand RestoreDefaultsCommand { get; }

    public bool MinimizeToTrayOnStart
    {
        get => _minimizeToTrayOnStart;
        set => SetProperty(ref _minimizeToTrayOnStart, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    /// <summary>「使用 3D 手柄视图」: the main window renders the procedural controller.</summary>
    public bool Use3DGamepad
    {
        get => _use3DGamepad;
        set => SetProperty(ref _use3DGamepad, value);
    }

    /// <summary>可挑选的 3D 手柄外形（见 <see cref="GamepadModelCatalog"/>）。</summary>
    public IReadOnlyList<GamepadModelInfo> GamepadModels => GamepadModelCatalog.Models;

    /// <summary>当前选中的 3D 手柄外形 id；保存后立即生效（无需重启）。</summary>
    public string GamepadModelId
    {
        get => _gamepadModelId;
        set
        {
            // 未知 id 一律落回目录里的默认模型，避免设置文件手改后选不中任何项。
            string resolved = GamepadModelCatalog.Find(value).Id;
            SetProperty(ref _gamepadModelId, resolved);
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetProperty(ref _language, AppSettings.NormalizeLanguageCode(value));
            }
        }
    }

    /// <summary>Absolute profiles folder; empty keeps the automatic the reference tool/PadShift default.</summary>
    public string ProfilesRoot
    {
        get => _profilesRoot;
        set => SetProperty(ref _profilesRoot, (value ?? "").Trim());
    }

    /// <summary>失焦自动停映射（行为见 AppSettings.TurnRemapOffOnFocusLoss）。</summary>
    public bool TurnRemapOffOnFocusLoss
    {
        get => _turnRemapOffOnFocusLoss;
        set => SetProperty(ref _turnRemapOffOnFocusLoss, value);
    }

    /// <summary>停宏全局热键（Ctrl+Alt+M 风格）；留空禁用。</summary>
    public string StopMacroHotkey
    {
        get => _stopMacroHotkey;
        set => SetProperty(ref _stopMacroHotkey, (value ?? "").Trim());
    }

    /// <summary>可选性能档（输出节拍），下次启动生效。</summary>
    public IReadOnlyList<string> PerformanceOptions { get; } = ["normal", "high", "ultra"];

    public string VirtualPadPerformance
    {
        get => _virtualPadPerformance;
        set
        {
            if (PerformanceOptions.Contains(value))
            {
                SetProperty(ref _virtualPadPerformance, value);
            }
        }
    }

    public bool UdpOutputEnabled
    {
        get => _udpOutputEnabled;
        set => SetProperty(ref _udpOutputEnabled, value);
    }

    public bool MaskPaddleButtons
    {
        get => _maskPaddleButtons;
        set => SetProperty(ref _maskPaddleButtons, value);
    }

    /// <summary>映射悬浮卡热键；留空禁用。</summary>
    public string MappingsOverlayHotkey
    {
        get => _mappingsOverlayHotkey;
        set => SetProperty(ref _mappingsOverlayHotkey, (value ?? "").Trim());
    }

    public int UdpOutputPort
    {
        get => _udpOutputPort;
        set => SetProperty(ref _udpOutputPort, Math.Clamp(value, 1, 65535));
    }

    /// <summary>Mirrors <c>HostOptions.AutoSwitchEnabled</c>; the app applies it on the next start.</summary>
    public bool AutoSwitchEnabled
    {
        get => _autoSwitchEnabled;
        set => SetProperty(ref _autoSwitchEnabled, value);
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(_message);

    // ------------------------------------------------------------------ actions

    /// <summary>Re-reads the settings file so a cancelled dialog does not leak stale edits.</summary>
    public void Reload()
    {
        AppSettings loaded = _settingsPath is null ? _settings : AppSettings.Load(_settingsPath);
        loaded.Normalize();

        MinimizeToTrayOnStart = loaded.MinimizeToTrayOnStart;
        CloseToTray = loaded.CloseToTray;
        Use3DGamepad = loaded.Use3DGamepad;
        GamepadModelId = GamepadModelCatalog.Find(loaded.GamepadModelId).Id;
        Language = loaded.Language;
        ProfilesRoot = loaded.ProfilesRoot;
        AutoSwitchEnabled = loaded.AutoSwitchEnabled;
        TurnRemapOffOnFocusLoss = loaded.TurnRemapOffOnFocusLoss;
        StopMacroHotkey = loaded.StopMacroHotkey;
        VirtualPadPerformance = PerformanceOptions.Contains(loaded.VirtualPadPerformance) ? loaded.VirtualPadPerformance : "high";
        UdpOutputEnabled = loaded.UdpOutputEnabled;
        UdpOutputPort = loaded.UdpOutputPort;
        MaskPaddleButtons = loaded.MaskPaddleButtons;
        MappingsOverlayHotkey = loaded.MappingsOverlayHotkey;

        Slots.Clear();
        foreach (SlotHotkeySetting setting in loaded.Slots)
        {
            Slots.Add(new SlotHotkeyViewModel(setting));
        }

        RadialMenu.Load(loaded.RadialMenu);
        Overlay.Load(loaded.Overlay);
        Confirmations.Load(loaded);

        Message = null;
    }

    /// <summary>Validates and persists; returns false with <see cref="Message"/> set on failure.</summary>
    public bool Save()
    {
        if (!ValidateProfilesRoot(out string? rootError))
        {
            Message = rootError;
            return false;
        }

        if (!TryReadSlotButtons(out Dictionary<int, int[]> buttons, out string? slotError))
        {
            Message = slotError;
            return false;
        }

        // 热键冲突校验：停宏 / 径向菜单 / 映射悬浮卡三个键盘热键互不相同（同一时刻只能各注册一份）。
        string stopMacro = _stopMacroHotkey.Trim();
        string mappingsHotkey = _mappingsOverlayHotkey.Trim();
        string radialHotkey = (RadialMenu.Hotkey ?? "").Trim();
        bool clash =
            (stopMacro.Length > 0 && stopMacro.Equals(radialHotkey, StringComparison.OrdinalIgnoreCase)) ||
            (mappingsHotkey.Length > 0 &&
                (mappingsHotkey.Equals(radialHotkey, StringComparison.OrdinalIgnoreCase) ||
                 mappingsHotkey.Equals(stopMacro, StringComparison.OrdinalIgnoreCase)));
        if (clash)
        {
            Message = Loc.T("msg.hotkeyclash");
            return false;
        }

        if (!RadialMenu.TryBuild(out RadialMenuSettings radialMenu))
        {
            Message = "径向菜单: " + (RadialMenu.ValidationError ?? "设置无效");
            return false;
        }

        OverlaySettings overlay = Overlay.ToSettings();

        _settings.MinimizeToTrayOnStart = _minimizeToTrayOnStart;
        _settings.CloseToTray = _closeToTray;
        _settings.Use3DGamepad = _use3DGamepad;
        _settings.GamepadModelId = _gamepadModelId;
        _settings.Language = _language;
        _settings.ProfilesRoot = _profilesRoot;
        _settings.AutoSwitchEnabled = _autoSwitchEnabled;
        _settings.TurnRemapOffOnFocusLoss = _turnRemapOffOnFocusLoss;
        _settings.StopMacroHotkey = _stopMacroHotkey;
        _settings.VirtualPadPerformance = _virtualPadPerformance;
        _settings.UdpOutputEnabled = _udpOutputEnabled;
        _settings.UdpOutputPort = _udpOutputPort;
        _settings.MaskPaddleButtons = _maskPaddleButtons;
        _settings.MappingsOverlayHotkey = _mappingsOverlayHotkey;
        _settings.RadialMenu = radialMenu;
        _settings.Overlay = overlay;
        _settings.Slots = Slots
            .Select(slot =>
            {
                SlotHotkeySetting model = slot.ToModel();
                model.Buttons = buttons.TryGetValue(slot.Slot, out int[]? ids) ? ids : Array.Empty<int>();
                return model;
            })
            .ToList();
        Confirmations.Apply(_settings);
        _settings.Normalize();

        if (!_settings.Save(_settingsPath))
        {
            Message = "写入 settings.json 失败（请检查 %LOCALAPPDATA% 的写权限）";
            return false;
        }

        _profilesRoot = _settings.ProfilesRoot;
        Message = Loc.T("msg.saved");
        return true;
    }

    /// <summary>Parses every slot combination and rejects invalid/duplicated ones.</summary>
    private bool TryReadSlotButtons(out Dictionary<int, int[]> buttons, out string? error)
    {
        buttons = new Dictionary<int, int[]>();
        error = null;

        foreach (SlotHotkeyViewModel slot in Slots)
        {
            if (!slot.TryGetButtons(out int[] parsed, out string? comboError))
            {
                error = $"{slot.Caption}: {comboError}";
                return false;
            }

            buttons[slot.Slot] = parsed;
        }

        for (int first = 0; first < Slots.Count; first++)
        {
            for (int second = first + 1; second < Slots.Count; second++)
            {
                if (buttons[Slots[first].Slot].Length > 0 &&
                    buttons[Slots[first].Slot].SequenceEqual(buttons[Slots[second].Slot]))
                {
                    error = $"{Slots[first].Caption} 与 {Slots[second].Caption} 的组合键相同，请改成不同的组合";
                    return false;
                }
            }
        }

        return true;
    }

    private async Task CheckUpdateAsync()
    {
        Message = Loc.T("update.checking");
        UpdateCheckResult? result = await UpdateService.CheckAsync();
        Message = result switch
        {
            { IsNewer: true } update => Loc.F("update.available", update.TagName, UpdateService.CurrentVersion()),
            { IsNewer: false } => Loc.F("update.latest", UpdateService.CurrentVersion()),
            _ => Loc.T("update.failed"),
        };
    }

    private void OpenWebsite() => UpdateService.OpenInBrowser(UpdateService.Website);

    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        defaults.Normalize();

        MinimizeToTrayOnStart = defaults.MinimizeToTrayOnStart;
        CloseToTray = defaults.CloseToTray;
        Use3DGamepad = defaults.Use3DGamepad;
        GamepadModelId = defaults.GamepadModelId;
        Language = defaults.Language;
        ProfilesRoot = "";
        AutoSwitchEnabled = defaults.AutoSwitchEnabled;
        TurnRemapOffOnFocusLoss = defaults.TurnRemapOffOnFocusLoss;
        StopMacroHotkey = defaults.StopMacroHotkey;
        VirtualPadPerformance = defaults.VirtualPadPerformance;
        UdpOutputEnabled = defaults.UdpOutputEnabled;
        UdpOutputPort = defaults.UdpOutputPort;
        MaskPaddleButtons = defaults.MaskPaddleButtons;
        MappingsOverlayHotkey = defaults.MappingsOverlayHotkey;

        Slots.Clear();
        foreach (SlotHotkeySetting setting in defaults.Slots)
        {
            Slots.Add(new SlotHotkeyViewModel(setting));
        }

        RadialMenu.Reset();
        Overlay.Reset();
        Confirmations.Load(defaults);
        Message = "已恢复默认值（点击保存写入） / Defaults restored (click Save to write)";
    }

    private bool ValidateProfilesRoot(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_profilesRoot))
        {
            return true;
        }

        if (!Path.IsPathRooted(_profilesRoot))
        {
            error = "Profile 根目录必须是绝对路径";
            return false;
        }

        try
        {
            Path.GetFullPath(_profilesRoot);
            return true;
        }
        catch (Exception)
        {
            error = "Profile 根目录包含无效字符";
            return false;
        }
    }
}

/// <summary>One slot row: display name, gamepad button combination and the 50–2500 ms hold time.</summary>
/// <summary>语言下拉项：设置代码 + 界面显示名。</summary>
public readonly record struct LanguageOption(string Code, string DisplayName);

public sealed class SlotHotkeyViewModel : ObservableObject
{
    private string _name;
    private string _comboInput;
    private string? _comboError;
    private int[] _buttons;
    private int _holdDurationMs;

    public SlotHotkeyViewModel(SlotHotkeySetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Slot = Math.Clamp(setting.Slot, 1, 4);
        _name = string.IsNullOrWhiteSpace(setting.Name) ? $"Slot {Slot}" : setting.Name;
        _buttons = setting.Buttons ?? Array.Empty<int>();
        _comboInput = FormatCombo(_buttons);
        _holdDurationMs = Math.Clamp(setting.HoldDurationMs, AppSettings.MinSlotHoldMs, AppSettings.MaxSlotHoldMs);
    }

    public int Slot { get; }

    public string Caption => $"Slot {Slot}";

    public string HoldRangeText => $"{AppSettings.MinSlotHoldMs}–{AppSettings.MaxSlotHoldMs} ms";

    public string Name
    {
        get => _name;
        set
        {
            if (value is not null && SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_name) ? $"Slot {Slot}" : _name;

    /// <summary>Free-form combination text ("LB+RB", "4+5", ...); empty means "no hotkey".</summary>
    public string ComboInput
    {
        get => _comboInput;
        set
        {
            if (!SetProperty(ref _comboInput, value ?? ""))
            {
                return;
            }

            if (TryParseCombo(_comboInput, out int[] parsed, out string? error))
            {
                _buttons = parsed;
                ComboError = null;
            }
            else
            {
                ComboError = error;
            }
        }
    }

    public string? ComboError
    {
        get => _comboError;
        private set
        {
            if (SetProperty(ref _comboError, value))
            {
                OnPropertyChanged(nameof(HasComboError));
            }
        }
    }

    public bool HasComboError => !string.IsNullOrWhiteSpace(_comboError);

    /// <summary>The last successfully parsed combination (persisted when the dialog saves).</summary>
    public IReadOnlyList<int> Buttons => _buttons;

    public int HoldDurationMs
    {
        get => _holdDurationMs;
        set
        {
            int clamped = Math.Clamp(value, AppSettings.MinSlotHoldMs, AppSettings.MaxSlotHoldMs);
            if (SetProperty(ref _holdDurationMs, clamped))
            {
                OnPropertyChanged(nameof(HoldText));
            }

            if (clamped != value)
            {
                OnPropertyChanged(nameof(HoldDurationMs));
            }
        }
    }

    public string HoldText => $"{_holdDurationMs} ms";

    /// <summary>Re-parses the text field; used by <see cref="PreferencesViewModel.Save"/>.</summary>
    public bool TryGetButtons(out int[] buttons, out string? error) =>
        TryParseCombo(_comboInput, out buttons, out error);

    public SlotHotkeySetting ToModel() => new()
    {
        Slot = Slot,
        Name = DisplayName,
        Buttons = (int[])_buttons.Clone(),
        HoldDurationMs = _holdDurationMs,
    };

    /// <summary>
    /// Parses "A+B" style combinations. Empty text is valid and means "no hotkey"; every token must
    /// be a numeric button id (0–255) or one of the labels from <see cref="GamepadButtons.All"/>.
    /// </summary>
    public static bool TryParseCombo(string? text, out int[] buttons, out string? error)
    {
        buttons = Array.Empty<int>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] tokens = text.Split(
            ['+', ',', ';', '/', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        var ids = new List<int>(tokens.Length);
        foreach (string token in tokens)
        {
            string normalized = token.Replace(" ", "").Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                if (numeric is < 0 or > 255)
                {
                    error = $"按钮编号 {numeric} 超出 0–255";
                    return false;
                }

                if (!ids.Contains(numeric))
                {
                    ids.Add(numeric);
                }

                continue;
            }

            long? known = FindButtonByLabel(normalized);
            if (known is not { } buttonId)
            {
                error = $"无法识别按钮“{token}”：可用 A/B/X/Y/LB/RB/Back/Start/Guide/L3/R3/Dpad Up…/LT/RT/P1–P4，或 0–255 的编号";
                return false;
            }

            int id = (int)buttonId;
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            error = "组合键至少需要一个按钮";
            return false;
        }

        buttons = ids.ToArray();
        return true;
    }

    /// <summary>Renders a combination with the friendly labels the parser also accepts.</summary>
    public static string FormatCombo(IEnumerable<int>? buttons)
    {
        if (buttons is null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (int button in buttons)
        {
            string? label = LabelOf(button);
            parts.Add(label ?? button.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("+", parts);
    }

    private static string? LabelOf(int buttonId)
    {
        foreach ((long id, string label) in GamepadButtons.All)
        {
            if (id == buttonId)
            {
                return label;
            }
        }

        return null;
    }

    private static long? FindButtonByLabel(string normalized)
    {
        foreach ((long id, string label) in GamepadButtons.All)
        {
            if (string.Equals(label.Replace(" ", ""), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        // Aliases that match the labels printed on the pad drawing.
        if (normalized.Equals("View", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.Back;
        }

        if (normalized.Equals("Menu", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.Start;
        }

        if (normalized.Equals("Up", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.DpadUp;
        }

        if (normalized.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.DpadDown;
        }

        if (normalized.Equals("Left", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.DpadLeft;
        }

        if (normalized.Equals("Right", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadButtons.DpadRight;
        }

        return null;
    }
}
