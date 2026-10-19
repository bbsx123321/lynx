using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.Core.Profile;

/// <summary>层（shift）定义：由一组按钮触发，切换后启用该层下的映射。</summary>
public sealed class ShiftRule
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public ShiftType Type { get; set; } = ShiftType.Default;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>触发该层的按钮（多个为"同时按下"语义）。</summary>
    [JsonPropertyName("set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ButtonRef>? Set { get; set; }

    /// <summary>这些掩码在本层下不再继承（例如切层后旧掩码必须失效）。</summary>
    [JsonPropertyName("unheritableMasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<long>? UnheritableMasks { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>掩码（mask）：一组必须同时按住的按钮；用于组合键条件，不是层。</summary>
public sealed class MaskRule
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ButtonRef>? Set { get; set; }

    [JsonPropertyName("radialMenuSet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? RadialMenuSet { get; set; }

    [JsonPropertyName("crosshairSet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? CrosshairSet { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>一个物理按钮引用。<c>buttonId</c> 在键盘设备上是 DIK 扫描码，在手柄上是按钮号。</summary>
public sealed class ButtonRef
{
    [JsonPropertyName("deviceId")]
    public long DeviceId { get; set; }

    [JsonPropertyName("buttonId")]
    public long ButtonId { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("extended")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Extended { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>映射规则：条件满足时执行动作（宏/跳层/硬件改写/屏蔽）。</summary>
public sealed class MappingRule
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("condition")]
    public MappingCondition Condition { get; set; } = new();

    [JsonPropertyName("macros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MacroElement>? Macros { get; set; }

    /// <summary>对硬件输出的直接改写，例如 <c>{"unmap":true}</c>。</summary>
    [JsonPropertyName("hardware")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MappingHardware? Hardware { get; set; }

    [JsonPropertyName("jumpToLayer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? JumpToLayer { get; set; }

    [JsonPropertyName("mouseRegion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? MouseRegion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MappingCondition
{
    /// <summary>要求当前处于某层（不填表示任意层）。</summary>
    [JsonPropertyName("shiftId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ShiftId { get; set; }

    [JsonPropertyName("mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionMask? Mask { get; set; }

    [JsonPropertyName("hardware")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionHardware? Hardware { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>条件里出现的所有设备 id（用于运行时快速过滤）。</summary>
    public IEnumerable<long> ReferencedDeviceIds()
    {
        if (Hardware is { } hw) yield return hw.DeviceId;
    }
}

public sealed class ConditionMask
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("activator")]
    public Activator Activator { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ConditionHardware
{
    [JsonPropertyName("deviceId")]
    public long DeviceId { get; set; }

    [JsonPropertyName("buttonId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ButtonId { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("stick")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StickKind? Stick { get; set; }

    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SideKind? Trigger { get; set; }

    [JsonPropertyName("axis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisKind? Axis { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MappingHardware
{
    [JsonPropertyName("buttonId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ButtonId { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("stick")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StickKind? Stick { get; set; }

    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SideKind? Trigger { get; set; }

    [JsonPropertyName("axis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisKind? Axis { get; set; }

    [JsonPropertyName("inverted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Inverted { get; set; }

    /// <summary>true = 该按钮在输出端被屏蔽（既不转发也不映射）。</summary>
    [JsonPropertyName("unmap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Unmap { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>激活器：决定"什么手势算触发"以及触发后按键的保持方式。</summary>
public sealed class Activator
{
    [JsonPropertyName("type")]
    public ActivatorType Type { get; set; } = ActivatorType.Single;

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivationMode? Mode { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivatorParams? Params { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>激活器参数；所有时间单位都是 <see cref="ProfileConfig.MinTimeUnit"/> 的倍数（默认 1 ms）。</summary>
public sealed class ActivatorParams
{
    [JsonPropertyName("expert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Expert { get; set; }

    /// <summary>Turbo/Repeat 的重复次数。</summary>
    [JsonPropertyName("times")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Times { get; set; }

    /// <summary>重复间隔。</summary>
    [JsonPropertyName("pause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Pause { get; set; }

    /// <summary>首次触发的延迟。</summary>
    [JsonPropertyName("delay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Delay { get; set; }

    /// <summary>true 表示 times/pause 作用于一整段宏而不是单个按键。</summary>
    [JsonPropertyName("macro")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Macro { get; set; }

    [JsonPropertyName("longwaittime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LongWaitTime { get; set; }

    [JsonPropertyName("singlewaittime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SingleWaitTime { get; set; }

    [JsonPropertyName("doublewaittime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DoubleWaitTime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 宏序列中的一个元素。文件里每个元素是一个"单键对象"（gamepad/keyboard/mouse/pause/...），
/// 所以用一个宽结构承接，未知键进 <see cref="Extra"/> 保留。
/// </summary>
public sealed class MacroElement
{
    [JsonPropertyName("gamepad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GamepadOutput? Gamepad { get; set; }

    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyboardOutput? Keyboard { get; set; }

    [JsonPropertyName("mouse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseOutput? Mouse { get; set; }

    [JsonPropertyName("userCommand")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UserCommand { get; set; }

    [JsonPropertyName("pause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PauseOutput? Pause { get; set; }

    [JsonPropertyName("break")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Break { get; set; }

    [JsonPropertyName("scroll")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Scroll { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GamepadOutput
{
    /// <summary>虚拟手柄 id（对应 devices.virtual.gamepads[].id）。</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("buttonId")]
    public long ButtonId { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>true = 直接给轴一个绝对值，而不是按一下按钮。</summary>
    [JsonPropertyName("absolute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Absolute { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Value { get; set; }

    [JsonPropertyName("pulsation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Pulsation { get; set; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Action { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class KeyboardOutput
{
    /// <summary>DIK 扫描码（见 <see cref="Dik"/>）。</summary>
    [JsonPropertyName("buttonId")]
    public long ButtonId { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Action { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("pulsation")]
    public bool? Pulsation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MouseOutput
{
    [JsonPropertyName("buttonId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ButtonId { get; set; }

    /// <summary>鼠标移动方向（相对量），与 direction 搭配。</summary>
    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Direction { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Value { get; set; }

    [JsonPropertyName("wheel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Wheel { get; set; }

    [JsonPropertyName("flick")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Flick { get; set; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Action { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PauseOutput
{
    [JsonPropertyName("value")]
    public long Value { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
