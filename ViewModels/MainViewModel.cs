using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    readonly LaserService      _service;
    readonly AsyncRelayCommand _connectCmd;
    readonly AsyncRelayCommand _disconnectCmd;
    readonly DispatcherTimer   _clockTimer;

    bool _busy;

    public StatusPageViewModel  StatusPageVM  { get; }
    public DevicesPageViewModel DevicesPageVM { get; }
    public AxisPageViewModel    AxisPageVM    { get; }

    public System.Windows.Input.ICommand ConnectCommand    => _connectCmd;
    public System.Windows.Input.ICommand DisconnectCommand => _disconnectCmd;

    // ── Navigation ─────────────────────────────────────────────────────────

    int _selectedPageIndex;
    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (SetField(ref _selectedPageIndex, value))
                OnPropertyChanged(nameof(CurrentPage));
        }
    }

    public object CurrentPage => SelectedPageIndex switch
    {
        1 => (object)DevicesPageVM,
        2 => AxisPageVM,
        _ => StatusPageVM,
    };

    // ── Connection UI state ────────────────────────────────────────────────

    string _host = "192.168.0.200";
    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public bool   IsConnected    => _service.IsConnected;
    public bool   IsDisconnected => !_service.IsConnected;

    public string StatusText => _service.IsConnected
        ? $"Connected  {_service.Settings.Host}"
        : "Disconnected";

    public IBrush StatusBrush => _service.IsConnected
        ? new SolidColorBrush(Color.Parse("#A6E3A1"))
        : new SolidColorBrush(Color.Parse("#6C7086"));

    // ── Clock ──────────────────────────────────────────────────────────────

    public string CurrentTime => DateTime.Now.ToString("HH:mm:ss");

    // ── Constructor ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _service = new LaserService();

        StatusPageVM  = new StatusPageViewModel(_service);
        DevicesPageVM = new DevicesPageViewModel(_service);
        AxisPageVM    = new AxisPageViewModel(_service);

        _connectCmd = new AsyncRelayCommand(
            ConnectAsync,
            () => !_service.IsConnected && !_busy);

        _disconnectCmd = new AsyncRelayCommand(
            DisconnectAsync,
            () => _service.IsConnected && !_busy);

        _service.StateChanged += () => Dispatcher.UIThread.Post(RefreshConnectionState);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => OnPropertyChanged(nameof(CurrentTime));
        _clockTimer.Start();
    }

    // ── Operations ─────────────────────────────────────────────────────────

    async Task ConnectAsync()
    {
        _busy = true;
        UpdateCommandStates();
        try
        {
            _service.Settings.Host = Host;
            StatusPageVM.AddLog($"─── Connecting to {Host} ───");
            await _service.ConnectAsync();
        }
        catch (Exception ex)
        {
            StatusPageVM.AddLog($"ERROR: {ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateCommandStates();
        }
    }

    async Task DisconnectAsync()
    {
        _busy = true;
        UpdateCommandStates();
        try
        {
            await Task.Run(_service.Disconnect);
        }
        finally
        {
            _busy = false;
            UpdateCommandStates();
        }
    }

    void RefreshConnectionState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        UpdateCommandStates();
    }

    void UpdateCommandStates()
    {
        _connectCmd.Raise();
        _disconnectCmd.Raise();
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _service.Dispose();
    }
}
