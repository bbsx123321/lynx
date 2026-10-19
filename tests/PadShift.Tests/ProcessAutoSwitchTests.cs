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

/// <summary>
/// Process watcher + process→profile auto-switching: start/stop diffing, index building from
/// real profile fixtures, ambiguity resolution and default-profile restore. All process data is
/// injected, so no real process, WMI query or elevation is involved.
/// </summary>
public class ProcessAutoSwitchTests
{
    private sealed class FakeInputSource : IInputSource
    {
        public FakeInputSource(string deviceId) => DeviceId = deviceId;

        public string DeviceId { get; }

        public bool IsConnected { get; set; } = true;

        public bool TryPoll(ControllerState state) => true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeOutputSink : IOutputSink
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

    private sealed class FakeProcessSource : IProcessSource
    {
        public List<ProcessEntry> Entries { get; } = new();

        public bool Fail { get; set; }

        public IReadOnlyList<ProcessEntry> GetProcesses()
        {
            if (Fail)
            {
                throw new InvalidOperationException("access denied (simulated)");
            }

            return Entries.ToArray();
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "padshift-autoswitch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteProfile(string root, string folder, string fileName, params string[] processNames)
    {
        GamepadProfile profile = Kit.Profile();
        profile.Config.ProcessNames = processNames.Length == 0
            ? null
            : new List<string>(processNames);
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        ProfileIo.Save(path, profile);
        return Path.GetFullPath(path);
    }

    private static RemapHost CreateHost(string root, IProcessSource? processes = null)
    {
        var options = new HostOptions
        {
            ProfilesRoot = root,
            TickMs = 1,
            AutoSwitchEnabled = true,
            AutoSwitchPollMs = 600_000,
        };

        return new RemapHost(
            new IInputSource[] { new FakeInputSource("XInput#0") },
            new FakeOutputSink(),
            options,
            processes ?? new FakeProcessSource());
    }

    // ------------------------------------------------------------- watcher

    [Fact]
    public void Watcher_Diffs_By_Pid_And_Name_And_Normalises_Names()
    {
        var source = new FakeProcessSource();
        source.Entries.Add(new ProcessEntry(100, "explorer", null));

        using var watcher = new ProcessWatcher(source, 600_000);
        var started = new List<ProcessStartedEventArgs>();
        var stopped = new List<ProcessStoppedEventArgs>();
        watcher.ProcessStarted += (_, e) => started.Add(e);
        watcher.ProcessStopped += (_, e) => stopped.Add(e);

        // First snapshot is a baseline: nothing is reported as started.
        Assert.False(watcher.PollOnce());
        Assert.Empty(started);

        source.Entries.Add(new ProcessEntry(200, "game", @"C:\Games\MyGame"));
        Assert.True(watcher.PollOnce());
        ProcessStartedEventArgs first = Assert.Single(started);
        Assert.Equal("game.exe", first.Name);
        Assert.Equal(200, first.Pid);
        Assert.Equal(@"C:\Games\MyGame", first.ParentFolder);
        Assert.Empty(stopped);

        started.Clear();
        Assert.False(watcher.PollOnce());
        Assert.Empty(started);

        // Same PID, different name = PID reuse: one stop + one start.
        source.Entries.RemoveAll(entry => entry.Pid == 200);
        source.Entries.Add(new ProcessEntry(200, "other", null));
        Assert.True(watcher.PollOnce());
        Assert.Equal("game.exe", Assert.Single(stopped).Name);
        Assert.Equal("other.exe", Assert.Single(started).Name);

        // Removal raises exactly one stop.
        started.Clear();
        stopped.Clear();
        source.Entries.RemoveAll(entry => entry.Pid == 200);
        Assert.True(watcher.PollOnce());
        Assert.Equal("other.exe", Assert.Single(stopped).Name);
        Assert.Empty(started);
    }

    [Fact]
    public void Watcher_Never_Throws_When_The_Source_Fails()
    {
        var source = new FakeProcessSource();
        source.Entries.Add(new ProcessEntry(1, "a", null));
        using var watcher = new ProcessWatcher(source, 10);
        var stopped = new List<ProcessStoppedEventArgs>();
        watcher.ProcessStopped += (_, e) => stopped.Add(e);

        Assert.False(watcher.PollOnce());
        Assert.Single(source.Entries);

        source.Fail = true;

        // A failing snapshot must not look like "every process exited".
        Assert.False(watcher.PollOnce());
        Assert.Empty(stopped);

        watcher.Start();
        watcher.Stop();
    }

    // ------------------------------------------------------------- index

    [Fact]
    public void Index_Is_Built_From_Real_Profile_Fixture_Process_Names()
    {
        string root = NewRoot();
        string fixture = Path.Combine(AppContext.BaseDirectory, "Assets", "double-movement.rewasd");
        Assert.True(File.Exists(fixture), fixture);

        string destination = Path.Combine(root, "Fortnite", "Config.rewasd");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(fixture, destination);
        destination = Path.GetFullPath(destination);

        // A profile without processNames must not contribute index entries.
        WriteProfile(root, "Default", "Config.rewasd");

        using var host = new RemapHost(
            Array.Empty<IInputSource>(),
            new FakeOutputSink(),
            new HostOptions { ProfilesRoot = root });
        using var switcher = new ProfileAutoSwitcher(host, new ProfileLibrary(root));

        GamepadProfile fixtureProfile = ProfileIo.Load(destination);
        Assert.NotNull(fixtureProfile.Config.ProcessNames);
        Assert.NotEmpty(fixtureProfile.Config.ProcessNames!);

        Assert.Equal(fixtureProfile.Config.ProcessNames!.Count, switcher.Index.Count);

        // Case-insensitive, extension-less, and path-qualified names all resolve.
        Assert.True(switcher.TryResolveProfile("fortniteclient-win64-shipping", null, out string? resolved));
        Assert.Equal(destination, resolved);

        Assert.True(switcher.TryResolveProfile(
            @"C:\Program Files\Epic Games\Fortnite\FortniteClient-Win64-Shipping.exe",
            null,
            out resolved));
        Assert.Equal(destination, resolved);

        Assert.False(switcher.TryResolveProfile("notepad.exe", null, out _));
    }

    [Fact]
    public void Index_Is_Invalidated_When_The_Library_Changes()
    {
        string root = NewRoot();
        var library = new ProfileLibrary(root);

        using var host = new RemapHost(
            Array.Empty<IInputSource>(),
            new FakeOutputSink(),
            new HostOptions { ProfilesRoot = root });
        using var switcher = new ProfileAutoSwitcher(host, library);

        Assert.Empty(switcher.Index);

        string alpha = WriteProfile(root, "Alpha", "Config.rewasd", "alpha.exe");
        library.Refresh();
        Assert.Single(switcher.Index);
        Assert.True(switcher.TryResolveProfile("alpha.exe", null, out string? resolved));
        Assert.Equal(alpha, resolved);

        string beta = WriteProfile(root, "Beta", "Config.rewasd", "beta.exe");
        library.Refresh();
        Assert.Equal(2, switcher.Index.Count);
        Assert.True(switcher.TryResolveProfile("BETA", null, out resolved));
        Assert.Equal(beta, resolved);
    }

    // ------------------------------------------------------------- ambiguity

    [Fact]
    public void Ambiguous_Process_Name_Prefers_The_Profile_Folder_Matching_The_Process_Folder()
    {
        string root = NewRoot();
        string alpha = WriteProfile(root, "Alpha", "Config.rewasd", "shared.exe");
        string beta = WriteProfile(root, "Beta", "Config.rewasd", "shared.exe");

        using var host = new RemapHost(
            Array.Empty<IInputSource>(),
            new FakeOutputSink(),
            new HostOptions { ProfilesRoot = root });
        using var switcher = new ProfileAutoSwitcher(host, new ProfileLibrary(root));

        // Folder tie-breaker wins.
        Assert.True(switcher.TryResolveProfile("shared.exe", "Beta", out string? resolved));
        Assert.Equal(beta, resolved);

        // No folder / unknown folder leaves the match ambiguous: skip, never guess.
        Assert.False(switcher.TryResolveProfile("shared.exe", null, out _));
        Assert.False(switcher.TryResolveProfile("shared.exe", "Gamma", out _));
        Assert.True(switcher.TryResolveProfile("shared.exe", "Alpha", out resolved));
        Assert.Equal(alpha, resolved);
    }

    // ------------------------------------------------------------- switching

    [Fact]
    public async Task Single_Match_Applies_To_Current_Slot_And_Stop_Restores_The_Manual_Default()
    {
        string root = NewRoot();
        string defaultProfile = WriteProfile(root, "Default", "Config.rewasd");
        string gameProfile = WriteProfile(root, "MyGame", "Config.rewasd", "game.exe");

        var processes = new FakeProcessSource();
        using RemapHost host = CreateHost(root, processes);
        var events = new System.Collections.Concurrent.ConcurrentQueue<HostEvent>();
        host.Event += (_, e) => events.Enqueue(e);

        Guid controller = host.Controllers[0].Id;
        Assert.True((await host.ApplyConfigAsync(controller, defaultProfile, 1)).Success);
        Assert.True(host.RemapEnabled);
        Assert.True(host.AutoSwitcher!.TryGetDefaultProfile(controller, out string? remembered, out int rememberedSlot));
        Assert.Equal(defaultProfile, remembered);
        Assert.Equal(1, rememberedSlot);

        host.Start();
        try
        {
            processes.Entries.Add(new ProcessEntry(4242, "game", "MyGame"));
            Assert.True(host.Watcher!.PollOnce());

            Assert.Equal(new[] { gameProfile }, host.GetAppliedConfigs(controller, 1));
            AutoSwitchAppliedEvent applied = Assert.Single(events.OfType<AutoSwitchAppliedEvent>());
            Assert.Equal(controller, applied.ControllerId);
            Assert.Equal(gameProfile, applied.ProfilePath);
            Assert.Equal("game.exe", applied.ProcessName);
            Assert.Equal(1, host.GetCurrentSlot(controller));

            // The auto-applied profile must not overwrite the remembered manual default.
            Assert.True(host.AutoSwitcher!.TryGetDefaultProfile(controller, out remembered, out _));
            Assert.Equal(defaultProfile, remembered);

            processes.Entries.Clear();
            Assert.True(host.Watcher.PollOnce());
            Assert.Equal(new[] { defaultProfile }, host.GetAppliedConfigs(controller, 1));
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public async Task Ambiguous_Start_Is_Skipped_But_The_Folder_Match_Applies()
    {
        string root = NewRoot();
        string defaultProfile = WriteProfile(root, "Default", "Config.rewasd");
        WriteProfile(root, "Alpha", "Config.rewasd", "shared.exe");
        string beta = WriteProfile(root, "Beta", "Config.rewasd", "shared.exe");

        var processes = new FakeProcessSource();
        using RemapHost host = CreateHost(root, processes);
        var autoEvents = new System.Collections.Concurrent.ConcurrentQueue<AutoSwitchAppliedEvent>();
        host.Event += (_, e) =>
        {
            if (e is AutoSwitchAppliedEvent applied)
            {
                autoEvents.Enqueue(applied);
            }
        };

        Guid controller = host.Controllers[0].Id;
        Assert.True((await host.ApplyConfigAsync(controller, defaultProfile, 1)).Success);

        host.Start();
        try
        {
            // Ambiguous and no process folder: skipped, the default stays applied.
            processes.Entries.Add(new ProcessEntry(5000, "shared", null));
            Assert.True(host.Watcher!.PollOnce());
            Assert.Equal(new[] { defaultProfile }, host.GetAppliedConfigs(controller, 1));
            Assert.Empty(autoEvents);

            // The exact same process, but now with a folder matching the profile folder.
            processes.Entries.Clear();
            processes.Entries.Add(new ProcessEntry(5001, "shared", "Beta"));
            Assert.True(host.Watcher.PollOnce());
            Assert.Equal(new[] { beta }, host.GetAppliedConfigs(controller, 1));
            Assert.Single(autoEvents);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public async Task AutoSwitch_Does_Not_Apply_While_Remap_Is_Disabled()
    {
        string root = NewRoot();
        string gameProfile = WriteProfile(root, "MyGame", "Config.rewasd", "game.exe");

        var processes = new FakeProcessSource();
        using RemapHost host = CreateHost(root, processes);
        Guid controller = host.Controllers[0].Id;
        Assert.True((await host.ApplyConfigAsync(controller, gameProfile, 0)).Success);
        host.DisableRemap(null);
        Assert.False(host.RemapEnabled);
        Assert.Empty(host.GetAppliedConfigs(controller, 1));

        host.Start();
        try
        {
            processes.Entries.Add(new ProcessEntry(6000, "game", "MyGame"));
            Assert.True(host.Watcher!.PollOnce());
            Assert.Empty(host.GetAppliedConfigs(controller, 1));
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void AutoSwitch_Is_Off_By_Default()
    {
        string root = NewRoot();
        using var host = new RemapHost(
            new IInputSource[] { new FakeInputSource("XInput#9") },
            new FakeOutputSink(),
            new HostOptions { ProfilesRoot = root });

        Assert.Null(host.Watcher);
        Assert.Null(host.AutoSwitcher);
    }
}
