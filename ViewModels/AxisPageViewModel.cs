using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LaserConsole.Services;

namespace LaserConsole.ViewModels;

public sealed class AxisPageViewModel : ViewModelBase
{
    readonly LaserService _service;
    readonly DispatcherTimer _posTimer;

    // Confirmed from Rofin-AxisTest.pcapng — constant second arg (2) is the axis
    // identifier within GetAxesControl. DOWN directions inferred from the pattern.
    const uint JogStop     = LaserService.JogStop;
    const uint JogSlowUp   = LaserService.JogSlowUp;
    const uint JogFastUp   = LaserService.JogFastUp;
    const uint JogSlowDown = LaserService.JogSlowDown;
    const uint JogFastDown = LaserService.JogFastDown;

    // Software limits from LaserConsole screenshot (E10 standard calibration).
    public const double SWLimitTop    = -0.1;    // mm — top of travel (bed closest to laser)
    public const double SWLimitBottom = 119.5;   // mm — bottom of travel (hardware endstop)

    readonly AsyncRelayCommand _fastDownCmd;
    readonly AsyncRelayCommand _slowDownCmd;
    readonly AsyncRelayCommand _stopCmd;
    readonly AsyncRelayCommand _slowUpCmd;
    readonly AsyncRelayCommand _fastUpCmd;
    readonly AsyncRelayCommand _referenceCmd;

    public System.Windows.Input.ICommand FastDownCommand  => _fastDownCmd;
    public System.Windows.Input.ICommand SlowDownCommand  => _slowDownCmd;
    public System.Windows.Input.ICommand StopCommand      => _stopCmd;
    public System.Windows.Input.ICommand SlowUpCommand    => _slowUpCmd;
    public System.Windows.Input.ICommand FastUpCommand    => _fastUpCmd;
    public System.Windows.Input.ICommand ReferenceCommand => _referenceCmd;

    public bool IsConnected => _service.IsConnected;

    public string PositionText => _service.AxisPosition.HasValue
        ? $"{_service.AxisPosition.Value:F2}"
        : "–––.––";

    // 0–100 value for ProgressBar showing travel (0=top, 100=bottom).
    public double PositionPercent
    {
        get
        {
            if (!_service.AxisPosition.HasValue) return 0;
            double span = SWLimitBottom - SWLimitTop;
            return Math.Clamp((_service.AxisPosition.Value - SWLimitTop) / span * 100, 0, 100);
        }
    }

    public string SWTopText    => $"{SWLimitTop:F2} mm";
    public string SWBottomText => $"{SWLimitBottom:F2} mm";

    public AxisPageViewModel(LaserService service)
    {
        _service = service;

        _fastDownCmd  = new AsyncRelayCommand(() => RunJog(JogFastDown),  () => IsConnected);
        _slowDownCmd  = new AsyncRelayCommand(() => RunJog(JogSlowDown),  () => IsConnected);
        _stopCmd      = new AsyncRelayCommand(() => RunJog(JogStop),      () => IsConnected);
        _slowUpCmd    = new AsyncRelayCommand(() => RunJog(JogSlowUp),    () => IsConnected);
        _fastUpCmd    = new AsyncRelayCommand(() => RunJog(JogFastUp),    () => IsConnected);
        _referenceCmd = new AsyncRelayCommand(RunReference,               () => IsConnected);

        service.StateChanged += () => Dispatcher.UIThread.Post(Refresh);

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += async (_, _) =>
        {
            try { await _service.RefreshAxisPositionAsync(); }
            catch { }
        };
        _posTimer.Start();
    }

    async Task RunJog(uint direction)
    {
        try { await _service.JogAsync(direction); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { /* swallow — UI stays responsive */ }
    }

    async Task RunReference()
    {
        try { await _service.ReferenceDriveAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { /* swallow */ }
    }

    void Refresh()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(PositionPercent));
        _fastDownCmd.Raise();
        _slowDownCmd.Raise();
        _stopCmd.Raise();
        _slowUpCmd.Raise();
        _fastUpCmd.Raise();
        _referenceCmd.Raise();
    }
}
