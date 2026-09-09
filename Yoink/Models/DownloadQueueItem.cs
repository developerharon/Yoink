using System;

namespace Yoink.Models;

/// <summary>
/// Where a queued download currently stands. <see cref="Completed"/>, <see cref="Failed"/>,
/// <see cref="Canceled"/> and <see cref="Missing"/> are terminal; <see cref="Paused"/> is not — a
/// paused item goes back to <see cref="Pending"/> (and eventually gets picked up again) via
/// <see cref="Services.DownloadQueueService.ResumeAsync"/>.
/// </summary>
public enum DownloadQueueStatus
{
    Pending,
    Active,
    Paused,
    Completed,
    Failed,
    Canceled,

    /// <summary>
    /// A one-way transition from <see cref="Completed"/>: <see cref="Services.DownloadQueueService"/>'s
    /// background loop periodically checks every completed row's <see cref="DownloadQueueItem.FilePath"/>
    /// still exists, and moves it here the moment it doesn't (deleted, or moved elsewhere on disk —
    /// same observable effect either way, since this app has no way to tell the two apart). Behaves
    /// like <see cref="Failed"/>/<see cref="Canceled"/> for retry purposes (see
    /// <see cref="DownloadQueueItem.CanRetry"/>) — re-downloading to the exact same
    /// <see cref="DownloadQueueItem.FilePath"/> if the source URL is still valid — but is a distinct
    /// status rather than reusing <see cref="Failed"/> so the queue view can tell "this download
    /// itself never worked" apart from "this one worked, and something happened to the file
    /// afterwards" (see <c>Converters.DownloadQueueStatusToTextDecorationsConverter</c>'s strikethrough
    /// and this app's <c>WarningBrush</c>, rather than <c>ErrorBrush</c>, in the queue view).
    /// </summary>
    Missing
}

/// <summary>
/// What a queued item actually is, and therefore which downloader
/// <see cref="Services.DownloadQueueService.ProcessItemAsync"/> hands it to. <see cref="Video"/>
/// (yt-dlp) is the original, sole behavior — every pre-existing row defaults to it via the same
/// <c>EnsureColumnExists</c> migration pattern <c>ContainerFormat</c> already used. <see cref="File"/>
/// is a plain direct-link download (PDF, .exe, .deb, ...) via <see cref="Services.DownloadEngine"/> —
/// <see cref="Resolution"/>/<see cref="ContainerFormat"/> don't mean anything for one of these; see
/// each property's own doc comment for what a File-kind row uses instead. <see cref="Torrent"/> is a
/// peer-to-peer download via <see cref="Services.TorrentEngine"/> — <see cref="Url"/> holds either a
/// <c>magnet:</c> link, a direct URL to a <c>.torrent</c> file, or a local filesystem path to one
/// (see <c>Views.AddDownloadDialog</c>'s "Browse" option); see <see cref="SeederCount"/>/
/// <see cref="LeecherCount"/>/<see cref="PhaseText"/> for what's specific to this kind.
/// </summary>
public enum DownloadKind
{
    Video,
    File,
    Torrent
}

