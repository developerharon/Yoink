using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MonoTorrent;
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
/// Now handles three kinds of source, not just YouTube (turning Yoink into a general download
/// manager): <see cref="DownloadUrlKind.IsTorrentSource"/> (checked first — a magnet link's own URI
/// scheme wouldn't otherwise be mistaken for anything else, but a <c>.torrent</c> URL's http(s)
/// scheme would fall through to the plain-file check below it) routes to
/// <see cref="ResolveTorrentAsync"/>; <see cref="DownloadUrlKind.IsYouTubeUrl"/> decides between the
/// resolve-video flow (unchanged) and a plain direct-link file resolved via a quick
/// <see cref="DownloadEngine.ProbeAsync"/> (filename/size/content-type). All three land in the same
/// "resolved, now confirm" panel, with <c>PanelVideoOptions</c> (resolution/container) shown only for
/// a video. Either way, the resolved name is passed through to
/// <see cref="DownloadQueueService.EnqueueAsync"/>'s <c>title</c> parameter, so
/// <c>DownloadQueueService</c>'s per-kind processing methods skip their own redundant resolve step
/// the same way the video path already skips its redundant metadata fetch.
///
/// A local <c>.torrent</c> file (picked via <see cref="BtnBrowseTorrentFile_Click"/>, rather than
/// pasted into <see cref="TxtUrl"/> — it has no URL/clipboard-text form) is tracked separately in
/// <see cref="_localTorrentFilePath"/> so it isn't mistaken for a YouTube/generic-file URL by the
/// checks above; <see cref="TxtUrl_TextChanged"/> clears it back to null the moment the user types
/// into <see cref="TxtUrl"/> instead, so whichever input the user touched last is the one that wins
/// rather than both silently fighting over which source actually gets resolved.
///
/// "Save to" (<see cref="TxtDestinationFolder"/>) is shown for every kind, not just video, and lets
/// this one download override the app-wide default folder — same read-only-TextBox + Browse/Reset
/// idiom as <c>Views.SettingsView</c>'s own download-folder row, seeded from whatever Settings
/// currently resolves to so the common case (just use the configured default) needs no interaction.
/// <see cref="_destinationFolderOverride"/> stays null until the user actually browses for something
/// else; null is exactly what <see cref="DownloadQueueService.EnqueueAsync"/>'s own
/// <c>destinationFolder</c> parameter means "no override, use Settings".
/// </summary>
public partial class AddDownloadDialog : Window
{
    private enum Stage { UrlEntry, Loading, Options }

    private DownloadQueueService? _queue;
    private YtDlpClient? _ytDlp;
    private YtDlpVideoInfo? _resolvedInfo;
    private DownloadKind _detectedKind = DownloadKind.Video;
    private Stage _stage = Stage.UrlEntry;
    private string? _localTorrentFilePath;
    private string? _destinationFolderOverride;

