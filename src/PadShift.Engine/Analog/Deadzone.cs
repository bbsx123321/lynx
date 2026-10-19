using System;

namespace PadShift.Engine.Analog;

/// <summary>
/// the reference tool-style deadzone <b>recalibration</b> (not clipping). Reproduces
/// <c>MacroCompiler.PrepareDeflectionPointsForCompiler</c> exactly:
/// <c>sign * (dz + |v| * (32768 - dz) / 32768)</c>, integer division, clamped to the signed 16-bit range.
/// </summary>
public static class Deadzone
{
    /// <summary>Full deflection used by the formula.</summary>
    public const int FullScale = 32768;

    /// <summary>Recalibrate a raw axis value through a radial deadzone.</summary>
    /// <param name="value">Raw value, nominally -32768..32767.</param>
    /// <param name="deadzone">Radial deadzone, 0..32768.</param>
    public static int Recalculate(int value, int deadzone)
    {
        if (value == 0)
        {
            return 0;
        }

        long dz = Math.Clamp((long)deadzone, 0L, FullScale);
        long magnitude = Math.Abs((long)value);
        long recalibrated = dz + magnitude * (FullScale - dz) / FullScale;
        long signed = value > 0 ? recalibrated : -recalibrated;
        return (int)Math.Clamp(signed, -32768L, 32767L);
    }

    /// <summary>Normalized magnitude after recalibration, 0..1.</summary>
    public static double Normalize(int magnitude, int deadzone) =>
        Recalculate(magnitude, deadzone) / (double)FullScale;
}
