using System.Globalization;
using System.Text.Json.Serialization;

namespace PadShift.App.Infrastructure;

/// <summary>One raw gyro reading (degrees/second per axis) captured while the pad lies still.</summary>
public readonly record struct GyroSample(double X, double Y, double Z);

/// <summary>Which centre estimator <see cref="GyroBiasCalculator"/> uses.</summary>
public enum GyroBiasMethod
{
    Mean = 0,
    Median = 1,
}

/// <summary>
/// Gyro zero-bias computed by the calibration wizard. <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/>
/// are subtracted from every raw gyro sample before the engine sees it; the drift fields are the
/// peak-to-peak spread of the accepted static samples (0 = perfectly still).
/// </summary>
public sealed class GyroBias
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    [JsonPropertyName("driftX")]
    public double DriftX { get; set; }

    [JsonPropertyName("driftY")]
    public double DriftY { get; set; }

    [JsonPropertyName("driftZ")]
    public double DriftZ { get; set; }

    /// <summary>Number of samples that survived outlier rejection.</summary>
    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; }

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "PadShift";

    /// <summary>True for a usable bias (finite values, both axes present).</summary>
    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) &&
        double.IsFinite(DriftX) && double.IsFinite(DriftY) && double.IsFinite(DriftZ) &&
        SampleCount > 0;

    public GyroBias Clone() => new()
    {
        X = X,
        Y = Y,
        Z = Z,
        DriftX = DriftX,
        DriftY = DriftY,
        DriftZ = DriftZ,
        SampleCount = SampleCount,
        CapturedAt = CapturedAt,
        Source = Source,
    };

    [JsonIgnore]
    public string Summary =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"X {X:F3}  Y {Y:F3}  Z {Z:F3} °/s · 漂移 X {DriftX:F3} / Y {DriftY:F3} / Z {DriftZ:F3} · {SampleCount} 个样本");
}

/// <summary>
/// Head-less gyro statistics used by the calibration wizard. Median is the default centre because a
/// single bumped sample must not shift the zero point; outlier rejection drops samples farther than
/// <c>sigma × 1.4826 × MAD</c> from the median on any axis.
/// </summary>
public static class GyroBiasCalculator
{
    public const double DefaultSigma = 3.0;

    /// <summary>Floor for the robust sigma, so perfectly repeated samples still accept normal noise.</summary>
    private const double MinimumRobustSigma = 0.25;

    public static GyroBias Compute(
        IReadOnlyList<GyroSample> samples,
        GyroBiasMethod method = GyroBiasMethod.Mean,
        bool rejectOutliers = true,
        double sigma = DefaultSigma)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return new GyroBias { SampleCount = 0, CapturedAt = DateTimeOffset.UtcNow };
        }

        List<GyroSample> accepted = rejectOutliers ? RejectOutliers(samples, sigma) : [.. samples];
        if (accepted.Count == 0)
        {
            accepted = [.. samples];
        }

        (double x, double y, double z) = Center(accepted, method);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (GyroSample sample in accepted)
        {
            minX = Math.Min(minX, sample.X);
            maxX = Math.Max(maxX, sample.X);
            minY = Math.Min(minY, sample.Y);
            maxY = Math.Max(maxY, sample.Y);
            minZ = Math.Min(minZ, sample.Z);
            maxZ = Math.Max(maxZ, sample.Z);
        }

        return new GyroBias
        {
            X = x,
            Y = y,
            Z = z,
            DriftX = maxX - minX,
            DriftY = maxY - minY,
            DriftZ = maxZ - minZ,
            SampleCount = accepted.Count,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    public static (double X, double Y, double Z) Center(
        IReadOnlyList<GyroSample> samples,
        GyroBiasMethod method)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return (0, 0, 0);
        }

        if (method == GyroBiasMethod.Median)
        {
            return (
                Median(samples.Select(sample => sample.X).ToArray()),
                Median(samples.Select(sample => sample.Y).ToArray()),
                Median(samples.Select(sample => sample.Z).ToArray()));
        }

        double x = 0, y = 0, z = 0;
        foreach (GyroSample sample in samples)
        {
            x += sample.X;
            y += sample.Y;
            z += sample.Z;
        }

        return (x / samples.Count, y / samples.Count, z / samples.Count);
    }

    public static double Median(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            return 0;
        }

        double[] copy = (double[])values.Clone();
        Array.Sort(copy);
        int middle = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[middle] : (copy[middle - 1] + copy[middle]) / 2.0;
    }

    private static List<GyroSample> RejectOutliers(IReadOnlyList<GyroSample> samples, double sigma)
    {
        if (samples.Count < 3)
        {
            return [.. samples];
        }

        double threshold = Math.Max(sigma, 0.5);
        double centerX = Median(samples.Select(sample => sample.X).ToArray());
        double centerY = Median(samples.Select(sample => sample.Y).ToArray());
        double centerZ = Median(samples.Select(sample => sample.Z).ToArray());

        double sigmaX = Math.Max(MinimumRobustSigma, 1.4826 * MedianAbsDeviation(samples, centerX, static (s, _) => s.X));
        double sigmaY = Math.Max(MinimumRobustSigma, 1.4826 * MedianAbsDeviation(samples, centerY, static (s, _) => s.Y));
        double sigmaZ = Math.Max(MinimumRobustSigma, 1.4826 * MedianAbsDeviation(samples, centerZ, static (s, _) => s.Z));

        var accepted = new List<GyroSample>(samples.Count);
        foreach (GyroSample sample in samples)
        {
            if (Math.Abs(sample.X - centerX) <= threshold * sigmaX &&
                Math.Abs(sample.Y - centerY) <= threshold * sigmaY &&
                Math.Abs(sample.Z - centerZ) <= threshold * sigmaZ)
            {
                accepted.Add(sample);
            }
        }

        return accepted;
    }

    private static double MedianAbsDeviation(
        IReadOnlyList<GyroSample> samples,
        double center,
        Func<GyroSample, double, double> selector)
    {
        var deviations = new double[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            deviations[index] = Math.Abs(selector(samples[index], center));
        }

        return Median(deviations);
    }
}
