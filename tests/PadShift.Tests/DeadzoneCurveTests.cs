using System;
using PadShift.Engine.Analog;
using Xunit;

namespace PadShift.Tests;

/// <summary>Deadzone recalibration and 4-point response curve, checked against hand-computed values.</summary>
public class DeadzoneCurveTests
{
    // sign * (dz + |v| * (32768 - dz) / 32768), integer division, clamped to -32768..32767.
    [Theory]
    [InlineData(0, 8192, 0)]            // zero stays zero
    [InlineData(16384, 8192, 20480)]    // 8192 + 16384*24576/32768 = 8192 + 12288
    [InlineData(-16384, 8192, -20480)]  // sign preserved
    [InlineData(100, 1000, 1096)]       // 1000 + 3_176_800/32768 = 1000 + 96
    [InlineData(-100, 1000, -1096)]
    [InlineData(16383, 8190, 20478)]    // 8190 + 12288 (floor of 12288.25)
    [InlineData(32767, 8190, 32767)]    // full deflection stays full
    [InlineData(32767, 0, 32767)]       // deadzone 0 is identity
    [InlineData(-32768, 0, -32768)]
    [InlineData(32767, 32768, 32767)]   // saturated deadzone clamps high
    public void Recalculate_Matches_Hand_Computed_Values(int value, int deadzone, int expected)
    {
        Assert.Equal(expected, Deadzone.Recalculate(value, deadzone));
    }

    [Fact]
    public void Curve_Interpolates_Linearly_And_Preserves_Endpoints()
    {
        ResponseCurve curve = ResponseCurve.FromPoints((0, 0), (8192, 16384), (24576, 24576), (32767, 32767));

        Assert.Equal(0, curve.Evaluate(0));
        Assert.Equal(8192, curve.Evaluate(4096));       // halfway up the first segment
        Assert.Equal(16384, curve.Evaluate(8192));
        Assert.Equal(20480, curve.Evaluate(16384));     // halfway through the middle segment
        Assert.Equal(24576, curve.Evaluate(24576));
        Assert.Equal(32767, curve.Evaluate(32767));
        Assert.Equal(0, curve.Evaluate(-100));          // clamped to the first point
        Assert.Equal(32767, curve.Evaluate(40000));     // clamped to the last point
    }

    [Fact]
    public void Identity_Curve_Is_Endpoint_Preserving()
    {
        for (int value = 0; value <= ResponseCurve.MaxValue; value += 997)
        {
            Assert.Equal(value, ResponseCurve.Identity.Evaluate(value));
        }

        Assert.Equal(0, ResponseCurve.Identity.Evaluate(0));
        Assert.Equal(32767, ResponseCurve.Identity.Evaluate(32767));
    }

    [Fact]
    public void StickToMouse_Applies_Radial_Deadzone_And_Y_Inversion()
    {
        var stick = new StickToMouse { Deadzone = 0, Sensitivity = 100 };
        Assert.Equal((100, 0), stick.Process(32767, 0));
        Assert.Equal((0, 0), stick.Process(0, 0));
        Assert.Equal((0, -100), stick.Process(0, 32767)); // pad Y grows up, mouse Y grows down

        var dead = new StickToMouse { Deadzone = 2000, Sensitivity = 100 };
        Assert.Equal((0, 0), dead.Process(1000, 0));
        Assert.Equal((0, 0), dead.Process(1414, 1414)); // radial magnitude 1999.8
    }

    [Fact]
    public void StickToMouse_Acceleration_And_Clamp_Are_Deterministic()
    {
        var accelerated = new StickToMouse { Deadzone = 0, Sensitivity = 100, Acceleration = 1 };
        Assert.Equal(200, accelerated.Process(32767, 0).Dx);
        Assert.Equal(200, accelerated.Process(32767, 0).Dx);

        var clamped = new StickToMouse { Deadzone = 0, Sensitivity = 10000, MaxPixelsPerTick = 400 };
        Assert.Equal(400, clamped.Process(32767, 0).Dx);
        Assert.Equal(-400, clamped.Process(-32767, 0).Dx);
    }

    [Fact]
    public void GyroToMouse_Scales_Rate_By_Elapsed_Time_And_Deadzone()
    {
        var gyro = new GyroToMouse { SensitivityX = 1, SensitivityY = 1, DeadzoneDegPerSec = 0 };
        Assert.Equal((9, 0), gyro.Process(900, 0, 10));   // 900 deg/s * 0.01 s
        Assert.Equal((0, -9), gyro.Process(0, 900, 10));  // inverted pitch

        var dead = new GyroToMouse { SensitivityX = 1, DeadzoneDegPerSec = 5 };
        Assert.Equal((0, 0), dead.Process(5, 0, 10));
        Assert.Equal((0, 0), dead.Process(-5, 0, 10));
    }

    [Fact]
    public void GyroToMouse_Is_Deterministic_With_Smoothing()
    {
        var a = new GyroToMouse { SensitivityX = 10, Smoothing = 0.5 };
        var b = new GyroToMouse { SensitivityX = 10, Smoothing = 0.5 };

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(a.Process(500, -200, 4), b.Process(500, -200, 4));
        }
    }
}
