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
/// real thing. The one test that needs it to actually fail
/// (<see cref="EnqueueAndWaitAsync_Throws_WhenYtDlpIsUnavailable"/>) used to rely on yt-dlp being
/// absent from PATH in this environment, which broke the moment a dev machine (or CI image) happens
/// to have yt-dlp installed for real — surfaced exactly that way once yt-dlp got installed here to
/// fix actual downloads (see the "dependency provisioning" project memory). It now points
/// <see cref="YtDlpClient.UseResolvedPaths"/> at a path that can't exist instead, the same mechanism
/// <c>DependencyProvisioningService</c> uses in production — deterministic regardless of what's
/// actually on PATH, rather than depending on this environment's absence of yt-dlp.
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
        Assert.Equal("", stored.Title);
        Assert.Equal("mp4", stored.ContainerFormat);
    }

    [Fact]
    public async Task EnqueueAsync_PersistsAnAlreadyResolvedTitleAndContainerFormat()
    {
        // Views.AddDownloadDialog now resolves the video (and picks a container) before enqueuing
        // at all — passing that through here is what lets DownloadQueueService's own background
        // loop skip its redundant GetVideoInfoAsync call once this item is dequeued.
        CloseScheduleWindow();
        using var queue = new DownloadQueueService(new YtDlpClient(), DbPath());

        var enqueued = await queue.EnqueueAsync(
            "https://youtu.be/abc123", 1080, title: "Some Resolved Title", containerFormat: "mkv");

        Assert.Equal("Some Resolved Title", enqueued.Title);
        Assert.Equal("mkv", enqueued.ContainerFormat);

        var stored = Assert.Single(await queue.GetAllAsync());
        Assert.Equal("Some Resolved Title", stored.Title);
        Assert.Equal("mkv", stored.ContainerFormat);
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
        // See the class doc comment: pointed at a path that can't exist, rather than relying on
        // yt-dlp being absent from PATH, so this stays deterministic regardless of what's actually
        // installed on the machine running the test.
        var ytDlp = new YtDlpClient();
        ytDlp.UseResolvedPaths(Path.Combine(_tempDir, "yt-dlp-that-does-not-exist"), null);
        using var queue = new DownloadQueueService(ytDlp, DbPath());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.EnqueueAndWaitAsync("https://youtu.be/abc123", 1080));

        Assert.NotNull(ex.Message);
    }

    /// <summary>
    /// Regression test for a real report: with the default MaxConcurrentDownloads of 1, finishing
    /// one download didn't start the next Pending one — it just sat there. Root cause was
    /// ProcessItemAsync's finally block never releasing <c>_workAvailable</c>, so
    /// ProcessLoopAsync (already parked in its 30-second WaitAsync, having seen zero capacity the
    /// moment this item started) only noticed the freed-up slot on its next periodic recheck rather
    /// than immediately.
    ///
    /// Needs a fake "yt-dlp" that genuinely runs for a moment before failing (a shell script that
    /// sleeps, then exits non-zero) rather than the "point at a path that doesn't exist" trick the
    /// other tests here use: a missing executable fails <c>Process.Start</c> itself, synchronously,
    /// often before <c>ProcessItemAsync</c> ever actually suspends — which let the whole
    /// enqueue-run-fail-cleanup cycle for the first item complete inline, within one
    /// <c>ProcessLoopAsync</c> iteration, no wait ever entered, silently defeating this test
    /// regardless of whether the fix was present. A real (if trivial) child process forces a genuine
    /// await on process exit, so the loop's very next iteration reliably observes capacity still
    /// taken — the actual scenario the bug depends on.
    ///
    /// Both items are seeded as Pending *before* the queue instance under test is ever constructed
    /// (via a separate, schedule-closed instance pointed at the same database — reusing
    /// CloseScheduleWindow/EnqueueAsync rather than hand-rolling a raw SQL insert, since that already
    /// guarantees the exact same row shape production code writes), specifically so that starting the
    /// real instance's background loop is the *only* thing that ever calls
    /// <c>DownloadQueueService.EnqueueAsync</c>/<c>ResumeAsync</c>/<c>RetryAsync</c> — the only other
    /// calls that legitimately release <c>_workAvailable</c> — on it. If either item's own enqueue
    /// supplied that release instead, the loop's second item could start on that accidental signal
    /// even with the bug still present, silently defeating the test the same way the fast-fail
    /// executable did. Each is enqueued with its title already set, too, so ProcessItemAsync skips
    /// its own <c>GetVideoInfoAsync</c> call (which would otherwise invoke the fake script an extra,
    /// untimed time per item) and goes straight to the timed <c>DownloadAsync</c> call.
    ///
    /// Asserts on timing rather than outcome: both items failing well inside the 20s fact timeout —
    /// nowhere near the 30s stall the bug caused — proves the second item started right after the
    /// first finished, not on the loop's own periodic recheck.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task SecondPendingItem_StartsImmediately_AfterFirstFinishes()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Stand-in "yt-dlp" below is a shell script — see DownloadAsyncStallWatchdogTests for
            // the same platform restriction on the same trick.
            Assert.Skip("Stand-in \"yt-dlp\" is a shell script, Linux-only.");
            return;
        }

        var fakeYtDlp = Path.Combine(_tempDir, "fake-yt-dlp.sh");
        File.WriteAllText(fakeYtDlp, "#!/bin/sh\nsleep 0.3\nexit 1\n");
        File.SetUnixFileMode(fakeYtDlp,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var dbPath = DbPath();
        long firstId, secondId;

        CloseScheduleWindow();
        using (var seedQueue = new DownloadQueueService(new YtDlpClient(), dbPath))
        {
            firstId = (await seedQueue.EnqueueAsync("https://youtu.be/abc123", 1080, title: "First video")).Id;
            secondId = (await seedQueue.EnqueueAsync("https://youtu.be/def456", 1080, title: "Second video")).Id;
        }

        // Reopens the schedule so the real instance under test can actually process what was just
        // seeded, and points the download folder at _tempDir rather than the platform's real
        // Downloads folder — DownloadAsync (unlike GetVideoInfoAsync, which the other tests in this
        // class stop short of) creates that directory for real.
        SettingsService.Save(new AppSettings { DownloadFolder = _tempDir });

        var ytDlp = new YtDlpClient();
        ytDlp.UseResolvedPaths(fakeYtDlp, null);
        using var queue = new DownloadQueueService(ytDlp, dbPath);

        // Registered immediately after construction — before the loop's own first iteration has any
        // realistic chance to reach, let alone finish, the first item — so there's no window for
        // either item to complete unobserved (see WaitForCompletionAsync's own doc comment on why
        // registration is synchronous, before that method's first await).
        var firstWait = queue.WaitForCompletionAsync(firstId);
        var secondWait = queue.WaitForCompletionAsync(secondId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => firstWait);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondWait);
    }
}
