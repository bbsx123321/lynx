using System;
using System.Collections.Generic;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;

namespace PadShift.Tests;

/// <summary>Macro program compilation and execution semantics (order, pauses, break, time scale).</summary>
public class EngineMacroTests
{
    private static RemapEngine Build(ActivationMode mode, params MacroElement[] macros)
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Start, mode, macros: macros);
        return Kit.Engine(profile);
    }

    private static ControllerState Pressed()
    {
        ControllerState state = Kit.State();
        state.Buttons[ButtonIds.A] = true;
        return state;
    }

    [Fact]
    public void Sequence_Executes_In_Array_Order()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.Key(Dik.A, "down"),
            Kit.Key(Dik.B, "down"),
            Kit.Key(Dik.A, "up"),
            Kit.Key(Dik.B, "up"));
        ControllerState state = Pressed();

        (bool A, bool B)[] expected =
        {
            (true, false),
            (true, true),
            (false, true),
            (false, false),
            (false, false),
        };

        for (int t = 0; t < expected.Length; t++)
        {
            OutputIntent intent = engine.Tick(state, t);
            Assert.Equal(expected[t], (intent.KeyboardKeys[Dik.A], intent.KeyboardKeys[Dik.B]));
        }
    }

    [Fact]
    public void Consecutive_Pauses_Merge_By_Sum()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.Pause(10),
            Kit.Pause(20),
            Kit.Key(Dik.X, "down"),
            Kit.Key(Dik.X, "up"));
        ControllerState state = Pressed();

        List<int> ticks = Kit.KeyTicks(engine, state, 100, (_, _) => { }, Dik.X);
        Assert.Equal(new[] { 30 }, ticks);
    }

    [Fact]
    public void Zero_Pause_Is_Dropped()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.Pause(0),
            Kit.Key(Dik.X, "down"),
            Kit.Key(Dik.X, "up"));
        ControllerState state = Pressed();

        List<int> ticks = Kit.KeyTicks(engine, state, 20, (_, _) => { }, Dik.X);
        Assert.Equal(new[] { 0 }, ticks);
    }

    [Fact]
    public void Break_Stops_Turbo_And_Does_Not_Loop()
    {
        RemapEngine engine = Build(
            ActivationMode.Turbo,
            Kit.Key(Dik.X, "down"),
            Kit.Key(Dik.X, "up"),
            Kit.Break());
        ControllerState state = Pressed();

        List<int> ticks = Kit.KeyTicks(engine, state, 500, (_, _) => { }, Dik.X);
        Assert.Equal(new[] { 0 }, ticks);
    }

    [Theory]
    [InlineData(1L, 11)]
    [InlineData(2L, 101)]
    [InlineData(null, 101)]
    public void MinTimeUnit_Not_One_Multiplies_Pauses_By_Ten(long? minTimeUnit, int releaseTick)
    {
        GamepadProfile profile = Kit.Profile();
        profile.Config.MinTimeUnit = minTimeUnit;
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(
            profile,
            1,
            ActivatorType.Start,
            ActivationMode.Onetime,
            macros: new[] { Kit.Key(Dik.X, "down"), Kit.Pause(10), Kit.Key(Dik.X, "up") });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Pressed();
        bool[] samples = Kit.KeySamples(engine, state, 200, (_, _) => { }, Dik.X);

        int held = 0;
        foreach (bool sample in samples)
        {
            if (sample) held++;
        }

        Assert.Equal(releaseTick, held);
        Assert.True(samples[releaseTick - 1]);
        Assert.False(samples[releaseTick]);
    }

    [Fact]
    public void Mouse_Move_Wheel_And_Button_Are_Emitted()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.MouseMove("right", 12),
            Kit.Wheel("up"),
            Kit.MouseButton(1));
        ControllerState state = Pressed();

        OutputIntent first = engine.Tick(state, 0);
        Assert.Equal(12, first.MouseDeltaX);
        Assert.Equal(0, first.MouseWheel);

        OutputIntent second = engine.Tick(state, 1);
        Assert.Equal(0, second.MouseDeltaX);
        Assert.Equal(120, second.MouseWheel); // WHEEL_DELTA per notch

        OutputIntent third = engine.Tick(state, 2);
        Assert.Equal(1 << 0, third.MouseButtons);

        OutputIntent fourth = engine.Tick(state, 3);
        Assert.Equal(0, fourth.MouseButtons);

        Assert.Equal(0, engine.Tick(state, 4).MouseWheel);
    }

    [Fact]
    public void Gamepad_Button_And_Absolute_Axis_Are_Emitted()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.PadButton(ButtonIds.A),
            Kit.PadAxis(Axes.LeftStickX, 32767));
        ControllerState state = Pressed();

        Assert.True(engine.Tick(state, 0).PadButtons[ButtonIds.A]);
        Assert.False(engine.Tick(state, 1).PadButtons[ButtonIds.A]);
        Assert.Equal(32767, engine.Tick(state, 2).PadLeftX);
        Assert.Equal(0, engine.Tick(state, 3).PadLeftX);
    }

    [Fact]
    public void Extended_Keys_Use_The_E0_Scan_Code_Index()
    {
        RemapEngine engine = Build(
            ActivationMode.Onetime,
            Kit.Key(Dik.Up, "down"),
            Kit.Key(Dik.LeftWin, "down"),
            Kit.Key(Dik.A, "down"));
        ControllerState state = Pressed();

        Assert.True(engine.Tick(state, 0).KeyboardKeys[0xC8]); // Up = E0 48
        Assert.True(engine.Tick(state, 1).KeyboardKeys[0xDB]); // LeftWin = E0 5B
        Assert.True(engine.Tick(state, 2).KeyboardKeys[0x1E]); // A = 1E, no flag
    }

    [Fact]
    public void Unmap_Suppresses_Physical_Button_And_Unmapped_Buttons_Pass_Through()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, macros: new[] { Kit.Key(Dik.X) });
        Kit.Unmap(Kit.MapHardware(profile, ButtonIds.B));

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        state.Buttons[ButtonIds.A] = true;
        state.Buttons[ButtonIds.B] = true;
        state.Buttons[ButtonIds.X] = true;

        OutputIntent intent = engine.Tick(state, 0);

        Assert.False(intent.PadButtons[ButtonIds.A]); // consumed by the mask binding
        Assert.False(intent.PadButtons[ButtonIds.B]); // consumed by hardware.unmap
        Assert.True(intent.PadButtons[ButtonIds.X]);  // untouched physical button passes through
    }

    [Fact]
    public void Malformed_Profiles_Never_Throw_On_Load_Or_Tick()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(-5));                                                  // invalid button id
        Kit.Map(profile, 1, ActivatorType.Single, ActivationMode.Onetime, macros: new[] { Kit.Key(999) }); // out of range scan code
        Kit.Map(profile, 999, ActivatorType.Single, ActivationMode.Onetime);                   // dangling mask id
        profile.Mappings!.Add(new MappingRule());                                              // no condition source
        profile.Shifts!.Add(new ShiftRule { Id = 99, Type = ShiftType.Toggle });               // layer above MaxLayers

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        state.Buttons[0] = true;
        state.Buttons[1] = true;

        for (int t = 0; t < 20; t++)
        {
            OutputIntent intent = engine.Tick(state, t);
            Assert.NotNull(intent);
        }

        Assert.Equal(0, engine.CurrentLayer);
    }

    [Fact]
    public void Tick_Does_Not_Allocate()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddMask(profile, 2, Kit.Btn(ButtonIds.B));
        Kit.Map(profile, 1, ActivatorType.Single, ActivationMode.HoldUntilRelease, macros: new[] { Kit.Key(Dik.W) });
        Kit.Map(profile, 2, ActivatorType.Start, ActivationMode.Turbo, pause: 5, macros: new[] { Kit.Key(Dik.S) });
        Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Long, ActivationMode.Toggle, layer: 1, longWait: 50, macros: new[] { Kit.Key(Dik.D) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();

        static void Inputs(ControllerState s, int i)
        {
            s.Buttons[ButtonIds.A] = i % 7 < 3;
            s.Buttons[ButtonIds.B] = i % 5 < 2;
            s.Buttons[ButtonIds.Back] = i % 31 == 0;
        }

        // Warm up every path (JIT, lazy statics) before measuring.
        for (int i = 0; i < 400; i++)
        {
            Inputs(state, i);
            engine.Tick(state, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 400; i < 1400; i++)
        {
            Inputs(state, i);
            engine.Tick(state, i);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
