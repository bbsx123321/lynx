using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PadShift.Core.Host;

/// <summary>对外暴露的控制器快照（HTTP API / UI 共用）。字段名与 the v2.6 protocol v2.6 DTO 对齐。</summary>
public sealed record HostController(
    Guid Id,
    string Name,
    string ControllerType,
    string ConnectionType,
    int VendorId,
    int ProductId,
    bool HasGyro,
    bool HasTouchpad,
    bool HasRumble,
    int BatteryPercent);

/// <summary>映射总状态。数值与 the v2.6 protocol 的 <c>RemapState</c> 枚举一致。</summary>
public enum RemapState
{
    NothingApplied = 0,
    RemapOn = 1,
    RemapOff = 2,
}

/// <summary>层（shift）信息，供 UI 显示层列表。</summary>
public sealed record ShiftInfo(int Shift, string Name);

/// <summary>一次 ApplyConfig 的结果。</summary>
public sealed record ApplyConfigResult(bool Success, string? Error, int Slot);

/// <summary>
/// 最近一次轮询到的控制器状态快照。数组由宿主复制，调用方可以安全读取或修改，
/// 不会影响引擎内部的 <see cref="PadShift.Core.ControllerState"/>。
/// </summary>
/// <param name="Buttons">数字按钮位图，索引见 <see cref="PadShift.Core.ButtonIds"/>。</param>
/// <param name="Axes">模拟轴，索引见 <see cref="PadShift.Core.Axes"/>；触发器为 0..65535（旧输入层为 0..255）。</param>
/// <param name="Connected">采样时设备是否仍在线。</param>
/// <param name="TimestampMs">采样时刻（<see cref="Environment.TickCount64"/> 毫秒）。</param>
public readonly record struct ControllerStateSnapshot(bool[] Buttons, int[] Axes, bool Connected, long TimestampMs);

/// <summary>启用映射的结果。</summary>
public sealed record EnableRemapResult(bool Success, string? Error);

/// <summary>生命周期事件；API 层转成 SSE，UI 层转成 UI 刷新。</summary>
public abstract record HostEvent(DateTimeOffset At);

public sealed record ControllerConnectedEvent(HostController Controller, DateTimeOffset At) : HostEvent(At);
public sealed record ControllerDisconnectedEvent(Guid ControllerId, DateTimeOffset At) : HostEvent(At);
public sealed record RemapStateChangedEvent(Guid ControllerId, RemapState State, DateTimeOffset At) : HostEvent(At);
public sealed record SlotChangedEvent(Guid ControllerId, int Slot, DateTimeOffset At) : HostEvent(At);
public sealed record ShiftChangedEvent(Guid ControllerId, int Shift, string Name, DateTimeOffset At) : HostEvent(At);
public sealed record ConfigAppliedEvent(Guid ControllerId, string ConfigPath, int Slot, DateTimeOffset At) : HostEvent(At);
public sealed record BatteryLevelChangedEvent(Guid ControllerId, int Percent, DateTimeOffset At) : HostEvent(At);

/// <summary>某个槽位上已应用的配置被 <see cref="IRemapHost.ClearSlot"/> 清除。</summary>
public sealed record SlotClearedEvent(Guid ControllerId, int Slot, DateTimeOffset At) : HostEvent(At);

/// <summary>
/// The runaway-output watchdog disabled remapping for one controller: it kept submitting more
/// than the configured outputs per second for several consecutive seconds. The UI must tell the
/// user that remapping was stopped automatically (and why) instead of silently dropping output.
/// </summary>
public sealed record OutputLoopDetectedEvent(Guid ControllerId, int SubmissionsPerSecond, DateTimeOffset At) : HostEvent(At);

/// <summary>
/// 进程自动切换命中了 profile 并成功应用。只在 host 的自动切换开启时出现；
/// <see cref="ProfilePath"/> 是实际生效的 profile 绝对路径，<see cref="ProcessName"/> 是触发它的进程名。
/// </summary>
public sealed record AutoSwitchAppliedEvent(Guid ControllerId, string ProfilePath, string ProcessName, DateTimeOffset At) : HostEvent(At);

