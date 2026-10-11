using System.Windows;
using FreeXplorer.App.ViewModels;
using FreeXplorer.App.Views;
using Microsoft.Win32;

namespace FreeXplorer.App.Services;

public sealed class WpfDialogs : IDialogs
{
    readonly Func<Window?> owner;

    public WpfDialogs(Func<Window?> owner) => this.owner = owner;

    public string? PickBackupToLoad()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load a backup",
            Filter = "Drive backups (*.img;*.bin;*.iso)|*.img;*.bin;*.iso|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public BackupRequest? AskCreateBackup(BackupDialogInfo info)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var model = new BackupDialogViewModel(info, folder);
        var window = new CreateBackupWindow(model) { Owner = owner() };
        return window.ShowDialog() == true ? model.BuildRequest() : null;
    }

    public RestoreRequest? AskRestore(RestoreDialogInfo info)
    {
        var model = new RestoreDialogViewModel(info);
        var window = new RestoreWindow(model) { Owner = owner() };
        return window.ShowDialog() == true ? model.BuildRequest() : null;
    }

    public FormatRequest? AskFormat(FormatDialogInfo info)
    {
        var model = new FormatDialogViewModel(info);
        var window = new FormatWindow(model) { Owner = owner() };
        return window.ShowDialog() == true ? model.BuildRequest() : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(owner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowText(string title, string text) =>
        new TextWindow(title, text) { Owner = owner() }.ShowDialog();

    public void ShowError(string message) =>
        MessageBox.Show(owner(), message, "freeXplorer", MessageBoxButton.OK, MessageBoxImage.Error);
}
