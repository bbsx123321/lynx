using System;
using System.Collections.Generic;
using System.ComponentModel;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Host;

namespace PadShift.Tests;

/// <summary>
/// Head-less tests for the live gamepad diagram: trigger normalization, stick Y inversion,
/// "notify only on change" at 60 Hz and the neutral state used when nothing is connected.
/// </summary>
public class GamepadVisualTests
{
    private sealed class FakeHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Test Pad",
            "Xbox",
            "USB",
            VendorId: 0,
            ProductId: 0,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 100);

        public Guid Id => _controller.Id;

        public bool Connected { get; set; } = true;

        public bool ThrowOnTryGetState { get; set; }

        public bool[] Buttons { get; } = new bool[256];

        public int[] Axes { get; } = new int[8];

        public event EventHandler<HostEvent>? Event
        {
            add { }
            remove { }
        }

        public bool RemapEnabled => false;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) => Array.Empty<ShiftInfo>();

        public RemapState GetRemapState(Guid controllerId) => RemapState.NothingApplied;

        public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot) =>
            Task.FromResult(new ApplyConfigResult(false, "preview", slot));

        public EnableRemapResult EnableRemap(Guid? controllerId) => new(false, "preview");

        public void DisableRemap(Guid? controllerId)
        {
        }

        public void SelectSlot(Guid controllerId, int slot)
        {
        }

        public int GetCurrentSlot(Guid controllerId) => 0;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) => Array.Empty<string>();

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
        {
            if (ThrowOnTryGetState)
            {
                throw new ObjectDisposedException(nameof(FakeHost));
            }

            if (!Connected || controllerId != _controller.Id)
            {
                state = default;
                return false;
            }

            state = new ControllerStateSnapshot(
                (bool[])Buttons.Clone(),
                (int[])Axes.Clone(),
                Connected: true,
                Environment.TickCount64);
            return true;
        }

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Trigger_Normalization_Maps_0_32767_65535()
    {
        Assert.Equal(0.0, GamepadViewModel.NormalizeTrigger(0));
        Assert.Equal(0.5, GamepadViewModel.NormalizeTrigger(32767), 3);
        Assert.Equal(1.0, GamepadViewModel.NormalizeTrigger(65535));
        Assert.Equal(0.0, GamepadViewModel.NormalizeTrigger(-100));

        // Legacy 0..255 sources are normalized by 255 instead.
        Assert.Equal(1.0, GamepadViewModel.NormalizeTrigger(255), 3);
    }

    [Fact]
    public void Stick_Y_Is_Inverted_For_Screen_Coordinates()
    {
        var host = new FakeHost();
        host.Axes[Axes.LeftStickX] = 32767;
        host.Axes[Axes.LeftStickY] = 32767;
        host.Axes[Axes.RightStickX] = -32768;
        host.Axes[Axes.RightStickY] = -32768;

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;
        pad.PollOnce();

        Assert.Equal(1.0, pad.LeftStickX, 3);
        Assert.Equal(-1.0, pad.LeftStickY, 3);
        Assert.Equal(GamepadViewModel.StickTravelPixels, pad.LeftStickOffsetX, 3);
        Assert.Equal(-GamepadViewModel.StickTravelPixels, pad.LeftStickOffsetY, 3);

        Assert.Equal(-1.0, pad.RightStickX, 3);
        Assert.Equal(1.0, pad.RightStickY, 3);
        Assert.Equal(-GamepadViewModel.StickTravelPixels, pad.RightStickOffsetX, 3);
        Assert.Equal(GamepadViewModel.StickTravelPixels, pad.RightStickOffsetY, 3);
    }

    [Fact]
    public void Buttons_And_Triggers_Light_Up_Properties_And_Hit_Areas()
    {
        var host = new FakeHost();
        host.Buttons[ButtonIds.A] = true;
        host.Buttons[ButtonIds.DpadLeft] = true;
        host.Buttons[ButtonIds.Guide] = true;
        host.Buttons[ButtonIds.Paddle2] = true;
        host.Axes[Axes.LeftTrigger] = 65535;
        host.Axes[Axes.RightTrigger] = 32767;

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;
        pad.PollOnce();

        Assert.True(pad.HasController);
        Assert.False(pad.ShowConnectionHint);
        Assert.True(pad.A);
        Assert.True(pad.Left);
        Assert.True(pad.Guide);
        Assert.True(pad.P2);
        Assert.False(pad.B);
        Assert.Equal(1.0, pad.LT, 3);
        Assert.Equal(0.5, pad.RT, 3);

        Assert.True(Control(pad, GamepadButtons.A).IsPressed);
        Assert.True(Control(pad, GamepadButtons.DpadLeft).IsPressed);
        Assert.True(Control(pad, GamepadButtons.Guide).IsPressed);
        Assert.True(Control(pad, GamepadButtons.Paddle2).IsPressed);
        Assert.True(Control(pad, GamepadButtons.LeftTrigger).IsPressed);
        Assert.True(Control(pad, GamepadButtons.RightTrigger).IsPressed);
        Assert.False(Control(pad, GamepadButtons.B).IsPressed);
    }

    [Fact]
    public void Identical_Ticks_Raise_No_PropertyChanged()
    {
        var host = new FakeHost();
        host.Buttons[ButtonIds.A] = true;
        host.Buttons[ButtonIds.LeftBumper] = true;
        host.Axes[Axes.LeftStickX] = 12345;
        host.Axes[Axes.LeftStickY] = -2345;
        host.Axes[Axes.RightTrigger] = 32767;

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;

        // First tick establishes the frame; all following identical ticks must be silent.
        pad.PollOnce();

        var changed = new List<string?>();
        pad.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        for (int tick = 0; tick < 100; tick++)
        {
            pad.PollOnce();
        }

        Assert.Empty(changed);
    }

    [Fact]
    public void Disconnected_Controller_Returns_Everything_To_Neutral()
    {
        var host = new FakeHost();
        host.Buttons[ButtonIds.A] = true;
        host.Buttons[ButtonIds.Start] = true;
        host.Axes[Axes.LeftStickX] = 20000;
        host.Axes[Axes.LeftStickY] = 20000;
        host.Axes[Axes.LeftTrigger] = 65535;

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;
        pad.PollOnce();

        Assert.True(pad.HasController);
        Assert.True(pad.A);

        host.Connected = false;
        pad.PollOnce();

        Assert.False(pad.HasController);
        Assert.True(pad.ShowConnectionHint);
        Assert.False(pad.A);
        Assert.False(pad.Menu);
        Assert.False(pad.Guide);
        Assert.Equal(0.0, pad.LeftStickX);
        Assert.Equal(0.0, pad.LeftStickY);
        Assert.Equal(0.0, pad.LT);
        Assert.Equal(0.0, pad.RT);
        Assert.All(pad.Controls, control => Assert.False(control.IsPressed));
    }

    [Fact]
    public void PollOnce_Does_Not_Throw_When_The_Host_Is_Disposed()
    {
        var host = new FakeHost { ThrowOnTryGetState = true };
        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;

        Exception? error = Record.Exception(pad.PollOnce);

        Assert.Null(error);
        Assert.False(pad.HasController);
    }

    private static GamepadControlViewModel Control(GamepadViewModel pad, long buttonId)
    {
        foreach (GamepadControlViewModel control in pad.Controls)
        {
            if (control.ButtonId == buttonId)
            {
                return control;
            }
        }

        throw new InvalidOperationException($"missing control {buttonId}");
    }
}
