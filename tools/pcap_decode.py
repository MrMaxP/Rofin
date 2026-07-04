#!/usr/bin/env python3
"""
tools/pcap_decode.py — Rofin EasyMark E10 GIOP/CORBA capture decoder

Decodes pcapng captures of the official LaserConsole client into human-readable
request/reply pairs and attribute-change events.

Background
----------
The controller speaks GIOP 1.2 (CORBA) over raw TCP on port 49160. Every
operation is a matched request/reply pair. The protocol is described in full
in docs/protocol.md; the known API operations are in docs/api-operations.md.

This script reads a pcapng file, reassembles the TCP streams on port 49160,
parses every GIOP message, matches replies to their requests by request_id,
and prints the resulting conversation.

Usage
-----
    python3 tools/pcap_decode.py <file.pcapng>
    python3 tools/pcap_decode.py <file.pcapng> --events  # only attribute events
    python3 tools/pcap_decode.py <file.pcapng> --ops     # skip polling/boilerplate
    python3 tools/pcap_decode.py <file.pcapng> --port N  # non-default server port

CDR alignment note
------------------
CDR alignment is measured from byte 0 of the GIOP message ("GIOP" magic).
The GIOP fixed header is 12 bytes.  CdrReader accepts a `base` parameter so
that `_align(n)` computes `(base + off)` rounded up to the next `n`-byte
boundary, then subtracts `base` again.  Use `base=12` when parsing request /
reply envelopes (whose first byte is GIOP byte 12) and `base=0` when parsing
stand-alone body blobs that already start on an 8-byte GIOP boundary (as the
spec guarantees for the argument / result section).
"""

import argparse
import struct
import sys
from typing import Iterator, Optional


# ---------------------------------------------------------------------------
# CDR reader
# ---------------------------------------------------------------------------

class CdrReader:
    """
    Stateful reader for CDR (Common Data Representation) byte streams.

    Parameters
    ----------
    data : bytes
        The buffer to read from.
    little_endian : bool
        Byte order for multi-byte primitives.
    base : int
        GIOP-absolute offset of data[0].  Used so _align() computes alignment
        from GIOP byte 0 rather than from the start of this buffer.
        Use 12 when reading request/reply envelopes (data = msg[12:]).
        Use 0 when reading body blobs that start at a GIOP 8-byte boundary.
    """

    def __init__(self, data: bytes, little_endian: bool = True, base: int = 0) -> None:
        self.data = data
        self.off = 0
        self.le = little_endian
        self.base = base            # GIOP-absolute offset of data[0]

    # ── alignment ─────────────────────────────────────────────────────────

    def _align(self, n: int) -> None:
        abs_off = self.base + self.off
        aligned = (abs_off + n - 1) & ~(n - 1)
        self.off = aligned - self.base

    # ── primitive reads ────────────────────────────────────────────────────

    def u8(self) -> int:
        v = self.data[self.off]; self.off += 1; return v

    def u16(self) -> int:
        self._align(2)
        v = struct.unpack_from('<H' if self.le else '>H', self.data, self.off)[0]
        self.off += 2; return v

    def u32(self) -> int:
        self._align(4)
        v = struct.unpack_from('<I' if self.le else '>I', self.data, self.off)[0]
        self.off += 4; return v

    def i32(self) -> int:
        self._align(4)
        v = struct.unpack_from('<i' if self.le else '>i', self.data, self.off)[0]
        self.off += 4; return v

    def f64(self) -> float:
        self._align(8)
        v = struct.unpack_from('<d' if self.le else '>d', self.data, self.off)[0]
        self.off += 8; return v

    def string(self) -> str:
        """CDR string: ULong length (including NUL) + chars + NUL."""
        n = self.u32()
        s = self.data[self.off:self.off + n].rstrip(b'\x00').decode('ascii', 'replace')
        self.off += n; return s

    def seq_octet(self) -> bytes:
        """CDR octet sequence: ULong length + raw bytes (no alignment on length)."""
        n = self.u32()
        b = self.data[self.off:self.off + n]; self.off += n; return b

    def bytes_n(self, n: int) -> bytes:
        b = self.data[self.off:self.off + n]; self.off += n; return b

    # ── CORBA any ─────────────────────────────────────────────────────────

    def any(self) -> str:
        """
        Decode a CORBA any: TypeCode (ULong) + value aligned per TypeCode.

        TypeCodes observed on this hardware (docs/protocol.md → CORBA any):
          3  tk_long     signed 32-bit
          5  tk_ulong    unsigned 32-bit
          7  tk_double   IEEE 754 double (8-byte aligned)
          8  tk_boolean  1-byte octet
          18 tk_string   length-prefixed CDR string
        """
        tc = self.u32()
        if tc == 3:  return f'long={self.i32()}'
        if tc == 5:  v = self.u32();  return f'ulong={v} (0x{v:04x})'
        if tc == 7:  return f'double={self.f64():.7g}'
        if tc == 8:  v = self.u8();   return f'bool={v}'
        if tc == 18: return f'str="{self.string()}"'
        return f'tc={tc}'

    # ── CORBA IOR ─────────────────────────────────────────────────────────

    def ior(self) -> tuple[str, str, int, bytes]:
        """
        Decode an IOR (Interoperable Object Reference).

        Returns (type_id, host, port, key).  Pulls the first TAG_INTERNET_IOP
        (tag=0) profile.  See docs/protocol.md → "Object references".
        """
        type_id = self.string()
        n_profiles = self.u32()
        host, port, key = '', 0, b''
        for _ in range(n_profiles):
            self._align(4)
            tag = self.u32()
            raw = self.seq_octet()
            if tag == 0 and not host:
                pr = CdrReader(raw, little_endian=(raw[0] == 1))
                pr.off = 1          # skip byte-order octet
                pr.off += 2         # skip major/minor IIOP version
                h = pr.string()
                pr._align(2)
                p = struct.unpack_from('<H' if pr.le else '>H', pr.data, pr.off)[0]
                pr.off += 2
                pr._align(4)
                klen = pr.u32(); k = pr.bytes_n(klen)
                host, port, key = h, p, k
        return type_id, host, port, key

    # ── utilities ──────────────────────────────────────────────────────────

    @property
    def remaining(self) -> bytes:
        return self.data[self.off:]


