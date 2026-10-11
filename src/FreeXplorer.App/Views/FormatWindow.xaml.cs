using System.Windows;
using FreeXplorer.App.ViewModels;

namespace FreeXplorer.App.Views;

public partial class FormatWindow : Window
{
    public FormatWindow(FormatDialogViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) => ConfirmBox.Focus();
    }

    void OnFormat(object sender, RoutedEventArgs e) => DialogResult = true;
}
