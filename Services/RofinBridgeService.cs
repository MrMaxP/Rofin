using System;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LaserConsole.Services;

// What the fake board is currently doing, derived from the LMC traffic —
// surfaced for the mark-preview UI. Idle: no list running. Tracing: an
// ExecuteList is running a jump-only list (LightBurn's outline/frame
// preview — beam nominally off). Marking: Fiber_SetMo(1) has fired and
// hasn't been followed by Fiber_SetMo(0) yet (the laser is actually firing).
public enum MarkState { Idle, Tracing, Marking }

// What a drawn segment represents. Jump/Marking are unambiguous from the
// opcode itself (listJumpTo vs listMarkTo). RedDot is inferred, not
// confirmed: WritePort's p1 carries bit 0x100 in every capture right where
// the pilot/guide beam would plausibly be lit (start of a list-download
// cycle) and clear where it plausibly wouldn't — but WritePort is a general
// I/O port write with no documented per-bit meaning, so treat this as a
// best-effort guess to verify against the real red dot, not a known fact.
public enum MoveKind { Jump, RedDot, Marking }

// One rendered move, in mm relative to field centre. Consumed by the UI's
// fading preview; CreatedAt drives the fade-out.
public readonly struct PreviewSegment
{
    public double X1Mm { get; init; }
    public double Y1Mm { get; init; }
    public double X2Mm { get; init; }
    public double Y2Mm { get; init; }
    public MoveKind Kind { get; init; }
    public DateTime CreatedAt { get; init; }
}

// Owns the CDC serial link to the Pico bridge board and answers LightBurn's
// LMC/BJJCZ connect handshake so it detects a fake galvo laser. Starts at
// app boot and keeps retrying — it must be draining the port before
// LightBurn connects, and it must survive the Pico not being plugged in yet.
// See docs/lightburn-rofin-bridge.md.
//
// List decoding (the actual mark geometry) and CORBA translation to the
// Rofin are not implemented yet (docs §5-6) — list entries are currently
// counted and logged, not acted on.
public sealed class RofinBridgeService : IDisposable
{
    public const int Vid = 0x9588;
    public const int Pid = 0x9899;

    // Fired on a background thread — handlers must marshal to the UI thread.
    public event Action<string>? LogMessage;
    public event Action?         StateChanged;

    public string? ManualPortName { get; set; }

    public bool   IsRunning       { get; private set; }   // Start()/Stop() lifecycle
    public bool   IsPortOpen      { get; private set; }   // serial port currently open
    public string? PortName       { get; private set; }
    public bool?  HostMounted     { get; private set; }   // from STATUS frames

    CancellationTokenSource? _cts;
    Task?                    _runTask;
    SerialPort?              _port;

    long _commandCount;
    long _listEntryCount;
    long _getVersionCount;
    long _readPortCount;
    public long CommandCount    => _commandCount;
    public long ListEntryCount  => _listEntryCount;
    public long GetVersionCount => _getVersionCount;
    public long ReadPortCount   => _readPortCount;

    // Last-seen timestamps for the high-frequency idle-poll commands, so the
    // UI can show them as an activity indicator instead of drowning the log.
    public DateTime? LastGetVersionAt { get; private set; }
    public DateTime? LastReadPortAt   { get; private set; }

    // ── Mark preview ──────────────────────────────────────────────────────
    // LightBurn never sends a bed/field size over the wire (docs §5 — it's
    // established once, client-side, not queried) so this is user-set; 100mm
    // is a plausible default for an entry fiber galvo field.
    public double FieldWidthMm  { get; set; } = 100;
    public double FieldHeightMm { get; set; } = 100;

    // Real boards download a whole list fast, then execute it over real
    // time at the commanded speed while reporting busy; LightBurn waits on
    // that before sending the next job. We normally ack everything
    // instantly, which is why the preview outruns LightBurn's own idea of
    // how fast the job should run. When this is on, both the preview and
    // our busy/ready status are paced from the same distance/speed estimate
    // instead. LightBurn's own listMarkSpeed/listJumpSpeed wire values are
    // undocumented (no confirmed units), so this deliberately uses your own
    // mm/s settings instead of guessing a decode for those.
    public bool   RealtimePlayback   { get; set; }
    public double MarkSpeedMmPerSec  { get; set; } = 20;
    public double JumpSpeedMmPerSec  { get; set; } = 300;