# ---------------------------------------------------------------------------
# pcapng / TCP reassembly
# ---------------------------------------------------------------------------

def _read_pcapng(path: str) -> list[bytes]:
    """Return the captured payload bytes for every Enhanced Packet Block."""
    with open(path, 'rb') as f:
        data = f.read()
    packets: list[bytes] = []
    pos = 0
    while pos + 8 <= len(data):
        btype = struct.unpack_from('<I', data, pos)[0]
        blen  = struct.unpack_from('<I', data, pos + 4)[0]
        if blen < 12 or pos + blen > len(data):
            break
        if btype == 6:              # Enhanced Packet Block
            body    = data[pos + 8: pos + blen - 4]
            caplen  = struct.unpack_from('<I', body, 12)[0]
            packets.append(body[20: 20 + caplen])
        pos += blen
    return packets


def _tcp_streams(packets: list[bytes], server_port: int) -> tuple[bytes, bytes]:
    """
    Reconstruct client→server and server→client TCP byte streams from raw
    Ethernet/IPv4/TCP packets.  Sorts by TCP sequence number; drops duplicates.
    """
    c2s: list[tuple[int, bytes]] = []
    s2c: list[tuple[int, bytes]] = []

    for pkt in packets:
        try:
            if len(pkt) < 34:
                continue
            if struct.unpack_from('>H', pkt, 12)[0] != 0x0800:
                continue                 # not IPv4
            ihl     = (pkt[14] & 0xF) * 4
            tcp_off = 14 + ihl
            if pkt[14 + 9] != 6:
                continue                 # not TCP
            src = struct.unpack_from('>H', pkt, tcp_off)[0]
            dst = struct.unpack_from('>H', pkt, tcp_off + 2)[0]
            seq = struct.unpack_from('>I', pkt, tcp_off + 4)[0]
            doff = ((pkt[tcp_off + 12] >> 4) & 0xF) * 4
            pay = pkt[tcp_off + doff:]
            if not pay:
                continue
            if dst == server_port:   c2s.append((seq, pay))
            elif src == server_port: s2c.append((seq, pay))
        except (IndexError, struct.error):
            pass

    def assemble(chunks: list[tuple[int, bytes]]) -> bytes:
        chunks.sort(key=lambda x: x[0])
        seen: set[int] = set()
        buf = b''
        for seq, pay in chunks:
            if seq not in seen:
                seen.add(seq)
                buf += pay
        return buf

    return assemble(c2s), assemble(s2c)


# ---------------------------------------------------------------------------
# GIOP message scanning
# ---------------------------------------------------------------------------

