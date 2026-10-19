using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using PadShift.Engine.Hosting;
using Xunit;

namespace PadShift.Tests;

/// <summary>Slot hotkeys: long-press detection rules and the RemapHost tick integration.</summary>
public class HostSlotHotkeyTests
{
    private sealed class FakeSource : IInputSource
    {
        private readonly int[] _buttons;

        public FakeSource(string deviceId, params int[] buttons)
        {
            DeviceId = deviceId;
            _buttons = buttons;
        }

        public string DeviceId { get; }

        public bool IsConnected { get; set; } = true;

        public bool Held { get; set; }

        public bool TryPoll(ControllerState state)
        {
            foreach (int button in _buttons)
            {
                state.Buttons[button] = Held;
            }

            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSink : IOutputSink
    {
        public void Submit(OutputIntent intent)
        {
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private static string ProfilesRoot() => Path.Combine(Path.GetTempPath(), "padshift-hotkey-tests");

    private static ControllerState HeldState(params int[] buttons)
    {
        var state = new ControllerState();
        foreach (int button in buttons)
        {
            state.Buttons[button] = true;
        }

        return state;
    }

    [Fact]
    public void Detector_Requires_HoldMs_And_Fires_Only_Once_Per_Hold()
    {
        var detector = new SlotHotkeyDetector(new[]
        {
            new SlotHotkey(2, new[] { ButtonIds.Back, ButtonIds.Start }, 500),
        });
        ControllerState state = HeldState(ButtonIds.Back, ButtonIds.Start);

        Assert.Equal(-1, detector.Detect(state, 0));
        Assert.Equal(-1, detector.Detect(state, 499));
        Assert.Equal(2, detector.Detect(state, 500));

        // Still holding: no repeat until every button is released.
        Assert.Equal(-1, detector.Detect(state, 501));
        Assert.Equal(-1, detector.Detect(state, 5000));
    }

    [Fact]
    public void Detector_Re_Arms_After_Release_And_Re_Press()
    {
        var detector = new SlotHotkeyDetector(new[]
        {
            new SlotHotkey(3, new[] { ButtonIds.LeftBumper, ButtonIds.RightBumper }, 400),
        });
        ControllerState state = HeldState(ButtonIds.LeftBumper, ButtonIds.RightBumper);

        Assert.Equal(-1, detector.Detect(state, 0));
        Assert.Equal(3, detector.Detect(state, 400));

        state.Buttons[ButtonIds.LeftBumper] = false;
        Assert.Equal(-1, detector.Detect(state, 500));

        state.Buttons[ButtonIds.LeftBumper] = true;
        Assert.Equal(-1, detector.Detect(state, 600));
        Assert.Equal(3, detector.Detect(state, 1000));
    }

    [Fact]
    public void Detector_Does_Not_Fire_A_Second_Hotkey_While_The_First_Is_Held()
    {
        var detector = new SlotHotkeyDetector(new[]
        {
            new SlotHotkey(1, new[] { ButtonIds.Back, ButtonIds.Start }, 300),
            new SlotHotkey(4, new[] { ButtonIds.A, ButtonIds.B }, 300),
        });

        // Both combos are down from the first sample: the first listed combo owns the hold.
        ControllerState state = HeldState(ButtonIds.Back, ButtonIds.Start, ButtonIds.A, ButtonIds.B);

        Assert.Equal(-1, detector.Detect(state, 0));
        Assert.Equal(1, detector.Detect(state, 300));
        Assert.Equal(-1, detector.Detect(state, 2000));
    }

    [Fact]
    public void Detector_Instances_Are_Independent_Per_Controller()
    {
        var first = new SlotHotkeyDetector(new[] { new SlotHotkey(2, new[] { ButtonIds.Back }, 200) });
        var second = new SlotHotkeyDetector(new[] { new SlotHotkey(2, new[] { ButtonIds.Back }, 200) });

        ControllerState firstState = HeldState(ButtonIds.Back);
        ControllerState secondState = new();

        Assert.Equal(-1, first.Detect(firstState, 0));
        Assert.Equal(2, first.Detect(firstState, 200));
        Assert.Equal(-1, second.Detect(secondState, 200));

        secondState.Buttons[ButtonIds.Back] = true;
        Assert.Equal(-1, second.Detect(secondState, 300));
        Assert.Equal(2, second.Detect(secondState, 500));
        Assert.Equal(-1, first.Detect(firstState, 500));
    }

    [Fact]
    public async Task RemapHost_Hotkey_Switches_Slot_Even_When_Buttons_Are_Not_In_The_Profile()
    {
        var source = new FakeSource("XInput#hotkey-1", ButtonIds.Back, ButtonIds.Start);
        var sink = new FakeSink();
        using var host = new RemapHost(
            new[] { source },
            sink,
            new HostOptions
            {
                ProfilesRoot = ProfilesRoot(),
                TickMs = 1,
                SlotHotkeys = new[] { new SlotHotkey(2, new[] { ButtonIds.Back, ButtonIds.Start }, 200) },
            });

        Guid id = host.Controllers[0].Id;

        // A profile that only consumes A: the hotkey buttons are absent from it.
        string path = Path.Combine(ProfilesRoot(), Guid.NewGuid().ToString("N") + ".rewasd");
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: null, macros: new[] { Kit.Key(Dik.X) });
        ProfileIo.Save(path, profile);

        try
        {
            ApplyConfigResult applied = await host.ApplyConfigAsync(id, path, 0);
            Assert.True(applied.Success, applied.Error);
            var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Event += (_, e) =>
            {
                if (e is SlotChangedEvent slot && slot.Slot == 1)
                {
                    changed.TrySetResult(slot.Slot);
                }
            };

            host.Start();
            source.Held = true;
            Task completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
            Assert.Same(changed.Task, completed);
            Assert.Equal(1, host.GetCurrentSlot(id));
        }
        finally
        {
            host.Stop();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemapHost_Hotkeys_On_Two_Controllers_Do_Not_Cross_Talk()
    {
        var firstSource = new FakeSource("XInput#hotkey-2a", ButtonIds.Back);
        var secondSource = new FakeSource("XInput#hotkey-2b", ButtonIds.Back);
        var sink = new FakeSink();
        using var host = new RemapHost(
            new[] { firstSource, secondSource },
            sink,
            new HostOptions
            {
                ProfilesRoot = ProfilesRoot(),
                TickMs = 1,
                SlotHotkeys = new[] { new SlotHotkey(3, new[] { ButtonIds.Back }, 200) },
            });

        IReadOnlyList<HostController> controllers = host.Controllers;
        Assert.Equal(2, controllers.Count);

        var changed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Event += (_, e) =>
        {
            if (e is SlotChangedEvent slot)
            {
                changed.TrySetResult(slot.ControllerId);
            }
        };

        host.Start();
        try
        {
            firstSource.Held = true;
            Task completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
            Assert.Same(changed.Task, completed);
            Guid switched = await changed.Task;

            Assert.Equal(2, host.GetCurrentSlot(switched));
            foreach (HostController controller in controllers)
            {
                if (controller.Id != switched)
                {
                    Assert.Equal(0, host.GetCurrentSlot(controller.Id));
                }
            }
        }
        finally
        {
            host.Stop();
        }
    }
}
