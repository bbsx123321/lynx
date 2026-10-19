using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PadShift.App.Infrastructure;

/// <summary>
/// Converts a captured WPF <see cref="Key"/> into the DIK scan code stored in
/// <c>keyboard.buttonId</c>. Uses <c>MapVirtualKey</c> (a plain user-mode API, so no elevation is
/// involved) and falls back to <see cref="KeyInterop"/> only for the display name.
/// </summary>
public static class DikMap
{
    private const uint MapVkToVsc = 0;
    private const uint MapVkToVscEx = 4;
    private const int ExtendedFlag = 0x80;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Keys that only report a modifier state and are ignored by the capture dialog.</summary>
    public static bool IsModifierOnly(Key key) => key is
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System;

    /// <summary>
    /// Converts <paramref name="key"/> to a DIK scan code. Returns false for keys that have no
    /// scan code (IME/dead keys, media keys the layout does not map, ...).
    /// </summary>
    public static bool TryFromKey(Key key, out int dik, out string name)
    {
        dik = 0;
        name = string.Empty;

        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed or Key.System)
        {
            return false;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return false;
        }

        dik = FromVirtualKey(virtualKey);
        if (dik <= 0)
        {
            return false;
        }

        name = DisplayName(key);
        return true;
    }

    /// <summary>Win32 virtual key → DIK (Set-1 make code with <c>0x80</c> for E0-prefixed keys).</summary>
    public static int FromVirtualKey(int virtualKey)
    {
        if (virtualKey <= 0)
        {
            return 0;
        }

        uint scan = MapVirtualKey((uint)virtualKey, MapVkToVsc);
        if (scan == 0)
        {
            return 0;
        }

        int dik = (int)(scan & 0xFF);
        uint extended = MapVirtualKey((uint)virtualKey, MapVkToVscEx);
        if ((extended >> 8) == 0xE0)
        {
            dik |= ExtendedFlag;
        }

        return dik;
    }

    /// <summary>Compact label for the binding panel.</summary>
    public static string DisplayName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Capital => "CapsLock",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.LeftShift => "LShift",
        Key.RightShift => "RShift",
        Key.LeftCtrl => "LCtrl",
        Key.RightCtrl => "RCtrl",
        Key.LeftAlt => "LAlt",
        Key.RightAlt => "RAlt",
        Key.LWin => "LWin",
        Key.RWin => "RWin",
        Key.Apps => "Menu",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        _ => key.ToString(),
    };
}
