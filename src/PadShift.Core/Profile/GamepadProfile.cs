using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.Core.Profile;

/// <summary>
/// .rewasd 文件根对象（schemaVersion 3）。字段名与 the v2.6 protocol 的
/// <c>GamepadProfile</c> 一致；未知字段用 <see cref="Extra"/> 兜底，
/// 保证「读入 → 写出」不丢数据。
/// </summary>
public sealed class GamepadProfile
{
    [JsonPropertyName("schemaVersion")]
    public decimal SchemaVersion { get; set; } = 3m;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "9.5.0";

    [JsonPropertyName("config")]
    public ProfileConfig Config { get; set; } = new();

    [JsonPropertyName("devices")]
    public ProfileDevices Devices { get; set; } = new();

    [JsonPropertyName("shifts")]
    public List<ShiftRule>? Shifts { get; set; }

    [JsonPropertyName("masks")]
    public List<MaskRule>? Masks { get; set; }

    [JsonPropertyName("commands")]
    public List<JsonElement>? Commands { get; set; }

    [JsonPropertyName("mappings")]
    public List<MappingRule>? Mappings { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProfileConfig
{
    /// <summary>可为 null：原版在"未命名配置"里会写出 <c>"appName": null</c>。</summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; set; }

    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("processNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcessNames { get; set; }

    /// <summary>最小时间单位（毫秒）。所有计时参数都是它的倍数。</summary>
    [JsonPropertyName("minTimeUnit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MinTimeUnit { get; set; }

    [JsonPropertyName("led")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Led { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProfileDevices
{
    [JsonPropertyName("groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DeviceGroup>? Groups { get; set; }

    [JsonPropertyName("hardware")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HardwareDevice>? Hardware { get; set; }

    [JsonPropertyName("virtual")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VirtualDevices? Virtual { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeviceGroup
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HardwareDevice
{
    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? GroupId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HardwareKind? Kind { get; set; }

    /// <summary>设备级默认设置（触摸板模式、摇杆死区等）。结构复杂且与固件相关，按原始 JSON 保留。</summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Default { get; set; }

    /// <summary>该硬件设备自己的层（通常是虚拟输出设备的初始状态）。</summary>
    [JsonPropertyName("shifts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? Shifts { get; set; }

    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Keyboard { get; set; }

    [JsonPropertyName("mouse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Mouse { get; set; }

    [JsonPropertyName("convertToVirtual")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConvertToVirtual { get; set; }

    [JsonPropertyName("hidePhysicalGamepad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HidePhysicalGamepad { get; set; }

    [JsonPropertyName("applyToPhysicalPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ApplyToPhysicalPriority { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VirtualDevices
{
    [JsonPropertyName("gamepads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VirtualGamepad>? Gamepads { get; set; }

    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Keyboard { get; set; }

    [JsonPropertyName("mouse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Mouse { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VirtualGamepad
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public GamepadKind Kind { get; set; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Default { get; set; }

    [JsonPropertyName("udp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Udp { get; set; }

    [JsonPropertyName("virtualUsbHub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? VirtualUsbHub { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
