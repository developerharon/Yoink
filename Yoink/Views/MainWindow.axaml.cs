using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Views;

public partial class MainWindow : Window
{
    private readonly YtDlpClient _ytDlp = new();
    private readonly DownloadQueueService _queue;
    private readonly ObservableCollection<DownloadQueueItem> _items = new();

    // Created once the window is attached to a screen (see MainWindow_Opened) — the clipboard
    // isn't guaranteed to be available any earlier than that.
    private ClipboardWatcherService? _clipboardWatcher;

    public MainWindow()
    {
        InitializeComponent();

        _queue = new DownloadQueueService(_ytDlp);
        _queue.ItemChanged += OnQueueItemChanged;

        var settings = SettingsService.Load();
        CboTheme.SelectedIndex = (int)settings.Theme;
        ChkClipboardWatch.IsChecked = settings.ClipboardWatchEnabled;

        LstQueue.ItemsSource = _items;
        UpdateEmptyQueueVisibility();

        Opened += MainWindow_Opened;

        _ = LoadQueueAsync();
        _ = WarnIfYtDlpMissingAsync();
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _clipboardWatcher = new ClipboardWatcherService(ReadClipboardTextAsync)
        {
            Enabled = SettingsService.Load().ClipboardWatchEnabled
        };
        _clipboardWatcher.UrlDetected += OnClipboardUrlDetected;
    }

    /// <summary>
    /// Avalonia 12's clipboard is data-transfer-shaped rather than a plain get/set-text API:
    /// <c>TryGetDataAsync</c> hands back an <see cref="IAsyncDataTransfer"/> snapshot, from which
    /// <c>TryGetTextAsync</c> (an extension method) pulls the text, if any.
    /// </summary>
    private async Task<string?> ReadClipboardTextAsync()
    {
        if (Clipboard is not { } clipboard)
            return null;

        var data = await clipboard.TryGetDataAsync();
        return data is null ? null : await data.TryGetTextAsync();
    }

    /// <summary>
    /// Called from a background thread by <see cref="ClipboardWatcherService"/> — hands off to the
    /// same add-download dialog the "+ Add download" button uses, pre-filled and awaiting
    /// confirmation, rather than queuing anything on its own.
    /// </summary>
    private void OnClipboardUrlDetected(string url)
    {
        Dispatcher.UIThread.Post(() => _ = AddDownloadDialog.ShowAsync(this, _queue, url));
    }

    private void ChkClipboardWatch_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = ChkClipboardWatch.IsChecked == true;
        if (_clipboardWatcher is not null)
            _clipboardWatcher.Enabled = enabled;

        var settings = SettingsService.Load();
        settings.ClipboardWatchEnabled = enabled;
        SettingsService.Save(settings);
    }

    private async Task LoadQueueAsync()
    {
        var all = await _queue.GetAllAsync();
        foreach (var item in all.OrderByDescending(i => i.CreatedAt))
            _items.Add(item);

        UpdateEmptyQueueVisibility();
    }

    private async Task WarnIfYtDlpMissingAsync()
    {
        if (!await _ytDlp.IsAvailableAsync())
        {
            await MessageBoxWindow.ShowAsync(
                this,
                "yt-dlp wasn't found on PATH, so downloads will fail until it's installed. See the README for setup instructions.",
                "yt-dlp not found");
        }
    }

    private void CboTheme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboTheme.SelectedIndex < 0)
            return;

        var preference = (ThemePreference)CboTheme.SelectedIndex;
        Application.Current!.RequestedThemeVariant = App.ToThemeVariant(preference);

        var settings = SettingsService.Load();
        settings.Theme = preference;
        SettingsService.Save(settings);
    }

    private async void BtnAddDownload_Click(object? sender, RoutedEventArgs e)
    {
        await AddDownloadDialog.ShowAsync(this, _queue);
    }

    private async void BtnPause_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.PauseAsync);
    private async void BtnResume_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.ResumeAsync);
    private async void BtnCancel_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.CancelAsync);
    private async void BtnRetry_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.RetryAsync);

    private static Task RunItemActionAsync(object? sender, Func<long, CancellationToken, Task> action) =>
        sender is Control { DataContext: DownloadQueueItem item } ? action(item.Id, default) : Task.CompletedTask;

    /// <summary>
    /// Opens the OS file manager at the downloaded file, so a completed queue entry is actually
    /// useful once it's scrolled out of view.
    /// </summary>
    private static void BtnShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DownloadQueueItem { FilePath: { } filePath } } || string.IsNullOrWhiteSpace(filePath))
            return;

        var directory = Path.GetDirectoryName(filePath);
        if (directory is null)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — not worth a dialog if the platform lacks a file manager to hand off to.
        }
    }

    /// <summary>
    /// Applies a live change from the queue to the in-memory list the UI is bound to. Every
    /// <see cref="DownloadQueueService.ItemChanged"/> payload is a complete snapshot of that item
    /// (see the comment in <see cref="DownloadQueueService"/>'s UpdateStatusAsync), so replacing
    /// the whole entry is enough — <see cref="DownloadQueueItem"/> doesn't need to implement
    /// INotifyPropertyChanged for the row to refresh.
    /// </summary>
    private void OnQueueItemChanged(DownloadQueueItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var index = -1;
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == item.Id)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
                _items[index] = item;
            else
                _items.Insert(0, item);

            UpdateEmptyQueueVisibility();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _queue.ItemChanged -= OnQueueItemChanged;
        _queue.Dispose();

        if (_clipboardWatcher is not null)
        {
            _clipboardWatcher.UrlDetected -= OnClipboardUrlDetected;
            _clipboardWatcher.Dispose();
        }

        base.OnClosed(e);
    }

    private void UpdateEmptyQueueVisibility()
    {
        TxtEmptyQueue.IsVisible = _items.Count == 0;
    }
}
