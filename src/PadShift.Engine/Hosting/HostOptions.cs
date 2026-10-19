using System;
using System.Collections.Generic;
using System.IO;

namespace PadShift.Engine.Hosting;

/// <summary>
/// 手柄组合键 → 槽位热键（长按触发）。<see cref="Slot"/> 是 the reference tool 的 1 基槽位号（1..4）；
/// <see cref="Buttons"/> 必须同时按住 <see cref="HoldMs"/> 毫秒才切换一次，松开前不会重复触发。
/// </summary>
/// <param name="Slot">目标槽位，1..4（宿主内部转换成 0..3 的槽位索引）。</param>
/// <param name="Buttons">组合键按钮号（<see cref="PadShift.Core.ButtonIds"/>）；不能为空。</param>
/// <param name="HoldMs">长按时间，50..2500 毫秒。</param>
public sealed record SlotHotkey
{
    public SlotHotkey(int Slot, int[] Buttons, int HoldMs = 500)
    {
        if (Slot is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(Slot), Slot, "Slot must be 1..4.");
        }

        ArgumentNullException.ThrowIfNull(Buttons);
        if (Buttons.Length == 0)
        {
            throw new ArgumentException("Buttons must contain at least one button id.", nameof(Buttons));
        }

        for (int i = 0; i < Buttons.Length; i++)
        {
            if (Buttons[i] is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Buttons),
                    Buttons[i],
                    "Every hotkey button must be in 0..255.");
            }
        }

        if (HoldMs is < 50 or > 2500)
        {
            throw new ArgumentOutOfRangeException(nameof(HoldMs), HoldMs, "HoldMs must be 50..2500.");
        }

        this.Slot = Slot;
        this.Buttons = (int[])Buttons.Clone();
        this.HoldMs = HoldMs;
    }

    public int Slot { get; }

    /// <summary>组合键按钮。构造时复制，调用方之后改数组不会影响已配置的热键。</summary>
    public int[] Buttons { get; }

    public int HoldMs { get; }
}

/// <summary>Process-level host configuration (poll rate, profile folder, slot count).</summary>
public sealed class HostOptions
{
    /// <summary>Poll period, milliseconds. the reference tool uses 4 ms (Normal), 2 ms (High), 1 ms (Ultra).</summary>
    public int TickMs { get; init; } = 4;

    /// <summary>屏蔽背键（P1–P4）：快照拷贝时清位，映射与 UI 轮询都不再看到背键输入。</summary>
    public bool MaskPaddles { get; init; }

    /// <summary>Folder scanned for <c>.rewasd</c> profiles. Empty = platform default.</summary>
    public string ProfilesRoot { get; init; } = "";

    public int MaxLayers { get; init; } = 12;

    /// <summary>Physical device id fed to every engine (see <see cref="RemapOptions.DeviceId"/>).</summary>
    public long DeviceId { get; init; } = 1;

    /// <summary>Number of profile slots; the reference tool exposes 4 (8 internally).</summary>
    public int MaxSlots { get; init; } = 4;

    /// <summary>
    /// Device ids the host must never accept as input. The client puts the virtual pad it created
    /// here (<c>XInput#N</c>), so the emulated pad can never become a remap source even if the
    /// watcher happens to see it before the exclusion is registered.
    /// </summary>
    public IReadOnlySet<string> ExcludedDeviceIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Watchdog: output submissions per second above which a controller is considered to be in an
    /// output loop. At the default 4 ms tick the ceiling is ~250/s per controller, so this only
    /// trips on a genuinely runaway loop (or a much faster tick configuration).
    /// </summary>
    public int OutputLoopSubmissionsPerSecond { get; init; } = 500;

    /// <summary>Watchdog: consecutive seconds above <see cref="OutputLoopSubmissionsPerSecond"/> before remap is disabled.</summary>
    public int OutputLoopSeconds { get; init; } = 3;

    /// <summary>
    /// 手柄组合键长按切槽热键。默认空列表（不抢占任何按钮）；按钮无需出现在 profile 里。
    /// </summary>
    public IReadOnlyList<SlotHotkey> SlotHotkeys { get; init; } = Array.Empty<SlotHotkey>();

    /// <summary>
    /// Enables the process watcher and per-process profile auto-switching
    /// (the reference 「每游戏进程关联/自动检测」). Off by default so that hosts which do not
    /// opt in never touch process enumeration or the file system in the background.
    /// </summary>
    public bool AutoSwitchEnabled { get; init; }

    /// <summary>Process poll period in milliseconds used when <see cref="AutoSwitchEnabled"/> is true.</summary>
    public int AutoSwitchPollMs { get; init; } = 2000;

    public static string DefaultProfilesRoot()
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrEmpty(common)
            ? Path.Combine("PadShift", "Profiles")
            : Path.Combine(common, "PadShift", "Profiles");
    }
}
