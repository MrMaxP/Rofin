using System;
using System.Collections.Generic;

namespace LaserConsole.Services;

// Frame types for the app <-> Pico CDC link. See docs/lightburn-rofin-bridge.md §2.
public enum CdcFrameType : byte
{
    VendorOut = 0x01,   // Pico -> app: bytes LightBurn wrote to the fake board (EP 0x02)
    VendorIn  = 0x02,   // app -> Pico: bytes to hand LightBurn on EP 0x88
    Status    = 0x10,   // Pico -> app: payload[0] = 1 host mounted, 0 unmounted
}

public readonly struct CdcFrame
{
    public CdcFrameType Type    { get; }
    public byte[]        Payload { get; }

    public CdcFrame(CdcFrameType type, byte[] payload)
    {
        Type    = type;
        Payload = payload;
    }
}

// Encodes/decodes the length-prefixed A5 5A frame used on the serial link:
//   0xA5 0x5A  type(1)  len(2, LE)  payload(len)  xor_checksum(1)
// checksum = XOR of type, len_lo, len_hi, and every payload byte.
public static class CdcFrameCodec
{
    public const byte Preamble0 = 0xA5;
    public const byte Preamble1 = 0x5A;

    public static byte[] Encode(CdcFrameType type, ReadOnlySpan<byte> payload)
    {
        var len = (ushort)payload.Length;
        var lenLo = (byte)(len & 0xFF);
        var lenHi = (byte)(len >> 8);

        byte checksum = (byte)type;
        checksum ^= lenLo;
        checksum ^= lenHi;
        foreach (var b in payload) checksum ^= b;

        var frame = new byte[5 + payload.Length + 1];
        frame[0] = Preamble0;
        frame[1] = Preamble1;
        frame[2] = (byte)type;
        frame[3] = lenLo;
        frame[4] = lenHi;
        payload.CopyTo(frame.AsSpan(5));
        frame[^1] = checksum;
        return frame;
    }
}

// Rolling parser: feed raw serial bytes in, get complete/validated frames out.
// Resyncs on the A5 5A preamble and tolerates a read boundary landing mid-frame.
public sealed class CdcFrameParser
{
    readonly List<byte> _buf = new();

    public List<CdcFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data.ToArray());
        var frames = new List<CdcFrame>();

        while (true)
        {
            // Resync: drop bytes until we see the preamble (or run out).
            int start = -1;
            for (int i = 0; i + 1 < _buf.Count; i++)
            {
                if (_buf[i] == CdcFrameCodec.Preamble0 && _buf[i + 1] == CdcFrameCodec.Preamble1)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
            {
                // Keep the last byte in case it's a split preamble; drop the rest.
                if (_buf.Count > 1) _buf.RemoveRange(0, _buf.Count - 1);
                return frames;
            }
            if (start > 0) _buf.RemoveRange(0, start);

            // Need at least the 5-byte header to know the payload length.
            if (_buf.Count < 5) return frames;

            int len = _buf[3] | (_buf[4] << 8);
            int frameLen = 5 + len + 1;
            if (_buf.Count < frameLen) return frames;   // wait for more bytes

            var type = (CdcFrameType)_buf[2];
            var payload = new byte[len];
            for (int i = 0; i < len; i++) payload[i] = _buf[5 + i];

            byte checksum = (byte)type;
            checksum ^= _buf[3];
            checksum ^= _buf[4];
            foreach (var b in payload) checksum ^= b;

            byte received = _buf[5 + len];
            _buf.RemoveRange(0, frameLen);

            if (checksum != received)
                continue;   // bad checksum — drop and keep resyncing from what's left

            frames.Add(new CdcFrame(type, payload));
        }
    }
}
