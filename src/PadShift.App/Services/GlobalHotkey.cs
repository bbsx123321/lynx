using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace PadShift.App.Services;

/// <summary>
/// 键盘全局热键（RegisterHotKey + message-only 窗口），格式 <c>Ctrl+Alt+M</c>（修饰键任意组合 +
/// 一个普通键：A–Z、0–9、F1–F12）。与径向菜单的热键机制一致：不使用低级键盘钩子、不吞按键。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly int _id;
    private readonly Action _onPressed;
    private HwndSource? _source;
    private bool _registered;

    public GlobalHotkey(int id, Action onPressed)
    {
        _id = id;
        _onPressed = onPressed;
    }

    /// <summary>解析并注册热键；格式非法或已被占用时返回 false（原注册保持不变）。</summary>
    public bool TryRegister(string? hotkey)
    {
        Unregister();
        string text = (hotkey ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (!TryParse(text, out uint modifiers, out uint vk))
        {
            return false;
        }

        EnsureSource();
        if (_source is null)
        {
            return false;
        }

        _registered = RegisterHotKey(_source.Handle, _id, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is { } source)
        {
            _ = UnregisterHotKey(source.Handle, _id);
        }

        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        // message-only 窗口：不可见、不进任务栏、不抢焦点，只收热键消息。
        var parameters = new HwndSourceParameters("LynxHotkey" + _id)
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            _onPressed();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>纯函数解析，供单测：修饰键不区分大小写；没有任何修饰键也允许（单键热键）。</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        uint mod = 0;
        string? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mod |= 0x0002;
                    continue;
                case "alt":
                    mod |= 0x0001;
                    continue;
                case "shift":
                    mod |= 0x0004;
                    continue;
                case "win":
                case "windows":
                    mod |= 0x0008;
                    continue;
            }

            if (key is not null)
            {
                return false; // 两个非修饰键
            }

            key = part;
        }

        if (key is null)
        {
            return false;
        }

        string upper = key.ToUpperInvariant();
        if (upper.Length == 1)
        {
            char c = upper[0];
            if (c is >= 'A' and <= 'Z')
            {
                vk = c;
                modifiers = mod;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = c;
                modifiers = mod;
                return true;
            }

            return false;
        }

        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper[1..], out int fx) && fx is >= 1 and <= 12)
        {
            vk = 0x70u + (uint)(fx - 1);
            modifiers = mod;
            return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
