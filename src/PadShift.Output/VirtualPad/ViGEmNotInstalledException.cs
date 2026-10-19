namespace PadShift.Output.VirtualPad;

/// <summary>
/// Base class for virtual-pad failures that are not "the bus is missing" - for example a full bus
/// slot table or a pad that disappeared at runtime.
/// </summary>
public class VirtualPadException : Exception
{
    public VirtualPadException(string message)
        : base(message)
    {
    }

    public VirtualPadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown once, from the pad sink constructor, when ViGEmBus is not installed (or its native client
/// cannot be loaded). After construction the sink never throws; runtime failures surface through
/// <see cref="VirtualPadSink.IsConnected"/> and <see cref="VirtualPadSink.LastError"/>.
/// </summary>
public sealed class ViGEmNotInstalledException : VirtualPadException
{
    /// <summary>Default actionable message shown to users.</summary>
    public const string DefaultMessage =
        "ViGEmBus was not found. Install ViGEmBus 1.22 or newer " +
        "(https://github.com/ViGEm/ViGEmBus/releases), make sure the ViGEmBus service is running, " +
        "then restart PadShift.";

    public ViGEmNotInstalledException()
        : base(DefaultMessage)
    {
    }

    public ViGEmNotInstalledException(string message)
        : base(message)
    {
    }

    public ViGEmNotInstalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
