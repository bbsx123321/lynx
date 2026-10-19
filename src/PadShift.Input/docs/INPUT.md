# PadShift.Input — input layer

`PadShift.Input` reads physical controllers and turns each poll into a `PadShift.Core.ControllerState`.
It contains no injection/output code and never requires administrator rights, a driver install, or
inter-process pipes.

## Files

| File | Purpose |
| --- | --- |
| `XInput/XInputSource.cs` | XInput user slots 0..3 via `xinput1_4.dll` → `xinput1_3.dll` → `xinput9_1_0.dll` fallback. |
| `Hid/HidDeviceSource.cs` | Opens one HidSharp `HidDevice`, selects a parser and decodes input reports. |
| `Hid/IReportParser.cs` | Parser contract: `bool TryParse(ReadOnlySpan<byte>, ControllerState)`. |
| `Hid/ReportParserRegistry.cs` | VID/PID table + generic fallback selection. |
| `Hid/DualShock4ReportParser.cs` | DualShock 4 USB/BT report decoding. |
| `Hid/DualSenseReportParser.cs` | DualSense USB/BT report decoding. |
| `Hid/SwitchProReportParser.cs` | Switch Pro Controller full-report decoding. |
| `Hid/ReportDescriptorParser.cs` | Minimal HID report-descriptor reader used by the generic parser. |
| `Hid/GenericGamepadReportParser.cs` | Descriptor-driven heuristic for unknown gamepads. |
| `Devices/DeviceWatcher.cs` | 1 s polling watcher with add/remove events. |
| `CompositeInputSource.cs` | Merges several sources into one `ControllerState`. |
| `ControllerStateCopy.cs` | Internal snapshot copy helper. |

## Threading contract

* `IInputSource.TryPoll` is **single-threaded**. Call it from one thread per source and do not call
  it concurrently with `Dispose`. `HidDeviceSource.TryPoll` never blocks waiting for a report: it
  drains the already-queued reports (at most 128) and keeps the newest valid snapshot.
* `DeviceWatcher` runs its scans on a `System.Threading.Timer` thread-pool thread once per second.
  `SourceAdded` / `SourceRemoved` are raised on that thread. Handlers must not block and must
  marshal to their UI thread themselves. `Sources` returns a thread-safe snapshot.
* `CompositeInputSource` calls its children sequentially from the caller's polling thread.
* No method on the polling path throws for device errors. A failed read sets `IsConnected = false`
  (or simply returns `false` for a timeout) and the caller can drop the source.

## Supported devices and parsed fields

| Device | Reports | Parsed | Not parsed in v1 |
| --- | --- | --- | --- |
| XInput pad (any) | `XInputGetState` | sticks (raw, deadzone-free), triggers, A/B/X/Y, bumpers, Back/Start/Guide, stick clicks, D-pad | guide-only packets on very old XInput DLLs, impulse triggers, battery |
| DualShock 4 `054C:09CC`, `054C:05C4` | USB `0x01` (64 B), BT `0x11` (78 B), short BT `0x01` (10 B) | sticks, L2/R2, Cross/Circle/Square/Triangle, L1/R1, Share/Options, L3/R3, PS, touchpad click, D-pad, gyro (X/Y/Z, °/s), first touch point (normalised 0..1) | accelerometer, battery, timestamp, second finger, touch ID, CRC validation, lightbar/rumble writes |
| DualSense `054C:0CE6` | USB `0x01` (64 B), BT `0x31` (78 B) | sticks, L2/R2, Cross/Circle/Square/Triangle, L1/R1, Create/Options, L3/R3, PS, touchpad click, D-pad, gyro (X/Y/Z, °/s), first touch point (normalised 0..1) | accelerometer, adaptive triggers, haptics, mic-mute button, Edge FN/paddle buttons, battery, second finger, CRC |
| Switch Pro Controller `057E:2009` | full input `0x30`, subcommand `0x21`, MCU `0x31` | sticks (12-bit packed, Y inverted), A/B/X/Y, L/R, ZL/ZR (as full-scale trigger axes), Minus/Plus, L3/R3, Home, D-pad | gyro/IMU (deliberately disabled in v1), Capture button, touch/NFC, rumble, battery, USB handshake |
| Unknown gamepad | descriptor-driven | up to 6 axes (X/Y/Rx/Ry/Z/Rz), hat switch (`0x39`), buttons 1..17 | gyro, touch, vendor-specific fields |

