using System;

namespace Yoink.Models;

/// <summary>
/// Where a queued download currently stands. <see cref="Completed"/>, <see cref="Failed"/> and
/// <see cref="Canceled"/> are terminal; <see cref="Paused"/> is not — a paused item goes back to
/// <see cref="Pending"/> (and eventually gets picked up again) via
/// <see cref="Services.DownloadQueueService.ResumeAsync"/>.
/// </summary>
public enum DownloadQueueStatus
{
    Pending,
    Active,
    Paused,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// What a queued item actually is, and therefore which downloader
/// <see cref="Services.DownloadQueueService.ProcessItemAsync"/> hands it to. <see cref="Video"/>
/// (yt-dlp) is the original, sole behavior — every pre-existing row defaults to it via the same
/// <c>EnsureColumnExists</c> migration pattern <c>ContainerFormat</c> already used. <see cref="File"/>
/// is a plain direct-link download (PDF, .exe, .deb, ...) via <see cref="Services.DownloadEngine"/> —
/// <see cref="Resolution"/>/<see cref="ContainerFormat"/> don't mean anything for one of these; see
/// each property's own doc comment for what a File-kind row uses instead.
/// </summary>
public enum DownloadKind
{
    Video,
    File
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

    public string DisplayTitle => string.IsNullOrEmpty(Title) ? Url : Title;

    public string StatusText => Status.ToString();

    /// <summary>"1080p" for a video row, "File" for a generic one — <see cref="Resolution"/> has no meaning there.</summary>
    private string KindLabel => Kind == DownloadKind.Video ? $"{Resolution}p" : "File";

    public string Subtitle => Status == DownloadQueueStatus.Failed && !string.IsNullOrEmpty(ErrorMessage)
        ? $"{KindLabel}  •  {ErrorMessage}"
        : $"{KindLabel}  •  {CreatedAt.ToLocalTime():MMM d, yyyy • h:mm tt}";

    /// <summary>0-100, for direct binding to a <c>ProgressBar</c> without a converter.</summary>
    public double ProgressPercent => Progress * 100;

    public bool ShowProgress => Status is DownloadQueueStatus.Active or DownloadQueueStatus.Paused;

    /// <summary>Only once yt-dlp has actually reported a size — see <see cref="TotalBytes"/>'s doc comment.</summary>
    public bool ShowSize => ShowProgress && TotalBytes is > 0;

    public string SizeText => $"{FormatBytes(DownloadedBytes ?? 0)} / {FormatBytes(TotalBytes ?? 0)}";

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
    public bool CanRetry => Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled;
    public bool CanShowInFolder => Status == DownloadQueueStatus.Completed;

    /// <summary>
    /// Whether this row can be removed from the list (optionally along with its downloaded file —
    /// see <see cref="Services.DownloadQueueService.DeleteAsync"/>). Only terminal states — the
    /// opposite of <see cref="CanCancel"/> — so a still-pending/active/paused item has to be
    /// canceled first rather than deleted out from under an in-flight yt-dlp process.
    /// </summary>
    public bool CanDelete => !CanCancel;
}
