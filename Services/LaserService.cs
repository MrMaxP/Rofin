using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LaserConsole.Models;

namespace LaserConsole.Services;

public sealed class ConnectionSettings
{
    public string Host            { get; set; } = "192.168.0.200";
    public int    NamingPort      { get; set; } = 10050;
    public string NamingKey       { get; set; } = "NameService";
    public string ControllerName  { get; set; } = "Controller";
    public string LoginUser       { get; set; } = "operator";
    public string LoginHash       { get; set; } = "4b583376b2767b923c3e1da60d10de59";
    public bool   LoginFlag       { get; set; } = false;
    public bool   DoLogin         { get; set; } = true;
    public int    NamingGiopMinor { get; set; } = 2;
}

public sealed class LaserService : IDisposable
{
    // ── Events — fired on thread-pool threads; handlers must marshal to UI ──
    public event Action<string>? LogMessage;
    public event Action?         StateChanged;

    public ConnectionSettings Settings { get; } = new();

    public bool    IsConnected       { get; private set; }
    public bool?   PilotState        { get; private set; }
    public bool?   FocusFinderState  { get; private set; }
    public bool?   ShutterState      { get; private set; }
    public bool?   LampTestState     { get; private set; }
    public double? AxisPosition      { get; private set; }

    GiopConn? _conn;
    ObjRef?   _controller;
    ObjRef?   _sysControl;
    ObjRef?   _machControl;
    ObjRef?   _laser;
    byte[]?   _lifAxisKey;   // first active LIFAxis — used for position polling

    readonly SemaphoreSlim _lock = new(1, 1);

    void Log(string msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        LogMessage?.Invoke(msg);
    }

    // ── Connect ────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (IsConnected) DisconnectLocked();

            var s = Settings;
            Log($"[1] naming :{s.NamingPort}  resolve(\"{s.ControllerName}\")");
            await Task.Run(() =>
            {
                using var naming = new GiopConn(s.Host, s.NamingPort);
                _controller = naming.ResolveName(
                    Encoding.ASCII.GetBytes(s.NamingKey),
                    s.ControllerName,
                    s.NamingGiopMinor);
                Log($"    -> {_controller}");
            }, ct);

            Log($"[2] connecting to controller {_controller!.Host}:{_controller.Port}");
            var ctrl = _controller;
            await Task.Run(() =>
            {
                _conn = new GiopConn(ctrl.Host, ctrl.Port);

                if (s.DoLogin)
                {
                    Log($"[3] Login(\"{s.LoginUser}\", <hash>, {s.LoginFlag})");
                    _sysControl = _conn.Login(ctrl.Key, s.LoginUser, s.LoginHash, s.LoginFlag);
                    Log($"    -> {_sysControl}");
                }

                Log("[4] GetMachineControl()");
                _machControl = _conn.GetMachineControl(_sysControl!.Key);
                Log($"    -> {_machControl}");

                Log("[5] GetLaser()");
                _laser = _conn.GetLaser(_machControl.Key);
                Log($"    -> {_laser}");

                Log("[6] Locating active LIFAxis...");
                _lifAxisKey = null;
                try
                {
                    var components = _conn.GetAllComponents(_sysControl!.Key);
                    foreach (var comp in components)
                    {
                        try
                        {
                            if (_conn.GetComponentClassName(comp.Key) != "LIFAxis") continue;
                            if (_conn.GetAttributeString(comp.Key, "active") == "true")
                            {
                                _lifAxisKey = comp.Key;
                                Log($"    -> LIFAxis found");
                                break;
                            }
                        }
                        catch { }
                    }
                    if (_lifAxisKey is null) Log("    warning: no active LIFAxis found");
                }
                catch (Exception ex) { Log($"    LIFAxis search failed: {ex.Message}"); }
            }, ct);

