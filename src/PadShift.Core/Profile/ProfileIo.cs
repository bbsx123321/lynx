using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.Core.Profile;

/// <summary>.rewasd 读写。保持与原版一致：camelCase 属性名由特性显式给出，缩进 2 空格，忽略 null。</summary>
public static class ProfileIo
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        // 注意：不要在这里加 JsonStringEnumConverter —— 它是工厂转换器，会抢在所有
        // 具体枚举转换器前面，导致 snake_case 映射失效。
        o.Converters.Add(new EnumTextConverter<ActivatorType>());
        o.Converters.Add(new EnumTextConverter<ActivationMode>());
        o.Converters.Add(new EnumTextConverter<ShiftType>());
        o.Converters.Add(new EnumTextConverter<GamepadKind>());
        o.Converters.Add(new EnumTextConverter<HardwareKind>());
        o.Converters.Add(new EnumTextConverter<StickKind>());
        o.Converters.Add(new EnumTextConverter<SideKind>());
        o.Converters.Add(new EnumTextConverter<AxisKind>());
        return o;
    }

    public static GamepadProfile Load(string path)
    {
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<GamepadProfile>(json, Options)
                      ?? throw new InvalidDataException($"无法解析 profile: {path}");
        Validate(profile);
        return profile;
    }

    public static void Save(string path, GamepadProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));
    }

    public static string ToJson(GamepadProfile profile) => JsonSerializer.Serialize(profile, Options);

    public static GamepadProfile FromJson(string json) =>
        JsonSerializer.Deserialize<GamepadProfile>(json, Options)
        ?? throw new InvalidDataException("无法解析 profile JSON");

    /// <summary>基本一致性检查；只报致命问题，不阻断（原版对未知字段很宽容）。</summary>
    public static IReadOnlyList<string> Validate(GamepadProfile profile)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion < 2.0m || profile.SchemaVersion > 3.0m)
        {
            errors.Add($"schemaVersion={profile.SchemaVersion} 不在支持范围（2.0–3.0）");
        }

        var hardwareIds = new HashSet<long>();
        foreach (var hw in profile.Devices.Hardware ?? [])
        {
            if (!hardwareIds.Add(hw.Id))
            {
                errors.Add($"devices.hardware id={hw.Id} 重复");
            }
        }

        var maskIds = new HashSet<long>();
        foreach (var mask in profile.Masks ?? [])
        {
            maskIds.Add(mask.Id);
        }

        foreach (var mapping in profile.Mappings ?? [])
        {
            var cond = mapping.Condition;
            if (cond.Mask is { } m && !maskIds.Contains(m.Id))
            {
                errors.Add($"mapping 引用不存在的 mask id={m.Id}");
            }

            if (cond.Hardware is { } h && !hardwareIds.Contains(h.DeviceId))
            {
                errors.Add($"mapping 引用不存在的 deviceId={h.DeviceId}");
            }
        }

        return errors;
    }
}

/// <summary>
/// DIK 扫描码表（DirectInput Keyboard）。.rewasd 里键盘按钮用这套编号，
/// 与 Windows Set-1 扫描码一一对应（箭头/小键盘等需配合 extended 标志）。
/// </summary>
public static class Dik
{
    public const int Escape = 1, D1 = 2, D2 = 3, D3 = 4, D4 = 5, D5 = 6, D6 = 7, D7 = 8, D8 = 9, D9 = 10, D0 = 11;
    public const int Minus = 12, Equal = 13, Back = 14, Tab = 15;
    public const int Q = 16, W = 17, E = 18, R = 19, T = 20, Y = 21, U = 22, I = 23, O = 24, P = 25;
    public const int LeftBracket = 26, RightBracket = 27, Return = 28, LeftControl = 29;
    public const int A = 30, S = 31, D = 32, F = 33, G = 34, H = 35, J = 36, K = 37, L = 38;
    public const int Semicolon = 39, Apostrophe = 40, Grave = 41, LeftShift = 42, Backslash = 43;
    public const int Z = 44, X = 45, C = 46, V = 47, B = 48, N = 49, M = 50;
    public const int Comma = 51, Period = 52, Slash = 53, RightShift = 54, Multiply = 55, LeftAlt = 56, Space = 57;
    public const int Capital = 58, F1 = 59, F2 = 60, F3 = 61, F4 = 62, F5 = 63, F6 = 64, F7 = 65, F8 = 66, F9 = 67, F10 = 68;
    public const int NumLock = 69, Scroll = 70, Numpad7 = 71, Numpad8 = 72, Numpad9 = 73, Subtract = 74;
    public const int Numpad4 = 75, Numpad5 = 76, Numpad6 = 77, Add = 78, Numpad1 = 79, Numpad2 = 80, Numpad3 = 81;
    public const int Numpad0 = 82, Decimal = 83, Oem102 = 86, F11 = 87, F12 = 88;
    public const int NumpadEnter = 156, RightControl = 157, Divide = 181, RightAlt = 184;
    public const int Home = 199, Up = 200, PageUp = 201, Left = 203, Right = 205, End = 207, Down = 208, PageDown = 209;
    public const int Insert = 210, Delete = 211, LeftWin = 219, RightWin = 220, Apps = 221;

    /// <summary>这些键在 Set-1 里需要 E0 前缀（对应 Win32 的 KEYEVENTF_EXTENDEDKEY）。</summary>
    public static bool IsExtended(int dik) => dik is
        NumpadEnter or RightControl or Divide or RightAlt or Home or Up or PageUp or Left or Right or End
        or Down or PageDown or Insert or Delete or LeftWin or RightWin or Apps;
}

/// <summary>常见鼠标按钮编号（与 .rewasd 的 mouse.buttonId 对齐：1=左 2=右 3=中 4=X1 5=X2）。</summary>
public static class MouseButtons
{
    public const int Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5;
}
