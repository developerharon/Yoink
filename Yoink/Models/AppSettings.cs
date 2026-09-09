using System;
using System.Collections.Generic;
using System.Linq;

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
    /// How many simultaneous HTTP connections <see cref="Services.DownloadEngine"/> may split one
    /// generic file download across, when the server supports it (see that class's own doc comment
    /// for exactly when it does or falls back to one connection). Doesn't apply to yt-dlp/video
    /// downloads — yt-dlp manages its own connections — or to a <see cref="Services.TorrentEngine"/>
    /// download, whose "how many connections" is really "how many peers", an entirely different
    /// concept MonoTorrent manages itself via its own swarm/choking logic, not something this app
    /// caps. Always clamped to at least 1 wherever it's read, same as <see cref="MaxConcurrentDownloads"/>.
    /// </summary>
    public int MaxConnectionsPerDownload { get; set; } = 4;

    /// <summary>
    /// KB/s cap applied to any single download — yt-dlp's own <c>--limit-rate</c> for a video,
    /// <see cref="Services.DownloadEngine"/>'s own rate limiter for a file, or MonoTorrent's
    /// <c>TorrentSettingsBuilder.MaximumDownloadRate</c> for a torrent, all fed the same computed
    /// value (see <see cref="Services.DownloadQueueService.ComputeRateLimitKBps"/>).
    /// Null or ≤0 means unlimited. If <see cref="GlobalSpeedLimitKBps"/> is also set, the smaller of
    /// the two wins.
    /// </summary>
    public int? PerDownloadSpeedLimitKBps { get; set; }

    /// <summary>
    /// KB/s cap meant to apply across every concurrently-active download combined. In practice it's
    /// split evenly by <see cref="MaxConcurrentDownloads"/> and applied to each download as it
    /// starts (each of the three downloaders above sets its own rate limit once, at the start of
    /// that download, and none of them can be adjusted while running, so this is a static split
    /// rather than a live rebalance across however many downloads happen to be active at a given
    /// moment). Null or ≤0 means unlimited.
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

    /// <summary>
    /// Whether <see cref="Services.NativeMessagingHost"/>'s Chrome native-messaging manifest has
    /// already been written to this user's browser config directories — see
    /// <c>Views.MainWindow.EnsureNativeMessagingHostRegistered</c>. Mirrors
    /// <see cref="Services.DependencyProvisioningService"/>'s own "no-op past first launch" shape;
    /// nothing in <c>Views.SettingsView</c> exposes this, same as <see cref="InstalledYtDlpVersion"/>
    /// above — purely internal bookkeeping, not a user-facing preference.
    /// </summary>
    public bool NativeMessagingHostRegistered { get; set; }

    /// <summary>
    /// Extra tracker URLs <see cref="Services.TorrentEngine"/> announces every torrent to, on top of
    /// whichever ones the magnet link/<c>.torrent</c> file already came with — added directly in
    /// response to a real "stuck at Fetching torrent metadata forever" report: a magnet with few or no
    /// trackers of its own relies entirely on DHT for peer discovery, and a cold DHT routing table
    /// (nothing bootstrapped yet, most likely on a fresh install or this app's very first torrent) can
    /// genuinely take longer than <see cref="Services.TorrentEngine.MetadataResolutionTimeout"/> to
    /// walk far enough to find any peers at all — whereas a tracker answers in one HTTP/UDP round trip,
    /// typically a few seconds, with no DHT walk needed. This is exactly the fix qBittorrent's own
    /// "Automatically add these trackers to new downloads" option exists for — the same idea, not a
    /// novel one. Defaults to <see cref="DefaultExtraTorrentTrackers"/>, a small curated set of
    /// well-known public trackers; editable/clearable in <c>Views.SettingsView</c> — an empty list
    /// disables this entirely (every torrent falls back to using only its own trackers/DHT, the
    /// original behavior). Public tracker uptime drifts over time by nature, so this list is worth
    /// revisiting occasionally rather than assumed permanently accurate — that's the whole reason it's
    /// user-editable rather than a hardcoded constant.
    /// </summary>
    public List<string> ExtraTorrentTrackers { get; set; } = DefaultExtraTorrentTrackers.ToList();

    /// <summary>
    /// The default value <see cref="ExtraTorrentTrackers"/> starts from (a fresh install) and what
    /// <c>Views.SettingsView</c>'s "Reset" button restores — a small, deliberately redundant set (UDP
    /// and HTTPS both represented, several independent operators) so metadata resolution has several
    /// independent chances to succeed quickly rather than depending on any one tracker's uptime.
    /// Public, not just internal, so <c>Views.SettingsView</c> can reference it directly for the Reset
    /// button without going through <see cref="AppSettings"/> construction.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExtraTorrentTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.tracker.cl:1337/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://open.stealth.si:80/announce",
        "https://tracker.tamersunion.org:443/announce"
    ];
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
