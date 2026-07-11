using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

// Surfaces the LightBurn USB bridge (docs/lightburn-rofin-bridge.md) — it
// runs independently of the Rofin CORBA connection, started at app boot.
public sealed class BridgePageViewModel : ViewModelBase, IDisposable
{
    readonly RofinBridgeService _bridge;
    readonly DispatcherTimer    _pollActivityTimer;
    readonly DispatcherTimer    _previewRenderTimer;

    // How long after the last GetVersion/ReadPort to keep the indicator lit.
    // LightBurn's idle poll repeats every few ms, so in practice this reads
    // as "steady on while polling, off shortly after it stops" rather than
    // an actual flicker — which is the point.
    static readonly TimeSpan PollHoldTime = TimeSpan.FromMilliseconds(300);

    // How long a drawn move stays visible before fully fading.
    static readonly TimeSpan PreviewFadeTime = TimeSpan.FromMilliseconds(500);

    public ObservableCollection<string> Log { get; } = new();

    public bool    IsPortOpen  => _bridge.IsPortOpen;
    public bool?   HostMounted => _bridge.HostMounted;
    public string? PortName    => _bridge.PortName;

    public string StatusText => (IsPortOpen, HostMounted) switch
    {
        (false, _)    => "Waiting for bridge device...",
        (true, true)  => $"Connected on {PortName} — LightBurn mounted",
        (true, false) => $"Connected on {PortName} — waiting for LightBurn",
        (true, null)  => $"Connected on {PortName}",
    };

    public IBrush StatusBrush => (IsPortOpen, HostMounted) switch
    {
        (false, _)   => new SolidColorBrush(Color.Parse("#6C7086")),
        (true, true) => new SolidColorBrush(Color.Parse("#A6E3A1")),
        (true, _)    => new SolidColorBrush(Color.Parse("#F9E2AF")),
    };

    public long CommandCount   => _bridge.CommandCount;
    public long ListEntryCount => _bridge.ListEntryCount;
    public long GetVersionCount => _bridge.GetVersionCount;
    public long ReadPortCount   => _bridge.ReadPortCount;

    bool _getVersionActive;
    bool _readPortActive;
    public bool GetVersionActive => _getVersionActive;
    public bool ReadPortActive   => _readPortActive;

    public IBrush GetVersionBrush => _getVersionActive
        ? new SolidColorBrush(Color.Parse("#89B4FA"))
        : new SolidColorBrush(Color.Parse("#313244"));
    public IBrush ReadPortBrush => _readPortActive
        ? new SolidColorBrush(Color.Parse("#89B4FA"))
        : new SolidColorBrush(Color.Parse("#313244"));

