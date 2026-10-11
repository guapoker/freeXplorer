using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FreeXplorer.App.Services;
using FreeXplorer.App.ViewModels;

namespace FreeXplorer.App;

public partial class MainWindow : Window
{
    readonly MainViewModel model;
    bool closeConfirmed;
    bool scrollQueued;

    public MainWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;

        DarkSwitch.IsChecked = Theme.IsDark;
        DarkSwitch.Checked += (_, _) => Theme.Choose(true);
        DarkSwitch.Unchecked += (_, _) => Theme.Choose(false);

        model.Log.CollectionChanged += OnLogChanged;
        Loaded += async (_, _) => await model.InitializeAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(OnWindowMessage);
    }

    // Windows tells every window when a drive is plugged in or pulled out, which is the cue to look again
    IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        const int DevNodesChanged = 0x0007;
        const int DeviceArrival = 0x8000;
        const int DeviceRemoved = 0x8004;

        if (message == WmDeviceChange && wParam.ToInt64() is DevNodesChanged or DeviceArrival or DeviceRemoved)
            model.DevicesChanged();
        return IntPtr.Zero;
    }

    // Scrolling right here would make the list lay itself out in the middle of the change that is still
    // being announced to it, which WPF reports as an inconsistent items source. So do it a bit later.
    void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (scrollQueued)
            return;
        scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            scrollQueued = false;
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    void OnLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    void OnPartitionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (model.MountCommand.CanExecute(null))
            model.MountCommand.Execute(null);
        else if (model.OpenInExplorerCommand.CanExecute(null))
            model.OpenInExplorerCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        model.Dispose();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closeConfirmed || (model.MountedCount == 0 && !model.Busy))
            return;

        // unmounting takes a moment, so stop the close and do it again once that's finished
        e.Cancel = true;
        if (await model.PrepareToCloseAsync())
        {
            closeConfirmed = true;
            Close();
        }
    }
}
