# Yoink

A free, open source download manager. It started as a YouTube playlist downloader; the goal now is a
general-purpose download manager that runs anywhere .NET runs — Linux included.

Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it's cross-platform (Linux, macOS, Windows)
rather than tied to Windows Forms.

## Using it

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Install [`yt-dlp`](https://github.com/yt-dlp/yt-dlp#installation) and [`ffmpeg`](https://ffmpeg.org/download.html),
   and make sure both are on your `PATH`. Yoink shells out to yt-dlp for everything YouTube-related (it's
   what actually resolves and downloads videos) and to ffmpeg to combine separately-downloaded video and
   audio into one file. Yoink checks for yt-dlp on startup and will tell you if it's missing.
3. Clone the repo.
4. Run it:
   ```
   dotnet run --project Yoink
   ```
5. Paste the YouTube video URL, pick a resolution, click Download.

## Roadmap

Where this project is headed, in build order — each step builds on the last, so read position as sequence;
there's no separate step number to track alongside it. Checked-off steps are implemented and in use, not
just scaffolded; an indented note under a step says what actually shipped and, for partial ones, what's
still open.

- [x] **Core download engine**
  HTTP client wrapper that supports resumable/segmented downloads (range requests), progress reporting,
  retry-on-failure, and cancellation. This is the heart of the app — get single-file downloads rock solid
  before adding YouTube-specific complexity.
  > Built as `DownloadEngine`. It isn't wired into the YouTube flow (next step covers why) — it's the engine
  > a later direct-link-downloads step will use once that lands.

- [x] **YouTube extraction layer**
  Wire up yt-dlp (or a .NET wrapper around it, or shell out to the binary) for URL resolution, playlist
  expansion, format/quality listing, and audio+video stream merging. Decide early: bundle yt-dlp as a
  dependency vs. reimplement extraction yourself — bundling is far less maintenance.
  > Built as `YtDlpClient`, shelling out to the `yt-dlp` binary (see "Using it" above for the PATH
  > requirement). This replaced an earlier `YoutubeExplode`-based implementation, which — like most
  > from-scratch YouTube clients — broke as YouTube's server-side behavior shifted; by the end it couldn't
  > find a muxed stream for most videos at all. yt-dlp downloads and merges video/audio itself (via ffmpeg),
  > so it also fixed that: any resolution up to 1440p now actually works, not just whatever still happened
  > to have a pre-muxed file.

- [x] **Download queue & persistence**
  In-memory + persisted queue (SQLite is a good fit here) holding pending/active/paused/completed/failed
  downloads. Support pause, resume, cancel, retry, and reordering. This is what the UI binds to.
  > Built as `DownloadQueueService`, backed by SQLite. Every download goes through this persisted,
  > resumable, retryable queue; a killed/crashed app recovers cleanly on restart.

- [x] **Main UI — queue view & add dialog**
  The main Avalonia window: an add-download dialog (URL + quality/format picker for YT links), a list view
  with per-item progress bars, and a details/settings panel. Bound to the queue from the previous step.
  > Built as `AddDownloadDialog` (URL + resolution) and a queue list in `MainWindow` bound directly to
  > `DownloadQueueService` — every row gets a live progress bar and Pause/Resume/Cancel/Retry/"Show in
  > folder" depending on its state; it doubles as download history, since the queue is never pruned. The
  > "settings panel" part is still just the existing Theme picker — a fuller settings screen makes more
  > sense once the speed-limits/concurrency/scheduling step below gives it something to actually hold.

- [x] **Auto-catch mechanism**
  The "automatically catch my downloads" piece, which is really two separate mechanisms: a browser
  extension (Chrome/Firefox) that detects downloadable links/media and hands the URL off to the app via a
  local HTTP endpoint or native messaging, and clipboard monitoring, where the app watches the clipboard for
  URLs matching known patterns (YouTube links, direct file links) and prompts to download. The browser
  extension is more IDM-like and reliable; clipboard watching is far less work to build first.
  > Clipboard monitoring is built, as `ClipboardWatcherService` — polling the clipboard (no OS-agnostic
  > "changed" event exists) for YouTube URLs and, on a match, opening the add-download dialog pre-filled
  > rather than downloading silently. A "Watch clipboard" toggle in the header controls it, on by default.
  > Known limitation: Wayland compositors can restrict clipboard reads while the app isn't focused, so this
  > is less reliable there than on X11/Windows in the background.
  > The browser extension is not started, and is being treated as its own separate future effort rather
  > than bundled in here — a much bigger undertaking (its own codebase per browser, a native-messaging host
  > to install, store review or side-loading) relative to what it'd add on top of clipboard monitoring at
  > this project's current scale.

- [x] **Background operation & notifications**
  A system tray icon so the app can run in the background and catch downloads without a window open, plus
  toast/desktop notifications on download complete/failed. On Ubuntu this means using Avalonia's tray
  support (or a platform-specific fallback) plus libnotify-based notifications.
  > Built: a tray icon (`App.axaml.cs`) with Show/Quit, plus a "Keep running in tray" toggle in the header —
  > **off by default**, since on a Linux desktop without tray/StatusNotifierItem support (plain GNOME
  > without an extension, say) the icon just won't show up, and hiding-not-closing by default there would
  > strand the window with no way back; turn it on once you've confirmed your tray actually shows it.
  > Notifications are built as `NotificationService`, shelling out to `notify-send` — Linux only for now
  > (matching this step's own "Ubuntu... libnotify" scoping); Windows/macOS toasts are a known gap, left for
  > the packaging step below since they need an app identity this project doesn't have yet.

- [ ] **Speed limits, concurrency & scheduling**
  Speed limiting per-download and globally, concurrent-download limits, scheduling (e.g. only download
  overnight), and a settings screen to control all of it. Nice-to-haves that make it feel like a real IDM
  replacement rather than a script with a UI.

- [ ] **Packaging for Ubuntu**
  Package as a self-contained .NET publish (single executable), decide on an install method (AppImage,
  .deb, or just a folder + shortcut), and keep this README current since this project started life as a
  YT-video-downloader.

## Contributing

You can clone it and improve it, create a new branch and work on it, or contribute in any other way.

Please comment and contribute to the project — I'm definitely not a pro, lol.
