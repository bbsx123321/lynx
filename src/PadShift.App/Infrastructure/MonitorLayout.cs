using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace PadShift.App.Infrastructure;

/// <summary>A rectangle in screen coordinates. The overlay uses this for both physical pixels
/// (<see cref="MonitorDescriptor.PhysicalBounds"/>) and device-independent pixels
/// (<see cref="MonitorDescriptor.DipBounds"/>).</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public int CenterX => Left + (Width / 2);

    public int CenterY => Top + (Height / 2);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>An absolute screen point (device-independent pixels unless the caller says otherwise).</summary>
public readonly record struct MonitorPoint(int X, int Y);

/// <summary>Crosshair offset from the selected monitor's centre, in DIPs.</summary>
public readonly record struct MonitorOffset(int X, int Y);

/// <summary>One physical display as reported by <c>System.Windows.Forms.Screen</c> plus its DPI scale.</summary>
public sealed record MonitorDescriptor(
    string DeviceName,
    bool IsPrimary,
    MonitorBounds PhysicalBounds,
    double ScaleX,
    double ScaleY)
{
    /// <summary>The monitor rectangle converted to device-independent pixels.</summary>
    public MonitorBounds DipBounds => MonitorLayout.ToDip(PhysicalBounds, ScaleX, ScaleY);
}

/// <summary>
/// Pure monitor placement math plus the Win32/WinForms enumeration the overlay needs. The overlay
/// window covers the whole virtual screen; the crosshair is placed relative to one selected monitor
/// and the toast stays on the primary monitor.
/// </summary>
public static class MonitorLayout
{
    private const int MdtEffectiveDpi = 0;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint DefaultDpi = 96;

    /// <summary>
    /// Resolves the crosshair position in absolute screen coordinates for the selected monitor.
    /// <paramref name="center"/> = true anchors on the monitor centre and ignores the offset;
    /// false keeps the centre and adds <paramref name="offsetX"/>/<paramref name="offsetY"/>.
    /// </summary>
    public static MonitorPoint Resolve(
        IReadOnlyList<MonitorBounds> screenBounds,
        int selected,
        bool center,
        int offsetX,
        int offsetY)
    {
        if (screenBounds is null || screenBounds.Count == 0)
        {
            return new MonitorPoint(center ? 0 : offsetX, center ? 0 : offsetY);
        }

        int index = Math.Clamp(selected, 0, screenBounds.Count - 1);
        MonitorBounds bounds = screenBounds[index];

        int x = bounds.CenterX + (center ? 0 : offsetX);
        int y = bounds.CenterY + (center ? 0 : offsetY);
        return new MonitorPoint(x, y);
    }

    /// <summary>Four-argument overload matching the documented <c>Resolve(bounds[], selected, center, offset)</c> shape.</summary>
    public static MonitorPoint Resolve(
        IReadOnlyList<MonitorBounds> screenBounds,
        int selected,
        bool center,
        MonitorOffset offset) =>
        Resolve(screenBounds, selected, center, offset.X, offset.Y);

    /// <summary>Converts a physical-pixel rectangle to DIPs for one monitor's effective DPI.</summary>
    public static MonitorBounds ToDip(MonitorBounds physical, double scaleX, double scaleY)
    {
        double sx = scaleX > 0 && double.IsFinite(scaleX) ? scaleX : 1;
        double sy = scaleY > 0 && double.IsFinite(scaleY) ? scaleY : 1;

        return new MonitorBounds(
            (int)Math.Round(physical.Left / sx),
            (int)Math.Round(physical.Top / sy),
            Math.Max(1, (int)Math.Round(physical.Width / sx)),
            Math.Max(1, (int)Math.Round(physical.Height / sy)));
    }

    /// <summary>
    /// Enumerates the connected displays. Never throws: a session without a desktop (or a failing
    /// Win32 call) yields an empty list and callers fall back to the virtual-screen rectangle.
    /// </summary>
    public static IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        WinForms.Screen[] screens;
        try
        {
            screens = WinForms.Screen.AllScreens;
        }
        catch (Exception)
        {
            return Array.Empty<MonitorDescriptor>();
        }

        if (screens.Length == 0)
        {
            return Array.Empty<MonitorDescriptor>();
        }

        var monitors = new List<MonitorDescriptor>(screens.Length);
        foreach (WinForms.Screen screen in screens)
        {
            System.Drawing.Rectangle bounds = screen.Bounds;
            var physical = new MonitorBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            (double scaleX, double scaleY) = GetScale(physical);
            monitors.Add(new MonitorDescriptor(screen.DeviceName, screen.Primary, physical, scaleX, scaleY));
        }

        return monitors;
    }

    /// <summary>Index of <paramref name="deviceName"/> or 0 (primary/first) when it is unknown.</summary>
    public static int IndexOf(IReadOnlyList<MonitorDescriptor> monitors, string? deviceName)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            if (string.Equals(monitors[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
            {
                return i;
            }
        }

        return 0;
    }

    private static (double ScaleX, double ScaleY) GetScale(MonitorBounds physical)
    {
        try
        {
            var point = new NativePoint
            {
                X = physical.Left + Math.Max(1, physical.Width / 2),
                Y = physical.Top + Math.Max(1, physical.Height / 2),
            };

            IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero
                || GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out uint dpiY) != 0
                || dpiX == 0
                || dpiY == 0)
            {
                return (1, 1);
            }

            return (dpiX / (double)DefaultDpi, dpiY / (double)DefaultDpi);
        }
        catch (Exception)
        {
            // shcore.dll is available on Windows 8.1+, but a stripped session may still fail.
            return (1, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
