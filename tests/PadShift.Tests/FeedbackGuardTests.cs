using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PadShift.App.Services;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using PadShift.Engine.Hosting;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// Regression coverage for the "enable remap freezes the desktop" feedback loop: our own ViGEm pad
/// must never become an input source, hot-plug is frozen while remap is ON, and the output watchdog
/// stops a controller that keeps submitting at an impossible rate. Also locks in the rule that the
/// host never holds its gate across <see cref="IOutputSink.Submit"/>.
/// </summary>
public sealed class FeedbackGuardTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeSource : IInputSource
    {
        public FakeSource(string deviceId) => DeviceId = deviceId;

        public string DeviceId { get; }

        public bool IsConnected { get; set; } = true;

        public bool TryPoll(ControllerState state) => true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSink : IOutputSink
    {
        private int _submits;

        public int Submits => Volatile.Read(ref _submits);

        public int LockTimeouts;

        public Action? OnSubmit;

        public void Submit(OutputIntent intent)
        {
            Action? callback = OnSubmit;
            if (callback is not null)
            {
                OnSubmit = null;
                callback();
            }

            Interlocked.Increment(ref _submits);
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string NewProfilesRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "padshift-feedback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteProfile(string root)
    {
        GamepadProfile profile = Kit.Profile();
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".rewasd");
        ProfileIo.Save(path, profile);
        return path;
    }

    // ------------------------------------------------------------------ 1) self-source filtered

    [Fact]
    public async Task VirtualPadSource_IsFiltered_AndNeverTickled()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);

        var real = new FakeSource("XInput#0");
        var ownVirtualPad = new FakeSource("XInput#1");
        var sink = new FakeSink();

        using var host = new RemapHost(
            new IInputSource[] { real, ownVirtualPad },
            sink,
            new HostOptions
            {
                ProfilesRoot = root,
                TickMs = 1000,
                ExcludedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XInput#1" },
            });

        HostController only = Assert.Single(host.Controllers);
        Assert.Equal("XInput#0", only.Name);
        Assert.DoesNotContain(host.Controllers, controller => controller.Name == "XInput#1");

        ApplyConfigResult applied = await host.ApplyConfigAsync(only.Id, path, 0);
        Assert.True(applied.Success, applied.Error);

        // One pump must produce exactly one submission: the virtual pad did not create a runtime.
        host.Start();
        host.PumpForTest(1000);
        host.Stop();

        Assert.Equal(1, sink.Submits);
        Assert.Single(host.Controllers);
    }

    // ------------------------------------------------------------------ 2) hot-plug frozen while ON

    [Fact]
    public async Task HotPlug_IsIgnored_WhileRemapEnabled_AndAppliedAfterDisable()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        var sources = new List<IInputSource> { new FakeSource("XInput#0") };

        using var session = new DeviceSession(root, () => sources.ToArray(), createVirtualPad: false);
        session.Start();

        Guid id = Assert.Single(session.Controllers).Id;
        ApplyConfigResult applied = await session.ApplyConfigAsync(id, path, 0);
        Assert.True(applied.Success, applied.Error);
        Assert.True(session.EnableRemap(id).Success);

        int generation = session.HostGeneration;

        // The watcher reports a newly plugged pad while remap is ON.
        sources.Add(new FakeSource("XInput#1"));
        session.NotifySourceChangedForTest();

        Assert.Equal(generation, session.HostGeneration);
        Assert.False(session.RebuildPendingForTest);
        Assert.True(session.DeviceSetDirtyForTest);
        Assert.Single(session.Controllers);

        // Turning remap off flushes the frozen change exactly once.
        session.DisableRemap(id);
        Assert.True(session.RebuildPendingForTest);

        session.RunPendingRebuildForTest();

        Assert.Equal(generation + 1, session.HostGeneration);
        Assert.Equal(2, session.Controllers.Count);
    }

    // ------------------------------------------------------------------ 3) watchdog

    [Fact]
    public async Task Watchdog_DisablesRemap_AfterThreeSecondsAboveFiveHundredOutputs()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        var source = new FakeSource("XInput#0");
        var sink = new FakeSink();

        using var host = new RemapHost(
            new[] { source },
            sink,
            new HostOptions
            {
                ProfilesRoot = root,
                TickMs = 1000,
                OutputLoopSubmissionsPerSecond = 500,
                OutputLoopSeconds = 3,
            });

        var events = new List<HostEvent>();
        host.Event += (_, e) => events.Add(e);

        Guid id = Assert.Single(host.Controllers).Id;
        ApplyConfigResult applied = await host.ApplyConfigAsync(id, path, 0);
        Assert.True(applied.Success, applied.Error);
        host.Start();

        try
        {
            const long start = 1_000_000;
            for (int second = 0; second < 3; second++)
            {
                // 501 outputs inside this one-second window.
                for (int i = 0; i < 501; i++)
                {
                    host.PumpForTest(start + (second * 1000) + (i / 2));
                }

                // Cross the window boundary: the watchdog evaluates the completed window.
                host.PumpForTest(start + ((second + 1) * 1000));
                host.DrainEventsForTest();

                if (second < 2)
                {
                    Assert.DoesNotContain(events, e => e is OutputLoopDetectedEvent);
                }
            }

            Assert.Contains(events, e => e is OutputLoopDetectedEvent);
            Assert.Equal(RemapState.RemapOff, host.GetRemapState(id));
            Assert.False(host.RemapEnabled);
        }
        finally
        {
            host.Stop();
        }
    }

    // ------------------------------------------------------------------ 4) no gate across Submit

    [Fact]
    public async Task Pump_Never_Holds_The_Gate_Across_Submit()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        var source = new FakeSource("XInput#0");
        var sink = new FakeSink();

        using var host = new RemapHost(
            new[] { source },
            sink,
            new HostOptions { ProfilesRoot = root, TickMs = 1000 });

        Guid id = Assert.Single(host.Controllers).Id;
        ApplyConfigResult applied = await host.ApplyConfigAsync(id, path, 0);
        Assert.True(applied.Success, applied.Error);

        // The sink calls back into the host from the pump thread; if the gate were still held,
        // this second thread could not enter DisableRemap and the wait below would time out.
        sink.OnSubmit = () =>
        {
            Task release = Task.Run(() => host.DisableRemap(id));
            if (!release.Wait(TimeSpan.FromSeconds(1)))
            {
                Interlocked.Increment(ref sink.LockTimeouts);
            }
        };

        host.Start();
        host.PumpForTest(1000);
        host.Stop();

        Assert.Equal(0, sink.LockTimeouts);
        Assert.False(host.RemapEnabled);
    }
}
