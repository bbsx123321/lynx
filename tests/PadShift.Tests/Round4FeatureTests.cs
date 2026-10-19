using System.IO;
using System.Text.Json;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Profile;
using PadShift.Engine;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// 「子配置（每层多输出混合）」的证明：同一层内，不同物理键分别输出键盘 / 鼠标 / 虚拟手柄，
/// 或一条宏内混合多种输出 —— 每帧 OutputIntent 同时携带键鼠与手柄状态，CompositeSink 同帧分发。
/// 这正是每层多套子配置（键盘/鼠标/手柄/触控板）的混合输出语义。
/// </summary>
public class Round4FeatureTests
{
    private static int _maskSeed;

    private static long NextMask(GamepadProfile profile, int buttonId)
    {
        long id = ++_maskSeed;
        Kit.AddMask(profile, id, Kit.Btn(buttonId));
        return id;
    }

    [Fact]
    public void One_Layer_Mixes_Keyboard_Mouse_And_Pad_Outputs_In_The_Same_Frame()
    {
        GamepadProfile profile = Kit.Profile();
        long maskA = NextMask(profile, ButtonIds.A);
        long maskB = NextMask(profile, ButtonIds.B);
        long maskX = NextMask(profile, ButtonIds.X);

        // 按键 A（键盘子配置语义）→ 键盘 Space；B（鼠标子配置语义）→ 鼠标左键；
        // X（手柄子配置语义）→ 虚拟手柄 Y。三条规则同属层 0。
        Kit.Map(profile, maskA, ActivatorType.Start, ActivationMode.Onetime, macros: Kit.Key(Dik.Space, "down"));
        Kit.Map(profile, maskB, ActivatorType.Start, ActivationMode.Onetime, macros: Kit.MouseButton(1, "down"));
        Kit.Map(profile, maskX, ActivatorType.Start, ActivationMode.Onetime, macros: Kit.PadButton(ButtonIds.Y, "down"));

        RemapEngine engine = Kit.Engine(profile);
        var state = new ControllerState();
        state.Buttons[ButtonIds.A] = true;
        state.Buttons[ButtonIds.B] = true;
        state.Buttons[ButtonIds.X] = true;

        bool sawKey = false, sawMouse = false, sawPad = false;
        for (int frame = 0; frame < 6; frame++)
        {
            OutputIntent intent = engine.Tick(state, frame);
            sawKey |= intent.KeyboardKeys[Dik.Space];
            sawMouse |= (intent.MouseButtons & 1) == 1;
            sawPad |= intent.PadButtons[ButtonIds.Y];
        }

        Assert.True(sawKey, "keyboard subconfig output");
        Assert.True(sawMouse, "mouse subconfig output");
        Assert.True(sawPad, "gamepad subconfig output");
    }

    [Fact]
    public void A_Single_Macro_Can_Carry_Mixed_Output_Kinds()
    {
        GamepadProfile profile = Kit.Profile();
        long mask = NextMask(profile, ButtonIds.A);

        // 一条宏内混合：按下键 → 按下虚拟手柄键 → 移动鼠标。等价于跨子配置的一条多步宏。
        Kit.Map(
            profile,
            mask,
            ActivatorType.Start,
            ActivationMode.Onetime,
            macros: new[]
            {
                Kit.Key(Dik.Q, "down"),
                Kit.PadButton(ButtonIds.LeftBumper, "down"),
                Kit.MouseMove("up", 12),
            });

        RemapEngine engine = Kit.Engine(profile);
        var state = new ControllerState();
        state.Buttons[ButtonIds.A] = true;

        bool sawKey = false, sawPad = false, sawMouse = false;
        for (int frame = 0; frame < 6; frame++)
        {
            OutputIntent intent = engine.Tick(state, frame);
            sawKey |= intent.KeyboardKeys[Dik.Q];
            sawPad |= intent.PadButtons[ButtonIds.LeftBumper];
            sawMouse |= intent.MouseDeltaY != 0 || intent.MouseWheel != 0 || intent.MouseDeltaX != 0;
        }

        Assert.True(sawKey, "keyboard step played");
        Assert.True(sawPad, "gamepad step played");
        Assert.True(sawMouse, "mouse step played");
    }