    // True while we're artificially holding LightBurn off via the busy
    // status bits (RealtimePlayback only) — surfaced so it's visible that
    // something is actually being held back, not just silently timed.
    public bool SimulatedBusy => !LmcResponder.IsReady;

    public MarkState MarkState { get; private set; } = MarkState.Idle;

    void SetMarkState(MarkState state)
    {
        if (MarkState == state) return;
        MarkState = state;
        StateChanged?.Invoke();
    }

    // Galvo coords are absolute (16-bit, 0x8000 = field centre); track the
    // head's last commanded position so each new listJumpTo/listMarkTo/GotoXY
    // can be turned into a drawable segment from "where it was" to "where
    // it's going". Starts centred, matching the connect handshake's GotoXY.
    double _curGalvoX = 32768;
    double _curGalvoY = 32768;
    bool   _hasMarkToSinceReset;

    // Best-effort pilot/red-dot state — see MoveKind.RedDot's doc comment.
    bool _pilotOn;

    // Segments carry their own reveal time (CreatedAt) — "now" for instant
    // (default) playback, or a scheduled future time when RealtimePlayback
    // paces them by distance/speed. A lock-protected list rather than a
    // queue because draining has to filter by "is this one due yet", not
    // just take everything.
    readonly object _previewLock = new();
    readonly System.Collections.Generic.List<PreviewSegment> _pendingPreview = new();

    void EnqueuePreview(PreviewSegment seg)
    {
        lock (_previewLock) _pendingPreview.Add(seg);
    }

    // Drains all segments due (CreatedAt <= now) since the last call — the
    // UI polls this on its own render timer rather than us pushing
    // per-segment events, since LightBurn's frame-trace loop can emit tens
    // of thousands of entries/sec.
    public System.Collections.Generic.List<PreviewSegment> DrainPreviewSegments()
    {
        var now = DateTime.Now;
        var due = new System.Collections.Generic.List<PreviewSegment>();
        lock (_previewLock)
        {
            for (int i = _pendingPreview.Count - 1; i >= 0; i--)
            {
                if (_pendingPreview[i].CreatedAt > now) continue;
                due.Add(_pendingPreview[i]);
                _pendingPreview.RemoveAt(i);
            }
        }
        due.Reverse();   // preserve chronological order (we walked back-to-front)
        return due;
    }

    // Segment-level dedup, separate from the log dedup above: a repeating
    // frame-trace loop retraces the same handful of points as fast as USB
    // allows, so without this we'd enqueue tens of thousands of visually
    // identical segments per second. Re-admit a segment for drawing at most
    // every 100ms so the preview still looks "live" without flooding.
    readonly System.Collections.Generic.Dictionary<(int, int, int, int, MoveKind), DateTime> _segmentLastSeen = new();
    static readonly TimeSpan SegmentReplayInterval = TimeSpan.FromMilliseconds(100);
    DateTime _lastSegmentDedupPrune = DateTime.MinValue;

    void MoveTo(double galvoX, double galvoY, MoveKind kind)
    {
        var key = ((int)_curGalvoX, (int)_curGalvoY, (int)galvoX, (int)galvoY, kind);
        var now = DateTime.Now;

        if (now - _lastSegmentDedupPrune > TimeSpan.FromSeconds(5))
        {
            _lastSegmentDedupPrune = now;
            foreach (var k in _segmentLastSeen
                         .Where(kv => now - kv.Value > TimeSpan.FromSeconds(5))
                         .Select(kv => kv.Key).ToList())
                _segmentLastSeen.Remove(k);
        }

        double x1Mm = ToMm(_curGalvoX, FieldWidthMm), y1Mm = ToMm(_curGalvoY, FieldHeightMm);
        double x2Mm = ToMm(galvoX, FieldWidthMm),     y2Mm = ToMm(galvoY, FieldHeightMm);

        if (!_segmentLastSeen.TryGetValue(key, out var last) || now - last >= SegmentReplayInterval)
        {
            _segmentLastSeen[key] = now;

            var revealAt = now;
            if (RealtimePlayback)
            {
                double distMm = Math.Sqrt(Math.Pow(x2Mm - x1Mm, 2) + Math.Pow(y2Mm - y1Mm, 2));
                double speedMmS = kind == MoveKind.Marking ? MarkSpeedMmPerSec : JumpSpeedMmPerSec;
                double durationSec = speedMmS > 0 ? distMm / speedMmS : 0;

                // Cumulative clock so segments reveal in order, spaced by
                // their real-world duration, instead of all landing "now"
                // the instant the whole list arrives over USB.
                if (_playbackClock < now) _playbackClock = now;
                revealAt = _playbackClock;
                _playbackClock = _playbackClock.AddSeconds(durationSec);
                _pendingJobDurationSec += durationSec;
            }

            EnqueuePreview(new PreviewSegment
            {
                X1Mm = x1Mm, Y1Mm = y1Mm, X2Mm = x2Mm, Y2Mm = y2Mm,
                Kind = kind, CreatedAt = revealAt,
            });
        }
        _curGalvoX = galvoX;
        _curGalvoY = galvoY;
    }

