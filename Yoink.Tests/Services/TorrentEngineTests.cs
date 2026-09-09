using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// <see cref="TorrentEngine"/>'s own network/process-spawning surface (<c>DownloadAsync</c>,
/// <c>LoadTorrentAsync</c> against a real .torrent URL) isn't covered here for the same reason
/// <c>YtDlpClient</c>'s and <c>DependencyProvisioningService</c>'s equivalents aren't — see those
/// classes' own doc comments. <see cref="TorrentEngine.TryGetMagnetDisplayName"/> is the one pure,
/// no-network piece: parsing a magnet URI's own <c>dn=</c> parameter is just string/URI parsing.
/// </summary>
public class TorrentEngineTests
{
    [Fact]
    public void TryGetMagnetDisplayName_ReturnsTheDnParameter_WhenPresent()
    {
        const string magnet = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Ubuntu+24.04&tr=udp%3A%2F%2Ftracker.example.com%3A80";

        Assert.Equal("Ubuntu 24.04", TorrentEngine.TryGetMagnetDisplayName(magnet));
    }

    [Fact]
    public void TryGetMagnetDisplayName_ReturnsNull_WhenNoDnParameter()
    {
        const string magnet = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

        Assert.Null(TorrentEngine.TryGetMagnetDisplayName(magnet));
    }

    [Fact]
    public void TryGetMagnetDisplayName_ReturnsNull_ForAnInvalidMagnetLink()
    {
        Assert.Null(TorrentEngine.TryGetMagnetDisplayName("not a magnet link"));
    }
}
