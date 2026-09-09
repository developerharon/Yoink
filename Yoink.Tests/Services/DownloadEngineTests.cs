using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Exercises DownloadEngine's pure segment-planning/filename logic directly (no network involved),
/// plus its actual segmented-download, resume, and rate-limiting behavior against a real local HTTP
/// server (a plain <see cref="HttpListener"/> on loopback) rather than a mock — matching this suite's
/// existing preference for exercising real behavior wherever a fake would just be re-testing the
/// fake (see e.g. DownloadQueueServiceTests' real SQLite file, ClipboardWatcherServiceTests' real
/// poll loop).
/// </summary>
public class DownloadEngineTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1024, 4)]
    [InlineData(4_999_999, 4)] // just under the 5MB-per-segment floor
    public void PlanSegments_StaysSingleSegment_BelowSplitThreshold(long totalBytes, int maxConnections)
    {
        var segments = DownloadEngine.PlanSegments(totalBytes, maxConnections);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
    }

    [Fact]
    public void PlanSegments_SplitsIntoContiguousGapFreeRanges()
    {
        const long totalBytes = 24_000_000; // comfortably above 4x the 5 MiB-per-segment floor
        var segments = DownloadEngine.PlanSegments(totalBytes, maxConnections: 4);

        Assert.Equal(4, segments.Count);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(totalBytes - 1, segments[^1].End);

        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
                Assert.Equal(segments[i - 1].End + 1, segments[i].Start); // no gap, no overlap
            Assert.True(segments[i].End >= segments[i].Start);
        }

        Assert.Equal(totalBytes, segments.Sum(s => s.End - s.Start + 1));
    }

    [Fact]
    public void PlanSegments_NeverExceedsMaxConnections()
    {
        var segments = DownloadEngine.PlanSegments(totalBytes: 1_000_000_000, maxConnections: 4);
        Assert.True(segments.Count <= 4);
    }

    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=report.pdf", "report.pdf")]
    public void TryGetFileNameFromContentDisposition_ParsesFileName(string headerValue, string expected)
    {
        var header = ContentDispositionHeaderValue.Parse(headerValue);
        Assert.Equal(expected, DownloadEngine.TryGetFileNameFromContentDisposition(header));
    }

    [Fact]
    public void TryGetFileNameFromContentDisposition_ReturnsNull_WhenAbsent()
    {
        Assert.Null(DownloadEngine.TryGetFileNameFromContentDisposition(null));
    }

    [Theory]
    [InlineData("https://github.com/developerharon/Yoink/releases/download/v0.1.0/Yoink.AppImage", "Yoink.AppImage")]
    [InlineData("https://example.com/files/report%20final.pdf", "report final.pdf")]
    [InlineData("https://example.com/", "download")]
    public void GetFileNameFromUri_DerivesFromLastPathSegment(string url, string expected)
    {
        Assert.Equal(expected, DownloadEngine.GetFileNameFromUri(new Uri(url)));
    }

    [Fact]
    public async Task DownloadAsync_Segmented_ProducesByteForByteMatch()
    {
        var content = RandomBytes(6 * 1024 * 1024); // above the 5MB-per-segment floor, so this actually splits
        using var server = new RangeSupportingTestServer(content);
        using var engine = new DownloadEngine();

        var destination = TempPath("segmented");
        try
        {
            double? lastFraction = null;
            var progress = new Progress<DownloadEngineProgress>(p => lastFraction = p.Fraction);

            await engine.DownloadAsync(server.Uri, destination, progress, maxConnections: 4);

            Assert.True(server.SawRangeRequest, "expected at least one ranged request against a range-capable server");
            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            // Individual callbacks can arrive out of send-order across concurrent segments (Progress<T>
            // posts each report independently), so only the final file content is asserted exactly —
            // this just confirms progress was reported at all, at some plausible value.
            Assert.True(lastFraction is > 0 and <= 1.0);
        }
        finally
        {
            CleanUp(destination);
        }
    }

    /// <summary>
    /// The IDM-style segmented-progress-bar visualization (Controls.SegmentedProgressBar) added in
    /// direct response to user feedback that a segmented download's queue row gave no visible sign
    /// it was actually splitting across connections — this is the DownloadEngine half of that: real
    /// per-segment progress genuinely reaches a progress subscriber, not just the aggregate fraction.
    /// 20MB (well above the 5MB-per-segment floor — unlike the 6MB used above, which
    /// PlanSegmentsTests confirms integer-divides down to a single segment) with maxConnections: 4
    /// guarantees actual multi-segment splitting, not just the segmented code path with one segment.
    /// </summary>
    [Fact]
    public async Task DownloadAsync_Segmented_ReportsPerSegmentProgress()
    {
        var content = RandomBytes(20 * 1024 * 1024);
        using var server = new RangeSupportingTestServer(content);
        using var engine = new DownloadEngine();

        var destination = TempPath("segmented-progress");
        try
        {
            IReadOnlyList<DownloadSegmentProgress>? lastSegments = null;
            var progress = new Progress<DownloadEngineProgress>(p =>
            {
                if (p.Segments is not null)
                    lastSegments = p.Segments;
            });

            await engine.DownloadAsync(server.Uri, destination, progress, maxConnections: 4);

            Assert.NotNull(lastSegments);
            Assert.Equal(4, lastSegments!.Count);
            // The final report (DownloadSegmentedAsync's own unthrottled "everything's done" flush)
            // is guaranteed to have landed last regardless of how the throttled ticks in between
            // happened to interleave, so every segment should read as fully complete here.
            Assert.All(lastSegments, s => Assert.Equal(1.0, s.Fraction));
            // Every byte accounted for exactly once, in order, with no gaps or overlaps.
            Assert.Equal(0, lastSegments[0].StartByte);
            Assert.Equal(content.Length - 1, lastSegments[^1].EndByte);
            for (var i = 1; i < lastSegments.Count; i++)
                Assert.Equal(lastSegments[i - 1].EndByte + 1, lastSegments[i].StartByte);
        }
        finally
        {
            CleanUp(destination);
        }
    }

    [Fact]
    public async Task DownloadAsync_ResumesOnlyTheUnfinishedPortion_AfterCancellation()
    {
        var content = RandomBytes(6 * 1024 * 1024);
        using var server = new RangeSupportingTestServer(content);
        using var engine = new DownloadEngine();

        var destination = TempPath("resume");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(120)); // comfortably inside the ~240ms+ a full transfer takes given the server's per-chunk delay below

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.DownloadAsync(server.Uri, destination, maxConnections: 4, cancellationToken: cts.Token));

            Assert.True(File.Exists(destination + ".partial"));
            Assert.True(File.Exists(destination + ".partial.segments.json"));

            var bytesRequestedBeforeResume = server.TotalRequestedBytes;

            await engine.DownloadAsync(server.Uri, destination, maxConnections: 4);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial.segments.json"));

            // The resumed call should only have re-requested whatever each segment still had left,
            // not the whole file again — proves the sidecar's per-segment progress was actually used.
            var bytesRequestedByResume = server.TotalRequestedBytes - bytesRequestedBeforeResume;
            Assert.True(bytesRequestedByResume < content.Length, $"expected the resume to request less than the full {content.Length} bytes, but it requested {bytesRequestedByResume}");
        }
        finally
        {
            CleanUp(destination);
        }
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToSequential_WhenServerIgnoresRangeRequests()
    {
        var content = RandomBytes(1024 * 1024);
        using var server = new NonRangeTestServer(content);
        using var engine = new DownloadEngine();

        var destination = TempPath("norange");
        try
        {
            await engine.DownloadAsync(server.Uri, destination, maxConnections: 4);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial.segments.json")); // sequential path never writes one
        }
        finally
        {
            CleanUp(destination);
        }
    }

    [Fact]
    public async Task DownloadAsync_RateLimit_MeasurablySlowsTheDownload()
    {
        var content = RandomBytes(400 * 1024);
        using var unlimitedServer = new NonRangeTestServer(content);
        using var limitedServer = new NonRangeTestServer(content);
        using var engine = new DownloadEngine();

        var destA = TempPath("rate-unlimited");
        var destB = TempPath("rate-limited");
        try
        {
            var swUnlimited = System.Diagnostics.Stopwatch.StartNew();
            await engine.DownloadAsync(unlimitedServer.Uri, destA);
            swUnlimited.Stop();

            var swLimited = System.Diagnostics.Stopwatch.StartNew();
            await engine.DownloadAsync(limitedServer.Uri, destB, rateLimitKBps: 40); // ~10s worth of budget for 400KB
            swLimited.Stop();

            Assert.True(
                swLimited.Elapsed > swUnlimited.Elapsed * 2,
                $"expected the rate-limited run ({swLimited.Elapsed}) to take meaningfully longer than the unlimited one ({swUnlimited.Elapsed})");
        }
        finally
        {
            CleanUp(destA);
            CleanUp(destB);
        }
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(12345).NextBytes(bytes);
        return bytes;
    }

    private static string TempPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"yoink-engine-test-{label}-{Guid.NewGuid():N}");

    private static void CleanUp(string destination)
    {
        foreach (var path in new[] { destination, destination + ".partial", destination + ".partial.segments.json" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort test cleanup.
            }
        }
    }
}

