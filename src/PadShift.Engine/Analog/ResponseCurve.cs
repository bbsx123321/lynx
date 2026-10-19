using System;
using System.Collections.Generic;

namespace PadShift.Engine.Analog;

/// <summary>
/// Four-point piecewise-linear response curve over the raw axis domain (0..32767).
/// Points are sorted by travel distance; evaluation clamps to the first/last point,
/// which makes the curve endpoint-preserving by construction.
/// </summary>
public sealed class ResponseCurve
{
    public const int MaxValue = 32767;

    private readonly int[] _x;
    private readonly int[] _y;

    private ResponseCurve(int[] x, int[] y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>Identity curve (straight line).</summary>
    public static ResponseCurve Identity { get; } = FromPoints((0, 0), (8192, 8192), (24576, 24576), (MaxValue, MaxValue));

    /// <summary>Number of control points actually used.</summary>
    public int Count => _x.Length;

    /// <summary>Build a curve from control points. Duplicate/out-of-range values are tolerated.</summary>
    public static ResponseCurve FromPoints(params (int Travel, int Value)[] points)
    {
        if (points is null || points.Length == 0)
        {
            return Identity;
        }

        var sorted = new List<(int X, int Y)>(points.Length);
        foreach ((int travel, int value) in points)
        {
            sorted.Add((Math.Clamp(travel, 0, MaxValue), Math.Clamp(value, 0, MaxValue)));
        }

        sorted.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        var xs = new int[sorted.Count];
        var ys = new int[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            xs[i] = sorted[i].X;
            ys[i] = sorted[i].Y;
        }

        if (xs.Length == 1)
        {
            // A single point would be a constant function; keep it endpoint-preserving instead.
            return Identity;
        }

        return new ResponseCurve(xs, ys);
    }

    /// <summary>Evaluate the curve at a raw travel distance (0..32767).</summary>
    public int Evaluate(int travel) => (int)Math.Round(EvaluateRaw(travel), MidpointRounding.AwayFromZero);

    /// <summary>Evaluate the curve for a normalized input (0..1) and return a normalized output.</summary>
    public double EvaluateNormalized(double normalized)
    {
        double clamped = Math.Clamp(normalized, 0.0, 1.0);
        return EvaluateRaw((int)Math.Round(clamped * MaxValue, MidpointRounding.AwayFromZero)) / (double)MaxValue;
    }

    private double EvaluateRaw(int travel)
    {
        if (travel <= _x[0])
        {
            return _y[0];
        }

        int last = _x.Length - 1;
        if (travel >= _x[last])
        {
            return _y[last];
        }

        for (int i = 1; i <= last; i++)
        {
            if (travel > _x[i])
            {
                continue;
            }

            int x0 = _x[i - 1];
            int x1 = _x[i];
            int y0 = _y[i - 1];
            int y1 = _y[i];
            if (x1 == x0)
            {
                return y1;
            }

            double t = (travel - (double)x0) / (x1 - x0);
            return y0 + t * (y1 - y0);
        }

        return _y[last];
    }
}
