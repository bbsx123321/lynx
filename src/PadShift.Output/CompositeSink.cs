using PadShift.Core;
using PadShift.Output.Keyboard;
using PadShift.Output.VirtualPad;

namespace PadShift.Output;

/// <summary>
/// Fans one <see cref="OutputIntent"/> out to at most one keyboard/mouse backend and at most one
/// virtual pad backend, and aggregates rumble requests into <see cref="RumbleState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both sinks receive the whole intent; <see cref="LayoutInjector"/> ignores pad fields and the pad
/// sink ignores keyboard/mouse fields. That keeps the composition trivial and avoids slicing the
/// intent into two objects every frame.
/// </para>
/// <para>
/// Rumble is intentionally not read from <see cref="OutputIntent.RumbleLeft"/>/<c>RumbleRight</c>:
/// a virtual pad cannot push rumble into a game, the game pushes it to us through the emulated pad's
/// output report. <see cref="Rumble"/> is where that request lands so the engine can forward it to
/// the physical controller's force-feedback API.
/// </para>
/// <para>
/// When constructed with <c>ownsSinks: true</c> (default), <see cref="Dispose"/> also disposes the
/// sinks. Otherwise it only releases held keys/buttons, leaving the sinks usable elsewhere.
/// </para>
/// </remarks>
public sealed class CompositeSink : IOutputSink
{
    private readonly LayoutInjector? _layout;
    private readonly VirtualPadSink? _pad;
    private readonly IRumbleSource? _layoutRumbleSource;
    private readonly IRumbleSource? _padRumbleSource;
    private readonly bool _ownsSinks;
    private int _disposed;

    /// <summary>
    /// Creates a composite. At least one of <paramref name="layout"/> / <paramref name="pad"/> must
    /// be non-null; both may be supplied.
    /// </summary>
    public CompositeSink(LayoutInjector? layout, VirtualPadSink? pad, bool ownsSinks = true)
    {
        if (layout is null && pad is null)
        {
            throw new ArgumentException("A composite sink needs at least one output backend.", nameof(layout));
        }

        _layout = layout;
        _pad = pad;
        _ownsSinks = ownsSinks;
        Rumble = new RumbleState();

        // Both halves are given a chance to feed rumble: the pad gets it from the game's output
        // report, and a future layout backend could expose a device passthrough the same way.
        _layoutRumbleSource = layout as IRumbleSource;
        _padRumbleSource = pad as IRumbleSource;

        if (_layoutRumbleSource is not null)
        {
            _layoutRumbleSource.RumbleReceived += OnRumbleReceived;
        }

        if (_padRumbleSource is not null)
        {
            _padRumbleSource.RumbleReceived += OnRumbleReceived;
        }
    }

    /// <summary>Keyboard/mouse backend, or null when running pad-only.</summary>
    public LayoutInjector? Layout => _layout;

    /// <summary>Virtual pad backend, or null when running keyboard/mouse-only.</summary>
    public VirtualPadSink? Pad => _pad;

    /// <summary>Latest rumble request produced by the emulated pad.</summary>
    public RumbleState Rumble { get; }

    /// <inheritdoc />
    public void Submit(OutputIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _layout?.Submit(intent);
        _pad?.Submit(intent);
    }

    /// <inheritdoc />
    public void ReleaseAll()
    {
        _layout?.ReleaseAll();
        _pad?.ReleaseAll();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_layoutRumbleSource is not null)
        {
            _layoutRumbleSource.RumbleReceived -= OnRumbleReceived;
        }

        if (_padRumbleSource is not null)
        {
            _padRumbleSource.RumbleReceived -= OnRumbleReceived;
        }

        if (_ownsSinks)
        {
            _layout?.Dispose();
            _pad?.Dispose();
        }
        else
        {
            // Still lift held keys/buttons even when the sinks are owned elsewhere.
            _layout?.ReleaseAll();
            _pad?.ReleaseAll();
        }

        Rumble.Reset();
        GC.SuppressFinalize(this);
    }

    private void OnRumbleReceived(byte largeMotor, byte smallMotor) => Rumble.Report(largeMotor, smallMotor);
}