def _giop_scan(stream: bytes) -> Iterator[tuple[int, int, bool, int]]:
    """
    Yield (offset, msg_type, little_endian, body_len) for each GIOP message
    in the byte stream.

    The body_len is read using the byte order declared in the message flags
    (bit 0), which may differ per message (requests are typically big-endian,
    replies little-endian on this hardware).
    """
    i = 0
    while i + 12 <= len(stream):
        if stream[i:i + 4] != b'GIOP':
            i += 1; continue
        flags    = stream[i + 6]
        msg_type = stream[i + 7]
        le       = bool(flags & 1)
        body_len = struct.unpack_from('<I' if le else '>I', stream, i + 8)[0]
        if i + 12 + body_len > len(stream):
            break
        yield i, msg_type, le, body_len
        i += 12 + body_len


# ---------------------------------------------------------------------------
# GIOP envelope parsers
# ---------------------------------------------------------------------------

def _parse_request(msg: bytes, le: bool) -> tuple[int, str, bytes, bytes]:
    """
    Parse a GIOP 1.2 request and return (request_id, operation, object_key, arg_body).

    GIOP 1.2 request envelope (docs/protocol.md → "Request body layout"):
      request_id       ULong
      response_flags   Octet    (0x03 = SYNC_WITH_TARGET)
      reserved[3]      Octet×3
      TargetAddress    Short    (0 = KeyAddr)
      object_key       Sequence<octet>
      operation        String
      service_context  Sequence
      [align to 8 from GIOP byte 0]
      arguments        CDR
    """
    r = CdrReader(msg[12:], little_endian=le, base=12)
    req_id  = r.u32()
    r.off  += 4             # response_flags + reserved[3]
    r._align(2)
    disc = struct.unpack_from('<H' if le else '>H', r.data, r.off)[0]; r.off += 2
    key = b''
    if disc == 0:           # KeyAddr: key sequence length is 4-byte aligned
        key_len = r.u32()
        key = r.bytes_n(key_len)
    op = r.string()
    sc_count = r.u32()
    for _ in range(sc_count):
        r._align(4); r.off += 4     # context_id
        r._align(4); dlen = r.u32(); r.off += dlen
    r._align(8)             # aligned from GIOP byte 0 (base=12 handles this)
    return req_id, op, key, r.remaining


def _parse_reply(msg: bytes, le: bool) -> tuple[int, int, bytes]:
    """
    Parse a GIOP 1.2 reply and return (request_id, reply_status, result_body).

    GIOP 1.2 reply envelope (docs/protocol.md → "Reply body layout"):
      request_id       ULong
      reply_status     ULong  (0=NO_EXCEPTION, 1=USER_EXCEPTION,
                               2=SYSTEM_EXCEPTION, 3=LOCATION_FORWARD)
      service_context  Sequence
      [align to 8 from GIOP byte 0]
      body             CDR
    """
    r = CdrReader(msg[12:], little_endian=le, base=12)
    req_id = r.u32()
    status = r.u32()
    sc_count = r.u32()
    for _ in range(sc_count):
        r._align(4); r.off += 4
        r._align(4); dlen = r.u32(); r.off += dlen
    r._align(8)
    return req_id, status, r.remaining


# ---------------------------------------------------------------------------
# Operation body decoders
# ---------------------------------------------------------------------------

_VOID_OPS = frozenset({
    'SetAttribute', 'Jog', 'ReferenceDrive', 'RefreshMasterAccess',
    'SignalProgramLoaded', 'SignalProgramUnLoaded', 'SignalProgramStart',
    'SignalProgramStop', 'StopProgram', 'Synchronize',
    'WriteIOPort', 'WriteGreyTable', 'SetLaserParameters', 'ExecutePrimitives',
    'SwitchFieldCorr', 'Shutdown', 'Logout',
})
_IOR_REPLY_OPS = frozenset({
    'Login', 'GetMachineControl', 'GetLaser', 'GetAxesControl', 'GetIOControl',
})
_NOARG_OPS = frozenset({
    'GetMachineControl', 'GetLaser', 'GetAxesControl', 'GetAllComponents',
    'GetClassInfos', 'ReferenceDrive', 'GetIOControl',
    '_get_genericName', '_get_className', '_get_id', '_get_classInfo', '_get_roleLevel',
    'SignalProgramLoaded', 'SignalProgramUnLoaded', 'SignalProgramStart',
    'SignalProgramStop', 'StopProgram', 'Synchronize',
})


