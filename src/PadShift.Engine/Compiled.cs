using System;
using PadShift.Core.Profile;

namespace PadShift.Engine;

/// <summary>Pre-resolved (deviceId, buttonId) pair. ButtonId is -1 when the profile was malformed.</summary>
internal readonly struct ButtonKey
{
    public readonly long DeviceId;
    public readonly int ButtonId;

    public ButtonKey(long deviceId, long buttonId)
    {
        DeviceId = deviceId;
        ButtonId = buttonId is >= 0 and < 256 ? (int)buttonId : -1;
    }
}

internal sealed class CompiledMask
{
    public long Id;
    public ButtonKey[] Keys = Array.Empty<ButtonKey>();
    public bool Active;
    public bool PrevActive;
    public bool RawActive;

    /// <summary>Rising edge of the effective (post-`unheritableMasks`) mask state.</summary>
    public bool BecameActive => Active && !PrevActive;

    /// <summary>Falling edge of the effective mask state.</summary>
    public bool BecameInactive => !Active && PrevActive;
}

internal sealed class CompiledShift
{
    public int Layer;
    public ShiftType Type;
    public ButtonKey[] Keys = Array.Empty<ButtonKey>();
    public string Name = "";
    /// <summary>Index-aligned with the compiled mask array; true when the mask is not inherited on this layer.</summary>
    public bool[]? Suppressed;
    public bool Held;
    public bool Toggled;
}

internal enum StepKind : byte
{
    None,
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
    MouseMove,
    Wheel,
    PadDown,
    PadUp,
    PadAxis,
    Pause,
    Break,
}

internal struct MacroStep
{
    public StepKind Kind;
    public int A;
    public int B;
    public int Value;

    public MacroStep(StepKind kind, int a = 0, int b = 0, int value = 0)
    {
        Kind = kind;
        A = a;
        B = b;
        Value = value;
    }
}

internal sealed class MacroProgram
{
    public MacroStep[] Steps = Array.Empty<MacroStep>();
    public int Count;
}

internal enum RunnerPhase : byte
{
    Idle,
    Running,
    WaitingRestart,
    Done,
}

internal sealed class MacroRunner
{
    public int Step;
    public long PauseEndsAt;
    public RunnerPhase Phase;
    public long RestartAt;
    public long ReleaseAt;
    public int RepeatCount;
    public bool StopRequested;
    public bool ToggleOn;

    public readonly uint[] HeldKeys = new uint[8];
    public int HeldMouse;
    public uint HeldPad;
    public readonly int[] Axis = new int[6];
    public readonly bool[] AxisSet = new bool[6];
}

internal enum GroupPhase : byte
{
    Idle,
    Down,
    Waiting,
    Consumed,
}

/// <summary>
/// Gesture detector shared by every mapping that listens to the same source
/// (mask id + declared layer, or hardware button). Sharing is what makes the
/// single/long/double/triple slots mutually exclusive, exactly like the reference
/// per-binding <c>Counter</c>/<c>PressTime</c> locals.
/// </summary>
internal sealed class ActivationGroup
{
    public int Layer = -1;
    public int MaskIndex = -1;
    public int HardwareButton = -1;

    public bool HasSingle;
    public bool HasLong;
    public bool HasDouble;
    public bool HasTriple;
    public bool HasStart;
    public bool HasRelease;

    public long LongWait = 500;
    public long SingleWait = 250;
    public long DoubleWait = 250;

    public bool Eligible;
    public bool Pressed;
    public int FireMask;
    public GroupPhase Phase;
    public int PressCount;
    public long PressStart;
    public long Deadline;
    public bool LongFired;
    public bool SingleFired;
}

internal sealed class CompiledMapping
{
    public int Layer = -1;
    public int MaskIndex = -1;
    public int HardwareButton = -1;
    public int Group = -1;

    /// <summary>Index into the engine's zone trackers, or -1 for button/mask mappings.</summary>
    public int ZoneTracker = -1;

    /// <summary>0 = low, 1 = med, 2 = high; only meaningful when <see cref="ZoneTracker"/> is set.</summary>
    public int ZoneIndex = -1;

    public ActivatorType Type;
    public ActivationMode Mode;
    public long RepeatPause;
    public long Times;

    public MacroProgram Program = new();
    public MacroRunner Runner = new();

    public bool Unmap;
    public int JumpLayer = -1;
    public long JumpDelay;
}

/// <summary>
/// 模拟轴分区状态，按 (layer, 轴来源, highZoneMax) 共享：low/med/high 三条 mapping 看到同一个
/// 当前区，保证任何时刻只有一个区处于激活态。阈值带 ±300 单位回差（见 ENGINE.md）。
/// </summary>
internal sealed class ZoneTracker
{
    public int Layer = -1;

    /// <summary>Button-axis index (4/5) for triggers; 0/1 is the stick side for magnitude zones.</summary>
    public int SourceIndex;

    public bool IsStick;

    /// <summary>highZoneMax（缺省 32767）：把 0..Max 三等分。</summary>
    public int Max = 32767;

    public int LowEdge;

    public int MidEdge;

    /// <summary>当前区（0..2），-1 表示尚未采样。</summary>
    public int Current = -1;

    /// <summary>本 tick 是否刚进入另一个区。</summary>
    public bool Changed;

    public bool Eligible;

    public int UpperEdge(int zone) => zone == 0 ? LowEdge : zone == 1 ? MidEdge : int.MaxValue;

    public int LowerEdge(int zone) => zone == 2 ? MidEdge : zone == 1 ? LowEdge : int.MinValue;
}
