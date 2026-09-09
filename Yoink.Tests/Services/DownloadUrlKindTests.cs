using Yoink.Services;

namespace Yoink.Tests.Services;

public class DownloadUrlKindTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/watch?v=dQw4w9WgXcQ")]
    public void IsYouTubeUrl_RecognizesEveryKnownHost(string url) => Assert.True(DownloadUrlKind.IsYouTubeUrl(url));

    [Theory]
    [InlineData("https://example.com/some/video")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void IsYouTubeUrl_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.IsYouTubeUrl(url));

    [Theory]
    [InlineData("https://github.com/developerharon/Yoink/releases/download/v0.1.0/Yoink.AppImage")]
    [InlineData("https://example.com/files/report.pdf")]
    [InlineData("https://example.com/setup.EXE")] // extension matching is case-insensitive
    [InlineData("https://example.com/package.deb")]
    [InlineData("https://example.com/archive.tar.gz")]
    public void LooksLikeDownloadableFile_RecognizesKnownExtensions(string url) => Assert.True(DownloadUrlKind.LooksLikeDownloadableFile(url));

    [Theory]
    [InlineData("https://example.com/some/page")] // ordinary webpage, no file extension
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")] // a video page, not a direct file link
    [InlineData("ftp://example.com/file.pdf")] // not http(s)
    [InlineData("not a url at all")]
    [InlineData("")]
    public void LooksLikeDownloadableFile_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.LooksLikeDownloadableFile(url));

    [Theory]
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Some+Torrent")]
    [InlineData("MAGNET:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")] // scheme match is case-insensitive
    public void IsMagnetLink_RecognizesMagnetScheme(string url) => Assert.True(DownloadUrlKind.IsMagnetLink(url));

    [Theory]
    [InlineData("https://example.com/some.torrent")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void IsMagnetLink_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.IsMagnetLink(url));

    [Theory]
    [InlineData("https://example.com/some.torrent")]
    [InlineData("https://example.com/some.TORRENT")] // extension matching is case-insensitive
    [InlineData("https://example.com/path/to/ubuntu-24.04.torrent")]
    public void IsTorrentFileUrl_RecognizesTorrentExtension(string url) => Assert.True(DownloadUrlKind.IsTorrentFileUrl(url));

    [Theory]
    [InlineData("https://example.com/report.pdf")]
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")] // not http(s)
    [InlineData("ftp://example.com/some.torrent")] // not http(s)
    [InlineData("not a url at all")]
    [InlineData("")]
    public void IsTorrentFileUrl_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.IsTorrentFileUrl(url));

    [Theory]
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")]
    [InlineData("https://example.com/some.torrent")]
    public void IsTorrentSource_True_ForEitherShape(string url) => Assert.True(DownloadUrlKind.IsTorrentSource(url));

    [Theory]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/report.pdf")]
    public void IsTorrentSource_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.IsTorrentSource(url));

    [Theory]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")] // YouTube
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")] // magnet
    [InlineData("https://example.com/some.torrent")] // .torrent file URL
    [InlineData("https://example.com/report.pdf")] // downloadable file
    public void IsRecognizedDownloadUrl_True_ForAnyKnownShape(string url) => Assert.True(DownloadUrlKind.IsRecognizedDownloadUrl(url));

    [Theory]
    [InlineData("https://example.com/some/page")] // plain webpage, none of the three shapes
    [InlineData("--url=https://youtube.com/watch?v=dQw4w9WgXcQ")] // a flag, not a bare URL
    [InlineData("not a url at all")]
    [InlineData("")]
    public void IsRecognizedDownloadUrl_False_ForAnythingElse(string url) => Assert.False(DownloadUrlKind.IsRecognizedDownloadUrl(url));
}