### Axis and trigger encoding

* Sticks: signed, approximately `-32768..32767`, no deadzone applied by this layer.
* Triggers: `byte * 257` → `0..65535`. This is the 16-bit XInput convention and matches the
  contract's "0..255 shifted left by 8" note; `value >> 8` recovers the original byte. Digital
  ZL/ZR and generic digital triggers use `65535` when pressed.
* Gyro: degrees per second, computed with 1024 LSB per °/s for DualShock 4 and DualSense.
* Touch: `TouchX`/`TouchY` normalised to `0..1`; DS4 uses a 1920×942 grid, DualSense 1920×1080.
* `TimestampTicks` is `DateTime.UtcNow.Ticks` at the moment the snapshot was accepted.

### DualShock 4 / DualSense layout notes

Both families share one payload structure. USB reports put the payload immediately after the report
ID; Bluetooth reports add a 2-byte header for the DualShock 4 `0x11` report and a 1-byte header for
the DualSense `0x31` report. The parsers reject a report whose report ID or length does not match,
so unrelated reports (battery, calibration, audio) never overwrite the last good state.

### Generic fallback

For an unknown VID/PID, `ReportParserRegistry` reads the HID report descriptor. The device is only
opened when the descriptor advertises a Generic Desktop Joystick (`0x04`), Gamepad (`0x05`) or
Multi-axis Controller (`0x08`) usage. `ReportDescriptorParser` then extracts variable input fields
(bit offset, size, logical range, signedness) and `GenericGamepadReportParser` maps:

* X/Y → left stick, Rx/Ry → right stick (fallback Z/Rz when Rx/Ry are absent),
* remaining Z/Rz/Rx/Ry axes → triggers,
* button usages 1..4 → A/B/X/Y, 5/6 → bumpers, 7/8 → digital triggers if no analog trigger,
  9/10 → Back/Start, 11/12 → stick clicks, 13 → Guide, 14..17 → paddles,
* hat usage `0x39` → D-pad.

Keyboards, mice and other non-gamepad HID collections are never opened.

## DeviceWatcher lifecycle

1. `Start()` performs an immediate scan, then polls every second (default interval).
2. Each scan probes XInput slots 0..3 with `XInputGetState` and enumerates
   `DeviceList.Local.GetHidDevices()` (no WMI).
3. Newly opened devices raise `SourceAdded`; removed or errored devices raise `SourceRemoved` after
   being disposed.
4. `Stop()` stops the timer but keeps the sources alive; `Dispose()` stops the timer and disposes
   every source.

A device that is visible through both XInput and HID may appear as two sources. Prefer the
`XInput#n` instance for Xbox-compatible pads.

## Composite semantics

`CompositeInputSource` keeps the last good snapshot for each child. Every successful aggregate poll:

* ORs all button bits,
* picks the signed axis value with the largest absolute magnitude (trigger max is therefore the
  deepest press),
* applies the same max-|value| rule per gyro channel,
* takes touch data from the first child with an active finger,
* returns `false` only when **no** child produced a new report in that call.

## Adding a parser

1. Create `Hid/MyDeviceReportParser.cs` implementing `IReportParser`.
2. Validate the report ID and minimum length first and return `false` for anything you do not
   recognise. Never index past `report.Length`.
3. On success call `state.Clear()` once, then fill **all** fields you support. Leave unsupported
   fields at their cleared values (zero/false).
4. Convert sticks to the signed axis range, triggers with `HidParserHelpers.ScaleTriggerByte`, and
   use `HidParserHelpers.ApplyHat` / `ApplyTouchPoint` where applicable.
5. Register the VID/PID in `ReportParserRegistry.Create` and in `IsKnownDevice` so
   `DeviceWatcher` opens it without a descriptor probe.
6. Do not throw, do not allocate per report beyond small locals, and do not call output APIs.
7. Add the device to the table above, including fields that are deliberately not parsed.

If a device needs state (calibration, feature reports, mode switching), keep it inside the parser
instance; `HidDeviceSource` creates one parser per opened device.
