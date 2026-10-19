using System;
using System.Collections.Generic;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;

namespace PadShift.Tests;

/// <summary>Layers (shifts): toggle, hold/default, unheritable masks, unlock, jumpToLayer, base layer.</summary>
public class EngineLayerTests
{
    [Fact]
    public void Toggle_Shift_Switches_Layers_And_Enables_Only_Matching_Mappings()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 1, macros: new[] { Kit.Key(Dik.Y) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var x = new List<int>();
        var y = new List<int>();
        var layers = new Dictionary<int, int>();

        for (int t = 0; t <= 80; t++)
        {
            state.Buttons[ButtonIds.Back] = (t >= 0 && t < 5) || (t >= 20 && t < 25);
            state.Buttons[ButtonIds.A] = (t >= 10 && t < 15) || (t >= 30 && t < 35);

            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) x.Add(t);
            if (intent.KeyboardKeys[Dik.Y]) y.Add(t);
            layers[t] = engine.CurrentLayer;
        }

        Assert.Equal(1, layers[5]);
        Assert.Equal(0, layers[25]);
        Assert.Equal(new[] { 30 }, x);
        Assert.Equal(new[] { 10 }, y);
    }

    [Fact]
    public void Hold_Shift_Is_Active_Only_While_The_Trigger_Is_Held()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Default, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 1, macros: new[] { Kit.Key(Dik.Y) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var x = new List<int>();
        var y = new List<int>();
        var layers = new Dictionary<int, int>();

        for (int t = 0; t <= 100; t++)
        {
            state.Buttons[ButtonIds.Back] = t >= 10 && t < 50;
            state.Buttons[ButtonIds.A] = (t >= 20 && t < 25) || (t >= 60 && t < 65);

            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) x.Add(t);
            if (intent.KeyboardKeys[Dik.Y]) y.Add(t);
            layers[t] = engine.CurrentLayer;
        }

        Assert.Equal(0, layers[5]);
        Assert.Equal(1, layers[30]);
        Assert.Equal(0, layers[60]);
        Assert.Equal(new[] { 60 }, x);
        Assert.Equal(new[] { 20 }, y);
    }

    [Fact]
    public void UnheritableMasks_Stop_Applying_On_The_Layer()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        ShiftRule shift = Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        shift.UnheritableMasks = new List<long> { 1 };
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var x = new List<int>();

        for (int t = 0; t <= 80; t++)
        {
            state.Buttons[ButtonIds.Back] = (t >= 10 && t < 15) || (t >= 30 && t < 35);
            state.Buttons[ButtonIds.A] = (t >= 0 && t < 5) || (t >= 20 && t < 25) || (t >= 40 && t < 45);

            if (engine.Tick(state, t).KeyboardKeys[Dik.X]) x.Add(t);
        }

        // fires in the base layer, is suppressed inside layer 1, fires again after returning to base
        Assert.Equal(new[] { 0, 40 }, x);
        Assert.Equal(0, engine.CurrentLayer);
    }

    [Fact]
    public void JumpToLayer_Applies_Immediately()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        MappingRule rule = Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });
        Kit.Jump(rule, 2);

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();

        for (int t = 0; t <= 9; t++)
        {
            state.Buttons[ButtonIds.A] = false;
            engine.Tick(state, t);
        }

        Assert.Equal(0, engine.CurrentLayer);
        state.Buttons[ButtonIds.A] = true;
        engine.Tick(state, 10);
        Assert.Equal(2, engine.CurrentLayer);
    }

    [Fact]
    public void JumpToLayer_Honours_The_Delay()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        MappingRule rule = Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });
        Kit.Jump(rule, 3, delay: 50);

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();

        for (int t = 0; t <= 120; t++)
        {
            state.Buttons[ButtonIds.A] = t >= 10 && t < 20;
            engine.Tick(state, t);
            if (t < 60)
            {
                Assert.NotEqual(3, engine.CurrentLayer);
            }
        }

        Assert.Equal(3, engine.CurrentLayer);
    }

    [Fact]
    public void Default_Shift_Without_A_Trigger_Is_The_Base_Layer()
    {
        // gamepad-to-kbm.rewasd declares shifts[0] = { id: 1, type: "default" } with no `set`,
        // and puts every mapping on shiftId 1: that shift is the profile's main layer.
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Default);
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 1, macros: new[] { Kit.Key(Dik.X) });

        RemapEngine engine = Kit.Engine(profile);
        Assert.Equal(1, engine.CurrentLayer);

        ControllerState state = Kit.State();
        state.Buttons[ButtonIds.A] = true;
        Assert.True(engine.Tick(state, 0).KeyboardKeys[Dik.X]);
    }

    [Fact]
    public void Unlock_Shift_Suspends_All_Mappings_While_Held()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Unlock, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var x = new List<int>();

        for (int t = 0; t <= 60; t++)
        {
            state.Buttons[ButtonIds.Back] = t >= 10 && t < 30;
            state.Buttons[ButtonIds.A] = (t >= 15 && t < 20) || (t >= 40 && t < 45);

            if (engine.Tick(state, t).KeyboardKeys[Dik.X]) x.Add(t);
        }

        Assert.Equal(new[] { 40 }, x);
    }

    [Fact]
    public void UnlockKeyboardOnly_Gates_Keyboard_But_Keeps_Mouse()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.UnlockKeyboardOnly, Kit.Btn(ButtonIds.Back));
        Kit.Map(
            profile,
            1,
            ActivatorType.Start,
            ActivationMode.Onetime,
            layer: 0,
            macros: new[] { Kit.Key(Dik.X), Kit.MouseButton(1) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        bool keyboard = false;
        bool mouse = false;

        for (int t = 0; t <= 20; t++)
        {
            state.Buttons[ButtonIds.Back] = t >= 5 && t < 15;
            state.Buttons[ButtonIds.A] = t >= 10 && t < 15;
            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) keyboard = true;
            if ((intent.MouseButtons & 1) != 0) mouse = true;
        }

        Assert.False(keyboard);
        Assert.True(mouse);
    }

    [Fact]
    public void UnlockMouseOnly_Gates_Mouse_But_Keeps_Keyboard()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.UnlockMouseOnly, Kit.Btn(ButtonIds.Back));
        Kit.Map(
            profile,
            1,
            ActivatorType.Start,
            ActivationMode.Onetime,
            layer: 0,
            macros: new[] { Kit.Key(Dik.X), Kit.MouseButton(1) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        bool keyboard = false;
        bool mouse = false;

        for (int t = 0; t <= 20; t++)
        {
            state.Buttons[ButtonIds.Back] = t >= 5 && t < 15;
            state.Buttons[ButtonIds.A] = t >= 10 && t < 15;
            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) keyboard = true;
            if ((intent.MouseButtons & 1) != 0) mouse = true;
        }

        Assert.True(keyboard);
        Assert.False(mouse);
    }

    [Fact]
    public void LayerChanged_Reports_Toggle_Hold_And_Jump()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        Kit.AddShift(profile, 2, ShiftType.Default, Kit.Btn(ButtonIds.Start));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        RemapEngine engine = Kit.Engine(profile);
        var events = new List<(int Layer, string Reason)>();
        engine.LayerChanged += (layer, reason) => events.Add((layer, reason));

        ControllerState state = Kit.State();

        state.Buttons[ButtonIds.Back] = true;
        engine.Tick(state, 0);
        state.Buttons[ButtonIds.Back] = false;
        engine.Tick(state, 1);

        state.Buttons[ButtonIds.Back] = true;
        engine.Tick(state, 2);
        state.Buttons[ButtonIds.Back] = false;
        engine.Tick(state, 3);

        state.Buttons[ButtonIds.Start] = true;
        engine.Tick(state, 4);
        state.Buttons[ButtonIds.Start] = false;
        engine.Tick(state, 5);

        Assert.Equal(new[] { (1, "toggle"), (0, "toggle"), (2, "hold"), (0, "release") }, events);
    }
}
