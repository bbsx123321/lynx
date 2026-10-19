using System;
using System.Collections.Generic;

namespace PadShift.Core;

/// <summary>
/// 一次轮询得到的控制器快照。按钮用「位 + 模拟值」两套表示，
/// 数字按钮 0..N 对应 <see cref="ButtonIds"/> 的稳定编号，模拟轴单独成组。
/// </summary>
public sealed class ControllerState
{
    /// <summary>数字按钮按下位图（索引即按钮号，见 <see cref="ButtonIds"/>）。</summary>
    public bool[] Buttons { get; } = new bool[256];

    /// <summary>模拟轴：索引 0..7，值为 -32768..32767（触发器为 0..255 → 左移 8 位存放）。</summary>
    public int[] Axes { get; } = new int[8];

    /// <summary>陀螺仪：度/秒。</summary>
    public float GyroX { get; set; }
    public float GyroY { get; set; }
    public float GyroZ { get; set; }

    /// <summary>触摸板（若设备支持）：X/Y 归一化 0..1，Active 表示有手指。</summary>
    public bool TouchActive { get; set; }
    public float TouchX { get; set; }
    public float TouchY { get; set; }

    public long TimestampTicks { get; set; }

    public void Clear()
    {
        Array.Clear(Buttons);
        Array.Clear(Axes);
        GyroX = GyroY = GyroZ = 0;
        TouchActive = false;
    }
}

/// <summary>
/// 轴索引常量。和 the reference tool 相同顺序：左摇杆在前，触发器在最后。
/// </summary>
public static class Axes
{
    public const int LeftStickX = 0;
    public const int LeftStickY = 1;
    public const int RightStickX = 2;
    public const int RightStickY = 3;
    public const int LeftTrigger = 4;
    public const int RightTrigger = 5;
}

/// <summary>
/// 按钮编号。沿用 the reference tool 的 DIK 语义不方便（键盘才是 DIK），
/// 这里用自洽的游戏手柄编号；<see cref="ButtonIds.FromDik"/> 负责 .rewasd 里的键盘码映射。
/// </summary>
public static class ButtonIds
{
    public const int A = 0, B = 1, X = 2, Y = 3;
    public const int LeftBumper = 4, RightBumper = 5;
    public const int Back = 6, Start = 7, Guide = 8;
    public const int LeftStickClick = 9, RightStickClick = 10;
    public const int DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14;
    public const int Paddle1 = 15, Paddle2 = 16, Paddle3 = 17, Paddle4 = 18;
    public const int TouchClick = 19;
    public const int Green = 20, Red = 21, Blue = 22, Yellow = 23;
    public const int Max = 32;
}

/// <summary>输入源：一次轮询填一个 <see cref="ControllerState"/>。</summary>
public interface IInputSource : IDisposable
{
    /// <summary>设备可读标识，例如 "XInput#0" / "HID:054C:09CC"。</summary>
    string DeviceId { get; }

    /// <summary>true 表示设备仍在线；false 时应从轮询列表移除。</summary>
    bool IsConnected { get; }

    /// <summary>读取最新状态；无新数据时返回 false（状态保持上一次）。</summary>
    bool TryPoll(ControllerState state);
}

/// <summary>输出端：接收「意图」，由实现负责差异化和注入。</summary>
public interface IOutputSink : IDisposable
{
    /// <summary>把意图下发给系统。实现内部做 diff，避免每帧重复发键。</summary>
    void Submit(OutputIntent intent);

    /// <summary>释放所有按下的键/按钮（禁用映射时调用，防止卡键）。</summary>
    void ReleaseAll();
}

/// <summary>一帧的输出意图。</summary>
public sealed class OutputIntent
{
    public bool[] KeyboardKeys { get; } = new bool[256];
    public int MouseButtons { get; set; }               // bit0 左, bit1 右, bit2 中, bit3 X1, bit4 X2
    public int MouseDeltaX { get; set; }
    public int MouseDeltaY { get; set; }
    public int MouseWheel { get; set; }
    public bool[] PadButtons { get; } = new bool[32];
    public int PadLeftX { get; set; }
    public int PadLeftY { get; set; }
    public int PadRightX { get; set; }
    public int PadRightY { get; set; }
    public byte PadLeftTrigger { get; set; }
    public byte PadRightTrigger { get; set; }

    /// <summary>震动反馈（0 表示不发）。</summary>
    public byte RumbleLeft { get; set; }
    public byte RumbleRight { get; set; }

    public void Clear()
    {
        Array.Clear(KeyboardKeys);
        PadButtons.AsSpan().Clear();
        MouseButtons = 0;
        MouseDeltaX = MouseDeltaY = MouseWheel = 0;
        PadLeftX = PadLeftY = PadRightX = PadRightY = 0;
        PadLeftTrigger = PadRightTrigger = 0;
        RumbleLeft = RumbleRight = 0;
    }
}
