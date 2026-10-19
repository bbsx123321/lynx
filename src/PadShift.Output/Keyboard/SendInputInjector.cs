using System.Runtime.InteropServices;

namespace PadShift.Output.Keyboard;

/// <summary>
/// Keyboard/mouse backend built on Win32 <c>SendInput</c>. Works without a driver or
/// administrator rights, which is why it is the default backend.
/// </summary>
/// <remarks>
/// <para>
/// Keyboard events use the scan-code path (<c>KEYEVENTF_SCANCODE</c>), never virtual-key codes.
/// That is what DirectInput/Raw Input games expect and it keeps the output independent of the
/// active keyboard layout. Keys marked with <see cref="ScanCode.ExtendedFlag"/> also set
/// <c>KEYEVENTF_EXTENDEDKEY</c>, which is how Windows distinguishes E0-prefixed keys from their
/// numpad twins.
/// </para>
/// <para>
/// Transitions collected during one <see cref="LayoutInjector.Submit"/> call are sent in a single
/// <c>SendInput</c> batch, which keeps a key-down and the mouse movement of the same frame as
/// close together as the OS allows. Mouse movement is relative and sent exactly as given by
/// <see cref="PadShift.Core.OutputIntent.MouseDeltaX"/>/<c>MouseDeltaY</c>; wheel deltas are in
/// <c>WHEEL_DELTA</c> units (120 per notch).
/// </para>
/// <para>
/// <c>SendInput</c> is silently blocked when the foreground window runs at a higher integrity
/// level (UIPI), for example an elevated game. The sink counts that as a failed batch, it does not
/// throw. Running PadShift at the same integrity level as the target game fixes it.
/// </para>
/// </remarks>
public sealed class SendInputInjector : LayoutInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;

    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    private readonly List<Input> _batch = new(32);

    /// <inheritdoc />
    public override string BackendName => "SendInput";

    /// <summary>Number of batches <c>SendInput</c> rejected or partially inserted (UIPI, session loss, ...).</summary>
    public long FailedBatches { get; private set; }

    /// <summary>Win32 error of the most recent failed batch (0 when the last batch succeeded).</summary>
    public int LastWin32Error { get; private set; }

    /// <inheritdoc />
    protected override void OnKey(int scanCode, bool down)
    {
        uint flags = KeyEventScanCode;
        if (ScanCode.IsExtended(scanCode))
        {
            flags |= KeyEventExtendedKey;
        }

        if (!down)
        {
            flags |= KeyEventKeyUp;
        }

        _batch.Add(new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    Scan = (ushort)ScanCode.BaseCode(scanCode),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        });
    }

    /// <inheritdoc />
    protected override void OnMouseButton(int buttonMask, bool down)
    {
        uint flags;
        uint data = 0;

        switch (buttonMask)
        {
            case ScanCode.Mouse.Left:
                flags = down ? MouseEventLeftDown : MouseEventLeftUp;
                break;
            case ScanCode.Mouse.Right:
                flags = down ? MouseEventRightDown : MouseEventRightUp;
                break;
            case ScanCode.Mouse.Middle:
                flags = down ? MouseEventMiddleDown : MouseEventMiddleUp;
                break;
            case ScanCode.Mouse.X1:
                flags = down ? MouseEventXDown : MouseEventXUp;
                data = XButton1;
                break;
            case ScanCode.Mouse.X2:
                flags = down ? MouseEventXDown : MouseEventXUp;
                data = XButton2;
                break;
            default:
                return;
        }

        _batch.Add(MouseEvent(0, 0, data, flags));
    }

    /// <inheritdoc />
    protected override void OnMouseMove(int deltaX, int deltaY)
    {
        _batch.Add(MouseEvent(deltaX, deltaY, 0, MouseEventMove));
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(int delta)
    {
        _batch.Add(MouseEvent(0, 0, unchecked((uint)delta), MouseEventWheel));
    }

    /// <inheritdoc />
    protected override void Flush()
    {
        if (_batch.Count == 0)
        {
            return;
        }

        Span<Input> inputs = CollectionsMarshal.AsSpan(_batch);
        uint sent = SendInput((uint)inputs.Length, ref inputs[0], InputSize);
        if (sent != (uint)inputs.Length)
        {
            LastWin32Error = Marshal.GetLastWin32Error();
            FailedBatches++;
        }
        else
        {
            LastWin32Error = 0;
        }

        _batch.Clear();
    }

    private static Input MouseEvent(int deltaX, int deltaY, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                DeltaX = deltaX,
                DeltaY = deltaY,
                MouseData = data,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref Input pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