            IsConnected      = true;
            PilotState       = null;
            FocusFinderState = null;
            Log("Connected.");
            StateChanged?.Invoke();
        }
        catch
        {
            DisconnectLocked();
            throw;
        }
        finally
        {
            _lock.Release();
        }

        // Best-effort initial reads — ignore failures.
        await RefreshPilotStateAsync(ct);
        await RefreshFocusFinderStateAsync(ct);
        await RefreshShutterStateAsync(ct);
        await RefreshLampTestStateAsync(ct);
    }

    public async Task RefreshPilotStateAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _laser is null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsConnected || _conn is null || _laser is null) return;
            var conn  = _conn;
            var laser = _laser;
            var state = await Task.Run(() =>
            {
                try { return conn.GetBoolAttribute(laser.Key, "pilotOn"); }
                catch (Exception ex) { Log($"    pilot state read failed: {ex.Message}"); return (bool?)null; }
            }, ct);
            PilotState = state;
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task RefreshFocusFinderStateAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _laser is null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsConnected || _conn is null || _laser is null) return;
            var conn  = _conn;
            var laser = _laser;
            var state = await Task.Run(() =>
            {
                try { return conn.GetBoolAttribute(laser.Key, "focusFinderOn"); }
                catch (Exception ex) { Log($"    focus finder state read failed: {ex.Message}"); return (bool?)null; }
            }, ct);
            FocusFinderState = state;
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    // ── Disconnect ─────────────────────────────────────────────────────────

    public void Disconnect()
    {
        // Close the socket first so any in-flight ReadFully() throws immediately,
        // releasing the lock. Without this, _lock.Wait() blocks forever.
        _conn?.Dispose();
        _lock.Wait();
        try { DisconnectLocked(); }
        finally { _lock.Release(); }
    }

    void DisconnectLocked()
    {
        _conn?.Dispose();
        _conn        = null;
        _controller  = null;
        _sysControl  = null;
        _machControl = null;
        _laser       = null;
        _lifAxisKey  = null;
        IsConnected      = false;
        PilotState       = null;
        FocusFinderState = null;
        ShutterState     = null;
        LampTestState    = null;
        AxisPosition     = null;
        Log("Disconnected.");
        StateChanged?.Invoke();
    }

    // ── Pilot control ──────────────────────────────────────────────────────

    public async Task SetPilotAsync(bool on, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log($"    pilotOn = {(on ? "TRUE" : "FALSE")}");
            var conn  = _conn!;
            var key   = _laser!.Key;
            await Task.Run(() =>
                conn.SetAttribute(key, "pilotOn",
                    new byte[] { 0x00, 0x00, 0x00, 0x08 },
                    new byte[] { (byte)(on ? 1 : 0), 0x00 }), ct);
            PilotState = on;
            Log($"    pilot is {(on ? "ON" : "OFF")}.");
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task SetFocusFinderAsync(bool on, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log($"    focusFinderOn = {(on ? "TRUE" : "FALSE")}");
            var conn = _conn!;
            var key  = _laser!.Key;
            await Task.Run(() =>
                conn.SetAttribute(key, "focusFinderOn",
                    new byte[] { 0x00, 0x00, 0x00, 0x08 },
                    new byte[] { (byte)(on ? 1 : 0), 0x00 }), ct);
            FocusFinderState = on;
            Log($"    focus finder is {(on ? "ON" : "OFF")}.");
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task RefreshShutterStateAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _laser is null) return;
        if (!await _lock.WaitAsync(50, ct)) return;
        try
        {
            if (!IsConnected || _conn is null || _laser is null) return;
            var conn = _conn; var laser = _laser;
            ShutterState = await Task.Run(() =>
            {
                try { return conn.GetBoolAttribute(laser.Key, "shutterOpen"); }
                catch (Exception ex) { Log($"    shutter state read failed: {ex.Message}"); return (bool?)null; }
            }, ct);
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task SetShutterAsync(bool on, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log($"    shutterOpen = {(on ? "TRUE" : "FALSE")}");
            var conn = _conn!; var key = _laser!.Key;
            await Task.Run(() =>
                conn.SetAttribute(key, "shutterOpen",
                    new byte[] { 0x00, 0x00, 0x00, 0x08 },
                    new byte[] { (byte)(on ? 1 : 0), 0x00 }), ct);
            ShutterState = on;
            Log($"    shutter is {(on ? "OPEN" : "CLOSED")}.");
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task RefreshLampTestStateAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _laser is null) return;
        if (!await _lock.WaitAsync(50, ct)) return;
        try
        {
            if (!IsConnected || _conn is null || _laser is null) return;
            var conn = _conn; var laser = _laser;
            LampTestState = await Task.Run(() =>
            {
                try { return conn.GetBoolAttribute(laser.Key, "lampTestOn"); }
                catch (Exception ex) { Log($"    lamp test state read failed: {ex.Message}"); return (bool?)null; }
            }, ct);
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task SetLampTestAsync(bool on, CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log($"    lampTestOn = {(on ? "TRUE" : "FALSE")}");
            var conn = _conn!; var key = _laser!.Key;
            await Task.Run(() =>
                conn.SetAttribute(key, "lampTestOn",
                    new byte[] { 0x00, 0x00, 0x00, 0x08 },
                    new byte[] { (byte)(on ? 1 : 0), 0x00 }), ct);
            LampTestState = on;
            Log($"    lamp test is {(on ? "ON" : "OFF")}.");
            StateChanged?.Invoke();
        }
        finally { _lock.Release(); }
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log("Shutdown: sending Logout(1) + Shutdown...");
            var conn   = _conn!;
            var sysKey = _sysControl!.Key;
            await Task.Run(() =>
            {
                conn.Logout(sysKey, 1);
                conn.Shutdown(sysKey);
            }, ct);
            Log("Shutdown command sent. Disconnecting.");
            DisconnectLocked();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"    Shutdown error: {ex.Message}");
            // Force disconnect regardless — controller may stop responding
            DisconnectLocked();
        }
        finally { _lock.Release(); }
    }

    public async Task BlinkAsync(int seconds, CancellationToken ct = default)
    {
        Log($"[blink] ON  (holding {seconds}s)");
        await SetPilotAsync(true, ct);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
        finally
        {
            // Always turn off, even if the delay was cancelled.
            Log("[blink] OFF");
            await SetPilotAsync(false, CancellationToken.None);
            Log("    blink complete.");
        }
    }

    // ── Axis control ───────────────────────────────────────────────────────

    // Jog direction constants — 0/1/3 confirmed from Rofin-AxisTest.pcapng.
    // 2/4 are inferred (symmetric with 1/3 for the opposite physical direction).
    public const uint JogStop     = 0;
    public const uint JogSlowUp   = 1;   // neg direction (bed toward laser)
    public const uint JogSlowDown = 2;   // pos direction (bed away from laser) — inferred
    public const uint JogFastUp   = 3;   // neg direction fast
    public const uint JogFastDown = 4;   // pos direction fast — inferred

    public async Task JogAsync(uint direction, CancellationToken ct = default)
    {
        Log($"    Jog({direction}): called (IsConnected={IsConnected})");
        EnsureConnected();
        Log($"    Jog({direction}): waiting for lock...");
        await _lock.WaitAsync(ct);
        try
        {
            Log($"    Jog({direction}): sending...");
            var conn    = _conn!;
            var machKey = _machControl!.Key;
            await Task.Run(() =>
            {
                var axesCtrl = conn.GetAxesControl(machKey);
                conn.Jog(axesCtrl.Key, direction);
            }, ct);
            Log($"    Jog({direction}): OK");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"    Jog({direction}) ERROR: {ex.Message}");
            if (ex is System.IO.IOException)
                DisconnectLocked();
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task ReferenceDriveAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            Log("    ReferenceDrive: GetAxesControl...");
            var conn    = _conn!;
            var machKey = _machControl!.Key;
            await Task.Run(() =>
            {
                var axesCtrl = conn.GetAxesControl(machKey);
                Log("    ReferenceDrive: sending...");
                conn.ReferenceDrive(axesCtrl.Key);
            }, ct);
            Log("    ReferenceDrive command sent.");
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"    ReferenceDrive ERROR: {ex.Message}");
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task RefreshAxisPositionAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _lifAxisKey is null) return;
        // Skip this poll cycle if a user command (pilot, jog, etc.) is already running.
        // Without this, timer ticks queue behind user commands and cause 5-15s delays.
        if (!await _lock.WaitAsync(50, ct)) return;
        try
        {
            if (!IsConnected || _conn is null || _lifAxisKey is null) return;
            var conn = _conn;
            var key  = _lifAxisKey;
            var pos  = await Task.Run(() => conn.GetDoubleAttribute(key, "actPosition"), ct);
            AxisPosition = pos;
            StateChanged?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (System.IO.IOException ex)
        {
            // Socket error during position poll — the TCP stream is now corrupted.
            // Disconnect immediately so the socket doesn't poison subsequent commands.
            Log($"    position poll IO error: {ex.Message} — disconnecting");
            DisconnectLocked();
        }
        catch { _lifAxisKey = null; }  // persistent failure: stop polling
        finally { _lock.Release(); }
    }

    // ── Device enumeration ─────────────────────────────────────────────────

    public async Task<List<DeviceInfo>> EnumerateDevicesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(BuildDeviceTree, ct);
        }
        finally { _lock.Release(); }
    }

    List<DeviceInfo> BuildDeviceTree()
    {
        // GetAllComponents(SystemControl) returns a flat list of ALL hardware
        // components across every subsystem — the server has no "get children of X"
        // API (every per-component call returns BAD_OPERATION; the 257KB GetClassInfos
        // blob contains the schema but is opaque/undocumented).
        // We reconstruct the nested view using a static className→parentClassName
        // table inferred from the official LaserConsole UI (screenshot, 2026-06-29).
        // All names, keys and roles are live data; only the grouping is a heuristic.

        List<ObjRef> flat;
        try { flat = _conn!.GetAllComponents(_sysControl!.Key); }
        catch (Exception ex) { Log($"    GetAllComponents failed: {ex.Message}"); flat = new(); }

        // Build DeviceInfo nodes, collect className for each.
        var nodes = new List<(DeviceInfo node, string cn)>(flat.Count);
        foreach (var obj in flat)
        {
            string generic = "", className = "";
            try { generic   = _conn!.GetComponentGenericName(obj.Key); } catch { }
            try { className = _conn!.GetComponentClassName(obj.Key);   } catch { }

            var attrs  = ReadComponentAttributes(obj.Key, className);
            var status = DeriveStatus(attrs);

            nodes.Add((new DeviceInfo
            {
                Name       = string.IsNullOrEmpty(generic) ? className : generic,
                Role       = className,
                Endpoint   = $"{obj.Host}:{obj.Port}",
                KeyHex     = BitConverter.ToString(obj.Key),
                Status     = status,
                Attributes = attrs,
            }, className));
        }

        // Attach each node to its parent's Children list using the static class map.
        // First pass: index all nodes by className so parents can be located.
        var byClass = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
        foreach (var (node, cn) in nodes)
            byClass.TryAdd(cn, node);  // first instance wins when duplicates exist

        var sysCtrlChildren = new List<DeviceInfo>();

        foreach (var (node, cn) in nodes)
        {
            // Skip self-reference: the controller lists SystemControl as one of its
            // own components, but it is already the tree root node.
            if (cn == "SystemControl") continue;

            if (ComponentParentClass.TryGetValue(cn, out string? parentCn))
            {
                // Attach to the named parent class — fall through to root if not found.
                if (parentCn != null && byClass.TryGetValue(parentCn, out var parent))
                { parent.Children.Add(node); continue; }
            }
            // Unknown class (or unmapped) → default to MachineControl if it exists.
            if (!RootComponentClasses.Contains(cn) &&
                byClass.TryGetValue("MachineControl", out var mach))
            { mach.Children.Add(node); continue; }

            sysCtrlChildren.Add(node);
        }

        var sysCtrl = new DeviceInfo
        {
            Name     = "SystemControl",
            Role     = _sysControl!.ShortType,
            Endpoint = $"{_sysControl.Host}:{_sysControl.Port}",
            KeyHex   = BitConverter.ToString(_sysControl.Key),
            Children = sysCtrlChildren,
        };

        return new List<DeviceInfo>
        {
            new DeviceInfo
            {
                Name     = "Controller",
                Role     = _controller!.ShortType,
                Endpoint = $"{_controller.Host}:{_controller.Port}",
                KeyHex   = BitConverter.ToString(_controller.Key),
                Children = { sysCtrl }
            }
        };
    }

    // Class names that are direct children of SystemControl in the display tree.
    static readonly HashSet<string> RootComponentClasses = new(StringComparer.Ordinal)
        { "ErrorMonitor", "MachineControl", "ProgramControl" };

    // className → parentClassName — inferred from the official LaserConsole UI.
    // Unmapped classes default to MachineControl (see BuildDeviceTree).
    // "SystemControl" is intentionally absent: it appears in the flat list as a
    // self-reference and is skipped via the fallback-to-MachineControl path.
    static readonly Dictionary<string, string?> ComponentParentClass =
        new(StringComparer.Ordinal)
    {
        // Top-level (direct children of SystemControl)
        ["ErrorMonitor"]          = null,   // null → sysCtrlChildren
        ["MachineControl"]        = null,
        ["ProgramControl"]        = null,
        // MachineControl's subsystem children
        ["AxesControl"]           = "MachineControl",
        ["CANOpen"]               = "MachineControl",
        ["GalvoControl"]          = "MachineControl",
        ["GenericCommand"]        = "MachineControl",
        ["GenericEvent"]          = "MachineControl",
        ["Interpolation"]         = "MachineControl",
        ["IOControl"]             = "MachineControl",
        ["PowerlineE"]            = "MachineControl",
        ["LIF_Driver"]            = "MachineControl",
        ["ServerHeartbeat"]       = "MachineControl",
        // AxesControl subtree
        ["AxesControllerLIF"]     = "AxesControl",
        ["LIFAxis"]               = "AxesControllerLIF",
        // GalvoControl subtree
        ["GalvoHeadContainer"]    = "GalvoControl",
        ["GalvoHead"]             = "GalvoHeadContainer",
        ["MarkingOnTheFly"]       = "GalvoControl",
        ["ScannerAutocalibration"] = "GalvoControl",
        // Interpolation subtree
        ["IIF_Driver"]            = "Interpolation",
        // IOControl subtree
        ["LIFIO"]                 = "IOControl",
        ["PLTIO"]                 = "IOControl",
        // PowerlineE subtree
        ["PowerSupply_HN800"]     = "PowerlineE",
        ["PLELaserHead"]          = "PowerlineE",
    };

    // Read standard + class-specific attributes for one component node.
    Dictionary<string, string> ReadComponentAttributes(byte[] key, string className)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Try(string name, string display)
        {
            try
            {
                var v = _conn!.GetAttributeString(key, name);
                if (v != null) d[display] = v;
            }
            catch { }
        }

        // Universal attributes present on every Persistent component.
        // State: format integer as human-readable enum text.
        try
        {
            var raw = _conn!.GetAttributeString(key, "state");
            d["state"] = raw switch { "0"=>"suspended", "1"=>"suspending", "2"=>"resuming", "3"=>"running", "4"=>"error", _ => raw ?? "" };
        }
        catch { }
        Try("active",    "active");
        Try("isWarning", "warning");

        // PowerlineE (the laser head) — rich status and control attributes
        if (className == "PowerlineE")
        {
            Try("beamOn",         "beamOn");
            Try("shutterOpen",    "shutter");
            Try("pilotOn",        "pilot");
            Try("actLaserPower",  "power");
            Try("actTemperature", "temperature");
            Try("cwAllowed",      "cwAllowed");
            Try("operationMode",  "operationMode");
        }
        // PowerSupply — thermal and electrical state
        else if (className == "PowerSupply_HN800")
        {
            Try("actTemperature", "temperature");
        }
        // Axes: current position and in-position flag
        else if (className == "LIFAxis")
        {
            Try("actPosition",  "position");
            Try("IN_POSITION",  "inPosition");
        }

        return d;
    }

    static ComponentStatus DeriveStatus(Dictionary<string, string> attrs)
    {
        if (attrs.TryGetValue("state", out string? st))
        {
            return st switch
            {
                "running"   => ComponentStatus.Ok,
                "error"     => ComponentStatus.Error,
                "suspended" or "suspending" or "resuming" => ComponentStatus.Warning,
                _           => ComponentStatus.Unknown,
            };
        }
        return ComponentStatus.Unknown;
    }

    void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected to controller.");
    }

    public void Dispose()
    {
        // Force-close the socket first so any in-flight GIOP read/write on a
        // background thread throws immediately and releases the lock, then we
        // can acquire it without blocking the UI thread indefinitely.
        _conn?.Dispose();

        _lock.Wait();
        try { DisconnectLocked(); }
        finally { _lock.Release(); _lock.Dispose(); }
    }
}
