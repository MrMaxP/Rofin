// ---------------------------------------------------------------------------
//  RofinPilotTest - minimal hand-rolled CORBA/GIOP client for the Rofin
//  EasyMark E10, used to PROVE the controller can be commanded from our own
//  code by toggling the PILOT (alignment) laser on and off.
//
//  This deliberately does the safest possible thing: it only writes the
//  boolean attribute "pilotOn". It never sends ExecutePrimitives,
//  SetLaserParameters, beamOn, or starts a program - so NO marking beam is
//  ever fired. The pilot is the low-power visible pointer.
//
//  Everything below was reconstructed byte-for-byte from two Wireshark
//  captures (WS-PilotOnOff / WS-LaserConsoleBoot). No external libraries:
//  raw TCP sockets + a small CDR/GIOP encoder, so it ports cleanly to Python
//  later for the LightBurn pipeline.
//
//  Protocol summary (proven on the wire):
//    1. TCP connect to <host>:10050  (the bootstrap / naming endpoint)
//       -> CosNaming resolve(["Controller"]) -> Controller IOR @ <host>:49160
//    2. TCP connect to <host>:49160  (one connection; objects addressed by key)
//       -> Controller.Login("operator", <md5-hash>, false) -> SystemControl ref
//       -> SystemControl.GetMachineControl()               -> MachineControl ref
//       -> MachineControl.GetLaser()                       -> Laser IOR
//       -> Laser.SetAttribute("pilotOn", any{boolean})
//
//  Wire facts that matter:
//    * GIOP 1.2, requests sent BIG-ENDIAN (matches LaserConsole's command path).
//    * Reply byte order is read from each reply's own flag (server replied LE).
//    * GIOP 1.2 request body is 8-byte aligned w.r.t. the START of the message
//      (the 12-byte header counts). We get this for free by writing positions
//      absolutely (12 placeholder header bytes first).
//    * CORBA strings: length INCLUDES the trailing NUL. Object keys are raw
//      octet sequences (NO trailing NUL).
//    * SetAttribute(string name, any value); for pilotOn the any is just
//      TypeCode kind tk_boolean (= 8, a 4-byte ulong) followed by 1 octet.
//
//  Build (cross-platform, requires .NET 10 SDK):
//      dotnet build -c Release                  (see RofinPilotTest.csproj)
//
//  Run:
//      RofinPilotTest                 // ON, hold 3s, OFF  (default, safe)
//      RofinPilotTest on              // pilot ON and leave on
//      RofinPilotTest off             // pilot OFF
//      RofinPilotTest --host 192.168.0.200 blink 5
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RofinPilotTest
{
    // ----- configuration (defaults taken from Connections.xml + captures) ---
    static class Config
    {
        public static string Host          = "192.168.0.200";
        public static int    NamingPort    = 10050;          // _port0 in Connections.xml
        public static string NamingKey     = "NameService";  // corbaloc well-known key
        public static string ControllerName = "Controller";  // resolved name

        // Credentials replayed from the capture. The hash was stable across
        // logins in the capture (looks like a static MD5, not a per-session
        // challenge), so replaying it authenticates us as "operator".
        public static string LoginUser     = "operator";
        public static string LoginHash     = "4b583376b2767b923c3e1da60d10de59";
        public static bool   LoginFlag     = false;          // observed false (and true); meaning unclear
        public static bool   DoLogin       = true;           // pilot worked w/o it, but we stay faithful

        // The capture used GIOP 1.0 for the very first "NameService" contact,
        // then 1.2 for everything else. TAO normally accepts resolve() at 1.2
        // on the "NameService" key too, so we default to 1.2. If resolve() fails
        // on real hardware, set this to 0 to replay the exact captured 1.0 path.
        public static int    NamingGiopMinor = 2;            // 2 or 0
    }

    // =======================================================================
    //  CDR writer - Common Data Representation, big-endian, message-absolute
    //  alignment. We pre-write 12 header bytes so Count == message offset and
    //  all alignment "just works".
    // =======================================================================
    sealed class Cdr
    {
        readonly List<byte> _b = new List<byte>(256);
        public bool BigEndian = true;       // we always send BE

        public int Count => _b.Count;
        public byte[] ToArray() => _b.ToArray();

        public void Align(int n)
        {
            while (_b.Count % n != 0) _b.Add(0);
        }
        public void Octet(byte v) => _b.Add(v);
        public void Raw(byte[] v) => _b.AddRange(v);
        public void Bool(bool v) => _b.Add((byte)(v ? 1 : 0));

        public void UShort(ushort v)
        {
            Align(2);
            if (BigEndian) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
            else           { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
        }
        public void ULong(uint v)
        {
            Align(4);
            if (BigEndian)
            { _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
            else
            { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24)); }
        }

        // CORBA string: ulong length (INCLUDING the NUL) + bytes + NUL
        public void Str(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            ULong((uint)(bytes.Length + 1));
            _b.AddRange(bytes);
            _b.Add(0);
        }
        // sequence<octet> (object keys): ulong length + raw bytes, NO NUL
        public void OctetSeq(byte[] v)
        {
            ULong((uint)v.Length);
            _b.AddRange(v);
        }
    }

    // =======================================================================
    //  CDR reader - honours the byte order it is constructed with, supports
    //  message-absolute alignment (msgBase) and spawning encapsulation readers.
    // =======================================================================
    sealed class CdrR
    {
        readonly byte[] _b;
        int _p;
        readonly bool _be;

        public CdrR(byte[] buf, int start, bool bigEndian) { _b = buf; _p = start; _be = bigEndian; }
        public int Pos => _p;

        public void Align(int n) { while (_p % n != 0) _p++; }
        public byte Octet() => _b[_p++];
        public bool Bool() => _b[_p++] != 0;
        public byte[] Bytes(int n) { var r = new byte[n]; Array.Copy(_b, _p, r, 0, n); _p += n; return r; }

        public ushort UShort()
        {
            Align(2);
            ushort v = _be ? (ushort)((_b[_p] << 8) | _b[_p + 1])
                           : (ushort)((_b[_p + 1] << 8) | _b[_p]);
            _p += 2; return v;
        }
        public uint ULong()
        {
            Align(4);
            uint v = _be ? ((uint)_b[_p] << 24 | (uint)_b[_p + 1] << 16 | (uint)_b[_p + 2] << 8 | _b[_p + 3])
                         : ((uint)_b[_p + 3] << 24 | (uint)_b[_p + 2] << 16 | (uint)_b[_p + 1] << 8 | _b[_p]);
            _p += 4; return v;
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
    }

    // ----- a CORBA object reference we care about: where + which key --------
    sealed class ObjRef
    {
        public string TypeId;
        public string Host;
        public int    Port;
        public byte[] Key;
        public override string ToString() =>
            $"{TypeId.Split('/').Last()} @ {Host}:{Port} key={BitConverter.ToString(Key)}";
    }

    // =======================================================================
    //  GIOP connection over one TCP socket.
    // =======================================================================
    sealed class GiopConn : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        uint _reqId = 1;

        public GiopConn(string host, int port)
        {
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.Connect(host, port);
            _s = _tcp.GetStream();
        }

        // Build a GIOP Request (minor 2 = GIOP 1.2, minor 0 = GIOP 1.0).
        // We pre-write 12 placeholder header bytes so positions are
        // message-absolute and alignment "just works".
        byte[] BuildRequest(byte[] objKey, string op, Action<Cdr> writeArgs, int minor)
        {
            var c = new Cdr();
            for (int i = 0; i < 12; i++) c.Octet(0);   // header placeholder

            if (minor == 2)
            {
                // ---- GIOP 1.2 request header ----
                c.ULong(_reqId);                     // request_id
                c.Octet(0x03);                       // response_flags = SYNC_WITH_TARGET
                c.Octet(0); c.Octet(0); c.Octet(0);  // reserved[3]
                c.UShort(0);                         // TargetAddress discriminator 0 = KeyAddr
                c.OctetSeq(objKey);                  // object key
                c.Str(op);                           // operation
                c.ULong(0);                          // service_context = empty
                c.Align(8);                          // 1.2 body 8-aligned to msg start
                writeArgs(c);
            }
            else
            {
                // ---- GIOP 1.0 request header (used for the naming bootstrap) ----
                c.ULong(0);                          // service_context = empty
                c.ULong(_reqId);                     // request_id
                c.Octet(1);                          // response_expected = true
                c.OctetSeq(objKey);                  // object key (sequence<octet>)
                c.Str(op);                           // operation
                c.ULong(0);                          // requesting_principal = empty
                writeArgs(c);                        // body follows immediately (no realign)
            }

            var msg = c.ToArray();
            msg[0] = (byte)'G'; msg[1] = (byte)'I'; msg[2] = (byte)'O'; msg[3] = (byte)'P';
            msg[4] = 1; msg[5] = (byte)minor;        // GIOP 1.<minor>
            msg[6] = 0x00;                           // flags: big-endian, not fragmented
            msg[7] = 0;                              // msg type 0 = Request
            uint bodyLen = (uint)(msg.Length - 12);
            msg[8]  = (byte)(bodyLen >> 24); msg[9]  = (byte)(bodyLen >> 16);
            msg[10] = (byte)(bodyLen >> 8);  msg[11] = (byte)bodyLen;
            return msg;
        }

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

        // Send request, read reply. Reply layout depends on the reply's own
        // GIOP minor version (1.0 and 1.2 order their fields differently).
        (uint status, CdrR body) Invoke(byte[] objKey, string op, Action<Cdr> writeArgs, int minor)
        {
            var msg = BuildRequest(objKey, op, writeArgs, minor);
            _s.Write(msg, 0, msg.Length);
            _reqId++;

            // ---- read reply ----
            var hdr = new byte[12];
            ReadFully(hdr, 0, 12);
            if (hdr[0] != 'G' || hdr[1] != 'I' || hdr[2] != 'O' || hdr[3] != 'P')
                throw new IOException("bad GIOP magic in reply");
            int replyMinor = hdr[5];
            bool be = (hdr[6] & 0x01) == 0;
            byte mtype = hdr[7];
            uint size = be ? ((uint)hdr[8] << 24 | (uint)hdr[9] << 16 | (uint)hdr[10] << 8 | hdr[11])
                           : ((uint)hdr[11] << 24 | (uint)hdr[10] << 16 | (uint)hdr[9] << 8 | hdr[8]);

            var full = new byte[12 + size];
            Array.Copy(hdr, full, 12);
            ReadFully(full, 12, (int)size);

            if (mtype != 1)   // not a Reply (CloseConnection=5, MessageError=6, ...)
                throw new IOException($"unexpected GIOP message type {mtype} in reply");

            var r = new CdrR(full, 12, be);
            uint status;
            if (replyMinor == 2)
            {
                r.ULong();                       // request_id
                status = r.ULong();              // reply_status
                uint nsc = r.ULong();            // service_context list
                for (uint i = 0; i < nsc; i++) { r.ULong(); r.OctetSeq(); }
                r.Align(8);                      // 1.2 body 8-aligned to msg start
            }
            else
            {
                uint nsc = r.ULong();            // service_context list (1.0: first)
                for (uint i = 0; i < nsc; i++) { r.ULong(); r.OctetSeq(); }
                r.ULong();                       // request_id
                status = r.ULong();              // reply_status
                                                 // 1.0 body follows immediately (no realign)
            }
            return (status, r);
        }

        // Invoke and throw on any non-zero reply status.
        CdrR InvokeOk(byte[] objKey, string op, Action<Cdr> writeArgs, int minor = 2)
        {
            var (st, body) = Invoke(objKey, op, writeArgs, minor);
            if (st == 2)  // SYSTEM_EXCEPTION
            {
                // Read the exception: repository_id (string) + minor_code (ulong) + completion_status (ulong)
                try
                {
                    string exId = body.Str();
                    uint minorCode = body.ULong();
                    uint completionStatus = body.ULong();
                    throw new ApplicationException($"{op} raised SYSTEM_EXCEPTION: {exId}, minor={minorCode}, completion={completionStatus}");
                }
                catch (Exception ex) when (!(ex is ApplicationException))
                {
                    throw new ApplicationException($"{op} returned SYSTEM_EXCEPTION but couldn't parse it: {ex.Message}");
                }
            }
            if (st != 0)
                throw new ApplicationException($"{op} returned reply_status {st} (0=NO_EXCEPTION, 1=USER_EXCEPTION, 2=SYSTEM_EXCEPTION, 3=LOCATION_FORWARD)");
            return body;
        }

        // ---- typed operations -------------------------------------------------

        // CosNaming resolve(["Controller"]) -> ObjRef
        // Handles LOCATION_FORWARD by retrying with the forwarded object key
        public ObjRef ResolveName(byte[] namingKey, string name)
        {
            var (st, body) = Invoke(namingKey, "resolve", c =>
            {
                c.ULong(1);          // Name = sequence<NameComponent> length 1
                c.Str(name);         // NameComponent.id
                c.Str("");           // NameComponent.kind = ""
            }, Config.NamingGiopMinor);

            // Handle LOCATION_FORWARD by retrying with the forwarded key
            if (st == 3)  // LOCATION_FORWARD
            {
                var forwarded = ReadIor(body);
                Console.WriteLine($"    -> LOCATION_FORWARD, retrying with new key...");
                body = InvokeOk(forwarded.Key, "resolve", c =>
                {
                    c.ULong(1);
                    c.Str(name);
                    c.Str("");
                }, Config.NamingGiopMinor);
            }
            else if (st != 0)
            {
                throw new ApplicationException($"resolve returned reply_status {st}");
            }

            return ReadIor(body);
        }

        // Controller.Login(user, hash, flag) -> SystemControl ref
        public ObjRef Login(byte[] ctrlKey, string user, string hash, bool flag)
        {
            var body = InvokeOk(ctrlKey, "Login", c =>
            {
                c.Str(user);
                c.Str(hash);
                c.Bool(flag);
            });
            // Login returns a SystemControl IOR - read and return it
            return ReadIor(body);
        }

        // SystemControl.GetMachineControl() -> ObjRef
        public ObjRef GetMachineControl(byte[] sysCtrlKey)
        {
            var body = InvokeOk(sysCtrlKey, "GetMachineControl", c => { /* void */ });
            return ReadIor(body);
        }

        // MachineControl.GetLaser() -> ObjRef
        public ObjRef GetLaser(byte[] machineCtrlKey)
        {
            var body = InvokeOk(machineCtrlKey, "GetLaser", c => { /* void */ });
            return ReadIor(body);
        }

        // Laser.SetAttribute("pilotOn", any{boolean})
        public void SetPilot(byte[] laserKey, bool on)
        {
            InvokeOk(laserKey, "SetAttribute", c =>
            {
                c.Str("pilotOn");
                // CORBA Any with simple TypeCode: just the kind + value, no encapsulation
                c.Raw(new byte[] { 0x00, 0x00, 0x00, 0x08 });  // TypeCode tk_boolean
                c.Raw(new byte[] { (byte)(on ? 1 : 0), 0x00 }); // boolean value + padding
            });
        }

        // ---- IOR parsing ------------------------------------------------------
        // Reply body holds an IOR: string type_id + sequence<TaggedProfile>.
        // We pick the first TAG_INTERNET_IOP profile whose host is NOT loopback
        // (every Rofin IOR carries both 192.168.0.200 and 127.0.0.1 profiles).
        static ObjRef ReadIor(CdrR r)
        {
            string typeId = r.Str();
            uint nprof = r.ULong();
            ObjRef chosen = null, loopback = null;
            for (uint i = 0; i < nprof; i++)
            {
                uint tag = r.ULong();
                byte[] prof = r.OctetSeq();
                if (tag != 0) continue;                       // 0 = TAG_INTERNET_IOP
                var pc = new CdrR(prof, 0, prof[0] == 0);     // encaps byte-order byte
                pc.Octet();                                    // byte order
                pc.Octet(); pc.Octet();                        // IIOP version major/minor
                string host = pc.Str();
                ushort port = pc.UShort();
                byte[] key = pc.OctetSeq();
                var oref = new ObjRef { TypeId = typeId, Host = host, Port = port, Key = key };
                if (host == "127.0.0.1" || host == "localhost") { loopback = loopback ?? oref; }
                else { chosen = chosen ?? oref; }
            }
            var result = chosen ?? loopback;
            if (result == null) throw new ApplicationException($"IOR for {typeId} had no IIOP profile");
            return result;
        }

        public void Dispose() { try { _s?.Dispose(); } catch { } _tcp?.Close(); }
    }

    // =======================================================================
    static class Program
    {
        static int Main(string[] args)
        {
            string mode = "blink";   // default: ON, hold, OFF
            int blinkSeconds = 3;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "on":    mode = "on";  break;
                    case "off":   mode = "off"; break;
                    case "blink": mode = "blink";
                                  if (i + 1 < args.Length && int.TryParse(args[i + 1], out int s)) { blinkSeconds = s; i++; }
                                  break;
                    case "--host": if (i + 1 < args.Length) { Config.Host = args[++i]; } break;
                    case "--no-login": Config.DoLogin = false; break;
                    case "--giop10-naming": Config.NamingGiopMinor = 0; break;
                    default: Console.Error.WriteLine($"(ignoring unknown arg '{args[i]}')"); break;
                }
            }

            Console.WriteLine($"Rofin pilot test  ->  controller {Config.Host}");
            try
            {
                // 1) resolve Controller via the naming service on :10050
                ObjRef controller;
                using (var naming = new GiopConn(Config.Host, Config.NamingPort))
                {
                    Console.WriteLine($"[1] naming :{Config.NamingPort}  resolve(\"{Config.ControllerName}\")");
                    controller = naming.ResolveName(
                        Encoding.ASCII.GetBytes(Config.NamingKey), Config.ControllerName);
                    Console.WriteLine($"    -> {controller}");
                }

                // 2) talk to the controller on its own endpoint (usually :49160)
                using (var conn = new GiopConn(controller.Host, controller.Port))
                {
                    ObjRef sysControl = null;
                    if (Config.DoLogin)
                    {
                        Console.WriteLine($"[2] Login(\"{Config.LoginUser}\", <hash>, {Config.LoginFlag})");
                        sysControl = conn.Login(controller.Key, Config.LoginUser, Config.LoginHash, Config.LoginFlag);
                        Console.WriteLine($"    -> {sysControl}");
                    }

                    Console.WriteLine("[3] GetMachineControl()");
                    var machineControl = conn.GetMachineControl(sysControl.Key);
                    Console.WriteLine($"    -> {machineControl}");

                    Console.WriteLine("[4] GetLaser()");
                    var laser = conn.GetLaser(machineControl.Key);
                    Console.WriteLine($"    -> {laser}");

                    switch (mode)
                    {
                        case "on":
                            Console.WriteLine("[5] pilotOn = TRUE");
                            conn.SetPilot(laser.Key, true);
                            Console.WriteLine("    pilot is ON (left on).");
                            break;
                        case "off":
                            Console.WriteLine("[5] pilotOn = FALSE");
                            conn.SetPilot(laser.Key, false);
                            Console.WriteLine("    pilot is OFF.");
                            break;
                        default: // blink
                            Console.WriteLine($"[5] pilotOn = TRUE  (holding {blinkSeconds}s)");
                            conn.SetPilot(laser.Key, true);
                            Thread.Sleep(blinkSeconds * 1000);
                            Console.WriteLine("    pilotOn = FALSE");
                            conn.SetPilot(laser.Key, false);
                            Console.WriteLine("    done - if you saw the red pointer appear and vanish, the channel is proven.");
                            break;
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("FAILED: " + ex.Message);
                Console.Error.WriteLine("Tips: confirm the controller IP/ports (Connections.xml), that the");
                Console.Error.WriteLine("controller is powered and reachable, and whether LaserConsole must be");
                Console.Error.WriteLine("closed (it may hold the connection). If resolve() fails, the naming");
                Console.Error.WriteLine("service may want GIOP 1.0 - see notes.");
                return 1;
            }
        }
    }
}