    string? _manualPort;
    public string? ManualPort
    {
        get => _manualPort;
        set
        {
            if (!SetField(ref _manualPort, value)) return;
            _bridge.ManualPortName = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    // ── Mark preview ──────────────────────────────────────────────────────

    public double FieldWidthMm
    {
        get => _bridge.FieldWidthMm;
        set
        {
            if (value <= 0 || _bridge.FieldWidthMm == value) return;
            _bridge.FieldWidthMm = value;
            OnPropertyChanged();
        }
    }

    public double FieldHeightMm
    {
        get => _bridge.FieldHeightMm;
        set
        {
            if (value <= 0 || _bridge.FieldHeightMm == value) return;
            _bridge.FieldHeightMm = value;
            OnPropertyChanged();
        }
    }

    public MarkState MarkState => _bridge.MarkState;

    public string MarkStateText => MarkState switch
    {
        MarkState.Marking => "MARKING",
        MarkState.Tracing => "TRACING OUTLINE",
        _                  => "IDLE",
    };

    public IBrush MarkStateBrush => MarkState switch
    {
        MarkState.Marking => new SolidColorBrush(Color.Parse("#F38BA8")),
        MarkState.Tracing => new SolidColorBrush(Color.Parse("#45B3D4")),
        _                  => new SolidColorBrush(Color.Parse("#6C7086")),
    };

    public bool SimulatedBusy => _bridge.SimulatedBusy;

    public bool RealtimePlayback
    {
        get => _bridge.RealtimePlayback;
        set
        {
            if (_bridge.RealtimePlayback == value) return;
            _bridge.RealtimePlayback = value;
            OnPropertyChanged();
        }
    }

    public double MarkSpeedMmPerSec
    {
        get => _bridge.MarkSpeedMmPerSec;
        set
        {
            if (value <= 0 || _bridge.MarkSpeedMmPerSec == value) return;
            _bridge.MarkSpeedMmPerSec = value;
            OnPropertyChanged();
        }
    }

    public double JumpSpeedMmPerSec
    {
        get => _bridge.JumpSpeedMmPerSec;
        set
        {
            if (value <= 0 || _bridge.JumpSpeedMmPerSec == value) return;
            _bridge.JumpSpeedMmPerSec = value;
            OnPropertyChanged();
        }
    }

    readonly List<(double x1, double y1, double x2, double y2, MoveKind kind, DateTime createdAt)> _liveSegments = new();

    public IReadOnlyList<PreviewLine> PreviewLines { get; private set; } = Array.Empty<PreviewLine>();

    public BridgePageViewModel(RofinBridgeService bridge)
    {
        _bridge = bridge;
        _bridge.LogMessage   += msg => Dispatcher.UIThread.Post(() => AddLog(msg));
        _bridge.StateChanged += ()  => Dispatcher.UIThread.Post(RefreshState);

        // Polls the last-seen timestamps rather than reacting to individual
        // commands, so the indicator also turns itself off once LightBurn
        // stops polling (not just when a new poll lights it up).
        _pollActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollActivityTimer.Tick += (_, _) => RefreshPollActivity();
        _pollActivityTimer.Start();

        // ~30fps: drains new segments from the bridge, ages/fades existing
        // ones, drops fully-faded ones. Independent of the log/poll timers
        // since it needs to feel smooth, not just "eventually consistent".
        _previewRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _previewRenderTimer.Tick += (_, _) => RenderPreviewTick();
        _previewRenderTimer.Start();
    }

    // Hard caps as a safety net: even with the dedup fixes in
    // RofinBridgeService, an unbounded ObservableCollection bound to a
    // non-virtualizing ItemsControl (or an unbounded segment list redrawn
    // every frame) is exactly what locked the UI thread up on a complex
    // shape — the log kept accepting entries and the preview kept growing
    // faster than either could ever be trimmed. These bound it regardless
    // of how the upstream traffic behaves.
    const int MaxLogLines = 500;
    const int MaxLiveSegments = 2000;

    void AddLog(string msg)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    void RefreshState()
    {
        OnPropertyChanged(nameof(IsPortOpen));
        OnPropertyChanged(nameof(HostMounted));
        OnPropertyChanged(nameof(PortName));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(CommandCount));
        OnPropertyChanged(nameof(ListEntryCount));
        OnPropertyChanged(nameof(GetVersionCount));
        OnPropertyChanged(nameof(ReadPortCount));
        OnPropertyChanged(nameof(MarkState));
        OnPropertyChanged(nameof(MarkStateText));
        OnPropertyChanged(nameof(MarkStateBrush));
    }

    void RenderPreviewTick()
    {
        foreach (var seg in _bridge.DrainPreviewSegments())
            _liveSegments.Add((seg.X1Mm, seg.Y1Mm, seg.X2Mm, seg.Y2Mm, seg.Kind, seg.CreatedAt));

        var now = DateTime.Now;
        _liveSegments.RemoveAll(s => now - s.createdAt >= PreviewFadeTime);
        if (_liveSegments.Count > MaxLiveSegments)
            _liveSegments.RemoveRange(0, _liveSegments.Count - MaxLiveSegments);
        if (_liveSegments.Count == 0 && PreviewLines.Count == 0) return;

        var snapshot = new List<PreviewLine>(_liveSegments.Count);
        foreach (var s in _liveSegments)
        {
            double opacity = 1.0 - (now - s.createdAt).TotalMilliseconds / PreviewFadeTime.TotalMilliseconds;
            snapshot.Add(new PreviewLine
            {
                X1Mm = s.x1, Y1Mm = s.y1, X2Mm = s.x2, Y2Mm = s.y2,
                Kind = s.kind, Opacity = Math.Clamp(opacity, 0, 1),
            });
        }
        PreviewLines = snapshot;
        OnPropertyChanged(nameof(PreviewLines));
    }

    bool _lastSimulatedBusy;

    void RefreshPollActivity()
    {
        var now = DateTime.Now;
        bool gv = _bridge.LastGetVersionAt is { } gvAt && now - gvAt < PollHoldTime;
        bool rp = _bridge.LastReadPortAt   is { } rpAt && now - rpAt < PollHoldTime;

        if (gv != _getVersionActive)
        {
            _getVersionActive = gv;
            OnPropertyChanged(nameof(GetVersionActive));
            OnPropertyChanged(nameof(GetVersionBrush));
        }
        if (rp != _readPortActive)
        {
            _readPortActive = rp;
            OnPropertyChanged(nameof(ReadPortActive));
            OnPropertyChanged(nameof(ReadPortBrush));
        }

        // SimulatedBusy flips on a background thread with no event of its
        // own (it's checked once per serial read-loop iteration) — piggyback
        // on this timer rather than adding another polling loop.
        bool busy = _bridge.SimulatedBusy;
        if (busy != _lastSimulatedBusy)
        {
            _lastSimulatedBusy = busy;
            OnPropertyChanged(nameof(SimulatedBusy));
        }
    }

    public void Dispose()
    {
        _pollActivityTimer.Stop();
        _previewRenderTimer.Stop();
    }
}
