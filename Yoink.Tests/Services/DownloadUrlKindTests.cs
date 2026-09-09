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
}
