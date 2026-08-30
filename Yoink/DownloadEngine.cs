using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink;

/// <summary>
/// The core download engine: a generic, resumable single-file HTTP downloader that everything
/// else (YouTube streams today, arbitrary direct links later) is meant to sit on top of. This is
/// step 1 of the roadmap in README.md — get plain HTTP downloads rock solid (range-request
/// resume, progress, retry, cancellation) before any source-specific extraction logic depends on
/// it.
///
/// Downloads are written to "&lt;destinationPath&gt;.partial" and only moved into place once
/// complete, so a half-finished file never looks like a finished one. If the partial file already
/// exists on a later call for the same destination, it's resumed via an HTTP Range request rather
/// than restarted, provided the server honors it (falls back to a full restart otherwise).
/// </summary>
public sealed class DownloadEngine : IDisposable
{
    private const string PartialFileSuffix = ".partial";
    private const int BufferSize = 81920;

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
    /// prior partial download when one is found. Reports fractional progress (0.0-1.0) when the
    /// server reports a content length; otherwise progress simply isn't reported. Retries
    /// transient failures up to <see cref="MaxRetries"/> times, reusing whatever bytes the
    /// previous attempt already wrote. A caller-requested cancellation is never retried — the
    /// partial file is left on disk so a later call can pick up where it left off.
    /// </summary>
    public async Task DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var partialPath = destinationPath + PartialFileSuffix;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadAttemptAsync(sourceUri, partialPath, progress, cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, destinationPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller asked us to stop, not a transient failure — don't retry, and leave the
                // partial file in place so the next call resumes instead of starting over.
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
        IProgress<double>? progress,
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
            // We asked to resume but the server ignored the Range header and sent the full body
            // (200 OK) instead of 206 Partial Content — restart from scratch rather than
            // appending a fresh full file onto the bytes we already have.
            resumeFrom = 0;
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = isResuming
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partialPath,
            isResuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[BufferSize];
        var totalRead = resumeFrom;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes is > 0)
                progress?.Report((double)totalRead / totalBytes.Value);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
