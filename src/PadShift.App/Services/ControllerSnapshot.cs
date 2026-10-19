namespace PadShift.App.Services;

/// <summary>Right-stick state used by the radial menu (normalized -1..1).</summary>
public readonly record struct RadialStickState(bool HasState, double X, double Y)
{
    public static RadialStickState None => new(false, 0, 0);
}

/// <summary>
/// Small immutable copy of the most recent controller poll. <see cref="DeviceSession"/> refreshes it
/// from its input sources, so UI features (radial menu, gyro wizard) can read the pad without
/// polling the device a second time.
/// </summary>
public readonly record struct ControllerSnapshot(
    bool HasState,
    ulong Buttons,
    int RightStickX,
    int RightStickY,
    double GyroX,
    double GyroY,
    double GyroZ,
    DateTimeOffset CapturedAt)
{
    public static ControllerSnapshot None { get; } = new(false, 0, 0, 0, 0, 0, 0, default);

    public bool IsButtonDown(int buttonId) =>
        HasState && buttonId is >= 0 and < 64 && (Buttons & (1UL << buttonId)) != 0;

    /// <summary>All <paramref name="buttonIds"/> held at the same time.</summary>
    public bool AreButtonsDown(IReadOnlyList<int>? buttonIds)
    {
        if (!HasState || buttonIds is null || buttonIds.Count == 0)
        {
            return false;
        }

        foreach (int buttonId in buttonIds)
        {
            if (!IsButtonDown(buttonId))
            {
                return false;
            }
        }

        return true;
    }

    public RadialStickState RightStick =>
        HasState
            ? new RadialStickState(true, RightStickX / 32768.0, RightStickY / 32768.0)
            : RadialStickState.None;
}
