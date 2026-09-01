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
    public string Title { get; set; } = string.Empty;
    public int Resolution { get; set; }

    /// <summary>
    /// The muxed container yt-dlp's own `--merge-output-format` produces — "mp4" or "mkv", chosen
    /// in <c>Views.AddDownloadDialog</c> alongside resolution. Defaults to "mp4" for any row
    /// created before this existed (via <c>Services.DownloadQueueService</c>'s column-add
    /// migration), matching this app's previous hardcoded behavior exactly.
    /// </summary>
    public string ContainerFormat { get; set; } = "mp4";

    public string? FilePath { get; set; }
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

    public string Subtitle => Status == DownloadQueueStatus.Failed && !string.IsNullOrEmpty(ErrorMessage)
        ? $"{Resolution}p  •  {ErrorMessage}"
        : $"{Resolution}p  •  {CreatedAt.ToLocalTime():MMM d, yyyy • h:mm tt}";

    /// <summary>0-100, for direct binding to a <c>ProgressBar</c> without a converter.</summary>
    public double ProgressPercent => Progress * 100;

    public bool ShowProgress => Status is DownloadQueueStatus.Active or DownloadQueueStatus.Paused;

    /// <summary>Only once yt-dlp has actually reported a size — see <see cref="TotalBytes"/>'s doc comment.</summary>
    public bool ShowSize => ShowProgress && TotalBytes is > 0;

    public string SizeText => $"{FormatBytes(DownloadedBytes ?? 0)} / {FormatBytes(TotalBytes ?? 0)}";

    /// <summary>
    /// Binary (1024-based) units, matching what these bytes were actually computed from — yt-dlp's
    /// own KiB/MiB/GiB progress output — but labeled the more familiar KB/MB/GB rather than the
    /// pedantically-correct KiB/MiB/GiB, matching how most end-user apps display file sizes.
    /// </summary>
    private static string FormatBytes(long bytes)
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
}
