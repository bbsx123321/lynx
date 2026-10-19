using System;
using System.Collections.Generic;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;

namespace PadShift.Tests;

/// <summary>Activation modes: hold_until_release / onetime / repeat / turbo / toggle.</summary>
public class EngineModeTests
{
    private static RemapEngine Build(
        ActivationMode mode,
        ActivatorType type = ActivatorType.Start,
        long? longWait = null,
        long? times = null,
        long? pause = null)
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, type, mode, longWait: longWait, times: times, pause: pause, macros: new[] { Kit.Key(Dik.X) });
        return Kit.Engine(profile);
    }

    [Fact]
    public void HoldUntilRelease_Keeps_The_Key_Down_While_The_Source_Is_Held()
    {
        RemapEngine engine = Build(ActivationMode.HoldUntilRelease, ActivatorType.Long, longWait: 100);
        ControllerState state = Kit.State();
        bool[] samples = Kit.KeySamples(engine, state, 400, (t, s) => s.Buttons[ButtonIds.A] = t < 300, Dik.X);

        Assert.False(samples[99]);
        Assert.True(samples[100]);
        Assert.True(samples[299]);
        Assert.False(samples[300]);
    }

    [Fact]
    public void Onetime_Taps_For_Exactly_One_Tick()
    {
        RemapEngine engine = Build(ActivationMode.Onetime);
        ControllerState state = Kit.State();
        bool[] samples = Kit.KeySamples(engine, state, 20, (t, s) => s.Buttons[ButtonIds.A] = t < 10, Dik.X);

        Assert.True(samples[0]);
        for (int t = 1; t < samples.Length; t++)
        {
            Assert.False(samples[t]);
        }
    }

    [Fact]
    public void Repeat_Pulses_Times_With_Pause()
    {
        RemapEngine engine = Build(ActivationMode.Repeat, times: 3, pause: 100);
        ControllerState state = Kit.State();
        List<int> ticks = Kit.KeyTicks(engine, state, 500, (t, s) => s.Buttons[ButtonIds.A] = t < 400, Dik.X);

        Assert.Equal(new[] { 0, 101, 202 }, ticks);
    }

    [Fact]
    public void Turbo_Pulses_Times_With_Pause()
    {
        RemapEngine engine = Build(ActivationMode.Turbo, times: 2, pause: 50);
        ControllerState state = Kit.State();
        List<int> ticks = Kit.KeyTicks(engine, state, 400, (t, s) => s.Buttons[ButtonIds.A] = t < 300, Dik.X);

        Assert.Equal(new[] { 0, 51 }, ticks);
    }

    [Fact]
    public void Repeat_With_Absent_Times_Repeats_Until_Release()
    {
        RemapEngine engine = Build(ActivationMode.Repeat, times: null, pause: 100);
        ControllerState state = Kit.State();
        List<int> ticks = Kit.KeyTicks(engine, state, 600, (t, s) => s.Buttons[ButtonIds.A] = t < 360, Dik.X);

        Assert.Equal(new[] { 0, 101, 202, 303 }, ticks);
    }

    [Fact]
    public void Toggle_Flips_Held_State_On_Each_Press()
    {
        RemapEngine engine = Build(ActivationMode.Toggle);
        ControllerState state = Kit.State();
        bool[] samples = Kit.KeySamples(
            engine,
            state,
            260,
            (t, s) => s.Buttons[ButtonIds.A] = (t >= 0 && t < 20) || (t >= 100 && t < 120) || (t >= 200 && t < 220),
            Dik.X);

        Assert.True(samples[0]);
        Assert.True(samples[19]);
        Assert.True(samples[50]);   // released, still toggled on
        Assert.True(samples[99]);
        Assert.False(samples[100]); // second press flips it off
        Assert.False(samples[199]);
        Assert.True(samples[200]);  // third press flips it on again
        Assert.True(samples[260]);
    }

    [Fact]
    public void Toggle_Off_Clears_Key_Even_Without_Release()
    {
        RemapEngine engine = Build(ActivationMode.Toggle);
        ControllerState state = Kit.State();
        bool[] samples = Kit.KeySamples(engine, state, 100, (t, s) => s.Buttons[ButtonIds.A] = true, Dik.X);

        Assert.True(samples[0]);
        Assert.True(samples[99]);
    }
}
