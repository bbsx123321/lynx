using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// DualSense parser. USB report 0x01 (64 bytes) and Bluetooth report 0x31 (78 bytes) carry the
/// same 63-byte payload; Bluetooth places it after a single header byte.
/// </summary>
internal sealed class DualSenseReportParser : IReportParser
{
    private const byte UsbReportId = 0x01;
    private const byte BluetoothReportId = 0x31;

    private const int UsbHeaderLength = 1;
    private const int BluetoothHeaderLength = 2;
    private const int CommonLength = 63;

    private const int GyroOffsetInCommon = 15;
    private const int TouchPointOffsetInCommon = 32;
    private const float GyroLsbPerDegreePerSecond = 1024f;
    private const float TouchpadWidth = 1920f;
    private const float TouchpadHeight = 1080f;

    public string Name => "DualSense";

    public bool TryParse(ReadOnlySpan<byte> report, ControllerState state)
    {
        if (report.Length < 12)
        {
            return false;
        }

        int header;

        switch (report[0])
        {
            case UsbReportId:
                header = UsbHeaderLength;
                break;

            case BluetoothReportId:
                if (report.Length < BluetoothHeaderLength + CommonLength)
                {
                    return false;
                }

                header = BluetoothHeaderLength;
                break;

            default:
                return false;
        }

        // Sticks (4 bytes), triggers (2), sequence (1) and the first button byte.
        if (report.Length < header + 10)
        {
            return false;
        }

        state.Clear();

        byte buttons0 = report[header + 7];
        byte buttons1 = report[header + 8];
        byte buttons2 = report[header + 9];

        state.Axes[Axes.LeftStickX] = HidParserHelpers.ScaleStickByte(report[header + 0]);
        state.Axes[Axes.LeftStickY] = HidParserHelpers.ScaleStickByteInverted(report[header + 1]);
        state.Axes[Axes.RightStickX] = HidParserHelpers.ScaleStickByte(report[header + 2]);
        state.Axes[Axes.RightStickY] = HidParserHelpers.ScaleStickByteInverted(report[header + 3]);
        state.Axes[Axes.LeftTrigger] = HidParserHelpers.ScaleTriggerByte(report[header + 4]);
        state.Axes[Axes.RightTrigger] = HidParserHelpers.ScaleTriggerByte(report[header + 5]);

        state.Buttons[ButtonIds.A] = (buttons0 & 0x20) != 0; // Cross
        state.Buttons[ButtonIds.B] = (buttons0 & 0x40) != 0; // Circle
        state.Buttons[ButtonIds.X] = (buttons0 & 0x10) != 0; // Square
        state.Buttons[ButtonIds.Y] = (buttons0 & 0x80) != 0; // Triangle

        state.Buttons[ButtonIds.LeftBumper] = (buttons1 & 0x01) != 0; // L1
        state.Buttons[ButtonIds.RightBumper] = (buttons1 & 0x02) != 0; // R1
        state.Buttons[ButtonIds.Back] = (buttons1 & 0x10) != 0; // Create
        state.Buttons[ButtonIds.Start] = (buttons1 & 0x20) != 0; // Options
        state.Buttons[ButtonIds.LeftStickClick] = (buttons1 & 0x40) != 0; // L3
        state.Buttons[ButtonIds.RightStickClick] = (buttons1 & 0x80) != 0; // R3
        state.Buttons[ButtonIds.Guide] = (buttons2 & 0x01) != 0; // PS
        state.Buttons[ButtonIds.TouchClick] = (buttons2 & 0x02) != 0; // touchpad click
        // buttons2 bit 2 is the hardware mic-mute button; intentionally not mapped.

        HidParserHelpers.ApplyHat(state, buttons0 & 0x0F);

        if (report.Length >= header + GyroOffsetInCommon + 6)
        {
            state.GyroX = HidParserHelpers.ReadGyroDegreesPerSecond(
                report, header + GyroOffsetInCommon, GyroLsbPerDegreePerSecond);
            state.GyroY = HidParserHelpers.ReadGyroDegreesPerSecond(
                report, header + GyroOffsetInCommon + 2, GyroLsbPerDegreePerSecond);
            state.GyroZ = HidParserHelpers.ReadGyroDegreesPerSecond(
                report, header + GyroOffsetInCommon + 4, GyroLsbPerDegreePerSecond);
        }

        int touchOffset = header + TouchPointOffsetInCommon;
        if (report.Length >= touchOffset + 4)
        {
            HidParserHelpers.ApplyTouchPoint(
                state,
                report.Slice(touchOffset, 4),
                TouchpadWidth,
                TouchpadHeight);
        }

        return true;
    }
}