/// <summary>
/// Base for the two fake servers below: a real loopback HTTP listener serving a fixed in-memory byte
/// buffer at "/file.bin", so DownloadEngine's actual HTTP behavior (range requests, resume, rate
/// limiting) gets exercised against a real socket rather than an abstraction over one.
/// </summary>
internal abstract class TestHttpServer : IDisposable
{
    protected readonly byte[] Content;
    private readonly HttpListener _listener;
    private bool _disposed;
    private long _totalRequestedBytes;

    public Uri Uri { get; }

    /// <summary>Total bytes this server has been asked to send across every request so far — resume tests use this to confirm a resumed download re-requested less than the whole file.</summary>
    public long TotalRequestedBytes => Interlocked.Read(ref _totalRequestedBytes);

    public bool SawRangeRequest { get; private set; }

    protected TestHttpServer(byte[] content)
    {
        Content = content;

        var port = GetFreeLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        Uri = new Uri(prefix + "file.bin");

        _ = Task.Run(AcceptLoopAsync);
    }

    protected abstract Task RespondAsync(HttpListenerContext context, string? rangeHeader);

    protected void RecordRequestedBytes(long bytes) => Interlocked.Add(ref _totalRequestedBytes, bytes);

    protected void MarkSawRange() => SawRangeRequest = true;

