using System.Globalization;
using System.Text.Json.Serialization;
using PadShift.Core;

namespace PadShift.App.Infrastructure;

/// <summary>What a radial menu sector does when it is released.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RadialSectorAction>))]
public enum RadialSectorAction
{
    /// <summary>Nothing (closes the menu).</summary>
    Empty = 0,

    /// <summary>Switch the editing/displayed layer; <see cref="RadialSectorSetting.Argument"/> is the layer number.</summary>
    SwitchLayer = 1,

    /// <summary>Select a controller slot; <see cref="RadialSectorSetting.Argument"/> is 1..4.</summary>
    SwitchSlot = 2,

    /// <summary>Enable, disable or toggle remapping; the argument is <c>on</c>, <c>off</c> or empty (toggle).</summary>
    ToggleRemap = 3,

    /// <summary>Play a key chord/sequence; <see cref="RadialSectorSetting.Argument"/> is the macro text.</summary>
    RunMacro = 4,

    /// <summary>Open the preferences window.</summary>
    OpenPreferences = 5,
}

/// <summary>One sector of the eight-sector radial menu.</summary>
public sealed class RadialSectorSetting
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("action")]
    public RadialSectorAction Action { get; set; }

    [JsonPropertyName("argument")]
    public string Argument { get; set; } = "";

    public RadialSectorSetting Clone() => new()
    {
        Label = Label,
        Action = Action,
        Argument = Argument,
    };

    public void Normalize(int index)
    {
        Label = string.IsNullOrWhiteSpace(Label) ? $"扇区 {index + 1}" : Label.Trim();
        Argument = (Argument ?? "").Trim();
        if (!Enum.IsDefined(Action))
        {
            Action = RadialSectorAction.Empty;
        }
    }
}

/// <summary>
/// Radial menu configuration persisted in <c>settings.json</c>. The menu is drawn by the client
/// (no profile round-trip): the trigger is either a held controller button combination or a
/// global hotkey.
/// </summary>
public sealed class RadialMenuSettings
{
    public const int SectorCount = 8;
    public const int MinHoldMs = 100;
    public const int MaxHoldMs = 2000;
    public const int DefaultHoldMs = 300;
    public const string DefaultHotkey = "Ctrl+Alt+R";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gamepad buttons that must all be held (Core <see cref="ButtonIds"/> numbering).</summary>
    [JsonPropertyName("triggerButtons")]
    public int[] TriggerButtons { get; set; } = [ButtonIds.Guide];

    /// <summary>How long the trigger has to be held before the menu opens.</summary>
    [JsonPropertyName("holdDurationMs")]
    public int HoldDurationMs { get; set; } = DefaultHoldMs;

    /// <summary>Global hotkey text, e.g. <c>Ctrl+Alt+R</c>. Empty disables the hotkey.</summary>
    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = DefaultHotkey;

    [JsonPropertyName("sectors")]
    public List<RadialSectorSetting> Sectors { get; set; } = CreateDefaultSectors();

    public void Normalize()
    {
        HoldDurationMs = Math.Clamp(HoldDurationMs, MinHoldMs, MaxHoldMs);
        Hotkey = (Hotkey ?? "").Trim();

        var buttons = new List<int>();
        foreach (int button in TriggerButtons ?? [])
        {
            if (button is >= 0 and < ButtonIds.Max && !buttons.Contains(button))
            {
                buttons.Add(button);
            }
        }

        TriggerButtons = buttons.ToArray();

        var sectors = new List<RadialSectorSetting>(SectorCount);
        var existing = Sectors ?? new List<RadialSectorSetting>();
        for (int index = 0; index < SectorCount; index++)
        {
            RadialSectorSetting sector = index < existing.Count && existing[index] is not null
                ? existing[index]
                : new RadialSectorSetting();
            sector.Normalize(index);
            sectors.Add(sector);
        }

        Sectors = sectors;
    }

