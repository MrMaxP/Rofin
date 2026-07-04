using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace LaserConsole.Services;

// One persistent GIOP/IIOP TCP connection.  All request IDs auto-increment.
sealed class GiopConn : IDisposable
{
    readonly TcpClient     _tcp;
    readonly NetworkStream _s;
    uint _reqId = 1;

    public GiopConn(string host, int port)
    {
        _tcp = new TcpClient { NoDelay = true };
        _tcp.Connect(host, port);
        _tcp.ReceiveTimeout = 5000;
        _tcp.SendTimeout    = 2000;
        _s = _tcp.GetStream();
    }

    // ── Request builder ────────────────────────────────────────────────────

    byte[] BuildRequest(byte[] objKey, string op, Action<Cdr> writeArgs, int minor)
    {
        var c = new Cdr();
        for (int i = 0; i < 12; i++) c.Octet(0);      // header placeholder

        if (minor == 2)
        {
            c.ULong(_reqId);
            c.Octet(0x03);                             // SYNC_WITH_TARGET
            c.Octet(0); c.Octet(0); c.Octet(0);       // reserved[3]
            c.UShort(0);                               // KeyAddr discriminator
            c.OctetSeq(objKey);
            c.Str(op);
            c.ULong(0);                                // empty service_context
            c.Align(8);                                // 1.2: body 8-aligned from msg start
            writeArgs(c);
        }
        else  // GIOP 1.0 (naming bootstrap fallback)
        {
            c.ULong(0);                                // service_context
            c.ULong(_reqId);
            c.Octet(1);                                // response_expected
            c.OctetSeq(objKey);
            c.Str(op);
            c.ULong(0);                                // requesting_principal
            writeArgs(c);
        }

        var msg = c.ToArray();
        msg[0] = (byte)'G'; msg[1] = (byte)'I'; msg[2] = (byte)'O'; msg[3] = (byte)'P';
        msg[4] = 1;  msg[5] = (byte)minor;
        msg[6] = 0;  msg[7] = 0;                      // flags=big-endian; type=Request
        uint body = (uint)(msg.Length - 12);
        msg[8] = (byte)(body >> 24); msg[9]  = (byte)(body >> 16);
        msg[10]= (byte)(body >>  8); msg[11] = (byte) body;
        return msg;
    }

    // ── Low-level I/O ──────────────────────────────────────────────────────

