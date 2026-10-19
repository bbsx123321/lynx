using System;

namespace PadShift.Engine.Analog;

/// <summary>
/// Gyroscope → mouse. Rates are degrees/second, converted to pixels using the elapsed
/// time supplied by the caller (never a wall clock), so the output is deterministic for
/// a given call sequence. Optional exponential smoothing keeps the last filtered rate.
/// </summary>
public sealed class GyroToMouse
{
    /// <summary>Rate below which nothing is emitted, degrees/second.</summary>
    public double DeadzoneDegPerSec { get; init; }

    /// <summary>Pixels per degree of rotation.</summary>
    public double SensitivityX { get; init; } = 1.0;

    public double SensitivityY { get; init; } = 1.0;

    /// <summary>0 = off, approaching 1 = heavy smoothing (EMA on the rate).</summary>
    public double Smoothing { get; init; }

    public bool InvertY { get; init; } = true;

    public int MaxPixelsPerTick { get; init; } = 400;

    private double _yaw;
    private double _pitch;
    private bool _initialized;
    private double _residualX;
    private double _residualY;

    /// <summary>Feed one gyro sample. <paramref name="yawRate"/> is horizontal, <paramref name="pitchRate"/> vertical.</summary>
    public (int Dx, int Dy) Process(double yawRate, double pitchRate, double elapsedMs)
    {
        if (elapsedMs <= 0.0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
        {
            return (0, 0);
        }

        double yaw = ApplyDeadzone(yawRate);
        double pitch = ApplyDeadzone(pitchRate);

        if (!_initialized || Smoothing <= 0.0)
        {
            _yaw = yaw;
            _pitch = pitch;
            _initialized = true;
        }
        else
        {
            double alpha = Math.Clamp(1.0 - Smoothing, 0.01, 1.0);
            _yaw += (yaw - _yaw) * alpha;
            _pitch += (pitch - _pitch) * alpha;
        }

        double seconds = elapsedMs / 1000.0;
        double dx = _yaw * SensitivityX * seconds + _residualX;
        double dy = _pitch * SensitivityY * seconds + _residualY;
        if (InvertY)
        {
            dy = -dy;
        }

        int pixelsX = (int)Math.Truncate(dx);
        int pixelsY = (int)Math.Truncate(dy);
        _residualX = dx - pixelsX;
        _residualY = dy - pixelsY;

        return (ClampPixels(pixelsX), ClampPixels(pixelsY));
    }

    /// <summary>Forget smoothing/residual state (e.g. after a controller reconnect).</summary>
    public void Reset()
    {
        _yaw = 0;
        _pitch = 0;
        _initialized = false;
        _residualX = 0;
        _residualY = 0;
    }

    private double ApplyDeadzone(double rate)
    {
        if (DeadzoneDegPerSec <= 0.0)
        {
            return rate;
        }

        if (Math.Abs(rate) <= DeadzoneDegPerSec)
        {
            return 0.0;
        }

        return rate > 0 ? rate - DeadzoneDegPerSec : rate + DeadzoneDegPerSec;
    }

    private int ClampPixels(int value)
    {
        if (value > MaxPixelsPerTick) return MaxPixelsPerTick;
        if (value < -MaxPixelsPerTick) return -MaxPixelsPerTick;
        return value;
    }
}
