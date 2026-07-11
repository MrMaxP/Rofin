using System;

namespace LaserConsole.Services;

// Hardware-free checks for the CDC framing codec and the LMC connect-handshake
// responder — run with `--bridge-selftest`. Mirrors the bench scripts'
// --codec-only / --selftest flags mentioned in docs/lightburn-rofin-bridge.md.
public static class BridgeSelfTest
{
    public static bool Run()
    {
        bool ok = true;
        ok &= Check("frame round-trip",        FrameRoundTrip);
        ok &= Check("parser resyncs on garbage", ParserResyncsOnGarbage);
        ok &= Check("parser splits multi-frame reads", ParserHandlesSplitReads);
        ok &= Check("GetVersion reply",         GetVersionReply);
        ok &= Check("GetSerialNo reply",        GetSerialNoReply);
        ok &= Check("WriteCorLine has no reply", WriteCorLineNoReply);
        ok &= Check("list opcode has no reply", ListOpcodeNoReply);
        ok &= Check("generic ack for unknown opcode", GenericAck);

        Console.WriteLine(ok ? "\nAll bridge self-tests passed." : "\nSome bridge self-tests FAILED.");
        return ok;
    }

    static bool Check(string name, Func<bool> test)
    {
        bool pass;
        string? error = null;
        try { pass = test(); }
        catch (Exception ex) { pass = false; error = ex.Message; }
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}" + (error is null ? "" : $"  ({error})"));
        return pass;
    }

    static bool FrameRoundTrip()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = CdcFrameCodec.Encode(CdcFrameType.VendorOut, payload);

        var parser = new CdcFrameParser();
        var frames = parser.Feed(encoded);
        if (frames.Count != 1) return false;
        var f = frames[0];
        return f.Type == CdcFrameType.VendorOut && f.Payload.AsSpan().SequenceEqual(payload);
    }

    static bool ParserResyncsOnGarbage()
    {
        var payload = new byte[] { 0xAA, 0xBB };
        var encoded = CdcFrameCodec.Encode(CdcFrameType.Status, payload);
        var garbage = new byte[] { 0x00, 0xFF, 0x12, 0x34 };

        var input = new byte[garbage.Length + encoded.Length];
        garbage.CopyTo(input, 0);
        encoded.CopyTo(input, garbage.Length);

        var parser = new CdcFrameParser();
        var frames = parser.Feed(input);
        return frames.Count == 1 && frames[0].Type == CdcFrameType.Status
            && frames[0].Payload.AsSpan().SequenceEqual(payload);
    }

    static bool ParserHandlesSplitReads()
    {
        var cmd = Encode12(LmcOpcodes.GetVersion, 0, 0, 0, 0, 0);
        var frame = CdcFrameCodec.Encode(CdcFrameType.VendorOut, cmd);

        var parser = new CdcFrameParser();
        var mid = frame.Length / 2;
        var first  = parser.Feed(frame.AsSpan(0, mid));
        var second = parser.Feed(frame.AsSpan(mid));

        if (first.Count != 0) return false;
        if (second.Count != 1) return false;
        return second[0].Payload.AsSpan().SequenceEqual(cmd);
    }

    static byte[] Encode12(ushort opcode, ushort p1, ushort p2, ushort p3, ushort p4, ushort p5)
    {
        var b = new byte[12];
        void W(int i, ushort v) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); }
        W(0, opcode); W(2, p1); W(4, p2); W(6, p3); W(8, p4); W(10, p5);
        return b;
    }

    static bool GetVersionReply()
    {
        var cmd = LmcCommand.Parse(Encode12(LmcOpcodes.GetVersion, 0, 0, 0, 0, 0));
        var reply = LmcResponder.BuildReply(cmd);
        if (reply is null || reply.Length != 8) return false;
        ushort w0 = (ushort)(reply[0] | (reply[1] << 8));
        ushort w3 = (ushort)(reply[6] | (reply[7] << 8));
        // w3 is the live status bitmask galvoplotter's is_ready()/is_busy() test —
        // READY must be set and BUSY must be clear, or LightBurn reads the
        // board as permanently busy and flaps its connection state.
        bool statusOk = (w3 & LmcResponder.StatusReady) != 0 && (w3 & LmcResponder.StatusBusy) == 0;
        return w0 == 0x0454 && statusOk;
    }

    static bool GetSerialNoReply()
    {
        var cmd = LmcCommand.Parse(Encode12(LmcOpcodes.GetSerialNo, 0, 0, 0, 0, 0));
        var reply = LmcResponder.BuildReply(cmd);
        if (reply is null || reply.Length != 8) return false;
        ushort w0 = (ushort)(reply[0] | (reply[1] << 8));
        return w0 == 1;
    }

    static bool WriteCorLineNoReply()
    {
        var cmd = LmcCommand.Parse(Encode12(LmcOpcodes.WriteCorLine, 1, 2, 3, 4, 5));
        return LmcResponder.BuildReply(cmd) is null;
    }

    static bool ListOpcodeNoReply()
    {
        var cmd = LmcCommand.Parse(Encode12(LmcOpcodes.ListMarkTo, 0x8000, 0x8000, 0, 0, 0));
        return LmcOpcodes.IsListOpcode(cmd.Opcode) && LmcResponder.BuildReply(cmd) is null;
    }

    static bool GenericAck()
    {
        var cmd = LmcCommand.Parse(Encode12(LmcOpcodes.SetStandby, 0, 0, 0, 0, 0));
        var reply = LmcResponder.BuildReply(cmd);
        return reply != null && reply.Length == 8 && Array.TrueForAll(reply, b => b == 0);
    }
}
