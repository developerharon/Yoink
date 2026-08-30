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
/// support. A single background loop processes one item at a time (concurrency limits are a later
/// roadmap step) via <see cref="YtDlpClient"/> for resolution/download and reports progress
/// through <see cref="ItemChanged"/>.
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
    private readonly string _connectionString;
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

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        EnsureSchema();
        RecoverStaleActiveItems();

        _processingLoop = Task.Run(() => ProcessLoopAsync(_stoppingCts.Token));
    }

    /// <summary>Raised whenever an item's status or progress changes — a future queue view binds to this.</summary>
    public event Action<DownloadQueueItem>? ItemChanged;

    public async Task<IReadOnlyList<DownloadQueueItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM download_queue ORDER BY Position ASC";

        var items = new List<DownloadQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadItem(reader));

        return items;
    }

    public async Task<DownloadQueueItem> EnqueueAsync(string url, int resolution, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.", nameof(url));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
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

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

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

    public async Task ReorderAsync(long id, int newPosition, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_queue SET Position = $position WHERE Id = $id";
        command.Parameters.AddWithValue("$position", newPosition);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DownloadQueueItem? next;
            try
            {
                next = await GetNextPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (next is null)
            {
                try
                {
                    await _workAvailable.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await ProcessItemAsync(next, stoppingToken).ConfigureAwait(false);
        }
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

            await _ytDlp.DownloadAsync(
                item.Url,
                selector,
                item.FilePath,
                expectedSegmentCount: 2,
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

    private static string BuildFormatSelector(int resolution) =>
        $"bestvideo[height<={resolution}]+bestaudio/best[height<={resolution}]/best";

    private static string BuildDestinationPath(string title)
    {
        var fileName = string.Concat(title.Split(Path.GetInvalidFileNameChars())) + ".mp4";
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private async Task<DownloadQueueItem?> GetNextPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM download_queue WHERE Status = $status ORDER BY Position ASC LIMIT 1";
        command.Parameters.AddWithValue("$status", DownloadQueueStatus.Pending.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    private async Task UpdateStatusAsync(
        long id,
        DownloadQueueStatus status,
        bool clearError = false,
        bool completeWaiter = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = clearError
                ? "UPDATE download_queue SET Status = $status, ErrorMessage = NULL WHERE Id = $id"
                : "UPDATE download_queue SET Status = $status WHERE Id = $id";
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Re-read the full row rather than constructing a bare Id+Status item: callers (the queue
        // view included) treat every ItemChanged payload as a complete, authoritative snapshot, so
        // a partial one would blank out Title/Progress/etc. wherever it's applied.
        var item = await GetItemAsync(id, connection, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return;

        RaiseChanged(item);

        if (completeWaiter)
            CompleteWaiter(item);
    }

    private static async Task<DownloadQueueItem?> GetItemAsync(long id, SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM download_queue WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    private async Task PersistAsync(DownloadQueueItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
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
    }

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

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
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
    /// so the queue picks it up (and yt-dlp resumes it) again instead of leaving it stuck.
    /// </summary>
    private void RecoverStaleActiveItems()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_queue SET Status = $pending WHERE Status = $active";
        command.Parameters.AddWithValue("$pending", DownloadQueueStatus.Pending.ToString());
        command.Parameters.AddWithValue("$active", DownloadQueueStatus.Active.ToString());
        command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
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
    }
}