    void ReadFully(byte[] buf, int off, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = _s.Read(buf, off + got, len - got);
            if (n <= 0) throw new IOException("connection closed mid-message");
            got += n;
        }
    }

    (uint status, CdrR body) Invoke(byte[] objKey, string op, Action<Cdr> writeArgs, int minor)
    {
        var msg = BuildRequest(objKey, op, writeArgs, minor);
        _s.Write(msg, 0, msg.Length);
        _reqId++;

        var hdr = new byte[12];
        ReadFully(hdr, 0, 12);
        if (hdr[0] != 'G' || hdr[1] != 'I' || hdr[2] != 'O' || hdr[3] != 'P')
            throw new IOException("bad GIOP magic in reply");

        int  replyMinor = hdr[5];
        bool be         = (hdr[6] & 0x01) == 0;
        uint size = be
            ? ((uint)hdr[8]<<24 | (uint)hdr[9]<<16 | (uint)hdr[10]<<8 | hdr[11])
            : ((uint)hdr[11]<<24 | (uint)hdr[10]<<16 | (uint)hdr[9]<<8 | hdr[8]);

        var full = new byte[12 + size];
        Array.Copy(hdr, full, 12);
        ReadFully(full, 12, (int)size);

        if (hdr[7] != 1)
            throw new IOException($"unexpected GIOP message type {hdr[7]}");

        var r = new CdrR(full, 12, be);
        uint status;
        if (replyMinor == 2)
        {
            r.ULong();                    // request_id
            status = r.ULong();           // reply_status
            uint nsc = r.ULong();         // service_context count
            for (uint i = 0; i < nsc; i++) { r.ULong(); r.OctetSeq(); }
            r.Align(8);
        }
        else
        {
            uint nsc = r.ULong();
            for (uint i = 0; i < nsc; i++) { r.ULong(); r.OctetSeq(); }
            r.ULong();                    // request_id
            status = r.ULong();
        }
        return (status, r);
    }

    CdrR InvokeOk(byte[] objKey, string op, Action<Cdr> writeArgs, int minor = 2)
    {
        var (st, body) = Invoke(objKey, op, writeArgs, minor);
        if (st == 1)
        {
            try   { throw new ApplicationException($"{op}: USER_EXCEPTION {body.Str()}"); }
            catch (ApplicationException) { throw; }
            catch (Exception ex) { throw new ApplicationException($"{op}: USER_EXCEPTION (parse failed: {ex.Message})"); }
        }
        if (st == 2)
        {
            try
            {
                string id = body.Str();
                uint   mc = body.ULong();
                uint   cs = body.ULong();
                throw new ApplicationException($"{op}: SYSTEM_EXCEPTION {id} minor={mc} cs={cs}");
            }
            catch (ApplicationException) { throw; }
            catch (Exception ex)
            {
                throw new ApplicationException($"{op}: SYSTEM_EXCEPTION (could not parse: {ex.Message})");
            }
        }
        if (st != 0)
            throw new ApplicationException($"{op}: reply_status={st}");
        return body;
    }

    // ── Typed operations ───────────────────────────────────────────────────

    // CosNaming resolve — handles LOCATION_FORWARD transparently.
    public ObjRef ResolveName(byte[] namingKey, string name, int minor)
    {
        var (st, body) = Invoke(namingKey, "resolve", c =>
        {
            c.ULong(1);
            c.Str(name);
            c.Str("");
        }, minor);

        if (st == 3)  // LOCATION_FORWARD
        {
            var fwd = ReadIor(body);
            body = InvokeOk(fwd.Key, "resolve", c => { c.ULong(1); c.Str(name); c.Str(""); }, minor);
        }
        else if (st != 0)
            throw new ApplicationException($"resolve: reply_status={st}");

        return ReadIor(body);
    }

    public ObjRef Login(byte[] ctrlKey, string user, string hash, bool flag)
    {
        var body = InvokeOk(ctrlKey, "Login", c =>
        {
            c.Str(user);
            c.Str(hash);
            c.Bool(flag);
        });
        return ReadIor(body);
    }

    public ObjRef GetMachineControl(byte[] sysCtrlKey)
        => ReadIor(InvokeOk(sysCtrlKey, "GetMachineControl", _ => { }));

    public ObjRef GetLaser(byte[] machCtrlKey)
        => ReadIor(InvokeOk(machCtrlKey, "GetLaser", _ => { }));

    public void SetAttribute(byte[] key, string name, byte[] typeCodeBytes, byte[] valueBytes)
        => InvokeOk(key, "SetAttribute", c => { c.Str(name); c.Raw(typeCodeBytes); c.Raw(valueBytes); });

    // Returns null if the server doesn't support GetAttribute or the TypeCode is not tk_boolean.
    public bool? GetBoolAttribute(byte[] key, string name)
    {
        var body = InvokeOk(key, "GetAttribute", c => c.Str(name));
        uint kind = body.ULong();
        if (kind != 8) return null;   // not tk_boolean — don't try to parse
        return body.Bool();
    }

    // Reads any attribute and returns it as a formatted string.
    // TypeCodes observed on this hardware: 3=tk_long, 5=tk_ulong, 7=tk_double,
    // 8=tk_boolean, 18=tk_string. Returns null on SYSTEM_EXCEPTION (e.g. bad op).
    public string? GetAttributeString(byte[] key, string name)
    {
        var (st, body) = Invoke(key, "GetAttribute", c => c.Str(name), 2);
        if (st != 0) return null;
        uint tc = body.ULong();
        return tc switch
        {
            3  => ((int)body.ULong()).ToString(),
            5  => body.ULong().ToString(),
            7  => body.Double().ToString("G6"),
            8  => body.Bool() ? "true" : "false",
            18 => body.Str(),
            _  => $"(tc={tc})",
        };
    }

    // Component tree enumeration — verified against the real LaserConsole boot
    // capture (Rofin-test.pcapng): the official client walks the whole device
    // tree by calling GetAllComponents() on SystemControl, then recursively on
    // each returned component, reading _get_className / _get_genericName at
    // every node. All four ops take zero arguments.
    public List<ObjRef> GetAllComponents(byte[] key)
    {
        var body = InvokeOk(key, "GetAllComponents", _ => { });
        uint count = body.ULong();
        var list = new List<ObjRef>((int)count);
        for (uint i = 0; i < count; i++)
        {
            try { list.Add(ReadIor(body)); }
            catch { /* component with no usable IIOP profile — skip it */ }
        }
        return list;
    }

    public string GetComponentClassName(byte[] key)
        => InvokeOk(key, "_get_className", _ => { }).Str();

    public string GetComponentGenericName(byte[] key)
        => InvokeOk(key, "_get_genericName", _ => { }).Str();

    // Axis control — verified from Rofin-AxisTest.pcapng
    public ObjRef GetAxesControl(byte[] machCtrlKey)
        => ReadIor(InvokeOk(machCtrlKey, "GetAxesControl", _ => { }));

    // Jog(axis, direction) — 2 args confirmed from Rofin-AxisTest.pcapng.
    // axis=2 is the LIF axis index within AxesControl (constant).
    // direction: 0=STOP, 1=slow_up (bed toward laser), 3=fast_up
    //            2=slow_down (inferred), 4=fast_down (inferred)
    public void Jog(byte[] axesCtrlKey, uint direction)
        => InvokeOk(axesCtrlKey, "Jog", c => { c.ULong(2); c.ULong(direction); });

    public void ReferenceDrive(byte[] axesCtrlKey)
        => InvokeOk(axesCtrlKey, "ReferenceDrive", _ => { });

    public double? GetDoubleAttribute(byte[] key, string name)
    {
        var (st, body) = Invoke(key, "GetAttribute", c => c.Str(name), 2);
        if (st != 0) return null;
        uint tc = body.ULong();
        return tc == 7 ? body.Double() : null;
    }

    // ── IOR parser ─────────────────────────────────────────────────────────
    // Picks the first non-loopback TAG_INTERNET_IOP profile (falls back to loopback).

    static ObjRef ReadIor(CdrR r)
    {
        string typeId = r.Str();
        uint   nprof  = r.ULong();
        ObjRef? chosen = null, loopback = null;

        for (uint i = 0; i < nprof; i++)
        {
            uint   tag  = r.ULong();
            byte[] prof = r.OctetSeq();
            if (tag != 0) continue;                    // 0 = TAG_INTERNET_IOP

            var pc = new CdrR(prof, 0, prof[0] == 0); // encaps byte-order byte
            pc.Octet();                                // skip byte-order byte
            pc.Octet(); pc.Octet();                    // IIOP version major/minor
            string host = pc.Str();
            ushort port = pc.UShort();
            byte[] key  = pc.OctetSeq();

            var oref = new ObjRef { TypeId = typeId, Host = host, Port = port, Key = key };
            if (host is "127.0.0.1" or "localhost") loopback ??= oref;
            else                                    chosen  ??= oref;
        }

        return chosen ?? loopback
            ?? throw new ApplicationException($"IOR for {typeId} contained no IIOP profile");
    }

    public void Dispose()
    {
        try { _s?.Dispose(); } catch { }
        _tcp?.Close();
    }
}