    private async Task AcceptLoopAsync()
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped/disposed
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await RespondAsync(context, context.Request.Headers["Range"]).ConfigureAwait(false);
        }
        catch
        {
            // The client (a canceled segment, in the resume test) disconnected mid-response — nothing to do.
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _listener.Stop();
        _listener.Close();
    }
}

/// <summary>
/// Honors Range requests (206 + Content-Range) the way a real CDN/file host does. Writes each
/// response in small chunks with a short delay between them — real loopback transfers a few MB
/// fast enough that a cancellation test would otherwise race the download to completion; this keeps
/// a mid-transfer cancel reliably reproducible regardless of the machine running the test.
/// </summary>
internal sealed class RangeSupportingTestServer(byte[] content) : TestHttpServer(content)
{
    private const int ChunkSize = 32768;
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(5);

    protected override async Task RespondAsync(HttpListenerContext context, string? rangeHeader)
    {
        if (rangeHeader is not null && TryParseRange(rangeHeader, Content.Length, out var start, out var end))
        {
            MarkSawRange();
            var slice = Content.AsMemory((int)start, (int)(end - start + 1));
            RecordRequestedBytes(slice.Length);

            context.Response.StatusCode = 206;
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{Content.Length}";
            context.Response.ContentLength64 = slice.Length;

            for (var offset = 0; offset < slice.Length; offset += ChunkSize)
            {
                var chunkLength = Math.Min(ChunkSize, slice.Length - offset);
                await context.Response.OutputStream.WriteAsync(slice.Slice(offset, chunkLength)).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                await Task.Delay(ChunkDelay).ConfigureAwait(false);
            }
        }
        else
        {
            RecordRequestedBytes(Content.Length);
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = Content.Length;
            await context.Response.OutputStream.WriteAsync(Content).ConfigureAwait(false);
        }

        context.Response.OutputStream.Close();
    }

    private static bool TryParseRange(string header, int totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;

        var value = header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) ? header["bytes=".Length..] : header;
        var parts = value.Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out start))
            return false;

        if (!string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out var parsedEnd))
            end = parsedEnd;

        return true;
    }
}

/// <summary>Ignores Range headers entirely and always serves the whole body with 200 — exercises DownloadEngine's single-connection fallback path.</summary>
internal sealed class NonRangeTestServer(byte[] content) : TestHttpServer(content)
{
    protected override async Task RespondAsync(HttpListenerContext context, string? rangeHeader)
    {
        RecordRequestedBytes(Content.Length);
        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = Content.Length;
        await context.Response.OutputStream.WriteAsync(Content).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }
}
