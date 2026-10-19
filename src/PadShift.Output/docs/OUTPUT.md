# PadShift output layer

This folder implements the output half of PadShift: it takes an `OutputIntent` from the engine and
turns it into real keyboard/mouse events and/or an emulated gamepad. It is a clean-room
implementation; it contains no the v2.6 wire protocol code.

```
PadShift.Engine
      │  OutputIntent (one per poll tick)
      ▼
CompositeSink ──────────────┬───────────────────────────────┐
      │                     │                               │
      ▼                     ▼                               ▼
LayoutInjector        VirtualPadSink                 RumbleState
(SendInput /          (Xbox360PadSink /              (rumble from the
 Interception)         Ds4PadSink)                   emulated pad)
      │                     │                               ▲
      ▼                     ▼                               │
Windows input         ViGEmBus → XInput / HID         game output report
```

## Prerequisites

| Component | Required | Notes |
|---|---|---|
| Windows 10/11 x64 | yes | The output layer uses Win32 `SendInput` and ViGEmBus. |
| .NET 10 runtime | yes | The project targets `net10.0`. |
| ViGEmBus **1.22 or newer** | only for virtual pads | <https://github.com/ViGEm/ViGEmBus/releases>. Install, reboot, verify the `ViGEmBus` service is running. The client library (`Nefarius.ViGEm.Client`) is already a NuGet dependency. |
| Interception driver | optional | Only for the lower-latency `InterceptionInjector`. Driver installation needs an elevated prompt and a reboot; see below. PadShift itself never requires administrator rights. |

## Contract conventions

These are the conventions the engine must follow for `PadShift.Core.OutputIntent`:

* **`KeyboardKeys[256]`** is indexed by `PadShift.Output.Keyboard.ScanCode`, not by virtual-key
  code. The low 7 bits are the Windows Set 1 make code and bit `0x80`
  (`ScanCode.ExtendedFlag`) means "send with the E0 prefix". Example: `ScanCode.A == 0x1E`,
  `ScanCode.Numpad8 == 0x48`, `ScanCode.Up == 0xC8`, `ScanCode.NumpadEnter == 0x9C`.
  Pause/E1 keys cannot be represented and are intentionally absent.
* **`MouseButtons`** is a bitmask: bit 0 left, bit 1 right, bit 2 middle, bit 3 X1, bit 4 X2
  (`ScanCode.Mouse.*`).
* **`MouseDeltaX` / `MouseDeltaY`** are per-frame relative deltas in pixels. They are forwarded
  verbatim: the sink does not smooth, scale, accumulate or split them. Fractional-pixel
  accumulation belongs in the engine.
* **`MouseWheel`** is in raw `WHEEL_DELTA` units: `ScanCode.WheelDelta` (120) is one notch.
  Positive scrolls up/away from the user.
* **Pad axes** are ints. Sticks use the full `-32768..32767` range; triggers are `0..255` bytes.
  The Xbox sink passes sticks through (clamped to `short`). The DS4 sink converts sticks to its
  native unsigned-byte range (`(value >> 8) + 128`).
* **`RumbleLeft` / `RumbleRight` on the intent are ignored by the sinks.** A virtual pad cannot
  push rumble into a game; rumble travels the other way and is exposed through
  `CompositeSink.Rumble`. Those two intent fields can be used by the engine for other purposes
  without affecting the output layer.

## Backends

### `Keyboard/SendInputInjector` (default)

* Injects through `user32!SendInput`.
* Keyboard: scan-code path (`KEYEVENTF_SCANCODE`), `wVk = 0`, E0 keys additionally set
  `KEYEVENTF_EXTENDEDKEY`. This is layout-independent and what DirectInput/Raw Input games read.
* Mouse: relative move, vertical wheel, left/right/middle/X1/X2 buttons.
* One `SendInput` batch per `Submit` call; transitions from one frame are delivered together.
* Diffing lives in `LayoutInjector`: a held key emits one down and one up, never a down per frame.
  `ReleaseAll()` walks the tracked key/button state and lifts everything.
* No administrator rights. If the foreground window is elevated (UIPI), Windows silently drops
  injected input; the sink counts it in `FailedBatches`/`LastWin32Error` instead of throwing.
  Running PadShift at the same integrity level as the game fixes this.

### `Keyboard/InterceptionInjector` (optional fast path)

* P/Invokes `interception.dll` (`interception_create_context`, `interception_send`,
  `interception_set_filter`, `interception_destroy_context`, plus
  `interception_is_keyboard`/`interception_is_mouse` for predicates). The DLL is bound manually
  with `NativeLibrary` so the probe can fail cleanly.