def decode_request(op: str, body: bytes, le: bool) -> str:
    """Return a human-readable summary of the request arguments."""
    if op in _NOARG_OPS:
        return f'{op}()'
    r = CdrReader(body, little_endian=le, base=0)
    try:
        if op == 'SetAttribute':
            name = r.string()
            tc   = r.u32()          # TypeCode: plain CDR ULong (4-byte aligned)
            val  = r.remaining
            if tc == 8:
                return f'SetAttribute("{name}", bool={val[0] if val else "?"})'
            if tc == 7:             # tk_double
                try: return f'SetAttribute("{name}", double={CdrReader(val, le, 0).f64():.7g})'
                except Exception: pass
            return f'SetAttribute("{name}", tc={tc}, val={val[:8].hex()})'
        if op == 'GetAttribute':
            return f'GetAttribute("{r.string()}")'
        if op == 'Jog':
            return f'Jog(axis={r.u32()}, direction={r.u32()})'
        if op == 'PullEvents2':
            return f'PullEvents2({r.u32()})'
        if op == 'PullEvents':
            raw = r.remaining
            return f'PullEvents({raw.hex()[:16]})'
        if op == 'RefreshMasterAccess':
            return f'RefreshMasterAccess({r.u32()})'
        if op == 'ReadIOPort':
            a = r.u32(); b = r.u32()
            try: c = r.u32(); return f'ReadIOPort({a}, {b}, {c})'
            except Exception: return f'ReadIOPort({a}, {b})'
        if op == 'Login':
            user = r.string(); pw = r.string()
            return f'Login("{user}", hash="{pw[:8]}...")'
        if op == 'Logout':
            return f'Logout({r.u32()})'
        if op in ('WriteIOPort', 'SwitchFieldCorr', 'WriteGreyTable',
                  'SetLaserParameters', 'ExecutePrimitives'):
            return f'{op}({body[:24].hex()})'
        return f'{op}({body[:24].hex()})'
    except Exception as exc:
        return f'{op}(?) [{exc}]'


def decode_reply(op: str, status: int, body: bytes, le: bool) -> str:
    """Return a human-readable summary of the reply result."""
    _STATUS = {0: 'NO_EXCEPTION', 1: 'USER_EXCEPTION',
               2: 'SYSTEM_EXCEPTION', 3: 'LOCATION_FORWARD'}
    if status != 0:
        r = CdrReader(body, little_endian=le, base=0)
        try:
            return f'{_STATUS.get(status, status)}: {r.string()}'
        except Exception:
            return _STATUS.get(status, str(status))

    if not body:
        return '→ void'

    r = CdrReader(body, little_endian=le, base=0)
    try:
        if op == 'GetAttribute':
            return f'→ {r.any()}'
        if op == 'GetAllComponents':
            return f'→ {r.u32()} component IORs'
        if op == '_get_genericName':
            return f'→ "{r.string()}"'
        if op == '_get_className':
            return f'→ "{r.string()}"'
        if op == '_get_id':
            return f'→ id={r.u32()}'
        if op in _VOID_OPS:
            return '→ void'
        if op in _IOR_REPLY_OPS:
            _tid, host, port, key = r.ior()
            return f'→ IOR {host}:{port} key={key.hex()[:12]}...'
        if op == 'ReadIOPort':
            raw = r.seq_octet()     # reply is sequence<octet>, not sequence<ULong>
            return f'→ {raw.hex()} ({len(raw)} bytes)'
        if op == 'PullEvents2':
            return _decode_events_summary(body, le)
        return f'→ {body[:24].hex()}'
    except Exception as exc:
        return f'→ ? [{exc}]'


# ---------------------------------------------------------------------------
# PullEvents2 event payload decoder
# ---------------------------------------------------------------------------

def _decode_events_summary(body: bytes, le: bool) -> str:
    """
    Decode a PullEvents2 reply body and return a multi-line summary string.

    Each event in the CDR sequence has this layout (body-local base=0 is valid
    here because the body starts at a GIOP 8-byte boundary):

      component_id  ULong   matches _get_id() values for the component
      attr_id       ULong   per-component attribute instance index
      reserved      ULong   always 0
      attr_name     String  same key as used in GetAttribute / SetAttribute
      value         Any     TypeCode + CDR-encoded value

    Events are fired whenever the controller detects an attribute change.
    """
    r = CdrReader(body, little_endian=le, base=0)
    try:
        count = r.u32()
    except Exception:
        return '→ (empty)'

    if count == 0:
        return '→ 0 events'

    lines = [f'→ {count} event{"s" if count != 1 else ""}:']
    for _ in range(count):
        try:
            comp_id = r.u32()
            attr_id = r.u32()
            r.u32()                     # reserved
            name    = r.string()
            val_str = r.any()
            lines.append(f'        [{comp_id:3d}/{attr_id:3d}]  {name:<28s} {val_str}')
        except Exception as exc:
            lines.append(f'        (parse error: {exc})')
            break

    return '\n'.join(lines)


