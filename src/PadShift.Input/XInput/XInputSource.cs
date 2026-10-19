using System.Runtime.InteropServices;
using PadShift.Core;

namespace PadShift.Input.XInput;

/// <summary>
/// XInput controller source for user slots 0..3. Tries <c>xinput1_4.dll</c> first and falls back
/// to <c>xinput1_3.dll</c> and then <c>xinput9_1_0.dll</c> at runtime, so no redistribution or
/// driver installation is required.
/// </summary>
/// <remarks>
/// Sticks are reported deadzone-free and raw (no radial/square deadzone is applied). Triggers use
/// the XInput 16-bit convention (<c>value * 257</c>, 0..65535); the contract's nominal
/// -32768..32767 range describes the signed stick axes.
/// <see cref="TryPoll"/> is not thread-safe and must be called from a single thread.
/// </remarks>
public sealed class XInputSource : IInputSource
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;

    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;
    private const ushort Start = 0x0010;
    private const ushort Back = 0x0020;
    private const ushort LeftThumb = 0x0040;
    private const ushort RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort Guide = 0x0400;
    private const ushort ButtonA = 0x1000;
    private const ushort ButtonB = 0x2000;
    private const ushort ButtonX = 0x4000;
    private const ushort ButtonY = 0x8000;

    private static readonly object LibraryGate = new();

    private static XInputLibrary _library;
    private static bool _libraryDetected;

    private readonly int _userIndex;

    private uint _lastPacketNumber;
    private bool _hasPacketNumber;
    private bool _connected;
    private bool _disposed;

    public XInputSource(int userIndex)
    {
        if (userIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex), "XInput user index must be 0..3.");
        }

        _userIndex = userIndex;
        DeviceId = $"XInput#{userIndex}";
        _connected = IsSlotConnected(userIndex);
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public bool IsConnected => !_disposed && _connected;

    /// <summary>Returns true when a controller currently answers on the given slot (0..3).</summary>
    public static bool IsSlotConnected(int userIndex)
    {
        if (userIndex is < 0 or > 3 || !TryGetLibrary(out XInputLibrary library))
        {
            return false;
        }

        try
        {
            return GetState(library, userIndex, out _) == ErrorSuccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Snapshot of the slots that currently answer, in ascending order. Used to tag the XInput slot
    /// a freshly created virtual pad occupies so the input side can ignore it.
    /// </summary>
    public static int[] ConnectedSlots()
    {
        var slots = new List<int>(4);
        for (int slot = 0; slot < 4; slot++)
        {
            if (IsSlotConnected(slot))
            {
                slots.Add(slot);
            }
        }

        return slots.ToArray();
    }

    /// <inheritdoc />
    public bool TryPoll(ControllerState state)
    {
        if (_disposed || !TryGetLibrary(out XInputLibrary library))
        {
            _connected = false;
            return false;
        }

        uint result;
        XInputState native;

        try
        {
            result = GetState(library, _userIndex, out native);
        }
        catch
        {
            _connected = false;
            return false;
        }

        if (result != ErrorSuccess)
        {
            // ERROR_DEVICE_NOT_CONNECTED is the normal "unplugged" result; anything else is
            // also treated as a dead device so the watcher can remove this source.
            _connected = false;
            return false;
        }

        if (!_connected)
        {
            // Reconnected: a reset packet counter must not suppress the first sample.
            _connected = true;
            _hasPacketNumber = false;
        }

        if (_hasPacketNumber && native.PacketNumber == _lastPacketNumber)
        {
            return false;
        }

        _hasPacketNumber = true;
        _lastPacketNumber = native.PacketNumber;

        state.Clear();

        state.Axes[Axes.LeftStickX] = native.Gamepad.ThumbLX;
        state.Axes[Axes.LeftStickY] = native.Gamepad.ThumbLY;
        state.Axes[Axes.RightStickX] = native.Gamepad.ThumbRX;
        state.Axes[Axes.RightStickY] = native.Gamepad.ThumbRY;
        state.Axes[Axes.LeftTrigger] = native.Gamepad.LeftTrigger * 257;
        state.Axes[Axes.RightTrigger] = native.Gamepad.RightTrigger * 257;

        ushort buttons = native.Gamepad.Buttons;

        state.Buttons[ButtonIds.A] = (buttons & ButtonA) != 0;
        state.Buttons[ButtonIds.B] = (buttons & ButtonB) != 0;
        state.Buttons[ButtonIds.X] = (buttons & ButtonX) != 0;
        state.Buttons[ButtonIds.Y] = (buttons & ButtonY) != 0;
        state.Buttons[ButtonIds.LeftBumper] = (buttons & LeftShoulder) != 0;
        state.Buttons[ButtonIds.RightBumper] = (buttons & RightShoulder) != 0;
        state.Buttons[ButtonIds.Back] = (buttons & Back) != 0;
        state.Buttons[ButtonIds.Start] = (buttons & Start) != 0;
        state.Buttons[ButtonIds.Guide] = (buttons & Guide) != 0;
        state.Buttons[ButtonIds.LeftStickClick] = (buttons & LeftThumb) != 0;
        state.Buttons[ButtonIds.RightStickClick] = (buttons & RightThumb) != 0;
        state.Buttons[ButtonIds.DpadUp] = (buttons & DpadUp) != 0;
        state.Buttons[ButtonIds.DpadDown] = (buttons & DpadDown) != 0;
        state.Buttons[ButtonIds.DpadLeft] = (buttons & DpadLeft) != 0;
        state.Buttons[ButtonIds.DpadRight] = (buttons & DpadRight) != 0;

        state.TimestampTicks = DateTime.UtcNow.Ticks;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connected = false;
    }

    private static bool TryGetLibrary(out XInputLibrary library)
    {
        if (_libraryDetected)
        {
            library = _library;
            return library != XInputLibrary.None;
        }

        lock (LibraryGate)
        {
            if (!_libraryDetected)
            {
                _library = DetectLibrary();
                _libraryDetected = true;
            }

            library = _library;
            return library != XInputLibrary.None;
        }
    }

    private static XInputLibrary DetectLibrary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return XInputLibrary.None;
        }

        if (CanLoad(XInputLibrary.Version14))
        {
            return XInputLibrary.Version14;
        }

        if (CanLoad(XInputLibrary.Version13))
        {
            return XInputLibrary.Version13;
        }

        return CanLoad(XInputLibrary.Version910) ? XInputLibrary.Version910 : XInputLibrary.None;
    }

    private static bool CanLoad(XInputLibrary library)
    {
        try
        {
            uint result = GetState(library, 0, out _);
            // 0 = a pad is present, 1167 = the DLL loaded but slot 0 is empty. Both prove the
            // module and entry point are usable.
            return result is ErrorSuccess or ErrorDeviceNotConnected;
        }
        catch
        {
            return false;
        }
    }

    private static uint GetState(XInputLibrary library, int userIndex, out XInputState state)
    {
        switch (library)
        {
            case XInputLibrary.Version14:
                return XInputGetState14(userIndex, out state);

            case XInputLibrary.Version13:
                return XInputGetState13(userIndex, out state);

            case XInputLibrary.Version910:
                return XInputGetState910(userIndex, out state);

            default:
                state = default;
                return ErrorDeviceNotConnected;
        }
    }

#pragma warning disable SYSLIB1054 // Runtime DLL fallback needs DllImport; LibraryImport binds one module.
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState14(int userIndex, out XInputState state);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState13(int userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState910(int userIndex, out XInputState state);
#pragma warning restore SYSLIB1054

    private enum XInputLibrary
    {
        None = 0,
        Version14 = 1,
        Version13 = 2,
        Version910 = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
