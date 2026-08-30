# Yoink

Grab a video, keep watching. Yoink is a free, open source download manager — it started as a YouTube
downloader and is growing into a general-purpose one, built to feel like a real desktop app on Linux rather
than an afterthought next to Windows.

## What it is

Paste a YouTube URL, pick a resolution, and Yoink queues it up: resolves the video via `yt-dlp`, downloads
video and audio, merges them with `ffmpeg`, and lands the finished file on disk — all visible in a live
queue you can pause, resume, retry, or reorder. Copy a YouTube link to your clipboard and it offers to grab
that too. Close the window and it keeps working from the tray, with a desktop notification when each
download finishes or fails.

Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it runs the same way on Linux, Windows, and
macOS rather than being tied to Windows Forms.

## Download

There's no pre-built download yet — packaging is still pending, Ubuntu first (a self-updating AppImage),
with Windows and macOS to follow once that's sorted. Once it's out, it'll be a straight download from this
repo's [Releases page](https://github.com/developerharon/Yoink/releases) — no separate
site or account needed — and the app checks for new releases on its own from then on, prompting before it
downloads or installs anything. Until then, running it from source is the way to use it — see "Using it"
below.

## Using it

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [`yt-dlp`](https://github.com/yt-dlp/yt-dlp#installation) — resolves and downloads every video; Yoink
  shells out to it rather than talking to YouTube directly
- [`ffmpeg`](https://ffmpeg.org/download.html) — merges the separately-downloaded video and audio into one
  file

`yt-dlp` and `ffmpeg` both need to be on your `PATH`. Yoink checks for `yt-dlp` on startup and tells you if
it's missing.

### Run it

1. Clone the repo.
2. Run it:
   ```
   dotnet run --project Yoink
   ```
3. Paste a YouTube URL, pick a resolution, and click "+ Add download".

## Features

- **Resumable downloads** — a killed or crashed download picks up where it left off instead of restarting.
- **Any resolution, reliably** — video and audio download separately and get merged locally, so quality
  isn't limited to whatever YouTube happens to still serve pre-merged.
- **A real download queue** — pause, resume, cancel, retry, and reorder; every download, completed or
  failed, stays visible as history rather than disappearing.
- **Clipboard auto-catch** — copy a YouTube link and Yoink offers to download it, no browser extension
  required.
- **Runs in the background** — a tray icon keeps it going with the window closed, with a desktop
  notification (Linux) when each download finishes or fails.
- **Speed limits, concurrency & scheduling** — cap bandwidth per download or globally, run several
  downloads at once, or restrict downloading to certain hours (overnight, say).
- **One settings screen** for all of it — theme, clipboard watching, tray behavior, speed limits,
  concurrency, and scheduling.
- **Checks for updates on its own** (once installed from a real release, not a source build) — silently,
  once a day, and always asks before downloading or installing anything.

## Contributing

You can clone it and improve it, create a new branch and work on it, or contribute in any other way.

Please comment and contribute to the project — I'm definitely not a pro, lol.
