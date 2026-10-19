using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace PadShift.App.Infrastructure;

/// <summary>Crosshair shapes drawn by <c>OverlayWindow</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CrosshairShape>))]
public enum CrosshairShape
{
    /// <summary>Two crossing bars (十字).</summary>
    Cross = 0,

    /// <summary>A single filled dot (点).</summary>
    Dot = 1,

    /// <summary>A hollow circle (圆).</summary>
    Circle = 2,
}

/// <summary>
/// Screen overlay configuration persisted in <c>settings.json</c>: the mapping-status toast and the
/// crosshair (position, shape, size, thickness, colour and the optional magnifier ring).
/// </summary>
public sealed class OverlaySettings
{
    public const int MinSize = 6;
    public const int MaxSize = 200;
    public const int MinThickness = 1;
    public const int MaxThickness = 12;
    public const int MinMagnification = 1;
    public const int MaxMagnification = 16;
    public const int MinMagnifierPercent = 10;
    public const int MaxMagnifierPercent = 100;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Show the 1.5 s mapping-status toast.</summary>
    [JsonPropertyName("showToast")]
    public bool ShowToast { get; set; } = true;

    /// <summary>
    /// <c>Screen.DeviceName</c> of the monitor the crosshair is centred on (for example
    /// <c>\\.\DISPLAY2</c>). Empty/unknown means the primary monitor. Toasts always stay on the
    /// primary monitor.
    /// </summary>
    [JsonPropertyName("monitor")]
    public string MonitorDeviceName { get; set; } = "";

    [JsonPropertyName("showCrosshair")]
    public bool ShowCrosshair { get; set; }

    /// <summary>True = screen centre; false = centre plus <see cref="OffsetX"/>/<see cref="OffsetY"/>.</summary>
    [JsonPropertyName("centerOnScreen")]
    public bool CenterOnScreen { get; set; } = true;

    [JsonPropertyName("offsetX")]
    public int OffsetX { get; set; }

    [JsonPropertyName("offsetY")]
    public int OffsetY { get; set; }

    [JsonPropertyName("shape")]
    public CrosshairShape Shape { get; set; } = CrosshairShape.Cross;

    /// <summary>Overall crosshair size in device-independent pixels.</summary>
    [JsonPropertyName("size")]
    public double Size { get; set; } = 30;

    [JsonPropertyName("thickness")]
    public double Thickness { get; set; } = 2;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FF5CFF8A";

    /// <summary>Optional magnifier ring. Uses GDI screen sampling only; it never captures input.</summary>
    [JsonPropertyName("useMagnifier")]
    public bool UseMagnifier { get; set; }

    [JsonPropertyName("magnification")]
    public int Magnification { get; set; } = 2;

    /// <summary>Ring diameter as a percentage of the crosshair size (10–100).</summary>
    [JsonPropertyName("magnifierSizePercent")]
    public int MagnifierSizePercent { get; set; } = 30;

    public void Normalize()
    {
        Size = Math.Clamp(Size, MinSize, MaxSize);
        Thickness = Math.Clamp(Thickness, MinThickness, MaxThickness);
        Magnification = Math.Clamp(Magnification, MinMagnification, MaxMagnification);
        MagnifierSizePercent = Math.Clamp(MagnifierSizePercent, MinMagnifierPercent, MaxMagnifierPercent);
        OffsetX = Math.Clamp(OffsetX, -4000, 4000);
        OffsetY = Math.Clamp(OffsetY, -4000, 4000);
        MonitorDeviceName = (MonitorDeviceName ?? "").Trim();

        if (!Enum.IsDefined(Shape))
        {
            Shape = CrosshairShape.Cross;
        }

        Color = NormalizeColor(Color);
    }

    /// <summary>Parses <c>#AARRGGBB</c>/<c>#RRGGBB</c>/named colours; invalid input becomes green.</summary>
    public static string NormalizeColor(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return "#FF5CFF8A";
        }

        try
        {
            Color parsed = (Color)ColorConverter.ConvertFromString(text)!;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"#{parsed.A:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}");
        }
        catch (Exception)
        {
            return "#FF5CFF8A";
        }
    }
}