* Enabled only when all of the following are true: `Interception.dll` is next to
  `PadShift.App.exe`, the exports bind, and `interception_create_context()` returns a context
  (i.e. the driver is installed and running). Otherwise `LayoutInjector.CreateDefault()` falls
  back to `SendInputInjector`.
* Injection uses the same Set 1 codes, with `KEY_E0` in the stroke state for extended keys;
  mouse strokes are relative moves, wheel, and the same five buttons.
* **Drop-in instructions** (the DLL is deliberately not bundled):
  1. Download the Interception release (or build it) from
     <https://github.com/oblitum/Interception>.
  2. Copy **`Interception.dll`** (x64 or x86 matching PadShift) next to `PadShift.App.exe`.
  3. From an elevated prompt run `install-interception.exe /install`, then reboot.
  4. Start PadShift. `LayoutInjector.CreateDefault()` will pick the Interception backend; remove
     the DLL to go back to SendInput without a rebuild.
* **Filters:** an inject-only context leaves receive filters at zero on purpose. The driver
  queues matching physical strokes for a context and stops forwarding them until that context
  pumps them, so setting filters without an `interception_wait`/`interception_receive` loop would
  freeze the real keyboard/mouse. `EnableCaptureFilters()` exists for a future capture loop and
  installs `KeyboardFilterAll` + `MouseFilterAll`; call it only when the caller pumps the context.

### `VirtualPad/VirtualPadSink` + `Xbox360PadSink` / `Ds4PadSink`

* `VirtualPadSink` is the sink contract: `Submit`, `ReleaseAll`, `Dispose`, plus
  `IsConnected`, `IsFaulted`, `LastError`. Submit/release never throw. A runtime driver failure
  sets `IsFaulted`/`LastError` and stops further bus traffic; recreate the sink to recover.
* Both sinks use `AutoSubmitReport = false` and diff the entire panel. A frame with no changes
  submits nothing; a frame with changes submits exactly **one** report.
* Construction is the only throwing operation. If ViGEmBus is missing (or the native client
  cannot load) the constructor throws `ViGEmNotInstalledException` **once** with an actionable
  message; `TryCreate` overloads return false instead. The caller decides whether to run
  keyboard/mouse-only, prompt for installation, or exit.
* **`Xbox360PadSink`** (`ViGEmClient.CreateXbox360Controller()` → `IXbox360Controller` +
  `IVirtualGamepad`):
  A/B/X/Y, bumpers, Back/Start, Guide, stick clicks, D-pad, both sticks, both triggers.
  Colour aliases Green/Red/Blue/Yellow are OR-ed with A/B/X/Y. Paddles and touchpad click have no
  Xbox 360 equivalent and are ignored.
* **`Ds4PadSink`** (`ViGEmClient.CreateDualShock4Controller()` → `IDualShock4Controller` +
  `IVirtualGamepad`):
  Cross/Circle/Square/Triangle, L1/R1, Share/Options, L3/R3, PS, touchpad click, D-pad (hat,
  including diagonals), both sticks, both triggers (analog slider plus digital L2/R2 bit).
  Rumble is delivered by the `FeedbackReceived` event (obsolete in ViGEm.NET but still the only
  managed callback; the alternative is a blocking `AwaitRawOutputReport()` thread).
* **DS4 touchpad passthrough** is opt-in and off by default:
  `new Ds4PadSink(enableTouchpadPassthrough: true)`, then call
  `SetTouch(active, x, y)` with normalized `0..1` coordinates. That mode emits raw 63-byte
  `DS4_REPORT_EX` reports (USB report `0x01` minus the report-id byte) so the touch packets at
  offsets 32 and 36 carry real coordinates. The next `Submit` sends them. Without the flag the
  typed setters are used and `SetTouch` is a no-op.
* **Rumble:** a game writing to the emulated pad raises `VirtualPadSink.RumbleReceived`
  (`Action<byte,byte>`, large/left then small/right) on the ViGEm notification thread. With
  `CompositeSink` the request lands in `RumbleState` (`Left`, `Right`, `Version`, `HasPending`,
  `Changed`, `TryConsume`). Try-consume it from the engine loop and forward it to the physical
  controller. A game that stops sending output reports leaves the last value in place; the engine
  should time it out and call `RumbleState.Reset()`.

### `CompositeSink`

`CompositeSink(LayoutInjector? layout, VirtualPadSink? pad, bool ownsSinks = true)`

* Fans a single intent out to the keyboard/mouse sink and the pad sink. Each sink ignores the
  fields it does not own, so no intent splitting is needed on the hot path.
* Aggregates rumble from any sink implementing `IRumbleSource` (the pad sink) into
  `RumbleState`.
