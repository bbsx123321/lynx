using System;
using System.Threading;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Engine.Hosting;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// Live-state plumbing: <see cref="ControllerStateSnapshot"/> copy semantics, unknown controllers
/// and the last polled values exposed by <see cref="RemapHost.TryGetState"/>.
/// </summary>
public class LiveStateTests
{
    private sealed class FakeSource : IInputSource
    {
        public FakeSource(string deviceId) => DeviceId = deviceId;

        public string DeviceId { get; }

        public bool IsConnected { get; set; } = true;

        public bool PressA { get; set; }

        public int LeftStickX { get; set; }

        public int LeftTrigger { get; set; }

        public bool TryPoll(ControllerState state)
        {
            state.Buttons[ButtonIds.A] = PressA;
            state.Axes[Axes.LeftStickX] = LeftStickX;
            state.Axes[Axes.LeftTrigger] = LeftTrigger;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSink : IOutputSink
    {
        public OutputIntent? Last { get; private set; }

        public void Submit(OutputIntent intent) => Last = intent;

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private static string ProfilesRoot() => Path.Combine(Path.GetTempPath(), "padshift-live-state-tests");

    [Fact]
    public void TryGetState_Returns_False_For_Unknown_Controller()
    {
        var source = new FakeSource("XInput#live-unknown");
        using var host = new RemapHost(new[] { source }, new FakeSink(), new HostOptions
        {
            ProfilesRoot = ProfilesRoot(),
            TickMs = 5,
        });

        Assert.False(host.TryGetState(Guid.NewGuid(), out ControllerStateSnapshot state));
        Assert.Null(state.Buttons);
        Assert.Null(state.Axes);
        Assert.False(state.Connected);
        Assert.Equal(0, state.TimestampMs);
    }

    [Fact]
    public void TryGetState_Returns_The_Last_Polled_Values()
    {
        var source = new FakeSource("XInput#live-values")
        {
            PressA = true,
            LeftStickX = 12345,
            LeftTrigger = 50000,
        };

        using var host = new RemapHost(new[] { source }, new FakeSink(), new HostOptions
        {
            ProfilesRoot = ProfilesRoot(),
            TickMs = 5,
        });

        Guid id = host.Controllers[0].Id;
        host.Start();
        try
        {
            ControllerStateSnapshot snapshot = WaitFor(host, id, state => state.Buttons[ButtonIds.A]);

            Assert.True(snapshot.Connected);
            Assert.Equal(12345, snapshot.Axes[Axes.LeftStickX]);
            Assert.Equal(50000, snapshot.Axes[Axes.LeftTrigger]);
            Assert.True(snapshot.TimestampMs > 0);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void Snapshot_Arrays_Are_Copies_Of_The_Engine_State()
    {
        var source = new FakeSource("XInput#live-copy")
        {
            PressA = true,
            LeftStickX = 12345,
            LeftTrigger = 50000,
        };

        using var host = new RemapHost(new[] { source }, new FakeSink(), new HostOptions
        {
            ProfilesRoot = ProfilesRoot(),
            TickMs = 5,
        });

        Guid id = host.Controllers[0].Id;
        host.Start();
        ControllerStateSnapshot snapshot = WaitFor(host, id, state => state.Buttons[ButtonIds.A]);
        host.Stop();

        // Changing the input source afterwards cannot rewrite the snapshot that was already issued.
        source.PressA = false;
        source.LeftStickX = -20000;
        source.LeftTrigger = 0;
        Assert.True(snapshot.Buttons[ButtonIds.A]);
        Assert.Equal(12345, snapshot.Axes[Axes.LeftStickX]);
        Assert.Equal(50000, snapshot.Axes[Axes.LeftTrigger]);

        // Mutating the returned arrays cannot reach the host's reusable snapshot buffer either.
        snapshot.Buttons[ButtonIds.A] = false;
        snapshot.Axes[Axes.LeftStickX] = -1;
        snapshot.Axes[Axes.LeftTrigger] = -1;

        Assert.True(host.TryGetState(id, out ControllerStateSnapshot again));
        Assert.True(again.Buttons[ButtonIds.A]);
        Assert.Equal(12345, again.Axes[Axes.LeftStickX]);
        Assert.Equal(50000, again.Axes[Axes.LeftTrigger]);
    }

    [Fact]
    public void Next_Poll_Replaces_The_Snapshot_With_New_Values()
    {
        var source = new FakeSource("XInput#live-next") { PressA = true, LeftStickX = 9000 };
        using var host = new RemapHost(new[] { source }, new FakeSink(), new HostOptions
        {
            ProfilesRoot = ProfilesRoot(),
            TickMs = 5,
        });

        Guid id = host.Controllers[0].Id;
        host.Start();
        try
        {
            Assert.True(WaitFor(host, id, state => state.Buttons[ButtonIds.A]).Buttons[ButtonIds.A]);

            source.PressA = false;
            source.LeftStickX = -9000;

            ControllerStateSnapshot updated = WaitFor(
                host,
                id,
                state => !state.Buttons[ButtonIds.A] && state.Axes[Axes.LeftStickX] == -9000);
            Assert.False(updated.Buttons[ButtonIds.A]);
            Assert.Equal(-9000, updated.Axes[Axes.LeftStickX]);
        }
        finally
        {
            host.Stop();
        }
    }

    private static ControllerStateSnapshot WaitFor(
        RemapHost host,
        Guid id,
        Func<ControllerStateSnapshot, bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        ControllerStateSnapshot latest = default;
        while (DateTime.UtcNow < deadline)
        {
            if (host.TryGetState(id, out latest) && predicate(latest))
            {
                return latest;
            }

            Thread.Sleep(5);
        }

        Assert.Fail("timed out waiting for the expected live state");
        return latest;
    }
}
