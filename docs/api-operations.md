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

### `Jog(axis, direction)` → void ✅

Starts or stops axis jogging. The axis continues moving until a subsequent
`Jog(2, 0)` (STOP) is sent.

```
Arguments:
  axis       ULong   2  (constant — LIF axis index within AxesControl)
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

Object returned by `GetIOControl()` on MachineControl.

### `ReadIOPort(portType, portIndex)` → 4 bytes ✅

Reads a hardware I/O port. Reply is a CDR `sequence<octet>` of 4 bytes.

```
ReadIOPort(5, 0)    — observed in all captures; returns 01020100
ReadIOPort(0, 5, 0) — 3-arg variant in Rofin-AxisTest.pcapng (port type 0, port 5, sub-index 0)
ReadIOPort(0, 5, 1)
ReadIOPort(0, 3, 0)
ReadIOPort(0, 3, 1)
```

The reply `01 02 01 00` is a bitmask of I/O port state. Port mapping (end stops,
sensors, interlocks) is not yet decoded.

### `WriteIOPort(data)` → void ✅

Writes to a hardware I/O port. Body is 24 bytes of raw CDR:

```
portType    ULong = 5
portIndex   ULong = 1
mask_len    ULong = 4  — length of mask sequence
mask[4]     bytes      — bits to affect
val_len     ULong = 4  — length of value sequence
val[4]      bytes      — bit values
```

Observed patterns (mask → value):
- `00000000` → `00400000`  (set bit in byte 1)
- `00000000` → `00000200`  (set bit in byte 2)
- `00020000` → `00000200`  (mask+value)
- `00400000` → `00400000`

Port bit mapping not yet decoded.

---

## ProgramControl

Object returned by `GetProgramControl()` on SystemControl. Manages job
lifecycle and laser firing parameters.

### `SignalProgramLoaded()` → void ✅

Signals the controller that a job program has been loaded.

### `SignalProgramStart()` → void ✅

Signals the controller to begin program execution. Sent after `WriteIOPort`
clears the stop bit.

### `SignalProgramStop()` → void ✅

Signals that the marking program has finished execution.

### `SignalProgramUnLoaded()` → void ✅

Signals that the job program has been unloaded. Sent after `SignalProgramStop`.

### `StopProgram()` → void ✅

Abort current program execution (seen in ws-outline.pcapng).

### `Synchronize()` → void ✅

Blocks until the controller has finished processing queued primitives.
Called after each batch of `ExecutePrimitives`.

### `SetLaserParameters(data)` → void ✅

Sets laser firing parameters. Body is 24 bytes of raw CDR (encoding TBD — likely
power, frequency, duty cycle, and speed settings). Called once per marking pass.

---

## GalvoControl (MarkControl)

Object obtained alongside ProgramControl; handles scan head commands, field
correction, and grey table. Object ID is `a8` in the ws-job/ws-outline session.

### `ExecutePrimitives(data)` → void ✅

Sends a batch of scan-head movement and laser-fire primitives to the galvo
controller. Body is a CDR byte sequence (encoding TBD — likely fixed-size
records containing primitive type, X/Y coordinates, speed, and laser state).

Called multiple times per job; the controller queues them and processes
asynchronously. Call `Synchronize()` on ProgramControl to wait for completion.

### `SwitchFieldCorr(data)` → void ✅

Activates field correction (distortion compensation for the scan field).
Observed body: `01000000 01000000` (two ULongs, likely mode flags).

### `WriteGreyTable(data)` → void ✅

Writes a grey-level lookup table mapping intensity to power/speed. Body is
24 bytes (encoding TBD). Called once before marking begins.

### `SetAttribute("resetStopInDriver", bool)` → void ✅

Clears the stop-in-driver flag before a marking run. Must be set to `true`
(value 1) before calling `SignalProgramLoaded`.

---

## SystemControl / event polling

### `PullEvents(seq, something)` → sequence 🔬

Called repeatedly (every ~100ms) by the official client to receive
asynchronous state change notifications. Arguments observed:
`0x0000000000000000` (two ULongs = 0, 0).

Return value format not decoded.

### `Logout(flag)` → void ✅

Ends the session. Observed: `Logout(0)` (normal) and `Logout(1)` (shutdown).

```
Arguments:
  flag   ULong   0 = normal logout, 1 = logout before shutdown
```

### `Shutdown()` → void ✅

Shuts down the controller. Called on the SystemControl object after `Logout(1)`.

---

## CORBA object key structure

All objects on the server share the server-prefix `14010f005253549X...` in
their CORBA object key (where `X` is session-specific). The last 12 bytes
encode two ULongs: `[obj_id, 1, obj_id+1]`. Objects can be distinguished
by `obj_id` (bytes 15–18, LE). IDs are assigned per-session and match the
IOR returned by the corresponding `Get*` call.

Example session (ws-job / ws-outline):

| obj_id | Interface | Operations |
|--------|-----------|------------|
| 0x51  | EventCtrl | PullEvents |
| 0xa4  | ProgramControl | Signal*, Synchronize, SetLaserParameters |
| 0xa6  | IOControl | ReadIOPort, WriteIOPort |
| 0xa7  | PowerlineE (Laser) | GetAttribute, SetAttribute |
| 0xa8  | GalvoControl | ExecutePrimitives, SwitchFieldCorr, WriteGreyTable |
| 0xb0  | EventCtrl2 | PullEvents2, RefreshMasterAccess |

---

## Operations seen in captures but not yet implemented

| Operation | Object | Notes |
|---|---|---|
| `GetFileSystem()` | SystemControl | Returns file system accessor |
| `GetConfiguration()` | SystemControl | Returns configuration accessor |
| `GetSystemState()` | SystemControl | Returns system-wide state enum |
| `GetObjectState()` | SystemControl | Returns detailed state struct |
| `_get_roleLevel()` | SystemControl | Returns current user role (integer) |
| `PullEvents2(arg)` | EventCtrl2 | arg=2; confirmed in ws-focusfind.pcapng |
| `RefreshMasterAccess(arg)` | EventCtrl2 | arg=32; keepalive for write access |
