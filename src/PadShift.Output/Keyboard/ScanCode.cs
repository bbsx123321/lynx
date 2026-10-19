namespace PadShift.Output.Keyboard;

/// <summary>
/// Windows Set 1 ("PC/AT") scan codes, plus the mouse-button bits used by
/// <see cref="PadShift.Core.OutputIntent.MouseButtons"/>.
/// </summary>
/// <remarks>
/// <para>
/// An <c>OutputIntent.KeyboardKeys</c> slot is indexed by a value from this table. The low seven
/// bits are the Set 1 make code; bit <see cref="ExtendedFlag"/> (0x80) marks keys that Windows
/// delivers with the E0 prefix. No Set 1 base make code uses bit 7, so the encoding is
/// unambiguous: 0x48 is Numpad 8 while 0xC8 is Up, 0x1C is Enter while 0x9C is Numpad Enter.
/// The injectors decode the index back to (make code, extended flag) before calling the OS.
/// </para>
/// <para>
/// Pause (E1 1D 45) and the E1-prefixed keys cannot be represented by this single-flag scheme and
/// are intentionally absent. Everything a gamepad remapper normally needs - letters, digits,
/// F1-F12, arrows, navigation cluster, modifiers, numpad, media keys and Windows keys - is here.
/// </para>
/// <para>
/// <see cref="ExtendedFlag"/> is only a layout convention. It lives in the highest bit of the
/// 0..255 index so that callers can keep using the 256-entry structures defined by
/// <see cref="PadShift.Core.OutputIntent"/> and <see cref="PadShift.Core.ControllerState"/>.
/// </para>
/// </remarks>
public static class ScanCode
{
    /// <summary>Bit set on a key index when the key is sent with the E0 prefix.</summary>
    public const int ExtendedFlag = 0x80;

    /// <summary>Number of wheel units in one notch; matches Win32 <c>WHEEL_DELTA</c>.</summary>
    public const int WheelDelta = 120;

    // Row 0 (top).
    public const int Escape = 0x01;
    public const int Digit1 = 0x02;
    public const int Digit2 = 0x03;
    public const int Digit3 = 0x04;
    public const int Digit4 = 0x05;
    public const int Digit5 = 0x06;
    public const int Digit6 = 0x07;
    public const int Digit7 = 0x08;
    public const int Digit8 = 0x09;
    public const int Digit9 = 0x0A;
    public const int Digit0 = 0x0B;
    public const int Minus = 0x0C;
    public const int EqualSign = 0x0D;
    public const int Backspace = 0x0E;
    public const int Tab = 0x0F;

    // Letters.
    public const int Q = 0x10;
    public const int W = 0x11;
    public const int E = 0x12;
    public const int R = 0x13;
    public const int T = 0x14;
    public const int Y = 0x15;
    public const int U = 0x16;
    public const int I = 0x17;
    public const int O = 0x18;
    public const int P = 0x19;
    public const int LeftBracket = 0x1A;
    public const int RightBracket = 0x1B;
    public const int Enter = 0x1C;
    public const int LeftCtrl = 0x1D;
    public const int A = 0x1E;
    public const int S = 0x1F;
    public const int D = 0x20;
    public const int F = 0x21;
    public const int G = 0x22;
    public const int H = 0x23;
    public const int J = 0x24;
    public const int K = 0x25;
    public const int L = 0x26;
    public const int Semicolon = 0x27;
    public const int Apostrophe = 0x28;
    public const int Grave = 0x29;
    public const int LeftShift = 0x2A;
    public const int Backslash = 0x2B;
    public const int Z = 0x2C;
    public const int X = 0x2D;
    public const int C = 0x2E;
    public const int V = 0x2F;
    public const int B = 0x30;
    public const int N = 0x31;
    public const int M = 0x32;
    public const int Comma = 0x33;
    public const int Period = 0x34;
    public const int Slash = 0x35;
    public const int RightShift = 0x36;
    public const int NumpadMultiply = 0x37;
    public const int LeftAlt = 0x38;
    public const int Space = 0x39;
    public const int CapsLock = 0x3A;

    // Function keys.
    public const int F1 = 0x3B;
    public const int F2 = 0x3C;
    public const int F3 = 0x3D;
    public const int F4 = 0x3E;
    public const int F5 = 0x3F;
    public const int F6 = 0x40;
    public const int F7 = 0x41;
    public const int F8 = 0x42;
    public const int F9 = 0x43;
    public const int F10 = 0x44;
    public const int F11 = 0x57;
    public const int F12 = 0x58;

