using System.Buffers.Binary;
using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>Shared byte-layout helpers for the device-family parsers.</summary>
internal static class HidParserHelpers
{
    /// <summary>Full-scale value used for analog triggers (255 * 257, the XInput convention).</summary>
    public const int TriggerFullScale = 255 * 257;

    private const int StickByteFullScale = 256;

    /// <summary>Maps an unsigned 8-bit stick axis (0..255, 128 = centre) to -32768..32767.</summary>
    public static int ScaleStickByte(byte value) => (value - 128) * StickByteFullScale;

    /// <summary>Same as <see cref="ScaleStickByte"/> but flips the sign (HID/PS Y grows downwards).</summary>
    public static int ScaleStickByteInverted(byte value) => Math.Min(32767, (128 - value) * StickByteFullScale);

    /// <summary>Maps an unsigned trigger byte (0..255) to 0..65535 (value * 257).</summary>
    public static int ScaleTriggerByte(byte value) => value * 257;

    /// <summary>Maps a 12-bit Switch stick axis (0..4095, 2048 = centre) to -32768..32752.</summary>
    public static int ScaleStick12Bit(int value) => (value - 2048) * 16;

    /// <summary>Same as <see cref="ScaleStick12Bit"/> but flips the sign.</summary>
    public static int ScaleStick12BitInverted(int value) => Math.Min(32767, (2048 - value) * 16);

    /// <summary>Reads a little-endian signed 16-bit sensor sample and converts it to degrees/second.</summary>
    public static float ReadGyroDegreesPerSecond(ReadOnlySpan<byte> report, int offset, float lsbPerDegreePerSecond)
    {
        if (offset < 0 || offset + 2 > report.Length || lsbPerDegreePerSecond <= 0f)
        {
            return 0f;
        }

        short raw = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(offset, 2));
        return raw / lsbPerDegreePerSecond;
    }

    /// <summary>Decodes a DualShock 4 / DualSense 4-byte touch point and normalises it to 0..1.</summary>
    public static void ApplyTouchPoint(ControllerState state, ReadOnlySpan<byte> point, float width, float height)
    {
        if (point.Length < 4 || width <= 0f || height <= 0f)
        {
            return;
        }

        // Bit 7 of the contact byte is set when the finger is NOT on the pad.
        if ((point[0] & 0x80) != 0)
        {
            return;
        }

        int x = point[1] | ((point[2] & 0x0F) << 8);
        int y = (point[2] >> 4) | (point[3] << 4);

        state.TouchActive = true;
        state.TouchX = Math.Clamp(x / width, 0f, 1f);
        state.TouchY = Math.Clamp(y / height, 0f, 1f);
    }

    /// <summary>Applies a standard 8-direction HID hat value (0 = up, clockwise) to the D-pad.</summary>
    public static void ApplyHat(ControllerState state, int hat)
    {
        switch (hat)
        {
            case 0:
                state.Buttons[ButtonIds.DpadUp] = true;
                break;
            case 1:
                state.Buttons[ButtonIds.DpadUp] = true;
                state.Buttons[ButtonIds.DpadRight] = true;
                break;
            case 2:
                state.Buttons[ButtonIds.DpadRight] = true;
                break;
            case 3:
                state.Buttons[ButtonIds.DpadRight] = true;
                state.Buttons[ButtonIds.DpadDown] = true;
                break;
            case 4:
                state.Buttons[ButtonIds.DpadDown] = true;
                break;
            case 5:
                state.Buttons[ButtonIds.DpadDown] = true;
                state.Buttons[ButtonIds.DpadLeft] = true;
                break;
            case 6:
                state.Buttons[ButtonIds.DpadLeft] = true;
                break;
            case 7:
                state.Buttons[ButtonIds.DpadLeft] = true;
                state.Buttons[ButtonIds.DpadUp] = true;
                break;
            default:
                break;
        }
    }
}
