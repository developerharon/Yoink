using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yoink.Models;
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
///
/// Now handles two kinds of URL, not just YouTube (turning Yoink into a general download manager):
/// <see cref="DownloadUrlKind.IsYouTubeUrl"/> decides which — a YouTube URL goes through the
/// resolve-video flow above unchanged, anything else is treated as a plain direct-link file and
/// resolved instead via a quick <see cref="DownloadEngine.ProbeAsync"/> (filename/size/content-type),
/// shown in the same "resolved, now confirm" panel with the video-only resolution/container pickers
/// hidden. Either way, the resolved name is passed through to <see cref="DownloadQueueService.EnqueueAsync"/>'s
/// <c>title</c> parameter, so <c>DownloadQueueService.ProcessFileItemAsync</c> skips its own
/// redundant probe the same way the video path already skipped its redundant metadata fetch.
/// </summary>
public partial class AddDownloadDialog : Window
{
    private enum Stage { UrlEntry, Loading, Options }

    private DownloadQueueService? _queue;
    private YtDlpClient? _ytDlp;
    private YtDlpVideoInfo? _resolvedInfo;
    private DownloadKind _detectedKind = DownloadKind.Video;
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

        await ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        var url = TxtUrl.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            await MessageBoxWindow.ShowAsync(this, "Please paste a URL first.", "Error");
            return;
        }

        _detectedKind = DownloadUrlKind.IsYouTubeUrl(url) ? DownloadKind.Video : DownloadKind.File;
        SetStage(Stage.Loading);

        if (_detectedKind == DownloadKind.Video)
            await ResolveVideoAsync(url);
        else
            await ResolveFileAsync(url);
    }

    private async Task ResolveVideoAsync(string url)
    {
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
        TxtFileMeta.IsVisible = false;
        PanelVideoOptions.IsVisible = true;
        PopulateResolutions(_resolvedInfo);
        SetStage(Stage.Options);
    }

    /// <summary>
    /// Resolves a plain direct-link file via a one-off <see cref="DownloadEngine"/> instance — just
    /// for this probe; the actual download later goes through <see cref="DownloadQueueService"/>'s
    /// own shared engine once the item is dequeued, same separation the video path already has
    /// between resolving here (via <see cref="_ytDlp"/>) and the real download happening elsewhere.
    /// </summary>
    private async Task ResolveFileAsync(string url)
    {
        DownloadProbe probe;
        try
        {
            using var engine = new DownloadEngine();
            probe = await engine.ProbeAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            SetStage(Stage.UrlEntry);
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Couldn't resolve that file");
            return;
        }

        var fileName = probe.FileName ?? DownloadEngine.GetFileNameFromUri(new Uri(url));
        TxtResolvedTitle.Text = fileName;
        TxtFileMeta.Text = BuildFileMetaText(probe);
        TxtFileMeta.IsVisible = true;
        PanelVideoOptions.IsVisible = false;
        SetStage(Stage.Options);
    }

    private static string BuildFileMetaText(DownloadProbe probe)
    {
        var parts = new List<string>();
        if (probe.TotalBytes is > 0)
            parts.Add(DownloadQueueItem.FormatBytes(probe.TotalBytes.Value));
        if (!string.IsNullOrEmpty(probe.ContentType))
            parts.Add(probe.ContentType);

        return parts.Count > 0 ? string.Join("  •  ", parts) : "Size unknown";
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

        try
        {
            if (_detectedKind == DownloadKind.Video)
            {
                var resolutionText = (string)CboResolution.SelectedItem!;
                var resolution = int.Parse(resolutionText.TrimEnd('p'));
                var containerFormat = ((ComboBoxItem)CboContainer.SelectedItem!).Content!.ToString()!.ToLowerInvariant();

                await _queue!.EnqueueAsync(url, resolution, title: _resolvedInfo!.Title, containerFormat: containerFormat, kind: DownloadKind.Video);
            }
            else
            {
                var fileName = TxtResolvedTitle.Text ?? DownloadEngine.GetFileNameFromUri(new Uri(url));
                await _queue!.EnqueueAsync(url, resolution: 0, title: fileName, kind: DownloadKind.File);
            }

            Close();
        }
        catch (Exception ex)
        {
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Error");
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
