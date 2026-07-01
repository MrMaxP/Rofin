using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using LaserConsole.ViewModels;

namespace LaserConsole.Views;

public partial class StatusPage : UserControl
{
    public StatusPage() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is StatusPageViewModel vm)
        {
            vm.Log.CollectionChanged += OnLogChanged;
        }
    }

    void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // Defer scroll to after layout pass so the new item is measured.
        Dispatcher.UIThread.Post(() =>
        {
            var scroll = this.FindControl<ScrollViewer>("LogScroll");
            scroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
