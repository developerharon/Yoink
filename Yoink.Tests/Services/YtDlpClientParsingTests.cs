using System.Text.Json;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Exercises YtDlpClient's JSON/text parsing directly against sample yt-dlp output shapes, without
/// ever spawning a real yt-dlp process — see each method's own doc comment for why it's internal
/// rather than private.
/// </summary>
public class ParseVideoInfoTests
{
    [Fact]
    public void ParsesIdTitleAndWebpageUrl()
    {
        var json = """
            {
              "id": "dQw4w9WgXcQ",
              "title": "Never Gonna Give You Up",
              "webpage_url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
              "formats": []
            }
            """;

        var info = YtDlpClient.ParseVideoInfo(JsonDocument.Parse(json).RootElement);

        Assert.Equal("dQw4w9WgXcQ", info.Id);
        Assert.Equal("Never Gonna Give You Up", info.Title);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", info.WebpageUrl);
        Assert.Empty(info.Formats);
    }

    [Fact]
    public void FallsBackToId_WhenTitleIsMissing()
    {
        var json = """{ "id": "abc123" }""";

        var info = YtDlpClient.ParseVideoInfo(JsonDocument.Parse(json).RootElement);

        Assert.Equal("abc123", info.Title);
        Assert.Equal(string.Empty, info.WebpageUrl);
    }

    [Fact]
    public void ParsesFormats_IncludingVideoOnlyAndAudioOnlyStreams()
    {
        // The third entry below deliberately has no "format_id" — YtDlpClient.ParseVideoInfo skips
        // any format missing one, since that's not something the app can actually select with `-f`.
        var jsonWithMissingId = """
            {
              "id": "abc123",
              "formats": [
                { "format_id": "137", "ext": "mp4", "height": 1080, "vcodec": "avc1.640028", "acodec": "none", "filesize": 123456 },
                { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize_approx": 654321 },
                { "vcodec": "none" }
              ]
            }
            """;

        var info = YtDlpClient.ParseVideoInfo(JsonDocument.Parse(jsonWithMissingId).RootElement);

        Assert.Equal(2, info.Formats.Count);

        var video = info.Formats[0];
        Assert.Equal("137", video.FormatId);
        Assert.Equal(1080, video.Height);
        Assert.True(video.HasVideo);
        Assert.False(video.HasAudio);
        Assert.Equal(123456, video.FileSizeBytes);

        var audio = info.Formats[1];
        Assert.Equal("140", audio.FormatId);
        Assert.False(audio.HasVideo);
        Assert.True(audio.HasAudio);
        Assert.Equal(654321, audio.FileSizeBytes); // fell back to filesize_approx
    }

    [Fact]
    public void MissingFormatsProperty_ResultsInEmptyList()
    {
        var json = """{ "id": "abc123" }""";

        var info = YtDlpClient.ParseVideoInfo(JsonDocument.Parse(json).RootElement);

        Assert.Empty(info.Formats);
    }
}

public class ParsePlaylistEntryLineTests
{
    [Fact]
    public void PrefersUrlProperty_OverWebpageUrl()
    {
        var entry = YtDlpClient.ParsePlaylistEntryLine(
            """{ "id": "v1", "title": "Video One", "url": "https://youtu.be/v1", "webpage_url": "https://www.youtube.com/watch?v=v1" }""");

        Assert.NotNull(entry);
        Assert.Equal("https://youtu.be/v1", entry!.Url);
    }

    [Fact]
    public void FallsBackToWebpageUrl_WhenUrlPropertyMissing()
    {
        var entry = YtDlpClient.ParsePlaylistEntryLine(
            """{ "id": "v1", "title": "Video One", "webpage_url": "https://www.youtube.com/watch?v=v1" }""");

        Assert.NotNull(entry);
        Assert.Equal("https://www.youtube.com/watch?v=v1", entry!.Url);
    }

    [Fact]
    public void FallsBackToId_WhenTitleMissing()
    {
        var entry = YtDlpClient.ParsePlaylistEntryLine("""{ "id": "v1", "url": "https://youtu.be/v1" }""");

        Assert.NotNull(entry);
        Assert.Equal("v1", entry!.Title);
    }

    [Fact]
    public void ReturnsNull_WhenNeitherUrlNorWebpageUrlPresent()
    {
        var entry = YtDlpClient.ParsePlaylistEntryLine("""{ "id": "v1", "title": "No URL Here" }""");

        Assert.Null(entry);
    }
}

public class ExtractErrorSummaryTests
{
    [Fact]
    public void PicksTheLastErrorLine_WhenMultiplePresent()
    {
        var stderr = "WARNING: something minor\nERROR: first problem\nERROR: the real problem\n";

        Assert.Equal("ERROR: the real problem", YtDlpClient.ExtractErrorSummary(stderr));
    }

    [Fact]
    public void FallsBackToLastLine_WhenNoErrorLinePresent()
    {
        var stderr = "some diagnostic output\nlast line of output";

        Assert.Equal("last line of output", YtDlpClient.ExtractErrorSummary(stderr));
    }

    [Fact]
    public void FallsBackToUnknownError_WhenStderrIsEmpty()
    {
        Assert.Equal("unknown error.", YtDlpClient.ExtractErrorSummary(""));
    }
}

public class TryParseProgressPercentTests
{
    [Theory]
    [InlineData("[download]  42.5% of 10.00MiB at 1.00MiB/s ETA 00:05", 42.5)]
    [InlineData("[download] 100% of 10.00MiB", 100)]
    [InlineData("[download]   0.0% of 10.00MiB", 0)]
    public void ParsesPercentFromARealDownloadLine(string line, double expected)
    {
        Assert.True(YtDlpClient.TryParseProgressPercent(line, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("[download] Destination: video.f137.mp4")]
    [InlineData("[Merger] Merging formats into \"video.mp4\"")]
    [InlineData("some unrelated line")]
    public void DoesNotMatch_NonProgressLines(string line)
    {
        Assert.False(YtDlpClient.TryParseProgressPercent(line, out _));
    }
}
