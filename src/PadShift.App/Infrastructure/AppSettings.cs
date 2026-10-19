using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.App.Infrastructure;

/// <summary>
/// Client preferences persisted to <c>%LOCALAPPDATA%\PadShift\settings.json</c>. The file is owned
/// by the WPF client only (never by the engine), so it also holds the UI-side profile root and the
/// slot hotkey timings. A missing or damaged file always falls back to sane defaults.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultLanguage = "zh-CN";

    /// <summary>把历史值（"简体中文"）与非法输入归一为 "zh-CN" / "en-US"。</summary>
    public static string NormalizeLanguageCode(string? language)
    {
        string value = (language ?? "").Trim();
        return value is "en-US" or "en" or "English" ? "en-US" : "zh-CN";
    }
    public const int MinSlotHoldMs = 50;
    public const int MaxSlotHoldMs = 2500;
    public const int DefaultSlotHoldMs = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("minimizeToTrayOnStart")]
    public bool MinimizeToTrayOnStart { get; set; }

    [JsonPropertyName("settingsVersion")]
    public int SettingsVersion { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = false;

    /// <summary>点最小化时收进托盘（默认开）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 主窗口用手柄示意图时使用 3D 控制器（默认开）。关掉或带 <c>--safe-graphics</c> 启动时，
    /// 显示原来的 2D 矢量手柄。
    /// </summary>
    [JsonPropertyName("use3DGamepad")]
    public bool Use3DGamepad { get; set; } = true;

    /// <summary>
    /// 3D 手柄外形（<see cref="GamepadModelCatalog"/> 的 id）。未知或缺失的值在读取端回退到默认模型。
    /// </summary>
    [JsonPropertyName("gamepadModelId")]
    public string GamepadModelId { get; set; } = GamepadModelCatalog.DefaultId;

    /// <summary>Display-only for now: the client ships with Simplified Chinese resources only.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = DefaultLanguage;

    /// <summary>Absolute profiles folder. Empty means "resolve the Lynx default".</summary>
    [JsonPropertyName("profilesRoot")]
    public string ProfilesRoot { get; set; } = "";

    /// <summary>Mirrors <c>HostOptions.AutoSwitchEnabled</c>; applied the next time the app starts.</summary>
    [JsonPropertyName("autoSwitchEnabled")]
    public bool AutoSwitchEnabled { get; set; }

    /// <summary>失焦自动停映射：前台进程不属于当前配置的 processNames 时自动停用映射（focus-loss auto-disable）。</summary>
    [JsonPropertyName("turnRemapOffOnFocusLoss")]
    public bool TurnRemapOffOnFocusLoss { get; set; }

    /// <summary>停宏全局热键（如 Ctrl+Alt+M）；留空禁用。与径向菜单热键互斥校验在保存时进行。</summary>
    [JsonPropertyName("stopMacroHotkey")]
    public string StopMacroHotkey { get; set; } = "Ctrl+Alt+M";

    /// <summary>虚拟手柄性能档："normal" / "high"（默认 4ms）/ "ultra"；映射引擎节拍，下次启动生效。</summary>
    [JsonPropertyName("virtualPadPerformance")]
    public string VirtualPadPerformance { get; set; } = "high";

    /// <summary>UDP 输出镜像开关；开启后每帧输出意图以 20 Hz JSON 发到 127.0.0.1:UdpOutputPort。</summary>
    [JsonPropertyName("udpOutputEnabled")]
    public bool UdpOutputEnabled { get; set; }

    /// <summary>UDP 输出镜像端口（默认 26760，同类工具的惯例端口段）。</summary>
    [JsonPropertyName("udpOutputPort")]
    public int UdpOutputPort { get; set; } = 26760;

    /// <summary>屏蔽手柄背键（P1–P4 输入在引擎入口被忽略，映射与 UI 均不再响应）；下次启动生效。</summary>
    [JsonPropertyName("maskPaddleButtons")]
    public bool MaskPaddleButtons { get; set; }

    /// <summary>映射悬浮卡热键（如 Ctrl+Alt+B）；留空禁用。与停宏/径向热键互斥校验在保存时进行。</summary>
    [JsonPropertyName("mappingsOverlayHotkey")]
    public string MappingsOverlayHotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>Slot 1..4 hotkey names and hold durations.</summary>
    [JsonPropertyName("slots")]
    public List<SlotHotkeySetting> Slots { get; set; } = SlotHotkeySetting.CreateDefaults();

    /// <summary>Radial menu trigger, hotkey and the eight sector actions.</summary>
    [JsonPropertyName("radialMenu")]
    public RadialMenuSettings RadialMenu { get; set; } = new();

    /// <summary>Overlay toast and crosshair configuration.</summary>
    [JsonPropertyName("overlay")]
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>
    /// "不再询问" flags keyed by <see cref="Services.ConfirmationKeys"/>. <c>true</c> means the
    /// question is suppressed (the default action is taken automatically).
    /// </summary>
    [JsonPropertyName("confirmations")]
    public Dictionary<string, bool> Confirmations { get; set; } = new();

    /// <summary>Last gyro bias computed by the calibration wizard; also pushed into <c>DeviceSession</c>.</summary>
    [JsonPropertyName("gyroBias")]
    public GyroBias? GyroBias { get; set; }

    /// <summary>Local settings file; falls back to the application folder on unusual profiles.</summary>
    public static string DefaultPath { get; } = ResolveDefaultPath();

    /// <summary>Reads the file, returning defaults when it is missing, empty or corrupt.</summary>
    public static AppSettings Load(string? path = null)
    {
        string target = ResolvePath(path);
        try
        {
            if (File.Exists(target))
            {
                string json = File.ReadAllText(target);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings is not null)
                    {
                        // v2 迁移：旧版本把 closeToTray 默认写成 true，导致点 × 只收托盘。
        // 老用户升级后按新默认（× = 退出）走，避免"关了还在后台"。
        if (settings.SettingsVersion < 2)
        {
            settings.CloseToTray = false;
            settings.SettingsVersion = 2;
        }

        settings.Normalize();
                        return settings;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A damaged settings file must never block startup; the defaults are used instead.
        }

        var fallback = new AppSettings();
        fallback.Normalize();
        return fallback;
    }

    /// <summary>Writes the file; returns false (without throwing) when the path is not writable.</summary>
    public bool Save(string? path = null)
    {
        string target = ResolvePath(path);
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(target, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Repairs values loaded from disk (unknown language, missing/duplicated slots, ranges).</summary>
    public void Normalize()
    {
        // 旧版本存储 "简体中文"；归一为语言代码，非法值回退默认中文。
            Language = NormalizeLanguageCode(Language);
        ProfilesRoot = (ProfilesRoot ?? "").Trim();

        var existing = Slots ?? new List<SlotHotkeySetting>();
        var normalized = new List<SlotHotkeySetting>(4);
        for (int slot = 1; slot <= 4; slot++)
        {
            SlotHotkeySetting? match = null;
            foreach (SlotHotkeySetting candidate in existing)
            {
                if (candidate is not null && candidate.Slot == slot)
                {
                    match = candidate;
                    break;
                }
            }

            match ??= new SlotHotkeySetting { Slot = slot };
            match.Normalize();
            normalized.Add(match);
        }

        Slots = normalized;

        RadialMenu ??= new RadialMenuSettings();
        RadialMenu.Normalize();

        Overlay ??= new OverlaySettings();
        Overlay.Normalize();

        var confirmations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, bool> entry in Confirmations ?? new Dictionary<string, bool>())
        {
            string key = (entry.Key ?? "").Trim();
            if (key.Length > 0)
            {
                confirmations[key] = entry.Value;
            }
        }

        Confirmations = confirmations;

        if (GyroBias is not null && !GyroBias.IsValid)
        {
            GyroBias = null;
        }
    }

    private static string ResolvePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;

    private static string ResolveDefaultPath()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
            {
                return Path.Combine(local, "PadShift", "settings.json");
            }
        }
        catch (Exception)
        {
            // Fall through to the application folder.
        }

        return Path.Combine(AppContext.BaseDirectory, "settings.json");
    }
}

