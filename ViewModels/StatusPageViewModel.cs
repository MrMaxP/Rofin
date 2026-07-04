using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

public sealed class StatusPageViewModel : ViewModelBase
{
    readonly LaserService _service;
    bool _busy;

    readonly AsyncRelayCommand _pilotToggleCmd;
    readonly AsyncRelayCommand _focusToggleCmd;
    readonly AsyncRelayCommand _shutterToggleCmd;
    readonly AsyncRelayCommand _lampToggleCmd;
    readonly AsyncRelayCommand _blinkCmd;
    readonly AsyncRelayCommand _shutdownCmd;

    public ObservableCollection<string> Log { get; } = new();

    public System.Windows.Input.ICommand PilotToggleCommand   => _pilotToggleCmd;
    public System.Windows.Input.ICommand FocusToggleCommand   => _focusToggleCmd;
    public System.Windows.Input.ICommand ShutterToggleCommand => _shutterToggleCmd;
    public System.Windows.Input.ICommand LampToggleCommand    => _lampToggleCmd;
    public System.Windows.Input.ICommand BlinkCommand         => _blinkCmd;
    public System.Windows.Input.ICommand ShutdownCommand      => _shutdownCmd;

    decimal _blinkSeconds = 3;
    public decimal BlinkSeconds
    {
        get => _blinkSeconds;
        set => SetField(ref _blinkSeconds, value);
    }

    public bool IsConnected => _service.IsConnected;

    // ── Pilot ──────────────────────────────────────────────────────────────

    public string PilotLabel => _service.PilotState switch
    {
        true  => "ON",
        false => "OFF",
        null  => "—",
    };

    public IBrush PilotBg => _service.PilotState switch
    {
        true  => new SolidColorBrush(Color.Parse("#1E3A2E")),
        false => new SolidColorBrush(Color.Parse("#252535")),
        null  => new SolidColorBrush(Color.Parse("#1E1E2E")),
    };

    public IBrush PilotFg => _service.PilotState switch
    {
        true  => new SolidColorBrush(Color.Parse("#A6E3A1")),
        false => new SolidColorBrush(Color.Parse("#6C7086")),
        null  => new SolidColorBrush(Color.Parse("#45475A")),
    };

    // ── Focus finder ───────────────────────────────────────────────────────

    public string FocusLabel => _service.FocusFinderState switch
    {
        true  => "ON",
        false => "OFF",
        null  => "—",
    };

    public IBrush FocusBg => _service.FocusFinderState switch
    {
        true  => new SolidColorBrush(Color.Parse("#2A1E3A")),
        false => new SolidColorBrush(Color.Parse("#252535")),
        null  => new SolidColorBrush(Color.Parse("#1E1E2E")),
    };

    public IBrush FocusFg => _service.FocusFinderState switch
    {
        true  => new SolidColorBrush(Color.Parse("#CBA6F7")),
        false => new SolidColorBrush(Color.Parse("#6C7086")),
        null  => new SolidColorBrush(Color.Parse("#45475A")),
    };

    // ── Shutter ────────────────────────────────────────────────────────────

    public string ShutterLabel => _service.ShutterState switch
    {
        true  => "OPEN",
        false => "CLOSED",
        null  => "—",
    };

    public IBrush ShutterBg => _service.ShutterState switch
    {
        true  => new SolidColorBrush(Color.Parse("#3A2A1E")),
        false => new SolidColorBrush(Color.Parse("#252535")),
        null  => new SolidColorBrush(Color.Parse("#1E1E2E")),
    };

    public IBrush ShutterFg => _service.ShutterState switch
    {
        true  => new SolidColorBrush(Color.Parse("#FAB387")),
        false => new SolidColorBrush(Color.Parse("#6C7086")),
        null  => new SolidColorBrush(Color.Parse("#45475A")),
    };

    // ── Lamp test ──────────────────────────────────────────────────────────

    public string LampLabel => _service.LampTestState switch
    {
        true  => "ON",
        false => "OFF",
        null  => "—",
    };

    public IBrush LampBg => _service.LampTestState switch
    {
        true  => new SolidColorBrush(Color.Parse("#3A361E")),
        false => new SolidColorBrush(Color.Parse("#252535")),
        null  => new SolidColorBrush(Color.Parse("#1E1E2E")),
    };

    public IBrush LampFg => _service.LampTestState switch
    {
        true  => new SolidColorBrush(Color.Parse("#F9E2AF")),
        false => new SolidColorBrush(Color.Parse("#6C7086")),
        null  => new SolidColorBrush(Color.Parse("#45475A")),
    };

    // ── Constructor ────────────────────────────────────────────────────────

    public StatusPageViewModel(LaserService service)
    {
        _service = service;

        _pilotToggleCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetPilotAsync(service.PilotState != true)),
            () => service.IsConnected && !_busy);

        _focusToggleCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetFocusFinderAsync(service.FocusFinderState != true)),
            () => service.IsConnected && !_busy);

        _shutterToggleCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetShutterAsync(service.ShutterState != true)),
            () => service.IsConnected && !_busy);

        _lampToggleCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetLampTestAsync(service.LampTestState != true)),
            () => service.IsConnected && !_busy);

        _blinkCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.BlinkAsync((int)BlinkSeconds)),
            () => service.IsConnected && !_busy);

        _shutdownCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.ShutdownAsync()),
            () => service.IsConnected && !_busy);

        service.LogMessage   += msg => Dispatcher.UIThread.Post(() => AddLog(msg));
        service.StateChanged += ()  => Dispatcher.UIThread.Post(RefreshState);
    }

    public void AddLog(string msg)
        => Log.Add($"{DateTime.Now:HH:mm:ss}  {msg}");

    async Task RunOperation(Func<Task> op)
    {
        _busy = true;
        RaiseAllCommands();
        try   { await op(); }
        catch (Exception ex) { AddLog($"ERROR: {ex.Message}"); }
        finally
        {
            _busy = false;
            RaiseAllCommands();
        }
    }

    void RefreshState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(PilotLabel));
        OnPropertyChanged(nameof(PilotBg));
        OnPropertyChanged(nameof(PilotFg));
        OnPropertyChanged(nameof(FocusLabel));
        OnPropertyChanged(nameof(FocusBg));
        OnPropertyChanged(nameof(FocusFg));
        OnPropertyChanged(nameof(ShutterLabel));
        OnPropertyChanged(nameof(ShutterBg));
        OnPropertyChanged(nameof(ShutterFg));
        OnPropertyChanged(nameof(LampLabel));
        OnPropertyChanged(nameof(LampBg));
        OnPropertyChanged(nameof(LampFg));
        RaiseAllCommands();
    }

    void RaiseAllCommands()
    {
        _pilotToggleCmd.Raise();
        _focusToggleCmd.Raise();
        _shutterToggleCmd.Raise();
        _lampToggleCmd.Raise();
        _blinkCmd.Raise();
        _shutdownCmd.Raise();
    }
}
