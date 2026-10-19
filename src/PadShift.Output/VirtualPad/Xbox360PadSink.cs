using System.Diagnostics.CodeAnalysis;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;
using PadShift.Core;

namespace PadShift.Output.VirtualPad;

/// <summary>
/// Emulates a wired Xbox 360 controller through ViGEmBus (XInput device).
/// </summary>
/// <remarks>
/// <para>
/// Buttons, both sticks and both analog triggers are mapped from <see cref="OutputIntent"/>. The
/// panel is diffed against the last applied state and exactly one report is submitted when
/// anything changed (<c>AutoSubmitReport</c> is disabled so individual setters do not emit reports).
/// </para>
/// <para>
/// Guide maps to the Xbox button. The four back paddles and the touchpad click have no Xbox 360
/// equivalent and are ignored. The DS4-style colour aliases (Green/Red/Blue/Yellow) are OR-ed with
/// A/B/X/Y respectively.
/// </para>
/// <para>
/// Rumble written by the game is surfaced through <see cref="VirtualPadSink.RumbleReceived"/> and,
/// when composed through <see cref="PadShift.Output.CompositeSink"/>, through
/// <see cref="PadShift.Output.CompositeSink.Rumble"/>.
/// </para>
/// </remarks>
public sealed class Xbox360PadSink : ViGEmPadSink
{
    private static readonly (Xbox360Button Button, int[] Sources)[] ButtonMap =
    [
        (Xbox360Button.A, new[] { ButtonIds.A, ButtonIds.Green }),
        (Xbox360Button.B, new[] { ButtonIds.B, ButtonIds.Red }),
        (Xbox360Button.X, new[] { ButtonIds.X, ButtonIds.Blue }),
        (Xbox360Button.Y, new[] { ButtonIds.Y, ButtonIds.Yellow }),
        (Xbox360Button.LeftShoulder, new[] { ButtonIds.LeftBumper }),
        (Xbox360Button.RightShoulder, new[] { ButtonIds.RightBumper }),
        (Xbox360Button.Back, new[] { ButtonIds.Back }),
        (Xbox360Button.Start, new[] { ButtonIds.Start }),
        (Xbox360Button.Guide, new[] { ButtonIds.Guide }),
        (Xbox360Button.LeftThumb, new[] { ButtonIds.LeftStickClick }),
        (Xbox360Button.RightThumb, new[] { ButtonIds.RightStickClick }),
        (Xbox360Button.Up, new[] { ButtonIds.DpadUp }),
        (Xbox360Button.Down, new[] { ButtonIds.DpadDown }),
        (Xbox360Button.Left, new[] { ButtonIds.DpadLeft }),
        (Xbox360Button.Right, new[] { ButtonIds.DpadRight }),
    ];

    private readonly IXbox360Controller _controller;
    private readonly IVirtualGamepad _gamepad;
    private readonly Xbox360FeedbackReceivedEventHandler _feedbackHandler;
    private readonly bool[] _appliedButtons = new bool[ButtonMap.Length];

    private int _appliedLeftX;
    private int _appliedLeftY;
    private int _appliedRightX;
    private int _appliedRightY;
    private byte _appliedLeftTrigger;
    private byte _appliedRightTrigger;

    /// <summary>
    /// Creates and plugs in an Xbox 360 pad. Throws <see cref="ViGEmNotInstalledException"/> once
    /// when ViGEmBus is missing; after that the instance never throws.
    /// </summary>
    /// <param name="sharedClient">Existing ViGEm client to reuse, or null to own a private one.</param>
    public Xbox360PadSink(ViGEmClient? sharedClient = null)
        : base(sharedClient)
    {
        _controller = Client.CreateXbox360Controller();
        _gamepad = (IVirtualGamepad)_controller;
        _gamepad.AutoSubmitReport = false;

        _feedbackHandler = (_, e) => ReportRumble(e.LargeMotor, e.SmallMotor);
        _controller.FeedbackReceived += _feedbackHandler;

        try
        {
            ConnectTarget(_gamepad);
            _gamepad.ResetReport();
            _gamepad.SubmitReport();
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
    public override string BackendName => "ViGEm Xbox 360";

    /// <summary>
    /// XInput user slot this pad currently occupies (0..3), or <see langword="null"/> while ViGEmBus
    /// has not reported one yet. The input side uses this to ignore the pad it created itself, which
    /// otherwise appears as a brand-new XInput controller and feeds the remapped output back in.
    /// </summary>
    public int? XInputUserIndex
    {
        get
        {
            try
            {
                int index = _controller.UserIndex;
                return index is >= 0 and <= 3 ? index : null;
            }
            catch (Xbox360UserIndexNotReportedException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Creates a sink without throwing; false means ViGEmBus/pad setup failed.</summary>
    public static bool TryCreate([NotNullWhen(true)] out Xbox360PadSink? sink, ViGEmClient? sharedClient = null)
    {
        try
        {
            sink = new Xbox360PadSink(sharedClient);
            return true;
        }
        catch (VirtualPadException)
        {
            sink = null;
            return false;
        }
    }

    /// <inheritdoc />
    protected override void SubmitCore(OutputIntent intent)
    {
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

        if (intent.PadLeftX != _appliedLeftX)
        {
            _appliedLeftX = intent.PadLeftX;
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis16(intent.PadLeftX));
            changed = true;
        }

        if (intent.PadLeftY != _appliedLeftY)
        {
            _appliedLeftY = intent.PadLeftY;
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis16(intent.PadLeftY));
            changed = true;
        }

        if (intent.PadRightX != _appliedRightX)
        {
            _appliedRightX = intent.PadRightX;
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis16(intent.PadRightX));
            changed = true;
        }

        if (intent.PadRightY != _appliedRightY)
        {
            _appliedRightY = intent.PadRightY;
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis16(intent.PadRightY));
            changed = true;
        }

        if (intent.PadLeftTrigger != _appliedLeftTrigger)
        {
            _appliedLeftTrigger = intent.PadLeftTrigger;
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, intent.PadLeftTrigger);
            changed = true;
        }

        if (intent.PadRightTrigger != _appliedRightTrigger)
        {
            _appliedRightTrigger = intent.PadRightTrigger;
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, intent.PadRightTrigger);
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

    private static short ToAxis16(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
