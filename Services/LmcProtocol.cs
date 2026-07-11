using System;
using System.Collections.Generic;

namespace LaserConsole.Services;

// LMC/BJJCZ galvo-board command protocol — what LightBurn speaks to the fake
// board. See docs/lightburn-rofin-bridge.md §3-4. This is the "spec" side
// (galvoplotter/balor is the reference implementation LightBurn uses).
public static class LmcOpcodes
{
    public const ushort DisableLaser       = 0x0002;
    public const ushort EnableLaser        = 0x0004;
    public const ushort ExecuteList        = 0x0005;
    public const ushort SetPwmPulseWidth   = 0x0006;
    public const ushort GetVersion         = 0x0007;
    public const ushort GetSerialNo        = 0x0009;
    public const ushort GetListStatus      = 0x000A;
    public const ushort LaserSignalOff     = 0x000E;
    public const ushort LaserSignalOn      = 0x000F;
    public const ushort GetPositionXY      = 0x000C;
    public const ushort GotoXY             = 0x000D;
    public const ushort WriteCorLine       = 0x0010;   // no reply — see NoReply
    public const ushort ResetList          = 0x0012;
    public const ushort WriteCorTable      = 0x0015;
    public const ushort SetControlMode     = 0x0016;
    public const ushort SetDelayMode       = 0x0017;
    public const ushort SetFirstPulseKiller = 0x001A;
    public const ushort SetLaserMode       = 0x001B;
    public const ushort SetTiming          = 0x001C;
    public const ushort SetStandby         = 0x001D;
    public const ushort SetPwmHalfPeriod   = 0x001E;
    // Sent by LightBurn in a tight repeating poll right after ExecuteList —
    // galvoplotter itself only fires this once per list flush, so LightBurn's
    // usage as a "is the list done" wait loop appears to be its own thing.
    public const ushort SetEndOfList       = 0x0019;
    public const ushort StopExecute        = 0x001F;
    public const ushort StopList           = 0x0020;
    public const ushort WritePort          = 0x0021;
    public const ushort WriteAnalogPort1   = 0x0022;
    public const ushort WriteAnalogPort2   = 0x0023;
    public const ushort WriteAnalogPortX   = 0x0024;
    public const ushort ReadPort           = 0x0025;
    public const ushort SetAxisMotionParam = 0x0026;
    public const ushort SetAxisOriginParam = 0x0027;
    public const ushort AxisGoOrigin       = 0x0028;
    public const ushort MoveAxisTo         = 0x0029;
    public const ushort GetAxisPos         = 0x002A;
    public const ushort GetFlyWaitCount    = 0x002B;
    public const ushort GetMarkCount       = 0x002D;
    public const ushort SetFpkParam2       = 0x002E;
    public const ushort FiberPulseWidth    = 0x002F;
    public const ushort FiberGetConfigExtend = 0x0030;
    public const ushort InputPort          = 0x0031;
    public const ushort SetFlyRes          = 0x0032;
    public const ushort Fiber_SetMo        = 0x0033;
    public const ushort Fiber_GetStMO_AP   = 0x0034;
    public const ushort GetUserData        = 0x0036;
    public const ushort GetFlySpeed        = 0x0038;
    public const ushort DisableZ           = 0x0039;
    public const ushort EnableZ            = 0x003A;
    public const ushort SetZData           = 0x003B;
    public const ushort SetSPISimmerCurrent = 0x003C;
    public const ushort Reset              = 0x0040;
    public const ushort GetMarkTime        = 0x0041;
    public const ushort SetFpkParam        = 0x0062;

    // List (geometry) opcodes — high bit set. Sent inside 0xC00-byte list
    // packets, never answered per-entry.
    public const ushort ListJumpTo         = 0x8001;
    public const ushort ListEndOfList      = 0x8002;
    public const ushort ListLaserOnPoint   = 0x8003;
    public const ushort ListDelayTime      = 0x8004;
    public const ushort ListMarkTo         = 0x8005;
    public const ushort ListJumpSpeed      = 0x8006;
    public const ushort ListLaserOnDelay   = 0x8007;
    public const ushort ListLaserOffDelay  = 0x8008;
    public const ushort ListMarkFreq       = 0x800A;
    public const ushort ListMarkPowerRatio = 0x800B;
    public const ushort ListMarkSpeed      = 0x800C;
    public const ushort ListJumpDelay      = 0x800D;
    public const ushort ListPolygonDelay   = 0x800F;
    public const ushort ListWritePort      = 0x8011;
    public const ushort ListMarkCurrent    = 0x8012;
    public const ushort ListMarkFreq2      = 0x8013;
    public const ushort ListFlyEnable      = 0x801A;
    public const ushort ListQSwitchPeriod  = 0x801B;
    public const ushort ListDirectLaserSwitch = 0x801C;
    public const ushort ListFlyDelay       = 0x801D;
    public const ushort ListSetCo2FPK      = 0x801E;
    public const ushort ListFlyWaitInput   = 0x801F;
    public const ushort ListFiberOpenMO    = 0x8021;
    public const ushort ListWaitForInput   = 0x8022;
    public const ushort ListChangeMarkCount = 0x8023;
    public const ushort ListSetWeldPowerWave = 0x8024;
    public const ushort ListEnableWeldPowerWave = 0x8025;
    public const ushort ListFiberYLPMPulseWidth = 0x8026;
    public const ushort ListFlyEncoderCount = 0x8028;
    public const ushort ListSetDaZWord     = 0x8029;
    public const ushort ListJptSetParam    = 0x8050;
    public const ushort ListReadyMark      = 0x8051;

