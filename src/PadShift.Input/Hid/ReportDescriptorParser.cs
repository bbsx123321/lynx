namespace PadShift.Input.Hid;

/// <summary>One variable input field extracted from a HID report descriptor.</summary>
internal readonly record struct ReportField(
    byte ReportId,
    ushort UsagePage,
    ushort Usage,
    int BitOffset,
    int BitSize,
    bool Signed,
    int LogicalMin,
    int LogicalMax);

/// <summary>Minimal parsed view of a HID report descriptor, used by the generic gamepad parser.</summary>
internal sealed class ReportDescriptor
{
    public bool IsGamepad { get; init; }

    public IReadOnlyList<ReportField> Fields { get; init; } = Array.Empty<ReportField>();
}

/// <summary>
/// Small, dependency-free HID report descriptor reader. It only extracts variable input fields
/// and whether the descriptor advertises a Joystick/Gamepad/Multi-axis application collection;
/// output/feature reports, arrays and physical units are intentionally ignored.
/// </summary>
internal static class ReportDescriptorParser
{
    private const int MaxFields = 512;
    private const int MaxBitSize = 32;

    private struct GlobalState
    {
        public ushort UsagePage;
        public int ReportSize;
        public int ReportCount;
        public byte ReportId;
        public int LogicalMin;
        public int LogicalMax;
    }

    public static ReportDescriptor Parse(ReadOnlySpan<byte> descriptor)
    {
        var fields = new List<ReportField>();
        var inputBitOffsets = new Dictionary<byte, int>();
        var globalStack = new Stack<GlobalState>();
        var usages = new List<(ushort Page, ushort Usage)>();
        var global = new GlobalState();
        int usageMin = 0;
        int usageMax = 0;
        ushort usageRangePage = 0;
        bool hasUsageRange = false;
        bool isGamepad = false;

        int index = 0;
        while (index < descriptor.Length)
        {
            byte prefix = descriptor[index++];
            int size = prefix & 0x03;
            if (size == 3)
            {
                size = 4;
            }

            int type = (prefix >> 2) & 0x03;
            int tag = (prefix >> 4) & 0x0F;

            if (index + size > descriptor.Length)
            {
                break;
            }

            uint value = ReadUnsigned(descriptor.Slice(index, size));
            index += size;

            switch (type)
            {
                case 0: // Main item
                    if (tag == 0x08) // Input
                    {
                        bool isConstant = (value & 0x01) != 0;
                        bool isVariable = (value & 0x02) != 0;
                        bool isValidLayout = global.ReportSize is > 0 and <= MaxBitSize
                            && global.ReportCount is > 0 and <= 256;

                        int bitOffset = inputBitOffsets.TryGetValue(global.ReportId, out int known)
                            ? known
                            : 0;

                        if (!isConstant && isVariable && isValidLayout)
                        {
                            for (int fieldIndex = 0; fieldIndex < global.ReportCount; fieldIndex++)
                            {
                                if (fields.Count >= MaxFields)
                                {
                                    break;
                                }

                                if (!TryResolveUsage(
                                        usages,
                                        usageMin,
                                        usageMax,
                                        usageRangePage,
                                        hasUsageRange,
                                        fieldIndex,
                                        global.ReportCount,
                                        out ushort page,
                                        out ushort usage))
                                {
                                    continue;
                                }

                                if (page == 0x01 && usage is 0x04 or 0x05 or 0x08)
                                {
                                    isGamepad = true;
                                }

                                bool interestingAxis = page == 0x01 && usage is >= 0x30 and <= 0x39;
                                bool interestingButton = page == 0x09;

                                if (interestingAxis || interestingButton)
                                {
                                    fields.Add(new ReportField(
                                        global.ReportId,
                                        page,
                                        usage,
                                        bitOffset + (fieldIndex * global.ReportSize),
                                        global.ReportSize,
                                        global.LogicalMin < 0,
                                        global.LogicalMin,
                                        global.LogicalMax));
                                }
                            }
                        }

                        if (isValidLayout)
                        {
                            inputBitOffsets[global.ReportId] =
                                bitOffset + (global.ReportCount * global.ReportSize);
                        }
                    }

                    // Local items are cleared by every main item.
                    usages.Clear();
                    hasUsageRange = false;
                    usageMin = 0;
                    usageMax = 0;
                    usageRangePage = 0;
                    break;

                case 1: // Global item
                    switch (tag)
                    {
                        case 0x00:
                            global.UsagePage = (ushort)value;
                            break;
                        case 0x01:
                            global.LogicalMin = SignExtend(value, size);
                            break;
                        case 0x02:
                            global.LogicalMax = SignExtend(value, size);
                            break;
                        case 0x07:
                            global.ReportSize = (int)value;
                            break;
                        case 0x08:
                            global.ReportId = (byte)value;
                            break;
                        case 0x09:
                            global.ReportCount = (int)value;
                            break;
                        case 0x0A:
                            globalStack.Push(global);
                            break;
                        case 0x0B:
                            if (globalStack.Count > 0)
                            {
                                global = globalStack.Pop();
                            }

                            break;
                        default:
                            break;
                    }

                    break;

                case 2: // Local item
                    switch (tag)
                    {
                        case 0x00:
                        {
                            ushort page = global.UsagePage;
                            ushort usage = (ushort)(value & 0xFFFF);
                            uint pageBits = value >> 16;
                            if (pageBits != 0)
                            {
                                page = (ushort)pageBits;
                            }

                            usages.Add((page, usage));

                            if (page == 0x01 && usage is 0x04 or 0x05 or 0x08)
                            {
                                isGamepad = true;
                            }

                            break;
                        }

                        case 0x01:
                            usageMin = (int)(value & 0xFFFF);
                            usageRangePage = (value >> 16) != 0
                                ? (ushort)(value >> 16)
                                : global.UsagePage;
                            hasUsageRange = true;
                            break;

                        case 0x02:
                            usageMax = (int)(value & 0xFFFF);
                            hasUsageRange = true;
                            break;

                        default:
                            break;
                    }

                    break;

                default:
                    break;
            }
        }

        return new ReportDescriptor
        {
            IsGamepad = isGamepad,
            Fields = fields,
        };
    }

