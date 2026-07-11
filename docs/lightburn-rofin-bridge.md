# LightBurn → Rofin Bridge — Integration Brief

Drop this file into the Rofin control app repo and prompt against it. It captures
everything verified so far so a fresh session can implement the bridge without
re-deriving anything. Every protocol fact below was confirmed from packet
captures, decompiled/library source, or a working bench test — not guessed.

---

## 0. Goal & current status

**Goal:** let LightBurn drive the Rofin EasyMark E10 galvo fiber marker. LightBurn
talks to a fake BJJCZ/LMC galvo board; we translate its output into the Rofin's
CORBA protocol and command the real laser.

**Working today (bench-verified):**
- A Raspberry Pi Pico 2 (RP2350) running custom firmware emulates the BJJCZ USB
  board and relays its two data endpoints to the host app over a USB CDC COM port.
- A Python responder answers LightBurn's connect handshake. **LightBurn
  auto-detects and adds the device with no complaints.**
- A separate, byte-verified C# GIOP client can already command the Rofin
  (proven by toggling the pilot laser end-to-end).

**Not done yet (this integration's job):**
- Fold the CDC framing + LMC responder into the main app (replace the standalone
  Python scripts).
- Decode LightBurn's **mark list** (the actual geometry) — currently only the
  connect handshake is handled.
- Wire decoded marks into the existing CORBA client so a LightBurn job comes out
  as Rofin `ExecutePrimitives` / `SetLaserParameters`.

---

## 1. End-to-end architecture

```
LightBurn ──USB (WinUSB/libusb)──► Pico 2 ──USB CDC (COM/tty)──► THIS APP ──TCP/CORBA──► Rofin controller
             vendor iface 0                  framed byte relay      bridge logic          192.168.0.200
             VID:PID 9588:9899                                      + translation
             bulk OUT 0x02 / IN 0x88
```

The Pico is a **dumb relay** — it never interprets an LMC byte. All intelligence
(answering LightBurn, decoding the list, driving the Rofin) lives in this app.
The app connects to the Pico as an ordinary **serial/COM port** and speaks the
framing in §2.

> The Pico firmware and the two proof scripts (`test_bridge.py`,
> `lmc_responder.py`) already exist and work; port their logic into the app. The
> firmware needs no changes for this phase.

---

## 2. CDC framing protocol (app ↔ Pico)

The vendor channel is bidirectional, multiplexed over one serial link. Each
direction is a length-prefixed frame:

```
0xA5 0x5A  type(1)  len(2, little-endian)  payload(len)  xor_checksum(1)
```

`checksum = XOR of type, len_lo, len_hi, and every payload byte.`

| type | direction | meaning |
|------|-----------|---------|
| `0x01` VENDOR_OUT | Pico → app | bytes LightBurn wrote to EP `0x02` (commands / list data) |
| `0x02` VENDOR_IN  | app → Pico | bytes to hand LightBurn on EP `0x88` (replies) |
| `0x10` STATUS     | Pico → app | `payload[0]`: `1`=host mounted, `0`=unmounted |

**Serial port:** the Pico's CDC interface is part of the same composite device, so
it enumerates with the same VID:PID (`0x9588:0x9899`) — auto-detect the port by
matching that, or by the interface string `"Rofin Bridge Link"`. Baud rate is
irrelevant (USB CDC); open at 115200, no timeout / non-blocking reads.

**Parser must:** resync on the `A5 5A` preamble, validate the checksum, and
tolerate a frame carrying more than one logical item (a VENDOR_OUT frame reflects
one FIFO read, *not* necessarily one USB transfer). Accumulate VENDOR_OUT bytes
into a rolling buffer and consume structured units from it (see §3).

**Ordering / timing rule:** the app must be draining the port before LightBurn
connects, or the Pico's buffer fills and back-pressures the vendor endpoint,
stalling LightBurn. Replies you send as VENDOR_IN are served on the next IN token;
if LightBurn reads `0x88` before you've queued anything it simply NAKs (fine for
the sparse handshake) — but never queue a reply for a command LightBurn won't read
(see the desync rule in §4).

