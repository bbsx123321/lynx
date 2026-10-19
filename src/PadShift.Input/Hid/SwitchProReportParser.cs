using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// Nintendo Switch Pro Controller parser for the standard full input report 0x30
/// (also accepts subcommand/IMU variants 0x21 and 0x31, which share the same header).
/// Sticks are 12-bit packed; Y is inverted for XInput-style up-positive axes.
/// v1 deliberately does not decode the IMU/gyro payload.
/// </summary>
internal sealed class SwitchProReportParser : IReportParser
{
    private const byte FullReportId = 0x30;
    private const byte SubcommandReplyId = 0x21;
    private const byte McuReportId = 0x31;
    private const int MinimumReportLength = 12;

    public string Name => "Switch Pro Controller";

    public bool TryParse(ReadOnlySpan<byte> report, ControllerState state)
    {
        if (report.Length < MinimumReportLength)
        {
            return false;
        }

        if (report[0] is not (FullReportId or SubcommandReplyId or McuReportId))
        {
            return false;
        }

        state.Clear();

        byte buttonsLow = report[3];
        byte buttonsMid = report[4];
        byte buttonsHigh = report[5];

        int leftX = report[6] | ((report[7] & 0x0F) << 8);
        int leftY = (report[7] >> 4) | (report[8] << 4);
        int rightX = report[9] | ((report[10] & 0x0F) << 8);
        int rightY = (report[10] >> 4) | (report[11] << 4);

        state.Axes[Axes.LeftStickX] = HidParserHelpers.ScaleStick12Bit(leftX);
        state.Axes[Axes.LeftStickY] = HidParserHelpers.ScaleStick12BitInverted(leftY);
        state.Axes[Axes.RightStickX] = HidParserHelpers.ScaleStick12Bit(rightX);
        state.Axes[Axes.RightStickY] = HidParserHelpers.ScaleStick12BitInverted(rightY);

        // Face buttons: the Switch layout is A (east), B (south), X (north), Y (west).
        state.Buttons[ButtonIds.A] = (buttonsLow & 0x08) != 0;
        state.Buttons[ButtonIds.B] = (buttonsLow & 0x04) != 0;
        state.Buttons[ButtonIds.X] = (buttonsLow & 0x02) != 0;
        state.Buttons[ButtonIds.Y] = (buttonsLow & 0x01) != 0;

        state.Buttons[ButtonIds.RightBumper] = (buttonsLow & 0x40) != 0; // R
        state.Buttons[ButtonIds.LeftBumper] = (buttonsHigh & 0x40) != 0; // L

        state.Buttons[ButtonIds.Back] = (buttonsMid & 0x01) != 0; // Minus
        state.Buttons[ButtonIds.Start] = (buttonsMid & 0x02) != 0; // Plus
        state.Buttons[ButtonIds.RightStickClick] = (buttonsMid & 0x04) != 0; // R stick
        state.Buttons[ButtonIds.LeftStickClick] = (buttonsMid & 0x08) != 0; // L stick
        state.Buttons[ButtonIds.Guide] = (buttonsMid & 0x10) != 0; // Home

        state.Buttons[ButtonIds.DpadDown] = (buttonsHigh & 0x01) != 0;
        state.Buttons[ButtonIds.DpadUp] = (buttonsHigh & 0x02) != 0;
        state.Buttons[ButtonIds.DpadRight] = (buttonsHigh & 0x04) != 0;
        state.Buttons[ButtonIds.DpadLeft] = (buttonsHigh & 0x08) != 0;

        // ZL/ZR are digital only; expose them as full-scale trigger axes.
        state.Axes[Axes.LeftTrigger] = (buttonsHigh & 0x80) != 0 ? HidParserHelpers.TriggerFullScale : 0;
        state.Axes[Axes.RightTrigger] = (buttonsLow & 0x80) != 0 ? HidParserHelpers.TriggerFullScale : 0;

        // No gyro, touchpad or capture-button mapping in v1.
        return true;
    }
}
