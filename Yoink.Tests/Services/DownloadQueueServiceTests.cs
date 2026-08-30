using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// <see cref="YtDlpClient"/> is sealed with no interface, so it can't be faked — these tests use the
/// real thing against yt-dlp, deliberately absent from this environment (confirmed: not on PATH here,
/// and not preinstalled on GitHub's ubuntu-latest runners either), which is actually useful: any item
/// this suite lets the background processing loop pick up will reliably and quickly fail (a
/// <c>Win32Exception</c> starting the process, caught by <c>ProcessItemAsync</c>'s error handling —
/// see DownloadQueueService.cs), never hang or hit the network.
///
/// For everything else — CRUD/persistence/status transitions that should NOT race the background
/// loop's own polling — each test closes the schedule window (<c>SchedulingEnabled=true</c>,
/// <c>ScheduleStart == ScheduleEnd</c>, which <see cref="IsWithinWindowTests.ZeroWidthWindow_IsNeverWithin"/>
/// confirms is never "within") via a redirected <see cref="SettingsService.SettingsPath"/>, so
/// <c>IsWithinSchedule</c> is false and the loop never dequeues anything — deterministic rather than
/// racy.
/// </summary>
public class DownloadQueueServiceTests : IDisposable
{
    private readonly string _originalSettingsPath = SettingsService.SettingsPath;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"yoink-tests-{Guid.NewGuid():N}");

    public DownloadQueueServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        SettingsService.SettingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        SettingsService.SettingsPath = _originalSettingsPath;
        Directory.Delete(_tempDir, recursive: true);
    }

    private string DbPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(_tempDir, $"{name}.db");

    /// <summary>Closes the schedule window so the background processing loop never picks anything
    /// up — see the class doc comment.</summary>
    private static void CloseScheduleWindow()
    {
        var noon = new TimeOnly(12, 0);
        SettingsService.Save(new AppSettings { SchedulingEnabled = true, ScheduleStart = noon, ScheduleEnd = noon });
    }

    [Fact]
    public async Task EnqueueAsync_PersistsAPendingItem_RetrievableViaGetAllAsync()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        var enqueued = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);

        Assert.Equal(DownloadQueueStatus.Pending, enqueued.Status);
        Assert.True(enqueued.Id > 0);

        var all = await queue.GetAllAsync();
        var stored = Assert.Single(all);
        Assert.Equal(enqueued.Id, stored.Id);
        Assert.Equal("https://youtu.be/abc123", stored.Url);
        Assert.Equal(1080, stored.Resolution);
        Assert.Equal(DownloadQueueStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task EnqueueAsync_Rejects_BlankUrl()
    {
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync("   ", 1080));
    }

    [Fact]
    public async Task ReorderAsync_UpdatesPosition()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        var first = await queue.EnqueueAsync("https://youtu.be/first", 720);
        var second = await queue.EnqueueAsync("https://youtu.be/second", 720);

        await queue.ReorderAsync(first.Id, newPosition: 5);

        var all = await queue.GetAllAsync();
        Assert.Equal(5, all.Single(i => i.Id == first.Id).Position);
        Assert.Equal(1, all.Single(i => i.Id == second.Id).Position); // untouched — enqueued 2nd, so starts at 1
    }

    [Fact]
    public async Task PauseAsync_OnAPendingItem_MarksItPaused()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());
        var item = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);

        await queue.PauseAsync(item.Id);

        var stored = (await queue.GetAllAsync()).Single(i => i.Id == item.Id);
        Assert.Equal(DownloadQueueStatus.Paused, stored.Status);
    }

    [Fact]
    public async Task ResumeAsync_OnAPausedItem_MarksItPendingAgain()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());
        var item = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);
        await queue.PauseAsync(item.Id);

        await queue.ResumeAsync(item.Id);

        var stored = (await queue.GetAllAsync()).Single(i => i.Id == item.Id);
        Assert.Equal(DownloadQueueStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task CancelAsync_OnAPendingItem_MarksItCanceled()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());
        var item = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);

        await queue.CancelAsync(item.Id);

        var stored = (await queue.GetAllAsync()).Single(i => i.Id == item.Id);
        Assert.Equal(DownloadQueueStatus.Canceled, stored.Status);
    }

    [Fact]
    public async Task RetryAsync_OnACanceledItem_MarksItPendingAndClearsError()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());
        var item = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);
        await queue.CancelAsync(item.Id);

        await queue.RetryAsync(item.Id);

        var stored = (await queue.GetAllAsync()).Single(i => i.Id == item.Id);
        Assert.Equal(DownloadQueueStatus.Pending, stored.Status);
        Assert.Null(stored.ErrorMessage);
    }

    [Fact]
    public async Task ItemChanged_FiresOnEnqueueAndOnStatusUpdate()
    {
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        var seenStatuses = new System.Collections.Concurrent.ConcurrentQueue<DownloadQueueStatus>();
        queue.ItemChanged += item => seenStatuses.Enqueue(item.Status);

        var item = await queue.EnqueueAsync("https://youtu.be/abc123", 1080);
        await queue.PauseAsync(item.Id);

        Assert.Contains(DownloadQueueStatus.Pending, seenStatuses);
        Assert.Contains(DownloadQueueStatus.Paused, seenStatuses);
    }

    /// <summary>
    /// End-to-end through the real background processing loop (schedule left open, unlike every test
    /// above) — with yt-dlp genuinely absent, this reliably reaches Failed within a couple of seconds
    /// rather than hanging or touching the network, exercising the actual error path
    /// WaitForCompletionAsync's own doc comment promises: it throws rather than returning a
    /// non-Completed item.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task EnqueueAndWaitAsync_Throws_WhenYtDlpIsUnavailable()
    {
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.EnqueueAndWaitAsync("https://youtu.be/abc123", 1080));

        Assert.NotNull(ex.Message);
    }
}
