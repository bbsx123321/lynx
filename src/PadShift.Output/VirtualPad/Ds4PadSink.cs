using System.Diagnostics.CodeAnalysis;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using PadShift.Core;

// ViGEm marks the DualShock 4 FeedbackReceived event obsolete in favour of a blocking
// AwaitRawOutputReport() loop. The event is still the only callback-based rumble notification, so
// it is used deliberately; the blocking loop would need a dedicated thread per pad.
#pragma warning disable CS0618

namespace PadShift.Output.VirtualPad;

/// <summary>
/// Emulates a wired DualShock 4 through ViGEmBus.
/// </summary>
/// <remarks>
/// <para>
/// Buttons, both sticks, both analog triggers and the D-pad are mapped from
/// <see cref="OutputIntent"/>. As with the Xbox sink the state is diffed and exactly one report is
/// submitted per changed frame.
/// </para>
/// <para>
/// <b>Touchpad passthrough</b> is opt-in: pass <c>enableTouchpadPassthrough: true</c> and feed
/// positions through <see cref="SetTouch"/>. That mode emits raw 63-byte DualShock 4 input reports
/// (the ViGEm <c>DS4_REPORT_EX</c> layout, report 0x01 without the leading report-id byte) so the
/// touch packets at offsets 32 and 36 can carry real coordinates. Without it, reports go through
/// the typed ViGEm setters.
/// </para>
/// <para>
/// L2/R2 are reported both as analog sliders and as their digital button bits, matching what a real
/// pad does. Rumble written by the game is surfaced through
/// <see cref="VirtualPadSink.RumbleReceived"/>.
/// </para>
/// </remarks>
public sealed class Ds4PadSink : ViGEmPadSink
{
    // DS4_REPORT_EX is 63 bytes: the 64-byte USB report 0x01 without its report-id byte.
    private const int RawReportLength = 63;
    private const int Touch1Offset = 32;
    private const int Touch2Offset = 36;
    private const int TouchActiveMask = 0x80; // set = no finger, cleared = finger down
    private const int TouchpadWidth = 1920;
    private const int TouchpadHeight = 943;

    private static readonly (DualShock4Button Button, int[] Sources)[] ButtonMap =
    [
        (DualShock4Button.Cross, new[] { ButtonIds.A, ButtonIds.Green }),
        (DualShock4Button.Circle, new[] { ButtonIds.B, ButtonIds.Red }),
        (DualShock4Button.Square, new[] { ButtonIds.X, ButtonIds.Blue }),
        (DualShock4Button.Triangle, new[] { ButtonIds.Y, ButtonIds.Yellow }),
        (DualShock4Button.ShoulderLeft, new[] { ButtonIds.LeftBumper }),
        (DualShock4Button.ShoulderRight, new[] { ButtonIds.RightBumper }),
        (DualShock4Button.Share, new[] { ButtonIds.Back }),
        (DualShock4Button.Options, new[] { ButtonIds.Start }),
        (DualShock4Button.ThumbLeft, new[] { ButtonIds.LeftStickClick }),
        (DualShock4Button.ThumbRight, new[] { ButtonIds.RightStickClick }),
        (DualShock4SpecialButton.Ps, new[] { ButtonIds.Guide }),
        (DualShock4SpecialButton.Touchpad, new[] { ButtonIds.TouchClick }),
    ];

    private readonly IDualShock4Controller _controller;
    private readonly IVirtualGamepad _gamepad;
    private readonly DualShock4FeedbackReceivedEventHandler _feedbackHandler;
    private readonly bool[] _appliedButtons = new bool[ButtonMap.Length];
    private readonly bool _touchpadPassthrough;
    private readonly byte[] _rawReport = new byte[RawReportLength];
    private readonly byte[] _lastRawReport = new byte[RawReportLength];

    private int _appliedLeftX;
    private int _appliedLeftY;
    private int _appliedRightX;
    private int _appliedRightY;
    private byte _appliedLeftTrigger;
    private byte _appliedRightTrigger;
    private DualShock4DPadDirection _appliedDpad = DualShock4DPadDirection.None;

    private bool _touchActive;
    private float _touchX;
    private float _touchY;

    /// <summary>
    /// Creates and plugs in a DualShock 4 pad. Throws <see cref="ViGEmNotInstalledException"/> once
    /// when ViGEmBus is missing; after that the instance never throws.
    /// </summary>
    /// <param name="sharedClient">Existing ViGEm client to reuse, or null to own a private one.</param>
    /// <param name="enableTouchpadPassthrough">Emit raw reports so <see cref="SetTouch"/> reaches games.</param>
    public Ds4PadSink(ViGEmClient? sharedClient = null, bool enableTouchpadPassthrough = false)
        : base(sharedClient)
    {
        _touchpadPassthrough = enableTouchpadPassthrough;
        _controller = Client.CreateDualShock4Controller();
        _gamepad = (IVirtualGamepad)_controller;
        _gamepad.AutoSubmitReport = false;

        _feedbackHandler = (_, e) => ReportRumble(e.LargeMotor, e.SmallMotor);
        _controller.FeedbackReceived += _feedbackHandler;

        try
        {
            ConnectTarget(_gamepad);

            if (_touchpadPassthrough)
            {
                SubmitRaw(new OutputIntent());
            }
            else
            {
                ApplyNeutral();
            }
        }
        catch
        {
            _controller.FeedbackReceived -= _feedbackHandler;
            try
            {
                _gamepad.Disconnect();
            }
            catch
            {
                // The pad never made it onto the bus.
            }

            DisposeClient();
            throw;
        }

        SetConnected(true);
    }

