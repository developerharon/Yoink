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
    public string? FilePath { get; set; }
    public DownloadQueueStatus Status { get; set; }
    public double Progress { get; set; }
    public string? ErrorMessage { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public string DisplayTitle => string.IsNullOrEmpty(Title) ? Url : Title;

    public string StatusText => Status.ToString();

    public string Subtitle => Status == DownloadQueueStatus.Failed && !string.IsNullOrEmpty(ErrorMessage)
        ? $"{Resolution}p  •  {ErrorMessage}"
        : $"{Resolution}p  •  {CreatedAt.ToLocalTime():MMM d, yyyy • h:mm tt}";

    /// <summary>0-100, for direct binding to a <c>ProgressBar</c> without a converter.</summary>
    public double ProgressPercent => Progress * 100;

    public bool ShowProgress => Status is DownloadQueueStatus.Active or DownloadQueueStatus.Paused;

    public bool CanPause => Status == DownloadQueueStatus.Active;
    public bool CanResume => Status == DownloadQueueStatus.Paused;
    public bool CanCancel => Status is DownloadQueueStatus.Pending or DownloadQueueStatus.Active or DownloadQueueStatus.Paused;
    public bool CanRetry => Status is DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled;
    public bool CanShowInFolder => Status == DownloadQueueStatus.Completed;
}