    [Fact]
    public void Each_Layer_Selects_Its_Own_Output_Mix()
    {
        GamepadProfile profile = Kit.Profile();
        long maskA = NextMask(profile, ButtonIds.A);
        long maskLB = NextMask(profile, ButtonIds.LeftBumper);

        // 层 0：A → 键盘。层 1（LB 触发）：A → 鼠标。层与输出族的混合彼此独立。
        Kit.AddShift(profile, 1, ShiftType.Default, Kit.Btn(ButtonIds.LeftBumper));
        Kit.Map(profile, maskA, ActivatorType.Start, ActivationMode.Onetime, layer: 0, macros: Kit.Key(Dik.Space, "down"));
        Kit.Map(profile, maskA, ActivatorType.Start, ActivationMode.Onetime, layer: 1, macros: Kit.MouseButton(2, "down"));

        RemapEngine engine = Kit.Engine(profile);
        var state = new ControllerState();
        state.Buttons[ButtonIds.A] = true;

        bool sawBaseKey = false;
        for (int frame = 0; frame < 4; frame++)
        {
            sawBaseKey |= engine.Tick(state, frame).KeyboardKeys[Dik.Space];
        }

        Assert.True(sawBaseKey, "base layer outputs the keyboard mapping");

        // 松开 A，按住 LB 切到层 1，再重新按下 A：层 1 的规则（鼠标右键）应当接管。
        state.Buttons[ButtonIds.A] = false;
        engine.Tick(state, 20);
        state.Buttons[ButtonIds.LeftBumper] = true;
        engine.Tick(state, 40);
        state.Buttons[ButtonIds.A] = true;

        bool sawShiftedMouse = false;
        bool anyShiftedKey = false;
        for (int frame = 60; frame < 64; frame++)
        {
            OutputIntent intent = engine.Tick(state, frame);
            sawShiftedMouse |= (intent.MouseButtons & 2) == 2;
            anyShiftedKey |= intent.KeyboardKeys[Dik.Space];
        }

        Assert.True(sawShiftedMouse, "shift layer outputs the mouse mapping");
        Assert.False(anyShiftedKey, "the base-layer keyboard mapping is gone on layer 1");
    }

    [Fact]
    public void Binding_List_Text_Serves_As_The_Mappings_Overlay_Source()
    {
        // 悬浮映射卡与导出共用 BindingListExporter：同一段文本既可打印也可悬浮显示。
        const string json = """
            { "appName": "Mix", "devices": [ { "hardware": [ { "controllerType": "XboxOne", "config": {
                "btnA": { "gamepadDescription": "Key: Space" },
                "btnB": { "keycodeDescription": "Mouse: Left" }
            } } ] } ] }
            """;
        string text = BindingListExporter.Build(json, "Mix");
        Assert.Contains("btnA  →  Key: Space", text);
        Assert.Contains("btnB  →  Mouse: Left", text);
    }

    [Fact]
    public void Mappings_Overlay_Hotkey_Round_Trips_And_Clashes_Are_Rejected()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "padshift-r4-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings();
            var preferences = new PreferencesViewModel(settings, tempPath);
            preferences.RadialMenu.Hotkey = "Ctrl+Alt+R";
            preferences.StopMacroHotkey = "Ctrl+Alt+M";

            preferences.MappingsOverlayHotkey = "ctrl+alt+r";
            Assert.False(preferences.Save());
            Assert.Contains(Loc.T("msg.hotkeyclash"), preferences.Message);

            preferences.MappingsOverlayHotkey = "ctrl+alt+m";
            Assert.False(preferences.Save());

            preferences.MappingsOverlayHotkey = "Ctrl+Alt+B";
            Assert.True(preferences.Save(), preferences.Message);
            Assert.Equal("Ctrl+Alt+B", AppSettings.Load(tempPath).MappingsOverlayHotkey);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
