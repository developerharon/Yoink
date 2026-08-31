using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

/// <summary>
/// One selectable quality/format for a video, as reported by yt-dlp. YouTube mostly no longer
/// serves pre-muxed (single-file) audio+video above a low resolution, so most formats here are
/// video-only or audio-only — see <see cref="YtDlpClient.DownloadAsync"/> for how a video-only
/// and an audio-only stream get combined into one file.
/// </summary>
public sealed record YtDlpFormat(
    string FormatId,
    string? Extension,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    long? FileSizeBytes)
{
    public bool HasVideo => !string.IsNullOrEmpty(VideoCodec) && VideoCodec != "none";
    public bool HasAudio => !string.IsNullOrEmpty(AudioCodec) && AudioCodec != "none";
}

/// <summary>A single video's metadata and the formats it's available in.</summary>
public sealed record YtDlpVideoInfo(
    string Id,
    string Title,
    string WebpageUrl,
    IReadOnlyList<YtDlpFormat> Formats);

/// <summary>One entry from a playlist/channel URL, as reported by yt-dlp's flat-playlist listing.</summary>
public sealed record YtDlpPlaylistEntry(string Id, string Title, string Url);

/// <summary>
/// One progress update from <see cref="YtDlpClient.DownloadAsync"/>: <paramref name="Fraction"/> is
/// the same overall 0.0-1.0 value this always reported; <paramref name="BytesDownloaded"/>/
/// <paramref name="TotalBytes"/> are null until yt-dlp has printed a progress line with a size on it
/// (near-instant in practice, but genuinely absent for some live/fragmented streams). For a
/// multi-stream selector (video+audio), <paramref name="TotalBytes"/> only reflects the streams
/// seen *so far* — it grows once a later stream's own size becomes known, rather than showing the
/// full combined size from the very first line — see <c>DrainStdOutForProgressAsync</c>'s own
/// comment for why that's an accepted trade-off, not a bug.
/// </summary>
public readonly record struct YtDlpDownloadProgress(double Fraction, long? BytesDownloaded, long? TotalBytes);

/// <summary>
/// The YouTube extraction layer (README roadmap step 2): everything that talks to YouTube itself
/// — resolving a video's direct info, expanding a playlist into its videos, listing available
/// formats, and downloading + merging audio/video into one file — goes through the `yt-dlp`
/// command-line tool rather than a hand-rolled/reverse-engineered client. yt-dlp is maintained
/// specifically to track YouTube's frequent extraction changes, which per the README roadmap is
/// far less maintenance than reimplementing that cat-and-mouse game here.
///
/// Requires `yt-dlp` (and, for merging separately-downloaded video/audio streams, `ffmpeg`) to be
/// discoverable on PATH — see README.md's "Using it" section for install notes. Actual downloads
/// happen inside the yt-dlp process itself (it has its own resumable, retrying downloader and,
/// via ffmpeg, its own muxer) rather than through <see cref="DownloadEngine"/> — reimplementing
/// segment download + muxing on top of that engine would just be redoing what yt-dlp already
/// does correctly. <see cref="DownloadEngine"/> remains the engine for plain, single-file HTTP
/// downloads (e.g. the direct-link downloads planned later in the roadmap).
/// </summary>
public sealed class YtDlpClient
{
    private const string ExecutableName = "yt-dlp";

