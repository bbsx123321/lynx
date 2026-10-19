using HidSharp;
using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// Generic HID input source. It opens one <see cref="HidDevice"/> through HidSharp, selects a
/// parser from the VID/PID table (or the descriptor-driven generic parser) and turns each input
/// report into a <see cref="ControllerState"/> snapshot.
/// </summary>
/// <remarks>
/// <see cref="TryPoll"/> is not thread-safe: call it from exactly one thread. Device errors never
/// escape the polling path; they set <see cref="IsConnected"/> to <c>false</c> instead.
/// </remarks>
public sealed class HidDeviceSource : IInputSource
{
    private const int ReadTimeoutMilliseconds = 0;
    private const int MaximumReportsPerPoll = 128;
    private const int MinimumReportBufferLength = 64;
    private const int MaximumReportBufferLength = 8192;

    private readonly HidStream _stream;
    private readonly IReportParser _parser;
    private readonly byte[] _buffer;
    private readonly ControllerState _scratch = new();
    private readonly ControllerState _latest = new();
    private readonly string _deviceId;

    private bool _connected = true;
    private bool _disposed;

    private HidDeviceSource(HidStream stream, IReportParser parser, string deviceId, int bufferLength)
    {
        _stream = stream;
        _parser = parser;
        _deviceId = deviceId;
        _buffer = new byte[bufferLength];
    }

    /// <inheritdoc />
    public string DeviceId => _deviceId;

    /// <inheritdoc />
    public bool IsConnected => !_disposed && _connected;

    /// <summary>Name of the selected report parser (diagnostics only).</summary>
    public string ParserName => _parser.Name;

    /// <summary>
    /// Attempts to open <paramref name="device"/> and create a source for it. Returns <c>false</c>
    /// when the device cannot be opened or does not expose a supported/gamepad report descriptor.
    /// </summary>
    public static bool TryCreate(HidDevice device, out HidDeviceSource? source)
    {
        ArgumentNullException.ThrowIfNull(device);
        source = null;

        HidStream? stream = null;
        try
        {
            if (!device.TryOpen(out stream) || stream is null || !stream.CanRead)
            {
                stream?.Dispose();
                return false;
            }

            // Zero means "do not wait" in HidSharp: TryPoll must never stall the caller's frame.
            stream.ReadTimeout = OperatingSystem.IsWindows() ? ReadTimeoutMilliseconds : 1;

            byte[]? rawDescriptor = null;
            if (!ReportParserRegistry.IsKnownDevice(device))
            {
                try
                {
                    rawDescriptor = device.GetRawReportDescriptor();
                }
                catch
                {
                    // Unknown devices without a readable descriptor cannot be classified.
                    rawDescriptor = null;
                }
            }

            IReportParser? parser = ReportParserRegistry.Create(device, rawDescriptor);
            if (parser is null)
            {
                stream.Dispose();
                return false;
            }

            int maxReportLength = 0;
            try
            {
                maxReportLength = device.GetMaxInputReportLength();
            }
            catch
            {
                maxReportLength = 0;
            }

            int bufferLength = Math.Clamp(
                Math.Max(MinimumReportBufferLength, maxReportLength + 1),
                MinimumReportBufferLength,
                MaximumReportBufferLength);

            source = new HidDeviceSource(stream, parser, BuildDeviceId(device), bufferLength);
            return true;
        }
        catch
        {
            try
            {
                stream?.Dispose();
            }
            catch
            {
                // Swallow disposal failures: construction reports false instead.
            }

            source = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryPoll(ControllerState state)
    {
        if (_disposed || !_connected)
        {
            return false;
        }

        bool parsedAny = false;

        // A 250-1000 Hz controller can queue several reports between two polls. Drain the queue
        // (bounded) and keep the newest valid snapshot instead of falling behind by one report.
        for (int reportIndex = 0; reportIndex < MaximumReportsPerPoll; reportIndex++)
        {
            int bytesRead;
            try
            {
                bytesRead = _stream.Read(_buffer);
            }
            catch (TimeoutException)
            {
                // No more reports currently queued: still connected, just no new data.
                break;
            }
            catch
            {
                _connected = false;
                return false;
            }

            if (bytesRead <= 0)
            {
                break;
            }

            try
            {
                _scratch.Clear();
                if (!_parser.TryParse(_buffer.AsSpan(0, bytesRead), _scratch))
                {
                    continue;
                }

                _scratch.TimestampTicks = DateTime.UtcNow.Ticks;
                ControllerStateCopy.Copy(_scratch, _latest);
                parsedAny = true;
            }
            catch
            {
                // A malformed report must never break the caller's polling loop.
                _connected = false;
                return false;
            }
        }

        if (!parsedAny)
        {
            return false;
        }

        ControllerStateCopy.Copy(_latest, state);
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

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Disposal must be idempotent and silent.
        }
    }

    private static string BuildDeviceId(HidDevice device)
    {
        int vendor = device.VendorID;
        int product = device.ProductID;

        string serial = string.Empty;
        try
        {
            serial = device.GetSerialNumber() ?? string.Empty;
        }
        catch
        {
            serial = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(serial))
        {
            return $"HID:{vendor:X4}:{product:X4}:{serial}";
        }

        string path = string.Empty;
        try
        {
            path = device.DevicePath ?? string.Empty;
        }
        catch
        {
            path = string.Empty;
        }

        return $"HID:{vendor:X4}:{product:X4}#{StableHash(path):X8}";
    }

    private static uint StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash;
        }
    }
}
