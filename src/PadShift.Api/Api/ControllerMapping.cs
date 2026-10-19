using PadShift.Core.Host;

namespace PadShift.Api;

/// <summary>把 <see cref="HostController"/> / 文件路径映射成 the v2.6 protocol 形状的 DTO。</summary>
public static class ControllerMapping
{
    public const string PeripheralControllerDataType = "PeripheralController";

    /// <summary>列表项：带上当前槽位与 remap 状态（列表里的 <paramref name="host"/> 是实时快照）。</summary>
    public static PeripheralControllerDto ToListDto(IRemapHost host, HostController controller) => new(
        PeripheralControllerDataType,
        [ToDeviceDto(controller, SlotCodec.TryFromIndex(SafeSlot(host, controller.Id)), SafeState(host, controller.Id))]);

    /// <summary>事件载荷用：只用事件里携带的信息，不回调 host，避免在 host 线程上重入。</summary>
    public static PeripheralControllerDto ToListDto(HostController controller) => new(
        PeripheralControllerDataType,
        [ToDeviceDto(controller)]);

    public static PeripheralDeviceDto ToDeviceDto(HostController controller) => new(
        controller.Id,
        controller.ControllerType,
        controller.ConnectionType,
        new ManufacturerInfoDto(controller.VendorId, controller.ProductId),
        Version: 0,
        controller.VendorId,
        controller.ProductId,
        controller.Name,
        controller.HasGyro,
        controller.HasTouchpad,
        controller.HasRumble,
        controller.BatteryPercent);

    public static PeripheralDeviceDto ToDeviceDto(HostController controller, Slot? slot, RemapState state) =>
        ToDeviceDto(controller) with
        {
            CurrentSlot = slot,
            RemapStateValue = state,
        };

    public static AppliedConfigDto ToAppliedConfigDto(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return new AppliedConfigDto(
            Id: null,
            GameId: null,
            Name: Path.GetFileNameWithoutExtension(path),
            GameName: string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory),
            Path: path);
    }

    /// <summary>状态读取失败不该让整个设备列表 500，退回默认值。</summary>
    private static int SafeSlot(IRemapHost host, Guid controllerId)
    {
        try
        {
            return host.GetCurrentSlot(controllerId);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static RemapState SafeState(IRemapHost host, Guid controllerId)
    {
        try
        {
            return host.GetRemapState(controllerId);
        }
        catch (Exception)
        {
            return RemapState.NothingApplied;
        }
    }
}
