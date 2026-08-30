# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Yoink (formerly "YourPlaylistDownloader") is a cross-platform desktop app, built with Avalonia on .NET 10,
that downloads a YouTube video given its URL at a chosen resolution. It targets Linux as a first-class
platform, not just Windows. Treat the app as free to evolve toward a general-purpose download manager as
needed, without deferring changes to a numbered future release.

README.md no longer carries a numbered roadmap (removed deliberately for a cleaner, user-facing doc — see
its "What it is"/"Features" sections for the current pitch instead). This file's "roadmap step N" phrasing
below is purely internal shorthand for the fixed sequence the app was actually built in — steps 1-7 are
done and in use; step 8 (packaging for Ubuntu, then Windows/macOS) and the browser-extension half of
step 5's auto-catch mechanism are the two pieces still open. There's no other numbered plan to keep in sync
with it.

## Build & run

SDK-style project, so the regular `dotnet` CLI works directly — no MSBuild/NuGet workarounds needed.

- Build: `dotnet build Yoink.sln`
- Run: `dotnet run --project Yoink`

There are no lint or test commands/projects in this repo.

## Architecture

### Folder structure & namespaces

The project outgrew "every file directly under `Yoink/`" once the queue view (step 4) added a second
window and several service/model classes, so it's organized by role, folder-per-namespace:

- `Yoink/Views/` (namespace `Yoink.Views`) — every `Window` and its code-behind. UI logic lives here in
  plain code-behind, not a separate ViewModel layer — that's a deliberate, still-current choice (see below),
  not a leftover from before the reorg.
- `Yoink/Services/` (namespace `Yoink.Services`) — non-UI logic: persistence, the download engine, the
  yt-dlp wrapper, the queue. No Avalonia UI types belong here.
- `Yoink/Models/` (namespace `Yoink.Models`) — plain data classes/enums shared across services and views.
- `Yoink/Converters/` (namespace `Yoink.Converters`) — `IValueConverter` implementations for XAML bindings.
- `Yoink/Program.cs`, `Yoink/App.axaml`/`.axaml.cs`, `Yoink/app.manifest` — stay at the project root
  (namespace `Yoink`); they're bootstrap, not a feature area.

Keep adding files under whichever of these a new class fits, rather than dropping new files back at the
project root — that's exactly the flat structure this reorg moved away from.

### Views (`Yoink/Views/`)

