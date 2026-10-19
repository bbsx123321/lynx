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

/// <summary>Host façade: profile library, applied-config map, slots, remap state, events, polling loop.</summary>
public class HostTests
{
    private sealed class FakeSource : IInputSource
    {
        public FakeSource(string deviceId) => DeviceId = deviceId;

        public string DeviceId { get; }

        public bool IsConnected { get; set; } = true;

        public bool PressA { get; set; }

        public bool TryPoll(ControllerState state)
        {
            state.Buttons[ButtonIds.A] = PressA;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSink : IOutputSink
    {
        public int Submits;
        public int Releases;
        public OutputIntent? Last;

        public void Submit(OutputIntent intent)
        {
            Submits++;
            Last = intent;
        }

        public void ReleaseAll() => Releases++;

        public void Dispose()
        {
        }
    }

    private static string ProfilesRoot() => Path.Combine(Path.GetTempPath(), "padshift-host-tests");

    private static string WriteProfile()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        string path = Path.Combine(ProfilesRoot(), Guid.NewGuid().ToString("N") + ".rewasd");
        ProfileIo.Save(path, profile);
        return path;
    }

    [Fact]
    public async Task ApplyConfig_Records_Path_Shifts_And_State()
    {
        var source = new FakeSource("XInput#0");
        var sink = new FakeSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        IReadOnlyList<HostController> controllers = host.Controllers;
        Assert.Single(controllers);
        Guid id = controllers[0].Id;

        var events = new List<HostEvent>();
        host.Event += (_, e) => events.Add(e);

        string path = WriteProfile();
        ApplyConfigResult result = await host.ApplyConfigAsync(id, path, 1);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Slot);
        Assert.Equal(new[] { path }, host.GetAppliedConfigs(id, 1));
        Assert.Empty(host.GetAppliedConfigs(id, 0));
        Assert.Equal(1, host.GetCurrentSlot(id));
        Assert.Equal(RemapState.RemapOn, host.GetRemapState(id));
        Assert.Contains(host.GetShifts(id), shift => shift.Shift == 1);
        Assert.True(host.RemapEnabled);
        Assert.Contains(events, e => e is ConfigAppliedEvent);
        Assert.Contains(events, e => e is SlotChangedEvent);
        Assert.Contains(events, e => e is RemapStateChangedEvent);

        host.DisableRemap(id);
        Assert.Equal(RemapState.RemapOff, host.GetRemapState(id));
        Assert.True(sink.Releases >= 1);

        Assert.True(host.EnableRemap(id).Success);
        Assert.Equal(RemapState.RemapOn, host.GetRemapState(id));
    }

    [Fact]
    public async Task ApplyConfig_Rejects_Bad_Paths_Slots_And_Controllers()
    {
        var source = new FakeSource("XInput#2");
        var sink = new FakeSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        Guid id = host.Controllers[0].Id;

        Assert.False((await host.ApplyConfigAsync(id, Path.Combine(ProfilesRoot(), "missing.rewasd"), 0)).Success);
        Assert.False((await host.ApplyConfigAsync(id, WriteProfile(), 99)).Success);
        Assert.False((await host.ApplyConfigAsync(Guid.NewGuid(), WriteProfile(), 0)).Success);

        // No profile applied yet.
        Assert.False(host.EnableRemap(id).Success);
        Assert.Equal(RemapState.NothingApplied, host.GetRemapState(id));

        host.SelectSlot(id, 99);
        Assert.Equal(0, host.GetCurrentSlot(id));
        host.SelectSlot(id, 2);
        Assert.Equal(2, host.GetCurrentSlot(id));
    }

    [Fact]
    public void Vibrate_Forwards_To_The_Sink()
    {
        var source = new FakeSource("XInput#3");
        var sink = new FakeSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot() });

        Guid id = host.Controllers[0].Id;
        host.Vibrate(id, 123, 45, 200);

        Assert.Equal(1, sink.Submits);
        Assert.NotNull(sink.Last);
        Assert.Equal(123, sink.Last!.RumbleLeft);
        Assert.Equal(45, sink.Last.RumbleRight);
    }

    [Fact]
    public async Task Start_Polls_Sources_And_Emits_Connected()
    {
        var source = new FakeSource("XInput#4");
        var sink = new FakeSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Event += (_, e) =>
        {
            if (e is ControllerConnectedEvent)
            {
                connected.TrySetResult(true);
            }
        };

        host.Start();
        Task finished = await Task.WhenAny(connected.Task, Task.Delay(2000));
        host.Stop();

        Assert.Same(connected.Task, finished);
        Assert.True(await connected.Task);
    }
}
