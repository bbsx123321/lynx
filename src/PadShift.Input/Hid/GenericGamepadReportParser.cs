using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// Best-effort parser for unknown gamepads that expose the standard HID Generic Desktop
/// Joystick/Gamepad usage page. Field offsets come from the device's report descriptor, so
/// devices using 8/12/16-bit axes and packed button bits are handled without a VID/PID entry.
/// </summary>
internal sealed class GenericGamepadReportParser : IReportParser
{
    private readonly Dictionary<byte, GenericLayout> _layouts;

    public GenericGamepadReportParser(ReportDescriptor descriptor)
    {
        _layouts = BuildLayouts(descriptor);
    }

    public string Name => "Generic HID gamepad";

    public bool TryParse(ReadOnlySpan<byte> report, ControllerState state)
    {
        if (report.Length == 0 || _layouts.Count == 0)
        {
            return false;
        }

        if (!_layouts.TryGetValue(report[0], out GenericLayout? layout)
            && !_layouts.TryGetValue(0, out layout))
        {
            return false;
        }

        int dataStart = layout.ReportId == 0 ? 0 : 1;
        int requiredBits = (dataStart * 8) + layout.MaxBitOffset;
        if (report.Length * 8 < requiredBits)
        {
            return false;
        }

        state.Clear();

        if (layout.LeftX is { } leftX)
        {
            state.Axes[Axes.LeftStickX] = ReadAxis(report, leftX, dataStart, invert: false);
        }

        if (layout.LeftY is { } leftY)
        {
            state.Axes[Axes.LeftStickY] = ReadAxis(report, leftY, dataStart, invert: true);
        }

        if (layout.RightX is { } rightX)
        {
            state.Axes[Axes.RightStickX] = ReadAxis(report, rightX, dataStart, invert: false);
        }

        if (layout.RightY is { } rightY)
        {
            state.Axes[Axes.RightStickY] = ReadAxis(report, rightY, dataStart, invert: true);
        }

        if (layout.LeftTrigger is { } leftTrigger)
        {
            state.Axes[Axes.LeftTrigger] = ReadTrigger(report, leftTrigger, dataStart);
        }

        if (layout.RightTrigger is { } rightTrigger)
        {
            state.Axes[Axes.RightTrigger] = ReadTrigger(report, rightTrigger, dataStart);
        }

        foreach (ButtonBinding button in layout.Buttons)
        {
            int value = ReadRaw(report, button.Field, dataStart);
            if (value == 0)
            {
                continue;
            }

            switch (button.Usage)
            {
                case 1:
                    state.Buttons[ButtonIds.A] = true;
                    break;
                case 2:
                    state.Buttons[ButtonIds.B] = true;
                    break;
                case 3:
                    state.Buttons[ButtonIds.X] = true;
                    break;
                case 4:
                    state.Buttons[ButtonIds.Y] = true;
                    break;
                case 5:
                    state.Buttons[ButtonIds.LeftBumper] = true;
                    break;
                case 6:
                    state.Buttons[ButtonIds.RightBumper] = true;
                    break;
                case 7:
                    if (layout.LeftTrigger is null)
                    {
                        state.Axes[Axes.LeftTrigger] = HidParserHelpers.TriggerFullScale;
                    }

                    break;
                case 8:
                    if (layout.RightTrigger is null)
                    {
                        state.Axes[Axes.RightTrigger] = HidParserHelpers.TriggerFullScale;
                    }

                    break;
                case 9:
                    state.Buttons[ButtonIds.Back] = true;
                    break;
                case 10:
                    state.Buttons[ButtonIds.Start] = true;
                    break;
                case 11:
                    state.Buttons[ButtonIds.LeftStickClick] = true;
                    break;
                case 12:
                    state.Buttons[ButtonIds.RightStickClick] = true;
                    break;
                case 13:
                    state.Buttons[ButtonIds.Guide] = true;
                    break;
                case 14:
                    state.Buttons[ButtonIds.Paddle1] = true;
                    break;
                case 15:
                    state.Buttons[ButtonIds.Paddle2] = true;
                    break;
                case 16:
                    state.Buttons[ButtonIds.Paddle3] = true;
                    break;
                case 17:
                    state.Buttons[ButtonIds.Paddle4] = true;
                    break;
                default:
                    break;
            }
        }

        if (layout.Hat is { } hat)
        {
            int value = ReadRaw(report, hat, dataStart);
            int normalised = value;
            if (normalised > 7 && hat.LogicalMin > 0)
            {
                normalised -= hat.LogicalMin;
            }

            if (normalised is >= 0 and <= 7)
            {
                HidParserHelpers.ApplyHat(state, normalised);
            }
        }

        return true;
    }

