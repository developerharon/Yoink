using System;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>Exercises the pure functions DownloadQueueService.IsWithinWindow was specifically split
/// out to make testable without controlling the system clock — see its own doc comment.</summary>
public class IsWithinWindowTests
{
    [Theory]
    [InlineData("09:00", "17:00", "08:59", false)]
    [InlineData("09:00", "17:00", "09:00", true)] // start is inclusive
    [InlineData("09:00", "17:00", "12:00", true)]
    [InlineData("09:00", "17:00", "16:59", true)]
    [InlineData("09:00", "17:00", "17:00", false)] // end is exclusive
    [InlineData("09:00", "17:00", "23:00", false)]
    public void SameDayWindow(string start, string end, string now, bool expected) =>
        Assert.Equal(expected, DownloadQueueService.IsWithinWindow(TimeOnly.Parse(now), TimeOnly.Parse(start), TimeOnly.Parse(end)));

    [Theory]
    [InlineData("22:00", "06:00", "23:00", true)]  // late evening, before midnight
    [InlineData("22:00", "06:00", "02:00", true)]  // after midnight, before end
    [InlineData("22:00", "06:00", "22:00", true)]  // start is inclusive
    [InlineData("22:00", "06:00", "06:00", false)] // end is exclusive
    [InlineData("22:00", "06:00", "12:00", false)] // broad daylight — outside an overnight window
    [InlineData("22:00", "06:00", "21:59", false)]
    public void OvernightWrapWindow(string start, string end, string now, bool expected) =>
        Assert.Equal(expected, DownloadQueueService.IsWithinWindow(TimeOnly.Parse(now), TimeOnly.Parse(start), TimeOnly.Parse(end)));

    [Fact]
    public void ZeroWidthWindow_IsNeverWithin()
    {
        // The trick DownloadQueueServiceTests uses to keep the background processing loop from
        // picking up items during a CRUD-focused test — start == end must never be "within".
        var noon = new TimeOnly(12, 0);
        Assert.False(DownloadQueueService.IsWithinWindow(noon, noon, noon));
    }
}

/// <summary>Exercises DownloadQueueService.ComputeRateLimitKBps — see its own doc comment for the
/// per-download-vs-global-share combination rule.</summary>
public class ComputeRateLimitKBpsTests
{
    private static AppSettings Settings(int? perDownload, int? global, int maxConcurrent = 1) => new()
    {
        PerDownloadSpeedLimitKBps = perDownload,
        GlobalSpeedLimitKBps = global,
        MaxConcurrentDownloads = maxConcurrent,
    };

    [Fact]
    public void NeitherSet_IsUnlimited() =>
        Assert.Null(DownloadQueueService.ComputeRateLimitKBps(Settings(null, null)));

    [Fact]
    public void OnlyPerDownloadSet_UsesItDirectly() =>
        Assert.Equal(500, DownloadQueueService.ComputeRateLimitKBps(Settings(500, null)));

    [Fact]
    public void OnlyGlobalSet_SplitsEvenlyAcrossConcurrentDownloads() =>
        Assert.Equal(250, DownloadQueueService.ComputeRateLimitKBps(Settings(null, 1000, maxConcurrent: 4)));

    [Fact]
    public void BothSet_TheSmallerOfTheTwoWins_PerDownloadSmaller() =>
        Assert.Equal(200, DownloadQueueService.ComputeRateLimitKBps(Settings(200, 1000, maxConcurrent: 2)));

    [Fact]
    public void BothSet_TheSmallerOfTheTwoWins_GlobalShareSmaller() =>
        Assert.Equal(250, DownloadQueueService.ComputeRateLimitKBps(Settings(900, 1000, maxConcurrent: 4)));

    [Fact]
    public void ZeroOrNegative_TreatedTheSameAsUnset()
    {
        Assert.Null(DownloadQueueService.ComputeRateLimitKBps(Settings(0, -5)));
    }

    [Fact]
    public void MaxConcurrentDownloads_NeverDividesByZeroOrNegative()
    {
        // AppSettings.MaxConcurrentDownloads' own doc comment: always clamped to at least 1 wherever
        // it's read, so a stray 0/negative in a hand-edited settings.json can't wedge this.
        var result = DownloadQueueService.ComputeRateLimitKBps(Settings(null, 1000, maxConcurrent: 0));
        Assert.Equal(1000, result);
    }
}

/// <summary>DownloadQueueService.BuildFormatSelector/BuildDestinationPath — the yt-dlp `-f` selector
/// and output filename each queued item actually gets built with.</summary>
public class BuildHelpersTests
{
    [Theory]
    [InlineData(1080)]
    [InlineData(720)]
    [InlineData(360)]
    public void BuildFormatSelector_CapsHeightAtRequestedResolution_WithSafeFallbacks(int resolution)
    {
        var selector = DownloadQueueService.BuildFormatSelector(resolution);

        Assert.Contains($"height<={resolution}", selector);
        Assert.EndsWith("/best", selector);
    }

    [Fact]
    public void BuildDestinationPath_StripsInvalidFileNameCharacters()
    {
        // Path.GetInvalidFileNameChars() is platform-specific — '/' is invalid everywhere (it's a
        // path separator), but e.g. ':'/'?' are only invalid on Windows, not Linux. Build the title
        // from whatever this platform actually considers invalid, so the test means the same thing
        // on every OS this app targets rather than assuming Windows' stricter rules.
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var title = "Some" + new string(invalid) + "Video Title";

        var fileName = System.IO.Path.GetFileName(DownloadQueueService.BuildDestinationPath(title));

        foreach (var c in invalid)
            Assert.DoesNotContain(c, fileName);
    }

    [Fact]
    public void BuildDestinationPath_AlwaysEndsWithMp4()
    {
        var path = DownloadQueueService.BuildDestinationPath("My Video");

        Assert.EndsWith(".mp4", path);
    }
}