- `MainWindow.axaml` / `.axaml.cs` — the queue view from README roadmap step 4: an `ItemsControl` bound to
  an `ObservableCollection<DownloadQueueItem>`, one row per queue entry (title, status, a progress bar
  when active/paused, and Pause/Resume/Cancel/Retry/"Show in folder" buttons — visibility of each driven by
  `DownloadQueueItem`'s `CanPause`/`CanResume`/etc. computed properties, no converters needed). This is also
  where "Recent downloads" ended up: the queue is never pruned, so completed/failed items just stay in the
  same list rather than living in a separate history view.
  - The collection is seeded once at startup from `DownloadQueueService.GetAllAsync()`, then kept live via
    `DownloadQueueService.ItemChanged`: `OnQueueItemChanged` replaces the matching item in the collection
    wholesale (by `Id`) rather than mutating one already bound in the UI. `DownloadQueueItem` is a plain
    class with no `INotifyPropertyChanged` — see the note on `DownloadQueueService.UpdateStatusAsync` below
    for why every `ItemChanged` payload is safe to treat as a complete replacement.
  - Each row's action buttons read the bound `DownloadQueueItem` off `((Control)sender).DataContext` and
    call straight into `DownloadQueueService` (`PauseAsync`/`ResumeAsync`/`CancelAsync`/`RetryAsync`).
  - "+ Add download" opens `AddDownloadDialog`; the queue view doesn't need anything back from it — the new
    item shows up on its own via `ItemChanged`.
  - A missing `yt-dlp` on PATH is checked once at startup and surfaced via `MessageBoxWindow`.
  - The header is now just the title and a single "⚙ Settings" button opening `SettingsWindow` (below) —
    Theme/clipboard-watch/minimize-to-tray all moved there as of roadmap step 7 ("a settings screen to
    control all of it"), so this window itself no longer holds any settings-editing controls or handlers.
  - `MainWindow_Opened` (not the constructor — the clipboard isn't guaranteed available before the window
    is attached to a screen) creates the `ClipboardWatcherService`, wired to `Window.Clipboard` via
    `ReadClipboardTextAsync` and to `AppSettings.ClipboardWatchEnabled` via a live `Func<bool>` passed to
    its constructor (see `ClipboardWatcherService` below for why it's a delegate rather than a settable
    property). When it raises `UrlDetected`, `OnClipboardUrlDetected` opens `AddDownloadDialog` pre-filled
    with the detected URL rather than queuing anything directly — see `ClipboardWatcherService`'s doc
    comment for why.
  - `OnQueueItemChanged` also fires a `NotificationService.NotifyAsync` call when an item's status
    transitions *into* `Completed`/`Failed` (comparing against the replaced item's previous status, not on
    every progress tick or on items loaded at startup) — the notifications half of roadmap step 6.
- `AddDownloadDialog.axaml` / `.axaml.cs` — the "add download" dialog half of step 4: URL + resolution
  picker, calls `DownloadQueueService.EnqueueAsync` directly and closes. Shown via the static
  `AddDownloadDialog.ShowAsync(owner, queue, prefillUrl)`, same pattern as `MessageBoxWindow.ShowAsync`.
  `prefillUrl` is optional — the clipboard watcher (above) is the only caller that passes one.
- `SettingsWindow.axaml` / `.axaml.cs` — the settings screen from README roadmap step 7 ("a settings
  screen to control all of it"): Theme, "Watch clipboard", "Keep running in tray" (all moved here from
  `MainWindow`'s header), plus the new concurrency/speed-limit/scheduling controls this step adds. Every
  control persists its own change immediately via `SettingsService` (read-modify-write, same pattern the
  header toggles used before) — there's no separate "Save" button, just "Close". Nothing here needs to
  push a live update anywhere: `DownloadQueueService`'s processing loop and `ClipboardWatcherService` both
  re-read settings fresh at the point they need them, so a change here takes effect on their very next
  check (within one processing-loop iteration or clipboard poll). Speed limits use `NumericUpDown` with 0
  or an empty field both meaning "unlimited" (`ToNullableLimit`); scheduling uses `TimePicker` (its
  `SelectedTime` is a `TimeSpan?`, converted to/from `AppSettings`' `TimeOnly` fields by hand since there's
  no built-in conversion). Opened modally via `new SettingsWindow().ShowDialog(owner)` from `MainWindow`'s
  "⚙ Settings" button.
- `MessageBoxWindow.axaml` / `.axaml.cs` — a minimal modal dialog (title + message + OK button) used in
  place of WinForms' `MessageBox`, which Avalonia doesn't provide out of the box. Use
  `MessageBoxWindow.ShowAsync(owner, message, title)` for anything that genuinely needs a blocking
  acknowledgment (validation errors, the yt-dlp-missing check) — per-download success/failure no longer
  goes through it now that the queue view shows each item's outcome inline, so don't reintroduce a modal
  popup per download.

### Services (`Yoink/Services/`)

- `SettingsService.cs` — persisted user preferences (currently just theme), paired with `Models/AppSettings.cs`.
  Reads/writes JSON at `%AppData%`/`~/.config`/`Yoink/settings.json` (via
  `Environment.SpecialFolder.ApplicationData`, so it works the same way cross-platform) and falls back to
  defaults if the file is missing or corrupt.
- `DownloadEngine.cs` — the generic core download engine from README roadmap step 1: a source-agnostic,
  resumable single-file HTTP downloader (range-request resume, progress via `IProgress<double>`,
  retry-with-backoff, cancellation). It writes to `<destination>.partial` and only moves the file into place
  on success. **Not currently wired into anything** — YouTube downloads go through `yt-dlp`'s own downloader
  instead (see below), since reimplementing yt-dlp's segment-download-and-mux behavior on top of this engine
  would just be redoing what it already does correctly. This class is the foundation for a later roadmap
  step: plain, non-YouTube direct-link downloads (e.g. the browser-extension/clipboard-watching "auto-catch"
  step).
- `YtDlpClient.cs` — the YouTube extraction layer from README roadmap step 2. Shells out to the `yt-dlp`
  CLI (must be on PATH — see README) for everything that talks to YouTube: `GetVideoInfoAsync` (title +
  available formats), `GetPlaylistEntriesAsync` (flat playlist/channel expansion), and `DownloadAsync`
  (download **and**, via ffmpeg, mux separate video-only/audio-only streams into one file — YouTube mostly
  doesn't serve pre-muxed formats above a low resolution anymore). `DownloadAsync` also takes an optional
  `rateLimitKBps`, passed straight through as yt-dlp's own `--limit-rate` (README roadmap step 7) — the
  caller (`DownloadQueueService`) is the one that works out what value that should be, this class just
  applies it. Parses yt-dlp's `--dump-json` output and its `[download] NN.N%` progress lines itself; no
  yt-dlp Python wrapper NuGet package is used. Defines its own small DTOs
  (`YtDlpFormat`/`YtDlpVideoInfo`/`YtDlpPlaylistEntry`) in the same file rather than under `Models/` —
  they're yt-dlp's own JSON contract, not app-wide models. See the class doc comment for why
  extraction/download is delegated to yt-dlp rather than reimplemented (same "far less maintenance"
  reasoning the README roadmap calls out) and why that means `DownloadEngine` sits unused for now.
- `DownloadQueueService.cs` (paired with `Models/DownloadQueueItem.cs`) — the persisted download queue from
  README roadmap step 3: SQLite-backed (`queue.db`, same config directory as settings, via
  `Microsoft.Data.Sqlite`), with `Pending`/`Active`/`Paused`/`Completed`/`Failed`/`Canceled` states and
  `Enqueue`/`Pause`/`Resume`/`Cancel`/`Retry`/`Reorder` operations, all persisted so a killed/crashed app
  recovers cleanly (any row still `Active` at startup — meaning the app died mid-download — is reset to
  `Pending`). Calls into `YtDlpClient` and raises `ItemChanged` as status/progress change. Pause/cancel both
  cancel the in-flight yt-dlp process; resuming re-invokes yt-dlp, which picks up from its own `.part` file
  rather than restarting. `UpdateStatusAsync` (used by e.g. pausing/canceling an item that isn't currently
  downloading) re-reads the full row after updating it rather than raising a bare `Id`+`Status` object —
  every `ItemChanged` subscriber, `Views.MainWindow` included, relies on each payload being a complete
  snapshot it can drop straight into place.
  - **Concurrency, speed limits, scheduling (README roadmap step 7)**: `ProcessLoopAsync` now runs up to
    `AppSettings.MaxConcurrentDownloads` items at once, using `_activeCancellations.Count` as the live count
    in flight (populated synchronously by `ProcessItemAsync` before its first await, so the loop's capacity
    check never races it). New items only start inside the configured schedule window when
    `AppSettings.SchedulingEnabled` is on (`IsWithinSchedule`/`IsWithinWindow` — the latter is a pure
    function of an explicit "now", split out purely so it's independently testable). Each download's
    `--limit-rate` comes from `ComputeRateLimitKBps`: the smaller of the per-download cap and this
    download's static share of the global cap (global ÷ `MaxConcurrentDownloads`, not a live rebalance —
    see `AppSettings.GlobalSpeedLimitKBps`'s doc comment for why). All three re-read settings fresh rather
    than being told about changes, matching the rest of the app's settings-handling pattern.
  - **Single shared connection**: all DB access goes through one `SqliteConnection` opened once in the
    constructor, serialized by a `SemaphoreSlim` (`WithLockAsync`) rather than each method opening its own
    connection. This isn't just tidiness — found the hard way while testing concurrency, opening a fresh
    connection per call meant two simultaneously-processing items' status writes measurably serialized on
    SQLite's file lock (with retry/backoff stalls of several seconds), which defeated the concurrency this
    step is supposed to add. A single connection sidesteps the contention entirely, since SQLite only
    supports one writer at a time regardless of how many connections ask for it.
- `ClipboardWatcherService.cs` — the clipboard-monitoring half of the "auto-catch mechanism" from README
  roadmap step 5 (the browser-extension half is not built — see the README roadmap note on why clipboard
  watching came first). Polls the clipboard on a timer (Avalonia's clipboard API has no change event, and
  there's no OS-agnostic native one either) via a caller-supplied `Func<Task<string?>>`, and raises
  `UrlDetected` when the text changes to something matching a conservative YouTube-URL regex. It never
  downloads anything itself — see `Views.MainWindow.OnClipboardUrlDetected` above for why detection and
  action are kept separate. Whether it's active is a caller-supplied `Func<bool> isEnabled`, checked fresh
  on every poll — not a settable property — so `Views.MainWindow` can hand it
  `() => SettingsService.Load().ClipboardWatchEnabled` once and never need to push a live update when the
  setting changes elsewhere (`SettingsWindow`, in particular). Known limitation, called out in its doc
  comment: Wayland compositors can restrict clipboard reads when the app isn't focused, so detection may be
  less reliable there than on X11 or Windows while the app is in the background.
- `NotificationService.cs` — the notifications half of "background operation & notifications" (README
  roadmap step 6). A static, stateless `NotifyAsync(title, message)` that shells out to `notify-send`
  (part of libnotify-bin) — **Linux only**; Windows/macOS are a documented gap (real toast notifications
  there need app identity/packaging this project doesn't have yet, so that's left for the packaging step).
  Best-effort like `YtDlpClient`'s error paths aren't — a missing `notify-send` or no notification daemon
  running just means silently no toast, never a crash or a `MessageBoxWindow`. Called from
  `Views.MainWindow.OnQueueItemChanged` when an item's status transitions *into* `Completed`/`Failed` (not
  on every event — see that method for how it detects a transition), which is deliberately a
  `Views.MainWindow` responsibility rather than `DownloadQueueService`'s: the queue service stays
  UI/OS-notification agnostic, and this still works while the window is hidden in the tray since hiding
  doesn't unsubscribe `ItemChanged` or stop the queue's background loop.

### Models (`Yoink/Models/`)

- `AppSettings.cs` — `AppSettings` + `ThemePreference`. Theme mapping itself
  (`ThemePreference` → Avalonia's `ThemeVariant`) stays in `App.ToThemeVariant` (see below), not here.
  Every property here is surfaced through `Views.SettingsWindow` and re-read fresh by whatever needs it
  (`DownloadQueueService`'s loop, `ClipboardWatcherService`'s poll, `App.axaml.cs`'s Closing handler) rather
  than pushed to it live — see the class doc comment. Besides `Theme`: `ClipboardWatchEnabled` (default
  `true`) and `MinimizeToTrayOnClose` (default `false` — see its own doc comment for why this one defaults
  off while clipboard watching defaults on); and, from roadmap step 7,
  `MaxConcurrentDownloads`/`PerDownloadSpeedLimitKBps`/`GlobalSpeedLimitKBps`/`SchedulingEnabled`/
  `ScheduleStart`/`ScheduleEnd` — see each property's doc comment, and `DownloadQueueService`'s notes above,
  for exactly how they combine.
- `DownloadQueueItem.cs` — `DownloadQueueItem` + `DownloadQueueStatus`. Plain data, no
  `INotifyPropertyChanged` — see the `Views.MainWindow`/`DownloadQueueService` notes above for how the
  queue view stays live without it. Carries presentational computed properties
  (`DisplayTitle`/`Subtitle`/`StatusText`/`ProgressPercent`/`CanPause`/etc.) so the queue view's
  `DataTemplate` can bind directly without converters — the same pattern the old, now-removed
  `DownloadHistoryEntry` used for the "Recent downloads" list.

### Converters (`Yoink/Converters/`)

- `DownloadQueueStatusToBrushConverter.cs` — the one `IValueConverter` in the app, mapping
  `DownloadQueueStatus` to the semantic Success/Error/muted brush (see `BRANDING.md`) for the queue view's
  status text.

### Root-level files

- `Program.cs` — entry point; builds and starts the Avalonia app (`AppBuilder.Configure<App>()...StartWithClassicDesktopLifetime`).
- `App.axaml` / `App.axaml.cs` — Avalonia `Application` bootstrap; sets the Fluent theme, creates `MainWindow` as the desktop lifetime's main window, and (`SetUpTrayIcon`) sets up the tray icon side of README roadmap step 6's "background operation": a `TrayIcon` with Show/Quit `NativeMenuItem`s, and a `MainWindow.Closing` handler that only ever intercepts a user-initiated close (`WindowCloseReason.WindowClosing` specifically — an app- or OS-driven shutdown is deliberately let through unmodified, or `desktop.TryShutdown()` from the tray menu's "Quit" would never actually terminate). What that intercepted close *does* depends on `AppSettings.MinimizeToTrayOnClose`: hide the window if it's on, or call `desktop.TryShutdown()` itself if it's off (the desktop lifetime is switched to `ShutdownMode.OnExplicitShutdown` up front specifically so this handler is always the one deciding, rather than an implicit shutdown racing it). Defaulting that setting to off — rather than clipboard watching's default-on — is deliberate: an unsupported tray (plain GNOME without an extension, for instance) would otherwise strand the window hidden with no visible way back.
- `Assets/tray-icon.png` — the one app icon, used for both the tray icon and `MainWindow`'s window icon (`Icon="/Assets/tray-icon.png"` in its XAML). Included via `<AvaloniaResource Include="Assets/**" />` in `Yoink.csproj`; reference new assets the same way rather than embedding them another way.
- Theme: `App.axaml` sets `RequestedThemeVariant` at startup from the saved preference (`ThemePreference.System/Light/Dark` in `AppSettings`). `System` maps to Avalonia's `ThemeVariant.Default`, which follows the OS light/dark setting live. `MainWindow` has a "Theme" combo box that flips `Application.Current.RequestedThemeVariant` immediately and persists the choice via `SettingsService`. `App.ToThemeVariant` is the single place that maps preference → `ThemeVariant`; reuse it rather than re-deriving the mapping.
- **`BRANDING.md`** (repo root) — the design tokens (colors, type, spacing/radius) implemented as Avalonia resources/style classes in `App.axaml`. Read it before adding new UI: reuse the existing style classes (`Card`, `AppTitle`, `Subtitle`, `SectionTitle`, `Caption`, `Primary` on buttons) instead of one-off styling, so new screens stay visually consistent with the rest of the app.

### Key dependency: yt-dlp (external, on PATH)

All YouTube interaction (metadata, playlist expansion, format listing, downloading, audio+video muxing)
goes through the `yt-dlp` command-line tool, invoked via `System.Diagnostics.Process` in `YtDlpClient` — not
a NuGet package. `yt-dlp` (and `ffmpeg`, for muxing) must be installed separately and discoverable on PATH;
see README.md's "Using it" section. This replaced `YoutubeExplode`, which reimplemented YouTube's
extraction logic in C# and, like the `YoutubeExtractor`/`YoutubeExtractorCore` packages before it, was prone
to breaking whenever YouTube changed something server-side (by the time it was replaced, it could no longer
find muxed streams for most videos at all). `yt-dlp` is maintained specifically to track those changes, which
per the README roadmap is far less maintenance than reimplementing the same cat-and-mouse game here.
