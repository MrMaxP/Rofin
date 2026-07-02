# GIOP/CORBA Wire Protocol

All communication with the Rofin EasyMark E10 controller uses raw GIOP 1.2 over
TCP. There is no ORB library — every byte is hand-assembled from the CORBA
specification and verified against Wireshark captures of the official
LaserConsole client.

---

## Transport

| Parameter | Value |
|---|---|
| Host | `192.168.0.200` (default) |
| Naming service port | `10050` |
| Controller port | `49160` (returned via LOCATION_FORWARD from naming) |
| Protocol | GIOP 1.2 over persistent TCP |
| Framing | One TCP connection per role; no keep-alive required |

---

## Message framing

Every GIOP message starts with a 12-byte header:

```
Offset  Size  Field
------  ----  -----
0       4     Magic: 'G' 'I' 'O' 'P'  (0x47 0x49 0x4F 0x50)
4       1     Major version: 1
5       1     Minor version: 2
6       1     Flags: bit 0 = byte order (0 = big-endian, 1 = little-endian)
7       1     Message type: 0=Request, 1=Reply, 3=CancelRequest, 5=CloseConnection
8       4     Body size in bytes (big-endian regardless of flags bit)
```

Requests are sent big-endian (flags = 0x00).
The server replies use **little-endian** (flags = 0x01) — read the flags byte
of each reply to determine byte order before parsing.

---

## GIOP 1.2 Request body layout

```
Field                  CDR type    Notes
-----                  --------    -----
request_id             ULong       auto-incrementing, starts at 1
response_flags         Octet       0x03 = SYNC_WITH_TARGET (normal request)
reserved[3]            Octet[3]    zeros
TargetAddress discrim  Short       0 = KeyAddr
(no alignment padding)
object_key length      ULong       4 bytes, length of key bytes following
object_key             Octet[]     per-boot instance identifier
operation              String      CORBA string: ULong length + chars + NUL
service_context count  ULong       0 = no service contexts
[align to 8-byte boundary from message start]
arguments              CDR         operation-specific body
```

**Alignment note**: The spec says KeyAddr is followed by 4-byte alignment for
the sequence length. In practice IIOP.NET omits this padding — the key sequence
length appears at the 2-byte boundary immediately after the 2-byte discriminant.
Our implementation matches this observed behaviour.

---

## GIOP 1.2 Reply body layout

```
Field                  CDR type    Notes
-----                  --------    -----
request_id             ULong
reply_status           ULong       0=NO_EXCEPTION, 1=USER_EXCEPTION,
                                   2=SYSTEM_EXCEPTION, 3=LOCATION_FORWARD
service_context count  ULong
service_context[]      (variable)
[align to 8-byte boundary from message start]
body                   CDR         result value, exception struct, or IOR
```

---

## CDR primitive types

| Type | CDR | Alignment | Notes |
|---|---|---|---|
| `octet` | 1 byte | 1 | `bool` is an octet (0=false, 1=true) |
| `short`/`ushort` | 2 bytes | 2 | |
| `long`/`ulong` | 4 bytes | 4 | CORBA `long` = 32-bit signed int |
| `double` | 8 bytes | 8 | IEEE 754 double |
| `string` | ULong length + chars + NUL | 4 (for length) | length includes NUL |
| `sequence<octet>` | ULong length + bytes | 4 (for length) | length excludes NUL |

CDR alignment is always measured from byte 0 of the message (the 'G' in 'GIOP').

---

## Object references (IORs)

`GetMachineControl`, `GetLaser`, `GetAllComponents`, etc. all return object
references encoded as an Interoperable Object Reference (IOR):

```
type_id     String          e.g. "IDL:rofin.com/ControllerComponents/..."
profiles    Sequence        array of TaggedProfile
  tag       ULong           0 = TAG_INTERNET_IOP
  profile   OctetSequence   encapsulated IIOP profile:
    byte_order  Octet       encapsulation byte order
    major/minor Octet×2     IIOP version (1, 2)
    host        String      IP address string
    port        UShort      TCP port
    object_key  Sequence    opaque key bytes
```

Object keys are per-boot random identifiers (~27 bytes). They encode a GUID +
instance ID and must be obtained live — none can be hardcoded.

The controller sometimes returns loopback addresses (127.0.0.1) in IORs.
Always substitute the actual controller IP when this occurs.

---

## CORBA `any` type

`SetAttribute` and `GetAttribute` use CORBA `any` to pass typed values.

**Wire format** (both sending and receiving):

```
TypeCode    ULong    identifies the value type (see table below)
value       CDR      encoded per the TypeCode, including alignment
```

**TypeCodes observed on this hardware:**

| TypeCode | Name | C# Type | Notes |
|---|---|---|---|
| 3 | `tk_long` | `int` | signed 32-bit |
| 5 | `tk_ulong` | `uint` | unsigned 32-bit |
| 7 | `tk_double` | `double` | 8-byte IEEE 754 |
| 8 | `tk_boolean` | `bool` | 1-byte octet |
| 18 | `tk_string` | `string` | length-prefixed + NUL |

---

## SYSTEM_EXCEPTION format

When `reply_status == 2`:

```
exception_id    String   e.g. "IDL:omg.org/CORBA/BAD_OPERATION:1.0"
minor_code      ULong    implementation-specific detail
completed       ULong    0=NO, 1=YES, 2=MAYBE
```

Common exceptions seen:
- `BAD_OPERATION` — method not supported on this object type
- `OBJECT_NOT_EXIST` — stale object reference (reconnect)
- `NO_PERMISSION` — write without acquiring master access

---

## Sources

- `Rofin-test.pcapng` — LaserConsole boot + pilot toggle
- `Rofin-AxisTest.pcapng` — ReferenceDrive + jog axis sequence
- `Components.dll` — IIOP.NET proxy DLL from official LaserConsole install,
  contains full IDL-derived type hierarchy (interface/method names, not docs)
  Location: Google Drive → `Rofin Laser/bin/LaserConsole/`
