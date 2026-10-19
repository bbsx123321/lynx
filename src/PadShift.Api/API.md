# PadShift local HTTP API (`/v2.6`)

Clean-room the reference config tool-compatible surface. Only routes that map 1:1 onto `IRemapHost` exist;
everything else answers `404` (JSON string body) and logs once.

## Listen / config
| Key | Default | Meaning |
|---|---|---|
| `PadShift:Api:Ports` | `[35474,35475,35476]` | candidate ports, first free wins |
| `PadShift:Api:Lan` | `false` | bind `http://*:port` instead of `http://127.0.0.1:port` |
| `PadShift:Api:Token` | – | shared secret, see Security |
| `PadShift:Api:SseHeartbeatSeconds` | `60` | SSE heartbeat interval |

The bound URL is logged at startup. If the container already has an `IRemapHost` it is used,
otherwise `Dev.FakeRemapHost` (one virtual pad, in-memory state) is registered.

## Security (deliberate improvement over the reference tool)
the reference config tool has no authentication at all. PadShift binds loopback by default; with `Lan=true`
every request must send `X-PadShift-Token: <PadShift:Api:Token>` when a token is set, else
`401` (JSON string). `Lan=true` without a token only logs a warning.

## Routes
`C` = `/v2.6/ControllerService`. JSON is PascalCase, enums are names, errors are a JSON string
body with a non-200 status. `Slot` accepts `"Slot1".."Slot4"` or `1..4`.

| Route | Body / query | Response |
|---|---|---|
| `GET C/Controllers` | – | `[{"DataType":"PeripheralController","Controllers":[...]}]` |
| `POST C/ApplyConfig` | `{ControllerId,ConfigPath,Slot}` (aliases `ControllerGuid`,`Path`) | `EnableRemapResponse` |
| `POST C/EnableRemap` | `{Guid?,RemapNoToggled?}` | `EnableRemapResponse` |
| `POST C/DisableRemap` | `{Guid?}` | `200` |
| `POST C/SelectSlot` | `{ControllerId,Slot}` (alias `Guid`) | `true` |
| `POST C/SetLayer` | `{ControllerId,Layer}` (alias `Guid`; `Layer` 0–19) | `true`, `422` if the profile does not declare that layer |
| `POST C/ClearSlot` | `{ControllerId,Slot}` (alias `Guid`; legacy `Slots:["SlotN"]`) | `200`, `400` if the slot has no applied config |
| `GET C/RemapState/{controllerId}` | – | `"NothingApplied"｜"RemapOn"｜"RemapOff"` |
| `GET C/CurrentSlot/{controllerId}` | – | `"Slot1".."Slot4"` |
| `GET C/CurrentLayer/{controllerId}` | – | `0..19` (`0` = main layer) |
| `GET C/AppliedConfigs/{controllerId}?slot=` | – | `{Id,GameId,Name,GameName,Path}`, `204` if empty |
| `GET C/Shifts/{controllerId}` | – | `[{"Shift":0,"Name":"Default"}]` |
| `GET C/KeyPressedPoller/{controllerId}?scanCode=` | – | `true` / `false` |
| `POST C/Vibrate` (alias `C/SendGamepadVibration`) | `{ControllerId,Motors,Duration,Intensity}` | `200` |
| `GET /v2.6/Events` | SSE | `event: <EventName>` + `data: <json>` |
| `GET /v2.6/Version/GetVersion`, `GET /Version` | – | `{"Version":"v2.6"}` |
| `GET /v2.6/Info/GetProtocolVersion` | – | `"v2.6"` |

SSE names: `RemapStateChanged`, `SlotChanged`, `ShiftChanged`, `ConfigApplied`, `SlotCleared`, `ControllerConnected`,
`ControllerDisconnected`, `BatteryLevelChanged`, `Heartbeat`.

## curl
```bash
curl http://127.0.0.1:35474/v2.6/ControllerService/Controllers
curl -H 'Content-Type: application/json' -d '{"ControllerId":"<guid>","ConfigPath":"C:\\p\\x.rewasd","Slot":"Slot1"}' http://127.0.0.1:35474/v2.6/ControllerService/ApplyConfig
curl -H 'Content-Type: application/json' -d '{"Guid":"<guid>"}' http://127.0.0.1:35474/v2.6/ControllerService/EnableRemap
curl -H 'Content-Type: application/json' -d '{"ControllerId":"<guid>","Layer":1}' http://127.0.0.1:35474/v2.6/ControllerService/SetLayer
curl http://127.0.0.1:35474/v2.6/ControllerService/CurrentLayer/<guid>
curl -N http://127.0.0.1:35474/v2.6/Events   # LAN: add -H 'X-PadShift-Token: <token>'
```

## Deliberate omissions / differences
- `SetLayer` / `CurrentLayer` are **PadShift extensions**: the reference config tool has no layer route (its layers
  change only through profile shifts). They expose `IRemapHost.SetLayer` / `GetLayer`; an unknown
  layer is `422`, not a silent no-op.
- No `GameService`, `License`, `Bluetooth`, `ExternalDevices`, `OutputGamepad*`, `WaitForInited`, `PressedButtons`: no 1:1 `IRemapHost` member, so they `404`.
- `ApplyConfig` turns remap on afterwards (the reference tool semantics); `RemapNoToggled` is accepted but ignored (PadShift's enable is idempotent).
- `KeyPressedPoller` is a one-shot `?scanCode=` query, not the reference 75 ms `GamepadState` SSE.
- SSE payloads carry only fields PadShift has (`ConfigPath` instead of config `Id`, `BatteryPercent` not `BatteryInfo`).
- `AppliedConfigs` has no config `Id`/`GameId` (file paths only); several paths in a slot give an array. `DataType` is always `PeripheralController`; `ManufacturerInfo` carries VID/PID only.
- `Dev.FakeRemapHost` validates `ConfigPath` existence (like the engine) and keeps state in memory only.