---

## 3. The LMC / BJJCZ command protocol

Reference implementation (host side, i.e. what LightBurn does): the
**galvoplotter** Python package (`pip install galvoplotter`), modules
`galvo/consts.py`, `galvo/controller.py`, `galvo/usb_connection.py`. Also
**balor** (gitlab.com/bryce15/balor). Treat these as the executable spec.

**Framing:**
- Command (host → board): **12 bytes = 6 little-endian `uint16`** = `opcode, p1, p2, p3, p4, p5`.
- Reply (board → host): **8 bytes = 4 little-endian `uint16`**.
- Coordinates are galvo units, **16-bit, `0x8000` = field centre**.

**Single-command opcodes** (subset; full list in galvoplotter `consts.py`):

| opcode | name | notes |
|-------:|------|-------|
| `0x0002` | DisableLaser | |
| `0x0004` | EnableLaser | |
| `0x0005` | ExecuteList | run the downloaded list |
| `0x0006` | SetPwmPulseWidth | |
| `0x0007` | GetVersion | **read** — LightBurn inspects |
| `0x0009` | GetSerialNo | **read** — LightBurn inspects |
| `0x000A` | GetListStatus | **read** — polled during marking (busy/idle) |
| `0x000C` | GetPositionXY | **read** |
| `0x000D` | GotoXY | |
| `0x0010` | WriteCorLine | **no reply** (read=False) — see desync rule |
| `0x0015` | WriteCorTable | starts correction-table download |
| `0x0016` | SetControlMode | |
| `0x001B` | SetLaserMode | |
| `0x0017` | SetDelayMode | |
| `0x001C` | SetTiming | |
| `0x001D` | SetStandby | |
| `0x001A` | SetFirstPulseKiller | |
| `0x001E` | SetPwmHalfPeriod | |
| `0x0021` | WritePort | |
| `0x0022` | WriteAnalogPort1 | |
| `0x0025` | ReadPort | **read** — machine-ready / fault bits |
| `0x002E` | SetFpkParam2 | |
| `0x0032` | SetFlyRes | |
| `0x0033` | Fiber_SetMo | MO on/off |
| `0x0034` | Fiber_GetStMO_AP | **read** |
| `0x003A` | EnableZ | |
| `0x0040` | Reset | |

**List opcodes** (the geometry; sent inside list packets, high bit set):

| opcode | name |
|-------:|------|
| `0x8001` | listJumpTo (x, y, angle, distance) |
| `0x8002` | listEndOfList |
| `0x8005` | listMarkTo (x, y, angle, distance) |
| `0x8004` | listDelayTime |
| `0x8006` | listJumpSpeed |
| `0x8007` | listLaserOnDelay |
| `0x8008` | listLaserOffDelay |
| `0x800A` | listMarkFreq |
| `0x800B` | listMarkPowerRatio |
| `0x800C` | listMarkSpeed |
| `0x800D` | listJumpDelay |
| `0x800F` | listPolygonDelay |
| `0x8012` | listMarkCurrent |
| `0x801B` | listQSwitchPeriod |
| `0x8021` | listFiberOpenMO |
| `0x8051` | listReadyMark |

Each list entry is also **12 bytes = 6 little-endian `uint16`** (same shape as a
single command). List entries are buffered by the host into **3072-byte
(`0xC00`) packets** and written to EP `0x02`, then run with `ExecuteList`
(`0x0005`). List packets are **not** answered per-entry; the host tracks progress
with `GetListStatus`.

---

## 4. Connect handshake — what makes LightBurn accept the board

This is fully working; replicate it in-app. LightBurn's connect sequence (galvo
`init_laser`) is:

