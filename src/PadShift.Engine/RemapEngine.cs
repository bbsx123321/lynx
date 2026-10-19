using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using PadShift.Core;
using PadShift.Core.Profile;

namespace PadShift.Engine;

/// <summary>
/// Deterministic mapping core. <see cref="Tick"/> is pure: it takes an input snapshot and a
/// monotonic millisecond clock, returns a preallocated intent, never allocates and never
/// throws on malformed profiles. All wall-clock / threading concerns live in the host façade.
/// </summary>
public sealed class RemapEngine
{
    private const int MaxWaitMs = 60_000;
    private const int MaxPauseMs = 600_000;
    private const int MaxJumpDelayMs = 5_000;

    /// <summary><c>OutputIntent.KeyboardKeys</c> index flag for E0-prefixed keys (see PadShift.Output ScanCode).</summary>
    private const int ScanCodeExtendedFlag = 0x80;

    /// <summary>Win32 WHEEL_DELTA; <c>OutputIntent.MouseWheel</c> counts wheel units, not notches.</summary>
    private const int WheelDelta = 120;

    /// <summary>模拟分区回差：进入/离开相邻区的阈值相差 300 个原始单位，避免边界抖动重复触发。</summary>
    private const int ZoneHysteresis = 300;

    /// <summary>无 <c>highZoneMax</c> 时模拟轴的分区上限（0..32767 三等分）。</summary>
    private const int DefaultZoneMax = 32767;

    private const string ReasonToggle = "toggle";
    private const string ReasonHold = "hold";
    private const string ReasonRelease = "release";
    private const string ReasonJump = "jump";
    private const string ReasonLayer = "layer";

    private readonly RemapOptions _options;
    private readonly OutputIntent _intent = new();

    // Precomputed lookups (never mutated while ticking).
    private readonly bool[] _consumed = new bool[256];
    private readonly bool[] _invertAxis = new bool[6];

    private CompiledMask[] _masks = Array.Empty<CompiledMask>();
    private CompiledShift[] _shifts = Array.Empty<CompiledShift>();
    private CompiledMapping[] _mappings = Array.Empty<CompiledMapping>();
    private ActivationGroup[] _groups = Array.Empty<ActivationGroup>();
    private ZoneTracker[] _zones = Array.Empty<ZoneTracker>();

    private int _defaultLayer;
    private int _activeLayer;
    private int _holdLayer;
    private int _reportedLayer;
    private bool _unlockAll;
    private bool _unlockKeyboard;
    private bool _unlockMouse;
    private int _pendingJumpLayer = -1;
    private long _pendingJumpAt;
    private long _now;
    private string _layerReason = ReasonToggle;
    private bool[]? _suppressedMasks;

    public RemapEngine(RemapOptions options)
    {
        _options = options ?? new RemapOptions();
    }

    /// <summary>Currently selected layer: an explicit shift when held/toggled/jumped, otherwise the profile's base layer.</summary>
    public int CurrentLayer => _activeLayer != 0 ? _activeLayer : _defaultLayer;

    /// <summary>Raised from <see cref="Tick"/> when the effective layer changes. Arguments: layer id, reason.</summary>
    public event Action<int, string>? LayerChanged;

    public RemapOptions Options => _options;

    /// <summary>Number of compiled shift definitions (layer 1..N), excluding the base layer.</summary>
    public int ShiftCount => _shifts.Length;

    /// <summary>Compile a profile into the fast structures used by <see cref="Tick"/>.</summary>
    public void LoadProfile(GamepadProfile profile)
    {
        Reset();
        _masks = Array.Empty<CompiledMask>();
        _shifts = Array.Empty<CompiledShift>();
        _mappings = Array.Empty<CompiledMapping>();
        _groups = Array.Empty<ActivationGroup>();
        _zones = Array.Empty<ZoneTracker>();
        _defaultLayer = 0;
        _activeLayer = 0;
        _holdLayer = 0;
        _reportedLayer = 0;
        _pendingJumpLayer = -1;
        _pendingJumpAt = 0;
        _layerReason = ReasonToggle;
        _suppressedMasks = null;
        Array.Clear(_consumed);
        Array.Clear(_invertAxis);

        try
        {
            Compile(profile ?? new GamepadProfile());
        }
        catch
        {
            // A malformed profile must never take the engine down: keep whatever compiled.
            _mappings = Array.Empty<CompiledMapping>();
            _groups = Array.Empty<ActivationGroup>();
            _zones = Array.Empty<ZoneTracker>();
        }

        _reportedLayer = CurrentLayer;
    }

    /// <summary>
    /// Switches to <paramref name="layer"/> as if a profile shift had selected it. Layer 0 is the
    /// base layer; any other value must be declared by <c>shifts[]</c> and compiled into this profile.
    /// Returns false (leaving the current layer untouched) for an unknown layer.
    /// </summary>
    /// <remarks>
    /// The selection is a normal active-layer assignment: a held default shift or a later
    /// toggle/jump still wins, exactly like a shift-triggered switch. <c>unheritableMasks</c> of
    /// the selected layer are applied by the next <see cref="Tick"/> because they are resolved from
    /// <see cref="CurrentLayer"/>.
    /// </remarks>
    public bool TrySetLayer(int layer)
    {
        if (layer < 0 || (layer != 0 && FindShift(layer) is null))
        {
            return false;
        }

        _pendingJumpLayer = -1;
        _pendingJumpAt = 0;
        _activeLayer = layer == _defaultLayer ? 0 : layer;
        _layerReason = ReasonLayer;

        // Always report the request, even when the resulting layer is already current: the radial
        // menu and the API both expect a status event for every accepted call.
        _reportedLayer = CurrentLayer;
        LayerChanged?.Invoke(CurrentLayer, _layerReason);
        return true;
    }

    /// <summary>Bool-result alias for <see cref="TrySetLayer"/>; false leaves the current layer untouched.</summary>
    public bool SetLayer(int layer) => TrySetLayer(layer);

    /// <summary>Drop every in-flight output (used when remapping is disabled).</summary>
    public void Reset()
    {
        for (int i = 0; i < _mappings.Length; i++)
        {
            StopMapping(_mappings[i]);
        }

        _pendingJumpLayer = -1;
        _pendingJumpAt = 0;
        _intent.Clear();
    }

    /// <summary>
    /// One deterministic frame. The returned intent is the same instance every call and is
    /// overwritten on the next tick, so consumers must copy or submit it before ticking again.
    /// </summary>
    public OutputIntent Tick(ControllerState state, long nowMs)
    {
        _intent.Clear();
        if (state is null)
        {
            return _intent;
        }

        _now = nowMs;

        UpdateShifts(state);
        UpdateMasks(state);
        UpdateZones(state);

        if (!_unlockAll)
        {
            UpdateGroups(state);
            UpdateMappings();
            UpdateJump(nowMs);
        }
        else
        {
            // Unlock layer: mapping is suspended, every source is passed through untouched.
            for (int i = 0; i < _groups.Length; i++)
            {
                _groups[i].FireMask = 0;
                _groups[i].Eligible = false;
            }

            for (int i = 0; i < _mappings.Length; i++)
            {
                StopMapping(_mappings[i]);
            }
        }

        WriteOutputs();
        ApplyOutputGates();
        PassThrough(state);
        return _intent;
    }

