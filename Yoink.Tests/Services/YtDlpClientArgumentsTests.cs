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

/// <summary>
/// Covers <see cref="YtDlpClient.DownloadAsync"/>'s <c>infoJson</c> parameter — the
/// --load-info-json plumbing added to skip yt-dlp's own redundant re-extraction when a caller
/// already resolved the video (see the yt-dlp-startup-latency project memory). Same stand-in
/// "yt-dlp" shell-script technique as <see cref="DownloadAsyncArgumentsTests"/>, for the same
/// reasons — Linux-only.
/// </summary>
public class DownloadAsyncInfoJsonTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"yoink-infojson-tests-{Guid.NewGuid():N}");

    public DownloadAsyncInfoJsonTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task DownloadAsync_PassesLoadInfoJson_AndOmitsTheUrl_WhenInfoJsonProvided()
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

        await client.DownloadAsync(
            "https://youtu.be/abc123", "best", destination, infoJson: """{"id":"abc123"}""");

        var recordedArgs = await File.ReadAllLinesAsync(argsFile);
        Assert.Contains("--load-info-json", recordedArgs);
        // The info file already carries the URL; passing it again alongside --load-info-json isn't
        // needed and the positional-args "--" marker (only ever added for the bare-URL path) should
        // be absent too.
        Assert.DoesNotContain("https://youtu.be/abc123", recordedArgs);
        Assert.DoesNotContain("--", recordedArgs);

        // The temp info-json file --load-info-json points at should be cleaned up afterwards, not
        // left behind on every single download.
        var infoJsonPathArg = recordedArgs[Array.IndexOf(recordedArgs, "--load-info-json") + 1];
        Assert.False(File.Exists(infoJsonPathArg));
    }

    [Fact]
    public async Task DownloadAsync_RetriesWithFreshUrlExtraction_WhenInfoJsonAttemptFails()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Stand-in \"yt-dlp\" is a shell script; the retry logic itself is plain, platform-agnostic control flow.");
            return;
        }

        // Fails whenever called with --load-info-json (simulating expired signed format URLs),
        // succeeds otherwise (an ordinary fresh extraction from the URL) — proving DownloadAsync
        // actually falls back rather than just failing the whole download outright.
        var callsFile = Path.Combine(_tempDir, "calls.txt");
        var scriptPath = Path.Combine(_tempDir, "fake-yt-dlp.sh");
        await File.WriteAllTextAsync(scriptPath, $"""
            #!/bin/sh
            printf '%s ' "$@" >> "{callsFile}"
            printf '\n' >> "{callsFile}"
            case "$*" in
              *--load-info-json*) exit 1 ;;
              *) exit 0 ;;
            esac
            """);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var client = new YtDlpClient();
        client.UseResolvedPaths(scriptPath, ffmpegDirectory: null);

        var destination = Path.Combine(_tempDir, "out.mp4");

        // Doesn't throw — the retry recovers it.
        await client.DownloadAsync(
            "https://youtu.be/abc123", "best", destination, infoJson: """{"id":"abc123"}""");

        // One line per invocation (each ending in its own trailing newline write) rather than one
        // line per argument.
        var calls = (await File.ReadAllTextAsync(callsFile))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, calls.Length);
        Assert.Contains("--load-info-json", calls[0]);
        Assert.DoesNotContain("--load-info-json", calls[1]);
    }
}