    // Jump-type moves (listJumpTo/GotoXY) are never actually marking — the
    // laser only fires on listMarkTo entries regardless of Fiber_SetMo/MO
    // state — but they may be lit by the pilot/guide beam while positioning.
    MoveKind JumpKind => _pilotOn ? MoveKind.RedDot : MoveKind.Jump;

    // Real-time playback bookkeeping: cumulative reveal clock for pacing
    // segments, and the estimated total duration of the list currently
    // being downloaded — read at ExecuteList to decide how long to hold
    // busy/not-ready, reset whenever a list cycle starts over.
    DateTime _playbackClock;
    double   _pendingJobDurationSec;
    DateTime _busyUntil = DateTime.MinValue;

    static double ToMm(double galvoUnit, double fieldSizeMm) => (galvoUnit - 32768.0) / 65536.0 * fieldSizeMm;

    void Log(string msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [bridge] {msg}");
        LogMessage?.Invoke(msg);
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        ClosePort();
        SetState(open: false, mounted: null, name: null);
    }

    // Retry loop: locate the port, open it, drain it until it fails or the
    // Pico is unplugged, then wait and try again. Runs for the app's lifetime.
    async Task RunLoop(CancellationToken ct)
    {
        Log("bridge starting — waiting for device...");
        while (!ct.IsCancellationRequested)
        {
            string? portName = null;
            try
            {
                portName = SerialPortLocator.Find(Vid, Pid, ManualPortName, Log);
            }
            catch (Exception ex) { Log($"port lookup failed: {ex.Message}"); }

            if (portName is null)
            {
                await DelayIgnoringCancel(2000, ct);
                continue;
            }

            try
            {
                RunPort(portName, ct);
            }
            catch (Exception ex)
            {
                Log($"port '{portName}' error: {ex.Message}");
            }
            finally
            {
                ClosePort();
                SetState(open: false, mounted: null, name: null);
            }

            if (!ct.IsCancellationRequested)
                await DelayIgnoringCancel(1000, ct);
        }
    }

    static async Task DelayIgnoringCancel(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    void RunPort(string portName, CancellationToken ct)
    {
        Log($"opening {portName}...");
        _port = new SerialPort(portName, 115200)
        {
            ReadTimeout  = 200,
            WriteTimeout = 500,
        };
        _port.Open();
        SetState(open: true, mounted: HostMounted, name: portName);
        Log($"{portName} open — draining port.");

        var parser = new CdcFrameParser();
        var buf = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            // Clear a real-time-playback busy period once its estimated
            // duration has elapsed — checked every loop iteration, which in
            // practice means within one of LightBurn's own status polls
            // (every few ms) rather than waiting for a fixed timer.
            if (!LmcResponder.IsReady && DateTime.Now >= _busyUntil)
                LmcResponder.IsReady = true;

            int n;
            try
            {
                n = _port.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                continue;   // no data within ReadTimeout — normal, keep polling
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                Log($"{portName} disconnected: {ex.Message}");
                return;     // device unplugged — RunLoop will retry
            }

            if (n <= 0) continue;

            foreach (var frame in parser.Feed(buf.AsSpan(0, n)))
                HandleFrame(frame);
        }
    }

    void HandleFrame(CdcFrame frame)
    {
        switch (frame.Type)
        {
            case CdcFrameType.Status:
                bool mounted = frame.Payload.Length > 0 && frame.Payload[0] != 0;
                HostMounted = mounted;
                Log(mounted ? "LightBurn host mounted." : "LightBurn host unmounted.");
                StateChanged?.Invoke();
                break;

            case CdcFrameType.VendorOut:
                HandleVendorOut(frame.Payload);
                StateChanged?.Invoke();   // refresh command/list counters
                break;

            default:
                break;
        }
    }