/// <summary>
/// One row in the persisted download queue (README roadmap step 3). Plain data — no
/// <c>INotifyPropertyChanged</c> ceremony; <see cref="Services.DownloadQueueService"/> owns
/// reading/writing it, and the queue view (<c>Views.MainWindow</c>) reflects a change by replacing
/// the whole item in its list rather than mutating one already bound in the UI, so this can stay a
/// plain data class. The presentational properties below exist so the queue view's DataTemplate
/// can bind directly without converters, the same pattern the old (now-removed, folded into this
/// queue) <c>DownloadHistoryEntry</c> used.
/// </summary>
public sealed class DownloadQueueItem
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The video's title for a <see cref="DownloadKind.Video"/> row; the resolved (or URL-derived)
    /// filename — e.g. "Yoink.AppImage" — for a <see cref="DownloadKind.File"/> one. Doubles as
    /// "already resolved, don't fetch it again" for both kinds, the same way it always has for
    /// video — see <see cref="Services.DownloadQueueService.EnqueueAsync"/>'s own doc comment.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    public DownloadKind Kind { get; set; } = DownloadKind.Video;

    /// <summary>Meaningless for <see cref="DownloadKind.File"/> — always 0 on those rows.</summary>
    public int Resolution { get; set; }

    /// <summary>
    /// The muxed container yt-dlp's own `--merge-output-format` produces — "mp4" or "mkv", chosen
    /// in <c>Views.AddDownloadDialog</c> alongside resolution. Defaults to "mp4" for any row
    /// created before this existed (via <c>Services.DownloadQueueService</c>'s column-add
    /// migration), matching this app's previous hardcoded behavior exactly. Meaningless for
    /// <see cref="DownloadKind.File"/> — a generic file keeps whatever extension its own resolved
    /// filename (<see cref="Title"/>) already has, rather than one being forced onto it.
    /// </summary>
    public string ContainerFormat { get; set; } = "mp4";

    public string? FilePath { get; set; }

    /// <summary>
    /// The raw yt-dlp JSON (<see cref="Services.YtDlpVideoInfo.RawJson"/>) <c>Views.AddDownloadDialog</c>
    /// already resolved for this URL before enqueueing it, carried through so
    /// <see cref="Services.DownloadQueueService.ProcessVideoItemAsync"/> can hand it straight to
    /// <see cref="Services.YtDlpClient.DownloadAsync"/>'s own <c>infoJson</c> parameter instead of
    /// making yt-dlp re-extract the same video from scratch — see that parameter's doc comment.
    /// Persisted (unlike <see cref="DownloadedBytes"/>/<see cref="TotalBytes"/> above) since a fresh
    /// row can sit <see cref="DownloadQueueStatus.Pending"/> across an app restart before it's ever
    /// processed, but genuinely single-use: <c>ProcessVideoItemAsync</c> clears it back to null in
    /// memory the moment it reads it (whether it actually used it or decided it was too stale — see
    /// that method), and the next <c>PersistAsync</c> call writes that null back out, so this never
    /// sits around bloating a queue that's deliberately never pruned.
    /// </summary>
    public string? InfoJson { get; set; }

    public DownloadQueueStatus Status { get; set; }
    public double Progress { get; set; }
    public string? ErrorMessage { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How far into the current download this is, in bytes, and the best-known total so far — see
    /// <see cref="Services.YtDlpDownloadProgress"/> for exactly how these are derived (including why
    /// the total can grow partway through a video+audio download rather than being known upfront).
    /// Not persisted to <c>queue.db</c> — like <see cref="Progress"/>'s per-tick updates, these are
    /// only ever meaningful while this item is actually <see cref="DownloadQueueStatus.Active"/>, so
    /// there's nothing worth writing to disk for a row that isn't (see
    /// <c>Services.DownloadQueueService.PersistAsync</c>, which only ever runs at a status
    /// transition, never on a progress tick).
    /// </summary>
    public long? DownloadedBytes { get; set; }

    public long? TotalBytes { get; set; }

    /// <summary>
    /// How many seeders/leechers <see cref="Services.TorrentEngine"/> currently sees for this
    /// torrent — <see cref="Services.DownloadQueueService.ProcessTorrentItemAsync"/>'s progress
    /// callback copies these onto the item on every tick, same "live-only, never persisted" pattern
    /// as <see cref="DownloadedBytes"/>/<see cref="TotalBytes"/> above (meaningless once the item
    /// isn't actually <see cref="DownloadQueueStatus.Active"/>). Null for a non-<see cref="DownloadKind.Torrent"/>
    /// row, or a torrent row that hasn't started reporting peer counts yet (still resolving metadata).
    /// </summary>
    public int? SeederCount { get; set; }

    public int? LeecherCount { get; set; }

    /// <summary>
    /// A short human-readable torrent phase — "Fetching metadata…", "Checking existing files…" —
    /// shown in place of the size readout while a <see cref="DownloadKind.Torrent"/> row is in one of
    /// those phases (see <c>Views.MainWindow</c>'s queue row template). Null once real piece
    /// downloading is underway, so <see cref="SizeText"/> takes over instead — same live-only,
    /// never-persisted reasoning as <see cref="SeederCount"/> above.
    /// </summary>
    public string? PhaseText { get; set; }

    public string DisplayTitle => string.IsNullOrEmpty(Title) ? Url : Title;

    public string StatusText => Status.ToString();

    /// <summary>"1080p" for a video row, "File"/"Torrent" for the other kinds — <see cref="Resolution"/> has no meaning for those.</summary>
    private string KindLabel => Kind switch
    {
        DownloadKind.Video => $"{Resolution}p",
        DownloadKind.Torrent => "Torrent",
        _ => "File"
    };

    public string Subtitle => Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Missing && !string.IsNullOrEmpty(ErrorMessage)
        ? $"{KindLabel}  •  {ErrorMessage}"
        : $"{KindLabel}  •  {CreatedAt.ToLocalTime():MMM d, yyyy • h:mm tt}";

    /// <summary>0-100, for direct binding to a <c>ProgressBar</c> without a converter.</summary>
    public double ProgressPercent => Progress * 100;

    public bool ShowProgress => Status is DownloadQueueStatus.Active or DownloadQueueStatus.Paused;

    /// <summary>Only once yt-dlp/the torrent engine has actually reported a size — see <see cref="TotalBytes"/>'s doc comment.</summary>
    public bool ShowSize => ShowProgress && TotalBytes is > 0 && string.IsNullOrEmpty(PhaseText);

    public string SizeText => $"{FormatBytes(DownloadedBytes ?? 0)} / {FormatBytes(TotalBytes ?? 0)}";

    /// <summary>Shown instead of <see cref="SizeText"/> while <see cref="PhaseText"/> is set — see that property's own doc comment.</summary>
    public bool ShowPhase => ShowProgress && !string.IsNullOrEmpty(PhaseText);

    /// <summary>
    /// Only for a <see cref="DownloadKind.Torrent"/> row, and only once <see cref="Services.TorrentEngine"/>
    /// has reported at least one peer-count tick (both fields are set together — see that class).
    /// Excludes <see cref="PhaseText"/> the same way <see cref="ShowSize"/> does — both bind to the
    /// same spot in the queue row (see <c>Views.MainWindow</c>'s row template), and a torrent can
    /// have both a phase and a peer count at once (peers are visible during hash-checking, say), so
    /// without this a row could show both texts overlapping in the same place.
    /// </summary>
    public bool ShowPeers => ShowProgress && Kind == DownloadKind.Torrent && SeederCount.HasValue && string.IsNullOrEmpty(PhaseText);

    public string PeersText => $"{SeederCount ?? 0} seeders  •  {LeecherCount ?? 0} peers";

    /// <summary>
    /// Binary (1024-based) units, matching what these bytes were actually computed from — yt-dlp's
    /// own KiB/MiB/GiB progress output — but labeled the more familiar KB/MB/GB rather than the
    /// pedantically-correct KiB/MiB/GiB, matching how most end-user apps display file sizes. Internal
    /// (not private) so <c>Views.AddDownloadDialog</c> can reuse it for a generic file's size caption
    /// rather than reimplementing the same formatting.
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }

    public bool CanPause => Status == DownloadQueueStatus.Active;
    public bool CanResume => Status == DownloadQueueStatus.Paused;
    public bool CanCancel => Status is DownloadQueueStatus.Pending or DownloadQueueStatus.Active or DownloadQueueStatus.Paused;
    public bool CanRetry => Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled or DownloadQueueStatus.Missing;
    public bool CanShowInFolder => Status == DownloadQueueStatus.Completed;

    /// <summary>
    /// Whether this row can be removed from the list (optionally along with its downloaded file —
    /// see <see cref="Services.DownloadQueueService.DeleteAsync"/>). Only terminal states — the
    /// opposite of <see cref="CanCancel"/> — so a still-pending/active/paused item has to be
    /// canceled first rather than deleted out from under an in-flight yt-dlp process.
    /// </summary>
    public bool CanDelete => !CanCancel;
}
