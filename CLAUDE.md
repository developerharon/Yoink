# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Yoink (formerly "YourPlaylistDownloader") is a cross-platform desktop app, built with Avalonia on .NET 10,
that downloads a YouTube video given its URL at a chosen resolution. It targets Linux as a first-class
platform, not just Windows. There is no version roadmap to track — treat the app as free to evolve toward
a general-purpose download manager as needed, without deferring changes to a numbered future release.

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
  - The "details/settings panel" half of step 4 is still minimal — just the existing Theme picker in the
    header. A real settings screen is more roadmap step 7's territory (speed limits, concurrency, scheduling
    all need one); revisit then rather than inventing settings early to fill the panel out.
  - The header's "Watch clipboard" `CheckBox` (`ChkClipboardWatch`) is the clipboard-monitoring half of
    roadmap step 5. `MainWindow_Opened` (not the constructor — the clipboard isn't guaranteed available
    before the window is attached to a screen) creates a `ClipboardWatcherService`, wired to
    `Window.Clipboard` via `ReadClipboardTextAsync`. When it raises `UrlDetected`, `OnClipboardUrlDetected`
    opens `AddDownloadDialog` pre-filled with the detected URL rather than queuing anything directly — see
    `ClipboardWatcherService`'s doc comment for why. The checkbox's state round-trips through
    `AppSettings.ClipboardWatchEnabled`.
- `AddDownloadDialog.axaml` / `.axaml.cs` — the "add download" dialog half of step 4: URL + resolution
  picker, calls `DownloadQueueService.EnqueueAsync` directly and closes. Shown via the static
  `AddDownloadDialog.ShowAsync(owner, queue, prefillUrl)`, same pattern as `MessageBoxWindow.ShowAsync`.
  `prefillUrl` is optional — the clipboard watcher (above) is the only caller that passes one.
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
  doesn't serve pre-muxed formats above a low resolution anymore). Parses yt-dlp's `--dump-json` output and
  its `[download] NN.N%` progress lines itself; no yt-dlp Python wrapper NuGet package is used. Defines its
  own small DTOs (`YtDlpFormat`/`YtDlpVideoInfo`/`YtDlpPlaylistEntry`) in the same file rather than under
  `Models/` — they're yt-dlp's own JSON contract, not app-wide models. See the class doc comment for why
  extraction/download is delegated to yt-dlp rather than reimplemented (same "far less maintenance"
  reasoning the README roadmap calls out) and why that means `DownloadEngine` sits unused for now.
- `DownloadQueueService.cs` (paired with `Models/DownloadQueueItem.cs`) — the persisted download queue from
  README roadmap step 3: SQLite-backed (`queue.db`, same config directory as settings, via
  `Microsoft.Data.Sqlite`), with `Pending`/`Active`/`Paused`/`Completed`/`Failed`/`Canceled` states and
  `Enqueue`/`Pause`/`Resume`/`Cancel`/`Retry`/`Reorder` operations, all persisted so a killed/crashed app
  recovers cleanly (any row still `Active` at startup — meaning the app died mid-download — is reset to
  `Pending`). A single background loop processes one pending item at a time (concurrency limits are a later
  roadmap step), calling into `YtDlpClient` and raising `ItemChanged` as status/progress change. Pause/cancel
  both cancel the in-flight yt-dlp process; resuming re-invokes yt-dlp, which picks up from its own `.part`
  file rather than restarting. `UpdateStatusAsync` (used by e.g. pausing/canceling an item that isn't
  currently downloading) re-reads the full row after updating it rather than raising a bare `Id`+`Status`
  object — every `ItemChanged` subscriber, `Views.MainWindow` included, relies on each payload being a
  complete snapshot it can drop straight into place.
- `ClipboardWatcherService.cs` — the clipboard-monitoring half of the "auto-catch mechanism" from README
  roadmap step 5 (the browser-extension half is not built — see the README roadmap note on why clipboard
  watching came first). Polls the clipboard on a timer (Avalonia's clipboard API has no change event, and
  there's no OS-agnostic native one either) via a caller-supplied `Func<Task<string?>>`, and raises
  `UrlDetected` when the text changes to something matching a conservative YouTube-URL regex. It never
  downloads anything itself — see `Views.MainWindow.OnClipboardUrlDetected` above for why detection and
  action are kept separate. Known limitation, called out in its doc comment: Wayland compositors can
  restrict clipboard reads when the app isn't focused, so detection may be less reliable there than on X11
  or Windows while the app is in the background.

### Models (`Yoink/Models/`)

- `AppSettings.cs` — `AppSettings` + `ThemePreference`. Theme mapping itself
  (`ThemePreference` → Avalonia's `ThemeVariant`) stays in `App.ToThemeVariant` (see below), not here.
  Also carries `ClipboardWatchEnabled` (default `true`), read/written by `Views.MainWindow`'s
  "Watch clipboard" checkbox.
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
- `App.axaml` / `App.axaml.cs` — Avalonia `Application` bootstrap; sets the Fluent theme and creates `MainWindow` as the desktop lifetime's main window.
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
