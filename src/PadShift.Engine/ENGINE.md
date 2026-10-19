# PadShift.Engine — design notes

Deterministic core: `Tick(state, nowMs)` never reads a clock, never spawns threads, never allocates
(verified by `EngineMacroTests.Tick_Does_Not_Allocate`) and never throws on malformed profiles
(`LoadProfile` compiles per-rule with try/catch and drops what it cannot parse). The host façade
owns time, devices and I/O.

## Pipeline order (one `Tick`)

1. `OutputIntent.Clear()` — the same intent instance is reused every frame.
2. **Shifts**: evaluate `shifts[].set[]`; toggle on rising edge, `default` while held, `unlock*` flags;
   select one non-base layer. `unheritableMasks` of the current layer suppress those masks.
3. **Masks**: `masks[].set[]` is active when every `(deviceId, buttonId)` is held on the polled state.
4. **Gestures**: one detector per source group = declared layer + mask id (or hardware button).
5. **Mappings**: layer match (`shiftId` equal or absent) + gesture fire → mode runner; `jumpToLayer`
   is scheduled (delay honoured).
6. **Macros**: execute at most one non-pause step per tick; pauses consume real time.
7. **Outputs**: held keyboard scan codes, mouse bits/deltas/wheel, pad buttons, pad axes/triggers.
8. **Gates**: `unlockKeyboardOnly` / `unlockMouseOnly` clear the corresponding output fields.
9. **Passthrough**: physical buttons not consumed by any rule, plus stick/trigger axes.

## Activator state machine (per group)

Phases: `Idle → Down → Waiting (inter-press window) → Consumed`.

* `start` fires on every press edge.
* `long` fires once when held ≥ `longwaittime`, and consumes the press (no `single` afterwards).
* `single` fires at `singlewaittime` while held, or on release when released early; when `double`/
  `triple` exist on the same group it is deferred to the end of the inter-press window.
* `double` fires on the 2nd press inside the window; `triple` on the 3rd. With both configured the
  double is deferred until the triple window ends, so a triple never also fires double.
* `release` fires on every release edge after a press.
* Windows use `doublewaittime`; a press landing exactly on the deadline still belongs to the window.
* **Mutual exclusion is group-scoped**: single/double/triple/long of the same mask in the same layer
  share one detector. The same mask bound in another layer is a different group and is independent.

## Modes

* `hold_until_release` — program starts on fire, held outputs are released when the source releases.
* `onetime` (schema default when `mode` is absent) — tap: a bare element becomes down, then up on the
  next tick; explicit `down`/`up` pairs run in order across ticks.
* `repeat` / `turbo` — same engine path: each cycle is one program run, then `params.pause` gap;
  `times` absent/0 = infinite; stops after the current cycle when the source releases.
* `toggle` — each fire flips held state; turning on runs the program and keeps its held outputs,
  turning off clears them.
* A `break` step clears held outputs and turbo/toggle state, sets the repeat counter to 1, and ends
  the program.
* Bare keyboard/mouse/pad elements are *held* in `hold`/`toggle` and *pulsed* (down+up) in the other
  modes. `mapping.hardware` becomes a synthetic program when `macros` is empty.

## Schema decisions (ambiguities resolved)

* `condition.shiftId` absent matches **any** layer (per the task spec); explicit `0` is the base layer.
* A `default` shift without `set[]` is the always-on main layer. Real `gamepad-to-kbm.rewasd` relies
  on this (all its mappings are `shiftId: 1` while that shift has no trigger).
* `config.minTimeUnit != 1` **or absent** multiplies `params.pause`, `params.delay` and macro pause
  values by the hardcoded 10; gesture wait times are not scaled.
* `jumpToLayer` targets 0..19 are applied. Reserved ranges 20..69, 70..109 (mapping-lock) and
  110..113 (jump-exec markers) are ignored; `postpone` is not modelled.
* `hardware.unmap` and any mask/hardware source consume the physical button (scoped by
  `RemapOptions.DeviceId`); all other physical buttons pass through untouched.
* Absolute gamepad macros use `buttonId` 0..5 as a Core axis index; the reference 33..60 stick-direction
  id space is not modelled.
* Keyboard `buttonId` is a DIK value and is converted to the output scan-code index (Set-1 make code,
  bit 0x80 = E0 prefix). Mouse wheel is in `WHEEL_DELTA` (120) units.
* With `single` and `long` both configured, a hold fires single at `singlewaittime` and long at
  `longwaittime` (literal task semantics).

## Intentionally unsupported

* `userCommand`, `scroll`, `rumble`, touch-swipe/zoom macro elements and `mouseRegion`.
* Mapping-lock layers 70..109, jump-exec markers 110..113, radial menus, crosshairs, LEDs.
* Multi-device states: one `ControllerState` per engine. `deviceId` is kept in every compiled key but
  all masks read the polled state; the host is expected to run one engine per physical controller.
* Key repeat / pulsation, adaptive triggers, virtual-pad device selection (output layer concern).

## Analog zones (`zone_low` / `zone_med` / `zone_high`)

* A zone mapping reads `condition.hardware.trigger` (left/right) or `.stick` (left/right), not a mask/button. The activator is `condition.mask.activator` when its type is a zone, or an `"activator"` object in hardware/condition/mapping extension data (the typed schema only puts activators under `condition.mask`).
* Trigger value is normalized 0..32767 (16-bit sources halved); sticks use radial magnitude `sqrt(x²+y²)`, clamped to 32767 (`axis` is irrelevant for zones).
* Bands: `[0,max/3)`, `[max/3,2max/3)`, `[2max/3,max]`; `max = highZoneMax` when present (clamped 1..32767), else 32767. `highZoneMax` is read from extension data on the hardware condition, condition, activator params/activator or mapping (first wins).
* Level-triggered: the zone stays "pressed" inside its band and the mapping's `mode` runner runs on zone entry/exit.
* Hysteresis: upward transitions need `value >= upperEdge + 300`, downward `value < lowerEdge - 300`. low/med/high share one tracker per axis/layer, so only one zone is active and boundary jitter cannot double-fire.
