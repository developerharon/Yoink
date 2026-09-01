using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Just the pure/isolable bit of <see cref="DependencyProvisioningService"/> — parsing yt-dlp's
/// version out of GitHub's release-download redirect location. Everything else on that class talks
/// to the network or spawns processes (yt-dlp/ffmpeg/tar), so it isn't exercised here — same
/// reasoning as <see cref="YtDlpClientParsingTests"/> only covering yt-dlp's own parsing helpers,
/// not process invocation.
/// </summary>
public class DependencyProvisioningServiceTests
{
    [Theory]
    [InlineData(
        "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
        "2026.08.19")]
    [InlineData(
        "https://github.com/yt-dlp/yt-dlp/releases/download/2025.01.01/yt-dlp_macos",
        "2025.01.01")]
    [InlineData(
        "https://github.com/yt-dlp/yt-dlp/releases/download/2025.01.01/yt-dlp_linux",
        "2025.01.01")]
    public void ParseYtDlpVersionFromRedirect_ExtractsVersionFromDownloadUrl(string redirectLocation, string expectedVersion)
    {
        var version = DependencyProvisioningService.ParseYtDlpVersionFromRedirect(redirectLocation);

        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void ParseYtDlpVersionFromRedirect_ReturnsNull_ForNullLocation()
    {
        Assert.Null(DependencyProvisioningService.ParseYtDlpVersionFromRedirect(null));
    }

    [Fact]
    public void ParseYtDlpVersionFromRedirect_ReturnsNull_ForUnrecognizedUrlShape()
    {
        Assert.Null(DependencyProvisioningService.ParseYtDlpVersionFromRedirect("https://example.com/yt-dlp.exe"));
    }
}
