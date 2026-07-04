using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

public enum JobStatus { Idle, Running, Paused, Complete, Error }

public sealed class JobPageViewModel : ViewModelBase, IDisposable
{
    readonly LaserService  _service;
    readonly GCodeServer   _server = new();

    // ── Machine state carried across GCode commands ───────────────────────

    float  _curX, _curY, _curZ;
    float  _feedRate  = 1000;
    float  _power     = 0;
    bool   _laserOn   = false;
    bool   _absolute  = true;     // G90 = absolute, G91 = relative
    bool   _mmUnits   = true;     // G21 = mm, G20 = inches

    // ── Commands ──────────────────────────────────────────────────────────

    readonly AsyncRelayCommand _toggleServerCmd;
    public System.Windows.Input.ICommand ToggleServerCommand => _toggleServerCmd;

    // ── Observable state ──────────────────────────────────────────────────

    int _port = 5000;
    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    JobStatus _jobStatus = JobStatus.Idle;
    public JobStatus JobStatus
    {
        get => _jobStatus;
        private set
        {
            SetField(ref _jobStatus, value);
            OnPropertyChanged(nameof(JobStatusText));
            OnPropertyChanged(nameof(JobStatusBrush));
        }
    }

    public string JobStatusText => _jobStatus switch
    {
        JobStatus.Idle     => "Idle — waiting for GCode",
        JobStatus.Running  => "Running job…",
        JobStatus.Paused   => "Paused",
        JobStatus.Complete => "Job complete",
        JobStatus.Error    => "Error",
        _                  => "Unknown",
    };

    public IBrush JobStatusBrush => _jobStatus switch
    {
        JobStatus.Running  => new SolidColorBrush(Color.Parse("#A6E3A1")),
        JobStatus.Complete => new SolidColorBrush(Color.Parse("#89B4FA")),
        JobStatus.Error    => new SolidColorBrush(Color.Parse("#F38BA8")),
        _                  => new SolidColorBrush(Color.Parse("#6C7086")),
    };

    public bool ServerRunning => _server.IsRunning;

    public string ServerToggleLabel  => _server.IsRunning ? "STOP SERVER"  : "START SERVER";
    public IBrush ServerToggleBg     => _server.IsRunning
        ? new SolidColorBrush(Color.Parse("#3A1E22"))
        : new SolidColorBrush(Color.Parse("#1E3A2E"));
    public IBrush ServerToggleFg     => _server.IsRunning
        ? new SolidColorBrush(Color.Parse("#F38BA8"))
        : new SolidColorBrush(Color.Parse("#A6E3A1"));

    public ObservableCollection<string> Log { get; } = new();

    // ── Constructor ───────────────────────────────────────────────────────

    public JobPageViewModel(LaserService service)
    {
        _service = service;

        _toggleServerCmd = new AsyncRelayCommand(ToggleServerAsync, () => true);

        _server.StatusMessage += msg => Dispatcher.UIThread.Post(() => AddLog(msg));
        _service.StateChanged += ()  => Dispatcher.UIThread.Post(RefreshState);
    }

    // ── Server toggle ─────────────────────────────────────────────────────

