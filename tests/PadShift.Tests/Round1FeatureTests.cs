using System.IO;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using Xunit;

namespace PadShift.Tests;

/// <summary>轮一功能：热键解析、宏中断、失焦停映射设置与停宏/径向热键互斥校验。</summary>
public class Round1FeatureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", 0x0002 | 0x0001, 'M')]
    [InlineData("ctrl+shift+f5", 0x0002 | 0x0004, 0x74)]
    [InlineData("Win+D", 0x0008, 'D')]
    [InlineData("9", 0u, '9')]
    [InlineData("F12", 0u, 0x7B)]
    public void Hotkey_Parser_Accepts_Modifier_Combo_And_Plain_Keys(string text, uint modifiers, uint vk)
    {
        Assert.True(GlobalHotkey.TryParse(text, out uint mods, out uint key));
        Assert.Equal(modifiers, mods);
        Assert.Equal(vk, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("A+B")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+Space")]
    public void Hotkey_Parser_Rejects_Malformed_Input(string text)
    {
        Assert.False(GlobalHotkey.TryParse(text, out _, out _));
    }

    [Fact]
    public void StopMacro_Request_Aborts_A_Playing_Macro()
    {
        var player = new RadialMacroPlayer();

        // sleep 注入：第一片等待后触发停宏请求，宏应在下一步前中断。
        bool aborted = !player.TryPlay("Ctrl+A;pause:500;Ctrl+B", out string? error, sleep: _ =>
            RadialMacroPlayer.RequestStop());

        Assert.True(aborted);
        Assert.Contains("中断", error);
    }

    [Fact]
    public void StopMacro_Hotkey_Must_Not_Clash_With_The_Radial_Menu_Hotkey()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "padshift-r1-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings();
            var preferences = new PreferencesViewModel(settings, tempPath);
            preferences.RadialMenu.Hotkey = "Ctrl+Alt+R";
            preferences.StopMacroHotkey = "ctrl+alt+r";

            Assert.False(preferences.Save());
            Assert.Contains(Loc.T("msg.hotkeyclash"), preferences.Message);

            preferences.StopMacroHotkey = "Ctrl+Alt+M";
            Assert.True(preferences.Save(), preferences.Message);
            Assert.Equal("Ctrl+Alt+M", settings.StopMacroHotkey);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Focus_Loss_Setting_Round_Trips()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "padshift-r1b-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings { TurnRemapOffOnFocusLoss = true, StopMacroHotkey = "Ctrl+F9" };
            var preferences = new PreferencesViewModel(settings, tempPath);
            Assert.True(preferences.TurnRemapOffOnFocusLoss);
            Assert.True(preferences.Save(), preferences.Message);
            Assert.Equal("Ctrl+F9", AppSettings.Load(tempPath).StopMacroHotkey);
            Assert.True(AppSettings.Load(tempPath).TurnRemapOffOnFocusLoss);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
