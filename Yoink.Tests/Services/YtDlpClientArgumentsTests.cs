using System;
using System.IO;
using System.Threading.Tasks;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Regression test for a real bug: a URL carrying a "list=" query parameter (attached automatically
/// by YouTube any time you copy a link while a Mix/Radio/autoplay playlist is going) made
/// <see cref="YtDlpClient.DownloadAsync"/> download the *entire* playlist into the one requested
/// destination path instead of just the single video the user actually queued — yt-dlp's own process
/// then kept running through every other item for as long as that took (sometimes hundreds of
/// videos), which looked exactly like the item being permanently stuck "Active" even though its own
/// video had already finished and was fully playable. Confirmed against a real "list="-decorated URL
/// before fixing it by adding the same <c>--no-playlist</c> flag <see cref="YtDlpClient.GetVideoInfoAsync"/>
/// already passed.
///
/// Rather than re-running a real multi-minute (or, unfixed, multi-hour) download to prove this,
/// points <see cref="YtDlpClient"/> at a stand-in "yt-dlp" that just records its own argv and exits,
/// so this runs in well under a second. Linux-only (the stand-in is a shell script) — skips itself
/// elsewhere, since the thing under test (which flags get built into the argument list) is
/// process-invocation plumbing that doesn't vary by platform.
/// </summary>
public class DownloadAsyncArgumentsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"yoink-args-tests-{Guid.NewGuid():N}");

    public DownloadAsyncArgumentsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task DownloadAsync_AlwaysPassesNoPlaylist()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Stand-in \"yt-dlp\" is a shell script; the flag itself is plain, platform-agnostic argument-list construction.");
            return;
        }

        var argsFile = Path.Combine(_tempDir, "args.txt");
        var scriptPath = Path.Combine(_tempDir, "fake-yt-dlp.sh");
        await File.WriteAllTextAsync(scriptPath, $"""
            #!/bin/sh
            printf '%s\n' "$@" > "{argsFile}"
            exit 0
            """);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var client = new YtDlpClient();
        client.UseResolvedPaths(scriptPath, ffmpegDirectory: null);

        var destination = Path.Combine(_tempDir, "out.mp4");

        await client.DownloadAsync("https://youtu.be/abc123?list=RDMMabc123", "best", destination);

        var recordedArgs = await File.ReadAllLinesAsync(argsFile);
        Assert.Contains("--no-playlist", recordedArgs);
    }
}