    // Numpad (non-extended keys).
    public const int NumLock = 0x45;
    public const int ScrollLock = 0x46;
    public const int Numpad7 = 0x47;
    public const int Numpad8 = 0x48;
    public const int Numpad9 = 0x49;
    public const int NumpadSubtract = 0x4A;
    public const int Numpad4 = 0x4B;
    public const int Numpad5 = 0x4C;
    public const int Numpad6 = 0x4D;
    public const int NumpadAdd = 0x4E;
    public const int Numpad1 = 0x4F;
    public const int Numpad2 = 0x50;
    public const int Numpad3 = 0x51;
    public const int Numpad0 = 0x52;
    public const int NumpadDecimal = 0x53;

    // Extended (E0) keys: base make code | ExtendedFlag.
    public const int NumpadEnter = ExtendedFlag | 0x1C;
    public const int RightCtrl = ExtendedFlag | 0x1D;
    public const int NumpadDivide = ExtendedFlag | 0x35;
    public const int PrintScreen = ExtendedFlag | 0x37;
    public const int RightAlt = ExtendedFlag | 0x38;
    public const int Home = ExtendedFlag | 0x47;
    public const int Up = ExtendedFlag | 0x48;
    public const int PageUp = ExtendedFlag | 0x49;
    public const int Left = ExtendedFlag | 0x4B;
    public const int Right = ExtendedFlag | 0x4D;
    public const int End = ExtendedFlag | 0x4F;
    public const int Down = ExtendedFlag | 0x50;
    public const int PageDown = ExtendedFlag | 0x51;
    public const int Insert = ExtendedFlag | 0x52;
    public const int Delete = ExtendedFlag | 0x53;
    public const int LeftWin = ExtendedFlag | 0x5B;
    public const int RightWin = ExtendedFlag | 0x5C;
    public const int Apps = ExtendedFlag | 0x5D;

    // Extended multimedia keys.
    public const int MediaPreviousTrack = ExtendedFlag | 0x10;
    public const int MediaNextTrack = ExtendedFlag | 0x19;
    public const int VolumeMute = ExtendedFlag | 0x20;
    public const int MediaPlayPause = ExtendedFlag | 0x22;
    public const int MediaStop = ExtendedFlag | 0x24;
    public const int VolumeDown = ExtendedFlag | 0x2E;
    public const int VolumeUp = ExtendedFlag | 0x30;

    /// <summary>Digit keys in <c>1..9, 0</c> order (the order they appear on the row).</summary>
    public static readonly int[] DigitKeys =
    [
        Digit1, Digit2, Digit3, Digit4, Digit5,
        Digit6, Digit7, Digit8, Digit9, Digit0,
    ];

    /// <summary>Letter keys in alphabetic order.</summary>
    public static readonly int[] LetterKeys =
    [
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    ];

    /// <summary>F1..F12.</summary>
    public static readonly int[] FunctionKeys =
    [
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    ];

    /// <summary>Arrow keys.</summary>
    public static readonly int[] ArrowKeys = [Up, Down, Left, Right];

    /// <summary>true when <paramref name="key"/> is an E0-prefixed key.</summary>
    public static bool IsExtended(int key) => (key & ExtendedFlag) != 0;

    /// <summary>Strips <see cref="ExtendedFlag"/> and returns the raw Set 1 make code.</summary>
    public static int BaseCode(int key) => key & ~ExtendedFlag;

    /// <summary>true when the value fits a 256-slot keyboard intent array.</summary>
    public static bool IsValid(int key) => (uint)key <= byte.MaxValue;

    /// <summary>
    /// Mouse button bits for <see cref="PadShift.Core.OutputIntent.MouseButtons"/>. The values are
    /// bit positions, not the Win32 <c>MOUSEEVENTF_*</c> flags.
    /// </summary>
    public static class Mouse
    {
        public const int Left = 1 << 0;
        public const int Right = 1 << 1;
        public const int Middle = 1 << 2;
        public const int X1 = 1 << 3;
        public const int X2 = 1 << 4;

        /// <summary>All five supported buttons.</summary>
        public const int All = Left | Right | Middle | X1 | X2;
    }
}
