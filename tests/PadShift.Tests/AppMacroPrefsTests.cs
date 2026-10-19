using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using Xunit;
using IOPath = System.IO.Path;

namespace PadShift.Tests;

/// <summary>
/// Head-less tests for the macro editor, the preferences/settings model and the
/// import/export/preset paths of the WPF client. No window, no dispatcher, no devices: the
/// ViewModels are driven directly and the host is a hand-written fake.
/// </summary>
public sealed class AppMacroPrefsTests
{
    // ------------------------------------------------------------------ fake host

    private sealed class FakeRemapHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Fake Pad",
            "Xbox360",
            "Wired",
            VendorId: 0x045E,
            ProductId: 0x028E,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 90);

        public Guid ControllerId => _controller.Id;

        public bool RemapEnabled { get; private set; }

        public event EventHandler<HostEvent>? Event;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start() => Event?.Invoke(this, new ControllerConnectedEvent(_controller, DateTimeOffset.UtcNow));

        public void Stop() => RemapEnabled = false;

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) => new[] { new ShiftInfo(0, "主层") };

        public RemapState GetRemapState(Guid controllerId) =>
            RemapEnabled ? RemapState.RemapOn : RemapState.NothingApplied;

        public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot) =>
            Task.FromResult(File.Exists(profilePath)
                ? new ApplyConfigResult(true, null, slot)
                : new ApplyConfigResult(false, "profile not found", slot));

        public EnableRemapResult EnableRemap(Guid? controllerId)
        {
            RemapEnabled = true;
            return new EnableRemapResult(true, null);
        }

        public void DisableRemap(Guid? controllerId) => RemapEnabled = false;

        public int CurrentSlot { get; private set; }

        public void SelectSlot(Guid controllerId, int slot) => CurrentSlot = slot;

        public int GetCurrentSlot(Guid controllerId) => CurrentSlot;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) => Array.Empty<string>();

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose() => Event = null;
    }

    // ------------------------------------------------------------------ helpers

    private static string NewProfilesRoot()
    {
        string root = IOPath.Combine(
            IOPath.GetTempPath(),
            "padshift-macro-prefs-tests",
            Guid.NewGuid().ToString("N"),
            "Profiles");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NewScratchFolder()
    {
        string folder = IOPath.Combine(
            IOPath.GetTempPath(),
            "padshift-macro-prefs-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string LoadGamepadToKbmAsset()
    {
        return IOPath.Combine(AppContext.BaseDirectory, "Assets", "gamepad-to-kbm.rewasd");
    }

    private static string Compact(MacroElement element) =>
        System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(element, ProfileIo.Options))!.ToJsonString();

    private static string Summarize(MacroElement element)
    {
        if (element.Keyboard is { } keyboard)
        {
            return $"keyboard:{keyboard.ButtonId}:{keyboard.Action?.GetString() ?? "auto"}";
        }

        if (element.Mouse is { } mouse)
        {
            if (mouse.ButtonId is { } buttonId)
            {
                return $"mouse:{buttonId}:{mouse.Action?.GetString() ?? "auto"}";
            }

            if (mouse.Direction is { } direction)
            {
                return $"move:{direction.GetString()}:{mouse.Value}";
            }

            return $"wheel:{mouse.Wheel}:{mouse.Value}";
        }

        if (element.Gamepad is { } gamepad)
        {
            return $"gamepad:{gamepad.ButtonId}:{gamepad.Action?.GetString() ?? "auto"}";
        }

        if (element.Pause is { } pause)
        {
            return $"pause:{pause.Value}";
        }

        return element.Break is not null ? "break" : "other";
    }

    // ------------------------------------------------------------------ 1) macro round-trip

    [Fact]
    public void MacroSequence_RoundTripsThroughMacroElements()
    {
        var editor = new MacroEditorViewModel();
        editor.AddKeyboard(Dik.D3, MacroAction.Down);
        editor.AddPause(120);
        editor.AddBreak();
        editor.AddMouseButton(MouseButtons.Left, MacroAction.Auto);
        editor.AddMouseMove("right", 12);
        editor.AddWheel("down", 2);
        editor.AddGamepadButton(GamepadButtons.A, MacroAction.Auto);
        editor.AddKeyboard(Dik.D3, MacroAction.Up);

        Assert.Equal(8, editor.Steps.Count);
        Assert.False(editor.HasErrors);
        Assert.True(editor.CanSave);
        Assert.Contains("暂停 120 ms", editor.PreviewText);

        List<MacroElement> elements = editor.BuildElements();

        Assert.Equal(8, elements.Count);
        Assert.Equal("{\"keyboard\":{\"buttonId\":4,\"action\":\"down\"}}", Compact(elements[0]));
        Assert.Equal("{\"pause\":{\"value\":120}}", Compact(elements[1]));
        Assert.Equal("{\"break\":{}}", Compact(elements[2]));
        Assert.Equal("{\"mouse\":{\"buttonId\":1}}", Compact(elements[3]));
        Assert.Equal("{\"mouse\":{\"direction\":\"right\",\"value\":12}}", Compact(elements[4]));
        Assert.Equal("{\"mouse\":{\"value\":2,\"wheel\":\"down\"}}", Compact(elements[5]));
        Assert.Equal("{\"gamepad\":{\"id\":1,\"buttonId\":1}}", Compact(elements[6]));
        Assert.Equal("{\"keyboard\":{\"buttonId\":4,\"action\":\"up\"}}", Compact(elements[7]));

        var reloaded = new MacroEditorViewModel();
        reloaded.Load(elements);

        Assert.Equal(
            editor.Steps.Select(step => step.Kind).ToArray(),
            reloaded.Steps.Select(step => step.Kind).ToArray());
        Assert.Equal(Dik.D3, reloaded.Steps[0].Dik);
        Assert.Equal(120, reloaded.Steps[1].PauseMs);
        Assert.Equal("right", reloaded.Steps[4].Direction);
        Assert.Equal(12, reloaded.Steps[4].Amount);
        Assert.Equal(2, reloaded.Steps[5].Amount);
        Assert.Equal(GamepadButtons.A, reloaded.Steps[6].GamepadButtonId);

        // Reloaded and untouched rows serialize back to the same JSON.
        List<MacroElement> second = reloaded.BuildElements();
        for (int index = 0; index < elements.Count; index++)
        {
            Assert.Equal(Compact(elements[index]), Compact(second[index]));
        }
    }

    // ------------------------------------------------------------------ 2) real profile macro

    [Fact]
    public void RealProfileMacro_OpensAndSavesWithoutChangingStepsOrOrder()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Fortnite");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "gamepad-to-kbm.rewasd");
        File.Copy(LoadGamepadToKbmAsset(), profilePath, overwrite: true);

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);

        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));

        // In that profile B lives on layer 1 and carries a real 5-step Alt+Tab macro.
        viewModel.BindingEditor.SetLayer(1);
        viewModel.Gamepad.Select(viewModel.Gamepad.Controls.Single(control => control.ButtonId == GamepadButtons.B));
        Assert.Equal(5, viewModel.BindingEditor.MacroStepCount);

        MacroEditorViewModel editor = viewModel.BindingEditor.BeginMacroEditor();
        Assert.Equal(5, editor.Steps.Count);
        Assert.Equal(
            new[] { MacroStepKind.Keyboard, MacroStepKind.Keyboard, MacroStepKind.Keyboard, MacroStepKind.Pause, MacroStepKind.Keyboard },
            editor.Steps.Select(step => step.Kind).ToArray());
        Assert.Equal(
            new[] { 56, 15, 15, 56 },
            editor.Steps.Where(step => step.Kind == MacroStepKind.Keyboard).Select(step => step.Dik).ToArray());
        Assert.Equal(10000, editor.Steps[3].PauseMs);
        Assert.Equal(
            new[] { MacroAction.Down, MacroAction.Down, MacroAction.Up, MacroAction.Up },
            editor.Steps.Where(step => step.Kind == MacroStepKind.Keyboard).Select(step => step.Action).ToArray());
        Assert.False(editor.HasErrors);

        Assert.True(viewModel.BindingEditor.ApplyMacroEditor());
        Assert.True(viewModel.BindingEditor.Save());

        GamepadProfile reloaded = ProfileIo.Load(profilePath);
        MappingRule rule = reloaded.Mappings!.Single(candidate =>
            candidate.Condition.Mask?.Id == 2 &&
            (candidate.Condition.ShiftId ?? 0) == 1 &&
            candidate.Condition.Mask.Activator.Type == ActivatorType.Single);

        Assert.Equal(
            new[]
            {
                "keyboard:56:down",
                "keyboard:15:down",
                "keyboard:15:up",
                "pause:10000",
                "keyboard:56:up",
            },
            rule.Macros!.Select(Summarize).ToArray());
    }

    // ------------------------------------------------------------------ 3) import

    [Fact]
    public void Import_DuplicateGameNamesGetSuffixes()
    {
        string root = NewProfilesRoot();
        string scratch = NewScratchFolder();
        string source = IOPath.Combine(scratch, "My Config.rewasd");
        ProfileIo.Save(source, Kit.Profile());

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);

        Assert.True(viewModel.ImportConfig(source));
        Assert.True(viewModel.ImportConfig(source));
        Assert.True(viewModel.ImportConfig(source));

        string first = IOPath.Combine(root, "My Config", "Controller", "My Config.rewasd");
        string second = IOPath.Combine(root, "My Config (2)", "Controller", "My Config.rewasd");
        string third = IOPath.Combine(root, "My Config (3)", "Controller", "My Config.rewasd");

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
        Assert.NotNull(ProfileIo.Load(third));
        Assert.Equal(3, viewModel.Library.Games.Count);
    }

    // ------------------------------------------------------------------ 4) invalid import

    [Fact]
    public void Import_InvalidFile_IsReportedAndLeavesNoHalfCopy()
    {
        string root = NewProfilesRoot();
        string scratch = NewScratchFolder();
        string source = IOPath.Combine(scratch, "broken.rewasd");
        File.WriteAllText(source, "{ \"schemaVersion\": ");

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);

        Assert.False(viewModel.ImportConfig(source));
        Assert.Contains("导入失败", viewModel.InlineMessage);
        Assert.False(Directory.Exists(IOPath.Combine(root, "broken")));
        Assert.Empty(viewModel.Library.Games);
    }

    // ------------------------------------------------------------------ 5) export

    [Fact]
    public void Export_WritesTheLoadedProfileToTheChosenPath()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Demo");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "Config.rewasd");

        GamepadProfile original = Kit.Profile();
        Kit.AddMask(original, 1, Kit.Btn(GamepadButtons.A));
        Kit.Map(original, 1, ActivatorType.Single, ActivationMode.Onetime, macros: Kit.Key(Dik.G));
        ProfileIo.Save(profilePath, original);

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);
        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));

        string destination = IOPath.Combine(NewScratchFolder(), "exported", "Copy.rewasd");
        Assert.True(viewModel.ExportConfig(destination));

        Assert.True(File.Exists(destination));
        GamepadProfile exported = ProfileIo.Load(destination);
        Assert.Equal(original.Mappings!.Count, exported.Mappings!.Count);
        Assert.Equal(Dik.G, Assert.Single(exported.Mappings!).Macros![0].Keyboard!.ButtonId);
    }

    // ------------------------------------------------------------------ 6) presets

    [Fact]
    public void SaveAsPreset_AddsDedupedFilesAndListsThePresetGroup()
    {
        string root = NewProfilesRoot();
        string presetsRoot = IOPath.Combine(Directory.GetParent(root)!.FullName, "Presets");
        Directory.CreateDirectory(presetsRoot);

        string gameFolder = IOPath.Combine(root, "Demo");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "Config.rewasd");
        ProfileIo.Save(profilePath, Kit.Profile());

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);
        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));
        Assert.Equal(presetsRoot, viewModel.Library.PresetsRoot);

        Assert.True(viewModel.SaveCurrentAsPreset("My Preset"));
        Assert.True(viewModel.SaveCurrentAsPreset("My Preset"));

        Assert.True(File.Exists(IOPath.Combine(presetsRoot, "My Preset.rewasd")));
        Assert.True(File.Exists(IOPath.Combine(presetsRoot, "My Preset (2).rewasd")));

        ProfileGroupViewModel presets = Assert.Single(viewModel.Library.Games, group => group.IsPreset);
        Assert.Equal("预设", presets.Name);
        Assert.Equal(2, presets.Profiles.Count);
    }

    // ------------------------------------------------------------------ 7) corrupt settings

    [Fact]
    public void Settings_CorruptFileFallsBackToDefaults()
    {
        string path = IOPath.Combine(NewScratchFolder(), "settings.json");
        File.WriteAllText(path, "{ this is definitely not json ");

        AppSettings settings = AppSettings.Load(path);

        Assert.False(settings.CloseToTray);   // v2 起默认「关闭=退出」
        Assert.False(settings.MinimizeToTrayOnStart);
        Assert.False(settings.AutoSwitchEnabled);
        Assert.Equal(AppSettings.DefaultLanguage, settings.Language);
        Assert.Equal("", settings.ProfilesRoot);
        Assert.Equal(4, settings.Slots.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, settings.Slots.Select(slot => slot.Slot).ToArray());
        Assert.All(settings.Slots, slot => Assert.Equal(AppSettings.DefaultSlotHoldMs, slot.HoldDurationMs));
    }

    // ------------------------------------------------------------------ 8) settings round-trip

    [Fact]
    public void Settings_RoundTripPersistsValuesAndClampsSlotDurations()
    {
        string path = IOPath.Combine(NewScratchFolder(), "settings.json");
        var settings = new AppSettings
        {
            MinimizeToTrayOnStart = true,
            CloseToTray = false,
            AutoSwitchEnabled = true,
            ProfilesRoot = @"C:\Temp\PadShiftProfiles",
            Language = AppSettings.DefaultLanguage,
        };
        settings.Slots[0].Name = "主位";
        settings.Slots[0].Buttons = [(int)GamepadButtons.LeftBumper, (int)GamepadButtons.RightBumper];
        settings.Slots[0].HoldDurationMs = 1800;
        settings.Slots[3].HoldDurationMs = 9999;

        Assert.True(settings.Save(path));

        AppSettings loaded = AppSettings.Load(path);
        Assert.True(loaded.MinimizeToTrayOnStart);
        Assert.False(loaded.CloseToTray);
        Assert.True(loaded.AutoSwitchEnabled);
        Assert.Equal(@"C:\Temp\PadShiftProfiles", loaded.ProfilesRoot);
        Assert.Equal("主位", loaded.Slots[0].Name);
        Assert.Equal(new[] { (int)GamepadButtons.LeftBumper, (int)GamepadButtons.RightBumper }, loaded.Slots[0].Buttons);
        Assert.Equal(1800, loaded.Slots[0].HoldDurationMs);
        Assert.Equal(AppSettings.MaxSlotHoldMs, loaded.Slots[3].HoldDurationMs);

        // The preference window clamps while typing, too.
        var slot = new SlotHotkeyViewModel(new SlotHotkeySetting { Slot = 2, Name = "Slot 2", HoldDurationMs = 500 });
        slot.HoldDurationMs = 10;
        Assert.Equal(AppSettings.MinSlotHoldMs, slot.HoldDurationMs);
        slot.HoldDurationMs = 10_000;
        Assert.Equal(AppSettings.MaxSlotHoldMs, slot.HoldDurationMs);

        // Combinations are parsed from names or numeric ids.
        Assert.True(SlotHotkeyViewModel.TryParseCombo("LB+RB", out int[] parsed, out _));
        Assert.Equal(new[] { (int)GamepadButtons.LeftBumper, (int)GamepadButtons.RightBumper }, parsed);
        Assert.True(SlotHotkeyViewModel.TryParseCombo("4+5", out int[] numeric, out _));
        Assert.Equal(new[] { 4, 5 }, numeric);
        Assert.False(SlotHotkeyViewModel.TryParseCombo("not-a-button", out _, out string? parseError));
        Assert.NotNull(parseError);

        // Saving through the preferences window rejects invalid and duplicated combinations.
        string prefsPath = IOPath.Combine(NewScratchFolder(), "prefs.json");
        var preferences = new PreferencesViewModel(new AppSettings(), prefsPath);
        preferences.Slots[0].ComboInput = "LB+RB";
        preferences.Slots[1].ComboInput = "not-a-button";
        Assert.False(preferences.Save());
        Assert.Contains("Slot 2", preferences.Message);

        preferences.Slots[1].ComboInput = "LB+RB";
        Assert.False(preferences.Save());
        Assert.Contains("相同", preferences.Message);

        preferences.Slots[1].ComboInput = "";
        Assert.True(preferences.Save());
        AppSettings persisted = AppSettings.Load(prefsPath);
        Assert.Equal(new[] { (int)GamepadButtons.LeftBumper, (int)GamepadButtons.RightBumper }, persisted.Slots[0].Buttons);
        Assert.Empty(persisted.Slots[1].Buttons);
    }

    // ------------------------------------------------------------------ 9) validation / limits

    [Fact]
    public void MacroValidation_RejectsEmptyStepsClampsPauseAndEnforcesStepLimit()
    {
        var editor = new MacroEditorViewModel();
        Assert.False(editor.CanSave);
        Assert.NotEmpty(editor.Errors);

        MacroStepViewModel? keyboard = editor.AddKeyboard();
        Assert.NotNull(keyboard);
        Assert.False(editor.CanSave);
        Assert.Contains("请捕获键盘按键", editor.ErrorText);

        keyboard!.SetKeyboardKey(Dik.G, "G");
        Assert.True(editor.CanSave);

        MacroStepViewModel? pause = editor.AddPause(120);
        Assert.NotNull(pause);
        pause!.PauseMs = 600_001;
        Assert.Equal(MacroStepViewModel.MaxPauseMs, pause.PauseMs);
        pause.PauseMs = -5;
        Assert.Equal(0, pause.PauseMs);

        var limited = new MacroEditorViewModel();
        for (int index = 0; index < MacroEditorViewModel.MaxSteps; index++)
        {
            Assert.NotNull(limited.AddBreak());
        }

        Assert.Equal(MacroEditorViewModel.MaxSteps, limited.Steps.Count);
        Assert.Null(limited.AddBreak());
        Assert.Equal($"宏最多 {MacroEditorViewModel.MaxSteps} 步", limited.Message);
    }

    // ------------------------------------------------------------------ 10) turbo parameters

    [Fact]
    public void TurboParameters_AreWrittenToActivatorParamsAndReloaded()
    {
        string root = NewProfilesRoot();
        string gameFolder = IOPath.Combine(root, "Demo");
        Directory.CreateDirectory(gameFolder);
        string profilePath = IOPath.Combine(gameFolder, "Turbo.rewasd");
        ProfileIo.Save(profilePath, Kit.Profile());

        using var host = new FakeRemapHost();
        using var viewModel = new MainViewModel(host, root);
        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(viewModel.BindingEditor.LoadProfile(item.Path));
        viewModel.Gamepad.Select(viewModel.Gamepad.Controls.Single(control => control.ButtonId == GamepadButtons.A));

        BindingEditorViewModel editor = viewModel.BindingEditor;
        editor.SetTarget(BindingTargetKind.Keyboard);
        editor.SetKeyboardTarget(Dik.G, "G");
        editor.IsTurbo = true;
        editor.TurboTimes = 3;
        editor.TurboPause = 250;
        editor.TurboMacro = true;

        MappingRule? rule = editor.Commit();
        Assert.NotNull(rule);
        Assert.Equal(ActivationMode.Turbo, rule!.Condition.Mask!.Activator.Mode);
        Assert.Equal(3, rule.Condition.Mask.Activator.Params!.Times);
        Assert.Equal(250, rule.Condition.Mask.Activator.Params.Pause);
        Assert.True(rule.Condition.Mask.Activator.Params.Macro);
        Assert.True(editor.Save());

        GamepadProfile reloaded = ProfileIo.Load(profilePath);
        MappingRule loaded = Assert.Single(reloaded.Mappings!);
        Assert.Equal(ActivationMode.Turbo, loaded.Condition.Mask!.Activator.Mode);
        Assert.Equal(3, loaded.Condition.Mask.Activator.Params!.Times);
        Assert.Equal(250, loaded.Condition.Mask.Activator.Params.Pause);
        Assert.True(loaded.Condition.Mask.Activator.Params.Macro);

        // A fresh panel reads the parameters back into its turbo fields.
        var second = new BindingEditorViewModel();
        second.LoadProfile(profilePath);
        var pad = new GamepadViewModel();
        second.SelectControl(pad.Controls.Single(control => control.ButtonId == GamepadButtons.A));

        Assert.True(second.IsTurbo);
        Assert.Equal(3, second.TurboTimes);
        Assert.Equal(250, second.TurboPause);
        Assert.True(second.TurboMacro);
    }
}
