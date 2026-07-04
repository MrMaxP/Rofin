using Avalonia.Controls;
using Avalonia.Input;
using LaserConsole.ViewModels;

namespace LaserConsole.Views;

public partial class AxisPage : UserControl
{
    public AxisPage() => InitializeComponent();

    void OnJogPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AxisPageViewModel vm) return;
        if (sender is not Button btn) return;
        if (!uint.TryParse(btn.Tag?.ToString(), out var dir)) return;
        e.Pointer.Capture(btn);   // ensure we receive the matching release
        vm.BeginJog(dir);
    }

    void OnJogPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not AxisPageViewModel vm) return;
        e.Pointer.Capture(null);
        vm.EndJog();
    }
}