    private static bool TryResolveUsage(
        List<(ushort Page, ushort Usage)> usages,
        int usageMin,
        int usageMax,
        ushort usageRangePage,
        bool hasUsageRange,
        int index,
        int count,
        out ushort page,
        out ushort usage)
    {
        page = 0;
        usage = 0;

        if (usages.Count > 0)
        {
            if (index < usages.Count)
            {
                (page, usage) = usages[index];
                return true;
            }

            // A single button usage with a report count larger than one: synthesise the
            // following button usages, which is how many cheap gamepads describe buttons.
            if (usages.Count == 1 && usages[0].Page == 0x09 && usages[0].Usage + index <= ushort.MaxValue)
            {
                page = usages[0].Page;
                usage = (ushort)(usages[0].Usage + index);
                return true;
            }

            return false;
        }

        if (hasUsageRange && usageMin <= usageMax && usageMin + index <= usageMax)
        {
            page = usageRangePage;
            usage = (ushort)(usageMin + index);
            return true;
        }

        return false;
    }

    private static uint ReadUnsigned(ReadOnlySpan<byte> data)
    {
        uint value = 0;
        for (int i = 0; i < data.Length; i++)
        {
            value |= (uint)data[i] << (8 * i);
        }

        return value;
    }

    private static int SignExtend(uint value, int sizeBytes)
    {
        if (sizeBytes <= 0 || sizeBytes >= 4)
        {
            return unchecked((int)value);
        }

        int bits = sizeBytes * 8;
        uint signBit = 1u << (bits - 1);
        if ((value & signBit) != 0)
        {
            value |= ~((1u << bits) - 1);
        }

        return unchecked((int)value);
    }
}