    // ---------------------------------------------------------------- compile

    private void Compile(GamepadProfile profile)
    {
        // schema quirk (docs/04 §1.1): minTimeUnit != 1 OR absent means every pause/delay is ×10.
        long scale = profile.Config is { MinTimeUnit: 1 } ? 1 : 10;

        var masks = new List<CompiledMask>();
        var maskIndexById = new Dictionary<long, int>();
        foreach (MaskRule? mask in profile.Masks ?? new List<MaskRule>())
        {
            if (mask is null)
            {
                continue;
            }

            if (maskIndexById.ContainsKey(mask.Id))
            {
                continue;
            }

            maskIndexById[mask.Id] = masks.Count;
            masks.Add(new CompiledMask { Id = mask.Id, Keys = CompileKeys(mask.Set) });
        }

        _masks = masks.ToArray();

        var shifts = new List<CompiledShift>();
        foreach (ShiftRule? shift in profile.Shifts ?? new List<ShiftRule>())
        {
            if (shift is null)
            {
                continue;
            }

            int id = (int)shift.Id;
            if (id < 1 || id > _options.EffectiveMaxLayers)
            {
                continue;
            }

            var keys = CompileKeys(shift.Set);
            var compiled = new CompiledShift
            {
                Layer = id,
                Type = shift.Type,
                Keys = keys,
                Name = shift.Description ?? string.Empty,
            };

            if (shift.UnheritableMasks is { Count: > 0 } unheritable)
            {
                var suppressed = new bool[_masks.Length];
                bool any = false;
                foreach (long maskId in unheritable)
                {
                    if (maskIndexById.TryGetValue(maskId, out int index))
                    {
                        suppressed[index] = true;
                        any = true;
                    }
                }

                if (any)
                {
                    compiled.Suppressed = suppressed;
                }
            }

            if (shift.Type == ShiftType.Default && keys.Length == 0 && _defaultLayer == 0)
            {
                // A `default` shift without a trigger set is the "main" layer: always on.
                _defaultLayer = id;
            }

            bool duplicate = false;
            for (int i = 0; i < shifts.Count; i++)
            {
                if (shifts[i].Layer == id)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                shifts.Add(compiled);
            }
        }

        _shifts = shifts.ToArray();

        var mappings = new List<CompiledMapping>();
        var groups = new List<ActivationGroup>();
        var groupIndex = new Dictionary<(int, int, int), int>();
        var zones = new List<ZoneTracker>();
        var zoneIndex = new Dictionary<(int, int, int, int), int>();

        foreach (MappingRule? rule in profile.Mappings ?? new List<MappingRule>())
        {
            if (rule is null)
            {
                continue;
            }

            try
            {
                CompiledMapping? mapping = CompileMapping(
                    rule, profile, maskIndexById, scale, groups, groupIndex, zones, zoneIndex);
                if (mapping is not null)
                {
                    mappings.Add(mapping);
                }
            }
            catch
            {
                // Keep the remaining rules alive.
            }
        }

        _mappings = mappings.ToArray();
        _groups = groups.ToArray();
        _zones = zones.ToArray();

        BuildConsumption(profile);
    }

    private CompiledMapping? CompileMapping(
        MappingRule rule,
        GamepadProfile profile,
        Dictionary<long, int> maskIndexById,
        long scale,
        List<ActivationGroup> groups,
        Dictionary<(int, int, int), int> groupIndex,
        List<ZoneTracker> zones,
        Dictionary<(int, int, int, int), int> zoneIndexById)
    {
        MappingCondition condition = rule.Condition ?? new MappingCondition();

        int layer = -1;
        if (condition.ShiftId.HasValue)
        {
            long rawLayer = condition.ShiftId.Value;
            if (rawLayer is < 0 or > 19)
            {
                return null;
            }

            layer = (int)rawLayer;
        }

        int maskIndex = -1;
        int hardwareButton = -1;
        int zoneTracker = -1;
        int zoneIndex = -1;
        PadShift.Core.Profile.Activator activator;

        // Analog zone activators read condition.hardware (trigger / stick axis) rather than a mask.
        // The activator can be given by condition.mask.activator (the only typed activator slot in
        // the schema) or by an "activator" object in one of the extension-data dictionaries.
        if (condition.Hardware is { } analog
            && (analog.Trigger is not null || analog.Stick is not null)
            && TryGetZoneActivator(rule, condition, analog, out PadShift.Core.Profile.Activator zoneActivator)
            && TryResolveZoneSource(analog, out bool stickZone, out int zoneSource))
        {
            activator = zoneActivator;
            zoneIndex = (int)activator.Type - (int)ActivatorType.ZoneLow;
            int max = ResolveHighZoneMax(profile, rule, condition, analog, activator);
            var zoneKey = (layer, stickZone ? 1 : 0, zoneSource, max);
            if (!zoneIndexById.TryGetValue(zoneKey, out zoneTracker))
            {
                zoneTracker = zones.Count;
                zoneIndexById[zoneKey] = zoneTracker;
                zones.Add(new ZoneTracker
                {
                    Layer = layer,
                    IsStick = stickZone,
                    SourceIndex = zoneSource,
                    Max = max,
                    LowEdge = max / 3,
                    MidEdge = max * 2 / 3,
                });
            }
        }
        else if (condition.Mask is { } conditionMask)
        {
            if (!maskIndexById.TryGetValue(conditionMask.Id, out maskIndex))
            {
                return null; // dangling mask reference
            }

            activator = conditionMask.Activator ?? new PadShift.Core.Profile.Activator();
        }
        else if (condition.Hardware is { ButtonId: { } } hardwareCondition)
        {
            long buttonId = hardwareCondition.ButtonId!.Value;
            if (buttonId is < 0 or > 255)
            {
                return null;
            }

            hardwareButton = (int)buttonId;
            // A bare hardware condition has no activator slot in the schema; treat it as a press.
            activator = new PadShift.Core.Profile.Activator { Type = ActivatorType.Start, Mode = ActivationMode.Onetime };
        }
        else
        {
            return null; // no source: nothing to listen to
        }

        if (IsZoneActivator(activator.Type) && zoneTracker < 0)
        {
            return null; // a zone activator without a modeled analog axis
        }

        ActivationMode mode = activator.Mode ?? ActivationMode.Onetime;
        ActivatorParams? p = activator.Params;

        long longWait = Clamp(p?.LongWaitTime ?? 500, 1, MaxWaitMs);
        long singleWait = Clamp(p?.SingleWaitTime ?? 250, 1, MaxWaitMs);
        long doubleWait = Clamp(p?.DoubleWaitTime ?? 250, 1, MaxWaitMs);
        long repeatPause = Clamp((p?.Pause ?? 0) * scale, 0, MaxPauseMs);
        long times = Clamp(p?.Times ?? 0, 0, 9999);
        long delay = Clamp((p?.Delay ?? 0) * scale, 0, MaxPauseMs);

        var mapping = new CompiledMapping
        {
            Layer = layer,
            MaskIndex = maskIndex,
            HardwareButton = hardwareButton,
            ZoneTracker = zoneTracker,
            ZoneIndex = zoneIndex,
            Type = activator.Type,
            Mode = mode,
            RepeatPause = repeatPause,
            Times = times,
            Unmap = rule.Hardware?.Unmap == true,
        };

        mapping.Program = CompileProgram(rule, mapping.Mode, scale, delay);
        ParseJump(rule, mapping);

        if (zoneTracker >= 0)
        {
            return mapping; // zone state is shared per axis, not the per-button gesture group
        }

        var key = (layer, maskIndex, hardwareButton);
        if (!groupIndex.TryGetValue(key, out int group))
        {
            group = groups.Count;
            groupIndex[key] = group;
            groups.Add(new ActivationGroup
            {
                Layer = layer,
                MaskIndex = maskIndex,
                HardwareButton = hardwareButton,
                LongWait = longWait,
                SingleWait = singleWait,
                DoubleWait = doubleWait,
            });
        }

        ActivationGroup g = groups[group];
        g.LongWait = Math.Min(g.LongWait, longWait);
        g.SingleWait = Math.Min(g.SingleWait, singleWait);
        g.DoubleWait = Math.Min(g.DoubleWait, doubleWait);
        switch (activator.Type)
        {
            case ActivatorType.Single: g.HasSingle = true; break;
            case ActivatorType.Long: g.HasLong = true; break;
            case ActivatorType.Double: g.HasDouble = true; break;
            case ActivatorType.Triple: g.HasTriple = true; break;
            case ActivatorType.Start: g.HasStart = true; break;
            case ActivatorType.Release: g.HasRelease = true; break;
        }

        mapping.Group = group;
        return mapping;
    }

