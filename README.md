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

Where this project is headed, roughly in build order. Each step builds on the last, so earlier ones are
more load-bearing than later ones. Checked-off steps are implemented and in use, not just scaffolded.

- [x] **1. Core download engine** — HTTP client wrapper that supports resumable/segmented downloads (range
  requests), progress reporting, retry-on-failure, and cancellation. This is the heart of the app — get
  single-file downloads rock solid before adding YouTube-specific complexity.
  <br>Built as `DownloadEngine`. It isn't wired into the YouTube flow (step 2 below covers why) — it's the
  engine step 5's direct-link downloads will use once that lands.
- [x] **2. YouTube extraction layer** — wire up yt-dlp (or a .NET wrapper around it, or shell out to the binary)
  for URL resolution, playlist expansion, format/quality listing, and audio+video stream merging. Decide
  early: bundle yt-dlp as a dependency vs. reimplement extraction yourself — bundling is far less
  maintenance.
  <br>Built as `YtDlpClient`, shelling out to the `yt-dlp` binary (see "Using it" above for the PATH
  requirement). This replaced an earlier `YoutubeExplode`-based implementation, which — like most
  from-scratch YouTube clients — broke as YouTube's server-side behavior shifted; by the end it couldn't
  find a muxed stream for most videos at all. yt-dlp downloads and merges video/audio itself (via ffmpeg),
  so it also fixed that: any resolution up to 1440p now actually works, not just whatever still happened
  to have a pre-muxed file.
- [x] **3. Download queue & persistence** — in-memory + persisted queue (SQLite is a good fit here) holding
  pending/active/paused/completed/failed downloads. Support pause, resume, cancel, retry, and reordering.
  This is what the UI binds to.
  <br>Built as `DownloadQueueService`, backed by SQLite. Every download — including today's single-button
  flow — goes through this persisted, resumable, retryable queue; a killed/crashed app recovers cleanly on
  restart. The part still missing is the *view*: there's no list UI yet to see multiple queued items or
  reach pause/resume/reorder by hand. That's step 4, next.
- [ ] **4. Main UI — queue view & add dialog** — the main Avalonia window: an add-download dialog (URL +
  quality/format picker for YT links), a list view with per-item progress bars, and a details/settings
  panel. Bound to the queue from the previous step.
- [ ] **5. Auto-catch mechanism** — the "automatically catch my downloads" piece, which is really two separate
  mechanisms: (1) a browser extension (Chrome/Firefox) that detects downloadable links/media and hands the
  URL off to the app via a local HTTP endpoint or native messaging, and (2) clipboard monitoring, where the
  app watches the clipboard for URLs matching known patterns (YouTube links, direct file links) and prompts
  to download. The browser extension is more IDM-like and reliable; clipboard watching is far less work to
  build first.
- [ ] **6. Background operation & notifications** — a system tray icon so the app can run in the background and
  catch downloads without a window open, plus toast/desktop notifications on download complete/failed. On
  Ubuntu this means using Avalonia's tray support (or a platform-specific fallback) plus libnotify-based
  notifications.
- [ ] **7. Speed limits, concurrency & scheduling** — speed limiting per-download and globally, concurrent-download
  limits, scheduling (e.g. only download overnight), and a settings screen to control all of it. Nice-to-haves
  that make it feel like a real IDM replacement rather than a script with a UI.
- [ ] **8. Packaging for Ubuntu** — package as a self-contained .NET publish (single executable), decide on an
  install method (AppImage, .deb, or just a folder + shortcut), and keep this README current since this
  project started life as a YT-video-downloader.

## Contributing

You can clone it and improve it, create a new branch and work on it, or contribute in any other way.

Please comment and contribute to the project — I'm definitely not a pro, lol.
