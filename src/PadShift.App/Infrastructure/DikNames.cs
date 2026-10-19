using PadShift.Core.Profile;

namespace PadShift.App.Infrastructure;

/// <summary>
/// Display names for the DIK scan codes stored in <c>keyboard.buttonId</c>. Only used for the UI
/// (the profile keeps the numeric code), so unknown codes fall back to a stable <c>DIK_n</c> label.
/// </summary>
public static class DikNames
{
    private static readonly Dictionary<int, string> Names = Build();

    public static string FromDik(int dik) =>
        Names.TryGetValue(dik, out string? name) ? name : dik > 0 ? $"DIK_{dik}" : "未设置";

    private static Dictionary<int, string> Build()
    {
        var names = new Dictionary<int, string>();

        int[] letters =
        [
            Dik.A, Dik.B, Dik.C, Dik.D, Dik.E, Dik.F, Dik.G, Dik.H, Dik.I, Dik.J, Dik.K, Dik.L, Dik.M,
            Dik.N, Dik.O, Dik.P, Dik.Q, Dik.R, Dik.S, Dik.T, Dik.U, Dik.V, Dik.W, Dik.X, Dik.Y, Dik.Z,
        ];
        string[] letterNames =
        [
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        ];
        for (int i = 0; i < letters.Length; i++)
        {
            names[letters[i]] = letterNames[i];
        }

        int[] digits =
        [
            Dik.D1, Dik.D2, Dik.D3, Dik.D4, Dik.D5, Dik.D6, Dik.D7, Dik.D8, Dik.D9, Dik.D0,
        ];
        for (int i = 0; i < digits.Length; i++)
        {
            names[digits[i]] = ((i + 1) % 10).ToString();
        }

        int[] functionKeys =
        [
            Dik.F1, Dik.F2, Dik.F3, Dik.F4, Dik.F5, Dik.F6, Dik.F7, Dik.F8, Dik.F9, Dik.F10, Dik.F11, Dik.F12,
        ];
        for (int i = 0; i < functionKeys.Length; i++)
        {
            names[functionKeys[i]] = $"F{i + 1}";
        }

        Add(names,
            (Dik.Escape, "Esc"), (Dik.Tab, "Tab"), (Dik.Capital, "CapsLock"),
            (Dik.Return, "Enter"), (Dik.Space, "Space"), (Dik.Back, "Backspace"),
            (Dik.Minus, "-"), (Dik.Equal, "="), (Dik.LeftBracket, "["), (Dik.RightBracket, "]"),
            (Dik.Backslash, "\\"), (Dik.Semicolon, ";"), (Dik.Apostrophe, "'"),
            (Dik.Grave, "`"), (Dik.Comma, ","), (Dik.Period, "."), (Dik.Slash, "/"),
            (Dik.Oem102, "OEM 102"),
            (Dik.LeftShift, "LShift"), (Dik.RightShift, "RShift"),
            (Dik.LeftControl, "LCtrl"), (Dik.RightControl, "RCtrl"),
            (Dik.LeftAlt, "LAlt"), (Dik.RightAlt, "RAlt"),
            (Dik.LeftWin, "LWin"), (Dik.RightWin, "RWin"), (Dik.Apps, "Menu"),
            (Dik.NumLock, "NumLock"), (Dik.Scroll, "ScrollLock"),
            (Dik.Divide, "Num /"), (Dik.Multiply, "Num *"), (Dik.Subtract, "Num -"), (Dik.Add, "Num +"),
            (Dik.Decimal, "Num ."), (Dik.NumpadEnter, "Num Enter"),
            (Dik.Numpad0, "Num 0"), (Dik.Numpad1, "Num 1"), (Dik.Numpad2, "Num 2"), (Dik.Numpad3, "Num 3"),
            (Dik.Numpad4, "Num 4"), (Dik.Numpad5, "Num 5"), (Dik.Numpad6, "Num 6"), (Dik.Numpad7, "Num 7"),
            (Dik.Numpad8, "Num 8"), (Dik.Numpad9, "Num 9"),
            (Dik.Insert, "Insert"), (Dik.Delete, "Delete"),
            (Dik.Home, "Home"), (Dik.End, "End"),
            (Dik.PageUp, "PageUp"), (Dik.PageDown, "PageDown"),
            (Dik.Up, "↑"), (Dik.Down, "↓"), (Dik.Left, "←"), (Dik.Right, "→"));

        return names;
    }

    private static void Add(Dictionary<int, string> names, params (int Dik, string Name)[] entries)
    {
        foreach ((int dik, string name) in entries)
        {
            names[dik] = name;
        }
    }
}
