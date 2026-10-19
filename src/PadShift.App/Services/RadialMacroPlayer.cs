using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using PadShift.App.Infrastructure;

namespace PadShift.App.Services;

/// <summary>
/// Minimal keyboard macro player for radial-menu "执行宏" sectors. The text is a small DSL:
/// <c>Ctrl+Shift+Esc</c> holds the chord for 40 ms, <c>;</c> or <c>,</c> separates steps and
/// <c>pause:250</c> waits. It injects through <c>SendInput</c> with scan codes, so it works without
/// a driver. The engine's multi-step profile macros are richer; this is the client-side fallback the
/// radial menu documents.
/// </summary>
public sealed class RadialMacroPlayer
{
    /// <summary>停宏热键置位；下一次分片等待前生效，正在按下的键会先正常抬起。</summary>
    private static volatile bool _stopRequested;

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const int ChordHoldMs = 40;
    private const int MaxPauseMs = 5000;

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    private static readonly string[] ModifierOrder = ["Ctrl", "Shift", "Alt", "Win"];

    /// <summary>停宏热键入口：请求停止当前/下一次宏播放。</summary>
    public static void RequestStop() => _stopRequested = true;

    /// <summary>Parses and plays <paramref name="macro"/>; returns false with a Chinese reason.</summary>
    public bool TryPlay(string? macro, out string? error, Action<int>? sleep = null)
    {
        _stopRequested = false;
        error = null;
        string text = (macro ?? "").Trim();
        if (text.Length == 0)
        {
            error = "宏内容为空";
            return false;
        }

        string[] steps = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (steps.Length == 0)
        {
            error = "宏内容为空";
            return false;
        }

        Action<int> wait = Interruptible(sleep ?? Thread.Sleep);

        foreach (string rawStep in steps)
        {
            if (_stopRequested)
            {
                error = "宏已被停宏热键中断";
                return false;
            }

            string step = rawStep.Trim();
            if (step.Length == 0)
            {
                continue;
            }

            if (step.StartsWith("pause:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(step[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pause))
                {
                    error = "无法解析暂停时长: " + step;
                    return false;
                }

                wait(Math.Clamp(pause, 0, MaxPauseMs));
                continue;
            }

            if (!TryPlayChord(step, wait, out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把整段等待切成 50 ms 片，停宏请求最多 50 ms 内生效。</summary>
    private static Action<int> Interruptible(Action<int> rawWait) => ms =>
    {
        int remaining = ms;
        while (remaining > 0 && !_stopRequested)
        {
            int slice = Math.Min(50, remaining);
            rawWait(slice);
            remaining -= slice;
        }
    };

    private static bool TryPlayChord(string step, Action<int> wait, out string? error)
    {
        error = null;
        string[] tokens = step.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "空的按键步骤";
            return false;
        }

        var modifiers = new List<int>(4);
        var keys = new List<int>(4);

        foreach (string token in tokens)
        {
            if (!TryParseKey(token, out Key key))
            {
                error = $"无法识别按键“{token}”（示例：Ctrl+Shift+Esc、F5、pause:200）";
                return false;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            int dik = DikMap.FromVirtualKey(virtualKey);
            if (dik <= 0)
            {
                error = $"按键“{token}”没有可用的扫描码";
                return false;
            }

            if (IsModifierToken(token))
            {
                modifiers.Add(dik);
            }
            else
            {
                keys.Add(dik);
            }
        }

        if (keys.Count == 0)
        {
            error = "按键步骤至少需要一个非修饰键";
            return false;
        }

        // Send modifiers in a stable order, then the actual keys, then release in reverse order.
        var downOrder = new List<int>(modifiers.Count + keys.Count);
        foreach (string modifier in ModifierOrder)
        {
            int dik = ModifierDik(modifier);
            if (modifiers.Contains(dik))
            {
                downOrder.Add(dik);
            }
        }

        downOrder.AddRange(keys);

        foreach (int dik in downOrder)
        {
            Send(dik, down: true);
        }

        wait(ChordHoldMs);

        for (int index = downOrder.Count - 1; index >= 0; index--)
        {
            Send(downOrder[index], down: false);
        }

        return true;
    }

    private static bool IsModifierToken(string token) =>
        token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("Windows", StringComparison.OrdinalIgnoreCase);

    private static int ModifierDik(string modifier) => modifier switch
    {
        "Ctrl" => DikMap.FromVirtualKey(0x11),
        "Shift" => DikMap.FromVirtualKey(0x10),
        "Alt" => DikMap.FromVirtualKey(0x12),
        _ => DikMap.FromVirtualKey(0x5B),
    };

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        string text = token.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.LeftCtrl;
            return true;
        }

        if (text.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.LeftShift;
            return true;
        }

        if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.LeftAlt;
            return true;
        }

        if (text.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.LWin;
            return true;
        }

        if (text.Equals("Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }

        if (text.Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Return", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Return;
            return true;
        }

        if (text.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Space;
            return true;
        }

        if (text.Equals("Tab", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Tab;
            return true;
        }

        if (text.Equals("Backspace", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Back;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }

    private static void Send(int dik, bool down)
    {
        uint flags = KeyEventScanCode;
        if ((dik & 0x80) != 0)
        {
            flags |= KeyEventExtendedKey;
        }

        if (!down)
        {
            flags |= KeyEventKeyUp;
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Scan = (ushort)(dik & ~0x80),
                    Flags = flags,
                },
            },
        };

        SendInput(1, [input], InputSize);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
