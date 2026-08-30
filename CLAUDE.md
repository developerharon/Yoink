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
  - `BtnDownload_Click` reads the URL (`TxtUrl`) and desired resolution (`CboResolution`) and calls `DownloadVideoAsync`.
  - `DownloadVideoAsync` uses `YoutubeExplode` (`YoutubeClient`) to fetch the video's stream manifest, then picks the muxed MP4 stream whose height is closest to the requested resolution (YouTube stopped serving muxed streams above 720p, so 1080/1440 selections intentionally fall back to the best available muxed stream rather than failing). The file is saved into the app's own base directory (`AppContext.BaseDirectory`) using the video's title as the filename.
  - Download progress is reported via `IProgress<double>`, marshalled back to the UI thread with `Dispatcher.UIThread.Post` before updating `ProgressBar`/`LblPercentage`.
  - Errors (including "no matching stream found") are surfaced via `MessageBoxWindow`, not left to propagate uncaught.
- `Yoink/MessageBoxWindow.axaml` / `.axaml.cs` — a minimal modal dialog (title + message + OK button) used in place of WinForms' `MessageBox`, which Avalonia doesn't provide out of the box. Use `MessageBoxWindow.ShowAsync(owner, message, title)` for any new user-facing success/error dialogs rather than adding another one-off dialog type.
- `Yoink/AppSettings.cs` / `Yoink/SettingsService.cs` — persisted user preferences (currently just theme). `SettingsService` reads/writes JSON at `%AppData%`/`~/.config`/`Yoink/settings.json` (via `Environment.SpecialFolder.ApplicationData`, so it works the same way cross-platform) and falls back to defaults if the file is missing or corrupt. This is one of the few exceptions to "everything lives in MainWindow" — it's persistence, not UI, so it stays a separate class; keep future non-UI state here rather than folding it into the window code-behind.
- Theme: `App.axaml` sets `RequestedThemeVariant` at startup from the saved preference (`ThemePreference.System/Light/Dark` in `AppSettings`). `System` maps to Avalonia's `ThemeVariant.Default`, which follows the OS light/dark setting live. `MainWindow` has a "Theme" combo box that flips `Application.Current.RequestedThemeVariant` immediately and persists the choice via `SettingsService`. `App.ToThemeVariant` is the single place that maps preference → `ThemeVariant`; reuse it rather than re-deriving the mapping.
- `Yoink/DownloadHistoryEntry.cs` / `Yoink/DownloadHistoryService.cs` — the "Recent downloads" list shown under the download form. Same pattern as settings: plain data class + a static service that reads/writes JSON (`history.json`, same config directory), capped at the 50 most recent entries. `MainWindow` keeps the live list in an `ObservableCollection<DownloadHistoryEntry>` bound to `LstHistory.ItemsSource`; every successful *and* failed download appends a new entry and re-saves.
- `Yoink/DownloadStatusToBrushConverter.cs` — the one `IValueConverter` in the app, mapping `DownloadStatus` to the semantic Success/Error brush (see `BRANDING.md`) for the history list's status text.
- `Yoink/DownloadEngine.cs` — the generic core download engine described in the README roadmap's step 1: a source-agnostic, resumable single-file HTTP downloader (range-request resume, progress via `IProgress<double>`, retry-with-backoff, cancellation). It writes to `<destination>.partial` and only moves the file into place on success, so a killed download resumes instead of restarting. This is deliberately not wired into `MainWindow` yet — the YouTube download path still goes through `YoutubeExplode`'s own `Streams.DownloadAsync`; `DownloadEngine` is the foundation the roadmap's later "YouTube extraction layer" and "download queue" steps are meant to build on for non-muxed/direct-link downloads, not a replacement for the current flow.
- **`BRANDING.md`** (repo root) — the design tokens (colors, type, spacing/radius) implemented as Avalonia resources/style classes in `App.axaml`. Read it before adding new UI: reuse the existing style classes (`Card`, `AppTitle`, `Subtitle`, `SectionTitle`, `Caption`, `Primary` on buttons) instead of one-off styling, so new screens stay visually consistent with the rest of the app.

### Key dependency: YoutubeExplode

All YouTube interaction (resolving stream manifests, stream selection, downloading) goes through the
`YoutubeExplode` NuGet package. It replaced the old `YoutubeExtractor`/`YoutubeExtractorCore` packages,
which are abandoned and don't target modern .NET. Look at `YoutubeClient.Videos.Streams` before adding new
download logic.
