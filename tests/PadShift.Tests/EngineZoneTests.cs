using System.Collections.Generic;
using System.Text.Json;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;
using ZoneActivator = PadShift.Core.Profile.Activator;

namespace PadShift.Tests;

/// <summary>Analog zone activators: band mapping, highZoneMax, stick magnitude and hysteresis.</summary>
public class EngineZoneTests
{
    private static RemapEngine ZoneEngine(
        ActivatorType type,
        ActivationMode mode = ActivationMode.Onetime,
        SideKind? trigger = SideKind.Right,
        StickKind? stick = null,
        AxisKind? axis = null,
        int? highZoneMax = null)
    {
        GamepadProfile profile = Kit.Profile();
        var rule = new MappingRule
        {
            Condition = new MappingCondition
            {
                Hardware = new ConditionHardware
                {
                    DeviceId = Kit.Device,
                    Trigger = trigger,
                    Stick = stick,
                    Axis = axis,
                },
                // The typed schema only has an activator slot under condition.mask; analog zones
                // take their type from here while the axis comes from condition.hardware.
                Mask = new ConditionMask
                {
                    Id = 0,
                    Activator = new ZoneActivator { Type = type, Mode = mode },
                },
            },
            Macros = new List<MacroElement> { Kit.Key(Dik.X) },
        };

        if (highZoneMax is { } max)
        {
            rule.Condition.Hardware.Extra = new Dictionary<string, JsonElement>
            {
                ["highZoneMax"] = JsonSerializer.SerializeToElement(max),
            };
        }

        profile.Mappings!.Add(rule);
        return Kit.Engine(profile);
    }

    private static bool FiresInTriggerZone(ActivatorType type, int value, int? highZoneMax = null)
    {
        RemapEngine engine = ZoneEngine(type, highZoneMax: highZoneMax);
        var state = new ControllerState();
        state.Axes[Axes.RightTrigger] = value;
        return engine.Tick(state, 0).KeyboardKeys[Dik.X];
    }

    [Theory]
    [InlineData(ActivatorType.ZoneLow, 1_000)]
    [InlineData(ActivatorType.ZoneMed, 15_000)]
    [InlineData(ActivatorType.ZoneHigh, 30_000)]
    public void Trigger_Zones_Fire_In_Their_Third(ActivatorType type, int value)
    {
        Assert.True(FiresInTriggerZone(type, value));
    }

    [Theory]
    [InlineData(ActivatorType.ZoneMed, 1_000)]
    [InlineData(ActivatorType.ZoneHigh, 15_000)]
    [InlineData(ActivatorType.ZoneLow, 30_000)]
    public void Trigger_Zones_Do_Not_Fire_Outside_Their_Third(ActivatorType type, int value)
    {
        Assert.False(FiresInTriggerZone(type, value));
    }

    [Theory]
    [InlineData(5_000, ActivatorType.ZoneLow)]
    [InlineData(15_000, ActivatorType.ZoneMed)]
    [InlineData(25_000, ActivatorType.ZoneHigh)]
    public void Stick_Zones_Use_Radial_Magnitude(int magnitude, ActivatorType type)
    {
        RemapEngine engine = ZoneEngine(type, trigger: null, stick: StickKind.Left, axis: AxisKind.X);
        var state = new ControllerState();
        state.Axes[Axes.LeftStickX] = magnitude;
        Assert.True(engine.Tick(state, 0).KeyboardKeys[Dik.X]);
    }

    [Fact]
    public void Stick_Zones_Use_Magnitude_Not_A_Single_Axis()
    {
        // Each axis alone is in the middle third (10923..21844), but the diagonal magnitude is high.
        RemapEngine high = ZoneEngine(ActivatorType.ZoneHigh, trigger: null, stick: StickKind.Left, axis: AxisKind.X);
        var state = new ControllerState();
        state.Axes[Axes.LeftStickX] = 18_000;
        state.Axes[Axes.LeftStickY] = 18_000;
        Assert.True(high.Tick(state, 0).KeyboardKeys[Dik.X]);

        RemapEngine med = ZoneEngine(ActivatorType.ZoneMed, trigger: null, stick: StickKind.Right, axis: AxisKind.Y);
        var medState = new ControllerState();
        medState.Axes[Axes.RightStickX] = 18_000;
        medState.Axes[Axes.RightStickY] = 0;
        Assert.True(med.Tick(medState, 0).KeyboardKeys[Dik.X]);
    }

    [Fact]
    public void HighZoneMax_Override_Moves_The_Med_High_Boundary()
    {
        // Default high boundary is 2*32767/3 = 21844; with highZoneMax=30000 it is 20000.
        Assert.True(FiresInTriggerZone(ActivatorType.ZoneHigh, 20_500, highZoneMax: 30_000));
        Assert.False(FiresInTriggerZone(ActivatorType.ZoneHigh, 20_500));
    }

    [Fact]
    public void Hysteresis_Prevents_Repeated_Low_Zone_Fires_At_The_Boundary()
    {
        RemapEngine engine = ZoneEngine(ActivatorType.ZoneLow);
        var state = new ControllerState();
        int lowEdge = 32767 / 3;

        // First sample is in the low band -> the mapping fires once.
        // Then jitter around the low/med boundary by less than the 300-unit hysteresis.
        int[] values = [100, lowEdge - 100, lowEdge + 100, lowEdge - 50, lowEdge + 120, lowEdge - 120];
        bool[] samples = Kit.KeySamples(engine, state, values.Length - 1, (tick, s) =>
        {
            s.Axes[Axes.RightTrigger] = values[tick];
        }, Dik.X);

        int trueSamples = 0;
        foreach (bool sample in samples)
        {
            if (sample)
            {
                trueSamples++;
            }
        }

        Assert.Equal(1, trueSamples);
    }

    [Fact]
    public void Zone_Mappings_Share_One_Active_Zone_Across_Low_Med_High()
    {
        GamepadProfile profile = Kit.Profile();
        AddZone(profile, ActivatorType.ZoneLow, Kit.Key(Dik.A));
        AddZone(profile, ActivatorType.ZoneMed, Kit.Key(Dik.B));
        AddZone(profile, ActivatorType.ZoneHigh, Kit.Key(Dik.C));
        RemapEngine engine = Kit.Engine(profile);

        var state = new ControllerState();
        state.Axes[Axes.RightTrigger] = 30_000;
        var intent = engine.Tick(state, 0);

        // Only the high mapping fires when the axis enters the high third.
        Assert.False(intent.KeyboardKeys[Dik.A]);
        Assert.False(intent.KeyboardKeys[Dik.B]);
        Assert.True(intent.KeyboardKeys[Dik.C]);
    }

    private static void AddZone(GamepadProfile profile, ActivatorType type, MacroElement macro)
    {
        profile.Mappings!.Add(new MappingRule
        {
            Condition = new MappingCondition
            {
                Hardware = new ConditionHardware
                {
                    DeviceId = Kit.Device,
                    Trigger = SideKind.Right,
                },
                Mask = new ConditionMask
                {
                    Id = 0,
                    Activator = new ZoneActivator { Type = type, Mode = ActivationMode.Onetime },
                },
            },
            Macros = new List<MacroElement> { macro },
        });
    }
}
