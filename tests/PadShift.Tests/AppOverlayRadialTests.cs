using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Profile;
using Xunit;
using IOPath = System.IO.Path;

namespace PadShift.Tests;

/// <summary>
/// Head-less tests for the P1 client features: radial sector math/action dispatch, the
/// confirmation gate, gyro bias statistics/profile write-back, the overlay toast queue and the
/// preferences persistence for the new tabs. No Window, no dispatcher, no devices.
/// </summary>
public sealed class AppOverlayRadialTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeRadialHost : IRadialMenuHost
    {
        public int LayerCalls { get; private set; }
        public int SlotCalls { get; private set; }
        public int ToggleCalls { get; private set; }
        public int SetRemapCalls { get; private set; }
        public int MacroCalls { get; private set; }
        public int PreferencesCalls { get; private set; }
        public int LastLayer { get; private set; } = -1;
        public int LastSlot { get; private set; } = -1;
        public bool? LastRemap { get; private set; }
        public string? LastMacro { get; private set; }

        public bool SwitchLayer(int layer)
        {
            LayerCalls++;
            LastLayer = layer;
            return true;
        }

        public bool SwitchSlot(int slot)
        {
            SlotCalls++;
            LastSlot = slot;
            return true;
        }

        public bool SetRemapEnabled(bool enabled)
        {
            SetRemapCalls++;
            LastRemap = enabled;
            return true;
        }

        public bool ToggleRemap()
        {
            ToggleCalls++;
            return true;
        }

        public bool RunMacro(string macro)
        {
            MacroCalls++;
            LastMacro = macro;
            return true;
        }

        public bool OpenPreferences()
        {
            PreferencesCalls++;
            return true;
        }
    }

    private sealed class CountingPrompt : IConfirmationPrompt
    {
        private readonly ConfirmationAnswer _answer;

        public CountingPrompt(ConfirmationAnswer answer) => _answer = answer;

        public int Calls { get; private set; }

        public ConfirmationAnswer Ask(ConfirmationRequest request)
        {
            Calls++;
            return _answer;
        }
    }

    private static string NewScratchFolder()
    {
        string folder = IOPath.Combine(
            IOPath.GetTempPath(),
            "padshift-p1-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ------------------------------------------------------------------ 1) sector geometry

    [Fact]
    public void RadialGeometry_MapsAnglesAndBoundariesToEightSectors()
    {
        // Index 0 is the top sector, clockwise: 1 top-right, 2 right, ... 7 top-left.
        Assert.Equal(0, RadialMenuGeometry.SectorIndex(0));
        Assert.Equal(0, RadialMenuGeometry.SectorIndex(22.49));
        Assert.Equal(1, RadialMenuGeometry.SectorIndex(22.5));
        Assert.Equal(1, RadialMenuGeometry.SectorIndex(45));
        Assert.Equal(2, RadialMenuGeometry.SectorIndex(90));
        Assert.Equal(4, RadialMenuGeometry.SectorIndex(180));
        Assert.Equal(6, RadialMenuGeometry.SectorIndex(270));
        Assert.Equal(0, RadialMenuGeometry.SectorIndex(337.5));
        Assert.Equal(7, RadialMenuGeometry.SectorIndex(337.4));
        Assert.Equal(0, RadialMenuGeometry.SectorIndex(360));
        Assert.Equal(7, RadialMenuGeometry.SectorIndex(-45));

        Assert.Equal(0, RadialMenuGeometry.AngleFromTopClockwise(0, -1), 3);
        Assert.Equal(45, RadialMenuGeometry.AngleFromTopClockwise(1, -1), 3);
        Assert.Equal(90, RadialMenuGeometry.AngleFromTopClockwise(1, 0), 3);
        Assert.Equal(180, RadialMenuGeometry.AngleFromTopClockwise(0, 1), 3);
        Assert.Equal(270, RadialMenuGeometry.AngleFromTopClockwise(-1, 0), 3);

        // Hit tests use pixel offsets; the dead zone and the outer radius are rejected.
        Assert.Equal(-1, RadialMenuGeometry.HitTest(0, 0, 0, 0));
        Assert.Equal(0, RadialMenuGeometry.HitTest(0, 0, 0, -100));
        Assert.Equal(2, RadialMenuGeometry.HitTest(0, 0, 100, 0));
        Assert.Equal(4, RadialMenuGeometry.HitTest(0, 0, 0, 100));
        Assert.Equal(6, RadialMenuGeometry.HitTest(0, 0, -100, 0));
        Assert.Equal(-1, RadialMenuGeometry.HitTest(0, 0, 0, 400));
    }

    // ------------------------------------------------------------------ 2) action dispatch

    [Fact]
    public void RadialDispatcher_RoutesEveryActionToTheHost()
    {
        var host = new FakeRadialHost();
        var dispatcher = new RadialMenuDispatcher(host);

        Assert.True(dispatcher.Execute(RadialSectorAction.SwitchLayer, "2"));
        Assert.Equal(2, host.LastLayer);

        Assert.True(dispatcher.Execute(RadialSectorAction.SwitchSlot, "3"));
        Assert.Equal(2, host.LastSlot); // UI 1-based -> host 0-based

        Assert.True(dispatcher.Execute(RadialSectorAction.ToggleRemap, ""));
        Assert.Equal(1, host.ToggleCalls);

        Assert.True(dispatcher.Execute(RadialSectorAction.ToggleRemap, "on"));
        Assert.True(host.LastRemap);

        Assert.True(dispatcher.Execute(RadialSectorAction.ToggleRemap, "off"));
        Assert.False(host.LastRemap);

        Assert.True(dispatcher.Execute(RadialSectorAction.RunMacro, "Ctrl+Shift+Esc"));
        Assert.Equal("Ctrl+Shift+Esc", host.LastMacro);

        Assert.True(dispatcher.Execute(RadialSectorAction.OpenPreferences, ""));
        Assert.Equal(1, host.PreferencesCalls);

        Assert.True(dispatcher.Execute(RadialSectorAction.Empty, "ignored"));
        Assert.False(dispatcher.Execute(RadialSectorAction.SwitchSlot, "9"));
        Assert.NotNull(dispatcher.LastError);
        Assert.Equal(1, host.SlotCalls); // the invalid argument never reached the host
        Assert.False(dispatcher.Execute(RadialSectorAction.SwitchLayer, "13"));
        Assert.False(dispatcher.Execute(RadialSectorAction.RunMacro, ""));
    }

    // ------------------------------------------------------------------ 3) radial settings

    [Fact]
    public void RadialSettings_DefaultsAndTriggerParsingRoundTrip()
    {
        var settings = new RadialMenuSettings();
        settings.Normalize();

        Assert.Equal(new[] { ButtonIds.Guide }, settings.TriggerButtons);
        Assert.Equal(RadialMenuSettings.DefaultHoldMs, settings.HoldDurationMs);
        Assert.Equal(8, settings.Sectors.Count);
        Assert.Equal("Guide", settings.TriggerText());

        Assert.True(RadialMenuSettings.TryParseButtons("LB+RB", out int[] buttons, out string? error));
        Assert.Null(error);
        Assert.Equal(new[] { ButtonIds.LeftBumper, ButtonIds.RightBumper }, buttons);
        Assert.Equal("LB+RB", RadialMenuSettings.FormatButtons(buttons));

        Assert.True(RadialMenuSettings.TryParseButtons("Guide+Back", out int[] guideBack, out _));
        Assert.Equal(new[] { ButtonIds.Guide, ButtonIds.Back }, guideBack);
        Assert.False(RadialMenuSettings.TryParseButtons("no-such-button", out _, out string? parseError));
        Assert.NotNull(parseError);

        var broken = new RadialMenuSettings
        {
            HoldDurationMs = 9999,
            TriggerButtons = new[] { 999, ButtonIds.Guide, ButtonIds.Guide },
            Sectors = [new RadialSectorSetting { Label = "", Action = RadialSectorAction.SwitchSlot }],
        };
        broken.Normalize();

        Assert.Equal(RadialMenuSettings.MaxHoldMs, broken.HoldDurationMs);
        Assert.Equal(new[] { ButtonIds.Guide }, broken.TriggerButtons);
        Assert.Equal(8, broken.Sectors.Count);
        Assert.False(string.IsNullOrWhiteSpace(broken.Sectors[0].Label));

        Assert.True(RadialMenuService.TryParseHotkey("Ctrl+Alt+R", out uint modifiers, out uint virtualKey));
        Assert.Equal(0x0002u | 0x0001u, modifiers);
        Assert.Equal((uint)'R', virtualKey);
        Assert.False(RadialMenuService.TryParseHotkey("Ctrl+Shift+", out _, out _));
    }

    // ------------------------------------------------------------------ 4) confirmations

    [Fact]
    public void ConfirmationService_DoNotAskAgainSuppressesAndPersists()
    {
        string path = IOPath.Combine(NewScratchFolder(), "settings.json");
        var settings = new AppSettings();
        var prompt = new CountingPrompt(new ConfirmationAnswer(true, DoNotAskAgain: true));
        var service = new ConfirmationService(settings, prompt, path);

        Assert.False(service.IsSuppressed(ConfirmationKeys.DeleteProfile));
        Assert.True(service.Confirm(ConfirmationKeys.DeleteProfile, "删除配置", "确定？", "删除"));
        Assert.Equal(1, prompt.Calls);
        Assert.True(service.IsSuppressed(ConfirmationKeys.DeleteProfile));

        // The prompt is skipped from now on, and the flag survives a reload.
        Assert.True(service.Confirm(ConfirmationKeys.DeleteProfile, "删除配置", "确定？", "删除"));
        Assert.Equal(1, prompt.Calls);

        AppSettings reloaded = AppSettings.Load(path);
        Assert.True(reloaded.Confirmations[ConfirmationKeys.DeleteProfile]);

        var another = new ConfirmationService(reloaded, prompt, path);
        Assert.True(another.Confirm(ConfirmationKeys.DeleteProfile, "删除配置", "确定？", "删除"));
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public void ConfirmationService_CancelDoesNotSuppress()
    {
        var prompt = new CountingPrompt(new ConfirmationAnswer(false, DoNotAskAgain: false));
        var service = new ConfirmationService(new AppSettings(), prompt);

        Assert.False(service.Confirm(ConfirmationKeys.DisableRemap, "停用映射", "会释放按键", "停用"));
        Assert.False(service.IsSuppressed(ConfirmationKeys.DisableRemap));
        Assert.False(service.Confirm(ConfirmationKeys.DisableRemap, "停用映射", "会释放按键", "停用"));
        Assert.Equal(2, prompt.Calls);
    }

    // ------------------------------------------------------------------ 5) gyro bias math

    [Fact]
    public void GyroBiasCalculator_UsesMeanAndMedianAndRejectsOutliers()
    {
        var samples = new List<GyroSample>(55);
        for (int index = 0; index < 50; index++)
        {
            double offset = (index % 3) * 0.1;
            samples.Add(new GyroSample(1.0 + offset, 2.0 + offset, 3.0 + offset));
        }

        for (int index = 0; index < 5; index++)
        {
            samples.Add(new GyroSample(50, 60, 70)); // bumped the pad during sampling
        }

        GyroBias meanWithOutliers = GyroBiasCalculator.Compute(samples, GyroBiasMethod.Mean, rejectOutliers: false);
        Assert.True(meanWithOutliers.X > 4, $"outliers should pull the raw mean, got {meanWithOutliers.X}");

        GyroBias robust = GyroBiasCalculator.Compute(samples, GyroBiasMethod.Mean, rejectOutliers: true);
        Assert.Equal(50, robust.SampleCount);
        Assert.InRange(robust.X, 1.0, 1.25);
        Assert.InRange(robust.Y, 2.0, 2.25);
        Assert.InRange(robust.Z, 3.0, 3.25);
        Assert.InRange(robust.DriftX, 0, 0.25);

        GyroBias median = GyroBiasCalculator.Compute(samples, GyroBiasMethod.Median, rejectOutliers: false);
        Assert.InRange(median.X, 1.0, 1.25);
        Assert.InRange(median.Y, 2.0, 2.25);
        Assert.InRange(median.Z, 3.0, 3.25);
        Assert.Equal(55, median.SampleCount);
        Assert.True(median.IsValid);

        GyroBias empty = GyroBiasCalculator.Compute(Array.Empty<GyroSample>());
        Assert.False(empty.IsValid);
        Assert.Equal(0, empty.SampleCount);
    }

    // ------------------------------------------------------------------ 6) gyro profile write-back

    [Fact]
    public void GyroCalibrationService_WritesBiasAndPreservesUnknownDefaultJson()
    {
        string folder = NewScratchFolder();
        string profilePath = IOPath.Combine(folder, "Calibrated.rewasd");

        GamepadProfile profile = Kit.Profile();
        profile.Devices.Hardware =
        [
            new HardwareDevice
            {
                Id = 1,
                Name = "gamepad",
                Default = JsonSerializer.SerializeToElement(new
                {
                    deadzone = 0,
                    nested = new { keep = "yes" },
                    gyro = new { sensitivity = 13573 },
                }),
            },
        ];
        ProfileIo.Save(profilePath, profile);

        var bias = new GyroBias
        {
            X = 1.25,
            Y = -0.5,
            Z = 0.75,
            DriftX = 0.12,
            DriftY = 0.08,
            DriftZ = 0.2,
            SampleCount = 80,
            CapturedAt = DateTimeOffset.UtcNow,
        };

        Assert.True(GyroCalibrationService.TryWriteBias(profilePath, deviceId: 1, bias, out string? error), error);

        GamepadProfile reloaded = ProfileIo.Load(profilePath);
        GyroBias? read = GyroCalibrationService.ReadBias(reloaded, 1);
        Assert.NotNull(read);
        Assert.Equal(1.25, read!.X, 3);
        Assert.Equal(-0.5, read.Y, 3);
        Assert.Equal(0.75, read.Z, 3);
        Assert.Equal(80, read.SampleCount);

        JsonElement savedDefault = reloaded.Devices.Hardware![0].Default!.Value;
        string raw = savedDefault.GetRawText();
        Assert.Contains("\"keep\": \"yes\"", raw);
        Assert.Contains("\"sensitivity\": 13573", raw);
        Assert.True(savedDefault.TryGetProperty("gyroBias", out JsonElement written));
        Assert.Equal(1.25, written.GetProperty("x").GetDouble(), 3);
    }

    // ------------------------------------------------------------------ 7) overlay toast

    [Fact]
    public void OverlayToastQueue_ShowsOnlyTheLastMessageThenFadesAndExpires()
    {
        var queue = new OverlayToastQueue(1500);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        queue.Enqueue("映射已启用", start);
        queue.Enqueue("已切换到 Slot 2", start);

        Assert.Equal("已切换到 Slot 2", queue.Current(start + TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1.0, queue.Opacity(start + TimeSpan.FromMilliseconds(100)), 3);

        // 1.5 s lifetime + 250 ms fade.
        Assert.Equal(0.5, queue.Opacity(start + TimeSpan.FromMilliseconds(1625)), 2);
        Assert.Null(queue.Current(start + TimeSpan.FromMilliseconds(2000)));
        Assert.Equal(0.0, queue.Opacity(start + TimeSpan.FromMilliseconds(2000)), 3);

        queue.Enqueue("映射已停用", start + TimeSpan.FromMilliseconds(2100));
        Assert.Equal("映射已停用", queue.Current(start + TimeSpan.FromMilliseconds(2200)));
    }

    // ------------------------------------------------------------------ 8) preferences persistence

    [Fact]
    public void Preferences_SavesRadialOverlayAndConfirmationSettings()
    {
        string path = IOPath.Combine(NewScratchFolder(), "prefs.json");
        var preferences = new PreferencesViewModel(new AppSettings(), path);

        preferences.RadialMenu.Enabled = true;
        preferences.RadialMenu.TriggerText = "LB+RB";
        preferences.RadialMenu.HoldDurationMs = 500;
        preferences.RadialMenu.Sectors[0].SelectedAction =
            preferences.RadialMenu.Actions.Single(option => option.Action == RadialSectorAction.RunMacro);
        preferences.RadialMenu.Sectors[0].Argument = "Ctrl+Shift+Esc";

        preferences.Overlay.Enabled = true;
        preferences.Overlay.ShowToast = false;
        preferences.Overlay.CenterOnScreen = false;
        preferences.Overlay.OffsetX = 24;
        preferences.Overlay.OffsetY = -16;
        preferences.Overlay.Shape = CrosshairShape.Dot;
        preferences.Overlay.Size = 48;
        preferences.Overlay.Thickness = 3;
        preferences.Overlay.Color = "#FF112233";
        preferences.Overlay.UseMagnifier = true;
        preferences.Overlay.Magnification = 4;

        preferences.Confirmations.Confirmations[0].Suppressed = true;

        Assert.True(preferences.Save(), preferences.Message);

        AppSettings saved = AppSettings.Load(path);
        Assert.True(saved.RadialMenu.Enabled);
        Assert.Equal(new[] { ButtonIds.LeftBumper, ButtonIds.RightBumper }, saved.RadialMenu.TriggerButtons);
        Assert.Equal(500, saved.RadialMenu.HoldDurationMs);
        Assert.Equal(RadialSectorAction.RunMacro, saved.RadialMenu.Sectors[0].Action);
        Assert.Equal("Ctrl+Shift+Esc", saved.RadialMenu.Sectors[0].Argument);

        Assert.True(saved.Overlay.Enabled);
        Assert.False(saved.Overlay.ShowToast);
        Assert.False(saved.Overlay.CenterOnScreen);
        Assert.Equal(24, saved.Overlay.OffsetX);
        Assert.Equal(-16, saved.Overlay.OffsetY);
        Assert.Equal(CrosshairShape.Dot, saved.Overlay.Shape);
        Assert.Equal(48, saved.Overlay.Size);
        Assert.Equal(3, saved.Overlay.Thickness);
        Assert.Equal("#FF112233", saved.Overlay.Color);
        Assert.True(saved.Overlay.UseMagnifier);
        Assert.Equal(4, saved.Overlay.Magnification);

        Assert.True(saved.Confirmations[ConfirmationKeys.DeleteProfile]);
    }

    // ------------------------------------------------------------------ 9) overlay settings sanity

    [Fact]
    public void OverlaySettings_NormalizeClampsAndRepairsColor()
    {
        var settings = new OverlaySettings
        {
            Size = 9999,
            Thickness = -5,
            Magnification = 99,
            MagnifierSizePercent = 1,
            Color = "not-a-color",
        };
        settings.Normalize();

        Assert.Equal(OverlaySettings.MaxSize, settings.Size);
        Assert.Equal(OverlaySettings.MinThickness, settings.Thickness);
        Assert.Equal(OverlaySettings.MaxMagnification, settings.Magnification);
        Assert.Equal(OverlaySettings.MinMagnifierPercent, settings.MagnifierSizePercent);
        Assert.Equal("#FF5CFF8A", settings.Color);

        settings.Color = "#112233";
        settings.Normalize();
        Assert.Equal("#FF112233", settings.Color);
    }
}
