using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Yoink.Models;

namespace Yoink.Services;

/// <summary>
/// The download queue & persistence layer (README roadmap step 3): a persisted queue of
/// pending/active/paused/completed/failed downloads, stored in SQLite (queue.db) alongside
/// settings.json in the user's per-user config directory, with pause/resume/cancel/retry/reorder
/// support. A background loop processes up to <see cref="AppSettings.MaxConcurrentDownloads"/>
/// items at once (README roadmap step 7), gated by the configured schedule window when scheduling
/// is on, each with a per-download speed cap derived from
/// <see cref="AppSettings.PerDownloadSpeedLimitKBps"/>/<see cref="AppSettings.GlobalSpeedLimitKBps"/>
/// — see <see cref="ProcessLoopAsync"/> and <see cref="ComputeRateLimitKBps"/> for exactly how.
/// Calls into <see cref="YtDlpClient"/> for resolution/download and reports progress through
/// <see cref="ItemChanged"/>.
///
/// This queue doubles as download history — it's never pruned, so completed/failed items stay
/// visible in <c>Views.MainWindow</c>'s queue view (roadmap step 4) rather than living in a
/// separate history store. There's no migration from the old history.json; that file is simply
/// unused now.
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Yoink",
        "queue.db");

    private readonly YtDlpClient _ytDlp;

    /// <summary>
    /// Owned by this service so its lifetime (and its <see cref="HttpClient"/>) matches the queue's
    /// own — every <see cref="DownloadKind.File"/> item's actual download goes through this one
    /// shared instance rather than a new engine per item.
    /// </summary>
    private readonly DownloadEngine _downloadEngine = new();

    /// <summary>
    /// Same "one shared instance, matches the queue's own lifetime" reasoning as
    /// <see cref="_downloadEngine"/> above — every <see cref="DownloadKind.Torrent"/> item shares one
    /// <see cref="Services.TorrentEngine"/> (and so one underlying MonoTorrent <c>ClientEngine</c>,
    /// listen port, and DHT node) rather than standing up a new one per item. Cache directory lives
    /// alongside the managed yt-dlp/ffmpeg folder <c>DependencyProvisioningService</c> already uses —
    /// same <c>%AppData%/Yoink/</c> root, its own subfolder — unless overridden by the constructor's
    /// own <c>torrentCacheDirectory</c> parameter, the same kind of override the constructor's
    /// <c>databasePath</c> parameter already has, and for the same reason: so Yoink.Tests never
    /// touches the real user's config directory (this class's constructor eagerly
    /// <see cref="Directory.CreateDirectory(string)"/>s it).
    /// </summary>
    private readonly TorrentEngine _torrentEngine;

    private readonly SqliteConnection _connection;

    // SQLite only ever allows one writer at a time regardless of how many connections you open —
    // opening a fresh one per call just adds lock-contention/retry overhead for no real
    // parallelism. A single shared connection, with every access serialized through this lock,
    // sidesteps that entirely: writes for concurrently-processing items (README roadmap step 7)
    // queue up briefly instead of contending for the SQLite file lock. Found the hard way — an
    // earlier per-call-connection version measurably serialized two "concurrent" downloads because
    // their status-write contention was slower than either download itself.
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _activeCancellations = new();
    // Every ProcessItemAsync task ProcessLoopAsync fires off, so Dispose can actually wait for
    // them — see Dispose's doc comment for why this is load-bearing, not redundant with
    // _processingLoop.
    private readonly ConcurrentDictionary<long, Task> _activeTasks = new();
    private readonly ConcurrentDictionary<long, bool> _pauseRequested = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DownloadQueueItem>> _waiters = new();
    private readonly Task _processingLoop;

    public DownloadQueueService(YtDlpClient ytDlp, string? databasePath = null, string? torrentCacheDirectory = null)
    {
        _ytDlp = ytDlp;
        _torrentEngine = new TorrentEngine(torrentCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yoink", "torrents"));

        var path = databasePath ?? DatabasePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        EnsureSchema();
        RecoverStaleActiveItems();

        _processingLoop = Task.Run(() => ProcessLoopAsync(_stoppingCts.Token));
    }

    /// <summary>Raised whenever an item's status or progress changes — a future queue view binds to this.</summary>
    public event Action<DownloadQueueItem>? ItemChanged;

    /// <summary>
    /// Raised after <see cref="DeleteAsync"/> actually removes a row — carries just the id, unlike
    /// <see cref="ItemChanged"/>'s full-snapshot payload, since there's no longer a row to snapshot.
    /// </summary>
    public event Action<long>? ItemRemoved;

    public Task<IReadOnlyList<DownloadQueueItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM download_queue ORDER BY Position ASC";

            var items = new List<DownloadQueueItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinals = ColumnOrdinals.FromReader(reader);
                do
                {
                    items.Add(ReadItem(reader, ordinals));
                } while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            return (IReadOnlyList<DownloadQueueItem>)items;
        }, cancellationToken);

    /// <summary>
    /// <paramref name="title"/> is optional — leave it blank and <see cref="ProcessItemAsync"/>
    /// resolves it itself via <see cref="YtDlpClient.GetVideoInfoAsync"/> once this item is
    /// actually picked up, same as before this parameter existed. Passing an already-resolved
    /// title (as <c>Views.AddDownloadDialog</c> does once it's fetched video info to build its own
    /// resolution/format picker) skips that redundant second yt-dlp call entirely, so the actual
    /// download starts the moment this item is dequeued instead of pausing to look the title up
    /// again first.
    /// </summary>
    /// <param name="infoJson">
    /// The same already-resolved video's <see cref="YtDlpVideoInfo.RawJson"/>, alongside
    /// <paramref name="title"/> — see <see cref="DownloadQueueItem.InfoJson"/>'s own doc comment for
    /// how <see cref="ProcessVideoItemAsync"/> uses (and clears) it. Meaningless without a
    /// <paramref name="title"/> already set too, since <see cref="ProcessVideoItemAsync"/> only ever
    /// looks at it once it already knows it doesn't need to resolve the title itself.
    /// </param>
    /// <param name="destinationFolder">
    /// This one download's folder override, or null to use whatever Settings says — see
    /// <see cref="DownloadQueueItem.DestinationFolder"/>'s own doc comment.
    /// </param>
    public async Task<DownloadQueueItem> EnqueueAsync(
        string url,
        int resolution,
        string title = "",
        string containerFormat = "mp4",
        DownloadKind kind = DownloadKind.Video,
        string? infoJson = null,
        string? destinationFolder = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.", nameof(url));

        var id = await WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO download_queue (Url, Title, Resolution, ContainerFormat, FilePath, Status, Progress, ErrorMessage, Position, CreatedAt, Kind, InfoJson, DestinationFolder)
                VALUES ($url, $title, $resolution, $containerFormat, NULL, $status, 0, NULL,
                        (SELECT COALESCE(MAX(Position), -1) + 1 FROM download_queue), $createdAt, $kind, $infoJson, $destinationFolder);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$resolution", resolution);
            command.Parameters.AddWithValue("$containerFormat", containerFormat);
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$kind", kind.ToString());
            command.Parameters.AddWithValue("$infoJson", (object?)infoJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$destinationFolder", (object?)destinationFolder ?? DBNull.Value);

            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);

        var item = new DownloadQueueItem
        {
            Id = id,
            Url = url,
            Title = title,
            Resolution = resolution,
            ContainerFormat = containerFormat,
            Kind = kind,
            InfoJson = infoJson,
            DestinationFolder = destinationFolder,
            Status = DownloadQueueStatus.Pending,
            CreatedAt = DateTimeOffset.Now
        };

        RaiseChanged(item);
        _workAvailable.Release();
        return item;
    }

    /// <summary>
    /// Awaits an already-enqueued item's outcome. Split out from <see cref="EnqueueAndWaitAsync"/>
    /// so a caller that wants to track live progress (e.g. via <see cref="ItemChanged"/>) can grab
    /// the item's id from <see cref="EnqueueAsync"/> before awaiting completion, instead of only
    /// finding out the id once the download is already done. Throws on failure or cancellation
    /// instead of returning a non-<see cref="DownloadQueueStatus.Completed"/> item, so callers can
    /// keep using an ordinary try/catch around it.
    /// </summary>
    public async Task<DownloadQueueItem> WaitForCompletionAsync(long id, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DownloadQueueItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[id] = tcs;

        await using var registration = cancellationToken.Register(static state =>
        {
            var (self, itemId) = ((DownloadQueueService Self, long Id))state!;
            _ = self.CancelAsync(itemId);
        }, (Self: this, Id: id));

        var result = await tcs.Task.ConfigureAwait(false);

        return result.Status switch
        {
            DownloadQueueStatus.Completed => result,
            DownloadQueueStatus.Canceled => throw new OperationCanceledException("Download canceled.", cancellationToken),
            _ => throw new InvalidOperationException(result.ErrorMessage ?? "Download failed.")
        };
    }

    /// <summary>Enqueues a download and awaits its outcome in one call — see <see cref="WaitForCompletionAsync"/>.</summary>
    public async Task<DownloadQueueItem> EnqueueAndWaitAsync(string url, int resolution, CancellationToken cancellationToken = default)
    {
        var item = await EnqueueAsync(url, resolution, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await WaitForCompletionAsync(item.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task PauseAsync(long id, CancellationToken cancellationToken = default)
    {
        if (_activeCancellations.TryGetValue(id, out var cts))
        {
            // Currently downloading — request a pause and cancel the in-flight yt-dlp process;
            // it'll pick up from its own .part file next time this item is processed.
            _pauseRequested[id] = true;
            cts.Cancel();
            return Task.CompletedTask;
        }

        return UpdateStatusAsync(id, DownloadQueueStatus.Paused, cancellationToken: cancellationToken);
    }

    public async Task ResumeAsync(long id, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, DownloadQueueStatus.Pending, cancellationToken: cancellationToken).ConfigureAwait(false);
        _workAvailable.Release();
    }

    public Task CancelAsync(long id, CancellationToken cancellationToken = default)
    {
        if (_activeCancellations.TryGetValue(id, out var cts))
        {
            cts.Cancel();
            return Task.CompletedTask;
        }

        return UpdateStatusAsync(id, DownloadQueueStatus.Canceled, cancellationToken: cancellationToken, completeWaiter: true);
    }

    public async Task RetryAsync(long id, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, DownloadQueueStatus.Pending, clearError: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        _workAvailable.Release();
    }

    public Task ReorderAsync(long id, int newPosition, CancellationToken cancellationToken = default) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE download_queue SET Position = $position WHERE Id = $id";
            command.Parameters.AddWithValue("$position", newPosition);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Removes a row from the queue/history entirely — the "clean up my list" ask this exists for,
    /// distinct from every other operation here, which only ever changes a row's status. Refuses a
    /// row that's currently downloading (mirrors <see cref="DownloadQueueItem.CanDelete"/>, which
    /// hides the button for exactly this reason in <c>Views.MainWindow</c>) rather than deleting the
    /// database row out from under an in-flight yt-dlp process still writing to
    /// <see cref="DownloadQueueItem.FilePath"/>.
    ///
    /// <paramref name="deleteFile"/> additionally deletes the downloaded file itself — off by
    /// default, since "clean up the list, keep the file" is the common case this was actually asked
    /// for; deleting the file too is the rarer, explicitly-opted-into one. File deletion is
    /// best-effort, same as <c>Views.MainWindow.BtnShowInFolder_Click</c>'s own filesystem hand-off —
    /// a missing/locked file shouldn't block the row itself from going away.
    /// </summary>
    public async Task DeleteAsync(long id, bool deleteFile = false, CancellationToken cancellationToken = default)
    {
        if (_activeCancellations.ContainsKey(id))
            return;

        var filePath = await WithLockAsync(async () =>
        {
            var existing = await GetItemNoLockAsync(id, cancellationToken).ConfigureAwait(false);

            await using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM download_queue WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return existing?.FilePath;
        }, cancellationToken).ConfigureAwait(false);

        _pauseRequested.TryRemove(id, out _);
        _waiters.TryRemove(id, out _);

        if (deleteFile && !string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                // A DownloadKind.Torrent row's FilePath is a directory, not a file (see
                // ProcessTorrentItemAsync's own doc comment) — File.Delete throws on one, so this
                // needs Directory.Delete instead, or "delete file and remove" would silently leave
                // every downloaded torrent's folder behind (the catch below swallows exactly that).
                if (Directory.Exists(filePath))
                    Directory.Delete(filePath, recursive: true);
                else if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // Best-effort — see the doc comment above.
            }
        }

        ItemRemoved?.Invoke(id);
    }

    /// <summary>
    /// Drives up to <c>AppSettings.MaxConcurrentDownloads</c> items at once (README roadmap step 7)
    /// and only starts new ones inside the configured schedule window, if scheduling is on. Doesn't
    /// await <see cref="ProcessItemAsync"/> — it's fired and left running so the loop can
    /// immediately check for more capacity, relying on <see cref="_activeCancellations"/> (which
    /// ProcessItemAsync populates synchronously before its first await, so this always sees an
    /// accurate count with no race) to know how many are already in flight. There's no signal for
    /// "the schedule window just opened" or "a setting changed", so the wait between iterations is
    /// capped at 30s as a periodic recheck rather than only waking on <see cref="_workAvailable"/>.
    /// </summary>
    private async Task ProcessLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Cheap (a File.Exists stat() call per Completed row) and unconditional every
            // iteration — see CheckForMissingFilesAsync's own doc comment for why that's fine even
            // for a queue that's never pruned.
            try
            {
                await CheckForMissingFilesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var settings = SettingsService.Load();
            var capacity = Math.Max(1, settings.MaxConcurrentDownloads) - _activeCancellations.Count;

            DownloadQueueItem? next = null;
            if (capacity > 0 && IsWithinSchedule(settings))
            {
                try
                {
                    next = await GetNextPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (next is null)
            {
                try
                {
                    await _workAvailable.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // Tracked in _activeTasks (removed in ProcessItemAsync's own finally, right next to
            // _activeCancellations' removal) purely so Dispose can wait for it — still
            // deliberately not awaited here, for the same reason the doc comment above gives:
            // this loop needs to immediately go check for more capacity, not wait for one
            // download to finish before considering the next.
            _activeTasks[next.Id] = ProcessItemAsync(next, stoppingToken);
        }
    }

    private static bool IsWithinSchedule(AppSettings settings) =>
        !settings.SchedulingEnabled ||
        IsWithinWindow(TimeOnly.FromDateTime(DateTime.Now), settings.ScheduleStart, settings.ScheduleEnd);

    /// <summary>
    /// Split out from <see cref="IsWithinSchedule"/> as a pure function of an explicit "now" purely
    /// so it's independently testable without needing to control the system clock. Internal (rather
    /// than private) for exactly that reason — see Yoink.Tests' DownloadQueueScheduleTests.
    /// </summary>
    internal static bool IsWithinWindow(TimeOnly now, TimeOnly start, TimeOnly end) =>
        start <= end
            ? now >= start && now < end // same-day window, e.g. 09:00-17:00
            : now >= start || now < end; // wraps past midnight, e.g. 22:00-06:00

    /// <summary>
    /// The smaller of the per-download cap and this download's share of the global cap (global ÷
    /// MaxConcurrentDownloads) — see <see cref="AppSettings.GlobalSpeedLimitKBps"/> for why it's a
    /// static split rather than a live rebalance. Null (unlimited) only when neither is set.
    /// Internal (not private) so Yoink.Tests can exercise it directly.
    /// </summary>
    internal static int? ComputeRateLimitKBps(AppSettings settings)
    {
        int? perDownload = settings.PerDownloadSpeedLimitKBps is > 0 ? settings.PerDownloadSpeedLimitKBps : null;
        int? globalShare = settings.GlobalSpeedLimitKBps is > 0
            ? settings.GlobalSpeedLimitKBps / Math.Max(1, settings.MaxConcurrentDownloads)
            : null;

        if (perDownload is null)
            return globalShare;
        if (globalShare is null)
            return perDownload;

        return Math.Min(perDownload.Value, globalShare.Value);
    }

    private async Task ProcessItemAsync(DownloadQueueItem item, CancellationToken stoppingToken)
    {
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeCancellations[item.Id] = itemCts;

        item.Status = DownloadQueueStatus.Active;
        await PersistAsync(item).ConfigureAwait(false);
        RaiseChanged(item);

        try
        {
            // Re-read settings rather than reusing whatever ProcessLoopAsync last saw: this item
            // may have been sitting Pending for a while, and the download-folder/speed-limit
            // settings could have changed since.
            var settings = SettingsService.Load();
            var rateLimitKBps = ComputeRateLimitKBps(settings);

            switch (item.Kind)
            {
                case DownloadKind.Video:
                    await ProcessVideoItemAsync(item, settings, rateLimitKBps, itemCts.Token).ConfigureAwait(false);
                    break;
                case DownloadKind.Torrent:
                    await ProcessTorrentItemAsync(item, settings, rateLimitKBps, itemCts.Token).ConfigureAwait(false);
                    break;
                default:
                    await ProcessFileItemAsync(item, settings, rateLimitKBps, itemCts.Token).ConfigureAwait(false);
                    break;
            }

            item.Status = DownloadQueueStatus.Completed;
            item.Progress = 1.0;
            await PersistAsync(item).ConfigureAwait(false);
            RaiseChanged(item);
            CompleteWaiter(item);
        }
        catch (OperationCanceledException) when (itemCts.IsCancellationRequested)
        {
            var wasPauseRequest = _pauseRequested.TryRemove(item.Id, out var paused) && paused;
            item.Status = wasPauseRequest ? DownloadQueueStatus.Paused : DownloadQueueStatus.Canceled;
            await PersistAsync(item).ConfigureAwait(false);
            RaiseChanged(item);

            if (!wasPauseRequest)
                CompleteWaiter(item);
        }
        catch (Exception ex)
        {
            item.Status = DownloadQueueStatus.Failed;
            item.ErrorMessage = ex.Message;
            await PersistAsync(item).ConfigureAwait(false);
            RaiseChanged(item);
            CompleteWaiter(item);
        }
        finally
        {
            _activeCancellations.TryRemove(item.Id, out _);
            _activeTasks.TryRemove(item.Id, out _);

            // Frees a concurrency slot ProcessLoopAsync's own capacity check (Math.Max(1,
            // MaxConcurrentDownloads) - _activeCancellations.Count) will now see as available — but
            // that loop iteration already ran (it's what started this very item) and, seeing no
            // capacity left, is sitting in its 30-second WaitAsync until something releases
            // _workAvailable. EnqueueAsync/ResumeAsync/RetryAsync already do that for "a new item
            // exists to consider"; this is the missing half for "a slot just freed up" — without it,
            // every completion (success, failure, or cancellation) left the next Pending item
            // waiting up to 30s for the loop's own periodic recheck instead of starting immediately,
            // which is especially visible at the default MaxConcurrentDownloads of 1, where this
            // fires after literally every single download.
            _workAvailable.Release();
        }
    }

    /// <summary>
    /// How long a resolved <see cref="DownloadQueueItem.InfoJson"/> is trusted before
    /// <see cref="ProcessVideoItemAsync"/> discards it rather than handing it to
    /// <see cref="YtDlpClient.DownloadAsync"/> — its format URLs are signed and time-limited, and an
    /// item can sit <see cref="DownloadQueueStatus.Pending"/> a while behind
    /// <c>AppSettings.MaxConcurrentDownloads</c>/scheduling before actually being processed. Deliberately
    /// generous (not tuned to any real observed expiry) since <see cref="YtDlpClient.DownloadAsync"/>'s
    /// own infoJson-failure retry is the actual backstop against a stale one slipping past this; this
    /// check just avoids the doomed attempt (and its retry delay) in the common case of a long wait.
    /// </summary>
    private static readonly TimeSpan InfoJsonMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>The original yt-dlp download path — unchanged in substance from before <see cref="DownloadKind"/> existed, just split out of <see cref="ProcessItemAsync"/> so that method can branch on kind.</summary>
    private async Task ProcessVideoItemAsync(DownloadQueueItem item, AppSettings settings, int? rateLimitKBps, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Title))
        {
            var info = await _ytDlp.GetVideoInfoAsync(item.Url, cancellationToken).ConfigureAwait(false);
            item.Title = info.Title;
        }

        // Single-use regardless of outcome (used or discarded as too stale) — see
        // DownloadQueueItem.InfoJson's own doc comment for why this is cleared here rather than left
        // for a later PersistAsync call to overwrite with the same value.
        var infoJson = item.InfoJson;
        item.InfoJson = null;
        var infoJsonToUse = !string.IsNullOrEmpty(infoJson) && DateTimeOffset.Now - item.CreatedAt < InfoJsonMaxAge
            ? infoJson
            : null;

        if (item.FilePath is null)
        {
            var candidatePath = BuildDestinationPath(item.Title, ResolveItemDestinationFolder(item, settings), item.ContainerFormat);
            item.FilePath = await ReserveUniqueDestinationPathAsync(item.Id, candidatePath, cancellationToken).ConfigureAwait(false);
        }

        var selector = BuildFormatSelector(item.Resolution);
        var progress = new Progress<YtDlpDownloadProgress>(p =>
        {
            item.Progress = p.Fraction;
            item.DownloadedBytes = p.BytesDownloaded;
            item.TotalBytes = p.TotalBytes;
            RaiseChanged(item);
        });

        await _ytDlp.DownloadAsync(
            item.Url,
            selector,
            item.FilePath,
            expectedSegmentCount: 2,
            rateLimitKBps: rateLimitKBps,
            containerFormat: item.ContainerFormat,
            progress: progress,
            infoJson: infoJsonToUse,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The generic direct-link path via <see cref="DownloadEngine"/> — a plain HTTP(S) URL to
    /// whatever file (PDF, .exe, .deb, ...), not a YouTube video. <see cref="DownloadQueueItem.Title"/>
    /// doubles as the resolved filename here, same "skip re-resolving it" reasoning as the video
    /// path above: <c>Views.AddDownloadDialog</c>'s generic-file flow already probes the URL before
    /// enqueueing (see that view's own notes), so this only probes again for an item that somehow
    /// reached here without one — enqueued directly through <see cref="EnqueueAsync"/>, for instance.
    /// </summary>
    private async Task ProcessFileItemAsync(DownloadQueueItem item, AppSettings settings, int? rateLimitKBps, CancellationToken cancellationToken)
    {
        var sourceUri = new Uri(item.Url);

        if (string.IsNullOrEmpty(item.Title))
        {
            var probe = await _downloadEngine.ProbeAsync(sourceUri, cancellationToken).ConfigureAwait(false);
            item.Title = probe.FileName ?? DownloadEngine.GetFileNameFromUri(sourceUri);
        }

        if (item.FilePath is null)
        {
            var candidatePath = BuildFileDestinationPath(item.Title, ResolveItemDestinationFolder(item, settings));
            item.FilePath = await ReserveUniqueDestinationPathAsync(item.Id, candidatePath, cancellationToken).ConfigureAwait(false);
        }

        var progress = new Progress<DownloadEngineProgress>(p =>
        {
            item.Progress = p.Fraction;
            item.DownloadedBytes = p.BytesDownloaded;
            item.TotalBytes = p.TotalBytes;
            RaiseChanged(item);
        });

        await _downloadEngine.DownloadAsync(
            sourceUri,
            item.FilePath,
            progress: progress,
            rateLimitKBps: rateLimitKBps,
            maxConnections: Math.Max(1, settings.MaxConnectionsPerDownload),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The peer-to-peer path via <see cref="TorrentEngine"/>. Unlike the video/file paths above,
    /// <see cref="DownloadQueueItem.FilePath"/> here is a <i>directory</i>, not a file — reserved
    /// uniquely the same way (see <see cref="ReserveUniqueDestinationPathAsync"/>'s <c>isDirectory</c>
    /// parameter) so two torrents that happen to share a name don't collide, and MonoTorrent lays out
    /// the torrent's own file(s) inside it (see <see cref="TorrentEngine.DownloadAsync"/>'s own doc
    /// comment for why <see cref="Views.MainWindow.BtnShowInFolder_Click"/> needs to know this).
    ///
    /// A magnet link's real name/size isn't known until its metadata actually resolves (a swarm round
    /// trip, not instant the way a local/remote <c>.torrent</c> file's contents are — see
    /// <c>Views.AddDownloadDialog</c>'s own notes), so <see cref="TorrentEngine.TryGetMagnetDisplayName"/>
    /// (read straight from the magnet URI's own <c>dn=</c> parameter, no network needed) is only ever
    /// a *provisional* title/destination-folder name for one — <see cref="TorrentDownloadProgress.PhaseText"/>
    /// updates <see cref="DownloadQueueItem.Title"/> to the real resolved name once metadata arrives,
    /// but deliberately leaves <see cref="DownloadQueueItem.FilePath"/> (already reserved, and
    /// possibly already being written to by MonoTorrent) alone rather than trying to rename a
    /// directory out from under an in-progress download for what's ultimately just a cosmetic
    /// mismatch.
    /// </summary>
    private async Task ProcessTorrentItemAsync(DownloadQueueItem item, AppSettings settings, int? rateLimitKBps, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Title))
        {
            item.Title = DownloadUrlKind.IsMagnetLink(item.Url)
                ? TorrentEngine.TryGetMagnetDisplayName(item.Url) ?? "Torrent"
                : (await TorrentEngine.LoadTorrentAsync(item.Url, cancellationToken: cancellationToken).ConfigureAwait(false)).Name;
        }

        if (item.FilePath is null)
        {
            var candidatePath = BuildTorrentDestinationPath(item.Title, ResolveItemDestinationFolder(item, settings));
            item.FilePath = await ReserveUniqueDestinationPathAsync(item.Id, candidatePath, cancellationToken, isDirectory: true).ConfigureAwait(false);
        }

        var progress = new Progress<TorrentDownloadProgress>(p =>
        {
            item.Progress = p.Fraction;
            item.DownloadedBytes = p.BytesDownloaded;
            item.TotalBytes = p.TotalBytes;
            item.SeederCount = p.SeederCount;
            item.LeecherCount = p.LeecherCount;
            item.PhaseText = p.PhaseText;

            // A magnet link's provisional title (see this method's own doc comment) only ever came
            // from the URI's own dn= parameter, never from real metadata — swap in the torrent's
            // actual name the moment it's known. Harmless to keep re-assigning the same value for a
            // .torrent-file item, whose title was already the real name from the start.
            if (!string.IsNullOrEmpty(p.ResolvedName))
                item.Title = p.ResolvedName;

            RaiseChanged(item);
        });

        await _torrentEngine.DownloadAsync(
            item.Url,
            item.FilePath,
            rateLimitKBps: rateLimitKBps,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void CompleteWaiter(DownloadQueueItem item)
    {
        if (_waiters.TryRemove(item.Id, out var tcs))
            tcs.TrySetResult(item);
    }

    // internal (not private) so Yoink.Tests can exercise these directly.
    internal static string BuildFormatSelector(int resolution) =>
        $"bestvideo[height<={resolution}]+bestaudio/best[height<={resolution}]/best";

    internal static string BuildDestinationPath(string title, string downloadFolder, string containerFormat = "mp4")
    {
        var fileName = string.Concat(title.Split(Path.GetInvalidFileNameChars())) + "." + containerFormat;
        return Path.Combine(downloadFolder, fileName);
    }

    /// <summary>
    /// Same idea as <see cref="BuildDestinationPath"/> but for a <see cref="DownloadKind.File"/> item
    /// — <paramref name="fileName"/> already carries whatever extension it actually has (from
    /// <see cref="DownloadEngine.ProbeAsync"/> or the URL itself), so unlike the video path nothing
    /// gets appended to it.
    /// </summary>
    internal static string BuildFileDestinationPath(string fileName, string downloadFolder)
    {
        var sanitized = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(downloadFolder, string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized);
    }

    /// <summary>
    /// Same idea again for a <see cref="DownloadKind.Torrent"/> item, except the result is a
    /// directory (see <see cref="ProcessTorrentItemAsync"/>'s own doc comment for why), not a file —
    /// no extension involved, and <see cref="ReserveUniqueDestinationPathAsync"/> is called with
    /// <c>isDirectory: true</c> for this one so its collision check uses
    /// <see cref="Directory.Exists(string)"/> rather than <see cref="File.Exists(string)"/>.
    /// </summary>
    internal static string BuildTorrentDestinationPath(string torrentName, string downloadFolder)
    {
        var sanitized = string.Concat(torrentName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(downloadFolder, string.IsNullOrWhiteSpace(sanitized) ? "torrent" : sanitized);
    }

    /// <summary>
    /// The configured download folder, or the platform's default Downloads folder when
    /// <see cref="AppSettings.DownloadFolder"/> is unset — see that property's doc comment. Internal
    /// (not private) so Yoink.Tests can exercise it directly.
    /// </summary>
    internal static string ResolveDownloadFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? SettingsService.GetDefaultDownloadFolder()
            : settings.DownloadFolder;

    /// <summary>
    /// Where a specific item's download actually lands: its own
    /// <see cref="DownloadQueueItem.DestinationFolder"/> override if it has one (set once, at enqueue
    /// time, via <c>Views.AddDownloadDialog</c>'s "Save to" picker), else the app-wide
    /// <see cref="ResolveDownloadFolder"/> default — the same fallback chain
    /// <see cref="DownloadQueueItem.DestinationFolder"/>'s own doc comment describes. Used by all
    /// three <c>Process*ItemAsync</c> methods so a per-item override and "just use what Settings has
    /// configured" both funnel through one place. Internal (not private) so Yoink.Tests can exercise
    /// it directly.
    /// </summary>
    internal static string ResolveItemDestinationFolder(DownloadQueueItem item, AppSettings settings) =>
        string.IsNullOrWhiteSpace(item.DestinationFolder) ? ResolveDownloadFolder(settings) : item.DestinationFolder;

    private Task<DownloadQueueItem?> GetNextPendingAsync(CancellationToken cancellationToken) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM download_queue WHERE Status = $status ORDER BY Position ASC LIMIT 1";
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
        }, cancellationToken);

    /// <summary>
    /// The other half of "one-on-one relation between a download file and the download list": every
    /// <see cref="DownloadQueueStatus.Completed"/> row's <see cref="DownloadQueueItem.FilePath"/> is
    /// checked for existence, and any that's gone — deleted, or moved elsewhere on disk; this app has
    /// no way to tell those two apart, and doesn't need to — is moved to
    /// <see cref="DownloadQueueStatus.Missing"/> instead of going on showing "Completed" for a file
    /// that's no longer actually there. <see cref="File.Exists"/> is a cheap stat() call, so scanning
    /// even a few hundred completed rows (this queue is deliberately never pruned — see the class doc
    /// comment) every <see cref="ProcessLoopAsync"/> iteration isn't worth a dedicated timer/throttle.
    ///
    /// One-way: a row already <see cref="DownloadQueueStatus.Missing"/> is left alone here regardless
    /// of what's on disk now — <see cref="RetryAsync"/> (the same button
    /// <see cref="DownloadQueueStatus.Failed"/>/<see cref="DownloadQueueStatus.Canceled"/> already
    /// show, since <see cref="DownloadQueueItem.CanRetry"/> covers all three) is what brings it back,
    /// re-downloading to this exact same <see cref="DownloadQueueItem.FilePath"/> — see
    /// <see cref="ReserveUniqueDestinationPathAsync"/>'s doc comment for why a retry never picks a new
    /// path.
    ///
    /// Internal (not private) so Yoink.Tests can trigger a scan deterministically instead of waiting
    /// out <see cref="ProcessLoopAsync"/>'s own real-time cadence.
    /// </summary>
    internal async Task CheckForMissingFilesAsync(CancellationToken cancellationToken = default)
    {
        var missingIds = await WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, FilePath FROM download_queue WHERE Status = $status AND FilePath IS NOT NULL";
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Completed.ToString());

            var missing = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // File.Exists alone would misfire for every single completed DownloadKind.Torrent
                // row — its FilePath is a directory (see ProcessTorrentItemAsync's own doc comment),
                // and File.Exists always returns false for a path that's actually a directory, even
                // one that's very much still there. Directory.Exists covers that case; a genuinely
                // deleted/moved file or folder still correctly fails both checks.
                var path = reader.GetString(1);
                if (!File.Exists(path) && !Directory.Exists(path))
                    missing.Add(reader.GetInt64(0));
            }

            return missing;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var id in missingIds)
            await MarkMissingAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkMissingAsync(long id, CancellationToken cancellationToken)
    {
        var item = await WithLockAsync(async () =>
        {
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "UPDATE download_queue SET Status = $status, ErrorMessage = $errorMessage WHERE Id = $id";
                command.Parameters.AddWithValue("$status", DownloadQueueStatus.Missing.ToString());
                command.Parameters.AddWithValue("$errorMessage", "File no longer found on disk.");
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await GetItemNoLockAsync(id, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (item is not null)
            RaiseChanged(item);
    }

    /// <summary>
    /// Picks a destination path nothing else already has — the other building block behind
    /// "one-on-one relation between a download file and the download list": <paramref name="candidatePath"/>
    /// as-is if it's free, else "name (1).ext", "name (2).ext", ... (the same convention browsers and
    /// OS file managers already use for a duplicate download) until one is. "Already has" means either
    /// a file that already exists at that exact path on disk (protects a pre-existing, unrelated file
    /// the user already had in their download folder too, not just Yoink's own downloads) or another
    /// row in <c>download_queue</c> already recorded against that same path.
    ///
    /// Both the check and the reservation (persisting the chosen path immediately, before any bytes
    /// are actually written) happen inside one <see cref="WithLockAsync{T}"/> acquisition so two items
    /// dequeued concurrently (<see cref="AppSettings.MaxConcurrentDownloads"/> &gt; 1) that both
    /// resolve to the same title can't both land on "name.ext" — the second one's check sees the
    /// first's reservation the moment it queries, not only once the first's file actually exists on
    /// disk (which wouldn't happen until well after this method returns).
    ///
    /// Only ever called for an item whose <see cref="DownloadQueueItem.FilePath"/> is still null —
    /// see <see cref="ProcessVideoItemAsync"/>/<see cref="ProcessFileItemAsync"/>/<see cref="ProcessTorrentItemAsync"/>.
    /// A retried <see cref="DownloadQueueStatus.Failed"/>/<see cref="DownloadQueueStatus.Canceled"/>/
    /// <see cref="DownloadQueueStatus.Missing"/> item already has one from its first attempt and
    /// deliberately reuses it as-is instead of coming back through here, so retrying re-downloads to
    /// the exact same place rather than picking up a new "(1)" suffix.
    /// </summary>
    /// <param name="isDirectory">
    /// True for a <see cref="ProcessTorrentItemAsync"/> caller, whose <paramref name="candidatePath"/>
    /// is a directory MonoTorrent lays files out inside rather than a single file — checked via
    /// <see cref="Directory.Exists(string)"/> instead of <see cref="File.Exists(string)"/>, since the
    /// latter always returns false for a path that's actually a directory and so would never detect a
    /// real collision on this path.
    /// </param>
    private Task<string> ReserveUniqueDestinationPathAsync(long itemId, string candidatePath, CancellationToken cancellationToken, bool isDirectory = false) =>
        WithLockAsync(async () =>
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT FilePath FROM download_queue WHERE FilePath IS NOT NULL AND Id != $id";
                command.Parameters.AddWithValue("$id", itemId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    claimed.Add(reader.GetString(0));
            }

            var suffix = 0;
            var chosen = candidatePath;
            while (claimed.Contains(chosen) || (isDirectory ? Directory.Exists(chosen) : File.Exists(chosen)))
            {
                suffix++;
                chosen = InsertPathSuffix(candidatePath, suffix, isDirectory);
            }

            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "UPDATE download_queue SET FilePath = $filePath WHERE Id = $id";
                command.Parameters.AddWithValue("$filePath", chosen);
                command.Parameters.AddWithValue("$id", itemId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return chosen;
        }, cancellationToken);

    /// <summary>
    /// "/dir/Title.mp4" + 1 → "/dir/Title (1).mp4" — the browser/OS-file-manager convention for
    /// disambiguating a duplicate filename, applied by <see cref="ReserveUniqueDestinationPathAsync"/>.
    /// <paramref name="suffix"/> of 0 (or less) returns <paramref name="path"/> unchanged. Internal
    /// (not private) so Yoink.Tests can exercise it directly.
    /// </summary>
    /// <param name="isDirectory">
    /// True for a torrent's directory candidate — skips the file-extension split entirely (the whole
    /// final path segment is the "stem"), since <see cref="Path.GetExtension(string)"/>/
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> would otherwise misread a genuine dot in
    /// a directory name (e.g. "Ubuntu 24.04") as a file extension and suffix in the wrong place —
    /// "Ubuntu 24 (1).04" instead of the intended "Ubuntu 24.04 (1)".
    /// </param>
    internal static string InsertPathSuffix(string path, int suffix, bool isDirectory = false)
    {
        if (suffix <= 0)
            return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (isDirectory)
            return Path.Combine(directory, $"{Path.GetFileName(path)} ({suffix})");

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{stem} ({suffix}){extension}");
    }

    private async Task UpdateStatusAsync(
        long id,
        DownloadQueueStatus status,
        bool clearError = false,
        bool completeWaiter = false,
        CancellationToken cancellationToken = default)
    {
        // Re-reads the full row after updating rather than constructing a bare Id+Status item:
        // callers (the queue view included) treat every ItemChanged payload as a complete,
        // authoritative snapshot, so a partial one would blank out Title/Progress/etc. wherever
        // it's applied. Both statements run inside the same lock acquisition so nothing else can
        // write to this row between the update and the re-read.
        var item = await WithLockAsync(async () =>
        {
            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = clearError
                    ? "UPDATE download_queue SET Status = $status, ErrorMessage = NULL WHERE Id = $id"
                    : "UPDATE download_queue SET Status = $status WHERE Id = $id";
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await GetItemNoLockAsync(id, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (item is null)
            return;

        RaiseChanged(item);

        if (completeWaiter)
            CompleteWaiter(item);
    }

    private async Task<DownloadQueueItem?> GetItemNoLockAsync(long id, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM download_queue WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    private Task PersistAsync(DownloadQueueItem item, CancellationToken cancellationToken = default) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE download_queue
                SET Title = $title, FilePath = $filePath, Status = $status, Progress = $progress, ErrorMessage = $errorMessage, InfoJson = $infoJson
                WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$filePath", (object?)item.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", item.Status.ToString());
            command.Parameters.AddWithValue("$progress", item.Progress);
            command.Parameters.AddWithValue("$errorMessage", (object?)item.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$infoJson", (object?)item.InfoJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", item.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private void RaiseChanged(DownloadQueueItem item) => ItemChanged?.Invoke(item);

    /// <summary>
    /// Every <c>SELECT *</c> in this class reads the same fixed <c>download_queue</c> schema, so
    /// a column's ordinal is the same for every row of one query — <see cref="ReadItem"/> used to
    /// re-look-up all nine (well, eleven counting the two IsDBNull-then-GetString columns) by name
    /// on every single row, which is wasted string-hashing work on a queue that's deliberately
    /// never pruned (it doubles as download history — see this class's own doc comment) and so has
    /// no upper bound on how many rows <see cref="GetAllAsync"/> reads back at startup. Computing
    /// this once per query and threading it through instead turns that into nine lookups total,
    /// not nine per row.
    /// </summary>
    private readonly record struct ColumnOrdinals(
        int Id, int Url, int Title, int Resolution, int ContainerFormat, int FilePath,
        int Status, int Progress, int ErrorMessage, int Position, int CreatedAt, int Kind, int InfoJson,
        int DestinationFolder)
    {
        public static ColumnOrdinals FromReader(SqliteDataReader reader) => new(
            reader.GetOrdinal("Id"), reader.GetOrdinal("Url"), reader.GetOrdinal("Title"),
            reader.GetOrdinal("Resolution"), reader.GetOrdinal("ContainerFormat"), reader.GetOrdinal("FilePath"),
            reader.GetOrdinal("Status"), reader.GetOrdinal("Progress"), reader.GetOrdinal("ErrorMessage"),
            reader.GetOrdinal("Position"), reader.GetOrdinal("CreatedAt"), reader.GetOrdinal("Kind"),
            reader.GetOrdinal("InfoJson"), reader.GetOrdinal("DestinationFolder"));
    }

    private static DownloadQueueItem ReadItem(SqliteDataReader reader) => ReadItem(reader, ColumnOrdinals.FromReader(reader));

    private static DownloadQueueItem ReadItem(SqliteDataReader reader, ColumnOrdinals o) => new()
    {
        Id = reader.GetInt64(o.Id),
        Url = reader.GetString(o.Url),
        Title = reader.GetString(o.Title),
        Resolution = reader.GetInt32(o.Resolution),
        ContainerFormat = reader.GetString(o.ContainerFormat),
        FilePath = reader.IsDBNull(o.FilePath) ? null : reader.GetString(o.FilePath),
        Status = Enum.Parse<DownloadQueueStatus>(reader.GetString(o.Status)),
        Progress = reader.GetDouble(o.Progress),
        ErrorMessage = reader.IsDBNull(o.ErrorMessage) ? null : reader.GetString(o.ErrorMessage),
        Position = reader.GetInt32(o.Position),
        CreatedAt = DateTimeOffset.Parse(
            reader.GetString(o.CreatedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Kind = Enum.Parse<DownloadKind>(reader.GetString(o.Kind)),
        InfoJson = reader.IsDBNull(o.InfoJson) ? null : reader.GetString(o.InfoJson),
        DestinationFolder = reader.IsDBNull(o.DestinationFolder) ? null : reader.GetString(o.DestinationFolder)
    };

    /// <summary>
    /// Called from the constructor, before anything else could possibly touch <see cref="_connection"/>
    /// — no locking needed yet.
    /// </summary>
    private void EnsureSchema()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS download_queue (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Resolution INTEGER NOT NULL,
                    FilePath TEXT,
                    Status TEXT NOT NULL,
                    Progress REAL NOT NULL,
                    ErrorMessage TEXT,
                    Position INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        // Added after the table above already shipped, so existing installs' queue.db needs a
        // real migration rather than just being part of the CREATE TABLE — guarded on the column
        // not already existing, since ALTER TABLE ADD COLUMN has no IF NOT EXISTS form in SQLite
        // and errors on a second run otherwise. Defaults every pre-existing row to "mp4", matching
        // this app's previous hardcoded behavior exactly.
        EnsureColumnExists("ContainerFormat", "TEXT NOT NULL DEFAULT 'mp4'");

        // Same reasoning, added when generic (non-YouTube) file downloads were: every pre-existing
        // row predates DownloadKind entirely, and "Video" (yt-dlp) is exactly what all of them
        // already were before this column existed.
        EnsureColumnExists("Kind", "TEXT NOT NULL DEFAULT 'Video'");

        // Same reasoning again, added for the --load-info-json plumbing (see
        // DownloadQueueItem.InfoJson's doc comment) — nullable, no default needed since every
        // pre-existing row simply has nothing to reuse, same as a freshly-enqueued item with no
        // title yet.
        EnsureColumnExists("InfoJson", "TEXT");

        // Same reasoning again, added for the per-download destination folder (see
        // DownloadQueueItem.DestinationFolder's doc comment) — nullable, no default needed: null
        // already means "use whatever Settings says", which is exactly what every pre-existing row
        // already did before this column existed.
        EnsureColumnExists("DestinationFolder", "TEXT");
    }

    private void EnsureColumnExists(string columnName, string columnDefinitionSql)
    {
        using (var checkCommand = _connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_queue') WHERE name = $name";
            checkCommand.Parameters.AddWithValue("$name", columnName);
            if ((long)checkCommand.ExecuteScalar()! > 0)
                return;
        }

        using var alterCommand = _connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE download_queue ADD COLUMN {columnName} {columnDefinitionSql}";
        alterCommand.ExecuteNonQuery();
    }

    /// <summary>
    /// Any item still marked <see cref="DownloadQueueStatus.Active"/> at startup means the app
    /// was closed or crashed mid-download — put it back to <see cref="DownloadQueueStatus.Pending"/>
    /// so the queue picks it up (and yt-dlp resumes it) again instead of leaving it stuck. Also
    /// called from the constructor before the processing loop starts, so — like
    /// <see cref="EnsureSchema"/> — nothing else could be contending for <see cref="_connection"/> yet.
    /// </summary>
    private void RecoverStaleActiveItems()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE download_queue SET Status = $pending WHERE Status = $active";
        command.Parameters.AddWithValue("$pending", DownloadQueueStatus.Pending.ToString());
        command.Parameters.AddWithValue("$active", DownloadQueueStatus.Active.ToString());
        command.ExecuteNonQuery();
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task WithLockAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Cancels the processing loop and every in-flight download, then actually waits for them to
    /// unwind before releasing anything else — not just <see cref="_processingLoop"/>. That used
    /// to be the only thing waited on here, which is a real gap: <see cref="ProcessLoopAsync"/>
    /// fires each <see cref="ProcessItemAsync"/> without awaiting it (deliberately — see that
    /// method's doc comment), so canceling <see cref="_stoppingCts"/> makes the *loop* return
    /// almost immediately while the downloads it started are still unwinding independently on
    /// their own tasks — still holding a live yt-dlp/ffmpeg child process at the moment this
    /// method would otherwise have returned. <see cref="_activeTasks"/> exists purely so those
    /// get waited on too, so the yt-dlp process is actually confirmed killed (via
    /// <c>YtDlpClient</c>'s own cancellation-triggered <c>TryKill</c>) before the app can exit out
    /// from under it and orphan it.
    /// </summary>
    public void Dispose()
    {
        _stoppingCts.Cancel();
        foreach (var cts in _activeCancellations.Values)
            cts.Cancel();

        try
        {
            var pending = new List<Task>(_activeTasks.Count + 1) { _processingLoop };
            pending.AddRange(_activeTasks.Values);
            Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort shutdown — a wedged yt-dlp process shouldn't block the app from closing.
        }

        _stoppingCts.Dispose();
        _workAvailable.Dispose();
        _dbLock.Dispose();
        _connection.Dispose();
        _downloadEngine.Dispose();
        _torrentEngine.Dispose();
    }
}