    // VENDOR_OUT bytes are whatever LightBurn wrote to EP 0x02 in one FIFO
    // read — not necessarily aligned to one logical command, so accumulate
    // and consume 12-byte units from a rolling buffer.
    readonly System.Collections.Generic.List<byte> _cmdBuf = new();

    // Dedup for list entries + SetEndOfList: LightBurn's frame-trace preview
    // re-sends the exact same entries in an endless loop while its start
    // dialog waits on the user, which floods the log with identical lines.
    // Suppress an entry once we've already logged its exact bytes recently.
    //
    // This is time-windowed, not count-windowed: an earlier count-based
    // version (remember only the last 16 distinct entries) worked for a
    // 4-point square but broke down completely on anything with more unique
    // points per lap (a circle can easily have 60+) — by the time the loop
    // came back around, the early points had already been evicted, so every
    // single entry looked "new" again and the log (and the UI thread trying
    // to render it, unbounded) locked up. Keying by content+time has no such
    // ceiling — it only cares whether *this exact* entry recurred recently.
    // Keyed by the parsed command fields (a value tuple — no heap allocation
    // per lookup), not a hex string of the raw bytes: with a complex shape
    // this is called for every single incoming command, and allocating a
    // fresh string each time was itself a meaningful chunk of the "falling
    // behind LightBurn" cost on dense geometry.
    readonly System.Collections.Generic.Dictionary<(ushort, ushort, ushort, ushort, ushort, ushort), DateTime> _recentlyLogged = new();
    static readonly TimeSpan LogReplayInterval = TimeSpan.FromMilliseconds(250);
    DateTime _lastDedupPrune = DateTime.MinValue;
    long _suppressedRepeats;
    DateTime _lastSuppressHeartbeat = DateTime.MinValue;

    bool ShouldLogOnce(in LmcCommand cmd)
    {
        var key = (cmd.Opcode, cmd.P1, cmd.P2, cmd.P3, cmd.P4, cmd.P5);
        var now = DateTime.Now;

        if (_recentlyLogged.TryGetValue(key, out var last) && now - last < LogReplayInterval)
        {
            Interlocked.Increment(ref _suppressedRepeats);
            if (now - _lastSuppressHeartbeat > TimeSpan.FromSeconds(2))
            {
                Log($"   ... {_suppressedRepeats} repeated entries suppressed (frame-trace loop?)");
                _suppressedRepeats = 0;
                _lastSuppressHeartbeat = now;
            }
            return false;
        }

        _recentlyLogged[key] = now;

        // Opportunistic prune so a long session with lots of distinct
        // geometry doesn't grow this dictionary forever.
        if (now - _lastDedupPrune > TimeSpan.FromSeconds(5))
        {
            _lastDedupPrune = now;
            foreach (var k in _recentlyLogged
                         .Where(kv => now - kv.Value > TimeSpan.FromSeconds(5))
                         .Select(kv => kv.Key).ToList())
                _recentlyLogged.Remove(k);
        }

        return true;
    }

