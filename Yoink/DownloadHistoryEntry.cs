using System;

namespace Yoink;

/// <summary>
/// One row in the "Recent downloads" list. Plain data — no view-model machinery, just a couple of
/// display-formatting properties the list's DataTemplate binds to directly.
/// </summary>
public class DownloadHistoryEntry
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Resolution { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset DownloadedAt { get; set; }
    public DownloadStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    public string Subtitle => Status == DownloadStatus.Completed
        ? $"{Resolution}p  •  {DownloadedAt.ToLocalTime():MMM d, yyyy • h:mm tt}"
        : $"Failed  •  {DownloadedAt.ToLocalTime():MMM d, yyyy • h:mm tt}";

    public string StatusText => Status == DownloadStatus.Completed ? "Completed" : "Failed";

    public bool CanShowInFolder => Status == DownloadStatus.Completed;
}

public enum DownloadStatus
{
    Completed,
    Failed
}
