using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace PadShift.Output.Keyboard;

/// <summary>
/// Optional fast-path keyboard/mouse backend that injects through the Interception kernel filter
/// driver (<c>interception.dll</c>).
/// </summary>
/// <remarks>
/// <para>
/// The library is never bundled. Drop <c>Interception.dll</c> next to <c>Lynx.exe</c> (and
/// install the driver with <c>install-interception.exe /install</c> from an elevated prompt, then
/// reboot). <see cref="TryCreate"/> probes for the DLL and for a usable driver context; when
/// either is missing it returns false and <see cref="LayoutInjector.CreateDefault"/> stays on
/// <see cref="SendInputInjector"/>.
/// </para>
/// <para>
/// The native API is bound manually with <see cref="NativeLibrary"/> on purpose: the DLL can live
/// only next to the executable, and the probe must be able to fail without leaving a partially
/// initialised context behind.
/// </para>
/// <para>
/// By default the context does <b>not</b> install receive filters. An inject-only context must not
/// swallow physical input: the driver queues matching strokes for a context and stops forwarding
/// them until the context pumps them. Only call <see cref="EnableCaptureFilters"/> when this
/// process also runs an <c>interception_wait</c>/<c>interception_receive</c> loop for those
/// devices. The filter masks are exposed as <see cref="KeyboardFilterAll"/> and
/// <see cref="MouseFilterAll"/> for such a capture loop.
/// </para>
/// </remarks>
public sealed class InterceptionInjector : LayoutInjector
{
    /// <summary>Expected native library file name, next to the executable.</summary>
    public const string LibraryFileName = "Interception.dll";

    /// <summary>Keyboard slots in Interception are 1..10; mouse slots are 11..20.</summary>
    public const int KeyboardDevice = 1;

    /// <summary>First mouse device slot (see <see cref="KeyboardDevice"/>).</summary>
    public const int MouseDevice = 11;

    /// <summary>KEY_DOWN | KEY_UP | KEY_E0 | KEY_E1.</summary>
    public const ushort KeyboardFilterAll = 0x000F;

    /// <summary>All mouse filter bits, including move and wheel.</summary>
    public const ushort MouseFilterAll = 0x3FFF;

    private const ushort KeyStateDown = 0x0000;
    private const ushort KeyStateUp = 0x0001;
    private const ushort KeyStateE0 = 0x0002;

    private const ushort MouseStateLeftDown = 0x0001;
    private const ushort MouseStateLeftUp = 0x0002;
    private const ushort MouseStateRightDown = 0x0004;
    private const ushort MouseStateRightUp = 0x0008;
    private const ushort MouseStateMiddleDown = 0x0010;
    private const ushort MouseStateMiddleUp = 0x0020;
    private const ushort MouseStateFourthDown = 0x0040;
    private const ushort MouseStateFourthUp = 0x0080;
    private const ushort MouseStateFifthDown = 0x0100;
    private const ushort MouseStateFifthUp = 0x0200;
    private const ushort MouseStateWheel = 0x0400;
    private const ushort MouseStateMove = 0x1000;

    private readonly NativeApi _api;
    private readonly IntPtr _library;
    private readonly IntPtr _context;

    private InterceptionInjector(NativeApi api, IntPtr library, IntPtr context)
    {
        _api = api;
        _library = library;
        _context = context;
    }

    /// <inheritdoc />
    public override string BackendName => "Interception";

