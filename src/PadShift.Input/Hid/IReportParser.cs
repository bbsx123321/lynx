using PadShift.Core;

namespace PadShift.Input.Hid;

/// <summary>
/// Parses one raw HID input report (including the leading report ID, when the device uses one)
/// into a <see cref="ControllerState"/> snapshot.
/// </summary>
/// <remarks>
/// Implementations must be deterministic and must never throw. Return <c>false</c> for reports
/// that are not recognised; the caller keeps the previous good snapshot in that case.
/// </remarks>
public interface IReportParser
{
    /// <summary>Human readable parser name, useful in diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Attempts to decode <paramref name="report"/>. On success the parser clears and fills
    /// <paramref name="state"/> completely, then returns <c>true</c>.
    /// </summary>
    bool TryParse(ReadOnlySpan<byte> report, ControllerState state);
}
