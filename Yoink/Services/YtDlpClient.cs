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
            await WaitForExitOrStallAsync(process, new ActivityTracker(), DefaultStallTimeout, cancellationToken)
                .ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (TimeoutException)
        {
            // A hung "--version" call is just as much a "not usable" signal as one that never
            // started at all — see WaitForExitOrStallAsync.
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
    /// <param name="stallTimeout">
    /// Defense-in-depth backstop, independent of <see cref="CreateProcess"/>'s stdin fix: if yt-dlp
    /// (or a child it spawns, e.g. ffmpeg during a merge) produces zero stdout/stderr output for this
    /// long without exiting, it's killed and this throws <see cref="TimeoutException"/> rather than
    /// hanging forever — see <see cref="WaitForExitOrStallAsync"/>. Null means
    /// <see cref="DefaultStallTimeout"/>; only overridden by Yoink.Tests, which need a much shorter
    /// value to exercise this without a multi-minute test run.
    /// </param>
    public async Task DownloadAsync(
        string url,
        string formatSelector,
        string destinationPath,
        int expectedSegmentCount = 1,
        int? rateLimitKBps = null,
        string containerFormat = "mp4",
        IProgress<YtDlpDownloadProgress>? progress = null,
        TimeSpan? stallTimeout = null,
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

        var activity = new ActivityTracker();
        var stderr = new StringBuilder();
        var stderrTask = DrainAsync(process.StandardError, stderr, cancellationToken, activity.Mark);
        var stdoutTask = DrainStdOutForProgressAsync(process.StandardOutput, expectedSegmentCount, progress, cancellationToken, activity.Mark);

        await WaitForExitOrStallAsync(process, activity, stallTimeout ?? DefaultStallTimeout, cancellationToken)
            .ConfigureAwait(false);

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
        CancellationToken cancellationToken,
        Action? onLine = null)
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
            onLine?.Invoke();

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

        // stdout has no per-line activity signal here (ReadToEndAsync only completes once fully
        // read, unlike DownloadAsync's line-by-line drain) — stderr-only activity tracking is a
        // best-effort backstop for this call specifically. yt-dlp's metadata/`--dump-json` calls
        // never spawn ffmpeg, so they don't carry the lingering-child-holds-the-pipe-open risk
        // DownloadAsync's grace period exists for; a stall here almost certainly means yt-dlp
        // itself is stuck (e.g. a network hang), which the watchdog below still catches.
        var activity = new ActivityTracker();
        var stdErr = new StringBuilder();
        var stdErrTask = DrainAsync(process.StandardError, stdErr, cancellationToken, activity.Mark);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await WaitForExitOrStallAsync(process, activity, DefaultStallTimeout, cancellationToken)
            .ConfigureAwait(false);

        var stdOut = await stdOutTask.ConfigureAwait(false);
        await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed: {ExtractErrorSummary(stdErr.ToString())}");

        return stdOut;
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder into, CancellationToken cancellationToken, Action? onLine = null)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            into.AppendLine(line);
            onLine?.Invoke();
        }
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

    /// <summary>
    /// How long a yt-dlp invocation can produce zero stdout/stderr output before
    /// <see cref="WaitForExitOrStallAsync"/> gives up on it and kills it. This is a backstop, not the
    /// primary fix, for a real, confirmed bug: an item whose file had already finished downloading
    /// (no leftover `.part`) sat "Active" forever because yt-dlp's own process never exited —
    /// <c>WaitForExitAsync</c> was still waiting on a process that itself was hung, most likely on a
    /// blocked stdin read (see <see cref="CreateProcess"/>'s stdin redirect, the primary fix for
    /// that). This exists so *any other* cause of the same symptom still surfaces as a clear,
    /// actionable failure instead of the item sitting stuck forever with no way out but a manual
    /// cancel. Five minutes comfortably covers a legitimate silent stretch (e.g. ffmpeg re-encoding
    /// a large file during a merge print nothing until it's done) while still being short enough
    /// that a genuinely hung download doesn't strand a user indefinitely.
    /// </summary>
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Tracks the last time any stdout/stderr line arrived, for <see cref="WaitForExitOrStallAsync"/>.</summary>
    private sealed class ActivityTracker
    {
        private long _lastActivityTicks = Environment.TickCount64;

        public void Mark() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

        public TimeSpan IdleFor(long nowTicks) =>
            TimeSpan.FromMilliseconds(nowTicks - Interlocked.Read(ref _lastActivityTicks));
    }

    /// <summary>
    /// Pulled out purely so the stall/no-stall decision is testable without spawning a process or
    /// waiting out a real timeout — same reasoning as <see cref="DownloadQueueService.IsWithinWindow"/>.
    /// </summary>
    internal static bool IsStalled(TimeSpan idleFor, TimeSpan stallTimeout) => idleFor >= stallTimeout;

    /// <summary>
    /// Waits for <paramref name="process"/> to exit, same as a plain <c>WaitForExitAsync</c>, except
    /// it also kills the process (and throws <see cref="TimeoutException"/> instead of waiting
    /// forever) if <paramref name="activity"/> reports no stdout/stderr output at all for longer than
    /// <paramref name="stallTimeout"/> — see <see cref="DefaultStallTimeout"/>'s doc comment for why
    /// this exists. A genuine external cancellation (<paramref name="cancellationToken"/> itself
    /// firing) still behaves exactly as before: kill the process and rethrow
    /// <see cref="OperationCanceledException"/>, letting callers tell the two apart (a user-requested
    /// pause/cancel is not a stall).
    /// </summary>
    private static async Task WaitForExitOrStallAsync(
        Process process, ActivityTracker activity, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stalled = 0;

        // Scales down with a short stallTimeout so Yoink.Tests can exercise this in well under a
        // second rather than waiting out a real multi-minute poll interval; production's 5-minute
        // default clamps to the 10s ceiling.
        var pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(stallTimeout.TotalMilliseconds / 5.0, 50, 10_000));

        async Task WatchForStallAsync()
        {
            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                    if (process.HasExited)
                        return;

                    if (IsStalled(activity.IdleFor(Environment.TickCount64), stallTimeout))
                    {
                        Interlocked.Exchange(ref stalled, 1);
                        TryKill(process);
                        watchdogCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // cancellationToken itself fired — WaitForExitAsync below is already unwinding.
            }
        }

        var watchdogTask = WatchForStallAsync();

        try
        {
            try
            {
                await process.WaitForExitAsync(watchdogCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref stalled) == 0)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                // The stall watchdog already killed the process and canceled watchdogCts — the
                // TimeoutException below is thrown regardless of whether that race lets this
                // exception surface here or WaitForExitAsync instead just observes the kill as a
                // normal exit (see the check right after this try/catch for why it isn't only here).
            }
        }
        finally
        {
            // Best-effort — the watchdog loop notices process.HasExited on its own next poll tick at
            // the latest, so this is just tidying up, not something the method needs to block on.
            await Task.WhenAny(watchdogTask, Task.Delay(TimeSpan.FromMilliseconds(50))).ConfigureAwait(false);
        }

        // Killing the process (TryKill, above) and canceling watchdogCts happen as two separate
        // statements, so WaitForExitAsync can win the race and observe the kill as a plain, immediate
        // exit rather than the cancellation — confirmed by a real test flake, not just theorized.
        // Checking the flag unconditionally here (rather than only in the catch above) means a stall
        // is reported as a TimeoutException either way, instead of falling through to DownloadAsync's
        // own "non-zero exit code" handling and getting misreported as an ordinary yt-dlp failure.
        if (Volatile.Read(ref stalled) == 1)
        {
            throw new TimeoutException(
                $"yt-dlp produced no output for {stallTimeout} and appeared to be hung, so it was killed.");
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
