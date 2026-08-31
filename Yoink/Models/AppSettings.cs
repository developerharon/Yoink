using System;

namespace Yoink.Models;

/// <summary>
/// User-configurable preferences for the app. Persisted to disk via <see cref="SettingsService"/>.
/// All of it is surfaced through <c>Views.SettingsView</c>; nothing here needs pushing to a live
/// component when it changes — <see cref="Services.DownloadQueueService"/> and
/// <see cref="Services.ClipboardWatcherService"/> both re-read settings fresh at the point they
/// need them (each processing-loop iteration, each clipboard poll) rather than being told about
/// changes, so a change here takes effect within one tick of whatever's reading it.
/// </summary>
public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// The one configurable color slot in the brand system (see BRANDING.md) — everything else
    /// (neutrals, semantic status colors) is fixed. Purely cosmetic: it only recolors primary
    /// buttons/progress bars/focus rings via <see cref="App.ApplyAccent"/>, so "pick whatever
    /// matches your mood" in <c>Views.SettingsView</c> is a completely safe thing to invite.
    /// </summary>
    public AccentColor AccentColor { get; set; } = AccentColor.Blue;

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
    /// Where finished downloads are saved. Null or blank means "use the platform default" —
    /// resolved fresh via <see cref="Services.SettingsService.GetDefaultDownloadFolder"/> rather
    /// than being baked in here at settings-creation time, so a plain unconfigured install always
    /// tracks whatever the OS actually considers the Downloads folder (including, on Linux, the
    /// user's own XDG_DOWNLOAD_DIR) rather than a guess frozen at first run.
    /// </summary>
    public string? DownloadFolder { get; set; }

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

    /// <summary>
    /// When <see cref="Views.MainWindow"/> last checked <see cref="Services.UpdateService"/> for a
    /// new release. Null means "never" — always worth checking. Throttles the check to roughly once
    /// a day rather than hitting GitHub's release feed on every launch.
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// The yt-dlp/ffmpeg version last installed by <see cref="Services.DependencyProvisioningService"/>
    /// into its managed folder — null whenever Yoink isn't managing that dependency itself (either
    /// one's on PATH, or neither has been provisioned yet). Compared against the latest upstream
    /// build on each check so a managed copy is only re-downloaded when something actually changed.
    /// <see cref="InstalledFfmpegBuildTag"/> holds a Last-Modified/ETag string rather than a real
    /// version number, since ffmpeg's static builds don't expose one the way yt-dlp's date-stamped
    /// releases do — see that class for exactly how each is derived.
    /// </summary>
    public string? InstalledYtDlpVersion { get; set; }

    public string? InstalledFfmpegBuildTag { get; set; }

    /// <summary>
    /// When yt-dlp/ffmpeg were last checked for a newer managed build. Null means "never". Rides
    /// the same once-a-day cadence as <see cref="LastUpdateCheckUtc"/> rather than its own separate
    /// timer — see <see cref="Services.DependencyProvisioningService.CheckForManagedUpdatesAsync"/>.
    /// </summary>
    public DateTimeOffset? LastDependencyCheckUtc { get; set; }
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

/// <summary>
/// The five accent presets from BRANDING.md — the "blue button, or Ubuntu orange, or purple"
/// setting. Each maps to a base/hover/active/soft/on-accent set of colors in
/// <see cref="App.ApplyAccent"/>; adding a sixth later is one more case there plus one more swatch
/// in <c>Views.SettingsView</c>.
/// </summary>
public enum AccentColor
{
    Blue,
    Orange,
    Purple,
    Green,
    Red
}
