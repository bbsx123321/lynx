using System;
using System.Collections.Generic;
using System.Text.Json;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;

namespace PadShift.Tests;

/// <summary>Small builders for in-memory profiles. Tests never touch real devices or files.</summary>
internal static class Kit
{
    public const long Device = 1;

    public static GamepadProfile Profile() => new()
    {
        Config = new ProfileConfig { MinTimeUnit = 1 },
        Shifts = new List<ShiftRule>(),
        Masks = new List<MaskRule>(),
        Mappings = new List<MappingRule>(),
    };

    public static ButtonRef Btn(long buttonId, long deviceId = Device) =>
        new() { DeviceId = deviceId, ButtonId = buttonId };

    public static void AddMask(GamepadProfile profile, long id, params ButtonRef[] keys) =>
        profile.Masks!.Add(new MaskRule { Id = id, Set = new List<ButtonRef>(keys) });

    public static ShiftRule AddShift(GamepadProfile profile, long id, ShiftType type, params ButtonRef[] keys)
    {
        var shift = new ShiftRule
        {
            Id = id,
            Type = type,
            Set = keys.Length == 0 ? null : new List<ButtonRef>(keys),
        };
        profile.Shifts!.Add(shift);
        return shift;
    }

    public static MappingRule Map(
        GamepadProfile profile,
        long maskId,
        ActivatorType type,
        ActivationMode mode,
        int? layer = null,
        long? longWait = null,
        long? singleWait = null,
        long? doubleWait = null,
        long? times = null,
        long? pause = null,
        long? delay = null,
        params MacroElement[] macros)
    {
        var activator = new PadShift.Core.Profile.Activator { Type = type, Mode = mode };
        var parameters = new ActivatorParams();
        bool any = false;
        if (longWait.HasValue) { parameters.LongWaitTime = longWait; any = true; }
        if (singleWait.HasValue) { parameters.SingleWaitTime = singleWait; any = true; }
        if (doubleWait.HasValue) { parameters.DoubleWaitTime = doubleWait; any = true; }
        if (times.HasValue) { parameters.Times = times; any = true; }
        if (pause.HasValue) { parameters.Pause = pause; any = true; }
        if (delay.HasValue) { parameters.Delay = delay; any = true; }
        if (any)
        {
            activator.Params = parameters;
        }

        var rule = new MappingRule
        {
            Condition = new MappingCondition
            {
                ShiftId = layer,
                Mask = new ConditionMask { Id = maskId, Activator = activator },
            },
            Macros = new List<MacroElement>(macros),
        };

        profile.Mappings!.Add(rule);
        return rule;
    }

    public static MappingRule MapHardware(GamepadProfile profile, long buttonId)
    {
        var rule = new MappingRule
        {
            Condition = new MappingCondition
            {
                Hardware = new ConditionHardware { DeviceId = Device, ButtonId = buttonId },
            },
        };
        profile.Mappings!.Add(rule);
        return rule;
    }

    public static void Jump(MappingRule rule, long layer, long delay = 0) =>
        rule.JumpToLayer = JsonSerializer.SerializeToElement(new { layer, delay });

    public static void Unmap(MappingRule rule) =>
        rule.Hardware = new MappingHardware { Unmap = true };

    public static MacroElement Key(int dik, string? action = null) => new()
    {
        Keyboard = new KeyboardOutput
        {
            ButtonId = dik,
            Action = action is null ? null : JsonSerializer.SerializeToElement(action),
        },
    };

    public static MacroElement PadButton(int buttonId, string? action = null) => new()
    {
        Gamepad = new GamepadOutput
        {
            Id = 1,
            ButtonId = buttonId,
            Action = action is null ? null : JsonSerializer.SerializeToElement(action),
        },
    };

    public static MacroElement PadAxis(int axis, int value) => new()
    {
        Gamepad = new GamepadOutput
        {
            Id = 1,
            ButtonId = axis,
            Absolute = true,
            Value = value,
        },
    };

    public static MacroElement MouseButton(int buttonId, string? action = null) => new()
    {
        Mouse = new MouseOutput
        {
            ButtonId = buttonId,
            Action = action is null ? null : JsonSerializer.SerializeToElement(action),
        },
    };

    public static MacroElement MouseMove(string direction, int value = 10) => new()
    {
        Mouse = new MouseOutput
        {
            Direction = JsonSerializer.SerializeToElement(direction),
            Value = value,
        },
    };

    public static MacroElement Wheel(string direction, int value = 1) => new()
    {
        Mouse = new MouseOutput
        {
            Wheel = JsonSerializer.SerializeToElement(direction),
            Value = value,
        },
    };

    public static MacroElement Pause(int milliseconds) => new() { Pause = new PauseOutput { Value = milliseconds } };

    public static MacroElement Break() => new() { Break = JsonSerializer.SerializeToElement(new { }) };

    public static RemapEngine Engine(GamepadProfile profile, long deviceId = Device)
    {
        var engine = new RemapEngine(new RemapOptions { DeviceId = deviceId });
        engine.LoadProfile(profile);
        return engine;
    }

    public static ControllerState State() => new();

    /// <summary>Tick 0..until and return the ticks where <paramref name="dik"/> is held in the intent.</summary>
    public static List<int> KeyTicks(
        RemapEngine engine,
        ControllerState state,
        int until,
        Action<int, ControllerState> setInputs,
        int dik)
    {
        var ticks = new List<int>();
        for (int t = 0; t <= until; t++)
        {
            setInputs(t, state);
            if (engine.Tick(state, t).KeyboardKeys[dik])
            {
                ticks.Add(t);
            }
        }

        return ticks;
    }

    /// <summary>Sample one key on every tick, as a bool array indexed by tick.</summary>
    public static bool[] KeySamples(
        RemapEngine engine,
        ControllerState state,
        int until,
        Action<int, ControllerState> setInputs,
        int dik)
    {
        var samples = new bool[until + 1];
        for (int t = 0; t <= until; t++)
        {
            setInputs(t, state);
            samples[t] = engine.Tick(state, t).KeyboardKeys[dik];
        }

        return samples;
    }
}
