using HidSharp;

namespace PadShift.Input.Hid;

/// <summary>Chooses a report parser from the device's VID/PID table, with a generic fallback.</summary>
internal static class ReportParserRegistry
{
    private const int SonyVendorId = 0x054C;
    private const int NintendoVendorId = 0x057E;

    private const int DualShock4ProductId = 0x09CC;
    private const int DualShock4ProductIdAlt = 0x05C4;
    private const int DualSenseProductId = 0x0CE6;
    private const int SwitchProProductId = 0x2009;

    public static bool IsKnownDevice(HidDevice device)
    {
        int vendor = device.VendorID;
        int product = device.ProductID;

        if (vendor == SonyVendorId)
        {
            return product is DualShock4ProductId or DualShock4ProductIdAlt or DualSenseProductId;
        }

        return vendor == NintendoVendorId && product == SwitchProProductId;
    }

    public static IReportParser? Create(HidDevice device, byte[]? rawReportDescriptor)
    {
        int vendor = device.VendorID;
        int product = device.ProductID;

        if (vendor == SonyVendorId)
        {
            if (product is DualShock4ProductId or DualShock4ProductIdAlt)
            {
                return new DualShock4ReportParser();
            }

            if (product == DualSenseProductId)
            {
                return new DualSenseReportParser();
            }
        }

        if (vendor == NintendoVendorId && product == SwitchProProductId)
        {
            return new SwitchProReportParser();
        }

        if (rawReportDescriptor is { Length: > 0 })
        {
            ReportDescriptor descriptor = ReportDescriptorParser.Parse(rawReportDescriptor);
            if (descriptor.IsGamepad && descriptor.Fields.Count > 0)
            {
                return new GenericGamepadReportParser(descriptor);
            }
        }

        return null;
    }
}
