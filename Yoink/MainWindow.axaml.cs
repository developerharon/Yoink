using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Yoink;

public partial class MainWindow : Window
{
    private readonly YoutubeClient _youtube = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BtnDownload_Click(object? sender, RoutedEventArgs e)
    {
        BtnDownload.IsEnabled = false;
        ProgressBar.Value = 0;
        LblPercentage.Text = "0%";

        try
        {
            var resolution = int.Parse(((ComboBoxItem)CboResolution.SelectedItem!).Content!.ToString()!);
            await DownloadVideoAsync(TxtUrl.Text ?? string.Empty, resolution);
            await MessageBoxWindow.ShowAsync(this, "Your download completed successfully.", "Download complete");
        }
        catch (Exception ex)
        {
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Error");
        }
        finally
        {
            BtnDownload.IsEnabled = true;
        }
    }

    private async Task DownloadVideoAsync(string url, int resolution)
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
    }
}