    public static List<RadialSectorSetting> CreateDefaultSectors() =>
    [
        new() { Label = "切换槽位 1", Action = RadialSectorAction.SwitchSlot, Argument = "1" },
        new() { Label = "切换槽位 2", Action = RadialSectorAction.SwitchSlot, Argument = "2" },
        new() { Label = "切换槽位 3", Action = RadialSectorAction.SwitchSlot, Argument = "3" },
        new() { Label = "切换槽位 4", Action = RadialSectorAction.SwitchSlot, Argument = "4" },
        new() { Label = "启用/停用映射", Action = RadialSectorAction.ToggleRemap, Argument = "" },
        new() { Label = "开关配置", Action = RadialSectorAction.OpenPreferences, Argument = "" },
        new() { Label = "切换层 1", Action = RadialSectorAction.SwitchLayer, Argument = "1" },
        new() { Label = "空", Action = RadialSectorAction.Empty, Argument = "" },
    ];

    /// <summary>Renders the trigger combination with the labels <see cref="TryParseButtons"/> accepts.</summary>
    public string TriggerText() => FormatButtons(TriggerButtons);

    /// <summary>
    /// Parses "Guide", "LB+RB" or numeric ids into Core <see cref="ButtonIds"/> values. Empty text is
    /// valid and means "no controller trigger".
    /// </summary>
    public static bool TryParseButtons(string? text, out int[] buttons, out string? error)
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
                if (numeric is < 0 or >= ButtonIds.Max)
                {
                    error = $"按钮编号 {numeric} 超出 0–{ButtonIds.Max - 1}";
                    return false;
                }

                if (!ids.Contains(numeric))
                {
                    ids.Add(numeric);
                }

                continue;
            }

            if (!TryParseButtonLabel(normalized, out int id))
            {
                error = $"无法识别按钮“{token}”：可用 A/B/X/Y/LB/RB/Back/Start/Guide/L3/R3/Dpad Up…，或 0–{ButtonIds.Max - 1} 的编号";
                return false;
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            error = "触发组合至少需要一个按钮";
            return false;
        }

        buttons = ids.ToArray();
        return true;
    }

    public static string FormatButtons(IEnumerable<int>? buttons)
    {
        if (buttons is null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (int button in buttons)
        {
            parts.Add(LabelOf(button));
        }

        return string.Join("+", parts);
    }

    public static string LabelOf(int buttonId) => buttonId switch
    {
        ButtonIds.A => "A",
        ButtonIds.B => "B",
        ButtonIds.X => "X",
        ButtonIds.Y => "Y",
        ButtonIds.LeftBumper => "LB",
        ButtonIds.RightBumper => "RB",
        ButtonIds.Back => "Back",
        ButtonIds.Start => "Start",
        ButtonIds.Guide => "Guide",
        ButtonIds.LeftStickClick => "L3",
        ButtonIds.RightStickClick => "R3",
        ButtonIds.DpadUp => "Dpad Up",
        ButtonIds.DpadDown => "Dpad Down",
        ButtonIds.DpadLeft => "Dpad Left",
        ButtonIds.DpadRight => "Dpad Right",
        ButtonIds.Paddle1 => "P1",
        ButtonIds.Paddle2 => "P2",
        ButtonIds.Paddle3 => "P3",
        ButtonIds.Paddle4 => "P4",
        _ => $"Btn {buttonId}",
    };

    private static bool TryParseButtonLabel(string normalized, out int buttonId)
    {
        foreach (int candidate in Enumerable.Range(0, ButtonIds.Max))
        {
            if (string.Equals(LabelOf(candidate).Replace(" ", ""), normalized, StringComparison.OrdinalIgnoreCase))
            {
                buttonId = candidate;
                return true;
            }
        }

        buttonId = normalized.ToUpperInvariant() switch
        {
            "VIEW" => ButtonIds.Back,
            "MENU" => ButtonIds.Start,
            "HOME" => ButtonIds.Guide,
            "UP" => ButtonIds.DpadUp,
            "DOWN" => ButtonIds.DpadDown,
            "LEFT" => ButtonIds.DpadLeft,
            "RIGHT" => ButtonIds.DpadRight,
            _ => -1,
        };

        return buttonId >= 0;
    }
}
