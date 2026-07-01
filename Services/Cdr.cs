using System;
using System.Collections.Generic;
using System.Text;

namespace LaserConsole.Services;

// CDR writer — always big-endian (what the Rofin controller expects on the wire).
// Pre-write 12 header bytes before any payload so _b.Count equals the
// message-absolute offset and alignment arithmetic works without adjustment.
sealed class Cdr
{
    readonly List<byte> _b = new(256);

    public int    Count   => _b.Count;
    public byte[] ToArray() => _b.ToArray();

    public void Align(int n) { while (_b.Count % n != 0) _b.Add(0); }
    public void Octet(byte v)  => _b.Add(v);
    public void Raw(byte[] v)  => _b.AddRange(v);
    public void Bool(bool v)   => _b.Add((byte)(v ? 1 : 0));

    public void UShort(ushort v)
    {
        Align(2);
        _b.Add((byte)(v >> 8)); _b.Add((byte)v);
    }

    public void ULong(uint v)
    {
        Align(4);
        _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16));
        _b.Add((byte)(v >>  8)); _b.Add((byte) v);
    }

    // CORBA string: ulong length INCLUDING the trailing NUL + bytes + NUL
    public void Str(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        ULong((uint)(bytes.Length + 1));
        _b.AddRange(bytes);
        _b.Add(0);
    }

    // sequence<octet>: ulong length + raw bytes, NO NUL
    public void OctetSeq(byte[] v)
    {
        ULong((uint)v.Length);
        _b.AddRange(v);
    }
}

// CDR reader — honours the byte-order flag taken from the reply's own header.
sealed class CdrR
{
    readonly byte[] _b;
    int _p;
    readonly bool _be;

    public CdrR(byte[] buf, int start, bool bigEndian)
    {
        _b = buf; _p = start; _be = bigEndian;
    }

    public int Pos => _p;

    public void   Align(int n) { while (_p % n != 0) _p++; }
    public byte   Octet() => _b[_p++];
    public bool   Bool()  => _b[_p++] != 0;

    public byte[] Bytes(int n)
    {
        var r = new byte[n];
        Array.Copy(_b, _p, r, 0, n);
        _p += n;
        return r;
    }

    public ushort UShort()
    {
        Align(2);
        ushort v = _be ? (ushort)((_b[_p] << 8) | _b[_p + 1])
                       : (ushort)((_b[_p + 1] << 8) | _b[_p]);
        _p += 2;
        return v;
    }

    public uint ULong()
    {
        Align(4);
        uint v = _be
            ? ((uint)_b[_p] << 24 | (uint)_b[_p+1] << 16 | (uint)_b[_p+2] << 8 | _b[_p+3])
            : ((uint)_b[_p+3] << 24 | (uint)_b[_p+2] << 16 | (uint)_b[_p+1] << 8 | _b[_p]);
        _p += 4;
        return v;
    }

    public string Str()
    {
        uint n = ULong();
        var s = Encoding.ASCII.GetString(_b, _p, (int)n);
        _p += (int)n;
        return s.TrimEnd('\0');
    }

    public byte[] OctetSeq()
    {
        uint n = ULong();
        return Bytes((int)n);
    }

    public double Double()
    {
        Align(8);
        var b = Bytes(8);
        // Byte order already matches CdrR's own endianness; on little-endian hosts
        // (and the server replies LE) this converts directly.
        return _be
            ? BitConverter.Int64BitsToDouble(
                  (long)((ulong)b[0]<<56|(ulong)b[1]<<48|(ulong)b[2]<<40|(ulong)b[3]<<32|
                         (ulong)b[4]<<24|(ulong)b[5]<<16|(ulong)b[6]<<8|(ulong)b[7]))
            : BitConverter.Int64BitsToDouble(
                  (long)((ulong)b[7]<<56|(ulong)b[6]<<48|(ulong)b[5]<<40|(ulong)b[4]<<32|
                         (ulong)b[3]<<24|(ulong)b[2]<<16|(ulong)b[1]<<8|(ulong)b[0]));
    }
}
