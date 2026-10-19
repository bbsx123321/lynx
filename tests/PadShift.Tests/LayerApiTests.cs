using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using PadShift.Engine;
using PadShift.Engine.Hosting;
using Xunit;
using IOPath = System.IO.Path;

namespace PadShift.Tests;

/// <summary>
/// Layer API (engine + host + HTTP + radial wiring) and the pure multi-monitor placement math the
/// overlay uses. Everything here is device-free: no controller, no window, no real network port.
/// </summary>
public sealed class LayerApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _api;

    public LayerApiTests(WebApplicationFactory<Program> factory) => _api = factory.CreateClient();

    // ------------------------------------------------------------------ engine

    [Fact]
    public void Engine_SetLayer_SwitchesAndHonorsUnheritableMasks()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        ShiftRule shift = Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        shift.UnheritableMasks = new List<long> { 1 };
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });

        RemapEngine engine = Kit.Engine(profile);
        ControllerState state = Kit.State();
        state.Buttons[ButtonIds.A] = true;

        Assert.True(engine.Tick(state, 0).KeyboardKeys[Dik.X]);

        Assert.True(engine.TrySetLayer(1));
        Assert.Equal(1, engine.CurrentLayer);
        Assert.False(engine.Tick(state, 10).KeyboardKeys[Dik.X]);

        Assert.True(engine.TrySetLayer(0));
        Assert.Equal(0, engine.CurrentLayer);
        Assert.True(engine.Tick(state, 20).KeyboardKeys[Dik.X]);
    }

    [Fact]
    public void Engine_SetLayer_RejectsUnknownLayersAndRaisesTheLayerEvent()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddShift(profile, 2, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        RemapEngine engine = Kit.Engine(profile);
        var events = new List<(int Layer, string Reason)>();
        engine.LayerChanged += (layer, reason) => events.Add((layer, reason));

        Assert.True(engine.TrySetLayer(2));
        Assert.Equal(2, engine.CurrentLayer);
        Assert.Contains((2, "layer"), events);

        int before = events.Count;
        Assert.False(engine.TrySetLayer(7));
        Assert.Equal(2, engine.CurrentLayer);
        Assert.Equal(before, events.Count);
    }

    // ------------------------------------------------------------------ host

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

    private sealed class RecordingSink : IOutputSink
    {
        public int SeenX;
        public int SeenY;

        public void Submit(OutputIntent intent)
        {
            if (intent.KeyboardKeys[Dik.X]) Interlocked.Increment(ref SeenX);
            if (intent.KeyboardKeys[Dik.Y]) Interlocked.Increment(ref SeenY);
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private static string ProfilesRoot() => IOPath.Combine(IOPath.GetTempPath(), "padshift-layer-tests");

    private static string WriteLayerProfile()
    {
        GamepadProfile profile = Kit.Profile();
        Kit.AddMask(profile, 1, Kit.Btn(ButtonIds.A));
        Kit.AddShift(profile, 1, ShiftType.Toggle, Kit.Btn(ButtonIds.Back));
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: new[] { Kit.Key(Dik.X) });
        Kit.Map(profile, 1, ActivatorType.Start, ActivationMode.Onetime, layer: 1, macros: new[] { Kit.Key(Dik.Y) });

        string path = IOPath.Combine(ProfilesRoot(), Guid.NewGuid().ToString("N") + ".rewasd");
        ProfileIo.Save(path, profile);
        return path;
    }

    [Fact]
    public async Task Host_SetLayer_ChangesTheLayerUsedBySubsequentTicks()
    {
        var source = new FakeSource("XInput#layer-1");
        var sink = new RecordingSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        Guid id = host.Controllers[0].Id;
        Assert.True((await host.ApplyConfigAsync(id, WriteLayerProfile(), 0)).Success);
        Assert.Equal(0, host.GetLayer(id));

        var events = new List<HostEvent>();
        host.Event += (_, e) => events.Add(e);

        host.Start();
        source.PressA = true;
        Assert.True(WaitFor(() => Volatile.Read(ref sink.SeenX) > 0), "base layer mapping never fired");

        Assert.True(host.SetLayer(id, 1));
        Assert.Equal(1, host.GetLayer(id));
        Assert.Contains(events, e => e is ShiftChangedEvent shift && shift.ControllerId == id && shift.Shift == 1);
        Assert.True(WaitFor(() => Volatile.Read(ref sink.SeenY) > 0), "layer 1 mapping never fired after SetLayer");
        host.Stop();
    }

    [Fact]
    public async Task Host_SetLayer_UnknownLayerReturnsFalseAndKeepsTheCurrentLayer()
    {
        var source = new FakeSource("XInput#layer-2");
        var sink = new RecordingSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        Guid id = host.Controllers[0].Id;
        Assert.True((await host.ApplyConfigAsync(id, WriteLayerProfile(), 0)).Success);

        Assert.True(host.SetLayer(id, 1));
        Assert.Equal(1, host.GetLayer(id));
        Assert.False(host.SetLayer(id, 9));
        Assert.Equal(1, host.GetLayer(id));
        Assert.False(host.SetLayer(Guid.NewGuid(), 0));
    }

    [Fact]
    public async Task Host_SetLayer_ProfileReloadResetsToZero()
    {
        var source = new FakeSource("XInput#layer-3");
        var sink = new RecordingSink();
        using var host = new RemapHost(new[] { source }, sink, new HostOptions { ProfilesRoot = ProfilesRoot(), TickMs = 1 });

        Guid id = host.Controllers[0].Id;
        string path = WriteLayerProfile();
        Assert.True((await host.ApplyConfigAsync(id, path, 0)).Success);
        Assert.True(host.SetLayer(id, 1));
        Assert.Equal(1, host.GetLayer(id));

        Assert.True((await host.ApplyConfigAsync(id, path, 1)).Success);
        Assert.Equal(0, host.GetLayer(id));
    }

    // ------------------------------------------------------------------ HTTP route

    [Fact]
    public async Task Api_SetLayer_Then_CurrentLayer_RoundTrips_AndRejectsUnknownLayers()
    {
        using var controllers = await _api.GetAsync("/v2.6/ControllerService/Controllers");
        controllers.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await controllers.Content.ReadAsStringAsync());
        Guid id = document.RootElement[0].GetProperty("Controllers")[0].GetProperty("Guid").GetGuid();

        using var set = await _api.PostAsync(
            "/v2.6/ControllerService/SetLayer",
            JsonContent.Create(new { ControllerId = id, Layer = 1 }));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True(await set.Content.ReadFromJsonAsync<bool>());

        using var current = await _api.GetAsync($"/v2.6/ControllerService/CurrentLayer/{id}");
        current.EnsureSuccessStatusCode();
        Assert.Equal(1, await current.Content.ReadFromJsonAsync<int>());

        using var unknown = await _api.PostAsync(
            "/v2.6/ControllerService/SetLayer",
            JsonContent.Create(new { ControllerId = id, Layer = 9 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        using var missing = await _api.PostAsync(
            "/v2.6/ControllerService/SetLayer",
            JsonContent.Create(new { ControllerId = Guid.NewGuid(), Layer = 0 }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ------------------------------------------------------------------ radial wiring

    private sealed class LayerHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Layer Pad",
            "Xbox360",
            "Wired",
            VendorId: 0x045E,
            ProductId: 0x028E,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 100);

        public bool AllowLayer { get; set; } = true;

        public int SetLayerCalls { get; private set; }

        public int Layer { get; private set; }

        public event EventHandler<HostEvent>? Event;

        public bool RemapEnabled => true;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) => new[] { new ShiftInfo(0, "主层") };

        public RemapState GetRemapState(Guid controllerId) => RemapState.RemapOn;

        public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot) =>
            Task.FromResult(new ApplyConfigResult(true, null, slot));

        public EnableRemapResult EnableRemap(Guid? controllerId) => new(true, null);

        public void DisableRemap(Guid? controllerId)
        {
        }

        public void SelectSlot(Guid controllerId, int slot)
        {
        }

        public int GetCurrentSlot(Guid controllerId) => 0;

        public bool SetLayer(Guid controllerId, int layer)
        {
            SetLayerCalls++;
            if (!AllowLayer)
            {
                return false;
            }

            Layer = layer;
            Event?.Invoke(this, new ShiftChangedEvent(controllerId, layer, $"层 {layer}", DateTimeOffset.UtcNow));
            return true;
        }

        public int GetLayer(Guid controllerId) => Layer;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) => Array.Empty<string>();

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void RadialLayer_GoesThroughTheHostAndReportsAnUnknownLayer()
    {
        string root = IOPath.Combine(IOPath.GetTempPath(), "padshift-layer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var host = new LayerHost();
        using var viewModel = new MainViewModel(host, root);
        var dispatcher = new RadialMenuDispatcher(new MainRadialHost(viewModel));

        Assert.True(dispatcher.Execute(RadialSectorAction.SwitchLayer, "1"));
        Assert.Equal(1, host.SetLayerCalls);
        Assert.Equal(1, host.Layer);

        host.AllowLayer = false;
        Assert.False(dispatcher.Execute(RadialSectorAction.SwitchLayer, "5"));
        Assert.Equal("该配置没有第 5 层", dispatcher.LastError);
        Assert.Contains("该配置没有第 5 层", viewModel.InlineMessage);
    }

    // ------------------------------------------------------------------ monitor math

    [Fact]
    public void MonitorLayout_ResolvesOneTwoAndThreeMonitorsWithNegativeOrigins()
    {
        var one = new[] { new MonitorBounds(0, 0, 1920, 1080) };
        Assert.Equal(new MonitorPoint(960, 540), MonitorLayout.Resolve(one, 0, center: true, offsetX: 40, offsetY: 40));
        Assert.Equal(new MonitorPoint(1000, 580), MonitorLayout.Resolve(one, 0, center: false, offsetX: 40, offsetY: 40));

        // Two monitors: one to the left (negative origin), one to the right.
        var two = new[]
        {
            new MonitorBounds(-1920, 0, 1920, 1080),
            new MonitorBounds(0, 0, 2560, 1440),
        };
        Assert.Equal(new MonitorPoint(-960, 540), MonitorLayout.Resolve(two, 0, center: true, offsetX: 0, offsetY: 0));
        Assert.Equal(new MonitorPoint(1280, 720), MonitorLayout.Resolve(two, 1, center: true, offsetX: 0, offsetY: 0));
        Assert.Equal(new MonitorPoint(-860, 490), MonitorLayout.Resolve(two, 0, center: false, new MonitorOffset(100, -50)));

        // Three monitors: negative x and negative y origins, plus a tallest centre monitor.
        var three = new[]
        {
            new MonitorBounds(-3840, -1080, 1920, 1080),
            new MonitorBounds(-1920, 0, 1920, 1080),
            new MonitorBounds(0, 0, 1920, 1200),
        };
        Assert.Equal(new MonitorPoint(-2880, -540), MonitorLayout.Resolve(three, 0, center: true, offsetX: 0, offsetY: 0));
        Assert.Equal(new MonitorPoint(-960, 540), MonitorLayout.Resolve(three, 1, center: true, offsetX: 0, offsetY: 0));
        Assert.Equal(new MonitorPoint(960, 600), MonitorLayout.Resolve(three, 2, center: true, offsetX: 0, offsetY: 0));
    }

    [Fact]
    public void MonitorLayout_ClampsTheSelectedIndexAndHandlesAnEmptyList()
    {
        var two = new[]
        {
            new MonitorBounds(-1920, 0, 1920, 1080),
            new MonitorBounds(0, 0, 2560, 1440),
        };

        Assert.Equal(new MonitorPoint(-960, 540), MonitorLayout.Resolve(two, -5, true, 0, 0));
        Assert.Equal(new MonitorPoint(1280, 720), MonitorLayout.Resolve(two, 99, true, 0, 0));

        Assert.Equal(new MonitorPoint(0, 0), MonitorLayout.Resolve(Array.Empty<MonitorBounds>(), 0, true, 10, 20));
        Assert.Equal(new MonitorPoint(10, 20), MonitorLayout.Resolve(Array.Empty<MonitorBounds>(), 4, false, 10, 20));
    }

    [Fact]
    public void OverlayPreferences_PersistsTheSelectedMonitorDeviceName()
    {
        var monitors = new[]
        {
            new MonitorOption(@"\\.\DISPLAY1", "1. 1920×1080（主）", IsPrimary: true),
            new MonitorOption(@"\\.\DISPLAY2", "2. 2560×1440"),
        };

        var editor = new OverlayPreferencesViewModel(new OverlaySettings(), monitors);
        Assert.True(editor.Monitors.Count >= 2);
        Assert.Equal(@"\\.\DISPLAY1", editor.SelectedMonitor!.DeviceName);

        editor.SelectedMonitor = monitors[1];
        OverlaySettings saved = editor.ToSettings();
        saved.Normalize();
        Assert.Equal(@"\\.\DISPLAY2", saved.MonitorDeviceName);

        string path = IOPath.Combine(IOPath.GetTempPath(), "padshift-layer-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var settings = new AppSettings { Overlay = saved };
        Assert.True(settings.Save(path));

        AppSettings reloaded = AppSettings.Load(path);
        Assert.Equal(@"\\.\DISPLAY2", reloaded.Overlay.MonitorDeviceName);

        var reopened = new OverlayPreferencesViewModel(reloaded.Overlay, monitors);
        Assert.Equal(@"\\.\DISPLAY2", reopened.SelectedMonitor!.DeviceName);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 3000)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }
}
