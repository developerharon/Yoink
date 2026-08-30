# Yoink

A free, open source download manager. It started as a YouTube playlist downloader; the goal now is a
general-purpose download manager that runs anywhere .NET runs — Linux included.

Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it's cross-platform (Linux, macOS, Windows)
rather than tied to Windows Forms.

## Using it

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone the repo.
3. Run it:
   ```
   dotnet run --project Yoink
   ```
4. Paste the YouTube video URL, pick a resolution, click Download.

## Roadmap

Where this project is headed, roughly in build order. Each step builds on the last, so earlier ones are
more load-bearing than later ones.

1. **Core download engine** — HTTP client wrapper that supports resumable/segmented downloads (range
   requests), progress reporting, retry-on-failure, and cancellation. This is the heart of the app — get
   single-file downloads rock solid before adding YouTube-specific complexity.
2. **YouTube extraction layer** — wire up yt-dlp (or a .NET wrapper around it, or shell out to the binary)
   for URL resolution, playlist expansion, format/quality listing, and audio+video stream merging. Decide
   early: bundle yt-dlp as a dependency vs. reimplement extraction yourself — bundling is far less
   maintenance.
3. **Download queue & persistence** — in-memory + persisted queue (SQLite is a good fit here) holding
   pending/active/paused/completed/failed downloads. Support pause, resume, cancel, retry, and reordering.
   This is what the UI binds to.
4. **Main UI — queue view & add dialog** — the main Avalonia window: an add-download dialog (URL +
   quality/format picker for YT links), a list view with per-item progress bars, and a details/settings
   panel. Bound to the queue from the previous step.
5. **Auto-catch mechanism** — the "automatically catch my downloads" piece, which is really two separate
   mechanisms: (1) a browser extension (Chrome/Firefox) that detects downloadable links/media and hands the
   URL off to the app via a local HTTP endpoint or native messaging, and (2) clipboard monitoring, where the
   app watches the clipboard for URLs matching known patterns (YouTube links, direct file links) and prompts
   to download. The browser extension is more IDM-like and reliable; clipboard watching is far less work to
   build first.
6. **Background operation & notifications** — a system tray icon so the app can run in the background and
   catch downloads without a window open, plus toast/desktop notifications on download complete/failed. On
   Ubuntu this means using Avalonia's tray support (or a platform-specific fallback) plus libnotify-based
   notifications.
7. **Speed limits, concurrency & scheduling** — speed limiting per-download and globally, concurrent-download
   limits, scheduling (e.g. only download overnight), and a settings screen to control all of it. Nice-to-haves
   that make it feel like a real IDM replacement rather than a script with a UI.
8. **Packaging for Ubuntu** — package as a self-contained .NET publish (single executable), decide on an
   install method (AppImage, .deb, or just a folder + shortcut), and keep this README current since this
   project started life as a YT-video-downloader.

## Contributing

You can clone it and improve it, create a new branch and work on it, or contribute in any other way.

Please comment and contribute to the project — I'm definitely not a pro, lol.
