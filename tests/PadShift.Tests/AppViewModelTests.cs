using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PadShift.App.ViewModels;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using Xunit;
using IOPath = System.IO.Path;

namespace PadShift.Tests;

/// <summary>
/// Head-less tests for the WPF client's ViewModels. No window, no dispatcher, no devices: the
/// ViewModels only talk to <see cref="IRemapHost"/>, which is faked below by hand.
/// </summary>
public sealed class AppViewModelTests
{
    // ------------------------------------------------------------------ fake host

    private sealed class FakeRemapHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Fake Pad",
            "Xbox360",
            "Wired",
            VendorId: 0x045E,
            ProductId: 0x028E,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 100);

        private readonly Dictionary<int, string> _applied = new();

        public bool Started { get; private set; }

        public bool RemapEnabled { get; private set; }

        public int SelectSlotCalls { get; private set; }

        public int LastSelectedSlot { get; private set; } = -1;

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public int LastAppliedSlot { get; private set; } = -1;

        public Guid ControllerId => _controller.Id;

        public event EventHandler<HostEvent>? Event;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start()
        {
            Started = true;
            Event?.Invoke(this, new ControllerConnectedEvent(_controller, DateTimeOffset.UtcNow));
        }

        public void Stop()
        {
            Started = false;
            RemapEnabled = false;
        }

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) =>
            new[] { new ShiftInfo(0, "主层"), new ShiftInfo(1, "层 1") };

        public RemapState GetRemapState(Guid controllerId) =>
            RemapEnabled ? RemapState.RemapOn : RemapState.NothingApplied;

        public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot)
        {
            if (controllerId != _controller.Id)
            {
                return Task.FromResult(new ApplyConfigResult(false, "unknown controller", slot));
            }

            if (!File.Exists(profilePath))
            {
                return Task.FromResult(new ApplyConfigResult(false, "profile not found", slot));
            }

            if (slot is < 0 or > 3)
            {
                return Task.FromResult(new ApplyConfigResult(false, "bad slot", slot));
            }

            _applied[slot] = profilePath;
            LastAppliedSlot = slot;
            RemapEnabled = true;
            Event?.Invoke(this, new ConfigAppliedEvent(controllerId, profilePath, slot, DateTimeOffset.UtcNow));
            return Task.FromResult(new ApplyConfigResult(true, null, slot));
        }

        public EnableRemapResult EnableRemap(Guid? controllerId)
        {
            EnableCalls++;
            RemapEnabled = true;
            return new EnableRemapResult(true, null);
        }

        public void DisableRemap(Guid? controllerId)
        {
            DisableCalls++;
            RemapEnabled = false;
        }

        public void SelectSlot(Guid controllerId, int slot)
        {
            SelectSlotCalls++;
            LastSelectedSlot = slot;
            CurrentSlot = slot;
        }

        public int CurrentSlot { get; private set; }

        public int GetCurrentSlot(Guid controllerId) => CurrentSlot;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) =>
            _applied.TryGetValue(slot, out string? path) ? new[] { path } : Array.Empty<string>();

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose() => Event = null;
    }

    // ------------------------------------------------------------------ helpers

    private static string NewProfilesRoot()
    {
        string root = IOPath.Combine(IOPath.GetTempPath(), "padshift-vm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static MainViewModel CreateViewModel(IRemapHost host, string root)
    {
        var viewModel = new MainViewModel(host, root);
        viewModel.Library.Refresh();
        return viewModel;
    }

    // ------------------------------------------------------------------ a) slots

    [Fact]
    public void SelectingSlot_SelectsTheSlotOnTheHost()
    {
        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, NewProfilesRoot());

        Assert.NotNull(viewModel.SelectedController);

        viewModel.SelectedSlot = viewModel.Slots[2];

        Assert.Equal(2, viewModel.SelectedSlot!.Index);
        Assert.Equal(1, host.SelectSlotCalls);
        Assert.Equal(2, host.LastSelectedSlot);
        Assert.Contains(viewModel.Slots, slot => slot.Index == 2 && slot.IsSelected);
    }

    // ------------------------------------------------------------------ b) remap toggle

    [Fact]
    public void TogglingRemap_CallsEnableAndDisable()
    {
        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, NewProfilesRoot());

        viewModel.RemapEnabled = true;

        Assert.Equal(1, host.EnableCalls);
        Assert.True(host.RemapEnabled);
        Assert.Contains("映射已启用", viewModel.StatusText);

        viewModel.RemapEnabled = false;

        Assert.Equal(1, host.DisableCalls);
        Assert.False(host.RemapEnabled);
        Assert.Contains("映射已停用", viewModel.StatusText);
    }

    // ------------------------------------------------------------------ c) apply profile

    [Fact]
    public async Task ApplyingProfileFromTheLibrary_UpdatesTheCurrentSlot()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Demo Game");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "My Config.rewasd");
        ProfileIo.Save(profilePath, Kit.Profile());

        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, root);

        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(item.IsValid, item.LoadError);

        viewModel.SelectedSlot = viewModel.Slots[1];
        bool applied = await viewModel.ApplyProfileAsync(item);

        Assert.True(applied);
        Assert.Equal(1, host.LastAppliedSlot);
        Assert.Equal(new[] { profilePath }, host.GetAppliedConfigs(host.ControllerId, 1));
        Assert.Equal(profilePath, viewModel.BindingEditor.ProfilePath);
        Assert.True(host.RemapEnabled);
    }

    // ------------------------------------------------------------------ d) binding editor

    [Fact]
    public void BindingEditor_ProducesMappingRule_AndSurvivesSaveThenLoad()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Demo Game");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "RoundTrip.rewasd");
        ProfileIo.Save(profilePath, Kit.Profile());

        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, root);

        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));

        // Selecting a control on the diagram must drive the binding panel.
        GamepadControlViewModel a = viewModel.Gamepad.Controls.Single(control => control.ButtonId == GamepadButtons.A);
        viewModel.Gamepad.Select(a);
        Assert.Same(a, viewModel.BindingEditor.SelectedControl);

        viewModel.BindingEditor.SetActivator(ActivatorType.Long);
        viewModel.BindingEditor.SetTarget(BindingTargetKind.Keyboard);
        viewModel.BindingEditor.SetKeyboardTarget(Dik.G, "G");
        viewModel.BindingEditor.IsTurbo = true;

        MappingRule? rule = viewModel.BindingEditor.Commit();

        Assert.NotNull(rule);
        Assert.Equal(ActivatorType.Long, rule!.Condition.Mask!.Activator.Type);
        Assert.Equal(ActivationMode.Turbo, rule.Condition.Mask.Activator.Mode);
        Assert.Equal(1, rule.Condition.Mask.Id);
        Assert.Equal(Dik.G, rule.Macros![0].Keyboard!.ButtonId);

        Assert.True(viewModel.BindingEditor.Save());

        GamepadProfile reloaded = ProfileIo.Load(profilePath);
        MappingRule loaded = Assert.Single(reloaded.Mappings!);
        Assert.Equal(ActivatorType.Long, loaded.Condition.Mask!.Activator.Type);
        Assert.Equal(ActivationMode.Turbo, loaded.Condition.Mask.Activator.Mode);
        Assert.Equal(Dik.G, loaded.Macros![0].Keyboard!.ButtonId);
        Assert.Equal(GamepadButtons.A, Assert.Single(reloaded.Masks!).Set![0].ButtonId);
    }

    // ------------------------------------------------------------------ real profile interop

    [Fact]
    public void BindingEditor_ReadsExistingMappingFromARealProfile()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Fortnite");
        Directory.CreateDirectory(gameFolder);
        string asset = IOPath.Combine(AppContext.BaseDirectory, "Assets", "gamepad-to-kbm.rewasd");
        string profilePath = IOPath.Combine(gameFolder, "gamepad-to-kbm.rewasd");
        File.Copy(asset, profilePath, overwrite: true);

        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, root);

        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));

        // That profile declares its bindings on layer 1 (a "default" shift), not on the main layer.
        viewModel.BindingEditor.SetLayer(1);
        Assert.Equal(1, viewModel.BindingEditor.SelectedLayer.Layer);

        viewModel.Gamepad.Select(viewModel.Gamepad.Controls.Single(control => control.ButtonId == GamepadButtons.A));

        Assert.Contains("单按", viewModel.BindingEditor.CurrentBindingText);
        Assert.Contains("滚轮 下", viewModel.BindingEditor.CurrentBindingText);
    }

    [Fact]
    public void SavingARealProfile_KeepsUnknownFieldsAndReplacesOnlyTheSelectedMapping()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Fortnite");
        Directory.CreateDirectory(gameFolder);
        string asset = IOPath.Combine(AppContext.BaseDirectory, "Assets", "gamepad-to-kbm.rewasd");
        string profilePath = IOPath.Combine(gameFolder, "gamepad-to-kbm.rewasd");
        File.Copy(asset, profilePath, overwrite: true);

        GamepadProfile original = ProfileIo.Load(profilePath);
        int originalMappingCount = original.Mappings!.Count;

        using var host = new FakeRemapHost();
        using var viewModel = CreateViewModel(host, root);
        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));
        viewModel.BindingEditor.SetLayer(1);
        viewModel.Gamepad.Select(viewModel.Gamepad.Controls.Single(control => control.ButtonId == GamepadButtons.A));

        viewModel.BindingEditor.SetKeyboardTarget(Dik.G, "G");
        Assert.True(viewModel.BindingEditor.Save());

        GamepadProfile reloaded = ProfileIo.Load(profilePath);

        // Metadata and unknown nested members survive the write.
        Assert.Equal(original.AppVersion, reloaded.AppVersion);
        Assert.Equal("Gamepad to Keyboard and Mouse", reloaded.Shifts![0].Description);
        Assert.NotNull(reloaded.Devices.Hardware![0].Shifts);
        Assert.Equal(originalMappingCount, reloaded.Mappings!.Count);

        // The A mapping on layer 1 now outputs the captured key, other mappings are untouched.
        MappingRule updated = reloaded.Mappings.Single(rule =>
            rule.Condition.Mask?.Id == 1 &&
            (rule.Condition.ShiftId ?? 0) == 1 &&
            rule.Condition.Mask.Activator.Type == ActivatorType.Single);
        Assert.Equal(Dik.G, updated.Macros![0].Keyboard!.ButtonId);
        Assert.Contains(reloaded.Mappings, rule => rule.Condition.Mask?.Id == 2);
    }

    // ------------------------------------------------------------------ error handling

    [Fact]
    public void MissingProfilesFolder_IsReportedInline_AndDoesNotThrow()
    {
        using var host = new FakeRemapHost();
        string missing = IOPath.Combine(IOPath.GetTempPath(), "padshift-vm-tests", Guid.NewGuid().ToString("N"), "nope");

        using var viewModel = CreateViewModel(host, missing);

        Assert.Contains("不存在", viewModel.Library.Message);
        Assert.Empty(viewModel.Library.Games);
        Assert.False(viewModel.RemapEnabled);
    }
}
