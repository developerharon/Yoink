using System;
using Yoink.Models;

namespace Yoink.Tests.Models;

/// <summary>
/// The queue view (Views/MainWindow.axaml) binds directly to these computed properties with no
/// converters — see DownloadQueueItem's own doc comment — so they're the actual presentation logic
/// for every row in the download queue, not just incidental plumbing.
/// </summary>
public class DownloadQueueItemTests
{
    private static DownloadQueueItem Make(DownloadQueueStatus status, string? errorMessage = null) => new()
    {
        Id = 1,
        Url = "https://youtu.be/abc123",
        Title = "Some Video",
        Resolution = 1080,
        Status = status,
        ErrorMessage = errorMessage,
        CreatedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData(DownloadQueueStatus.Pending, false, false, true, false, false, false)]
    [InlineData(DownloadQueueStatus.Active, true, false, true, false, false, true)]
    [InlineData(DownloadQueueStatus.Paused, false, true, true, false, false, true)]
    [InlineData(DownloadQueueStatus.Completed, false, false, false, false, true, false)]
    [InlineData(DownloadQueueStatus.Failed, false, false, false, true, false, false)]
    [InlineData(DownloadQueueStatus.Canceled, false, false, false, true, false, false)]
    public void ActionVisibility_MatchesExactlyOneStateMachine(
        DownloadQueueStatus status,
        bool canPause,
        bool canResume,
        bool canCancel,
        bool canRetry,
        bool canShowInFolder,
        bool showProgress)
    {
        var item = Make(status);

        Assert.Equal(canPause, item.CanPause);
        Assert.Equal(canResume, item.CanResume);
        Assert.Equal(canCancel, item.CanCancel);
        Assert.Equal(canRetry, item.CanRetry);
        Assert.Equal(canShowInFolder, item.CanShowInFolder);
        Assert.Equal(showProgress, item.ShowProgress);
    }

    [Fact]
    public void DisplayTitle_FallsBackToUrl_WhenTitleNotYetResolved()
    {
        var item = Make(DownloadQueueStatus.Pending);
        item.Title = "";

        Assert.Equal(item.Url, item.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_UsesTitle_WhenResolved()
    {
        var item = Make(DownloadQueueStatus.Active);

        Assert.Equal("Some Video", item.DisplayTitle);
    }

    [Fact]
    public void StatusText_IsTheEnumName()
    {
        Assert.Equal("Completed", Make(DownloadQueueStatus.Completed).StatusText);
        Assert.Equal("Failed", Make(DownloadQueueStatus.Failed).StatusText);
    }

    [Fact]
    public void ProgressPercent_ScalesFractionTo0To100()
    {
        var item = Make(DownloadQueueStatus.Active);
        item.Progress = 0.42;

        Assert.Equal(42, item.ProgressPercent);
    }

    [Fact]
    public void Subtitle_ShowsErrorMessage_WhenFailedWithOne()
    {
        var item = Make(DownloadQueueStatus.Failed, errorMessage: "yt-dlp failed: video unavailable.");

        Assert.Contains("yt-dlp failed: video unavailable.", item.Subtitle);
        Assert.StartsWith("1080p", item.Subtitle);
    }

    [Fact]
    public void Subtitle_ShowsCreatedDate_NotAnErrorMessage_WhenFailedWithNoErrorMessage()
    {
        var item = Make(DownloadQueueStatus.Failed, errorMessage: null);

        Assert.Contains("1080p", item.Subtitle);
        Assert.Contains("2026", item.Subtitle);
    }

    [Fact]
    public void Subtitle_ShowsCreatedDate_WhenNotFailed()
    {
        var item = Make(DownloadQueueStatus.Completed, errorMessage: "should be ignored — only Failed shows it");

        Assert.Contains("1080p", item.Subtitle);
        Assert.Contains("2026", item.Subtitle);
        Assert.DoesNotContain("ignored", item.Subtitle);
    }
}