    private static Dictionary<byte, GenericLayout> BuildLayouts(ReportDescriptor descriptor)
    {
        var layouts = new Dictionary<byte, GenericLayout>();

        foreach (IGrouping<byte, ReportField> group in descriptor.Fields.GroupBy(static field => field.ReportId))
        {
            var axes = new Dictionary<ushort, ReportField>();
            var buttons = new List<ButtonBinding>();

            foreach (ReportField field in group)
            {
                if (field.UsagePage == 0x01 && field.Usage is >= 0x30 and <= 0x39)
                {
                    axes[field.Usage] = field;
                }
                else if (field.UsagePage == 0x09)
                {
                    buttons.Add(new ButtonBinding(field.Usage, field));
                }
            }

            if (axes.Count == 0 && buttons.Count == 0)
            {
                continue;
            }

            ReportField? x = axes.TryGetValue(0x30, out ReportField xValue) ? xValue : null;
            ReportField? y = axes.ContainsKey(0x31) ? axes[0x31] : null;
            ReportField? z = axes.ContainsKey(0x32) ? axes[0x32] : null;
            ReportField? rx = axes.ContainsKey(0x33) ? axes[0x33] : null;
            ReportField? ry = axes.ContainsKey(0x34) ? axes[0x34] : null;
            ReportField? rz = axes.ContainsKey(0x35) ? axes[0x35] : null;
            ReportField? hat = axes.ContainsKey(0x39) ? axes[0x39] : null;

            ReportField? rightX;
            ReportField? rightY;

            if (rx is not null && ry is not null)
            {
                // The common XInput-like layout: X/Y left stick, Rx/Ry right stick.
                rightX = rx;
                rightY = ry;
            }
            else if (z is not null && rz is not null)
            {
                // Four-axis DirectInput pads: X/Y left stick, Z/Rz right stick.
                rightX = z;
                rightY = rz;
            }
            else
            {
                rightX = rx ?? z;
                rightY = ry ?? rz;
            }

            var triggerCandidates = new List<ReportField>(4);
            AddIfUnused(triggerCandidates, z, rightX, rightY);
            AddIfUnused(triggerCandidates, rz, rightX, rightY);
            AddIfUnused(triggerCandidates, rx, rightX, rightY);
            AddIfUnused(triggerCandidates, ry, rightX, rightY);

            ReportField? leftTrigger = triggerCandidates.Count > 0 ? triggerCandidates[0] : null;
            ReportField? rightTrigger = triggerCandidates.Count > 1 ? triggerCandidates[1] : null;

            buttons.Sort(static (a, b) => a.Usage.CompareTo(b.Usage));

            var layout = new GenericLayout
            {
                ReportId = group.Key,
                LeftX = x,
                LeftY = y,
                RightX = rightX,
                RightY = rightY,
                LeftTrigger = leftTrigger,
                RightTrigger = rightTrigger,
                Hat = hat,
                Buttons = buttons,
            };

            layout.MaxBitOffset = CalculateMaxBitOffset(layout);
            layouts[group.Key] = layout;
        }

        return layouts;
    }

    private static void AddIfUnused(
        List<ReportField> candidates,
        ReportField? field,
        ReportField? rightX,
        ReportField? rightY)
    {
        if (field is not { } value)
        {
            return;
        }

        if (rightX is { } rx && rx.Equals(value))
        {
            return;
        }

        if (rightY is { } ry && ry.Equals(value))
        {
            return;
        }

        if (!candidates.Contains(value))
        {
            candidates.Add(value);
        }
    }

    private static int CalculateMaxBitOffset(GenericLayout layout)
    {
        int max = 0;

        foreach (ReportField? field in new[]
                 {
                     layout.LeftX, layout.LeftY, layout.RightX, layout.RightY,
                     layout.LeftTrigger, layout.RightTrigger, layout.Hat,
                 })
        {
            if (field is { } value)
            {
                max = Math.Max(max, value.BitOffset + value.BitSize);
            }
        }

        foreach (ButtonBinding button in layout.Buttons)
        {
            max = Math.Max(max, button.Field.BitOffset + button.Field.BitSize);
        }

        return max;
    }

    private static int ReadAxis(ReadOnlySpan<byte> report, ReportField field, int dataStart, bool invert)
    {
        int raw = ReadRaw(report, field, dataStart);
        double min = field.LogicalMin;
        double max = field.LogicalMax;

        if (max <= min)
        {
            max = min + ((1L << Math.Clamp(field.BitSize, 1, 30)) - 1);
        }

        double normalised = (raw - min) / (max - min);
        int value = (int)Math.Round((normalised * 65535.0) - 32768.0);
        value = Math.Clamp(value, -32768, 32767);

        return invert ? Math.Clamp(-value, -32768, 32767) : value;
    }

    private static int ReadTrigger(ReadOnlySpan<byte> report, ReportField field, int dataStart)
    {
        int raw = ReadRaw(report, field, dataStart);
        double min = field.LogicalMin;
        double max = field.LogicalMax;

        if (max <= min)
        {
            max = min + ((1L << Math.Clamp(field.BitSize, 1, 30)) - 1);
        }

        double normalised = Math.Clamp((raw - min) / (max - min), 0.0, 1.0);
        return (int)Math.Round(normalised * HidParserHelpers.TriggerFullScale);
    }

    private static int ReadRaw(ReadOnlySpan<byte> report, ReportField field, int dataStart)
    {
        int bits = field.BitSize;
        if (bits is <= 0 or > 32)
        {
            return 0;
        }

        int absoluteBit = (dataStart * 8) + field.BitOffset;
        int byteIndex = absoluteBit >> 3;
        int shift = absoluteBit & 0x07;
        int byteCount = (shift + bits + 7) >> 3;

        if (byteIndex < 0 || byteIndex + byteCount > report.Length)
        {
            return 0;
        }

        ulong value = 0;
        for (int i = 0; i < byteCount; i++)
        {
            value |= (ulong)report[byteIndex + i] << (8 * i);
        }

        value >>= shift;
        value &= (1UL << bits) - 1;

        if (field.Signed && (value & (1UL << (bits - 1))) != 0)
        {
            value |= ~((1UL << bits) - 1);
        }

        return unchecked((int)value);
    }

    private sealed class GenericLayout
    {
        public byte ReportId;
        public ReportField? LeftX;
        public ReportField? LeftY;
        public ReportField? RightX;
        public ReportField? RightY;
        public ReportField? LeftTrigger;
        public ReportField? RightTrigger;
        public ReportField? Hat;
        public List<ButtonBinding> Buttons = new();
        public int MaxBitOffset;
    }

    private readonly record struct ButtonBinding(ushort Usage, ReportField Field);
}