def decode_events(body: bytes, le: bool) -> list[dict]:
    """
    Parse a PullEvents2 body into a list of event dicts for programmatic use.

    Returns a list of dicts with keys:
      component_id  int    component from _get_id()
      attr_id       int    attribute instance index
      name          str    attribute name
      tc            int    CORBA TypeCode discriminant
      value         any    decoded Python value
    """
    r = CdrReader(body, little_endian=le, base=0)
    events = []
    try:
        count = r.u32()
    except Exception:
        return events
    for _ in range(count):
        try:
            comp_id = r.u32()
            attr_id = r.u32()
            r.u32()
            name    = r.string()
            tc      = r.u32()
            val: object = None
            if   tc == 3:  val = r.i32()
            elif tc == 5:  val = r.u32()
            elif tc == 7:  val = r.f64()
            elif tc == 8:  val = r.u8()
            elif tc == 18: val = r.string()
            events.append({'component_id': comp_id, 'attr_id': attr_id,
                           'name': name, 'tc': tc, 'value': val})
        except Exception:
            break
    return events


# ---------------------------------------------------------------------------
# Component name cache
# ---------------------------------------------------------------------------

def _build_name_cache(c2s: bytes, s2c: bytes) -> dict[bytes, str]:
    """
    Walk request/reply streams and build an object-key → display-name map.

    Two sources:
    1. _get_genericName replies: map the queried key → hardware name (e.g. "Laser")
    2. IOR-returning operations: map the *returned* object key → operation name
       (e.g. GetIOControl reply IOR key → "IOControl")
    """
    # Map request_id → (op, key) from the client stream
    req_info: dict[int, tuple[str, bytes]] = {}
    for off, mtype, le, blen in _giop_scan(c2s):
        if mtype != 0:
            continue
        try:
            req_id, op, key, _ = _parse_request(c2s[off: off + 12 + blen], le)
            req_info[req_id] = (op, key)
        except Exception:
            pass

    # Ordered list of _get_genericName requests (to match replies positionally)
    gname_order: list[tuple[int, bytes]] = [
        (rid, key) for rid, (op, key) in req_info.items() if op == '_get_genericName'
    ]
    gname_order.sort()      # ascending request_id order

    # IOR-returning operations → label for the returned object's key
    _IOR_OP_LABELS: dict[str, str] = {
        'Login':            'SystemCtrl',
        'GetMachineControl': 'MachineCtrl',
        'GetLaser':         'Laser',
        'GetAxesControl':   'AxesCtrl',
        'GetIOControl':     'IOControl',
        'GetProgramControl': 'ProgramCtrl',
    }

    # Build key→name from replies
    cache: dict[bytes, str] = {}
    gname_idx = 0
    for off, mtype, le, blen in _giop_scan(s2c):
        if mtype != 1:
            continue
        try:
            req_id, status, body = _parse_reply(s2c[off: off + 12 + blen], le)
            if status != 0:
                continue
            op_key = req_info.get(req_id)
            if not op_key:
                continue
            op, _ = op_key
            if op == '_get_genericName':
                name = CdrReader(body, little_endian=le, base=0).string()
                if gname_idx < len(gname_order):
                    _, key = gname_order[gname_idx]
                    cache[key] = name
                    gname_idx += 1
            elif op in _IOR_OP_LABELS:
                try:
                    _tid, _host, _port, key = CdrReader(body, little_endian=le, base=0).ior()
                    if key:
                        cache.setdefault(key, _IOR_OP_LABELS[op])
                except Exception:
                    pass
        except Exception:
            pass

    return cache


def _key_label(key: bytes, cache: dict[bytes, str]) -> str:
    if key in cache:
        return f'[{cache[key]}]'
    # Extract object ID from CORBA object key (session-specific ULong at byte 15)
    if len(key) >= 19:
        obj_id = struct.unpack_from('<I', key, 15)[0]
        return f'[obj#{obj_id}]'
    return f'[{key.hex()[:10]}]'


