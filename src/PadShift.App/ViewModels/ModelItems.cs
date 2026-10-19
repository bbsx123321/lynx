using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PadShift.Core.Host;
using PadShift.Core.Profile;

namespace PadShift.App.ViewModels;

/// <summary>What a binding can output. Mirrors the three macro element kinds the profile supports.</summary>
public enum BindingTargetKind
{
    Keyboard,
    Mouse,
    Gamepad,
}

/// <summary>One physical controller as shown in the device selector.</summary>
public sealed class ControllerItem
{
    public ControllerItem(HostController controller)
    {
        Source = controller;
        Id = controller.Id;
        Name = string.IsNullOrWhiteSpace(controller.Name) ? "手柄" : controller.Name;
        ControllerType = controller.ControllerType;
        ConnectionType = controller.ConnectionType;
        BatteryPercent = controller.BatteryPercent;
    }

    public HostController Source { get; }

    public Guid Id { get; }

    public string Name { get; }

    public string ControllerType { get; }

    public string ConnectionType { get; }

    public int BatteryPercent { get; }

    public string DisplayName => $"{Name} · {ControllerType} · 电量 {BatteryPercent}%";
}

/// <summary>Slot 1..4. <see cref="Index"/> is the 0-based value the host expects.</summary>
public sealed class SlotItem : ObservableObject
{
    private bool _isSelected;

    public SlotItem(int index)
    {
        Index = index;
        Label = $"Slot {index + 1}";
    }

    public int Index { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A profile file discovered in the library.</summary>
public sealed class ProfileItem
{
    public ProfileItem(string path, string gameName, string? appName, string? loadError, string? iconPath = null, string? description = null)
    {
        Path = path;
        GameName = gameName;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        AppName = string.IsNullOrWhiteSpace(appName) ? null : appName;
        LoadError = loadError;
        IconPath = iconPath;
        Description = description;
    }

    public string Path { get; }

    public string GameName { get; }

    public string Name { get; }

    public string? AppName { get; }

    public string? LoadError { get; }

    public bool IsValid => LoadError is null;

    /// <summary>游戏封面（游戏目录下的 IcoGame.png）；无则为 null。</summary>
    public string? IconPath { get; }

    /// <summary>配置描述（description 字段首行摘要）；无则为 null。</summary>
    public string? Description { get; }

    public bool HasIcon => IconPath is not null;

    public string SubText => LoadError ?? (AppName is null ? "未命名配置" : $"程序: {AppName}");
}

/// <summary>Profiles of one game folder (the top directory under the profiles root) or the preset library.</summary>
public sealed class ProfileGroupViewModel
{
    public ProfileGroupViewModel(string name, bool isPreset = false)
    {
        Name = name;
        IsPreset = isPreset;
    }

    public string Name { get; }

    /// <summary>True for the global <c>Presets</c> folder shown as 「预设」 in the library.</summary>
    public bool IsPreset { get; }

    public ObservableCollection<ProfileItem> Profiles { get; } = new();
}

/// <summary>Activator choice shown in the binding panel (单按/长按/双击/三击/释放/开始).</summary>
public sealed record ActivatorOption(ActivatorType Type, string Label);

/// <summary>Output kind choice shown in the binding panel.</summary>
public sealed record TargetOption(BindingTargetKind Kind, string Label);

/// <summary>Mouse button target (1 = left, 2 = right, 3 = middle, 4/5 = side buttons).</summary>
public sealed record MouseButtonOption(int ButtonId, string Label);

/// <summary>Virtual gamepad button target. Ids use the the v2.6 protocol <c>GamepadButton</c> numbering.</summary>
public sealed record GamepadTargetOption(long ButtonId, string Label);

/// <summary>Layer choice. <see cref="Layer"/> 0 is the main layer; 1..12 are shift layers.</summary>
public sealed record LayerOption(int Layer, string Label)
{
    public static LayerOption Main { get; } = new(0, "主层");
}

/// <summary>Human-readable names for the the v2.6 protocol gamepad button ids used by this client.</summary>
public static class GamepadButtons
{
    public const long A = 1, B = 2, X = 3, Y = 4;
    public const long LeftBumper = 5, RightBumper = 6;
    public const long Back = 7, Start = 8;
    public const long LeftStickClick = 9, RightStickClick = 10;
    public const long Guide = 11;
    public const long DpadUp = 33, DpadDown = 34, DpadLeft = 35, DpadRight = 36;
    public const long LeftTrigger = 51, RightTrigger = 55;
    public const long Paddle1 = 71, Paddle2 = 72, Paddle3 = 73, Paddle4 = 74;

    /// <summary>All ids the pad drawing can bind, in a stable order.</summary>
    public static IReadOnlyList<(long Id, string Label)> All { get; } =
    [
        (A, "A"), (B, "B"), (X, "X"), (Y, "Y"),
        (LeftBumper, "LB"), (RightBumper, "RB"),
        (Back, "Back"), (Start, "Start"), (Guide, "Guide"),
        (LeftStickClick, "L3"), (RightStickClick, "R3"),
        (DpadUp, "Dpad Up"), (DpadDown, "Dpad Down"), (DpadLeft, "Dpad Left"), (DpadRight, "Dpad Right"),
        (LeftTrigger, "LT"), (RightTrigger, "RT"),
        (Paddle1, "P1"), (Paddle2, "P2"), (Paddle3, "P3"), (Paddle4, "P4"),
    ];

    public static string LabelOf(long id)
    {
        foreach ((long candidate, string label) in All)
        {
            if (candidate == id)
            {
                return label;
            }
        }

        return $"Btn {id}";
    }
}