    public const ushort ListOpcodeMask = 0x8000;

    public static bool IsListOpcode(ushort opcode) => (opcode & ListOpcodeMask) != 0;

    // Commands sent with no read on the wire — queuing a reply desyncs every
    // subsequent exchange (see docs §4 "DESYNC RULE").
    public static readonly HashSet<ushort> NoReply = new() { WriteCorLine };

    public static string Name(ushort opcode) => opcode switch
    {
        DisableLaser => "DisableLaser",
        EnableLaser => "EnableLaser",
        ExecuteList => "ExecuteList",
        SetPwmPulseWidth => "SetPwmPulseWidth",
        GetVersion => "GetVersion",
        GetSerialNo => "GetSerialNo",
        GetListStatus => "GetListStatus",
        LaserSignalOff => "LaserSignalOff",
        LaserSignalOn => "LaserSignalOn",
        GetPositionXY => "GetPositionXY",
        GotoXY => "GotoXY",
        WriteCorLine => "WriteCorLine",
        ResetList => "ResetList",
        WriteCorTable => "WriteCorTable",
        SetControlMode => "SetControlMode",
        SetDelayMode => "SetDelayMode",
        SetFirstPulseKiller => "SetFirstPulseKiller",
        SetLaserMode => "SetLaserMode",
        SetTiming => "SetTiming",
        SetStandby => "SetStandby",
        SetPwmHalfPeriod => "SetPwmHalfPeriod",
        SetEndOfList => "SetEndOfList",
        StopExecute => "StopExecute",
        StopList => "StopList",
        WritePort => "WritePort",
        WriteAnalogPort1 => "WriteAnalogPort1",
        WriteAnalogPort2 => "WriteAnalogPort2",
        WriteAnalogPortX => "WriteAnalogPortX",
        ReadPort => "ReadPort",
        SetAxisMotionParam => "SetAxisMotionParam",
        SetAxisOriginParam => "SetAxisOriginParam",
        AxisGoOrigin => "AxisGoOrigin",
        MoveAxisTo => "MoveAxisTo",
        GetAxisPos => "GetAxisPos",
        GetFlyWaitCount => "GetFlyWaitCount",
        GetMarkCount => "GetMarkCount",
        SetFpkParam2 => "SetFpkParam2",
        FiberPulseWidth => "FiberPulseWidth",
        FiberGetConfigExtend => "FiberGetConfigExtend",
        InputPort => "InputPort",
        SetFlyRes => "SetFlyRes",
        Fiber_SetMo => "Fiber_SetMo",
        Fiber_GetStMO_AP => "Fiber_GetStMO_AP",
        GetUserData => "GetUserData",
        GetFlySpeed => "GetFlySpeed",
        DisableZ => "DisableZ",
        EnableZ => "EnableZ",
        SetZData => "SetZData",
        SetSPISimmerCurrent => "SetSPISimmerCurrent",
        Reset => "Reset",
        GetMarkTime => "GetMarkTime",
        SetFpkParam => "SetFpkParam",
        ListJumpTo => "listJumpTo",
        ListEndOfList => "listEndOfList",
        ListLaserOnPoint => "listLaserOnPoint",
        ListMarkTo => "listMarkTo",
        ListDelayTime => "listDelayTime",
        ListJumpSpeed => "listJumpSpeed",
        ListLaserOnDelay => "listLaserOnDelay",
        ListLaserOffDelay => "listLaserOffDelay",
        ListMarkFreq => "listMarkFreq",
        ListMarkPowerRatio => "listMarkPowerRatio",
        ListMarkSpeed => "listMarkSpeed",
        ListJumpDelay => "listJumpDelay",
        ListPolygonDelay => "listPolygonDelay",
        ListWritePort => "listWritePort",
        ListMarkCurrent => "listMarkCurrent",
        ListMarkFreq2 => "listMarkFreq2",
        ListFlyEnable => "listFlyEnable",
        ListQSwitchPeriod => "listQSwitchPeriod",
        ListDirectLaserSwitch => "listDirectLaserSwitch",
        ListFlyDelay => "listFlyDelay",
        ListSetCo2FPK => "listSetCo2FPK",
        ListFlyWaitInput => "listFlyWaitInput",
        ListFiberOpenMO => "listFiberOpenMO",
        ListWaitForInput => "listWaitForInput",
        ListChangeMarkCount => "listChangeMarkCount",
        ListSetWeldPowerWave => "listSetWeldPowerWave",
        ListEnableWeldPowerWave => "listEnableWeldPowerWave",
        ListFiberYLPMPulseWidth => "listFiberYLPMPulseWidth",
        ListFlyEncoderCount => "listFlyEncoderCount",
        ListSetDaZWord => "listSetDaZWord",
        ListJptSetParam => "listJptSetParam",
        ListReadyMark => "listReadyMark",
        _ => $"0x{opcode:X4}",
    };
}

