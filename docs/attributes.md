# Component Attribute Reference

Attributes are read via `GetAttribute(name)` → CORBA `any` (TypeCode + value)
and written via `SetAttribute(name, typeCode, value)`. All reads confirmed live
against the controller unless marked otherwise.

See `protocol.md` → "CORBA `any` type" for TypeCode encoding.

---

## Universal attributes

Present on every `Persistent` component (all 27 nodes).

| Attribute | TypeCode | Type | Values / Notes |
|---|---|---|---|
| `state` | 3 | `tk_long` | 0=suspended, 1=suspending, 2=resuming, 3=running, 4=error |
| `active` | 8 | `tk_boolean` | `true` when the component is operational |
| `isWarning` | 8 | `tk_boolean` | `true` when a non-fatal warning condition is active |

---

## PowerlineE (className = `"PowerlineE"`, genericName = `"Laser"`)

The laser source object. `GetLaser()` on MachineControl returns this.

| Attribute | TypeCode | Type | R/W | Notes |
|---|---|---|---|---|
| `state` | 3 | long | R | Universal |
| `active` | 8 | bool | R | Universal |
| `isWarning` | 8 | bool | R | Universal |
| `pilotOn`        | 8 | bool | **R/W** | Alignment laser — **confirmed writable** ✅ |
| `focusFinderOn`  | 8 | bool | **R/W** | Focus-finding mode — **confirmed writable** ✅ (ws-focusfind.pcapng) |
| `shutterOpen`    | 8 | bool | **R/W** | Physical shutter — **confirmed writable** ✅ (ws-closeshutter.pcapng) |
| `lampTestOn`     | 8 | bool | **R/W** | Lamp test mode — **confirmed writable** ✅ (ws-lamptest.pcapng) |
| `beamOn` | 8 | bool | R | Main beam active |
| `actLaserPower` | 7 | double | R | Actual laser power (unit unknown, likely W or %) |
| `actTemperature` | 7 | double | R | Laser head temperature (°C) |
| `cwAllowed` | 8 | bool | R | Continuous-wave operation permitted |
| `operationMode` | — | — | R | Operation mode enum (TypeCode not yet decoded) |

All 47 attributes are listed in `PowerlineE._get_classInfo()` XML.

**Writing boolean attributes:**

```
SetAttribute("pilotOn",
    typeCode = [0x00, 0x00, 0x00, 0x08],   // tk_boolean
    value    = [0x01, 0x00])                // true + alignment byte

// Same pattern applies to focusFinderOn, shutterOpen, lampTestOn
```

---

## PowerSupply_HN800 (genericName = `"PowerSupply"`)

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | Universal |
| `active` | 8 | bool | Universal |
| `actTemperature` | 7 | double | Power supply temperature (°C) |
| `opHoursPowersupply` | 7 | double | Cumulative power-supply operating hours |

---

## LIFAxis (genericName = `"Axis"`)

Two instances exist. Only the one with `active=true` is the physical Z axis.

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | 3=running (active), 0=suspended (inactive) |
| `active` | 8 | bool | false for the unused second axis |
| `actPosition` | 7 | double | Current position in mm |
| `IN_POSITION` | 8 | bool | true when settled at commanded position |

**Z-axis travel:**

| Limit | Value | Direction |
|---|---|---|
| SW Top (min) | −0.10 mm | Bed highest (closest to laser head) |
| SW Bottom (max) | 119.50 mm | Bed lowest (hardware endstop) |

Position reads `119.50 mm` immediately after a successful `ReferenceDrive`.
Position is unreliable until referenced after power-on.

---

## AxesControllerLIF (genericName = `"AxesController"`)

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | Universal |
| `active` | 8 | bool | Universal |

---

## AxesControl

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | Universal |
| `active` | 8 | bool | Universal |

---

## PLELaserHead (genericName = `"SafetyShutter"`)

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | Universal |
| `active` | 8 | bool | Universal |
| `opHourLaser` | 7 | double | Cumulative laser operating hours |
| `opHourSystem` | 7 | double | Cumulative system operating hours |

---

## GalvoHead / GalvoControl

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `state` | 3 | long | Universal |
| `active` | 8 | bool | Universal |

Additional galvo-specific attributes exist in `_get_classInfo()` XML but have
not been decoded (scan head control is not yet implemented).

---

## MarkingOnTheFly / ScannerAutocalibration

Both are `suspended` / `active=false` on this machine configuration. No
additional attributes have been read.

---

## GalvoControl (MarkControl)

Object obtained from ProgramControl or MachineControl (exact accessor not yet decoded).
Manages scan head commands and laser marking parameters.

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `resetStopInDriver` | 8 | bool | **R/W** | Clear stop-in-driver flag before marking. Set to `true` before `SignalProgramLoaded`. |

---

## ProgramControl

Object returned by `GetProgramControl()` on SystemControl.

| Attribute | TypeCode | Type | Notes |
|---|---|---|---|
| `prcServerState` | — | — | Server state enum (TypeCode not yet decoded) |

---

## Discovering attributes for any component

Every component supports `_get_classInfo()` which returns an XML blob:

```xml
<ObjectStore xmlns="x-schema:objectStore-schema.xml">
  <Object name="PowerlineE" ...>
    <Attribute name="actLaserPower" type="double" access="read" .../>
    ...
  </Object>
</ObjectStore>
```

Strip embedded NUL bytes before XML parsing. Query with
`XDocument.Descendants().Where(e => e.Name.LocalName == "Attribute")`.

`SystemControl.GetClassInfos()` returns the schema for all 257 classes at once
(~257 KB XML). Useful for offline exploration but the per-component
`_get_classInfo()` is faster for targeted lookups.