    /// <summary>
    /// The exact source string actually resolved — set once, at the top of <see cref="ResolveAsync"/>,
    /// and reused by <see cref="AddToQueueAsync"/> instead of that method re-reading
    /// <see cref="TxtUrl"/>/<see cref="_localTorrentFilePath"/> itself, which could otherwise disagree
    /// with what was actually resolved if either changed in between (most concretely possible for a
    /// local <c>.torrent</c> file: <see cref="TxtUrl"/> never shows that path at all, so there'd be
    /// nothing else to read it back from here).
    /// </summary>
    private string _resolvedSource = string.Empty;

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
        var source = _localTorrentFilePath ?? (TxtUrl.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source))
        {
            await MessageBoxWindow.ShowAsync(this, "Please paste a URL, or browse for a .torrent file, first.", "Error");
            return;
        }

        _resolvedSource = source;
        _detectedKind = _localTorrentFilePath is not null || DownloadUrlKind.IsTorrentSource(source)
            ? DownloadKind.Torrent
            : DownloadUrlKind.IsYouTubeUrl(source) ? DownloadKind.Video : DownloadKind.File;

        // Reset to "no override" at the start of every resolve attempt (including a retry after a
        // failed one) rather than only once at construction — otherwise an override picked before a
        // failed resolve would silently carry over into a completely different resolved item.
        _destinationFolderOverride = null;
        TxtDestinationFolder.Text = DownloadQueueService.ResolveDownloadFolder(SettingsService.Load());

        SetStage(Stage.Loading);

        switch (_detectedKind)
        {
            case DownloadKind.Video:
                await ResolveVideoAsync(source);
                break;
            case DownloadKind.Torrent:
                await ResolveTorrentAsync(source);
                break;
            default:
                await ResolveFileAsync(source);
                break;
        }
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
    /// Resolves a torrent source. A magnet link's real name/size isn't known until its metadata
    /// actually resolves — a swarm round trip, not something worth blocking this dialog on the way
    /// the other two kinds' resolve steps are (a quick HTTP call either way) — so this deliberately
    /// does <i>not</i> try to connect to anything for one: <see cref="TorrentEngine.TryGetMagnetDisplayName"/>
    /// (the magnet URI's own <c>dn=</c> parameter, if it has one) is the best available name up front,
    /// and the caption below says plainly that the rest resolves once the download actually starts
    /// (see <c>DownloadQueueService.ProcessTorrentItemAsync</c> for where that real resolve happens).
    /// A local/remote <c>.torrent</c> file, by contrast, already has its full contents on hand (or one
    /// quick download away) with no swarm involved, so it resolves fully here, same as the file path
    /// above.
    /// </summary>
    private async Task ResolveTorrentAsync(string source)
    {
        if (DownloadUrlKind.IsMagnetLink(source))
        {
            TxtResolvedTitle.Text = TorrentEngine.TryGetMagnetDisplayName(source) ?? "Magnet link";
            TxtFileMeta.Text = "Peer count and file size resolve once the download starts.";
            TxtFileMeta.IsVisible = true;
            PanelVideoOptions.IsVisible = false;
            SetStage(Stage.Options);
            return;
        }

        Torrent torrent;
        try
        {
            torrent = await TorrentEngine.LoadTorrentAsync(source);
        }
        catch (Exception ex)
        {
            SetStage(Stage.UrlEntry);
            await MessageBoxWindow.ShowAsync(this, ex.Message, "Couldn't resolve that torrent");
            return;
        }

        TxtResolvedTitle.Text = torrent.Name;
        TxtFileMeta.Text = $"{DownloadQueueItem.FormatBytes(torrent.Size)}  •  {torrent.Files.Count} file{(torrent.Files.Count == 1 ? "" : "s")}";
        TxtFileMeta.IsVisible = true;
        PanelVideoOptions.IsVisible = false;
        SetStage(Stage.Options);
    }

    /// <summary>Clears a previously browsed-for local .torrent file the moment the user types here instead — see the class doc comment.</summary>
    private void TxtUrl_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_localTorrentFilePath is not null)
        {
            _localTorrentFilePath = null;
            TxtSelectedTorrentFile.IsVisible = false;
        }
    }

    /// <summary>
    /// The "Browse" half of torrent input — a local .torrent file has no clipboard-text/URL form, so
    /// unlike everything else this dialog resolves, it can't just be pasted into <see cref="TxtUrl"/>.
    /// Same <see cref="IStorageProvider"/> file-picker mechanism <c>Views.SettingsView</c>'s own
    /// folder Browse button already uses.
    /// </summary>
    private async void BtnBrowseTorrentFile_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
            return;

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a .torrent file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Torrent files") { Patterns = ["*.torrent"] }]
        });

        var path = result.Count > 0 ? result[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(path))
            return;

        _localTorrentFilePath = path;
        TxtSelectedTorrentFile.Text = $"Selected: {System.IO.Path.GetFileName(path)}";
        TxtSelectedTorrentFile.IsVisible = true;
    }

    /// <summary>
    /// Lets this one download override the app-wide default folder — same
    /// <see cref="IStorageProvider"/> folder-picker mechanism <c>Views.SettingsView</c>'s own download
    /// folder Browse button already uses, but writes to <see cref="_destinationFolderOverride"/>
    /// instead of persisting to <c>AppSettings</c>: this choice is for this download only.
    /// </summary>
    private async void BtnBrowseDestinationFolder_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
            return;

        var startLocation = await TryGetStartFolderAsync(storageProvider, TxtDestinationFolder.Text);

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for this download",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation
        });

        var folder = result.Count > 0 ? result[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(folder))
            return;

        _destinationFolderOverride = folder;
        TxtDestinationFolder.Text = folder;
    }

    private void BtnResetDestinationFolder_Click(object? sender, RoutedEventArgs e)
    {
        _destinationFolderOverride = null;
        TxtDestinationFolder.Text = DownloadQueueService.ResolveDownloadFolder(SettingsService.Load());
    }

    /// <summary>Best-effort: an unset/no-longer-existing path just opens the picker at its own platform-chosen default rather than failing the whole click — same as Views.SettingsView's own copy of this.</summary>
    private static async Task<IStorageFolder?> TryGetStartFolderAsync(IStorageProvider storageProvider, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return await storageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
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
        var source = _resolvedSource;

        try
        {
            switch (_detectedKind)
            {
                case DownloadKind.Video:
                {
                    var resolutionText = (string)CboResolution.SelectedItem!;
                    var resolution = int.Parse(resolutionText.TrimEnd('p'));
                    var containerFormat = ((ComboBoxItem)CboContainer.SelectedItem!).Content!.ToString()!.ToLowerInvariant();

                    await _queue!.EnqueueAsync(source, resolution, title: _resolvedInfo!.Title, containerFormat: containerFormat, kind: DownloadKind.Video, infoJson: _resolvedInfo.RawJson, destinationFolder: _destinationFolderOverride);
                    break;
                }
                case DownloadKind.Torrent:
                {
                    // Whatever TxtResolvedTitle ended up showing — the torrent's real name for a
                    // resolved .torrent file, or the magnet's own dn= (or "Magnet link", if it had
                    // none) — same "pass through whatever was already resolved" reasoning as the
                    // other two kinds. A placeholder title here is harmless even for a magnet link:
                    // DownloadQueueService.ProcessTorrentItemAsync's own progress reporting
                    // overwrites it with the real resolved name the moment metadata arrives.
                    var title = TxtResolvedTitle.Text ?? "Torrent";
                    await _queue!.EnqueueAsync(source, resolution: 0, title: title, kind: DownloadKind.Torrent, destinationFolder: _destinationFolderOverride);
                    break;
                }
                default:
                {
                    var fileName = TxtResolvedTitle.Text ?? DownloadEngine.GetFileNameFromUri(new Uri(source));
                    await _queue!.EnqueueAsync(source, resolution: 0, title: fileName, kind: DownloadKind.File, destinationFolder: _destinationFolderOverride);
                    break;
                }
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
