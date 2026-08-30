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

- `Yoink/Program.cs` — entry point; builds and starts the Avalonia app (`AppBuilder.Configure<App>()...StartWithClassicDesktopLifetime`).
- `Yoink/App.axaml` / `App.axaml.cs` — Avalonia `Application` bootstrap; sets the Fluent theme and creates `MainWindow` as the desktop lifetime's main window.
- `Yoink/MainWindow.axaml` / `MainWindow.axaml.cs` — the entire app UI and logic lives here, in code-behind (no MVVM/ViewModel layer — keep new features in this same style unless the UI grows enough to justify introducing one):
  - `BtnDownload_Click` reads the URL (`TxtUrl`) and desired resolution (`CboResolution`), enqueues a `DownloadQueueService` item, and awaits its outcome via `WaitForCompletionAsync` — see `DownloadQueueService` below. It's split into enqueue-then-wait (rather than one `EnqueueAndWaitAsync` call) specifically so `_trackedItemId` is set before the download starts, so live progress events for that item aren't missed.
  - Download progress arrives via `DownloadQueueService.ItemChanged` (filtered to whichever item id is currently tracked), marshalled to the UI thread with `Dispatcher.UIThread.Post` before updating `ProgressBar`/`LblPercentage`.
  - Errors (download failures, cancellation) are surfaced via `MessageBoxWindow`, not left to propagate uncaught. A missing `yt-dlp` on PATH is checked once at startup and also surfaced this way.
- `Yoink/MessageBoxWindow.axaml` / `.axaml.cs` — a minimal modal dialog (title + message + OK button) used in place of WinForms' `MessageBox`, which Avalonia doesn't provide out of the box. Use `MessageBoxWindow.ShowAsync(owner, message, title)` for any new user-facing success/error dialogs rather than adding another one-off dialog type.
- `Yoink/AppSettings.cs` / `Yoink/SettingsService.cs` — persisted user preferences (currently just theme). `SettingsService` reads/writes JSON at `%AppData%`/`~/.config`/`Yoink/settings.json` (via `Environment.SpecialFolder.ApplicationData`, so it works the same way cross-platform) and falls back to defaults if the file is missing or corrupt. This is one of the few exceptions to "everything lives in MainWindow" — it's persistence, not UI, so it stays a separate class; keep future non-UI state here rather than folding it into the window code-behind.
- Theme: `App.axaml` sets `RequestedThemeVariant` at startup from the saved preference (`ThemePreference.System/Light/Dark` in `AppSettings`). `System` maps to Avalonia's `ThemeVariant.Default`, which follows the OS light/dark setting live. `MainWindow` has a "Theme" combo box that flips `Application.Current.RequestedThemeVariant` immediately and persists the choice via `SettingsService`. `App.ToThemeVariant` is the single place that maps preference → `ThemeVariant`; reuse it rather than re-deriving the mapping.
- `Yoink/DownloadHistoryEntry.cs` / `Yoink/DownloadHistoryService.cs` — the "Recent downloads" list shown under the download form. Same pattern as settings: plain data class + a static service that reads/writes JSON (`history.json`, same config directory), capped at the 50 most recent entries. `MainWindow` keeps the live list in an `ObservableCollection<DownloadHistoryEntry>` bound to `LstHistory.ItemsSource`; every successful *and* failed download appends a new entry and re-saves.
- `Yoink/DownloadStatusToBrushConverter.cs` — the one `IValueConverter` in the app, mapping `DownloadStatus` to the semantic Success/Error brush (see `BRANDING.md`) for the history list's status text.
- `Yoink/DownloadEngine.cs` — the generic core download engine from README roadmap step 1: a source-agnostic, resumable single-file HTTP downloader (range-request resume, progress via `IProgress<double>`, retry-with-backoff, cancellation). It writes to `<destination>.partial` and only moves the file into place on success. **Not currently wired into anything** — YouTube downloads go through `yt-dlp`'s own downloader instead (see below), since reimplementing yt-dlp's segment-download-and-mux behavior on top of this engine would just be redoing what it already does correctly. This class is the foundation for a later roadmap step: plain, non-YouTube direct-link downloads (e.g. the browser-extension/clipboard-watching "auto-catch" step).
- `Yoink/YtDlpClient.cs` — the YouTube extraction layer from README roadmap step 2. Shells out to the `yt-dlp` CLI (must be on PATH — see README) for everything that talks to YouTube: `GetVideoInfoAsync` (title + available formats), `GetPlaylistEntriesAsync` (flat playlist/channel expansion), and `DownloadAsync` (download **and**, via ffmpeg, mux separate video-only/audio-only streams into one file — YouTube mostly doesn't serve pre-muxed formats above a low resolution anymore). Parses yt-dlp's `--dump-json` output and its `[download] NN.N%` progress lines itself; no yt-dlp Python wrapper NuGet package is used. See the class doc comment for why extraction/download is delegated to yt-dlp rather than reimplemented (same "far less maintenance" reasoning the README roadmap calls out) and why that means `DownloadEngine` sits unused for now.
- `Yoink/DownloadQueueItem.cs` / `Yoink/DownloadQueueService.cs` — the persisted download queue from README roadmap step 3: SQLite-backed (`queue.db`, same config directory as settings/history, via `Microsoft.Data.Sqlite`), with `Pending`/`Active`/`Paused`/`Completed`/`Failed`/`Canceled` states and `Enqueue`/`Pause`/`Resume`/`Cancel`/`Retry`/`Reorder` operations, all persisted so a killed/crashed app recovers cleanly (any row still `Active` at startup — meaning the app died mid-download — is reset to `Pending`). A single background loop processes one pending item at a time (concurrency limits are a later roadmap step), calling into `YtDlpClient` and raising `ItemChanged` as status/progress change. Pause/cancel both cancel the in-flight yt-dlp process; resuming re-invokes yt-dlp, which picks up from its own `.part` file rather than restarting. **The queue view itself — a list UI to browse/manage items and actually reach Pause/Resume/Reorder — is roadmap step 4 and doesn't exist yet.** Today `MainWindow` only ever has at most one item in flight, via `EnqueueAsync` + `WaitForCompletionAsync`, so every download already goes through this persisted, resumable, retryable path even though the UI still looks like a single one-shot download.
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
