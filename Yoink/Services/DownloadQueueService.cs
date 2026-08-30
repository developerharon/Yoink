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
    private readonly ConcurrentDictionary<long, bool> _pauseRequested = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DownloadQueueItem>> _waiters = new();
    private readonly Task _processingLoop;

    public DownloadQueueService(YtDlpClient ytDlp, string? databasePath = null)
    {
        _ytDlp = ytDlp;

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

    public Task<IReadOnlyList<DownloadQueueItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM download_queue ORDER BY Position ASC";

            var items = new List<DownloadQueueItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(ReadItem(reader));

            return (IReadOnlyList<DownloadQueueItem>)items;
        }, cancellationToken);

    public async Task<DownloadQueueItem> EnqueueAsync(string url, int resolution, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.", nameof(url));

        var id = await WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO download_queue (Url, Title, Resolution, FilePath, Status, Progress, ErrorMessage, Position, CreatedAt)
                VALUES ($url, '', $resolution, NULL, $status, 0, NULL,
                        (SELECT COALESCE(MAX(Position), -1) + 1 FROM download_queue), $createdAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$resolution", resolution);
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);

        var item = new DownloadQueueItem
        {
            Id = id,
            Url = url,
            Resolution = resolution,
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
        var item = await EnqueueAsync(url, resolution, cancellationToken).ConfigureAwait(false);
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

            _ = ProcessItemAsync(next, stoppingToken);
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
            if (string.IsNullOrEmpty(item.Title))
            {
                var info = await _ytDlp.GetVideoInfoAsync(item.Url, itemCts.Token).ConfigureAwait(false);
                item.Title = info.Title;
            }

            item.FilePath ??= BuildDestinationPath(item.Title);

            var selector = BuildFormatSelector(item.Resolution);
            var progress = new Progress<double>(p =>
            {
                item.Progress = p;
                RaiseChanged(item);
            });

            // Re-read settings rather than reusing whatever ProcessLoopAsync last saw: this item
            // may have been sitting Pending for a while, and the speed-limit/concurrency settings
            // could have changed since.
            var rateLimitKBps = ComputeRateLimitKBps(SettingsService.Load());

            await _ytDlp.DownloadAsync(
                item.Url,
                selector,
                item.FilePath,
                expectedSegmentCount: 2,
                rateLimitKBps: rateLimitKBps,
                progress: progress,
                cancellationToken: itemCts.Token).ConfigureAwait(false);

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
        }
    }

    private void CompleteWaiter(DownloadQueueItem item)
    {
        if (_waiters.TryRemove(item.Id, out var tcs))
            tcs.TrySetResult(item);
    }

    // internal (not private) so Yoink.Tests can exercise these two directly.
    internal static string BuildFormatSelector(int resolution) =>
        $"bestvideo[height<={resolution}]+bestaudio/best[height<={resolution}]/best";

    internal static string BuildDestinationPath(string title)
    {
        var fileName = string.Concat(title.Split(Path.GetInvalidFileNameChars())) + ".mp4";
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private Task<DownloadQueueItem?> GetNextPendingAsync(CancellationToken cancellationToken) =>
        WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM download_queue WHERE Status = $status ORDER BY Position ASC LIMIT 1";
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
        }, cancellationToken);

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
                SET Title = $title, FilePath = $filePath, Status = $status, Progress = $progress, ErrorMessage = $errorMessage
                WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$filePath", (object?)item.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", item.Status.ToString());
            command.Parameters.AddWithValue("$progress", item.Progress);
            command.Parameters.AddWithValue("$errorMessage", (object?)item.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", item.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private void RaiseChanged(DownloadQueueItem item) => ItemChanged?.Invoke(item);

    private static DownloadQueueItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        Url = reader.GetString(reader.GetOrdinal("Url")),
        Title = reader.GetString(reader.GetOrdinal("Title")),
        Resolution = reader.GetInt32(reader.GetOrdinal("Resolution")),
        FilePath = reader.IsDBNull(reader.GetOrdinal("FilePath")) ? null : reader.GetString(reader.GetOrdinal("FilePath")),
        Status = Enum.Parse<DownloadQueueStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        Progress = reader.GetDouble(reader.GetOrdinal("Progress")),
        ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
        Position = reader.GetInt32(reader.GetOrdinal("Position")),
        CreatedAt = DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("CreatedAt")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
    };

    /// <summary>
    /// Called from the constructor, before anything else could possibly touch <see cref="_connection"/>
    /// — no locking needed yet.
    /// </summary>
    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
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

    public void Dispose()
    {
        _stoppingCts.Cancel();
        foreach (var cts in _activeCancellations.Values)
            cts.Cancel();

        try
        {
            _processingLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort shutdown — a wedged yt-dlp process shouldn't block the app from closing.
        }

        _stoppingCts.Dispose();
        _workAvailable.Dispose();
        _dbLock.Dispose();
        _connection.Dispose();
    }
}
