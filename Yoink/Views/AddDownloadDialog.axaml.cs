using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// The "add download" dialog from README roadmap step 4, redesigned around a real, user-reported
/// problem: the old version queued a bare URL immediately and only resolved the video's title/
/// formats later, in the background, once <see cref="DownloadQueueService"/>'s processing loop
/// actually picked the item up — which meant a multi-second yt-dlp round trip (metadata extraction
/// is a real network call, not instant) happened silently, well after the dialog had already
/// closed, with nothing in the UI explaining the pause. This version resolves the video up front,
/// with a visible loading state, and shows the *actual* resolutions/formats yt-dlp reports for that
/// specific video (not a guessed fixed list — the old picker topped out at 1440p with no 4K option
/// at all) before anything is queued. Passing the already-resolved title through to
/// <see cref="DownloadQueueService.EnqueueAsync"/> also means the background loop skips its own
/// redundant metadata fetch once this item is dequeued, so the actual download starts immediately.
/// </summary>
public partial class AddDownloadDialog : Window
{
    private enum Stage { UrlEntry, Loading, Options }

    private DownloadQueueService? _queue;
    private YtDlpClient? _ytDlp;
    private YtDlpVideoInfo? _resolvedInfo;
    private Stage _stage = Stage.UrlEntry;

    public AddDownloadDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog. <paramref name="prefillUrl"/> is used by the clipboard watcher (README
    /// roadmap step 5) to hand over a detected URL for the user to confirm — leave it null for the
    /// ordinary "+ Add download" button, which starts from a blank form. Either way, the user still
    /// has to click "Continue" themselves to actually resolve it — detection and action stay
    /// separate even here (see <c>Views.MainWindow.OnClipboardUrlDetected</c>'s own doc comment).
    /// </summary>
    public static Task ShowAsync(Window owner, DownloadQueueService queue, YtDlpClient ytDlp, string? prefillUrl = null)
    {
        var dialog = new AddDownloadDialog { _queue = queue, _ytDlp = ytDlp };

        if (!string.IsNullOrEmpty(prefillUrl))
        {
            dialog.Title = "Download detected";
            dialog.TitleBar.Title = "Download detected";
            dialog.TxtUrl.Text = prefillUrl;
        }

        return dialog.ShowDialog(owner);
    }

    private void SetStage(Stage stage)
    {
        _stage = stage;
        PanelUrlEntry.IsVisible = stage == Stage.UrlEntry;
        PanelLoading.IsVisible = stage == Stage.Loading;
        PanelOptions.IsVisible = stage == Stage.Options;

        BtnPrimary.Content = stage == Stage.Options ? "Add to queue" : "Continue";
        BtnPrimary.IsEnabled = stage != Stage.Loading;
        BtnCancel.IsEnabled = stage != Stage.Loading;
    }

    private async void BtnPrimary_Click(object? sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Options)
        {
            await AddToQueueAsync();
            return;
        }

        await ResolveVideoAsync();
    }

    private async Task ResolveVideoAsync()
    {
        var url = TxtUrl.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            await MessageBoxWindow.ShowAsync(this, "Please paste a YouTube video URL first.", "Error");
            return;
        }

        SetStage(Stage.Loading);

        try
        {
            _resolvedInfo = await _ytDlp!.GetVideoInfoAsync(url);
        }
        catch (Exception ex)
        {
            SetStage(Stage.UrlEntry);
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Couldn't resolve that video");
            return;
        }

        TxtResolvedTitle.Text = _resolvedInfo.Title;
        PopulateResolutions(_resolvedInfo);
        SetStage(Stage.Options);
    }

    /// <summary>
    /// Every distinct height yt-dlp actually reported a video-capable format for, highest first
    /// (so the best available quality is the default) — replacing the old fixed 360/480/720/1080/
    /// 1440 list, which both guessed at what was actually available for a given video and had no
    /// 2160p/4K option at all. Falls back to that same old fixed list only if yt-dlp's response
    /// genuinely had no usable video formats to read heights from (never observed in practice, but
    /// cheap insurance against showing an empty picker).
    /// </summary>
    private void PopulateResolutions(YtDlpVideoInfo info)
    {
        var heights = info.Formats
            .Where(f => f.HasVideo && f.Height is > 0)
            .Select(f => f.Height!.Value)
            .Distinct()
            .OrderByDescending(h => h)
            .ToList();

        if (heights.Count == 0)
            heights = [1080, 720, 480, 360];

        CboResolution.ItemsSource = heights.Select(h => $"{h}p").ToList();
        CboResolution.SelectedIndex = 0;
    }

    private async Task AddToQueueAsync()
    {
        var url = TxtUrl.Text ?? string.Empty;
        var resolutionText = (string)CboResolution.SelectedItem!;
        var resolution = int.Parse(resolutionText.TrimEnd('p'));
        var containerFormat = ((ComboBoxItem)CboContainer.SelectedItem!).Content!.ToString()!.ToLowerInvariant();

        try
        {
            await _queue!.EnqueueAsync(url, resolution, title: _resolvedInfo!.Title, containerFormat: containerFormat);
            Close();
        }
        catch (Exception ex)
        {
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Error");
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