// A single 12-byte host->board command: opcode + 5 u16 params.
public readonly struct LmcCommand
{
    public ushort Opcode { get; }
    public ushort P1 { get; }
    public ushort P2 { get; }
    public ushort P3 { get; }
    public ushort P4 { get; }
    public ushort P5 { get; }

    public LmcCommand(ushort opcode, ushort p1, ushort p2, ushort p3, ushort p4, ushort p5)
    {
        Opcode = opcode; P1 = p1; P2 = p2; P3 = p3; P4 = p4; P5 = p5;
    }

    public const int Size = 12;

    public static LmcCommand Parse(ReadOnlySpan<byte> bytes12) => new(
        (ushort)(bytes12[0] | (bytes12[1] << 8)),
        (ushort)(bytes12[2] | (bytes12[3] << 8)),
        (ushort)(bytes12[4] | (bytes12[5] << 8)),
        (ushort)(bytes12[6] | (bytes12[7] << 8)),
        (ushort)(bytes12[8] | (bytes12[9] << 8)),
        (ushort)(bytes12[10] | (bytes12[11] << 8)));
}

// Builds the connect-handshake responses that make LightBurn accept the
// board (docs §4). Marking/list-status logic is a stub for now — the list
// decoder and CORBA translation (docs §5-6) are a separate follow-up.
public static class LmcResponder
{
    public const int ReplySize = 8;

    static byte[] Pack4H(ushort w0, ushort w1, ushort w2, ushort w3)
    {
        var b = new byte[8];
        b[0] = (byte)w0; b[1] = (byte)(w0 >> 8);
        b[2] = (byte)w1; b[3] = (byte)(w1 >> 8);
        b[4] = (byte)w2; b[5] = (byte)(w2 >> 8);
        b[6] = (byte)w3; b[7] = (byte)(w3 >> 8);
        return b;
    }

    static readonly byte[] GenericAck = Pack4H(0, 0, 0, 0);

    // GetVersion's word3 is not a version echo — it's the live status bitmask
    // the host polls continuously (confirmed against galvoplotter's
    // controller.py: status() reads word3 of get_version() and tests these
    // bits). Word0 carries the actual version marker LightBurn inspects at
    // connect time. Getting word3 wrong here is what causes LightBurn to
    // read the board as permanently busy/not-ready and flap its connection
    // state even though the handshake itself succeeded.
    public const ushort StatusBusy  = 0x04;
    public const ushort StatusReady = 0x20;
    public const ushort StatusAxis  = 0x40;

    // Whether the fake board should currently report itself ready-and-idle.
    // Flip this (and set StatusBusy) once ExecuteList/GetListStatus actually
    // drive a real marking job — see docs §5 "status feedback".
    public static bool IsReady { get; set; } = true;

    // Returns the 8-byte reply to queue, or null if this command must not be
    // answered (WriteCorLine, or any list opcode — never answered per-entry).
    public static byte[]? BuildReply(in LmcCommand cmd)
    {
        if (LmcOpcodes.IsListOpcode(cmd.Opcode)) return null;
        if (LmcOpcodes.NoReply.Contains(cmd.Opcode)) return null;

        return cmd.Opcode switch
        {
            LmcOpcodes.GetVersion  => Pack4H(0x0454, 0, 0, (ushort)(IsReady ? StatusReady : StatusBusy)),
            LmcOpcodes.GetSerialNo => Pack4H(1, 0, 0, 0),
            // No fault/door bits set — machine reads as ready.
            LmcOpcodes.ReadPort    => Pack4H(0, 0, 0, 0),
            // EXPERIMENT: LightBurn polls SetEndOfList in a tight loop right
            // after ExecuteList (galvoplotter itself only sends it once per
            // list flush, so this looks like LightBurn's own "is the list
            // done" wait). GetListStatus is the documented opcode for that,
            // but we've never actually seen LightBurn call it — so try the
            // same READY/BUSY bits here too, on the theory LightBurn checks
            // status the same way regardless of which opcode it asked via.
            // Needs a live capture to confirm/deny.
            LmcOpcodes.SetEndOfList   => Pack4H(0, 0, 0, (ushort)(IsReady ? StatusReady : StatusBusy)),
            LmcOpcodes.GetListStatus  => Pack4H(0, 0, 0, (ushort)(IsReady ? StatusReady : StatusBusy)),
            _                      => GenericAck,
        };
    }
}
