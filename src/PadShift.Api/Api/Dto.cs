using System.Text.Json;
using System.Text.Json.Serialization;
using PadShift.Core.Host;

namespace PadShift.Api;

/// <summary>
/// 控制器列表项：外层多态壳，<c>DataType</c> 与实测 the v2.6 protocol 响应
/// <c>[{"DataType":"PeripheralController","Controllers":[...]}]</c> 一致（docs/01-components.md §6）。
/// PadShift 把所有设备都装进这个壳，保持列表形状稳定。
/// </summary>
public sealed record PeripheralControllerDto(
    [property: JsonPropertyName("DataType")] string DataType,
    IReadOnlyList<PeripheralDeviceDto> Controllers);

/// <summary>单个物理外设。只放 PadShift 真正知道的值（不编造序列号/厂商字符串）。</summary>
public sealed record PeripheralDeviceDto(
    Guid Guid,
    string ControllerType,
    string ConnectionType,
    ManufacturerInfoDto ManufacturerInfo,
    int Version,
    int VendorId,
    int ProductId,
    string Name,
    bool HasGyro,
    bool HasTouchpad,
    bool HasRumble,
    int BatteryPercent,
    Slot? CurrentSlot = null,
    [property: JsonPropertyName("RemapState")] RemapState? RemapStateValue = null);

/// <summary>the v2.6 protocol 的 ManufacturerInfo；我们只有 VID/PID 是真实值。</summary>
public sealed record ManufacturerInfoDto(int VendorId, int ProductId);

/// <summary>the v2.6 protocol 的 AppliedConfigDto。PadShift 只跟踪文件路径，没有配置 Id / GameId。</summary>
public sealed record AppliedConfigDto(Guid? Id, Guid? GameId, string? Name, string? GameName, string? Path);

/// <summary>ApplyConfig / EnableRemap 的统一响应（字段名沿用 the v2.6 protocol 的拼写）。</summary>
public sealed record EnableRemapResponse(bool IsSucceded, bool DontReCallEnableRemap, IReadOnlyList<object> Dialogs);

public sealed record VersionInfoDto(string Version);

// ---------------------------------------------------------------- 请求体
// 字段名同时接受 the v2.6 protocol 文档里的名字（Guid / ControllerGuid / Path）和 PadShift 文档里的
// 别名（ControllerId / ConfigPath）；两条都为空时才报 400。

public sealed record ApplyConfigRequest(
    Guid? ControllerGuid,
    Guid? ControllerId,
    string? Path,
    string? ConfigPath,
    JsonElement? Slot,
    JsonElement? Bundle);

public sealed record EnableRemapRequest(
    Guid? Guid,
    Guid? ControllerId,
    bool? RemapNoToggled,
    JsonElement? Bundle);

public sealed record DisableRemapRequest(
    Guid? Guid,
    Guid? ControllerId);

public sealed record SelectSlotRequest(
    Guid? Guid,
    Guid? ControllerId,
    JsonElement? Slot);

/// <summary>
/// SetLayer request (PadShift extension, no the v2.6 protocol counterpart). Layer 0 is the main layer;
/// 1..19 must be declared by the applied profile's <c>shifts[]</c>.
/// </summary>
public sealed record SetLayerRequest(
    Guid? ControllerId,
    Guid? Guid,
    int? Layer);

/// <summary>
/// ClearSlot 请求体。文档形状是 <c>{ControllerId, Slot}</c>；同时接受 <c>Guid</c> 别名和
/// CLI 仍在发送的 <c>Slots: ["SlotN"]</c> 数组，保持旧客户端可用。
/// </summary>
public sealed record ClearSlotRequest(
    Guid? ControllerId,
    Guid? Guid,
    JsonElement? Slot,
    JsonElement? Slots);

public sealed record VibrateRequest(
    Guid? ControllerId,
    Guid? Guid,
    Guid? GamepadGuid,
    byte? LeftMotor,
    byte? RightMotor,
    JsonElement? Motors,
    int? Intensity,
    int? Duration,
    int? DurationMs);

// ---------------------------------------------------------------- SSE 载荷
// 载荷只带 PadShift 真有的字段；字段名对齐 the v2.6 protocol 的事件名（RemapState/Slot/Shift/...）。

public sealed record ControllerConnectedPayload(PeripheralControllerDto Controller);

public sealed record ControllerDisconnectedPayload(Guid ControllerGuid);

public sealed record RemapStateChangedPayload(Guid ControllerId, RemapState RemapState);

public sealed record SlotChangedPayload(Guid ControllerId, Slot? Slot);

public sealed record ShiftChangedPayload(Guid ControllerId, int Shift, string Name);

public sealed record ConfigAppliedPayload(Guid ControllerId, string ConfigPath, Slot? Slot);

public sealed record SlotClearedPayload(Guid ControllerId, Slot? Slot);

public sealed record BatteryLevelChangedPayload(Guid ControllerId, int BatteryPercent);