    async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
        }
        else
        {
            await Task.Run(() => _server.Start(Port, HandleGCodeLine));
        }
        RefreshServerState();
    }

    void RefreshServerState()
    {
        OnPropertyChanged(nameof(ServerRunning));
        OnPropertyChanged(nameof(ServerToggleLabel));
        OnPropertyChanged(nameof(ServerToggleBg));
        OnPropertyChanged(nameof(ServerToggleFg));
        _toggleServerCmd.Raise();
    }

    void RefreshState()
    {
        // Connection state may affect what we can do
        _toggleServerCmd.Raise();
    }

    // ── GCode handler ─────────────────────────────────────────────────────

    async Task HandleGCodeLine(GCodeLine line, CancellationToken ct)
    {
        if (line.IsEmpty) return;

        await Dispatcher.UIThread.InvokeAsync(() => JobStatus = JobStatus.Running);

        // ── Units & coordinate mode ──────────────────────────────────────
        if (line.G == 20) { _mmUnits = false; return; }
        if (line.G == 21) { _mmUnits = true;  return; }
        if (line.G == 90) { _absolute = true;  return; }
        if (line.G == 91) { _absolute = false; return; }

        // ── Feed rate (standalone F word or from G1) ─────────────────────
        if (line.F.HasValue) _feedRate = line.F.Value;

        // ── Power (standalone S word) ─────────────────────────────────────
        if (line.S.HasValue) _power = line.S.Value;

        // ── M codes ──────────────────────────────────────────────────────
        if (line.M.HasValue)
        {
            switch (line.M.Value)
            {
                case 3: case 4:  // laser on
                    _laserOn = true;
                    if (line.S.HasValue) _power = line.S.Value;
                    AddLogUI($"   laser ON  power={_power:F0}");
                    if (_service.IsConnected)
                        await _service.SetPilotAsync(true, ct);
                    return;

                case 5:          // laser off
                    _laserOn = false;
                    AddLogUI($"   laser OFF");
                    if (_service.IsConnected)
                        await _service.SetPilotAsync(false, ct);
                    return;

                case 2: case 30: // end of program
                    AddLogUI("   program end");
                    _laserOn = false;
                    if (_service.IsConnected)
                        await _service.SetPilotAsync(false, ct);
                    await Dispatcher.UIThread.InvokeAsync(() => JobStatus = JobStatus.Complete);
                    return;
            }
        }

        // ── G codes ──────────────────────────────────────────────────────
        if (line.G.HasValue)
        {
            switch (line.G.Value)
            {
                case 28:  // home / reference drive
                    AddLogUI("   G28 — reference drive");
                    if (_service.IsConnected)
                        await _service.ReferenceDriveAsync(ct);
                    return;

                case 0:   // rapid move (laser off)
                case 1:   // cutting move (laser on)
                    await HandleMove(line, ct);
                    return;
            }
        }
    }

    async Task HandleMove(GCodeLine line, CancellationToken ct)
    {
        // Resolve target coordinates
        float tx = line.X.HasValue ? (_absolute ? ToMm(line.X.Value) : _curX + ToMm(line.X.Value)) : _curX;
        float ty = line.Y.HasValue ? (_absolute ? ToMm(line.Y.Value) : _curY + ToMm(line.Y.Value)) : _curY;
        float tz = line.Z.HasValue ? (_absolute ? ToMm(line.Z.Value) : _curZ + ToMm(line.Z.Value)) : _curZ;

        bool isRapid = line.G == 0;

        // Z axis — map to physical bed movement
        if (Math.Abs(tz - _curZ) > 0.001f)
        {
            AddLogUI($"   Z move {_curZ:F3} → {tz:F3} mm  (bed axis)");
            // Positive Z in Lightburn = move bed away (down), negative = toward laser
            // TODO: implement incremental jogging to reach tz
            _curZ = tz;
        }

        // X/Y — galvo scan head marking (ExecutePrimitives — not yet implemented)
        if (Math.Abs(tx - _curX) > 0.001f || Math.Abs(ty - _curY) > 0.001f)
        {
            string moveType = isRapid || !_laserOn
                ? "G0 rapid"
                : $"G1 mark  F={_feedRate:F0} S={_power:F0}";
            AddLogUI($"   {moveType}  ({_curX:F3},{_curY:F3}) → ({tx:F3},{ty:F3})  [ExecutePrimitives TBD]");
            _curX = tx;
            _curY = ty;
        }

        await Task.CompletedTask;
    }

    float ToMm(float v) => _mmUnits ? v : v * 25.4f;

    // ── Logging ───────────────────────────────────────────────────────────

    void AddLog(string msg)
        => Log.Add($"{DateTime.Now:HH:mm:ss}  {msg}");

    void AddLogUI(string msg)
        => Dispatcher.UIThread.Post(() => AddLog(msg));

    public void Dispose()
    {
        _server.Dispose();
    }
}