```
GetSerialNo → GetVersion → Reset →
WriteCorTable → (many) WriteCorLine →
EnableLaser → SetControlMode → SetLaserMode → SetDelayMode → SetTiming →
SetStandby → SetFirstPulseKiller → SetPwmHalfPeriod → SetPwmPulseWidth →
Fiber_SetMo → SetFpkParam2 → SetFlyRes → EnableZ → WriteAnalogPort1 → EnableZ
```

**Responder rules (verified working):**
- Reply **8 bytes** to every command **except** `WriteCorLine` (`0x0010`).
- **DESYNC RULE:** `WriteCorLine` is sent with no read. If you queue a reply for
  it, that stale 8 bytes gets served on the next read and everything after
  desyncs. Never reply to it. (`NO_REPLY = {0x0010}`.)
- Values that matter (LightBurn inspects these):
  - `GetVersion` → put a plausible version in the reply. Working value: `0x0454`,
    placed in **word0 and word3** to cover both host read-conventions:
    `struct.pack("<4H", 0x0454, 0, 0, 0x0454)`.
  - `GetSerialNo` → any plausible serial, e.g. `struct.pack("<4H", 1, 0, 0, 0)`.
  - `ReadPort` → `0x0000` (no fault/door bits). Flip bits only if LightBurn
    reports "not ready".
- Everything else → generic ACK: eight `0x00` bytes.
- Collapse the `WriteCorLine` burst in logs; it's just the correction table.

If LightBurn ever rejects the board, the live command log shows which read it
disliked — tune that reply value. This was not needed in the bench test.

---

## 5. Marking — decode the list and drive the Rofin (the new work)

Once connected and the user starts a job, LightBurn downloads list packets then
calls `ExecuteList`. Steps:

1. **Reassemble list packets.** Collect VENDOR_OUT bytes; walk them as 12-byte
   entries. Distinguish from single commands: list entries have opcode high bit
   set (`>= 0x8000`). Packets are `0xC00`-aligned bursts. Don't reply per entry.
2. **Interpret entries** into an intermediate job model:
   - `listJumpTo` → rapid move (laser off) to (x, y)
   - `listMarkTo` → mark segment (laser on) to (x, y)
   - `listMarkSpeed` / `listJumpSpeed` → speeds
   - `listMarkFreq` / `listQSwitchPeriod` → Q-switch frequency
   - `listMarkPowerRatio` / `listMarkCurrent` → power / current
   - `listLaserOnDelay` / `listLaserOffDelay` / `listJumpDelay` /
     `listPolygonDelay` → scanner timing delays
   - `listReadyMark`, `listFiberOpenMO`, `listEndOfList` → framing/state
3. **Translate to Rofin** using the existing CORBA client (see §6):
   - geometry: galvo 16-bit units (centre `0x8000`) → Rofin device units
     (**500 units/mm**, i.e. 1 unit = 0.002 mm). Scale + offset; establish the
     field-size mapping once (LightBurn field size ↔ Rofin field).
   - `listMarkTo` runs → `ExecutePrimitives` line/polyline segments with the beam
     on; `listJumpTo` → move with beam off.
   - frequency, speeds, delays → `SetLaserParameters` (+ your delay params).
   - **power mapping is yours to own:** LightBurn gives power **%**, the Rofin
     wants pump-diode **current (A)**. Map via your calibration / grey table
     (`WriteGreyTable`). Field distortion is Rofin-side (`SwitchFieldCorr`), not
     from LightBurn.
4. **Status:** while the Rofin executes, answer `GetListStatus` with "busy" until
   done, then "idle", so LightBurn's progress/complete UI behaves.

Reference for exact list-entry field layouts: galvoplotter `controller.py`
`list_*` methods (they *emit* these entries, so they document the field order).

---

## 6. The Rofin CORBA side (already built & byte-verified)