    private static bool IsZoneActivator(ActivatorType type) =>
        type is ActivatorType.ZoneLow or ActivatorType.ZoneMed or ActivatorType.ZoneHigh;

    private static bool TryGetZoneActivator(
        MappingRule rule,
        MappingCondition condition,
        ConditionHardware hardware,
        out PadShift.Core.Profile.Activator activator)
    {
        if (condition.Mask?.Activator is { } maskActivator && IsZoneActivator(maskActivator.Type))
        {
            activator = maskActivator;
            return true;
        }

        if (TryReadActivator(hardware.Extra, out activator) && IsZoneActivator(activator.Type))
        {
            return true;
        }

        if (TryReadActivator(condition.Extra, out activator) && IsZoneActivator(activator.Type))
        {
            return true;
        }

        if (TryReadActivator(rule.Extra, out activator) && IsZoneActivator(activator.Type))
        {
            return true;
        }

        activator = new PadShift.Core.Profile.Activator();
        return false;
    }

    private static bool TryReadActivator(
        Dictionary<string, JsonElement>? extra,
        out PadShift.Core.Profile.Activator activator)
    {
        activator = new PadShift.Core.Profile.Activator();
        if (extra is null)
        {
            return false;
        }

        foreach (KeyValuePair<string, JsonElement> pair in extra)
        {
            if (!string.Equals(pair.Key, "activator", StringComparison.OrdinalIgnoreCase)
                || pair.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<PadShift.Core.Profile.Activator>(pair.Value, ProfileIo.Options);
                if (parsed is not null)
                {
                    activator = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // A malformed activator extension is ignored like every other malformed field.
            }

            return false;
        }

        return false;
    }

    private static bool TryResolveZoneSource(ConditionHardware hardware, out bool isStick, out int sourceIndex)
    {
        isStick = false;
        sourceIndex = -1;

        if (hardware.Trigger is { } side)
        {
            sourceIndex = side switch
            {
                SideKind.Left => 4,
                SideKind.Right => 5,
                _ => -1,
            };
            return sourceIndex >= 0;
        }

        if (hardware.Stick is { } stick)
        {
            isStick = true;
            sourceIndex = stick switch
            {
                StickKind.Left => 0,
                StickKind.Right => 1,
                _ => -1,
            };
            return sourceIndex >= 0;
        }

        return false;
    }

    private static int ResolveHighZoneMax(
        GamepadProfile profile,
        MappingRule rule,
        MappingCondition condition,
        ConditionHardware hardware,
        PadShift.Core.Profile.Activator activator)
    {
        if (TryReadHighZoneMax(hardware.Extra, out int max)
            || TryReadHighZoneMax(condition.Extra, out max)
            || TryReadHighZoneMax(activator.Params?.Extra, out max)
            || TryReadHighZoneMax(activator.Extra, out max)
            || TryReadHighZoneMax(rule.Extra, out max)
            || TryReadDeviceHighZoneMax(profile, hardware, out max))
        {
            return Math.Clamp(max, 1, DefaultZoneMax);
        }

        return DefaultZoneMax;
    }

    /// <summary>
    /// docs/04 §7.1：<c>highZoneMax</c> 属于 <c>devices.hardware[].default.triggers[]</c>。
    /// 条件里没有显式覆盖时读设备级设置（按设备 id 定位，找不到则用第一台设备）。
    /// </summary>
    private static bool TryReadDeviceHighZoneMax(
        GamepadProfile profile,
        ConditionHardware hardware,
        out int max)
    {
        max = 0;
        if (hardware.Trigger is not { } side || side is not (SideKind.Left or SideKind.Right))
        {
            return false;
        }

        List<HardwareDevice>? devices = profile.Devices.Hardware;
        if (devices is null || devices.Count == 0)
        {
            return false;
        }

        HardwareDevice? device = null;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i] is { } candidate && candidate.Id == hardware.DeviceId)
            {
                device = candidate;
                break;
            }
        }

        device ??= devices[0];
        if (device?.Default is not { } defaults || defaults.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!defaults.TryGetProperty("triggers", out JsonElement triggers)
            || triggers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string expected = side == SideKind.Left ? "left" : "right";
        int position = side == SideKind.Left ? 0 : 1;
        int index = 0;
        foreach (JsonElement trigger in triggers.EnumerateArray())
        {
            if (trigger.ValueKind == JsonValueKind.Object)
            {
                string? name = trigger.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                bool match = string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)
                    || (name is null && index == position);

                if (match
                    && trigger.TryGetProperty("highZoneMax", out JsonElement value)
                    && value.TryGetInt32(out max))
                {
                    return true;
                }
            }

            index++;
        }

        return false;
    }

    private static bool TryReadHighZoneMax(Dictionary<string, JsonElement>? extra, out int max)
    {
        max = 0;
        if (extra is null)
        {
            return false;
        }

        foreach (KeyValuePair<string, JsonElement> pair in extra)
        {
            if (!string.Equals(pair.Key, "highZoneMax", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value.ValueKind == JsonValueKind.Number && pair.Value.TryGetInt32(out max))
            {
                return true;
            }

            if (pair.Value.ValueKind == JsonValueKind.String
                && int.TryParse(pair.Value.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out max))
            {
                return true;
            }

            return false;
        }

        return false;
    }

    private static void ParseJump(MappingRule rule, CompiledMapping mapping)
    {
        if (rule.JumpToLayer is not { } jump || jump.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!jump.TryGetProperty("layer", out JsonElement layerElement) || layerElement.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (!layerElement.TryGetInt64(out long target) || target is < 0 or > 19)
        {
            // 20..69 / 70..109 (mapping-lock) / 110..113 (jump-exec markers) are not modeled.
            return;
        }

        long delay = 0;
        if (jump.TryGetProperty("delay", out JsonElement delayElement) && delayElement.ValueKind == JsonValueKind.Number)
        {
            delayElement.TryGetInt64(out delay);
        }

        mapping.JumpLayer = (int)target;
        mapping.JumpDelay = Clamp(delay, 0, MaxJumpDelayMs);
    }

    private static MacroProgram CompileProgram(MappingRule rule, ActivationMode mode, long scale, long initialDelay)
    {
        var steps = new List<MacroStep>(8);
        bool bareHeld = mode is ActivationMode.HoldUntilRelease or ActivationMode.Toggle;

        foreach (MacroElement? element in rule.Macros ?? new List<MacroElement>())
        {
            if (element is null)
            {
                continue;
            }

            if (element.Pause is { } pause)
            {
                long value = pause.Value;
                if (value <= 0)
                {
                    continue; // pause.value == 0 is dropped
                }

                int merged = (int)Clamp(value * scale, 1, MaxPauseMs);
                if (steps.Count > 0 && steps[^1].Kind == StepKind.Pause)
                {
                    // consecutive pauses merge by sum
                    long sum = (long)steps[^1].Value + merged;
                    steps[^1] = new MacroStep(StepKind.Pause, value: (int)Clamp(sum, 1, MaxPauseMs));
                }
                else
                {
                    steps.Add(new MacroStep(StepKind.Pause, value: merged));
                }

                continue;
            }

            if (element.Break is { } breaker && breaker.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                steps.Add(new MacroStep(StepKind.Break));
                continue;
            }

            if (element.Keyboard is { } keyboard)
            {
                AddKeyboardSteps(steps, keyboard, bareHeld);
                continue;
            }

            if (element.Mouse is { } mouse)
            {
                AddMouseSteps(steps, mouse, bareHeld);
                continue;
            }

            if (element.Gamepad is { } gamepad)
            {
                AddGamepadSteps(steps, gamepad, bareHeld);
                continue;
            }

            // userCommand / scroll / rumble / unknown keys are intentionally unsupported.
        }

        if (steps.Count == 0 && rule.Hardware is { } hardware)
        {
            AddHardwareSteps(steps, hardware, bareHeld);
        }

        if (initialDelay > 0)
        {
            steps.Insert(0, new MacroStep(StepKind.Pause, value: (int)initialDelay));
        }

        return new MacroProgram { Steps = steps.ToArray(), Count = steps.Count };
    }

    private static void AddKeyboardSteps(List<MacroStep> steps, KeyboardOutput keyboard, bool bareHeld)
    {
        // Profile keyboard.buttonId is a DIK scan code; the output intent is indexed by the
        // Set-1 make code with bit 0x80 marking E0-prefixed keys.
        int code = ToKeyboardIndex(keyboard.ButtonId);
        if (code < 0)
        {
            return;
        }

        switch (ParseAction(keyboard.Action))
        {
            case ActionKind.Down:
                steps.Add(new MacroStep(StepKind.KeyDown, code));
                break;
            case ActionKind.Up:
                steps.Add(new MacroStep(StepKind.KeyUp, code));
                break;
            case ActionKind.SingleDown:
                steps.Add(new MacroStep(StepKind.KeyDown, code));
                steps.Add(new MacroStep(StepKind.KeyUp, code));
                break;
            default:
                steps.Add(new MacroStep(StepKind.KeyDown, code));
                if (!bareHeld)
                {
                    steps.Add(new MacroStep(StepKind.KeyUp, code));
                }

                break;
        }
    }

    private static void AddMouseSteps(List<MacroStep> steps, MouseOutput mouse, bool bareHeld)
    {
        if (mouse.ButtonId is { } buttonId)
        {
            int bit = BitForMouseButton((int)buttonId);
            if (bit != 0)
            {
                switch (ParseAction(mouse.Action))
                {
                    case ActionKind.Up:
                        steps.Add(new MacroStep(StepKind.MouseUp, bit));
                        break;
                    case ActionKind.SingleDown:
                        steps.Add(new MacroStep(StepKind.MouseDown, bit));
                        steps.Add(new MacroStep(StepKind.MouseUp, bit));
                        break;
                    default:
                        steps.Add(new MacroStep(StepKind.MouseDown, bit));
                        if (!bareHeld)
                        {
                            steps.Add(new MacroStep(StepKind.MouseUp, bit));
                        }

                        break;
                }

                return;
            }
        }

        int amount = (int)Clamp(mouse.Value ?? 8, 1, 32767);
        if (mouse.Direction is { } directionElement && directionElement.ValueKind == JsonValueKind.String)
        {
            string? text = directionElement.GetString();
            int dx = 0;
            int dy = 0;
            if (Is(text, "up")) dy = -amount;
            else if (Is(text, "down")) dy = amount;
            else if (Is(text, "left")) dx = -amount;
            else if (Is(text, "right")) dx = amount;
            if (dx != 0 || dy != 0)
            {
                steps.Add(new MacroStep(StepKind.MouseMove, dx, dy));
                return;
            }
        }

        if (mouse.Wheel is { } wheelElement && wheelElement.ValueKind == JsonValueKind.String)
        {
            string? text = wheelElement.GetString();
            int notches = (int)Clamp(mouse.Value ?? 1, 1, 10);
            if (Is(text, "up")) steps.Add(new MacroStep(StepKind.Wheel, WheelDelta * notches));
            else if (Is(text, "down")) steps.Add(new MacroStep(StepKind.Wheel, -WheelDelta * notches));
        }
    }

    private static int ToKeyboardIndex(long dik)
    {
        int code = (int)dik;
        if (code is < 0 or > 255)
        {
            return -1;
        }

        return Dik.IsExtended(code) ? code | ScanCodeExtendedFlag : code;
    }

    private static void AddGamepadSteps(List<MacroStep> steps, GamepadOutput gamepad, bool bareHeld)
    {
        if (gamepad.Absolute == true)
        {
            int axis = (int)gamepad.ButtonId;
            if (axis is < 0 or > 5)
            {
                return; // the v2.6 wire protocol stick-direction ids (33..60) are not modeled
            }

            int value = (int)Clamp(gamepad.Value ?? 0, axis >= 4 ? 0 : -32768, axis >= 4 ? 255 : 32767);
            steps.Add(new MacroStep(StepKind.PadAxis, axis, value: value));
            if (!bareHeld)
            {
                steps.Add(new MacroStep(StepKind.PadAxis, axis, value: 0));
            }

            return;
        }

        int button = (int)gamepad.ButtonId;
        if (button is < 0 or > 31)
        {
            return;
        }

        switch (ParseAction(gamepad.Action))
        {
            case ActionKind.Up:
                steps.Add(new MacroStep(StepKind.PadUp, button));
                break;
            case ActionKind.SingleDown:
                steps.Add(new MacroStep(StepKind.PadDown, button));
                steps.Add(new MacroStep(StepKind.PadUp, button));
                break;
            default:
                steps.Add(new MacroStep(StepKind.PadDown, button));
                if (!bareHeld)
                {
                    steps.Add(new MacroStep(StepKind.PadUp, button));
                }

                break;
        }
    }

    private static void AddHardwareSteps(List<MacroStep> steps, MappingHardware hardware, bool bareHeld)
    {
        if (hardware.ButtonId is { } buttonId)
        {
            int button = (int)buttonId;
            if (button is < 0 or > 31)
            {
                return;
            }

            steps.Add(new MacroStep(StepKind.PadDown, button));
            if (!bareHeld)
            {
                steps.Add(new MacroStep(StepKind.PadUp, button));
            }

            return;
        }

        int axis = AxisFor(hardware.Stick, hardware.Trigger, hardware.Axis);
        if (axis < 0)
        {
            return;
        }

        int value = axis >= 4 ? 255 : 32767;
        if (hardware.Inverted == true)
        {
            value = axis >= 4 ? 0 : -32768;
        }

        steps.Add(new MacroStep(StepKind.PadAxis, axis, value: value));
        if (!bareHeld)
        {
            steps.Add(new MacroStep(StepKind.PadAxis, axis, value: 0));
        }
    }

    private void BuildConsumption(GamepadProfile profile)
    {
        long device = _options.DeviceId;
        bool anyDevice = device <= 0;

        for (int i = 0; i < _masks.Length; i++)
        {
            MarkConsumed(_masks[i].Keys, device, anyDevice);
        }

        for (int i = 0; i < _shifts.Length; i++)
        {
            MarkConsumed(_shifts[i].Keys, device, anyDevice);
        }

        foreach (MappingRule? rule in profile.Mappings ?? new List<MappingRule>())
        {
            if (rule?.Condition?.Hardware is not { } hardware)
            {
                continue;
            }

            if (hardware.ButtonId is not { } buttonId || (hardware.DeviceId != device && !anyDevice))
            {
                continue;
            }

            if (buttonId is >= 0 and < 256)
            {
                _consumed[(int)buttonId] = true;
            }

            if (rule.Hardware is { Inverted: true } || rule.Hardware is { Stick: not null } || rule.Hardware is { Trigger: not null })
            {
                int axis = AxisFor(hardware.Stick, hardware.Trigger, hardware.Axis);
                if (axis >= 0)
                {
                    _invertAxis[axis] = rule.Hardware?.Inverted == true;
                }
            }
        }
    }

    private void MarkConsumed(ButtonKey[] keys, long device, bool anyDevice)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            ButtonKey key = keys[i];
            if (key.ButtonId < 0 || (key.DeviceId != device && !anyDevice))
            {
                continue;
            }

            _consumed[key.ButtonId] = true;
        }
    }

    private static ButtonKey[] CompileKeys(List<ButtonRef>? set)
    {
        if (set is null || set.Count == 0)
        {
            return Array.Empty<ButtonKey>();
        }

        var keys = new List<ButtonKey>(set.Count);
        foreach (ButtonRef? reference in set)
        {
            if (reference is null)
            {
                continue;
            }

            keys.Add(new ButtonKey(reference.DeviceId, reference.ButtonId));
        }

        return keys.ToArray();
    }

    // ------------------------------------------------------------------ tick

    private void UpdateShifts(ControllerState state)
    {
        _unlockAll = false;
        _unlockKeyboard = false;
        _unlockMouse = false;

        int holdLayer = 0;
        for (int i = 0; i < _shifts.Length; i++)
        {
            CompiledShift shift = _shifts[i];
            bool held = shift.Keys.Length > 0 && AllHeld(shift.Keys, state);
            bool rising = held && !shift.Held;
            shift.Held = held;

            switch (shift.Type)
            {
                case ShiftType.Default:
                    if (held)
                    {
                        holdLayer = shift.Layer;
                    }

                    break;

                case ShiftType.Toggle:
                    if (rising)
                    {
                        shift.Toggled = !shift.Toggled;
                        if (shift.Toggled)
                        {
                            ClearOtherToggles(shift.Layer);
                            _activeLayer = shift.Layer;
                            _layerReason = ReasonToggle;
                        }
                        else if (_activeLayer == shift.Layer)
                        {
                            _activeLayer = 0;
                            _layerReason = ReasonToggle;
                        }
                    }

                    break;

                case ShiftType.Unlock:
                    if (held) _unlockAll = true;
                    break;

                case ShiftType.UnlockKeyboardOnly:
                    if (held) _unlockKeyboard = true;
                    break;

                case ShiftType.UnlockMouseOnly:
                    if (held) _unlockMouse = true;
                    break;
            }
        }

        if (holdLayer != 0)
        {
            if (_holdLayer != holdLayer)
            {
                _layerReason = ReasonHold;
            }

            _activeLayer = holdLayer;
        }
        else if (_holdLayer != 0)
        {
            _activeLayer = 0;
            _layerReason = ReasonRelease;
        }

        _holdLayer = holdLayer;
        _suppressedMasks = null;
        CompiledShift? current = FindShift(CurrentLayer);
        if (current?.Suppressed is { } suppressed)
        {
            _suppressedMasks = suppressed;
        }

        FlushLayerEvent();
    }

    private void UpdateMasks(ControllerState state)
    {
        for (int i = 0; i < _masks.Length; i++)
        {
            CompiledMask mask = _masks[i];
            bool raw = AllHeld(mask.Keys, state);
            bool suppressed = _suppressedMasks is { } table && i < table.Length && table[i];
            mask.PrevActive = mask.Active;
            mask.RawActive = raw;
            mask.Active = raw && !suppressed;
        }
    }

    /// <summary>
    /// 模拟轴分区采样。每个 (layer, 轴, highZoneMax) 只有一个当前区；相邻区之间要求越过
    /// ±<see cref="ZoneHysteresis"/> 才切换，因此边界附近的抖动不会让 low/med/high 反复触发。
    /// </summary>
    private void UpdateZones(ControllerState state)
    {
        for (int i = 0; i < _zones.Length; i++)
        {
            ZoneTracker zone = _zones[i];
            if (zone.Layer >= 0 && zone.Layer != CurrentLayer)
            {
                // 不在本层：清空状态，回到本层时重新按当前值判定并触发一次。
                zone.Eligible = false;
                zone.Changed = false;
                zone.Current = -1;
                continue;
            }

            zone.Eligible = true;
            int value = ReadZoneValue(zone, state);
            int raw = value < zone.LowEdge ? 0 : value < zone.MidEdge ? 1 : 2;
            int current = zone.Current;

            if (current < 0)
            {
                current = raw;
                zone.Changed = true;
            }
            else if (raw > current)
            {
                while (current < 2 && value >= zone.UpperEdge(current) + ZoneHysteresis)
                {
                    current++;
                }

                zone.Changed = current != zone.Current;
            }
            else if (raw < current)
            {
                while (current > 0 && value < zone.LowerEdge(current) - ZoneHysteresis)
                {
                    current--;
                }

                zone.Changed = current != zone.Current;
            }
            else
            {
                zone.Changed = false;
            }

            zone.Current = current;
        }
    }

    private static int ReadZoneValue(ZoneTracker zone, ControllerState state)
    {
        int[] axes = state.Axes;
        if (!zone.IsStick)
        {
            if (zone.SourceIndex >= axes.Length)
            {
                return 0;
            }

            int raw = axes[zone.SourceIndex];
            if (raw <= 0)
            {
                return 0;
            }

            // 触发器按 0..32767 归一化；16 位源的 0..65535 折半到同一量程。
            return raw > DefaultZoneMax ? raw >> 1 : raw;
        }

        int xIndex = zone.SourceIndex == 0 ? 0 : 2;
        int yIndex = xIndex + 1;
        if (yIndex >= axes.Length)
        {
            return 0;
        }

        int x = axes[xIndex];
        int y = axes[yIndex];

        // sticks 用径向幅值（斜向也不会漏掉 high 区），并钳到 0..32767。
        long squared = ((long)x * x) + ((long)y * y);
        int magnitude = (int)Math.Sqrt(squared);
        return magnitude > DefaultZoneMax ? DefaultZoneMax : magnitude;
    }

    private void UpdateGroups(ControllerState state)
    {
        for (int i = 0; i < _groups.Length; i++)
        {
            ActivationGroup group = _groups[i];
            bool eligible = group.Layer < 0 || group.Layer == CurrentLayer;
            group.Eligible = eligible;

            bool pressed;
            if (!eligible)
            {
                pressed = false;
            }
            else if (group.MaskIndex >= 0)
            {
                pressed = _masks[group.MaskIndex].Active;
            }
            else if (group.HardwareButton >= 0)
            {
                pressed = state.Buttons[group.HardwareButton];
            }
            else
            {
                pressed = false;
            }

            AdvanceGroup(group, pressed, _now);
        }
    }

    private static void AdvanceGroup(ActivationGroup g, bool pressed, long now)
    {
        g.FireMask = 0;

        if (!g.Eligible)
        {
            ResetGroup(g);
            return;
        }

        // The inter-press window must expire before new edges are interpreted. A press that
        // lands exactly on the deadline still belongs to the window.
        if (g.Phase == GroupPhase.Waiting && (now > g.Deadline || (now == g.Deadline && !pressed)))
        {
            ExpireWaiting(g);
        }

        bool pressEdge = pressed && !g.Pressed;
        bool releaseEdge = !pressed && g.Pressed;
        g.Pressed = pressed;

        if (pressEdge)
        {
            if (g.HasStart)
            {
                Fire(g, ActivatorType.Start);
            }

            if (g.Phase == GroupPhase.Waiting && now <= g.Deadline)
            {
                g.PressCount++;
                g.PressStart = now;
                g.LongFired = false;
                g.SingleFired = false;
                g.Phase = GroupPhase.Down;

                if (g.PressCount >= 3)
                {
                    if (g.HasTriple)
                    {
                        Fire(g, ActivatorType.Triple);
                        g.Phase = GroupPhase.Consumed;
                    }
                    else if (g.HasDouble)
                    {
                        Fire(g, ActivatorType.Double);
                        g.Phase = GroupPhase.Consumed;
                    }
                    else
                    {
                        g.PressCount = 1;
                    }
                }
                else if (g.PressCount == 2)
                {
                    if (g.HasTriple)
                    {
                        // triple takes precedence: wait for release, then for a third press
                    }
                    else if (g.HasDouble)
                    {
                        Fire(g, ActivatorType.Double);
                        g.Phase = GroupPhase.Consumed;
                    }
                    else
                    {
                        g.PressCount = 1;
                    }
                }
            }
            else
            {
                g.PressCount = 1;
                g.PressStart = now;
                g.LongFired = false;
                g.SingleFired = false;
                g.Phase = GroupPhase.Down;
            }
        }
        else if (releaseEdge && g.Phase == GroupPhase.Down)
        {
            if (g.HasRelease)
            {
                Fire(g, ActivatorType.Release);
            }

            if (g.PressCount == 1 && (g.HasDouble || g.HasTriple) && !g.SingleFired)
            {
                g.Phase = GroupPhase.Waiting;
                g.Deadline = now + g.DoubleWait;
            }
            else if (g.PressCount == 2 && (g.HasDouble || g.HasTriple))
            {
                g.Phase = GroupPhase.Waiting;
                g.Deadline = now + g.DoubleWait;
            }
            else
            {
                if (g.PressCount == 1 && g.HasSingle && !g.SingleFired)
                {
                    Fire(g, ActivatorType.Single);
                }

                g.Phase = GroupPhase.Idle;
                g.PressCount = 0;
            }
        }
        else if (releaseEdge && g.Phase == GroupPhase.Consumed)
        {
            if (g.HasRelease)
            {
                Fire(g, ActivatorType.Release);
            }

            g.Phase = GroupPhase.Idle;
            g.PressCount = 0;
        }

        if (g.Phase == GroupPhase.Down && g.Pressed)
        {
            if (g.HasLong && !g.LongFired && now - g.PressStart >= g.LongWait)
            {
                Fire(g, ActivatorType.Long);
                g.LongFired = true;
                g.SingleFired = true;
            }
            else if (g.HasSingle && !g.SingleFired && !g.LongFired && now - g.PressStart >= g.SingleWait)
            {
                // "single fires on press after singlewaittime"
                Fire(g, ActivatorType.Single);
                g.SingleFired = true;
            }
        }
    }

    private static void ExpireWaiting(ActivationGroup g)
    {
        if (g.PressCount >= 2)
        {
            if (g.HasDouble)
            {
                Fire(g, ActivatorType.Double);
            }
        }
        else if (g.PressCount == 1 && g.HasSingle && !g.SingleFired)
        {
            Fire(g, ActivatorType.Single);
        }

        g.Phase = GroupPhase.Idle;
        g.PressCount = 0;
    }

    private static void Fire(ActivationGroup g, ActivatorType type)
    {
        g.FireMask |= 1 << (int)type;
    }

    private static void ResetGroup(ActivationGroup g)
    {
        g.Pressed = false;
        g.Phase = GroupPhase.Idle;
        g.PressCount = 0;
        g.LongFired = false;
        g.SingleFired = false;
        g.PressStart = 0;
        g.Deadline = 0;
    }

    private void UpdateMappings()
    {
        for (int i = 0; i < _mappings.Length; i++)
        {
            CompiledMapping mapping = _mappings[i];
            bool fired;
            bool pressed;

            if (mapping.ZoneTracker >= 0)
            {
                ZoneTracker zone = _zones[mapping.ZoneTracker];
                fired = zone.Eligible && zone.Changed && zone.Current == mapping.ZoneIndex;
                pressed = zone.Eligible && zone.Current == mapping.ZoneIndex;
            }
            else
            {
                ActivationGroup group = _groups[mapping.Group];
                fired = group.Eligible && (group.FireMask & (1 << (int)mapping.Type)) != 0;
                pressed = group.Eligible && group.Pressed;
            }

            // A finished tap keeps its held keys for one tick so "down, up" programs are visible.
            MacroRunner runner = mapping.Runner;
            if (runner.ReleaseAt != 0 && _now >= runner.ReleaseAt)
            {
                runner.ReleaseAt = 0;
                ClearHeld(runner);
            }

            if (fired && mapping.JumpLayer >= 0)
            {
                _pendingJumpLayer = mapping.JumpLayer;
                _pendingJumpAt = _now + mapping.JumpDelay;
            }

            switch (mapping.Mode)
            {
                case ActivationMode.HoldUntilRelease:
                    if (fired)
                    {
                        Begin(mapping);
                    }
                    else if (!pressed)
                    {
                        StopMapping(mapping);
                    }

                    break;

                case ActivationMode.Toggle:
                    if (fired)
                    {
                        ToggleMapping(mapping);
                    }

                    break;

                case ActivationMode.Repeat:
                case ActivationMode.Turbo:
                    if (fired && mapping.Runner.Phase is RunnerPhase.Idle or RunnerPhase.Done)
                    {
                        Begin(mapping);
                    }

                    if (!pressed)
                    {
                        mapping.Runner.StopRequested = true;
                    }

                    break;

                default:
                    if (fired)
                    {
                        Begin(mapping);
                    }

                    break;
            }

            RunMacro(mapping, pressed);
        }
    }

    private static void Begin(CompiledMapping mapping)
    {
        MacroRunner runner = mapping.Runner;
        ClearHeld(runner);
        runner.Step = 0;
        runner.PauseEndsAt = 0;
        runner.RestartAt = 0;
        runner.ReleaseAt = 0;
        runner.RepeatCount = 0;
        runner.StopRequested = false;
        runner.Phase = RunnerPhase.Running;
    }

    private static void ToggleMapping(CompiledMapping mapping)
    {
        MacroRunner runner = mapping.Runner;
        if (runner.ToggleOn)
        {
            runner.ToggleOn = false;
            ClearHeld(runner);
            runner.Phase = RunnerPhase.Idle;
            runner.Step = 0;
            runner.PauseEndsAt = 0;
            runner.ReleaseAt = 0;
        }
        else
        {
            Begin(mapping);
            runner.ToggleOn = true;
        }
    }

    private static void StopMapping(CompiledMapping mapping)
    {
        MacroRunner runner = mapping.Runner;
        ClearHeld(runner);
        runner.Step = 0;
        runner.PauseEndsAt = 0;
        runner.RestartAt = 0;
        runner.ReleaseAt = 0;
        runner.RepeatCount = 0;
        runner.StopRequested = false;
        runner.Phase = RunnerPhase.Idle;
    }

    private static void ClearHeld(MacroRunner runner)
    {
        Array.Clear(runner.HeldKeys);
        runner.HeldMouse = 0;
        runner.HeldPad = 0;
        Array.Clear(runner.AxisSet);
        runner.Axis[0] = runner.Axis[1] = runner.Axis[2] = 0;
        runner.Axis[3] = runner.Axis[4] = runner.Axis[5] = 0;
    }

    private void RunMacro(CompiledMapping mapping, bool pressed)
    {
        MacroRunner runner = mapping.Runner;

        if (runner.Phase == RunnerPhase.WaitingRestart)
        {
            if (_now < runner.RestartAt)
            {
                return;
            }

            if (!pressed && mapping.Mode is ActivationMode.Repeat or ActivationMode.Turbo)
            {
                StopMapping(mapping);
                return;
            }

            runner.Step = 0;
            runner.Phase = RunnerPhase.Running;
        }

        if (runner.Phase != RunnerPhase.Running)
        {
            return;
        }

        if (runner.PauseEndsAt != 0)
        {
            if (_now < runner.PauseEndsAt)
            {
                return;
            }

            runner.PauseEndsAt = 0;
        }

        MacroProgram program = mapping.Program;
        if (runner.Step >= program.Count)
        {
            CompleteCycle(mapping, pressed);
            return;
        }

        MacroStep step = program.Steps[runner.Step];
        switch (step.Kind)
        {
            case StepKind.Pause:
                runner.Step++;
                runner.PauseEndsAt = _now + Math.Max(1, step.Value);
                return;

            case StepKind.Break:
                // break: stop the loop, clear turbo/toggle state, reset the repeat counter to 1.
                runner.Step = program.Count;
                runner.RepeatCount = 1;
                runner.StopRequested = false;
                runner.ToggleOn = false;
                runner.ReleaseAt = 0;
                ClearHeld(runner);
                runner.Phase = RunnerPhase.Done;
                return;

            default:
                ApplyStep(mapping, step);
                runner.Step++;
                if (runner.Step >= program.Count)
                {
                    CompleteCycle(mapping, pressed);
                }

                return;
        }
    }

    private void CompleteCycle(CompiledMapping mapping, bool pressed)
    {
        MacroRunner runner = mapping.Runner;
        switch (mapping.Mode)
        {
            case ActivationMode.HoldUntilRelease:
            case ActivationMode.Toggle:
                runner.Phase = RunnerPhase.Done;
                break;

            case ActivationMode.Repeat:
            case ActivationMode.Turbo:
                runner.ReleaseAt = _now + 1;
                runner.RepeatCount++;
                if (runner.StopRequested || !pressed || (mapping.Times > 0 && runner.RepeatCount >= mapping.Times))
                {
                    runner.Phase = RunnerPhase.Done;
                    break;
                }

                runner.Phase = RunnerPhase.WaitingRestart;
                runner.RestartAt = _now + mapping.RepeatPause;
                break;

            default:
                runner.ReleaseAt = _now + 1;
                runner.Phase = RunnerPhase.Done;
                break;
        }
    }

    private void ApplyStep(CompiledMapping mapping, in MacroStep step)
    {
        MacroRunner runner = mapping.Runner;
        switch (step.Kind)
        {
            case StepKind.KeyDown:
                if ((uint)step.A < 256u)
                {
                    runner.HeldKeys[step.A >> 5] |= 1u << (step.A & 31);
                }

                break;

            case StepKind.KeyUp:
                if ((uint)step.A < 256u)
                {
                    runner.HeldKeys[step.A >> 5] &= ~(1u << (step.A & 31));
                }

                break;

            case StepKind.MouseDown:
                runner.HeldMouse |= step.A;
                break;

            case StepKind.MouseUp:
                runner.HeldMouse &= ~step.A;
                break;

            case StepKind.PadDown:
                if ((uint)step.A < 32u)
                {
                    runner.HeldPad |= 1u << step.A;
                }

                break;

            case StepKind.PadUp:
                if ((uint)step.A < 32u)
                {
                    runner.HeldPad &= ~(1u << step.A);
                }

                break;

            case StepKind.PadAxis:
                if ((uint)step.A < 6u)
                {
                    runner.Axis[step.A] = step.Value;
                    runner.AxisSet[step.A] = true;
                }

                break;

            case StepKind.MouseMove:
                _intent.MouseDeltaX += step.A;
                _intent.MouseDeltaY += step.B;
                break;

            case StepKind.Wheel:
                _intent.MouseWheel += step.A;
                break;
        }
    }

    private void UpdateJump(long now)
    {
        if (_pendingJumpLayer < 0 || now < _pendingJumpAt)
        {
            return;
        }

        int target = _pendingJumpLayer;
        _pendingJumpLayer = -1;
        _pendingJumpAt = 0;

        if (target is < 0 or > 19)
        {
            return; // reserved ranges (70..113) are documented as unsupported
        }

        _activeLayer = target == _defaultLayer ? 0 : target;
        _layerReason = ReasonJump;
        FlushLayerEvent();
    }

    private void FlushLayerEvent()
    {
        int layer = CurrentLayer;
        if (layer == _reportedLayer)
        {
            return;
        }

        _reportedLayer = layer;
        LayerChanged?.Invoke(layer, _layerReason);
    }

    private CompiledShift? FindShift(int layer)
    {
        if (layer == 0)
        {
            return null;
        }

        for (int i = 0; i < _shifts.Length; i++)
        {
            if (_shifts[i].Layer == layer)
            {
                return _shifts[i];
            }
        }

        return null;
    }

    private void ClearOtherToggles(int layer)
    {
        for (int i = 0; i < _shifts.Length; i++)
        {
            if (_shifts[i].Layer != layer)
            {
                _shifts[i].Toggled = false;
            }
        }
    }

    // ---------------------------------------------------------------- output

    private void WriteOutputs()
    {
        for (int i = 0; i < _mappings.Length; i++)
        {
            MacroRunner runner = _mappings[i].Runner;

            for (int word = 0; word < 8; word++)
            {
                uint bits = runner.HeldKeys[word];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    int code = (word << 5) + bit;
                    if (code < 256)
                    {
                        _intent.KeyboardKeys[code] = true;
                    }

                    bits &= bits - 1;
                }
            }

            _intent.MouseButtons |= runner.HeldMouse;

            uint pad = runner.HeldPad;
            while (pad != 0)
            {
                int bit = BitOperations.TrailingZeroCount(pad);
                if (bit < 32)
                {
                    _intent.PadButtons[bit] = true;
                }

                pad &= pad - 1;
            }

            for (int axis = 0; axis < 6; axis++)
            {
                if (!runner.AxisSet[axis])
                {
                    continue;
                }

                int value = runner.Axis[axis];
                switch (axis)
                {
                    case 0: _intent.PadLeftX = value; break;
                    case 1: _intent.PadLeftY = value; break;
                    case 2: _intent.PadRightX = value; break;
                    case 3: _intent.PadRightY = value; break;
                    case 4: _intent.PadLeftTrigger = (byte)Clamp(value, 0, 255); break;
                    case 5: _intent.PadRightTrigger = (byte)Clamp(value, 0, 255); break;
                }
            }
        }
    }

    private void ApplyOutputGates()
    {
        if (_unlockKeyboard)
        {
            Array.Clear(_intent.KeyboardKeys);
        }

        if (_unlockMouse)
        {
            _intent.MouseButtons = 0;
            _intent.MouseDeltaX = 0;
            _intent.MouseDeltaY = 0;
            _intent.MouseWheel = 0;
        }
    }

    private void PassThrough(ControllerState state)
    {
        // Physical buttons with no remapping rule pass through to the virtual pad unchanged.
        for (int i = 0; i < ButtonIds.Max && i < state.Buttons.Length; i++)
        {
            if (state.Buttons[i] && !_consumed[i])
            {
                _intent.PadButtons[i] = true;
            }
        }

        int lx = AxisValue(state.Axes, 0, _invertAxis[0]);
        int ly = AxisValue(state.Axes, 1, _invertAxis[1]);
        int rx = AxisValue(state.Axes, 2, _invertAxis[2]);
        int ry = AxisValue(state.Axes, 3, _invertAxis[3]);

        if (lx != 0 && _intent.PadLeftX == 0) _intent.PadLeftX = lx;
        if (ly != 0 && _intent.PadLeftY == 0) _intent.PadLeftY = ly;
        if (rx != 0 && _intent.PadRightX == 0) _intent.PadRightX = rx;
        if (ry != 0 && _intent.PadRightY == 0) _intent.PadRightY = ry;

        if (_intent.PadLeftTrigger == 0)
        {
            _intent.PadLeftTrigger = ToTrigger(state.Axes, 4);
        }

        if (_intent.PadRightTrigger == 0)
        {
            _intent.PadRightTrigger = ToTrigger(state.Axes, 5);
        }
    }

    private static int AxisValue(int[] axes, int index, bool inverted)
    {
        if (index >= axes.Length)
        {
            return 0;
        }

        int value = axes[index];
        if (!inverted)
        {
            return value;
        }

        return value == -32768 ? 32767 : -value;
    }

    private static byte ToTrigger(int[] axes, int index)
    {
        if (index >= axes.Length)
        {
            return 0;
        }

        int raw = axes[index];
        if (raw <= 0)
        {
            return 0;
        }

        int scaled = raw > 255 ? raw >> 8 : raw;
        return (byte)(scaled > 255 ? 255 : scaled);
    }

    // --------------------------------------------------------------- helpers

    private static bool AllHeld(ButtonKey[] keys, ControllerState state)
    {
        if (keys.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            ButtonKey key = keys[i];
            if (key.ButtonId < 0 || key.ButtonId >= state.Buttons.Length)
            {
                return false;
            }

            if (!state.Buttons[key.ButtonId])
            {
                return false;
            }
        }

        return true;
    }

    private static int AxisFor(StickKind? stick, SideKind? trigger, AxisKind? axis)
    {
        if (trigger is { } side)
        {
            return side switch
            {
                SideKind.Left => 4,
                SideKind.Right => 5,
                _ => -1,
            };
        }

        if (stick is not { } kind)
        {
            return -1;
        }

        bool x = axis is null or AxisKind.X;
        return kind switch
        {
            StickKind.Left => x ? 0 : 1,
            StickKind.Right => x ? 2 : 3,
            _ => -1,
        };
    }

    private static int BitForMouseButton(int buttonId) => buttonId switch
    {
        1 => 0b00001,
        2 => 0b00010,
        3 => 0b00100,
        4 => 0b01000,
        5 => 0b10000,
        _ => 0,
    };

    private enum ActionKind
    {
        Auto,
        Down,
        Up,
        SingleDown,
    }

    private static ActionKind ParseAction(JsonElement? action)
    {
        if (action is not { } element || element.ValueKind != JsonValueKind.String)
        {
            return ActionKind.Auto;
        }

        string? text = element.GetString();
        if (Is(text, "down")) return ActionKind.Down;
        if (Is(text, "up")) return ActionKind.Up;
        if (Is(text, "single_down")) return ActionKind.SingleDown;
        return ActionKind.Auto;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static long Clamp(long value, long min, long max) =>
        value < min ? min : value > max ? max : value;
}