    private static readonly Regex ProgressLineRegex = new(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

    // Matches yt-dlp's own "of <size><unit>" token on the same progress line, e.g.
    // "[download]  42.5% of  218.53KiB at 1.38MiB/s ETA 00:00" or "[download] 100% of  218.53KiB
    // in 00:00:00 at ...". Confirmed against a real `yt-dlp --newline` run rather than assumed —
    // see the "download-progress-size" project memory for the actual captured output. The
    // optional "~" appears when yt-dlp only has an estimated size (a live/fragmented stream).
    private static readonly Regex ProgressSizeRegex = new(
        @"\[download\]\s+[\d.]+%\s+of\s+~?\s*([\d.]+)(KiB|MiB|GiB|TiB|B)\b", RegexOptions.Compiled);

    // Defaults to a bare PATH lookup, exactly like before dependency provisioning existed — every
    // Yoink.Tests use of this class (the parsing tests) never calls UseResolvedPaths and keeps
    // working unchanged. Views.MainWindow calls UseResolvedPaths once DependencyProvisioningService
    // has resolved where yt-dlp/ffmpeg actually live (PATH, or a Yoink-managed copy).
    private string _executablePath = ExecutableName;
    private string? _ffmpegDirectory;

    /// <summary>
    /// Points this client at a resolved yt-dlp executable (a bare name to keep using PATH lookup, or
    /// a full path to a Yoink-managed copy) and, optionally, the directory holding a managed ffmpeg
    /// — passed to every yt-dlp invocation as <c>--ffmpeg-location</c> so yt-dlp's own muxing step
    /// finds it without needing ffmpeg on PATH itself. See
    /// <see cref="DependencyProvisioningService"/> for how these are resolved.
    /// </summary>
    public void UseResolvedPaths(string executablePath, string? ffmpegDirectory)
    {
        _executablePath = executablePath;
        _ffmpegDirectory = ffmpegDirectory;
    }

    /// <summary>
    /// Pulled out of <see cref="DownloadAsync"/>'s stdout loop (which calls this, not the regex
    /// directly) purely so the line-matching itself is testable without a real yt-dlp process.
    /// </summary>
    internal static bool TryParseProgressPercent(string line, out double percent)
    {
        var match = ProgressLineRegex.Match(line);
        if (match.Success &&
            double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
            return true;

        percent = 0;
        return false;
    }

    /// <summary>
    /// The stream's total size (in bytes) off the same progress line <see cref="TryParseProgressPercent"/>
    /// reads the percent from, or null when the line has no size on it at all (yt-dlp genuinely
    /// doesn't always know one up front). Internal (not private) so Yoink.Tests can exercise it
    /// directly, same reasoning as <see cref="TryParseProgressPercent"/>.
    /// </summary>
    internal static long? TryParseTotalBytes(string line)
    {
        var match = ProgressSizeRegex.Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        var multiplier = match.Groups[2].Value switch
        {
            "B" => 1L,
            "KiB" => 1024L,
            "MiB" => 1024L * 1024,
            "GiB" => 1024L * 1024 * 1024,
            "TiB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L
        };

        return (long)(value * multiplier);
    }

    /// <summary>
    /// Quick presence check so callers can surface a friendly "please install yt-dlp" message
    /// instead of a bare process-start failure the first time it's actually needed.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = CreateProcess(["--version"]);
            StartProcess(process);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Resolves a single video's title and available formats.</summary>
    public async Task<YtDlpVideoInfo> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        // The trailing "--" tells yt-dlp's own argument parser that everything after it is
        // positional, never an option — see CreateProcess's doc comment for why this matters:
        // url is whatever the user (or the clipboard watcher) handed us, unvalidated, and
        // ArgumentList only rules out *shell* injection, not yt-dlp treating a value that starts
        // with "-" as one of its own flags (e.g. "--exec=...", which runs an arbitrary command
        // after a successful download).
        var json = await RunForStdOutAsync(["--no-playlist", "--dump-json", "--", url], cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        return ParseVideoInfo(document.RootElement);
    }

    /// <summary>
    /// Expands a playlist (or channel) URL into its individual videos, without resolving each
    /// one's formats — cheap enough to call just to list what a playlist contains.
    /// </summary>
    public async Task<IReadOnlyList<YtDlpPlaylistEntry>> GetPlaylistEntriesAsync(string url, CancellationToken cancellationToken = default)
    {
        // yt-dlp prints one JSON object per line (JSON Lines) when dumping multiple entries.
        // See GetVideoInfoAsync above for why the "--" before url is load-bearing, not decorative.
        var output = await RunForStdOutAsync(["--flat-playlist", "--dump-json", "--", url], cancellationToken).ConfigureAwait(false);

        var entries = new List<YtDlpPlaylistEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParsePlaylistEntryLine(line) is { } entry)
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// One line of yt-dlp's flat-playlist JSON-lines output, or null if it has no URL to speak of.
    /// Split out of <see cref="GetPlaylistEntriesAsync"/>'s loop, and internal rather than private,
    /// purely so Yoink.Tests can feed it sample lines directly.
    /// </summary>
    internal static YtDlpPlaylistEntry? ParsePlaylistEntryLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
        var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? id : id;
        var entryUrl = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString()
            : root.TryGetProperty("webpage_url", out var webpageProp) ? webpageProp.GetString()
            : null;

        return string.IsNullOrEmpty(entryUrl) ? null : new YtDlpPlaylistEntry(id, title, entryUrl);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> using <paramref name="formatSelector"/> (yt-dlp's `-f`
    /// syntax) to <paramref name="destinationPath"/>, merging separate video/audio streams into
    /// one file via ffmpeg when the selector resolves to more than one. yt-dlp handles its own
    /// resume (via its default `--continue` behavior against the `.part` file it writes next to
    /// the destination) and per-fragment retries, so a caller that cancels and later re-issues
    /// the same download resumes rather than restarting.
    /// </summary>
    /// <param name="expectedSegmentCount">
    /// A progress-reporting hint only: how many separate streams the selector is expected to
    /// resolve to (2 for a "video+audio" selector, 1 for an already-muxed single format). Used to
    /// turn yt-dlp's per-file 0-100% progress lines into one overall fraction; it has no effect on
    /// what actually gets downloaded.
    /// </param>
    /// <param name="rateLimitKBps">
    /// Optional speed cap in KB/s, passed straight through to yt-dlp's own `--limit-rate` (README
    /// roadmap step 7). Null or ≤0 means unlimited. This is a per-process limit set once at launch —
    /// combining a per-download and a global setting into this single value is the caller's job (see
    /// <c>DownloadQueueService</c>).
    /// </param>
    /// <param name="containerFormat">
    /// yt-dlp's own `--merge-output-format` value ("mp4" or "mkv") — must match
    /// <paramref name="destinationPath"/>'s own extension, since yt-dlp names its merged output
    /// after the destination it was given regardless of this flag; this only controls which muxer
    /// it actually uses. Defaults to "mp4" to match this method's behavior before the option
    /// existed.
    /// </param>
    public async Task DownloadAsync(
        string url,
        string formatSelector,
        string destinationPath,
        int expectedSegmentCount = 1,
        int? rateLimitKBps = null,
        string containerFormat = "mp4",
        IProgress<YtDlpDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var arguments = new List<string>
        {
            "-f", formatSelector,
            "--merge-output-format", containerFormat,
            "--newline",
            "--no-mtime",
            "-o", destinationPath,
        };

        if (rateLimitKBps is > 0)
        {
            arguments.Add("--limit-rate");
            arguments.Add($"{rateLimitKBps.Value}K");
        }

        // See GetVideoInfoAsync's doc comment for why "--" has to precede url here too.
        arguments.Add("--");
        arguments.Add(url);

        using var process = CreateProcess(arguments);

        try
        {
            StartProcess(process);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Could not run yt-dlp. Make sure it's installed and available on PATH.", ex);
        }

        var stderr = new StringBuilder();
        var stderrTask = DrainAsync(process.StandardError, stderr, cancellationToken);
        var stdoutTask = DrainStdOutForProgressAsync(process.StandardOutput, expectedSegmentCount, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // yt-dlp itself exiting is what actually determines success/failure — don't keep
        // blocking on the stdout/stderr pipes reaching EOF past this point. yt-dlp's merge step
        // spawns ffmpeg as a child, which inherits those same redirected handles; if anything
        // about it lingers even briefly past yt-dlp's own exit (observed in practice — a
        // download whose final file was already complete on disk still sat "Active" forever),
        // the pipe's write end stays open and ReadLineAsync/DrainAsync would otherwise wait for
        // an EOF that never comes. A short grace period still lets an already-finished drain
        // collect whatever's left in the buffer without reintroducing that hang.
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(2)))
            .ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed: {ExtractErrorSummary(stderr.ToString())}");

        // The Completed status this leads to in DownloadQueueService hides progress/size display
        // entirely (see DownloadQueueItem.ShowProgress), so there's no need to preserve the last
        // known byte counts here — a bare 100% is enough for whatever briefly reads this.
        progress?.Report(new YtDlpDownloadProgress(1.0, null, null));
    }

    /// <summary>
    /// Parses yt-dlp's `--newline`d stdout for progress, run as its own background task (like
    /// <see cref="DrainAsync"/> already drains stderr) rather than a loop <see cref="DownloadAsync"/>
    /// blocks on directly — see that method's comment on why blocking on this reaching EOF was the
    /// actual bug being fixed.
    /// </summary>
    private static async Task DrainStdOutForProgressAsync(
        StreamReader reader,
        int expectedSegmentCount,
        IProgress<YtDlpDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalFiles = Math.Max(expectedSegmentCount, 1);
        var fileIndex = 0;
        var seenFirstDestination = false;

        // Bytes already fully accounted for by streams earlier than the one currently downloading
        // (added in once a later stream's own "Destination"/"Resuming" line shows up, since that's
        // when the earlier stream's own total is final) — see YtDlpDownloadProgress's doc comment
        // for why the running total only grows as later streams start, rather than reflecting the
        // full combined size from the very first line.
        long completedStreamsBytes = 0;
        long? currentStreamTotalBytes = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.StartsWith("[download] Destination:", StringComparison.Ordinal) ||
                line.StartsWith("[download] Resuming download", StringComparison.Ordinal))
            {
                // Each new "Destination"/"Resuming" line marks the start of the next stream
                // (video, then audio) — everything before it is done, so bump the file index.
                if (seenFirstDestination)
                {
                    fileIndex = Math.Min(fileIndex + 1, totalFiles - 1);
                    completedStreamsBytes += currentStreamTotalBytes ?? 0;
                    currentStreamTotalBytes = null;
                }
                seenFirstDestination = true;
                continue;
            }

            if (TryParseProgressPercent(line, out var percent))
            {
                if (TryParseTotalBytes(line) is { } lineTotalBytes)
                    currentStreamTotalBytes = lineTotalBytes;

                var overall = (fileIndex + percent / 100.0) / totalFiles;

                long? downloadedBytes = null;
                long? totalBytesSoFar = null;
                if (currentStreamTotalBytes is { } streamTotal)
                {
                    downloadedBytes = completedStreamsBytes + (long)(streamTotal * (percent / 100.0));
                    totalBytesSoFar = completedStreamsBytes + streamTotal;
                }

                progress?.Report(new YtDlpDownloadProgress(Math.Clamp(overall, 0.0, 1.0), downloadedBytes, totalBytesSoFar));
            }
        }
    }

