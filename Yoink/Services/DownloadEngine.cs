using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

/// <summary>
/// One progress update from <see cref="DownloadEngine.DownloadAsync"/> — mirrors
/// <see cref="YtDlpDownloadProgress"/>'s shape (fraction + bytes) so <c>DownloadQueueItem</c>'s
/// existing <c>SizeText</c>/<c>ShowSize</c> presentation logic works unmodified for either download
/// path. Unlike that type, <paramref name="BytesDownloaded"/> is never null here — this class always
/// knows exactly how many bytes it has itself written, whether or not the server ever told it a
/// total.
/// </summary>
public readonly record struct DownloadEngineProgress(double Fraction, long BytesDownloaded, long? TotalBytes);

/// <summary>
/// The core download engine: a generic, resumable HTTP downloader everything else (YouTube streams
/// via yt-dlp, arbitrary direct links here) is meant to sit on top of — see
/// <c>Services.DownloadQueueService</c> for where the "arbitrary direct link" half now plugs in.
///
/// Downloads are written to "&lt;destinationPath&gt;.partial" and only moved into place once
/// complete, so a half-finished file never looks like a finished one — same invariant
/// <c>YtDlpClient</c>'s own downloads already follow.
///
/// <b>Segmented downloads</b>: when the server both reports a size and honors HTTP Range requests
/// (checked via <see cref="ProbeAsync"/>, a single 1-byte ranged GET rather than a HEAD request,
/// since not every server implements HEAD), the file is split into up to <c>maxConnections</c>
/// concurrent range requests, each writing directly into its own slice of the preallocated
/// <c>.partial</c> file via <see cref="RandomAccess"/> — positional I/O, so multiple segment tasks
/// can safely write to different offsets of the same file handle at once with no shared file-pointer
/// race. A small "&lt;destinationPath&gt;.partial.segments.json" sidecar records each segment's own
/// start/end/downloaded-so-far, which is the only way resuming after a pause can know how far each
/// individual segment got — checking the partial file's overall length the way the old single-stream
/// version did doesn't work once segments are writing out of order into non-contiguous offsets. When
/// the server can't do either (no Content-Length, or a Range request comes back as a plain 200), this
/// falls back to the original single-connection sequential-append behavior, no sidecar file needed.
///
/// A single logical download attempt (whether segmented or sequential) is one unit for
/// <see cref="MaxRetries"/>/<see cref="RetryDelay"/> — a failed attempt simply re-enters
/// <see cref="DownloadAttemptAsync"/>, which reads back whatever the sidecar/partial file already
/// has and only re-fetches what's still missing, so a transient failure partway through a segmented
/// download doesn't throw away the segments that already finished.
/// </summary>
public sealed class DownloadEngine : IDisposable
{
    private const string PartialFileSuffix = ".partial";
    private const string SegmentMetadataSuffix = ".partial.segments.json";
    private const int BufferSize = 81920;

    /// <summary>
    /// Below this many bytes, splitting into multiple connections isn't worth the extra
    /// connection/handshake overhead relative to what it'd actually save — an arbitrary but
    /// reasonable floor, not derived from any measurement.
    /// </summary>
    private const long MinBytesPerSegment = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public DownloadEngine() : this(new HttpClient())
    {
        _ownsHttpClient = true;
    }