    /// <summary>true when <c>Interception.dll</c> sits next to the executable.</summary>
    public static bool IsLibraryPresent =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, LibraryFileName));

    /// <summary>Number of strokes the driver refused.</summary>
    public long FailedSends { get; private set; }

    /// <summary>true after <see cref="EnableCaptureFilters"/> was called.</summary>
    public bool CaptureFiltersEnabled { get; private set; }

    /// <summary>
    /// Probes for <c>Interception.dll</c> next to the executable and opens a driver context.
    /// Returns false (without throwing) when the DLL or the driver is missing.
    /// </summary>
    public static bool TryCreate([NotNullWhen(true)] out InterceptionInjector? injector)
    {
        injector = null;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path = Path.Combine(AppContext.BaseDirectory, LibraryFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        IntPtr library = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        NativeApi? api = null;

        try
        {
            if (!NativeLibrary.TryLoad(path, out library))
            {
                return false;
            }

            api = NativeApi.TryBind(library);
            if (api is null)
            {
                return false;
            }

            context = api.CreateContext();
            if (context == IntPtr.Zero)
            {
                return false;
            }

            injector = new InterceptionInjector(api, library, context);
            return true;
        }
        catch
        {
            // A corrupt/mismatched DLL or a driver that vanished mid-probe means "not available".
            return false;
        }
        finally
        {
            if (injector is null)
            {
                if (context != IntPtr.Zero && api is not null)
                {
                    try
                    {
                        api.DestroyContext(context);
                    }
                    catch
                    {
                        // Best effort during a failed probe.
                    }
                }

                if (library != IntPtr.Zero)
                {
                    NativeLibrary.Free(library);
                }
            }
        }
    }

    /// <summary>
    /// Installs keyboard and mouse receive filters on this context. Only use this together with a
    /// receive loop; see the class remarks. An inject-only context must leave filters at zero.
    /// </summary>
    public void EnableCaptureFilters()
    {
        lock (SyncRoot)
        {
            if (IsDisposed || CaptureFiltersEnabled)
            {
                return;
            }

            _api.SetFilter(_context, _api.IsKeyboard, KeyboardFilterAll);
            _api.SetFilter(_context, _api.IsMouse, MouseFilterAll);
            CaptureFiltersEnabled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKey(int scanCode, bool down)
    {
        InterceptionStroke stroke = default;
        stroke.Key.Code = (ushort)ScanCode.BaseCode(scanCode);
        stroke.Key.State = down ? KeyStateDown : KeyStateUp;
        if (ScanCode.IsExtended(scanCode))
        {
            stroke.Key.State |= KeyStateE0;
        }

        Send(ref stroke, KeyboardDevice);
    }

    /// <inheritdoc />
    protected override void OnMouseButton(int buttonMask, bool down)
    {
        ushort state;
        switch (buttonMask)
        {
            case ScanCode.Mouse.Left:
                state = down ? MouseStateLeftDown : MouseStateLeftUp;
                break;
            case ScanCode.Mouse.Right:
                state = down ? MouseStateRightDown : MouseStateRightUp;
                break;
            case ScanCode.Mouse.Middle:
                state = down ? MouseStateMiddleDown : MouseStateMiddleUp;
                break;
            case ScanCode.Mouse.X1:
                state = down ? MouseStateFourthDown : MouseStateFourthUp;
                break;
            case ScanCode.Mouse.X2:
                state = down ? MouseStateFifthDown : MouseStateFifthUp;
                break;
            default:
                return;
        }

        InterceptionStroke stroke = default;
        stroke.Mouse.State = state;
        Send(ref stroke, MouseDevice);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(int deltaX, int deltaY)
    {
        InterceptionStroke stroke = default;
        stroke.Mouse.State = MouseStateMove;
        stroke.Mouse.Flags = 0; // INTERCEPTION_MOUSE_MOVE_RELATIVE
        stroke.Mouse.X = deltaX;
        stroke.Mouse.Y = deltaY;
        Send(ref stroke, MouseDevice);
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(int delta)
    {
        InterceptionStroke stroke = default;
        stroke.Mouse.State = MouseStateWheel;
        stroke.Mouse.Rolling = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
        Send(ref stroke, MouseDevice);
    }

    /// <inheritdoc />
    protected override void Flush()
    {
        // Interception has no batching primitive that spans keyboard and mouse devices, so every
        // transition is already on its way by the time Flush is called.
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        _api.DestroyContext(_context);
        NativeLibrary.Free(_library);
    }

    private void Send(ref InterceptionStroke stroke, int device)
    {
        if (_api.Send(_context, device, ref stroke, 1) != 1)
        {
            FailedSends++;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InterceptionStroke
    {
        [FieldOffset(0)]
        public KeyStroke Key;

        [FieldOffset(0)]
        public MouseStroke Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyStroke
    {
        public ushort Code;
        public ushort State;
        public uint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseStroke
    {
        public ushort State;
        public ushort Flags;
        public short Rolling;
        public int X;
        public int Y;
        public uint Information;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateContextDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyContextDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SendDelegate(IntPtr context, int device, ref InterceptionStroke stroke, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFilterDelegate(IntPtr context, IntPtr predicate, ushort filter);

    private sealed class NativeApi
    {
        private NativeApi(
            CreateContextDelegate createContext,
            DestroyContextDelegate destroyContext,
            SendDelegate send,
            SetFilterDelegate setFilter,
            IntPtr isKeyboard,
            IntPtr isMouse)
        {
            CreateContext = createContext;
            DestroyContext = destroyContext;
            Send = send;
            SetFilter = setFilter;
            IsKeyboard = isKeyboard;
            IsMouse = isMouse;
        }

        public CreateContextDelegate CreateContext { get; }

        public DestroyContextDelegate DestroyContext { get; }

        public SendDelegate Send { get; }

        public SetFilterDelegate SetFilter { get; }

        public IntPtr IsKeyboard { get; }

        public IntPtr IsMouse { get; }

        public static NativeApi? TryBind(IntPtr library)
        {
            CreateContextDelegate? createContext = Bind<CreateContextDelegate>(library, "interception_create_context");
            DestroyContextDelegate? destroyContext = Bind<DestroyContextDelegate>(library, "interception_destroy_context");
            SendDelegate? send = Bind<SendDelegate>(library, "interception_send");
            SetFilterDelegate? setFilter = Bind<SetFilterDelegate>(library, "interception_set_filter");

            if (createContext is null ||
                destroyContext is null ||
                send is null ||
                setFilter is null ||
                !NativeLibrary.TryGetExport(library, "interception_is_keyboard", out IntPtr isKeyboard) ||
                !NativeLibrary.TryGetExport(library, "interception_is_mouse", out IntPtr isMouse))
            {
                return null;
            }

            return new NativeApi(createContext, destroyContext, send, setFilter, isKeyboard, isMouse);
        }

        private static T? Bind<T>(IntPtr library, string name)
            where T : Delegate
        {
            return NativeLibrary.TryGetExport(library, name, out IntPtr address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;
        }
    }
}