/// <summary>
/// 引擎对外门面：HTTP API 与 UI 都只依赖这个接口，不依赖具体映射实现。
/// 所有方法都必须是线程安全的（API 会在 Kestrel 的工作线程上调用）。
/// </summary>
public interface IRemapHost : IDisposable
{
    /// <summary>启动输入轮询循环。</summary>
    void Start();

    /// <summary>停止轮询并释放所有输出（等同于 ReleaseAll）。</summary>
    void Stop();

    bool RemapEnabled { get; }

    /// <summary>当前已连接的控制器（含虚拟输出设备）。</summary>
    IReadOnlyList<HostController> Controllers { get; }

    /// <summary>
    /// Optional diagnostic: why the virtual-pad output is unavailable (for example a missing
    /// ViGEmBus driver), or <see langword="null"/> when it is available or the host cannot tell.
    /// The UI appends this to a failed <see cref="EnableRemap"/> message so the reason is actionable.
    /// </summary>
    /// <remarks>有默认实现是为了让只关心其它成员的第三方 <see cref="IRemapHost"/> 实现保持源码兼容。</remarks>
    string? OutputProblem => null;

    IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId);

    RemapState GetRemapState(Guid controllerId);

    /// <summary>应用一个 .rewasd 到指定槽位。路径必须是主机上真实存在的文件。</summary>
    Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot);

    /// <summary>启用映射；controllerId 为 null 表示全局启用。</summary>
    EnableRemapResult EnableRemap(Guid? controllerId);

    void DisableRemap(Guid? controllerId);

    void SelectSlot(Guid controllerId, int slot);

    int GetCurrentSlot(Guid controllerId);

    /// <summary>
    /// Selects a layer (shift) for the controller without needing a profile trigger. Layer 0 is the
    /// base/main layer; 1..N are declared by <c>shifts[]</c>. Returns <see langword="false"/> when the
    /// controller is unknown or the loaded profile does not declare that layer, in which case the
    /// active layer is unchanged.
    /// </summary>
    /// <remarks>
    /// Default implementation keeps third-party <see cref="IRemapHost"/> fakes source-compatible.
    /// </remarks>
    bool SetLayer(Guid controllerId, int layer) => false;

    /// <summary>Active layer for the controller; 0 when there is no controller/profile or the base layer is active.</summary>
    int GetLayer(Guid controllerId) => 0;

    /// <summary>某个槽位当前生效的配置路径列表。</summary>
    IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot);

    /// <summary>
    /// 清除指定槽位已应用的配置（<c>POST /v2.6/ControllerService/ClearSlot</c>）。
    /// 若该槽位正是引擎当前加载的配置，则卸载引擎里的 profile；当该控制器所有槽位都不再有
    /// 配置时停止 remap（<see cref="RemapState.NothingApplied"/>）。
    /// </summary>
    /// <remarks>
    /// 有默认实现（空操作）是为了让只关心其它成员的第三方 <see cref="IRemapHost"/> 实现保持源码兼容；
    /// 引擎宿主 <c>RemapHost</c> 与 API 的开发宿主都覆盖了它。
    /// </remarks>
    void ClearSlot(Guid controllerId, int slot)
    {
        // 默认实现：不支持槽位管理的宿主没有可清除的状态。
    }

    /// <summary>供 KeyPressedPoller 使用：该扫描码当前是否按下。</summary>
    bool IsKeyPressed(Guid controllerId, int scanCode);

    /// <summary>最近一次轮询到的控制器状态快照；没有该控制器时返回 false。</summary>
    /// <remarks>
    /// 默认实现返回 false，让只关心其它成员的第三方 <see cref="IRemapHost"/> 实现保持源码兼容。
    /// 返回的数组是宿主的副本，调用方修改它们不会影响引擎状态。
    /// </remarks>
    bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
    {
        state = default;
        return false;
    }

    /// <summary>震动请求（强度 0–255，时长 ms，电机位掩码）。</summary>
    void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs);

    event EventHandler<HostEvent>? Event;
}
