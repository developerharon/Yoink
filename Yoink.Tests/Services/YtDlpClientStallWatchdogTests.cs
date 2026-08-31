using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// <see cref="YtDlpClient.IsStalled"/> is internal (not private) purely so this pure logic is
/// testable without spawning a process or waiting out a real timeout.
/// </summary>
public class IsStalledTests
{
    [Fact]
    public void False_WhenIdleTimeIsBelowTimeout() =>
        Assert.False(YtDlpClient.IsStalled(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)));

    [Fact]
    public void True_WhenIdleTimeReachesTimeout() =>
        Assert.True(YtDlpClient.IsStalled(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));

    [Fact]
    public void True_WhenIdleTimeExceedsTimeout() =>
        Assert.True(YtDlpClient.IsStalled(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5)));
}

/// <summary>
/// Regression test for a real bug: an item whose file had already finished downloading (no leftover
/// `.part`) sat "Active" forever because yt-dlp's own process never exited — <c>WaitForExitAsync</c>
/// was still waiting on a process that itself was hung (most likely on a blocked stdin read; see
/// <c>YtDlpClient.CreateProcess</c>'s stdin redirect, the primary fix for that). This test doesn't
/// exercise that specific stdin trigger — it goes straight at the backstop
/// (<c>YtDlpClient.DefaultStallTimeout</c>'s doc comment) by pointing <see cref="YtDlpClient"/> at a
/// stand-in "yt-dlp" that produces no output and never exits on its own for any reason, so the only
/// way <see cref="YtDlpClient.DownloadAsync"/> can ever return is via the watchdog killing it.
///
/// Linux-only (the stand-in is a shell script) — the watchdog logic itself is platform-agnostic and
/// this is the only place it's exercised end-to-end against a real process, so it skips rather than
/// silently passing elsewhere.
/// </summary>
public class DownloadAsyncStallWatchdogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"yoink-stall-tests-{Guid.NewGuid():N}");

    public DownloadAsyncStallWatchdogTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task DownloadAsync_KillsHungProcessAndThrowsTimeoutException()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Stand-in \"yt-dlp\" is a shell script; the watchdog logic itself is covered platform-agnostically by IsStalledTests.");
            return;
        }

        var scriptPath = Path.Combine(_tempDir, "fake-yt-dlp.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var client = new YtDlpClient();
        client.UseResolvedPaths(scriptPath, ffmpegDirectory: null);

        var destination = Path.Combine(_tempDir, "out.mp4");
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.DownloadAsync(
                "https://example.invalid/video",
                "best",
                destination,
                stallTimeout: TimeSpan.FromMilliseconds(300)));

        stopwatch.Stop();

        Assert.Contains("no output", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Should be detected promptly off the short test stallTimeout, nowhere near the real
        // 5-minute production default — proves the poll interval actually scales down with it.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Watchdog took too long: {stopwatch.Elapsed}");
    }
}
