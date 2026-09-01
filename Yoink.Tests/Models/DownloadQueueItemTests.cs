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

    [Fact]
    public void ShowSize_IsFalse_WhenTotalBytesNotYetKnown()
    {
        var item = Make(DownloadQueueStatus.Active);
        item.TotalBytes = null;

        Assert.False(item.ShowSize);
    }

    [Fact]
    public void ShowSize_IsFalse_WhenNotActiveOrPaused_EvenWithKnownTotal()
    {
        // A Completed item still carries whatever bytes/total DownloadAsync's own final report
        // left it with (see YtDlpClient.DownloadAsync) — ShowSize hides it anyway, same gate as
        // ShowProgress, since the size readout only makes sense mid-download.
        var item = Make(DownloadQueueStatus.Completed);
        item.DownloadedBytes = 1024;
        item.TotalBytes = 2048;

        Assert.False(item.ShowSize);
    }

    [Fact]
    public void ShowSize_IsTrue_WhenActiveWithKnownTotal()
    {
        var item = Make(DownloadQueueStatus.Active);
        item.DownloadedBytes = 1024;
        item.TotalBytes = 2048;

        Assert.True(item.ShowSize);
    }

    [Theory]
    [InlineData(0L, 0L, "0 B / 0 B")]
    [InlineData(512L, 1024L, "512 B / 1 KB")]
    [InlineData(1_572_864L, 10_485_760L, "1.5 MB / 10 MB")]
    [InlineData(1_073_741_824L, 2_147_483_648L, "1 GB / 2 GB")]
    public void SizeText_FormatsBothSidesInBinaryUnits(long downloaded, long total, string expected)
    {
        var item = Make(DownloadQueueStatus.Active);
        item.DownloadedBytes = downloaded;
        item.TotalBytes = total;

        Assert.Equal(expected, item.SizeText);
    }
}
