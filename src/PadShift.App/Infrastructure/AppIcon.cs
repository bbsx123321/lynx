using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadShift.App.Infrastructure;

/// <summary>
/// Loads the embedded <c>assets/icon/padshift.ico</c> for the window and the tray. The icon is a
/// build input (<c>EmbeddedResource</c> with a stable logical name) rather than a loose file, so a
/// single-file deployment still gets the artwork. Every loader is defensive: a missing or corrupt
/// icon must never keep the app from starting.
/// </summary>
public static class AppIcon
{
    /// <summary>Logical name of the embedded icon resource (see PadShift.App.csproj).</summary>
    public const string ResourceName = "PadShift.App.Assets.padshift.ico";

    /// <summary>Opens the embedded ICO stream, or null when the resource is missing.</summary>
    public static Stream? OpenResource() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);

    /// <summary>Largest frame of the embedded ICO as a frozen WPF image, or null.</summary>
    public static ImageSource? LoadWindowIcon()
    {
        try
        {
            using Stream? stream = OpenResource();
            if (stream is null)
            {
                return null;
            }

            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            BitmapFrame? best = null;
            foreach (BitmapFrame frame in decoder.Frames)
            {
                if (best is null || frame.PixelWidth > best.PixelWidth)
                {
                    best = frame;
                }
            }

            best?.Freeze();
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Tray icon at <paramref name="size"/> px, or null when the resource cannot be read.</summary>
    public static System.Drawing.Icon? LoadTrayIcon(int size = 32)
    {
        try
        {
            using Stream? stream = OpenResource();
            if (stream is null)
            {
                return null;
            }

            return new System.Drawing.Icon(stream, new System.Drawing.Size(size, size));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
