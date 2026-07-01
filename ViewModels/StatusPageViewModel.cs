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

    readonly AsyncRelayCommand _pilotOnCmd;
    readonly AsyncRelayCommand _pilotOffCmd;
    readonly AsyncRelayCommand _blinkCmd;

    public ObservableCollection<string> Log { get; } = new();

    public System.Windows.Input.ICommand PilotOnCommand  => _pilotOnCmd;
    public System.Windows.Input.ICommand PilotOffCommand => _pilotOffCmd;
    public System.Windows.Input.ICommand BlinkCommand    => _blinkCmd;

    decimal _blinkSeconds = 3;
    public decimal BlinkSeconds
    {
        get => _blinkSeconds;
        set => SetField(ref _blinkSeconds, value);
    }

    public bool IsConnected => _service.IsConnected;

    public string PilotStatusText => _service.PilotState switch
    {
        true  => "ON",
        false => "OFF",
        null  => "Unknown",
    };

    public IBrush PilotStatusBrush => _service.PilotState switch
    {
        true  => new SolidColorBrush(Color.Parse("#A6E3A1")),
        false => new SolidColorBrush(Color.Parse("#6C7086")),
        null  => new SolidColorBrush(Color.Parse("#45475A")),
    };

    public StatusPageViewModel(LaserService service)
    {
        _service = service;

        _pilotOnCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetPilotAsync(true)),
            () => service.IsConnected && !_busy);

        _pilotOffCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.SetPilotAsync(false)),
            () => service.IsConnected && !_busy);

        _blinkCmd = new AsyncRelayCommand(
            () => RunOperation(() => service.BlinkAsync((int)BlinkSeconds)),
            () => service.IsConnected && !_busy);

        service.LogMessage  += msg   => Dispatcher.UIThread.Post(() => AddLog(msg));
        service.StateChanged += () => Dispatcher.UIThread.Post(RefreshState);
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
        OnPropertyChanged(nameof(PilotStatusText));
        OnPropertyChanged(nameof(PilotStatusBrush));
        RaiseAllCommands();
    }

    void RaiseAllCommands()
    {
        _pilotOnCmd.Raise();
        _pilotOffCmd.Raise();
        _blinkCmd.Raise();
    }
}
