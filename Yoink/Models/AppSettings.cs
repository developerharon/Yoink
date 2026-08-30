using System;

namespace Yoink.Models;

/// <summary>
/// User-configurable preferences for the app. Persisted to disk via <see cref="SettingsService"/>.
/// All of it is surfaced through <c>Views.SettingsWindow</c>; nothing here needs pushing to a live
/// component when it changes — <see cref="Services.DownloadQueueService"/> and
/// <see cref="Services.ClipboardWatcherService"/> both re-read settings fresh at the point they
/// need them (each processing-loop iteration, each clipboard poll) rather than being told about
/// changes, so a change here takes effect within one tick of whatever's reading it.
/// </summary>
public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Whether <see cref="Services.ClipboardWatcherService"/> is active. On by default — it only
    /// ever prompts before queuing anything, never downloads silently — but stays easy to turn off
    /// for anyone who'd rather not have their clipboard polled at all.
    /// </summary>
    public bool ClipboardWatchEnabled { get; set; } = true;

    /// <summary>
    /// Whether closing the main window hides it to the tray icon instead of quitting the app (see
    /// <c>App.SetUpTrayIcon</c>). Off by default: on a Linux desktop without tray/StatusNotifierItem
    /// support (plain GNOME without an extension, for instance) the tray icon simply won't be
    /// visible, and hiding-not-closing by default there would strand the window with no way back.
    /// Opt-in once someone's confirmed their tray actually shows it.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; }

    /// <summary>
    /// How many downloads <see cref="Services.DownloadQueueService"/> runs at once. Always clamped
    /// to at least 1 wherever it's read, so a stray 0 or negative value in a hand-edited
    /// settings.json can't wedge the queue.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 1;

    /// <summary>
    /// KB/s cap applied to any single download (yt-dlp's own <c>--limit-rate</c>). Null or ≤0 means
    /// unlimited. If <see cref="GlobalSpeedLimitKBps"/> is also set, the smaller of the two wins —
    /// see <see cref="Services.DownloadQueueService"/> for exactly how they combine.
    /// </summary>
    public int? PerDownloadSpeedLimitKBps { get; set; }

    /// <summary>
    /// KB/s cap meant to apply across every concurrently-active download combined. In practice it's
    /// split evenly by <see cref="MaxConcurrentDownloads"/> and applied to each download as it
    /// starts (yt-dlp's <c>--limit-rate</c> is set once at process launch and can't be adjusted
    /// while it's running, so this is a static split rather than a live rebalance across however
    /// many downloads happen to be active at a given moment). Null or ≤0 means unlimited.
    /// </summary>
    public int? GlobalSpeedLimitKBps { get; set; }

    /// <summary>
    /// When true, <see cref="Services.DownloadQueueService"/> only starts new downloads inside the
    /// <see cref="ScheduleStart"/>-<see cref="ScheduleEnd"/> window (which may wrap past midnight,
    /// e.g. 22:00-06:00 for "overnight"). Downloads already running when the window closes are left
    /// to finish rather than being paused mid-transfer — this only gates picking up new ones.
    /// </summary>
    public bool SchedulingEnabled { get; set; }

    public TimeOnly ScheduleStart { get; set; } = new(22, 0);
    public TimeOnly ScheduleEnd { get; set; } = new(6, 0);
}

/// <summary>
/// The theme the user has chosen. <see cref="System"/> follows the OS light/dark setting and updates
/// automatically if the OS setting changes while the app is running.
/// </summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
}
