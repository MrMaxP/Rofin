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

    readonly AsyncRelayCommand _referenceCmd;

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

        _referenceCmd = new AsyncRelayCommand(RunReference, () => IsConnected);

        service.StateChanged += () => Dispatcher.UIThread.Post(OnServiceStateChanged);

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += async (_, _) =>
        {
            try { await _service.RefreshAxisPositionAsync(); }
            catch { }
        };
    }

    public void Activate()
    {
        if (_service.IsConnected)
            _posTimer.Start();
    }

    public void Deactivate()
    {
        _posTimer.Stop();
    }

    public void BeginJog(uint direction)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] BeginJog({direction}) IsConnected={_service.IsConnected}");
        if (!_service.IsConnected) return;
        _ = RunJog(direction);
    }

    public void EndJog()
    {
        if (!_service.IsConnected) return;
        _ = RunJog(JogStop);
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

    void OnServiceStateChanged()
    {
        if (!_service.IsConnected)
            _posTimer.Stop();
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(PositionPercent));
        _referenceCmd.Raise();
    }
}
