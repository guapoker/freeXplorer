using System.Windows;

namespace FreeXplorer.App.Views;

public partial class TextWindow : Window
{
    public TextWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        Body.Text = text;
    }
}