The Rofin controller is a CORBA/GIOP 1.2 stack over TCP. A working C# client
exists that connects, resolves the controller, logs in, gets the Laser, and sets
attributes (proven by toggling `pilotOn`). Reuse it.

- **Bootstrap:** `Connections.xml` gives controller IP `192.168.0.200`, naming
  port `10050`. Resolve name **"Controller"** via CosNaming on `:10050` →
  Controller IOR at `:49160`.
- **Session (on `:49160`):** `Login("operator", <md5-hash>, false)` → returns a
  SystemControl ref; `GetLaser()` → Laser ref. Object keys embed a per-boot GUID,
  so discover them each session — don't hardcode.
- **IOR gotcha:** component IORs advertise both `192.168.0.200:49160` and a
  loopback `127.0.0.1:49161`; use the first non-loopback profile and reuse the
  one connection, addressing objects by key.
- **Wire quirks:** requests are big-endian CDR; honour each reply's own byte-order
  flag. CORBA strings include a trailing NUL in their length; object keys are raw
  octet sequences (no NUL). GIOP 1.2 request body is 8-aligned relative to message
  start (the 12-byte header counts).
- **Marking calls:** `ExecutePrimitives` (geometry; ~248 bytes for a square,
  larger jobs split into ~16 KB chunks), `SetLaserParameters` (doubles:
  frequency, current, pulse params), `SwitchFieldCorr`, `WriteGreyTable`,
  `beamOn`, and the program-lifecycle signals (`SignalProgramLoaded/Start/Stop`,
  `Synchronize`). Speed encodes as `encoded = 16000 / speed_mm_per_s`
  (clamp ~min 160 ⇒ ~100 mm/s max). No `RefreshMasterAccess` was needed for
  pilot; confirm whether marking needs the master lock.

**Safety:** stage all live testing pilot-first / low-power on scrap, behind the
enclosure and interlocks. The first marking milestone should be a single low-power
square from a LightBurn job, not a full design.

---

## 7. Suggested build order in the app

1. **Serial + framing layer** — open the CDC port (auto-detect by VID:PID
   `9588:9899`), implement the §2 frame codec, handle STATUS.
2. **LMC responder** — port §4. Get the app itself to make LightBurn connect
   (replacing the standalone `lmc_responder.py`). Log every decoded command.
3. **List decoder** — port §5 steps 1–2 into an intermediate job model. Prove it
   by dumping a decoded LightBurn square to the log (no laser yet).
4. **CORBA translation** — feed the job model into the existing client (§6).
   First milestone: pilot-on, then a low-power square on scrap.
5. **Status feedback** — `GetListStatus` busy/idle tied to Rofin execution.

Test each layer offline where possible: the framing and responder have
hardware-free self-tests already (`--codec-only`, `--selftest`) — mirror that
approach in the app's tests. Develop the list decoder against
galvoplotter-generated byte streams before involving LightBurn or the Rofin.

---

## 8. Key reference values (quick recall)

| thing | value |
|------|-------|
| Board USB VID:PID | `0x9588` : `0x9899` |
| Vendor bulk OUT / IN | `0x02` / `0x88` (interface 0) |
| Command / reply size | 12 bytes (6× u16 LE) / 8 bytes (4× u16 LE) |
| List packet size | `0xC00` (3072) bytes |
| Galvo coords | 16-bit, `0x8000` = centre |
| No-reply opcode | `WriteCorLine` `0x0010` |
| GetVersion reply (working) | `pack("<4H", 0x0454, 0, 0, 0x0454)` |
| Rofin controller | `192.168.0.200`, naming `:10050`, session `:49160` |
| Naming name | `"Controller"` (CosNaming) |
| Login | `Login("operator", <md5>, false)` |
| Rofin scale | 500 units/mm (1 unit = 0.002 mm) |
| Rofin speed encode | `16000 / speed_mm_per_s` |
| Host-side spec code | `galvoplotter` (pip), `balor` (gitlab) |
```