* `ownsSinks: true` (default) disposes both sinks from `Dispose`; `false` only releases keys and
  leaves the sinks usable elsewhere.

Typical composition:

```csharp
using PadShift.Output;
using PadShift.Output.Keyboard;
using PadShift.Output.VirtualPad;

LayoutInjector keyboardMouse = LayoutInjector.CreateDefault(); // Interception if installed

Xbox360PadSink? pad = null;
if (!Xbox360PadSink.TryCreate(out pad))
{
    // ViGEmBus not installed: keep keyboard/mouse output, surface the problem in the UI.
}

using CompositeSink sink = new(keyboardMouse, pad);

// Engine loop
sink.Submit(intent);
if (sink.Rumble.TryConsume(out byte left, out byte right))
{
    physicalController.SetVibration(left, right);
}
```

## Latency notes

* **Poll loop:** the output layer does no timing of its own. Latency is dominated by the engine's
  poll interval; 250-1000 Hz is typical. Wake the loop with a waitable timer (or `timeBeginPeriod`)
  rather than `Thread.Sleep(1)`, which rounds up to the timer tick.
* **SendInput:** one syscall per changed frame. Batched transitions are inserted in order; a
  key-down and a mouse delta from the same frame are not separated by an extra frame. Expect tens
  to a few hundred microseconds per batch, plus whatever the target app's message loop adds.
* **Interception:** similar syscall cost but bypasses the low-level hook chain and user-mode
  message queue, which removes jitter caused by other injected-input software. Driver install is
  the price.
* **ViGEm:** each changed frame produces one USB report through ViGEmBus; the bus delivers it to
  XInput/HID immediately. Report rates above ~1 kHz are pointless: XInput polls at 1000 Hz and DS4
  HID reports are expected at 250 Hz (USB) / 100 Hz (Bluetooth). Do not submit identical reports
  (the dirty flag already prevents that).
* **No smoothing:** mouse deltas are sent as given. If the engine needs sub-pixel accumulation,
  do it before filling `OutputIntent`; adding smoothing here would change the game's aim feel and
  break the "exactly these deltas" contract.
* **DS4 raw touch mode** costs a 63-byte buffer compare per frame; it is only enabled when
  passthrough is requested.

## Swapping in a different backend

There are two seams, both in this project:

1. **Keyboard/mouse:** derive from `PadShift.Output.Keyboard.LayoutInjector` and implement
   `OnKey`, `OnMouseButton`, `OnMouseMove`, `OnMouseWheel` and `Flush`, plus `BackendName`.
   The base class already does transition diffing, `ReleaseAll` and disposal, so the subclass only
   turns individual events into native calls. Then either pass the instance to
   `new CompositeSink(myInjector, pad)` or extend `LayoutInjector.CreateDefault()` to return it.
   That factory is the exact composition point the app calls; no other code knows about
   SendInput/Interception.
2. **Virtual pad:** derive from `PadShift.Output.VirtualPad.VirtualPadSink` (or
   `ViGEmPadSink` if ViGEm is involved) and implement `SubmitCore`, `ReleaseAllCore` and
   `DisconnectCore`. `CompositeSink` accepts the new pad without changes as long as it implements
   `IOutputSink`; implement `IRumbleSource` to feed `RumbleState`.

`CompositeSink` itself is the only class the engine needs to know about; it is the intended
dependency-injection boundary.

## Build verification

```powershell
cd O:\package\obj\newobj2\the v2.6 wire protocol
dotnet build src/PadShift.Output/PadShift.Output.csproj -v q
```

Expected: `0 Warning(s)`, `0 Error(s)`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Constructor throws `ViGEmNotInstalledException` | ViGEmBus missing or older than 1.22. Install, reboot, check the service. `ViGEmPadSink.TryProbeBus(out error)` gives the native error. |
| `IsFaulted == true`, `LastError != null` | The pad disappeared or the bus restarted. Dispose and recreate the sink. |
| Keys work in Notepad but not in a game | The game is elevated (UIPI) or uses Raw Input in exclusive mode. Run PadShift at the same integrity level; optionally use Interception. |
| Mouse movement is jittery | The engine is sending irregular poll deltas or another injected-input tool is also running. Check the engine's accumulation; the sink sends exactly what it receives. |
| DS4 touch does nothing | `enableTouchpadPassthrough` was not set, or the game reads touch from a device path that ViGEm's HID descriptor does not expose. |
| Interception not used | `Interception.dll` must be next to the executable and the driver must be installed; otherwise the factory falls back to SendInput silently. Check `InterceptionInjector.IsLibraryPresent` and `TryCreate`. |
