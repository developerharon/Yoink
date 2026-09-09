# Yoink

Yoink is a free, source-available download manager — YouTube videos, torrents, and any other direct-link
file, all in one queue. Built to feel like a real desktop app on Linux rather than an afterthought next to
Windows.

It's a hobby project, built purely because making it was fun — see [License](#license) below for what that
means for how you can use it.

## What it is

Paste a URL and Yoink queues it up. A YouTube link resolves via `yt-dlp`, downloads video and audio, and
merges them with `ffmpeg`; a magnet link or a `.torrent` file downloads peer-to-peer, showing live
seeder/leecher counts as it goes; any other direct link (a PDF, an installer, an archive, whatever)
downloads straight to disk over multiple connections at once for full speed. Every kind lands in the same
live queue you can pause, resume, retry, or reorder. Copy a YouTube link, a magnet link, or a link to a
downloadable file to your clipboard and it offers to grab that too. Close the window and it keeps working
from the tray, with a desktop notification when each download finishes or fails.

Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it runs the same way on Linux, Windows, and
macOS rather than being tied to Windows Forms.

## Screenshots

<table>
<tr>
<td width="50%">

**Clipboard auto-catch** — copy a YouTube link, and Yoink offers to grab it, no browser extension required.

![Clipboard auto-catch prompt](docs/screenshots/clipboard-detected.png)

</td>
<td width="50%">

**Pick a resolution and format** — resolved from what that specific video actually offers, not a guessed list.

![Add download dialog with resolution and format pickers](docs/screenshots/add-download.png)

</td>
</tr>
<tr>
<td width="50%">

**A live queue** — pause or cancel any download in progress, with real-time size and speed.

![Downloads queue with an active download in progress](docs/screenshots/active-download.png)

</td>
<td width="50%">

**History that never disappears** — every download, completed or not, stays visible.

![Downloads queue full of completed downloads](docs/screenshots/download-history.png)

</td>
</tr>
<tr>
<td width="50%">

**Appearance & auto-catch** — theme, accent color, and clipboard watching.

![Settings screen: Appearance and Auto-catch sections](docs/screenshots/settings-appearance.png)

</td>
<td width="50%">

**Downloads & scheduling** — download folder, concurrency, speed limits, and quiet hours.

![Settings screen: Downloads and Scheduling sections](docs/screenshots/settings-downloads.png)

</td>
</tr>
</table>

## Download

Grab the latest `.deb` from this repo's
[Releases page](https://github.com/developerharon/Yoink/releases/latest) — no separate site or account
needed.

### Installing the .deb on Ubuntu

```
sudo apt install ./yoink_<version>_amd64.deb
```

(`apt install ./...` rather than `dpkg -i` so any missing dependency is resolved automatically —
though Yoink ships self-contained with its own .NET runtime, so in practice there's nothing to
resolve beyond the optional `libnotify-bin`, used for desktop notifications if it's present.)

That's it — Yoink shows up in the app launcher/search like any other installed app from then on, and
`sudo apt remove yoink` uninstalls it cleanly. Unlike the AppImage this used to ship as, installing a
`.deb` update isn't something the app can do for itself (that needs root) — Yoink checks for new
releases on its own in the background and prompts when one's available, but "Open Release Page" is as
far as that goes; downloading and re-running `apt install` on the new `.deb` is a manual step.

Linux (Ubuntu/.deb) is the only packaged platform for now — Windows and macOS builds are pending
real-hardware verification. Until those are out, running from source is the way to use it there too — see
"Using it" below.

## Using it

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

Yoink also needs [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) (resolves and downloads every video) and
[`ffmpeg`](https://ffmpeg.org/download.html) (merges the separately-downloaded video and audio into one
file), but you don't need to install either yourself: on first launch, Yoink checks for both on `PATH`
and, for whichever it can't find, downloads the current official build straight into its own config
folder and keeps that copy fresh from then on, riding the same daily check as its own update check. If
you'd rather manage them yourself (a distro package, a version you're pinning, etc.), just put them on
`PATH` first and Yoink leaves them alone.

### Run it

1. Clone the repo.
2. Run it:
   ```
   dotnet run --project Yoink
   ```
3. Click "+ Add download" and paste a URL — a YouTube link, a magnet link, or a link to any other file
   (or browse for a local `.torrent` file) — then click "Add to queue".

## Features

- **Not just YouTube** — paste a link to any downloadable file (a PDF, an installer, an archive, ...) and
  Yoink grabs it too, over several connections at once for full speed, right alongside your video queue.
- **Torrents, peer-to-peer** — paste a magnet link, or point Yoink at a `.torrent` file (a link or a local
  file), and it downloads straight from the swarm, showing live seeder/leecher counts and progress. Once a
  torrent finishes, Yoink stops connecting to peers entirely rather than continuing to seed in the
  background — it's a download manager, not a long-running torrent client.
- **Resumable downloads** — a killed or crashed download picks up where it left off instead of restarting,
  whatever kind it is.
- **Any resolution, reliably** — video and audio download separately and get merged locally, so quality
  isn't limited to whatever YouTube happens to still serve pre-merged.
- **A real download queue** — pause, resume, cancel, retry, and reorder; every download, completed or
  failed, stays visible as history rather than disappearing. A completed download whose file later goes
  missing gets crossed out rather than silently claiming to still be there, and a re-download never
  silently overwrites an existing file with the same name.
- **Clipboard auto-catch** — copy a YouTube link, a magnet link, or a link to a downloadable file and Yoink
  offers to grab it, no browser extension required.
- **Runs in the background** — a tray icon keeps it going with the window closed, with a desktop
  notification (Linux) when each download finishes or fails.
- **Speed limits, concurrency & scheduling** — cap bandwidth per download or globally, control how many
  connections a single file download uses, run several downloads at once, or restrict downloading to
  certain hours (overnight, say).
- **One settings screen** for all of it — theme, clipboard watching, tray behavior, speed limits,
  concurrency, and scheduling.
- **Checks for updates on its own** (once installed from a real release, not a source build) — silently,
  once a day, and always asks before doing anything about it. On Windows/macOS that means downloading
  and installing in-app with one click; on Linux (a `.deb` install can't be updated without root) it
  means opening the release page for you to grab the new `.deb` yourself.

## Contributing

You can clone it and improve it, create a new branch and work on it, or contribute in any other way.

Please comment and contribute to the project — I'm definitely not a pro, lol.

## License

Yoink is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md) — free to use, modify, fork,
and share for any noncommercial purpose, but not to sell or build a paid product or service on top of. I
built this purely for fun, and I want it to stay a free gift to anyone who wants it, not something someone
else profits from.

That restriction is why it's "source-available" above rather than "open source" — the
[Open Source Definition](https://opensource.org/osd) requires letting anyone use software commercially too,
which this license deliberately doesn't. Full terms: [LICENSE.md](LICENSE.md), or the license's own page at
[polyformproject.org](https://polyformproject.org/licenses/noncommercial/1.0.0).
