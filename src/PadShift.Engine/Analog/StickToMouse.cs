using System;

namespace PadShift.Engine.Analog;

/// <summary>
/// Right-stick �?mouse. Pure function of the stick sample: 2D radial deadzone,
/// response curve applied to the magnitude, sensitivity converted to pixels per tick,
/// optional acceleration. Identical inputs always produce identical deltas.
/// </summary>
public sealed class StickToMouse
{
    public int Deadzone { get; init; }

    public ResponseCurve Curve { get; init; } = ResponseCurve.Identity;

    /// <summary>Pixels per tick at full deflection (the reference <c>sensitivity</c>).</summary>
    public double Sensitivity { get; init; } = 8.0;

    /// <summary>Extra multiplier at full deflection; 0 disables acceleration.</summary>
    public double Acceleration { get; init; }

    /// <summary>Gamepad Y grows upwards, mouse Y grows downwards.</summary>
    public bool InvertY { get; init; } = true;

    /// <summary>Safety clamp on a single tick's delta.</summary>
    public int MaxPixelsPerTick { get; init; } = 400;

    /// <summary>Compute one tick's mouse delta from a stick sample (-32768..32767 per axis).</summary>
    public (int Dx, int Dy) Process(int x, int y)
    {
        double magnitude = Math.Sqrt((double)x * x + (double)y * y);
        if (magnitude < 1.0 || magnitude <= Deadzone)
        {
            return (0, 0);
        }

        int recalibrated = global::PadShift.Engine.Analog.Deadzone.Recalculate(
            (int)Math.Round(magnitude, MidpointRounding.AwayFromZero),
            Deadzone);
        double normalized = recalibrated / (double)global::PadShift.Engine.Analog.Deadzone.FullScale;
        double curved = Curve.EvaluateNormalized(normalized);
        if (curved <= 0.0)
        {
            return (0, 0);
        }

        double scale = Sensitivity * curved * (1.0 + Acceleration * curved);
        double dx = x / magnitude * scale;
        double dy = y / magnitude * scale;
        if (InvertY)
        {
            dy = -dy;
        }

        return (ClampPixels(dx), ClampPixels(dy));
    }

    private int ClampPixels(double value)
    {
        double limit = MaxPixelsPerTick;
        if (value > limit) value = limit;
        else if (value < -limit) value = -limit;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
