using System.Windows;
using FreeXplorer.App.ViewModels;
using Microsoft.Win32;

namespace FreeXplorer.App.Views;

public partial class RestoreWindow : Window
{
    readonly RestoreDialogViewModel model;

    public RestoreWindow(RestoreDialogViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick the backup to restore",
            Filter = "Drive backups (*.img;*.bin)|*.img;*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        string? folder = Path.GetDirectoryName(model.BackupPath);
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            model.BackupPath = dialog.FileName;
    }

    void OnRestore(object sender, RoutedEventArgs e) => DialogResult = true;
}
