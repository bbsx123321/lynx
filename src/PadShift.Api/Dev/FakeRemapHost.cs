using PadShift.Core.Host;

namespace PadShift.Api.Dev;

/// <summary>
/// 容器里没有注册 <see cref="IRemapHost"/> 时的最小内存实现：一个虚拟手柄 + 槽位/remap 状态。
/// 让 API 可以脱离引擎独立启动（联调、集成测试）；不读写任何真实设备。
/// </summary>
public sealed class FakeRemapHost : IRemapHost
{
    public const string ControllerIdText = "b6a9f0f2-6f5c-4a3e-9a4d-3f2f0a1c7d10";

    private readonly object _gate = new();
    private readonly HostController _controller = new(
        Guid.Parse(ControllerIdText),
        "PadShift Virtual Pad",
        "Xbox360",
        "Wired",
        VendorId: 0x045E,
        ProductId: 0x028E,
        HasGyro: false,
        HasTouchpad: false,
        HasRumble: true,
        BatteryPercent: 100);

    private readonly List<ShiftInfo> _shifts =
    [
        new ShiftInfo(0, "Default"),
        new ShiftInfo(1, "Shift 1"),
        new ShiftInfo(2, "Shift 2"),
    ];

    private readonly Dictionary<int, List<string>> _applied = [];
    private RemapState _remapState = RemapState.NothingApplied;
    private int _currentSlot;
    private int _currentLayer;
    private bool _started;
    private (byte Left, byte Right, ushort Duration)? _lastVibration;

    public event EventHandler<HostEvent>? Event;

    public bool RemapEnabled
    {
        get
        {
            lock (_gate)
            {
                return _remapState == RemapState.RemapOn;
            }
        }
    }

    public IReadOnlyList<HostController> Controllers => [_controller];

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>最近一次振动请求，方便手工验证 Vibrate 路由。</summary>
    public (byte Left, byte Right, ushort Duration)? LastVibration
    {
        get
        {
            lock (_gate)
            {
                return _lastVibration;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _remapState = RemapState.NothingApplied;
            _currentLayer = 0;
        }
    }

    public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId)
    {
        RequireController(controllerId);
        return _shifts;
    }

    public RemapState GetRemapState(Guid controllerId)
    {
        RequireController(controllerId);
        lock (_gate)
        {
            return _remapState;
        }
    }

    public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot)
    {
        RequireController(controllerId);

        if (!File.Exists(profilePath))
        {
            return Task.FromResult(new ApplyConfigResult(false, $"Config file not found: {profilePath}", slot));
        }

        if (!SlotCodec.IsValid(slot))
        {
            return Task.FromResult(new ApplyConfigResult(false, $"Slot index {slot} is outside 0..3.", slot));
        }

        lock (_gate)
        {
            _applied[slot] = [profilePath];
            _currentSlot = slot;
            _currentLayer = 0;
        }

        Raise(new ConfigAppliedEvent(controllerId, profilePath, slot, DateTimeOffset.UtcNow));
        return Task.FromResult(new ApplyConfigResult(true, null, slot));
    }

    public EnableRemapResult EnableRemap(Guid? controllerId)
    {
        if (controllerId is { } id)
        {
            RequireController(id);
        }

        lock (_gate)
        {
            _remapState = RemapState.RemapOn;
        }

        Raise(new RemapStateChangedEvent(controllerId ?? _controller.Id, RemapState.RemapOn, DateTimeOffset.UtcNow));
        return new EnableRemapResult(true, null);
    }

    public void DisableRemap(Guid? controllerId)
    {
        if (controllerId is { } id)
        {
            RequireController(id);
        }

        lock (_gate)
        {
            _remapState = RemapState.RemapOff;
        }

        Raise(new RemapStateChangedEvent(controllerId ?? _controller.Id, RemapState.RemapOff, DateTimeOffset.UtcNow));
    }

    public void SelectSlot(Guid controllerId, int slot)
    {
        RequireController(controllerId);
        if (!SlotCodec.IsValid(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot index must be 0..3.");
        }

        lock (_gate)
        {
            _currentSlot = slot;
        }

        Raise(new SlotChangedEvent(controllerId, slot, DateTimeOffset.UtcNow));
    }

    public int GetCurrentSlot(Guid controllerId)
    {
        RequireController(controllerId);
        lock (_gate)
        {
            return _currentSlot;
        }
    }

    public bool SetLayer(Guid controllerId, int layer)
    {
        RequireController(controllerId);

        ShiftInfo? shift = _shifts.FirstOrDefault(candidate => candidate.Shift == layer);
        if (layer != 0 && shift is null)
        {
            return false;
        }

        lock (_gate)
        {
            _currentLayer = layer;
        }

        Raise(new ShiftChangedEvent(
            controllerId,
            layer,
            shift?.Name ?? "Default",
            DateTimeOffset.UtcNow));
        return true;
    }

    public int GetLayer(Guid controllerId)
    {
        RequireController(controllerId);
        lock (_gate)
        {
            return _currentLayer;
        }
    }

    public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot)
    {
        RequireController(controllerId);
        lock (_gate)
        {
            return _applied.TryGetValue(slot, out var paths) ? [.. paths] : [];
        }
    }

    public void ClearSlot(Guid controllerId, int slot)
    {
        RequireController(controllerId);
        if (!SlotCodec.IsValid(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot index must be 0..3.");
        }

        bool removed;
        lock (_gate)
        {
            removed = _applied.Remove(slot);
            if (_applied.Count == 0)
            {
                _remapState = RemapState.NothingApplied;
            }
        }

        if (removed)
        {
            Raise(new SlotClearedEvent(controllerId, slot, DateTimeOffset.UtcNow));
        }
    }

    public bool IsKeyPressed(Guid controllerId, int scanCode)
    {
        RequireController(controllerId);
        return false;
    }

    public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
    {
        RequireController(controllerId);
        lock (_gate)
        {
            _lastVibration = (leftMotor, rightMotor, durationMs);
        }
    }

    public void Dispose()
    {
        Stop();
        Event = null;
    }

    private void RequireController(Guid controllerId)
    {
        if (controllerId != _controller.Id)
        {
            throw new KeyNotFoundException($"FakeRemapHost only knows controller {_controller.Id}.");
        }
    }

    private void Raise(HostEvent hostEvent)
    {
        var handler = Event;
        handler?.Invoke(this, hostEvent);
    }
}
