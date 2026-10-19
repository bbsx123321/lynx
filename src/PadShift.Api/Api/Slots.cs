using System.Globalization;
using System.Text.Json;

namespace PadShift.Api;

/// <summary>the v2.6 protocol 槽位枚举；线上始终发名字字符串（"Slot1".."Slot4"），不发数字。</summary>
public enum Slot
{
    Slot1 = 0,
    Slot2 = 1,
    Slot3 = 2,
    Slot4 = 3,
}

/// <summary>
/// 槽位编解码。<see cref="IRemapHost"/> 用 0..3 的 int；the v2.6 protocol 的线上格式是 <c>Slot1..Slot4</c>。
/// 请求里同时接受名字和数字（数字按 1 基，0 也当 Slot1），方便手写脚本。
/// </summary>
public static class SlotCodec
{
    public const int Count = 4;

    public static bool IsValid(int index) => index is >= 0 and < Count;

    public static Slot FromIndex(int index) => index switch
    {
        0 => Slot.Slot1,
        1 => Slot.Slot2,
        2 => Slot.Slot3,
        3 => Slot.Slot4,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index must be 0..3."),
    };

    public static Slot? TryFromIndex(int index) => IsValid(index) ? FromIndex(index) : null;

    public static string ToName(Slot slot) => slot.ToString();

    public static bool TryParseName(string? text, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith("Slot", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && TryFromNumber(number, out index);
    }

    public static bool TryParse(JsonElement? element, out int index)
    {
        index = -1;
        if (element is not { } value)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => TryParseName(value.GetString(), out index),
            JsonValueKind.Number => value.TryGetInt32(out var number) && TryFromNumber(number, out index),
            _ => false,
        };
    }

    private static bool TryFromNumber(int number, out int index)
    {
        index = number == 0 ? 0 : number - 1;
        return IsValid(index);
    }
}