    public DownloadEngine(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Number of attempts before giving up, including the first. Defaults to 3.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base delay between retries; attempt N waits N times this long.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Downloads <paramref name="sourceUri"/> to <paramref name="destinationPath"/>, resuming a
    /// prior partial download when one is found and splitting across up to
    /// <paramref name="maxConnections"/> simultaneous connections when the server supports it (see
    /// the class doc comment). <paramref name="maxConnections"/> is a parameter rather than a
    /// constructor setting so a caller (<c>DownloadQueueService</c>) can pass whatever the live
    /// settings say at the moment this particular download actually starts, the same way
    /// <paramref name="rateLimitKBps"/> already works. Retries transient failures up to
    /// <see cref="MaxRetries"/> times, reusing whatever bytes previous attempts already wrote. A
    /// caller-requested cancellation is never retried — all progress made so far is left on disk
    /// (partial file plus, for a segmented download, its sidecar) so a later call resumes instead of
    /// starting over.
    /// </summary>
    public async Task DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<DownloadEngineProgress>? progress = null,
        int? rateLimitKBps = null,
        int maxConnections = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var partialPath = destinationPath + PartialFileSuffix;
        var metadataPath = destinationPath + SegmentMetadataSuffix;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadAttemptAsync(
                    sourceUri, partialPath, metadataPath, progress, rateLimitKBps, Math.Max(1, maxConnections),
                    cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, destinationPath, overwrite: true);
                DeleteMetadataFile(metadataPath);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadAttemptAsync(
        Uri sourceUri,
        string partialPath,
        string metadataPath,
        IProgress<DownloadEngineProgress>? progress,
        int? rateLimitKBps,
        int maxConnections,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(sourceUri, cancellationToken).ConfigureAwait(false);

        if (!probe.SupportsRanges || probe.TotalBytes is not > 0 || maxConnections <= 1)
        {
            await DownloadSequentialAsync(sourceUri, partialPath, probe.TotalBytes, progress, rateLimitKBps, cancellationToken)
                .ConfigureAwait(false);
            DeleteMetadataFile(metadataPath);
            return;
        }

        await DownloadSegmentedAsync(
            sourceUri, partialPath, metadataPath, probe.TotalBytes.Value, maxConnections, progress, rateLimitKBps,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The original single-connection behavior, used whenever the server can't support the segmented
    /// path (no length, or ignores Range requests). Resumes by simply appending to whatever the
    /// partial file already has — no sidecar needed, since with one connection "bytes already on
    /// disk" and "bytes already downloaded" are the same number.
    /// </summary>
    private async Task DownloadSequentialAsync(
        Uri sourceUri,
        string partialPath,
        long? knownTotalBytes,
        IProgress<DownloadEngineProgress>? progress,
        int? rateLimitKBps,
        CancellationToken cancellationToken)
    {
        var resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var isResuming = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !isResuming)
        {
            // Asked to resume but the server ignored the Range header and sent the full body (200
            // OK) instead of 206 Partial Content — restart rather than appending a fresh full body
            // onto bytes we already have.
            resumeFrom = 0;
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = isResuming
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength ?? knownTotalBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partialPath,
            isResuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var rateLimiter = new RateLimiter(rateLimitKBps);
        var buffer = new byte[BufferSize];
        var totalRead = resumeFrom;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            progress?.Report(new DownloadEngineProgress(
                totalBytes is > 0 ? (double)totalRead / totalBytes.Value : 0,
                totalRead,
                totalBytes));

            await rateLimiter.ThrottleAsync(bytesRead, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DownloadSegmentedAsync(
        Uri sourceUri,
        string partialPath,
        string metadataPath,
        long totalBytes,
        int maxConnections,
        IProgress<DownloadEngineProgress>? progress,
        int? rateLimitKBps,
        CancellationToken cancellationToken)
    {
        var metadata = LoadOrCreateMetadata(metadataPath, sourceUri, totalBytes, maxConnections);
        PreallocateFile(partialPath, metadata.TotalBytes);

        var rateLimiter = new RateLimiter(rateLimitKBps);
        var totalDownloaded = metadata.Segments.Sum(s => s.Downloaded);
        var progressLock = new object();

        void ReportProgress(long deltaBytes)
        {
            long snapshot;
            lock (progressLock)
            {
                totalDownloaded += deltaBytes;
                snapshot = totalDownloaded;
            }

            progress?.Report(new DownloadEngineProgress(
                metadata.TotalBytes > 0 ? (double)snapshot / metadata.TotalBytes : 0,
                snapshot,
                metadata.TotalBytes));
        }

        using var handle = File.OpenHandle(partialPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? firstFailure = null;

        var pending = metadata.Segments.Where(s => !s.IsComplete).ToList();
        var segmentTasks = pending.Select(segment => Task.Run(async () =>
        {
            try
            {
                await DownloadSegmentAsync(sourceUri, handle, segment, rateLimiter, ReportProgress, failureCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (pause/cancel) asked to stop — propagate so the caller sees it.
                throw;
            }
            catch (OperationCanceledException) when (failureCts.IsCancellationRequested)
            {
                // Stopped because a sibling segment failed for real, not this one's own doing —
                // that sibling's exception (captured below) is what actually matters.
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref firstFailure, ex, null);
                failureCts.Cancel();
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(segmentTasks).ConfigureAwait(false);
        }
        finally
        {
            // Persist per-segment progress unconditionally — success, a real failure, or a
            // pause/cancel all need the sidecar to reflect exactly how far each segment actually
            // got, so the next attempt resumes rather than restarting from scratch.
            SaveMetadata(metadataPath, metadata);
        }

        if (firstFailure is not null)
            throw firstFailure;

        cancellationToken.ThrowIfCancellationRequested();

        DeleteMetadataFile(metadataPath);
    }

    private async Task DownloadSegmentAsync(
        Uri sourceUri,
        SafeFileHandle handle,
        SegmentState segment,
        RateLimiter rateLimiter,
        Action<long> onBytesRead,
        CancellationToken cancellationToken)
    {
        var offset = segment.Start + segment.Downloaded;
        if (offset > segment.End)
            return; // already fully downloaded by a previous attempt

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(offset, segment.End);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            RandomAccess.Write(handle, buffer.AsSpan(0, bytesRead), offset);
            offset += bytesRead;
            segment.Downloaded += bytesRead;
            onBytesRead(bytesRead);

            await rateLimiter.ThrottleAsync(bytesRead, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Works out whether <paramref name="sourceUri"/> can be split into ranged connections, and
    /// picks up whatever filename/content-type the server is willing to volunteer along the way —
    /// <c>Views.AddDownloadDialog</c>'s generic-file flow reuses this same probe to show the user
    /// what they're about to download before it's queued (see that view's own notes). Uses a 1-byte
    /// ranged GET rather than HEAD: some servers don't implement HEAD at all (or answer it
    /// differently from GET), so a GET is the one request method every server has to answer
    /// correctly by definition — and with <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
    /// disposing the response immediately after reading the headers costs almost nothing even when
    /// the server ignores the Range header and starts sending the whole body.
    /// </summary>
    internal async Task<DownloadProbe> ProbeAsync(Uri sourceUri, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;
        var totalBytes = supportsRanges
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        var fileName = TryGetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
            ?? GetFileNameFromUri(response.RequestMessage?.RequestUri ?? sourceUri);

        return new DownloadProbe(supportsRanges, totalBytes, fileName, response.Content.Headers.ContentType?.MediaType);
    }

    // internal (not private) so Yoink.Tests can exercise these directly — pure string logic, no
    // network needed to verify them.
    internal static string? TryGetFileNameFromContentDisposition(ContentDispositionHeaderValue? contentDisposition)
    {
        var name = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        name = name.Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
    }

    internal static string GetFileNameFromUri(Uri uri)
    {
        var lastSegment = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
        var fileName = Uri.UnescapeDataString(lastSegment);
        return string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
    }

    /// <summary>
    /// Splits <paramref name="totalBytes"/> into up to <paramref name="maxConnections"/> contiguous,
    /// gap-free ranges, each at least <see cref="MinBytesPerSegment"/> — never more segments than
    /// that floor allows, and always at least one. Pure and internal purely so
    /// Yoink.Tests can verify the ranges line up correctly without any network involved.
    /// </summary>
    internal static IReadOnlyList<(long Start, long End)> PlanSegments(long totalBytes, int maxConnections)
    {
        if (totalBytes <= 0)
            return [(0, 0)];

        var segmentCount = (int)Math.Min(maxConnections, Math.Max(1, totalBytes / MinBytesPerSegment));
        if (segmentCount <= 1)
            return [(0, totalBytes - 1)];

        var baseSize = totalBytes / segmentCount;
        var segments = new List<(long Start, long End)>(segmentCount);
        var start = 0L;
        for (var i = 0; i < segmentCount; i++)
        {
            var end = i == segmentCount - 1 ? totalBytes - 1 : start + baseSize - 1;
            segments.Add((start, end));
            start = end + 1;
        }

        return segments;
    }

    private static DownloadMetadata LoadOrCreateMetadata(string metadataPath, Uri sourceUri, long totalBytes, int maxConnections)
    {
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = File.ReadAllText(metadataPath);
                var existing = JsonSerializer.Deserialize(json, DownloadMetadataJsonContext.Default.DownloadMetadata);
                if (existing is { Segments.Count: > 0 } m && m.SourceUri == sourceUri.ToString() && m.TotalBytes == totalBytes)
                    return m;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Corrupt/unreadable sidecar — fall through and start a fresh segment plan below
                // rather than failing the whole download over a resume-bookkeeping file.
            }
        }

        var ranges = PlanSegments(totalBytes, maxConnections);
        return new DownloadMetadata
        {
            SourceUri = sourceUri.ToString(),
            TotalBytes = totalBytes,
            Segments = ranges.Select(r => new SegmentState { Start = r.Start, End = r.End }).ToList()
        };
    }

    private static void SaveMetadata(string metadataPath, DownloadMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata, DownloadMetadataJsonContext.Default.DownloadMetadata);
        File.WriteAllText(metadataPath, json);
    }

    private static void DeleteMetadataFile(string metadataPath)
    {
        try
        {
            File.Delete(metadataPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a leftover sidecar next to a file that no longer has a matching
            // .partial is harmless; the next unrelated download to this same destination just won't
            // find a usable match (LoadOrCreateMetadata checks SourceUri/TotalBytes) and starts fresh.
        }
    }

    private static void PreallocateFile(string partialPath, long totalBytes)
    {
        using var stream = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        if (stream.Length != totalBytes)
            stream.SetLength(totalBytes);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>Result of <see cref="DownloadEngine.ProbeAsync"/> — what's known about a URL before committing to a download plan.</summary>
internal readonly record struct DownloadProbe(bool SupportsRanges, long? TotalBytes, string? FileName, string? ContentType);

/// <summary>One planned byte range of a segmented download, plus how far it's actually gotten — the unit <see cref="DownloadMetadataJsonContext"/> (de)serializes.</summary>
internal sealed class SegmentState
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore]
    public long Length => End - Start + 1;

    [JsonIgnore]
    public bool IsComplete => Downloaded >= Length;
}

/// <summary>
/// The "&lt;destinationPath&gt;.partial.segments.json" sidecar contents — see
/// <see cref="DownloadEngine"/>'s own doc comment for why a segmented download needs this at all.
/// <see cref="SourceUri"/>/<see cref="TotalBytes"/> are checked on load so a sidecar left over from a
/// different URL, or one that no longer matches what the server now reports for this URL, is
/// discarded rather than trusted.
/// </summary>
internal sealed class DownloadMetadata
{
    public string SourceUri { get; set; } = "";
    public long TotalBytes { get; set; }
    public List<SegmentState> Segments { get; set; } = [];
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="DownloadMetadata"/> — same
/// trim-safety reasoning as <c>SettingsService</c>'s own <c>AppSettingsJsonContext</c> (see that
/// class's doc comment): reflection-based <c>JsonSerializer.Serialize/Deserialize&lt;T&gt;</c> is
/// exactly what a trimmed publish (<c>PublishTrimmed</c> in <c>Yoink.csproj</c>) can't statically
/// prove is safe.
/// </summary>
[JsonSerializable(typeof(DownloadMetadata))]
internal sealed partial class DownloadMetadataJsonContext : JsonSerializerContext;

/// <summary>
/// A simple shared fixed-window rate limiter — every segment task of one download reports its bytes
/// through the same instance, so the combined throughput across every connection stays under the
/// configured cap rather than each connection independently getting the full limit. Coarse (a
/// straightforward per-second window, not a smooth token bucket) but adequate for the same purpose
/// yt-dlp's own <c>--limit-rate</c> already serves elsewhere in this app.
/// </summary>
internal sealed class RateLimiter(int? kbpsLimit)
{
    private readonly long? _bytesPerSecond = kbpsLimit is > 0 ? (long)kbpsLimit.Value * 1024 : null;
    private readonly object _sync = new();
    private long _bytesInWindow;
    private long _windowStartTicks = Environment.TickCount64;

    public async Task ThrottleAsync(int bytesJustTransferred, CancellationToken cancellationToken)
    {
        if (_bytesPerSecond is not { } limit)
            return;

        TimeSpan delay;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            var elapsed = now - _windowStartTicks;
            if (elapsed >= 1000)
            {
                _windowStartTicks = now;
                _bytesInWindow = 0;
                elapsed = 0;
            }

            _bytesInWindow += bytesJustTransferred;
            if (_bytesInWindow <= limit)
                return;

            delay = TimeSpan.FromMilliseconds(1000 - elapsed);
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
