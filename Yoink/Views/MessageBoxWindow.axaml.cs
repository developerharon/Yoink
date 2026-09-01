using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yoink.Views;

/// <summary>
/// A minimal modal dialog used in place of WinForms' MessageBox, which Avalonia does not provide.
/// </summary>
public partial class MessageBoxWindow : Window
{
    private bool _confirmed;

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

    /// <summary>
    /// Same modal shell as <see cref="ShowAsync"/>, but with a Cancel button alongside a
    /// caller-labeled confirm button — for a destructive, hard-to-reverse action (currently just
    /// "delete file and remove" in <c>Views.MainWindow</c>) that needs an explicit yes/no rather
    /// than a bare acknowledgment. Returns true only if <paramref name="confirmText"/>'s button was
    /// actually clicked — closing the window any other way (Cancel, the titlebar's own close button)
    /// reads as "no", via <see cref="_confirmed"/>'s default.
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(Window owner, string message, string title, string confirmText)
    {
        var window = new MessageBoxWindow { Title = title };
        window.TitleBar.Title = title;
        window.MessageText.Text = message;
        window.OkButton.Content = confirmText;
        window.CancelButton.IsVisible = true;

        await window.ShowDialog(owner);
        return window._confirmed;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