    void HandleVendorOut(byte[] payload)
    {
        _cmdBuf.AddRange(payload);

        while (_cmdBuf.Count >= LmcCommand.Size)
        {
            var bytes = _cmdBuf.GetRange(0, LmcCommand.Size).ToArray();
            _cmdBuf.RemoveRange(0, LmcCommand.Size);

            var cmd = LmcCommand.Parse(bytes);

            if (LmcOpcodes.IsListOpcode(cmd.Opcode))
            {
                Interlocked.Increment(ref _listEntryCount);
                // Geometry decoding + Rofin translation: not implemented yet —
                // logged (once per distinct entry) so we can see what
                // LightBurn is actually sending.
                if (ShouldLogOnce(cmd))
                    Log($"   {LmcOpcodes.Name(cmd.Opcode)}  p1={cmd.P1} p2={cmd.P2} p3={cmd.P3} p4={cmd.P4} p5={cmd.P5}");

                if (cmd.Opcode == LmcOpcodes.ListJumpTo)
                {
                    MoveTo(cmd.P1, cmd.P2, JumpKind);
                }
                else if (cmd.Opcode == LmcOpcodes.ListMarkTo)
                {
                    _hasMarkToSinceReset = true;
                    MoveTo(cmd.P1, cmd.P2, MoveKind.Marking);
                }
                continue;
            }

            var name = LmcOpcodes.Name(cmd.Opcode);
            var reply = LmcResponder.BuildReply(cmd);

            // GetVersion/ReadPort are LightBurn's idle safety poll — it fires
            // every few ms and would otherwise drown out real commands in the
            // count and the log. Track it as an activity pulse instead.
            bool isPoll = cmd.Opcode == LmcOpcodes.GetVersion || cmd.Opcode == LmcOpcodes.ReadPort;
            if (cmd.Opcode == LmcOpcodes.GetVersion) { Interlocked.Increment(ref _getVersionCount); LastGetVersionAt = DateTime.Now; }
            else if (cmd.Opcode == LmcOpcodes.ReadPort) { Interlocked.Increment(ref _readPortCount); LastReadPortAt = DateTime.Now; }
            else Interlocked.Increment(ref _commandCount);

            // SetEndOfList repeats in lockstep with the frame-trace loop —
            // dedup it the same way so it doesn't flood between entries.
            bool suppressLog = cmd.Opcode == LmcOpcodes.SetEndOfList && !ShouldLogOnce(cmd);

            switch (cmd.Opcode)
            {
                case LmcOpcodes.GotoXY:
                    MoveTo(cmd.P1, cmd.P2, JumpKind);
                    break;

                case LmcOpcodes.Fiber_SetMo:
                    SetMarkState(cmd.P1 != 0 ? MarkState.Marking : MarkState.Idle);
                    break;

                case LmcOpcodes.WritePort:
                    // Best-effort: see MoveKind.RedDot — bit 0x100 correlates
                    // with pilot-beam-plausible moments in every capture so
                    // far, but is not a confirmed port assignment.
                    _pilotOn = (cmd.P1 & 0x100) != 0;
                    break;

                case LmcOpcodes.ExecuteList:
                    // A list with no listMarkTo entries is LightBurn's
                    // outline/frame preview (beam nominally off); one that
                    // does is a real mark, already flagged Marking by the
                    // Fiber_SetMo(1) that preceded it.
                    if (!_hasMarkToSinceReset && MarkState != MarkState.Marking)
                        SetMarkState(MarkState.Tracing);

                    // Hold "busy" for the estimated real-world duration of
                    // what we're about to run, so LightBurn's own
                    // GetVersion/SetEndOfList poll loop actually waits
                    // instead of proceeding the instant we ack — this is
                    // what makes it "pause until we're ready for more data".
                    if (RealtimePlayback && _pendingJobDurationSec > 0)
                    {
                        LmcResponder.IsReady = false;
                        _busyUntil = DateTime.Now.AddSeconds(_pendingJobDurationSec);
                    }
                    _pendingJobDurationSec = 0;
                    break;

                case LmcOpcodes.ResetList:
                case LmcOpcodes.StopExecute:
                case LmcOpcodes.StopList:
                    _hasMarkToSinceReset = false;
                    _segmentLastSeen.Clear();
                    _playbackClock = DateTime.Now;
                    if (MarkState == MarkState.Tracing)
                        SetMarkState(MarkState.Idle);
                    break;
            }

            if (reply is null)
            {
                if (cmd.Opcode != LmcOpcodes.WriteCorLine && !isPoll)
                    Log($"<- {name} (no reply)");
                continue;
            }

            if (!isPoll && !suppressLog)
                Log($"<- {name}  p1={cmd.P1} p2={cmd.P2} p3={cmd.P3} p4={cmd.P4} p5={cmd.P5}");
            SendVendorIn(reply);
        }
    }

    void SendVendorIn(byte[] replyPayload)
    {
        if (_port is not { IsOpen: true }) return;
        var frame = CdcFrameCodec.Encode(CdcFrameType.VendorIn, replyPayload);
        try { _port.Write(frame, 0, frame.Length); }
        catch (Exception ex) { Log($"reply write failed: {ex.Message}"); }
    }

    void ClosePort()
    {
        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;
        _cmdBuf.Clear();
    }

    void SetState(bool open, bool? mounted, string? name)
    {
        IsPortOpen  = open;
        HostMounted = mounted;
        PortName    = name;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
