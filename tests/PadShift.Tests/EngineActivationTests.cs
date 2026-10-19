using System;
using System.Collections.Generic;
using System.Linq;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// Activator state machines driven by a virtual clock. Every scenario is (press intervals, until, expected fire ticks).
/// A press interval <c>(Down, Up)</c> means the button is down for ticks t with Down &lt;= t &lt; Up; the release edge is at Up.
/// </summary>
public class EngineActivationTests
{
    private const int FireKey = Dik.X;

    private sealed record Scenario(
        string Name,
        ActivatorType Type,
        int LongWait,
        int SingleWait,
        int DoubleWait,
        int Step,
        (int Down, int Up)[] Presses,
        int Until,
        int[] Expected);

    private static readonly Scenario[] Cases =
    {
        new("start fires immediately on press", ActivatorType.Start, 500, 250, 250, 1, [(0, 50)], 5, [0]),
        new("release fires on release after a press", ActivatorType.Release, 500, 250, 250, 1, [(0, 50)], 60, [50]),
        new("long fires once at longwaittime", ActivatorType.Long, 300, 250, 250, 1, [(0, 400)], 600, [300]),
        new("single fires on release when released early", ActivatorType.Single, 500, 250, 250, 1, [(0, 120)], 200, [120]),
        new("single fires at singlewaittime while held", ActivatorType.Single, 500, 250, 250, 1, [(0, 400)], 400, [250]),
        new("double fires on the second press", ActivatorType.Double, 500, 250, 250, 1, [(0, 20), (100, 120)], 500, [100]),
        new("triple fires on the third press", ActivatorType.Triple, 500, 250, 250, 1, [(0, 20), (100, 120), (200, 220)], 500, [200]),
        new("start fires on 10 ms clock steps", ActivatorType.Start, 500, 250, 250, 10, [(0, 50)], 40, [0]),
        new("long fires at threshold on 10 ms steps", ActivatorType.Long, 300, 250, 250, 10, [(0, 400)], 400, [300]),
        new("release fires on release on 10 ms steps", ActivatorType.Release, 500, 250, 250, 10, [(0, 100)], 150, [100]),
        new("double fires on second press on 10 ms steps", ActivatorType.Double, 500, 250, 250, 10, [(0, 20), (100, 120)], 400, [100]),
    };

    [Fact]
    public void Activator_Table_Matches_Expected_Fire_Ticks()
    {
        foreach (Scenario scenario in Cases)
        {
            List<int> fired = Run(scenario);
            Assert.True(
                scenario.Expected.SequenceEqual(fired),
                $"{scenario.Name}: expected [{string.Join(",", scenario.Expected)}] but fired [{string.Join(",", fired)}]");
        }
    }

    private static List<int> Run(Scenario scenario)
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(
            profile,
            1,
            scenario.Type,
            ActivationMode.Onetime,
            longWait: scenario.LongWait,
            singleWait: scenario.SingleWait,
            doubleWait: scenario.DoubleWait,
            macros: new[] { Kit.Key(FireKey) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var fired = new List<int>();

        for (int t = 0; t <= scenario.Until; t += scenario.Step)
        {
            state.Buttons[ButtonIds.A] = IsDown(scenario.Presses, t);
            if (engine.Tick(state, t).KeyboardKeys[FireKey])
            {
                fired.Add(t);
            }
        }

        return fired;
    }

    private static bool IsDown((int Down, int Up)[] presses, int t)
    {
        foreach ((int down, int up) in presses)
        {
            if (t >= down && t < up)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------- single vs double exclusion

    [Fact]
    public void Double_Suppresses_Single_For_The_Same_Press()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Single, ActivationMode.Onetime, singleWait: 250, doubleWait: 250, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Double, ActivationMode.Onetime, singleWait: 250, doubleWait: 250, macros: new[] { Kit.Key(Dik.Y) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var singleTicks = new List<int>();
        var doubleTicks = new List<int>();

        for (int t = 0; t <= 500; t++)
        {
            // tap, wait, second tap inside the double window
            state.Buttons[ButtonIds.A] = (t >= 0 && t < 20) || (t >= 100 && t < 120);
            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) singleTicks.Add(t);
            if (intent.KeyboardKeys[Dik.Y]) doubleTicks.Add(t);
        }

        Assert.Equal(new[] { 100 }, doubleTicks);
        Assert.Empty(singleTicks);
    }

    [Fact]
    public void Single_Fires_After_The_Double_Window_When_No_Second_Press_Arrives()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Single, ActivationMode.Onetime, singleWait: 250, doubleWait: 250, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Double, ActivationMode.Onetime, singleWait: 250, doubleWait: 250, macros: new[] { Kit.Key(Dik.Y) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var singleTicks = new List<int>();
        var doubleTicks = new List<int>();

        for (int t = 0; t <= 400; t++)
        {
            state.Buttons[ButtonIds.A] = t < 20;
            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) singleTicks.Add(t);
            if (intent.KeyboardKeys[Dik.Y]) doubleTicks.Add(t);
        }

        // release at 20 + 250 ms double window
        Assert.Equal(new[] { 270 }, singleTicks);
        Assert.Empty(doubleTicks);
    }

    [Fact]
    public void Triple_Takes_Precedence_Over_Double()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Double, ActivationMode.Onetime, doubleWait: 250, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Triple, ActivationMode.Onetime, doubleWait: 250, macros: new[] { Kit.Key(Dik.Y) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        var doubleTicks = new List<int>();
        var tripleTicks = new List<int>();

        for (int t = 0; t <= 600; t++)
        {
            state.Buttons[ButtonIds.A] = (t >= 0 && t < 20) || (t >= 100 && t < 120) || (t >= 200 && t < 220);
            OutputIntent intent = engine.Tick(state, t);
            if (intent.KeyboardKeys[Dik.X]) doubleTicks.Add(t);
            if (intent.KeyboardKeys[Dik.Y]) tripleTicks.Add(t);
        }

        Assert.Equal(new[] { 200 }, tripleTicks);
        Assert.Empty(doubleTicks);
    }
}
