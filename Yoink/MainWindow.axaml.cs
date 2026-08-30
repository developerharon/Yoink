using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Yoink;

public partial class MainWindow : Window
{
    private readonly YoutubeClient _youtube = new();
    private readonly ObservableCollection<DownloadHistoryEntry> _history = new(DownloadHistoryService.Load());

    public MainWindow()
    {
        InitializeComponent();

        CboTheme.SelectedIndex = (int)SettingsService.Load().Theme;

        LstHistory.ItemsSource = _history;
        UpdateEmptyHistoryVisibility();
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
        BtnDownload.IsEnabled = false;
        ProgressBar.Value = 0;
        LblPercentage.Text = "0%";

        var url = TxtUrl.Text ?? string.Empty;
        var resolution = int.Parse(((ComboBoxItem)CboResolution.SelectedItem!).Content!.ToString()!);

        try
        {
            var (title, filePath) = await DownloadVideoAsync(url, resolution);
            AddHistoryEntry(new DownloadHistoryEntry
            {
                Title = title,
                Url = url,
                Resolution = resolution,
                FilePath = filePath,
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
            BtnDownload.IsEnabled = true;
        }
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

    private async Task<(string Title, string FilePath)> DownloadVideoAsync(string url, int resolution)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Please paste a YouTube video URL first.");

        var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(url);

        // Prefer an exact match for the requested resolution, otherwise fall back to the
        // closest muxed MP4 stream available (YouTube stopped serving muxed streams above
        // 720p, so 1080/1440 will usually land here).
        IStreamInfo? streamInfo = streamManifest.GetMuxedStreams()
            .Where(s => s.Container == Container.Mp4)
            .OrderBy(s => Math.Abs(s.VideoQuality.MaxHeight - resolution))
            .FirstOrDefault();

        if (streamInfo is null)
            throw new InvalidOperationException("No downloadable MP4 stream was found for this video.");

        var video = await _youtube.Videos.GetAsync(url);
        var fileName = string.Concat(video.Title.Split(Path.GetInvalidFileNameChars())) + ".mp4";
        var filePath = Path.Combine(AppContext.BaseDirectory, fileName);

        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = p * 100;
            LblPercentage.Text = $"{p * 100:0.##}%";
        }));

        await _youtube.Videos.Streams.DownloadAsync(streamInfo, filePath, progress);

        return (video.Title, filePath);
    }
}
