using System.Windows;
using System.Windows.Threading;
using FreeXplorer.App.Services;
using FreeXplorer.App.ViewModels;

namespace FreeXplorer.App;

public partial class App : Application
{
    // The tests create an App of their own for its styles and don't want the real window with a drive scan behind it
    public static bool Quiet { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Quiet)
            return;
        DispatcherUnhandledException += OnUnhandled;
        Theme.Start();

        MainWindow? window = null;
        var dialogs = new WpfDialogs(() => window);
        var model = new MainViewModel(new WindowsPlatform(), dialogs);
        window = new MainWindow(model);
        MainWindow = window;
        window.Show();

        // a backup file dropped on the exe or passed on the command line loads straight away
        foreach (var arg in e.Args.Where(File.Exists))
            _ = model.LoadBackupAsync(arg);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong that the app didn't expect:\n\n{e.Exception.Message}",
            "freeXplorer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