    /// <summary>Internal (not private) so Yoink.Tests can feed it sample yt-dlp JSON directly.</summary>
    internal static YtDlpVideoInfo ParseVideoInfo(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
        var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? id : id;
        var webpageUrl = root.TryGetProperty("webpage_url", out var webpageProp) ? webpageProp.GetString() ?? string.Empty : string.Empty;

        var formats = new List<YtDlpFormat>();
        if (root.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                var formatId = format.TryGetProperty("format_id", out var formatIdProp) ? formatIdProp.GetString() : null;
                if (string.IsNullOrEmpty(formatId))
                    continue;

                formats.Add(new YtDlpFormat(
                    formatId,
                    GetOptionalString(format, "ext"),
                    GetOptionalInt(format, "height"),
                    GetOptionalString(format, "vcodec"),
                    GetOptionalString(format, "acodec"),
                    GetOptionalLong(format, "filesize") ?? GetOptionalLong(format, "filesize_approx")));
            }
        }

        return new YtDlpVideoInfo(id, title, webpageUrl, formats);
    }

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetOptionalInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static long? GetOptionalLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private async Task<string> RunForStdOutAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(arguments);

        try
        {
            StartProcess(process);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Could not run yt-dlp. Make sure it's installed and available on PATH.", ex);
        }

        var stdErr = new StringBuilder();
        var stdErrTask = DrainAsync(process.StandardError, stdErr, cancellationToken);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed: {ExtractErrorSummary(stdErr.ToString())}");

        return stdOut;
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder into, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            into.AppendLine(line);
    }

    /// <summary>Internal (not private) so Yoink.Tests can exercise it directly.</summary>
    internal static string ExtractErrorSummary(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errorLine = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        return errorLine ?? lines.LastOrDefault() ?? "unknown error.";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have already exited between the check and the kill.
        }
    }

    private Process CreateProcess(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Added ahead of every caller's own arguments (never after — everything past their trailing
        // "--" is positional to yt-dlp, so a flag placed there would be treated as a URL instead).
        if (!string.IsNullOrEmpty(_ffmpegDirectory))
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(_ffmpegDirectory);
        }

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return new Process { StartInfo = startInfo };
    }

    /// <summary>
    /// Starts <paramref name="process"/> and immediately closes its stdin. Without this, yt-dlp (and
    /// any child it spawns, e.g. ffmpeg for merging) inherits Yoink's own stdin — a real, open,
    /// never-EOF stream whenever Yoink itself is run from a terminal or an IDE's run console (as this
    /// repo's own `dotnet run --project Yoink` workflow does). If yt-dlp or ffmpeg ever blocks on a
    /// stdin read for any reason (an interactive confirmation, say), it then hangs forever with
    /// nobody watching that terminal to answer it — observed in practice as an item whose file had
    /// already finished downloading (no leftover `.part`) but which never left its `Active` status,
    /// because <c>WaitForExitAsync</c> was still waiting on a yt-dlp process that itself never
    /// exited. Closing stdin up front means any such read gets an instant EOF instead, so the tool
    /// fails fast rather than blocking.
    /// </summary>
    private static void StartProcess(Process process)
    {
        process.Start();
        process.StandardInput.Close();
    }
}