/// <summary>Per-slot hotkey display name, gamepad button combination and required hold time.</summary>
public sealed class SlotHotkeySetting
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gamepad button ids that must be held together (<see cref="PadShift.Core.ButtonIds"/>).</summary>
    [JsonPropertyName("buttons")]
    public int[] Buttons { get; set; } = Array.Empty<int>();

    [JsonPropertyName("holdDurationMs")]
    public int HoldDurationMs { get; set; } = AppSettings.DefaultSlotHoldMs;

    public static List<SlotHotkeySetting> CreateDefaults() =>
        Enumerable.Range(1, 4)
            .Select(slot => new SlotHotkeySetting
            {
                Slot = slot,
                Name = $"Slot {slot}",
                Buttons = Array.Empty<int>(),
                HoldDurationMs = AppSettings.DefaultSlotHoldMs,
            })
            .ToList();

    public void Normalize()
    {
        Slot = Math.Clamp(Slot, 1, 4);
        Name = string.IsNullOrWhiteSpace(Name) ? $"Slot {Slot}" : Name.Trim();
        HoldDurationMs = Math.Clamp(HoldDurationMs, AppSettings.MinSlotHoldMs, AppSettings.MaxSlotHoldMs);

        var buttons = new List<int>();
        foreach (int button in Buttons ?? Array.Empty<int>())
        {
            if (button is >= 0 and <= 255 && !buttons.Contains(button))
            {
                buttons.Add(button);
            }
        }

        Buttons = buttons.ToArray();
    }
}
