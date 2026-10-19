using PadShift.Core;

namespace PadShift.Input;

/// <summary>
/// Copies every field of a <see cref="ControllerState"/> snapshot.
/// Used so a failed or incomplete report never clobbers the caller's last good state.
/// </summary>
internal static class ControllerStateCopy
{
    public static void Copy(ControllerState source, ControllerState destination)
    {
        Array.Copy(source.Buttons, destination.Buttons, source.Buttons.Length);
        Array.Copy(source.Axes, destination.Axes, source.Axes.Length);
        destination.GyroX = source.GyroX;
        destination.GyroY = source.GyroY;
        destination.GyroZ = source.GyroZ;
        destination.TouchActive = source.TouchActive;
        destination.TouchX = source.TouchX;
        destination.TouchY = source.TouchY;
        destination.TimestampTicks = source.TimestampTicks;
    }
}
