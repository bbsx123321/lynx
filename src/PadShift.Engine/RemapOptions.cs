namespace PadShift.Engine;

/// <summary>
/// Engine tuning. All values are fixed at construction so a <see cref="RemapEngine"/>
/// never touches wall-clock time or host configuration while ticking.
/// </summary>
public sealed class RemapOptions
{
    /// <summary>Number of addressable non-base layers (1..19). the v2.6 wire protocol caps this at 12.</summary>
    public int MaxLayers { get; init; } = 12;

    /// <summary>Nominal poll period used by the host façade; the engine itself is time-driven by <c>nowMs</c>.</summary>
    public int TickMs { get; init; } = 4;

    /// <summary>
    /// Physical device id this engine consumes (matches <c>devices.hardware[].id</c>).
    /// Only used to decide which physical buttons are swallowed by remapping rules;
    /// all masks are evaluated against the single <see cref="PadShift.Core.ControllerState"/>
    /// handed to <see cref="RemapEngine.Tick"/>.
    /// </summary>
    public long DeviceId { get; init; } = 1;

    internal int EffectiveMaxLayers => MaxLayers < 1 ? 1 : MaxLayers > 19 ? 19 : MaxLayers;
}
