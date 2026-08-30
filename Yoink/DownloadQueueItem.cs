using System;

namespace Yoink;

/// <summary>
/// Where a queued download currently stands. <see cref="Completed"/>, <see cref="Failed"/> and
/// <see cref="Canceled"/> are terminal; <see cref="Paused"/> is not — a paused item goes back to
/// <see cref="Pending"/> (and eventually gets picked up again) via
/// <see cref="DownloadQueueService.ResumeAsync"/>.
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
/// One row in the persisted download queue (README roadmap step 3). Plain data, same style as
/// <see cref="DownloadHistoryEntry"/> — <see cref="DownloadQueueService"/> owns reading/writing it.
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
}
