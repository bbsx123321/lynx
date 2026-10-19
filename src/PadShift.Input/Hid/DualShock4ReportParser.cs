using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// DualShock 4 parser.
/// USB report 0x01 (64 bytes) and Bluetooth report 0x11 (78 bytes) share the same payload;
/// the Bluetooth form is preceded by two reserved bytes. Short third-party 0x01 reports
/// (buttons/sticks/triggers only) are accepted as well.
/// </summary>
internal sealed class DualShock4ReportParser : IReportParser
{
    private const byte UsbReportId = 0x01;
    private const byte BluetoothReportId = 0x11;

    private const int UsbHeaderLength = 1;
    private const int BluetoothHeaderLength = 3;
    private const int CommonLength = 32;

    private const int TouchPointOffsetInCommon = 34;
    private const float GyroLsbPerDegreePerSecond = 1024f;
    private const float TouchpadWidth = 1920f;
    private const float TouchpadHeight = 942f;

    public string Name => "DualShock 4";

    public bool TryParse(ReadOnlySpan<byte> report, ControllerState state)
    {
        if (report.Length < UsbHeaderLength + 9)
        {
            return false;
        }

        int header;
        bool full;

        switch (report[0])
        {
            case UsbReportId:
                header = UsbHeaderLength;
                // A full USB report is 64 bytes; shorter 0x01 reports only carry the first
                // 9 payload bytes and must not be read past their end.
                full = report.Length >= 64;
                break;

            case BluetoothReportId:
                if (report.Length < BluetoothHeaderLength + CommonLength + 1)
                {
                    return false;
                }

                header = BluetoothHeaderLength;
                full = report.Length >= BluetoothHeaderLength + CommonLength + 9;
                break;

            default:
                return false;
        }

        if (report.Length < header + 9)
        {
            return false;
        }

        state.Clear();

        byte leftX = report[header + 0];
        byte leftY = report[header + 1];
        byte rightX = report[header + 2];
        byte rightY = report[header + 3];
        byte buttons0 = report[header + 4];
        byte buttons1 = report[header + 5];
        byte buttons2 = report[header + 6];
        byte leftTrigger = report[header + 7];
        byte rightTrigger = report[header + 8];

        state.Axes[Axes.LeftStickX] = HidParserHelpers.ScaleStickByte(leftX);
        state.Axes[Axes.LeftStickY] = HidParserHelpers.ScaleStickByteInverted(leftY);
        state.Axes[Axes.RightStickX] = HidParserHelpers.ScaleStickByte(rightX);
        state.Axes[Axes.RightStickY] = HidParserHelpers.ScaleStickByteInverted(rightY);
        state.Axes[Axes.LeftTrigger] = HidParserHelpers.ScaleTriggerByte(leftTrigger);
        state.Axes[Axes.RightTrigger] = HidParserHelpers.ScaleTriggerByte(rightTrigger);

        // Matches the PlayStation layout; Cross/Circle/Square/Triangle map to A/B/X/Y.
        state.Buttons[ButtonIds.A] = (buttons0 & 0x20) != 0; // Cross
        state.Buttons[ButtonIds.B] = (buttons0 & 0x40) != 0; // Circle
        state.Buttons[ButtonIds.X] = (buttons0 & 0x10) != 0; // Square
        state.Buttons[ButtonIds.Y] = (buttons0 & 0x80) != 0; // Triangle

        state.Buttons[ButtonIds.LeftBumper] = (buttons1 & 0x01) != 0; // L1
        state.Buttons[ButtonIds.RightBumper] = (buttons1 & 0x02) != 0; // R1
        state.Buttons[ButtonIds.Back] = (buttons1 & 0x10) != 0; // Share
        state.Buttons[ButtonIds.Start] = (buttons1 & 0x20) != 0; // Options
        state.Buttons[ButtonIds.LeftStickClick] = (buttons1 & 0x40) != 0; // L3
        state.Buttons[ButtonIds.RightStickClick] = (buttons1 & 0x80) != 0; // R3
        state.Buttons[ButtonIds.Guide] = (buttons2 & 0x01) != 0; // PS
        state.Buttons[ButtonIds.TouchClick] = (buttons2 & 0x02) != 0; // touchpad click

        HidParserHelpers.ApplyHat(state, buttons0 & 0x0F);

        if (full)
        {
            state.GyroX = HidParserHelpers.ReadGyroDegreesPerSecond(report, header + 12, GyroLsbPerDegreePerSecond);
            state.GyroY = HidParserHelpers.ReadGyroDegreesPerSecond(report, header + 14, GyroLsbPerDegreePerSecond);
            state.GyroZ = HidParserHelpers.ReadGyroDegreesPerSecond(report, header + 16, GyroLsbPerDegreePerSecond);

            int touchOffset = header + TouchPointOffsetInCommon;
            if (report.Length >= touchOffset + 4)
            {
                HidParserHelpers.ApplyTouchPoint(
                    state,
                    report.Slice(touchOffset, 4),
                    TouchpadWidth,
                    TouchpadHeight);
            }
        }

        return true;
    }
}
