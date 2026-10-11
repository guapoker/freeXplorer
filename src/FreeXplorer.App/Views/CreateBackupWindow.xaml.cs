using System.Windows;
using FreeXplorer.App.ViewModels;
using Microsoft.Win32;

namespace FreeXplorer.App.Views;

public partial class CreateBackupWindow : Window
{
    readonly BackupDialogViewModel model;

    public CreateBackupWindow(BackupDialogViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.CaretIndex = PathBox.Text.Length;
        };
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Where should the backup go?",
            Filter = "Drive backup (*.img)|*.img|All files (*.*)|*.*",
            DefaultExt = ".img",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = Path.GetFileName(model.FilePath),
        };
        string? folder = Path.GetDirectoryName(model.FilePath);
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            model.FilePath = dialog.FileName;
    }

    void OnCreate(object sender, RoutedEventArgs e) => DialogResult = true;
}
