namespace Yoink.Tests;

/// <summary>
/// Just <see cref="Program.ExtractUrlArg"/> — the one pure/isolable piece of <c>Program.Main</c>'s
/// startup dance; everything else there (the real <see cref="Services.SingleInstanceLock"/> file
/// lock, starting the real Avalonia app, spawning a process) needs a real environment to exercise, so
/// it isn't covered here.
/// </summary>
public class ProgramTests
{
    [Fact]
    public void ExtractUrlArg_ReadsExplicitUrlFlag()
    {
        var url = Program.ExtractUrlArg(["--url=https://youtu.be/dQw4w9WgXcQ"]);

        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", url);
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")] // a .desktop file's Exec=... %u shape
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")]
    public void ExtractUrlArg_RecognizesBarePositionalUrl(string url)
    {
        var extracted = Program.ExtractUrlArg([url]);

        Assert.Equal(url, extracted);
    }

    [Fact]
    public void ExtractUrlArg_PrefersExplicitFlagOverBarePositional()
    {
        var url = Program.ExtractUrlArg(["https://example.com/unrelated.pdf", "--url=https://youtu.be/dQw4w9WgXcQ"]);

        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", url);
    }

    [Fact]
    public void ExtractUrlArg_ReturnsNull_WhenNoArgsGiven()
    {
        Assert.Null(Program.ExtractUrlArg([]));
    }

    [Theory]
    [InlineData("--some-other-flag")]
    [InlineData("not a url at all")]
    public void ExtractUrlArg_ReturnsNull_WhenNoUrlPresent(string arg)
    {
        Assert.Null(Program.ExtractUrlArg([arg]));
    }
}
