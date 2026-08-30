using System;
using System.Threading;
using System.Threading.Tasks;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Drives the real background poll loop with a fast interval and fake clipboard-read/enabled
/// delegates — see ClipboardWatcherService's own doc comment for why detection is a conservative,
/// YouTube-only regex and why it's a delegate-based design rather than exposing settable properties.
/// </summary>
public class ClipboardWatcherServiceTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL12345")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/WATCH?V=DQW4W9WGXCQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ")] // surrounding whitespace, as a real clipboard copy might have
    public async Task UrlDetected_Fires_ForRecognizedYouTubeUrlShapes(string clipboardText)
    {
        string? detected = null;
        var detectedEvent = new System.Threading.ManualResetEventSlim(false);

        using var watcher = new ClipboardWatcherService(
            () => Task.FromResult<string?>(clipboardText),
            () => true,
            PollInterval);
        watcher.UrlDetected += url =>
        {
            detected = url;
            detectedEvent.Set();
        };

        Assert.True(detectedEvent.Wait(WaitTimeout), "UrlDetected never fired.");
        Assert.Equal(clipboardText.Trim(), detected);
    }

    [Theory]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("not a url at all")]
    [InlineData("ftp://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/results?search_query=cats")]
    [InlineData("")]
    public async Task UrlDetected_NeverFires_ForNonMatchingText(string clipboardText)
    {
        var fired = false;
        using var watcher = new ClipboardWatcherService(
            () => Task.FromResult<string?>(clipboardText),
            () => true,
            PollInterval);
        watcher.UrlDetected += _ => fired = true;

        // Give it several poll cycles to prove a negative, rather than just one.
        await Task.Delay(PollInterval * 10);

        Assert.False(fired);
    }

    [Fact]
    public async Task UrlDetected_FiresOnlyOnce_ForTheSameUnchangedClipboardText()
    {
        var fireCount = 0;
        using var watcher = new ClipboardWatcherService(
            () => Task.FromResult<string?>("https://youtu.be/dQw4w9WgXcQ"),
            () => true,
            PollInterval);
        watcher.UrlDetected += _ => Interlocked.Increment(ref fireCount);

        await Task.Delay(PollInterval * 15);

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task UrlDetected_NeverFires_WhenDisabled()
    {
        var fired = false;
        using var watcher = new ClipboardWatcherService(
            () => Task.FromResult<string?>("https://youtu.be/dQw4w9WgXcQ"),
            () => false,
            PollInterval);
        watcher.UrlDetected += _ => fired = true;

        await Task.Delay(PollInterval * 10);

        Assert.False(fired);
    }
}
