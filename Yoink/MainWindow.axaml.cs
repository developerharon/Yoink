using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Yoink;

public partial class MainWindow : Window
{
    private readonly YtDlpClient _ytDlp = new();
    private readonly DownloadQueueService _queue;
    private readonly ObservableCollection<DownloadHistoryEntry> _history = new(DownloadHistoryService.Load());

    // Which queue item the progress bar/label are currently tracking. The queue can hold more
    // than one pending item (its API already supports that), but this window's UI only ever
    // shows one download at a time — a real queue view is a later roadmap step.
    private long? _trackedItemId;

    public MainWindow()
    {
        InitializeComponent();

        _queue = new DownloadQueueService(_ytDlp);
        _queue.ItemChanged += OnQueueItemChanged;

        CboTheme.SelectedIndex = (int)SettingsService.Load().Theme;

        LstHistory.ItemsSource = _history;
        UpdateEmptyHistoryVisibility();

        _ = WarnIfYtDlpMissingAsync();
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

    private async void BtnDownload_Click(object? sender, RoutedEventArgs e)
    {
        var url = TxtUrl.Text ?? string.Empty;
        var resolution = int.Parse(((ComboBoxItem)CboResolution.SelectedItem!).Content!.ToString()!);

        if (string.IsNullOrWhiteSpace(url))
        {
            await MessageBoxWindow.ShowAsync(this, "Please paste a YouTube video URL first.", "Error");
            return;
        }

        BtnDownload.IsEnabled = false;
        ProgressBar.Value = 0;
        LblPercentage.Text = "0%";

        try
        {
            // Enqueue first so _trackedItemId is set before the download starts, otherwise the
            // progress events fired while it runs would have nowhere to be attributed to.
            var queued = await _queue.EnqueueAsync(url, resolution);
            _trackedItemId = queued.Id;

            var completed = await _queue.WaitForCompletionAsync(queued.Id);
            AddHistoryEntry(new DownloadHistoryEntry
            {
                Title = completed.Title,
                Url = url,
                Resolution = resolution,
                FilePath = completed.FilePath!,
                DownloadedAt = DateTimeOffset.Now,
                Status = DownloadStatus.Completed
            });
            await MessageBoxWindow.ShowAsync(this, "Your download completed successfully.", "Download complete");
        }
        catch (Exception ex)
        {
            AddHistoryEntry(new DownloadHistoryEntry
            {
                Title = string.IsNullOrWhiteSpace(url) ? "Unknown video" : url,
                Url = url,
                Resolution = resolution,
                DownloadedAt = DateTimeOffset.Now,
                Status = DownloadStatus.Failed,
                ErrorMessage = ex.Message
            });
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Error");
        }
        finally
        {
            _trackedItemId = null;
            BtnDownload.IsEnabled = true;
        }
    }

    private void OnQueueItemChanged(DownloadQueueItem item)
    {
        if (item.Id != _trackedItemId)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = item.Progress * 100;
            LblPercentage.Text = $"{item.Progress * 100:0.##}%";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _queue.ItemChanged -= OnQueueItemChanged;
        _queue.Dispose();
        base.OnClosed(e);
    }

    private void AddHistoryEntry(DownloadHistoryEntry entry)
    {
        _history.Insert(0, entry);
        DownloadHistoryService.Save(_history);
        UpdateEmptyHistoryVisibility();
    }

    private void UpdateEmptyHistoryVisibility()
    {
        TxtEmptyHistory.IsVisible = _history.Count == 0;
    }

    /// <summary>
    /// Opens the OS file manager at the downloaded file, so "Recent downloads" is actually useful
    /// once a file has scrolled out of view in the app's own download directory.
    /// </summary>
    private static void ShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filePath } || string.IsNullOrWhiteSpace(filePath))
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
}
