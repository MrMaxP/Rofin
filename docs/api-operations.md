# API Operations Reference

All operations confirmed from packet captures or live testing against the
controller at `192.168.0.200`. Argument and return types are CDR-encoded
as described in `protocol.md`.

**Confidence key:**
- ✅ Confirmed — verified byte-for-byte against a capture
- 🔬 Live — called successfully against the real controller in code
- ⚠️ Inferred — deduced from pattern/symmetry, not yet verified

---

## CosNaming

Called on the naming service key `b"NameService"` at port 10050.

### `resolve(name)` → IOR ✅

Resolves a name to an object reference.

```
Arguments:
  count     ULong = 1
  id        String  e.g. "Controller"
  kind      String  "" (empty)

Returns:
  IOR of the named object
  (May return LOCATION_FORWARD — follow the forwarded IOR)
```

---

## Controller

Called on the IOR returned by `resolve("Controller")`.

### `Login(user, hash, flag)` → SystemControl ref ✅

```
Arguments:
  user   String   "operator"
  hash   String   "4b583376b2767b923c3e1da60d10de59"
  flag   Bool     false

Returns:
  IOR of the SystemControl object
```

---

## SystemControl

Called on the IOR returned by `Login()`.

### `GetMachineControl()` → MachineControl ref ✅

```
Arguments: none
Returns:   IOR of MachineControl
```

### `GetAllComponents()` → sequence of IORs ✅

Returns a **flat** list of all 27 hardware component descriptors. Each element
is a `Persistent` interface IOR. This is the only object that supports this
operation — calling it on any returned component raises `BAD_OPERATION`.

```
Arguments: none
Returns:
  count    ULong
  items[]  IOR[]  — 27 entries; some profiles may be loopback-only
```

See `components.md` for the full list and how to reconstruct the hierarchy.

### `GetClassInfos()` → String ✅

Returns a 257 KB XML blob encoding the attribute schema for every class.
Structure is `ObjectStore` format (custom XML). Contains attribute names,
types, ranges, and access flags — not parent-child hierarchy.

---

## MachineControl

Called on the IOR returned by `GetMachineControl()`.

### `GetLaser()` → PowerlineE ref ✅

```
Arguments: none
Returns:   IOR of the PowerlineE (laser source) component
```

### `GetAxesControl()` → AxesControl ref ✅

```
Arguments: none
Returns:   IOR of the AxesControl component

Note: The official client calls this fresh before every axis operation.
      Do not cache — re-fetch each time for safety.
```

### `GetIOControl()` → IOControl ref 🔬

```
Arguments: none
Returns:   IOR of the IOControl component
```

---

## Generic component operations

Supported on every component that is a `Persistent` subclass (i.e. all 27
nodes returned by `GetAllComponents`). Some operations raise `BAD_OPERATION`
on lighter interface types.

### `_get_className()` → String 🔬

Returns the class name, e.g. `"PowerlineE"`, `"LIFAxis"`, `"MachineControl"`.

### `_get_genericName()` → String 🔬

Returns the human-readable display name, e.g. `"Laser"`, `"Axis"`, `"IO"`.

### `_get_id()` → ULong 🔬

Returns the numeric component identifier.

### `GetAttribute(name)` → any 🔬

Read an attribute by name. Returns a CORBA `any` (TypeCode + value).

```
Arguments:
  name    String   attribute name

Returns:
  TypeCode    ULong
  value       CDR per TypeCode
```

See `attributes.md` for per-class attribute lists.

### `SetAttribute(name, typecode, value)` → void ✅

Write an attribute.

```
Arguments:
  name        String
  typeCode    ULong[1]  e.g. 0x00000008 for tk_boolean
  value       CDR-encoded value (include alignment padding if needed)
```

### `_get_classInfo()` → String 🔬

Returns a per-component XML blob (`ObjectStore` format) listing all attribute
schemas for that component's class. Contains embedded NUL bytes in CDR
encoding — strip before XML parsing.

---

## AxesControl

Called on the IOR returned by `GetAxesControl()`.

### `Jog(arg0, arg1, direction)` → void ✅

Starts or stops axis jogging. The axis continues moving until a subsequent
`Jog(0, 2, 0)` (STOP) is sent.

```
Arguments:
  arg0       ULong   0  (constant — always 0 in captures)
  arg1       ULong   2  (constant — axis selector within AxesControl)
  direction  ULong   see table below

Direction values:
  0  STOP            confirmed ✅
  1  Slow UP         (bed toward laser, position decreases)  confirmed ✅
  3  Fast UP         (bed toward laser, position decreases)  confirmed ✅
  2  Slow DOWN       (bed away from laser, position increases) inferred ⚠️
  4  Fast DOWN       (bed away from laser, position increases) inferred ⚠️
```

"UP" means the bed rises toward the laser head; position value *decreases*
toward the SW top limit (−0.10 mm). "DOWN" moves the bed away from the laser;
position *increases* toward the SW bottom limit (119.50 mm).

### `ReferenceDrive()` → void ✅

Drives the bed to the hardware endstop to establish the zero reference.
Motion is toward the bottom (increasing position). The command returns
immediately — motion continues asynchronously.

```
Arguments: none
Returns:   void
```

Position readings are unreliable until reference is run after power-on.
After completion, position reads the SW bottom limit (119.50 mm).

---

## LIFAxis

Called on LIFAxis component IORs from `GetAllComponents()`. Two instances
exist; only the one with `active=true` is the physical Z axis.

### `GetAttribute("actPosition")` → double 🔬

Current axis position in mm. Updates continuously while the axis is moving.

### `GetAttribute("IN_POSITION")` → bool 🔬

`true` when the axis has settled at its commanded position.

---

## IOControl

### `ReadIOPort(portType, portIndex, something)` → sequence ✅

Reads a hardware I/O port. Observed calls from `Rofin-AxisTest.pcapng`:

```
ReadIOPort(0, 5, 0)   — port type 0, port 5, sub-index 0
ReadIOPort(0, 5, 1)   — port type 0, port 5, sub-index 1
ReadIOPort(0, 3, 0)   — port type 0, port 3, sub-index 0
ReadIOPort(0, 3, 1)   — port type 0, port 3, sub-index 1
```

Return value format and port mapping (end stops, sensors) are not yet decoded.

---

## SystemControl / event polling

### `PullEvents(seq, something)` → sequence 🔬

Called repeatedly (every ~100ms) by the official client to receive
asynchronous state change notifications. Arguments observed:
`0x0000000000000000` (two ULongs = 0, 0).

Return value format not decoded.

---

## Operations seen in captures but not yet implemented

| Operation | Object | Notes |
|---|---|---|
| `GetProgramControl()` | SystemControl | Returns program execution controller |
| `GetFileSystem()` | SystemControl | Returns file system accessor |
| `GetConfiguration()` | SystemControl | Returns configuration accessor |
| `GetSystemState()` | SystemControl | Returns system-wide state enum |
| `GetObjectState()` | SystemControl | Returns detailed state struct |
| `Logout()` | SystemControl | Seen at session end |
| `_get_roleLevel()` | SystemControl | Returns current user role (integer) |