# ---------------------------------------------------------------------------
# Main analysis loop
# ---------------------------------------------------------------------------

# Operations that are uninteresting in --ops mode (setup / polling boilerplate)
_BORING_OPS = frozenset({
    '_get_genericName', '_get_className', '_get_id', '_get_classInfo',
    'GetAllComponents', 'GetMachineControl', 'GetLaser', 'GetAxesControl',
    'GetIOControl', 'Login', 'GetClassInfos', 'PullEvents', 'PullEvents2',
    'ReadIOPort',
})


def analyse(path: str, server_port: int, mode: str) -> None:
    packets = _read_pcapng(path)
    c2s, s2c = _tcp_streams(packets, server_port)

    if not c2s and not s2c:
        print(f'No TCP traffic found on port {server_port}. '
              f'Try --port with a different value.', file=sys.stderr)
        sys.exit(1)

    name_cache = _build_name_cache(c2s, s2c)

    # ── parse requests ────────────────────────────────────────────────────
    requests: dict[int, tuple[str, bytes, bool, bytes]] = {}  # id→(op,key,le,body)
    for off, mtype, le, blen in _giop_scan(c2s):
        if mtype != 0:
            continue
        try:
            req_id, op, key, body = _parse_request(c2s[off: off + 12 + blen], le)
            requests[req_id] = (op, key, le, body)
        except Exception:
            pass

    # ── parse replies ─────────────────────────────────────────────────────
    replies: dict[int, tuple[int, bool, bytes]] = {}  # id→(status,le,body)
    for off, mtype, le, blen in _giop_scan(s2c):
        if mtype != 1:
            continue
        try:
            req_id, status, body = _parse_reply(s2c[off: off + 12 + blen], le)
            replies[req_id] = (status, le, body)
        except Exception:
            pass

    # ── print ─────────────────────────────────────────────────────────────
    print(f'=== {path}  (server port {server_port}) ===')
    print(f'    {len(requests)} requests  |  {len(replies)} replies  '
          f'|  {sum(1 for r in requests if r in replies)} matched\n')

    for req_id in sorted(requests):
        op, key, le, req_body = requests[req_id]
        label = _key_label(key, name_cache)

        if mode == 'events' and op != 'PullEvents2':
            continue
        if mode == 'ops' and op in _BORING_OPS:
            continue

        req_str = decode_request(op, req_body, le)
        print(f'  >> #{req_id:<5d} {label:<14s}  {req_str}')

        if req_id in replies:
            status, rle, rep_body = replies[req_id]

            # In events mode, skip empty PullEvents2 replies
            if mode == 'events':
                evs = decode_events(rep_body, rle)
                if not evs:
                    continue

            rep_str = decode_reply(op, status, rep_body, rle)
            lines = rep_str.split('\n')
            print(f'  << #{req_id:<5d}               {lines[0]}')
            for line in lines[1:]:
                print(f'                              {line}')
        else:
            print(f'  << #{req_id:<5d}               (no reply in capture)')

        print()


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> None:
    p = argparse.ArgumentParser(
        description='Decode Rofin EasyMark E10 GIOP/CORBA pcapng captures.',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
modes (mutually exclusive):
  (default)  All messages with full event payloads expanded.
  --ops      Control operations only; skip polling and boilerplate.
  --events   Only PullEvents2 replies that carry at least one event.

examples:
  python3 tools/pcap_decode.py WiresharkCaptures/ws-focusfind.pcapng --ops
  python3 tools/pcap_decode.py WiresharkCaptures/ws-focusfind.pcapng --events
  python3 tools/pcap_decode.py WiresharkCaptures/Rofin-AxisTest.pcapng --ops
        """)
    p.add_argument('pcapng', help='Path to .pcapng capture file')
    p.add_argument('--port', type=int, default=49160,
                   help='Server TCP port (default: 49160)')
    p.add_argument('--ops',    action='store_true',
                   help='Show control operations only (skip polling)')
    p.add_argument('--events', action='store_true',
                   help='Show only non-empty PullEvents2 event payloads')
    args = p.parse_args()

    if args.ops and args.events:
        p.error('--ops and --events are mutually exclusive')

    mode = 'events' if args.events else ('ops' if args.ops else 'all')
    analyse(args.pcapng, args.port, mode)


if __name__ == '__main__':
    main()
