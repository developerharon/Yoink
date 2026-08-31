using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yoink.Views;

/// <summary>
/// A minimal modal dialog used in place of WinForms' MessageBox, which Avalonia does not provide.
/// </summary>
public partial class MessageBoxWindow : Window
{
    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    public static Task ShowAsync(Window owner, string message, string title)
    {
        var window = new MessageBoxWindow { Title = title };
        window.TitleBar.Title = title;
        window.MessageText.Text = message;
        return window.ShowDialog(owner);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
