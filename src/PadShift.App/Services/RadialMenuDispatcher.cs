using System.Globalization;
using PadShift.App.Infrastructure;

namespace PadShift.App.Services;

/// <summary>
/// The app operations a radial sector can invoke. Keep this interface free of WPF types so the
/// dispatcher (and the ViewModel that implements it) can be driven head-less by tests.
/// </summary>
public interface IRadialMenuHost
{
    /// <summary>Selects layer 0..12 for the binding panel/status display.</summary>
    bool SwitchLayer(int layer);

    /// <summary>Selects a controller slot (0-based, 0..3).</summary>
    bool SwitchSlot(int slot);

    /// <summary>Enables or disables remapping.</summary>
    bool SetRemapEnabled(bool enabled);

    /// <summary>Toggles remapping.</summary>
    bool ToggleRemap();

    /// <summary>Plays a key sequence ("Ctrl+Shift+Esc", "F5;pause:200;F6").</summary>
    bool RunMacro(string macro);

    /// <summary>Opens the preferences window.</summary>
    bool OpenPreferences();
}

/// <summary>
/// Maps a sector to its host operation. All argument parsing lives here, so the window only has to
/// report "sector N was applied".
/// </summary>
public sealed class RadialMenuDispatcher
{
    private readonly IRadialMenuHost _host;

    public RadialMenuDispatcher(IRadialMenuHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Last failure reason (Chinese), or null after a successful dispatch.</summary>
    public string? LastError { get; private set; }

    public bool Execute(RadialSectorSetting? sector)
    {
        if (sector is null)
        {
            LastError = null;
            return true;
        }

        return Execute(sector.Action, sector.Argument);
    }

    public bool Execute(RadialSectorAction action, string? argument)
    {
        string text = (argument ?? "").Trim();
        LastError = null;

        try
        {
            switch (action)
            {
                case RadialSectorAction.Empty:
                    return true;

                case RadialSectorAction.SwitchLayer:
                    if (!TryParseInt(text, 0, 12, out int layer))
                    {
                        LastError = "切换层需要 0–12 的层编号";
                        return false;
                    }

                    if (!_host.SwitchLayer(layer))
                    {
                        // MainViewModel may have a more specific reason; when it does not, this is
                        // the documented "profile does not define that layer" case.
                        LastError = $"该配置没有第 {layer} 层";
                        return false;
                    }

                    return true;

                case RadialSectorAction.SwitchSlot:
                    if (!TryParseInt(text, 1, 4, out int slot))
                    {
                        LastError = "切换槽位需要 1–4 的槽位编号";
                        return false;
                    }

                    return _host.SwitchSlot(slot - 1);

                case RadialSectorAction.ToggleRemap:
                    if (text.Length == 0 ||
                        text.Equals("toggle", StringComparison.OrdinalIgnoreCase) ||
                        text is "切换" or "开关")
                    {
                        return _host.ToggleRemap();
                    }

                    if (IsTrue(text))
                    {
                        return _host.SetRemapEnabled(true);
                    }

                    if (IsFalse(text))
                    {
                        return _host.SetRemapEnabled(false);
                    }

                    LastError = "启用/停用映射的参数应为 on、off 或留空";
                    return false;

                case RadialSectorAction.RunMacro:
                    if (text.Length == 0)
                    {
                        LastError = "执行宏需要填写按键序列";
                        return false;
                    }

                    return _host.RunMacro(text);

                case RadialSectorAction.OpenPreferences:
                    return _host.OpenPreferences();

                default:
                    LastError = "未知的扇区动作";
                    return false;
            }
        }
        catch (Exception ex)
        {
            LastError = "扇区动作执行失败: " + ex.Message;
            return false;
        }
    }

    private static bool TryParseInt(string text, int min, int max, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= min && value <= max)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool IsTrue(string text) =>
        text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
        text is "1" or "启用" or "开";

    private static bool IsFalse(string text) =>
        text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("disable", StringComparison.OrdinalIgnoreCase) ||
        text is "0" or "停用" or "关";
}
