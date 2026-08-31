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
    // Every ProcessItemAsync task ProcessLoopAsync fires off, so Dispose can actually wait for
    // them — see Dispose's doc comment for why this is load-bearing, not redundant with
    // _processingLoop.
    private readonly ConcurrentDictionary<long, Task> _activeTasks = new();
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
    public async Task<DownloadQueueItem> EnqueueAsync(
        string url,
        int resolution,
        string title = "",
        string containerFormat = "mp4",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.", nameof(url));

        var id = await WithLockAsync(async () =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO download_queue (Url, Title, Resolution, ContainerFormat, FilePath, Status, Progress, ErrorMessage, Position, CreatedAt)
                VALUES ($url, $title, $resolution, $containerFormat, NULL, $status, 0, NULL,
                        (SELECT COALESCE(MAX(Position), -1) + 1 FROM download_queue), $createdAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$resolution", resolution);
            command.Parameters.AddWithValue("$containerFormat", containerFormat);
            command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);

        var item = new DownloadQueueItem
        {
            Id = id,
            Url = url,
            Title = title,
            Resolution = resolution,
            ContainerFormat = containerFormat,
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
            if (string.IsNullOrEmpty(item.Title))
            {
                var info = await _ytDlp.GetVideoInfoAsync(item.Url, itemCts.Token).ConfigureAwait(false);
                item.Title = info.Title;
            }

            // Re-read settings rather than reusing whatever ProcessLoopAsync last saw: this item
            // may have been sitting Pending for a while, and the download-folder/speed-limit
            // settings could have changed since.
            var settings = SettingsService.Load();

            item.FilePath ??= BuildDestinationPath(item.Title, ResolveDownloadFolder(settings), item.ContainerFormat);

            var selector = BuildFormatSelector(item.Resolution);
            var progress = new Progress<YtDlpDownloadProgress>(p =>
            {
                item.Progress = p.Fraction;
                item.DownloadedBytes = p.BytesDownloaded;
                item.TotalBytes = p.TotalBytes;
                RaiseChanged(item);
            });

            var rateLimitKBps = ComputeRateLimitKBps(settings);

            await _ytDlp.DownloadAsync(
                item.Url,
                selector,
                item.FilePath,
                expectedSegmentCount: 2,
                rateLimitKBps: rateLimitKBps,
                containerFormat: item.ContainerFormat,
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
            _activeTasks.TryRemove(item.Id, out _);
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

    internal static string BuildDestinationPath(string title, string downloadFolder, string containerFormat = "mp4")
    {
        var fileName = string.Concat(title.Split(Path.GetInvalidFileNameChars())) + "." + containerFormat;
        return Path.Combine(downloadFolder, fileName);
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
        int Status, int Progress, int ErrorMessage, int Position, int CreatedAt)
    {
        public static ColumnOrdinals FromReader(SqliteDataReader reader) => new(
            reader.GetOrdinal("Id"), reader.GetOrdinal("Url"), reader.GetOrdinal("Title"),
            reader.GetOrdinal("Resolution"), reader.GetOrdinal("ContainerFormat"), reader.GetOrdinal("FilePath"),
            reader.GetOrdinal("Status"), reader.GetOrdinal("Progress"), reader.GetOrdinal("ErrorMessage"),
            reader.GetOrdinal("Position"), reader.GetOrdinal("CreatedAt"));
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
            reader.GetString(o.CreatedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
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
    }
}
