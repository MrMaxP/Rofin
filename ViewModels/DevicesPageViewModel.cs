using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

public sealed class DevicesPageViewModel : ViewModelBase
{
    readonly LaserService _service;
    readonly AsyncRelayCommand _refreshCmd;

    public ObservableCollection<DeviceNode> RootDevices { get; } = new();

    public System.Windows.Input.ICommand RefreshCommand => _refreshCmd;

    bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    DeviceNode? _selectedDevice;
    public DeviceNode? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetField(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    public bool HasSelection   => _selectedDevice is not null;
    public bool HasNoSelection => _selectedDevice is null;

    public DevicesPageViewModel(LaserService service)
    {
        _service = service;

        _refreshCmd = new AsyncRelayCommand(
            RefreshAsync,
            () => service.IsConnected && !_isRefreshing);

        service.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _refreshCmd.Raise();
            if (service.IsConnected) _ = RefreshAsync();
        });
    }

    public async Task RefreshAsync()
    {
        if (!_service.IsConnected) return;
        IsRefreshing = true;
        _refreshCmd.Raise();
        try
        {
            var infos = await _service.EnumerateDevicesAsync();
            RootDevices.Clear();
            foreach (var info in infos)
                RootDevices.Add(DeviceNode.FromInfo(info));
            SelectedDevice = null;
        }
        catch (Exception ex)
        {
            RootDevices.Clear();
            RootDevices.Add(new DeviceNode
            {
                Name       = "Enumeration failed",
                Role       = ex.Message,
                NodeStatus = DeviceNodeStatus.Error,
            });
        }
        finally
        {
            IsRefreshing = false;
            _refreshCmd.Raise();
        }
    }
}