    /// <inheritdoc />
    public override string BackendName => "ViGEm DualShock 4";

    /// <summary>true when raw reports (and therefore touch input) are enabled.</summary>
    public bool TouchpadPassthroughEnabled => _touchpadPassthrough;

    /// <summary>Creates a sink without throwing; false means ViGEmBus/pad setup failed.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] out Ds4PadSink? sink,
        bool enableTouchpadPassthrough = false,
        ViGEmClient? sharedClient = null)
    {
        try
        {
            sink = new Ds4PadSink(sharedClient, enableTouchpadPassthrough);
            return true;
        }
        catch (VirtualPadException)
        {
            sink = null;
            return false;
        }
    }

    /// <summary>
    /// Latches a touch sample for the next <see cref="VirtualPadSink.Submit"/> call. Coordinates are
    /// normalized 0..1 (origin top-left); the sink scales them to the DS4's 1920x943 touchpad.
    /// No-op unless touchpad passthrough was enabled in the constructor.
    /// </summary>
    public void SetTouch(bool active, float normalizedX, float normalizedY)
    {
        if (!_touchpadPassthrough)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return;
            }

            _touchActive = active;
            _touchX = float.IsFinite(normalizedX) ? Math.Clamp(normalizedX, 0f, 1f) : 0f;
            _touchY = float.IsFinite(normalizedY) ? Math.Clamp(normalizedY, 0f, 1f) : 0f;
        }
    }

    /// <inheritdoc />
    protected override void SubmitCore(OutputIntent intent)
    {
        if (_touchpadPassthrough)
        {
            SubmitRaw(intent);
            return;
        }

        bool changed = false;

        for (int i = 0; i < ButtonMap.Length; i++)
        {
            bool want = AnyPressed(intent.PadButtons, ButtonMap[i].Sources);
            if (want == _appliedButtons[i])
            {
                continue;
            }

            _appliedButtons[i] = want;
            _controller.SetButtonState(ButtonMap[i].Button, want);
            changed = true;
        }

        DualShock4DPadDirection dpad = ResolveDpadDirection(intent.PadButtons);
        if (!ReferenceEquals(dpad, _appliedDpad))
        {
            _appliedDpad = dpad;
            _controller.SetDPadDirection(dpad);
            changed = true;
        }

        if (intent.PadLeftX != _appliedLeftX)
        {
            _appliedLeftX = intent.PadLeftX;
            _controller.SetAxisValue(DualShock4Axis.LeftThumbX, ToAxis8(intent.PadLeftX));
            changed = true;
        }

        if (intent.PadLeftY != _appliedLeftY)
        {
            _appliedLeftY = intent.PadLeftY;
            _controller.SetAxisValue(DualShock4Axis.LeftThumbY, ToAxis8(intent.PadLeftY));
            changed = true;
        }

        if (intent.PadRightX != _appliedRightX)
        {
            _appliedRightX = intent.PadRightX;
            _controller.SetAxisValue(DualShock4Axis.RightThumbX, ToAxis8(intent.PadRightX));
            changed = true;
        }

        if (intent.PadRightY != _appliedRightY)
        {
            _appliedRightY = intent.PadRightY;
            _controller.SetAxisValue(DualShock4Axis.RightThumbY, ToAxis8(intent.PadRightY));
            changed = true;
        }

        if (intent.PadLeftTrigger != _appliedLeftTrigger)
        {
            _appliedLeftTrigger = intent.PadLeftTrigger;
            _controller.SetSliderValue(DualShock4Slider.LeftTrigger, intent.PadLeftTrigger);
            _controller.SetButtonState(DualShock4Button.TriggerLeft, intent.PadLeftTrigger > 0);
            changed = true;
        }

        if (intent.PadRightTrigger != _appliedRightTrigger)
        {
            _appliedRightTrigger = intent.PadRightTrigger;
            _controller.SetSliderValue(DualShock4Slider.RightTrigger, intent.PadRightTrigger);
            _controller.SetButtonState(DualShock4Button.TriggerRight, intent.PadRightTrigger > 0);
            changed = true;
        }

        if (changed)
        {
            _gamepad.SubmitReport();
        }
    }

    /// <inheritdoc />
    protected override void ReleaseAllCore()
    {
        SubmitCore(new OutputIntent());
    }

    /// <inheritdoc />
    protected override void DisconnectTarget()
    {
        _controller.FeedbackReceived -= _feedbackHandler;
        try
        {
            _gamepad.Disconnect();
        }
        catch
        {
            // Already removed (for example after a driver restart).
        }
    }

    private void ApplyNeutral()
    {
        _controller.SetAxisValue(DualShock4Axis.LeftThumbX, 128);
        _controller.SetAxisValue(DualShock4Axis.LeftThumbY, 128);
        _controller.SetAxisValue(DualShock4Axis.RightThumbX, 128);
        _controller.SetAxisValue(DualShock4Axis.RightThumbY, 128);
        _controller.SetSliderValue(DualShock4Slider.LeftTrigger, 0);
        _controller.SetSliderValue(DualShock4Slider.RightTrigger, 0);
        _controller.SetButtonState(DualShock4Button.TriggerLeft, false);
        _controller.SetButtonState(DualShock4Button.TriggerRight, false);
        _controller.SetDPadDirection(DualShock4DPadDirection.None);
        _gamepad.SubmitReport();
    }

    private void SubmitRaw(OutputIntent intent)
    {
        byte[] report = _rawReport;
        Array.Clear(report, 0, report.Length);

        report[0] = ToAxis8(intent.PadLeftX);
        report[1] = ToAxis8(intent.PadLeftY);
        report[2] = ToAxis8(intent.PadRightX);
        report[3] = ToAxis8(intent.PadRightY);

        ushort buttons = ResolveDpadDirection(intent.PadButtons).Value;
        byte special = 0;

        foreach ((DualShock4Button button, int[] sources) in ButtonMap)
        {
            if (!AnyPressed(intent.PadButtons, sources))
            {
                continue;
            }

            if (button is DualShock4SpecialButton)
            {
                special |= (byte)button.Value;
            }
            else
            {
                buttons |= button.Value;
            }
        }

        if (intent.PadLeftTrigger > 0)
        {
            buttons |= DualShock4Button.TriggerLeft.Value;
        }

        if (intent.PadRightTrigger > 0)
        {
            buttons |= DualShock4Button.TriggerRight.Value;
        }

        report[4] = (byte)(buttons & 0xFF);
        report[5] = (byte)(buttons >> 8);
        report[6] = special;
        report[7] = intent.PadLeftTrigger;
        report[8] = intent.PadRightTrigger;

        WriteTouch(report);

        if (report.AsSpan().SequenceEqual(_lastRawReport))
        {
            return;
        }

        _controller.SubmitRawReport(report);
        report.CopyTo(_lastRawReport, 0);
    }

    private void WriteTouch(byte[] report)
    {
        if (!_touchActive)
        {
            // 0x80 in the tracking-id byte means "no finger" for both touch packets.
            report[Touch1Offset] = TouchActiveMask;
            report[Touch2Offset] = TouchActiveMask;
            return;
        }

        int x = (int)MathF.Round(_touchX * (TouchpadWidth - 1));
        int y = (int)MathF.Round(_touchY * (TouchpadHeight - 1));

        // 12-bit packed DS4 touch packet: X low 8 bits, shared nibble, Y high 8 bits.
        report[Touch1Offset] = 0; // tracking id 0, bit 7 clear = active
        report[Touch1Offset + 1] = (byte)(x & 0xFF);
        report[Touch1Offset + 2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        report[Touch1Offset + 3] = (byte)((y >> 4) & 0xFF);
        report[Touch2Offset] = TouchActiveMask; // second finger unused
    }

    private static bool AnyPressed(bool[] buttons, int[] sources)
    {
        foreach (int source in sources)
        {
            if (buttons[source])
            {
                return true;
            }
        }

        return false;
    }

    private static DualShock4DPadDirection ResolveDpadDirection(bool[] buttons)
    {
        bool up = buttons[ButtonIds.DpadUp];
        bool down = buttons[ButtonIds.DpadDown];
        bool left = buttons[ButtonIds.DpadLeft];
        bool right = buttons[ButtonIds.DpadRight];

        if (up)
        {
            if (left)
            {
                return DualShock4DPadDirection.Northwest;
            }

            if (right)
            {
                return DualShock4DPadDirection.Northeast;
            }

            return DualShock4DPadDirection.North;
        }

        if (down)
        {
            if (left)
            {
                return DualShock4DPadDirection.Southwest;
            }

            if (right)
            {
                return DualShock4DPadDirection.Southeast;
            }

            return DualShock4DPadDirection.South;
        }

        if (left)
        {
            return DualShock4DPadDirection.West;
        }

        if (right)
        {
            return DualShock4DPadDirection.East;
        }

        return DualShock4DPadDirection.None;
    }

    private static byte ToAxis8(int value) => (byte)Math.Clamp((value >> 8) + 128, 0, 255);
}
