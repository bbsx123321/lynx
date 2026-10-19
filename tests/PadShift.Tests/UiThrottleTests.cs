using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Host;
using Xunit;
using Xunit.Abstractions;

namespace PadShift.Tests;

/// <summary>
/// Regression coverage for the "LT freezes the desktop" UI-thread saturation fix: the live poll is
/// capped at 20 Hz, analog values are quantized to 1/64 before PropertyChanged is raised, the
/// adaptive valve drops to 10 Hz after five slow ticks and overlapping ticks are dropped.
/// </summary>
public class UiThrottleTests
{
    private readonly ITestOutputHelper _output;

    public UiThrottleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Sixty_Identical_Ticks_Raise_No_PropertyChanged()
    {
        var host = new FakeHost();
        host.Analog[PadShift.Core.Axes.LeftStickX] = 12345;
        host.Analog[PadShift.Core.Axes.LeftStickY] = -2345;
        host.Analog[PadShift.Core.Axes.RightStickX] = 16384;
        host.Analog[PadShift.Core.Axes.RightTrigger] = 32767;

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;
        pad.PollOnce(); // establish the frame

        int events = 0;
        pad.PropertyChanged += (_, _) => events++;
        for (int tick = 0; tick < 60; tick++)
        {
            pad.PollOnce();
        }

        Assert.Equal(0, events);
    }

    [Fact]
    public void Jitter_Below_The_Quantum_Raises_No_Events()
    {
        var host = new FakeHost();

        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;

        // Every value sits well inside a 1/64 bucket, so +/- jitter stays in the same bucket and
        // must not invalidate the diagram.
        host.Analog[PadShift.Core.Axes.LeftStickX] = 8000;
        host.Analog[PadShift.Core.Axes.LeftStickY] = -12000;
        host.Analog[PadShift.Core.Axes.RightStickX] = 16384;
        host.Analog[PadShift.Core.Axes.RightStickY] = 22222;
        host.Analog[PadShift.Core.Axes.LeftTrigger] = 20787;
        host.Analog[PadShift.Core.Axes.RightTrigger] = 41267;
        pad.PollOnce();

        int events = 0;
        pad.PropertyChanged += (_, _) => events++;
        for (int tick = 0; tick < 60; tick++)
        {
            host.Nudge(30);
            pad.PollOnce();
        }

        Assert.Equal(0, events);
    }

    [Fact]
    public void Five_Slow_Ticks_Drop_The_Refresh_Rate_To_10Hz()
    {
        using var pad = new GamepadViewModel();

        for (int tick = 0; tick < UiPollThrottle.SlowTicksBeforeDrop - 1; tick++)
        {
            pad.SimulateTickCost(TimeSpan.FromMilliseconds(31));
        }

        Assert.Equal(GamepadViewModel.PollIntervalMs, pad.CurrentPollIntervalMs);
        Assert.False(pad.PollThrottle.IsThrottled);

        // A fast tick resets the streak: four more slow ticks must not open the valve.
        pad.SimulateTickCost(TimeSpan.FromMilliseconds(5));
        for (int tick = 0; tick < UiPollThrottle.SlowTicksBeforeDrop - 1; tick++)
        {
            pad.SimulateTickCost(TimeSpan.FromMilliseconds(31));
        }

        Assert.Equal(GamepadViewModel.PollIntervalMs, pad.CurrentPollIntervalMs);

        // The fifth consecutive slow tick drops to 10 Hz.
        pad.SimulateTickCost(TimeSpan.FromMilliseconds(31));
        Assert.True(pad.PollThrottle.IsThrottled);
        Assert.Equal(GamepadViewModel.SlowPollIntervalMs, pad.CurrentPollIntervalMs);
    }

    [Fact]
    public void Overlapping_Ticks_Are_Dropped_By_The_Reentrancy_Guard()
    {
        var host = new FakeHost();
        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;

        // The host re-enters the view model the way a synchronous change handler could.
        host.OnTryGetState = () =>
        {
            host.OnTryGetState = null;
            pad.PollOnce();
        };

        pad.PollOnce();

        Assert.Equal(1, host.TryGetStateCalls);
        Assert.Equal(1, pad.DroppedTicks);
    }

    [Fact]
    public void Headless_200_Polls_With_Changing_Triggers()
    {
        var host = new FakeHost();
        using var pad = new GamepadViewModel();
        pad.AttachHost(host);
        pad.ControllerId = host.Id;

        pad.PollOnce();
        int events = 0;
        pad.PropertyChanged += (_, _) => events++;

        var watch = Stopwatch.StartNew();
        for (int tick = 0; tick < 200; tick++)
        {
            host.NextTriggerFrame();
            pad.PollOnce();
        }

        watch.Stop();
        _output.WriteLine($"HEADLESS elapsed={watch.Elapsed.TotalMilliseconds:F3}ms events={events}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "200 head-less polls should be far below 2 s");
    }

    private sealed class FakeHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Throttle Pad",
            "Xbox",
            "USB",
            VendorId: 0,
            ProductId: 0,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 100);

        private int _frame;
        private int _jitter;

        public Guid Id => _controller.Id;

        public bool[] Buttons { get; } = new bool[256];

        public int[] Analog { get; } = new int[8];

        public int TryGetStateCalls { get; private set; }

        public Action? OnTryGetState { get; set; }

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
            TryGetStateCalls++;
            OnTryGetState?.Invoke();

            if (controllerId != _controller.Id)
            {
                state = default;
                return false;
            }

            state = new ControllerStateSnapshot(
                (bool[])Buttons.Clone(),
                (int[])Analog.Clone(),
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

        public void NextTriggerFrame()
        {
            _frame++;
            Analog[PadShift.Core.Axes.LeftTrigger] = (_frame * 997) % 65536;
            Analog[PadShift.Core.Axes.RightTrigger] = (_frame * 6151) % 65536;
        }

        /// <summary>Alternates +/- <paramref name="rawDelta"/> on every analog channel.</summary>
        public void Nudge(int rawDelta)
        {
            _jitter = _jitter == 0 ? rawDelta : -_jitter;
            int[] indices =
            [
                PadShift.Core.Axes.LeftStickX,
                PadShift.Core.Axes.LeftStickY,
                PadShift.Core.Axes.RightStickX,
                PadShift.Core.Axes.RightStickY,
                PadShift.Core.Axes.LeftTrigger,
                PadShift.Core.Axes.RightTrigger,
            ];
            int[] bases = [8000, -12000, 16384, 22222, 20787, 41267];
            for (int i = 0; i < indices.Length; i++)
            {
                Analog[indices[i]] = bases[i] + _jitter;
            }
        }
    }
}
