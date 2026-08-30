using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// The "add download" dialog from README roadmap step 4: URL + resolution picker, enqueues
/// straight into <see cref="DownloadQueueService"/> on submit. The queue view in
/// <see cref="MainWindow"/> picks the new item up on its own via
/// <see cref="DownloadQueueService.ItemChanged"/> — this dialog doesn't need to hand anything back.
/// </summary>
public partial class AddDownloadDialog : Window
{
    private DownloadQueueService? _queue;

    public AddDownloadDialog()
    {
        InitializeComponent();
    }

    public static Task ShowAsync(Window owner, DownloadQueueService queue)
    {
        var dialog = new AddDownloadDialog { _queue = queue };
        return dialog.ShowDialog(owner);
    }

    private async void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var url = TxtUrl.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            await MessageBoxWindow.ShowAsync(this, "Please paste a YouTube video URL first.", "Error");
            return;
        }

        var resolution = int.Parse(((ComboBoxItem)CboResolution.SelectedItem!).Content!.ToString()!);

        try
        {
            await _queue!.EnqueueAsync(url, resolution);
            Close();
        }
        catch (Exception ex)
        {
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Error");
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
