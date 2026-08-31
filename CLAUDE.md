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

- Build: `dotnet build Yoink.slnx`
- Run: `dotnet run --project Yoink`
- Test: `dotnet test Yoink.slnx`

There is no lint command/config in this repo. `Yoink.Tests` (xUnit v3 — see its own "Key dependency"
note below for why v3 specifically, not the more commonly-seen v2) is the test project; CI
(`.github/workflows/ci.yml`) runs it on every push/PR to `master` alongside the build.

## Testing (`Yoink.Tests/`)

A handful of production methods that exist purely as pure/isolable logic (see each one's own doc
comment) are `internal` rather than `private` specifically so tests can reach them directly —
`Yoink.csproj` grants `Yoink.Tests` an `InternalsVisibleTo` for exactly this. The whole assembly runs
sequentially (`[assembly: CollectionBehavior(DisableTestParallelization = true)]` in
`AssemblyInfo.cs`), since a couple of test classes redirect the static, process-wide
`SettingsService.SettingsPath` to a temp file for their duration — safe only one at a time.

- `Models/` — `DownloadQueueItemTests` (every `CanPause`/`CanResume`/etc. computed property across
  all six `DownloadQueueStatus` values), `AppSettingsTests` (fresh-install defaults, the five
  `AccentColor` presets).
- `Services/SettingsServiceTests` — save/load round-tripping and the missing-file/corrupt-file
  fallback-to-defaults behavior, against a redirected `SettingsService.SettingsPath` (never the real
  user's actual settings.json).
- `Services/DownloadQueueScheduleTests` — `DownloadQueueService.IsWithinWindow` (same-day and
  overnight-wrap schedule windows, boundary-inclusive/exclusive edges), `ComputeRateLimitKBps` (every
  combination of per-download/global caps), `BuildFormatSelector`/`BuildDestinationPath`/
  `ResolveDownloadFolder`, and `SettingsService.GetDefaultDownloadFolder`/`ParseXdgDownloadDir` (the
  freedesktop.org user-dirs.dirs parsing behind the Linux default-Downloads-folder guess).
- `Services/DownloadQueueServiceTests` — real `DownloadQueueService` instances against a temp SQLite
  file (its constructor already accepts a `databasePath` override, so this needed no production
  change): Enqueue/GetAll/Reorder/Pause/Resume/Cancel/Retry, plus an end-to-end
  `EnqueueAndWaitAsync` test that lets the real background loop run against the real (but genuinely
  absent in this environment and on GitHub's runners) `yt-dlp`, verifying it reaches `Failed` and
  throws rather than hanging — see the class's own doc comment for why yt-dlp being missing is
  actually *useful* here rather than a gap. `YtDlpClient` is sealed with no interface, so it can't be
  faked; the CRUD-focused tests instead close the schedule window (`SchedulingEnabled=true`,
  `ScheduleStart == ScheduleEnd`, which `IsWithinWindowTests.ZeroWidthWindow_IsNeverWithin` confirms is
  never "within") so the background loop never dequeues anything mid-test, rather than racing it.
- `Services/ClipboardWatcherServiceTests` — the real background poll loop (given a fast poll interval)
  against fake clipboard-read/is-enabled delegates: every recognized YouTube URL shape, negative cases,
  fires-once-per-change, and respects the enabled/disabled delegate.
- `Services/YtDlpClientParsingTests` — `ParseVideoInfo`/`ParsePlaylistEntryLine` against sample yt-dlp
  JSON (no process spawned), `ExtractErrorSummary`, and `TryParseProgressPercent` (pulled out of
  `DownloadAsync`'s stdout loop, which now calls this rather than duplicating the regex match, so the
  tested code path is the real one).
- `Converters/DownloadQueueStatusToBrushConverterTests` — via `[AvaloniaFact]`/`[AvaloniaTheory]` (see
  `TestAppBuilder.cs`), against the real `App` and its actual `App.axaml` resources.
- `Branding/AppTests.cs` — `App.ToThemeVariant`'s mapping, and `App.ApplyAccent`'s actual effect on
  `Application.Current`'s resources for all five `AccentColor` presets (also `[AvaloniaFact]`).

**Known gap, not yet covered**: `Views.MainWindow`/`Views.SettingsView` themselves aren't
instantiated in tests — `MainWindow`'s constructor has no way to override `DownloadQueueService`'s
real %AppData%-pointed database path (unlike the service itself, which already takes one), so
constructing it for real in a test would touch the actual user's queue.db. Extending it with the same
kind of override `DownloadQueueService` already has is the natural next step if window-level
interaction tests (e.g. the `FANavigationView` back-button regression from the navigation-shell work)
are wanted later.

### Key dependency: xUnit v3 (not v2)

`Avalonia.Headless.XUnit` 12.1.1 (used for the `[AvaloniaFact]`/`[AvaloniaTheory]` tests above) only
ships against `xunit.v3.extensibility.core` — there's no v2-compatible build. `dotnet new xunit` in
this SDK still scaffolds a v2-shaped project (`xunit`/`xunit.runner.visualstudio` packages,
VSTest-hosted-in-a-DLL model) by default; mixing that with `Avalonia.Headless.XUnit` produces a
duplicate-`FactAttribute` compile error (both `xunit.core` v2 and `xunit.v3.core` present at once) and,
before that's even fixed, a separate "could not find app host executable" runtime failure from the
VSTest adapter trying to host a v3-shaped test assembly the old way. `Yoink.Tests.csproj` is set up for
v3 properly instead: the `xunit.v3` package (not `xunit`), plus `<OutputType>Exe</OutputType>` and
`<UseAppHost>true</UseAppHost>` — v3 runs as a self-contained executable via Microsoft.Testing.Platform,
not a DLL loaded by a separate host. Confirmed by reproducing both failures against a bare
`dotnet new xunit` project before fixing them, not assumed from docs.

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

- `MainWindow.axaml` / `.axaml.cs` — the app shell **and** the queue view. The shell part is
  FluentAvaloniaUI's `FANavigationView` (top-nav mode, `PaneDisplayMode="Top"`) wrapping everything: the
  mark (`Path.YoinkMark`, see `BRANDING.md`) plus "Yoink" sit in `NavigationView.PaneCustomContent` (not
  `PaneHeader` — that slot is simply never rendered in top-nav mode in FluentAvaloniaUI 3.1.0, confirmed by
  rendering an actual frame rather than trusting the XAML compiled; see `BRANDING.md`'s "The mark" section),
  and
  FluentAvalonia's built-in Settings entry (`IsSettingsVisible="True"`) is the *only* menu item — deliberately
  no "Downloads" item alongside it, since Downloads is the home/dashboard you land on, not a destination you
  navigate to; it's what used to be a "⚙ Settings" button opening a separate modal window (`SettingsWindow`,
  now deleted) — Settings is a page inside this same window now, not a popup, which is the whole reason this
  shell exists (see the `ui-navigation-shell` project memory for why: a modal Settings window read as
  "outside the app" no matter how it was styled). `NavView_SelectionChanged` reads `e.IsSettingsSelected`
  (true only when that built-in entry is picked, since there's nothing else to select) and swaps which of
  `DownloadsBody`/`SettingsBody` (two overlaid children of one `Grid`, toggled via `IsVisible` — not a
  `Frame`/page navigation stack, since there are only ever these two pages) is showing, revealing the nav
  bar's own back button (`IsBackButtonVisible`/`IsBackEnabled`, both otherwise `False` — the back button
  only exists at all while on Settings, since Downloads has nowhere to go "back" from) at the same time;
  `NavView_BackRequested` reverses both, hides the back button again, and clears `SelectedItem` to
  un-highlight the Settings entry (there's no "Downloads" item to select instead). Neither page needs
  anything handed back when the other is shown — every setting is read fresh at the point it's needed
  regardless of which page is currently visible (see `SettingsView` below).
  - **This same top row doubles as the window's own title bar**, rather than sitting below a separate
    native one: the root `Window` sets `ExtendClientAreaToDecorationsHint="True"` (plus
    `ExtendClientAreaTitleBarHeightHint="48"`, tuned to roughly match the nav row's own rendered
    height), and `PaneCustomContent`'s icon+wordmark `StackPanel` carries
    `Avalonia.Controls.Chrome.WindowDecorationProperties.ElementRole="TitleBar"` so that area supports
    native drag-to-move/double-click-to-maximize. `WindowDecorationMargin` (subscribed to via
    `PropertyChanged` in the constructor, applied straight onto `NavView.Margin`) reports how much
    space the system reserves for its own caption buttons — right-side width for min/max/close on
    Windows, left-side width for the macOS traffic lights — so the icon/wordmark/Settings tab never
    render underneath them, with no per-platform branching needed in this codebase at all. `NavView`
    is margined rather than padded specifically because `FANavigationView.Padding` (inherited from
    `TemplatedControl`) was tried first and, verified via a throwaway headless render, has **no
    effect** on the Top-mode pane row at all in FluentAvaloniaUI 3.1.0 — `Margin` on the whole control
    does shift the pane row (and, as a side effect, narrows the body content area by the same sliver
    on that edge while the window is extended, an accepted trade-off rather than fighting
    `FANavigationView`'s own template further). This also means `MainWindow` deliberately stayed a
    plain `Window`, not FluentAvaloniaUI's own `FluentAvalonia.UI.Windowing.FAAppWindow` — reflecting
    the actually-installed 3.1.0 DLL showed its `AppWindowTitleBar` lacks the `LeftInset`/`RightInset`/
    `SetDragRectangles`/`TitleBarHitTestType` members its current online docs describe (version drift,
    the same class of gotcha as the `PaneHeader` one above), and switching base classes would also
    change `Icon`'s type from `WindowIcon` (what `App.CurrentIcon`/`ApplyAccent` push everywhere) to
    `IImage` for no benefit here. Real native chrome (exact button rendering, macOS traffic-light
    position) can't be verified headless — see the `headless-visual-verification` memory — and needs a
    real windowed run on each platform to confirm.
  - The queue view itself (`DownloadsBody`) is unchanged in substance from before this shell existed: an
    `ItemsControl` bound to an `ObservableCollection<DownloadQueueItem>`, one row per queue entry (title,
    status, a progress bar when active/paused, and Pause/Resume/Cancel/Retry/"Show in folder" buttons —
    visibility of each driven by `DownloadQueueItem`'s `CanPause`/`CanResume`/etc. computed properties, no
    converters needed). This is also where "Recent downloads" ended up: the queue is never pruned, so
    completed/failed items just stay in the same list rather than living in a separate history view.
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
  - The constructor also fires `CheckForUpdatesAsync` — see `UpdateService`/`UpdatePromptDialog` below.
- `AddDownloadDialog.axaml` / `.axaml.cs` — the "add download" dialog half of step 4: URL + resolution
  picker, calls `DownloadQueueService.EnqueueAsync` directly and closes. Shown via the static
  `AddDownloadDialog.ShowAsync(owner, queue, prefillUrl)`, same pattern as `MessageBoxWindow.ShowAsync`.
  `prefillUrl` is optional — the clipboard watcher (above) is the only caller that passes one.
- `UpdatePromptDialog.axaml` / `.axaml.cs` — the update/distribution story's UI half (see `UpdateService`
  below for the mechanism). Shows the new version + release notes with "Install Update"/"Later" buttons;
  clicking "Install Update" downloads (progress bar, reusing the same pattern as everywhere else) then
  calls `UpdateService.ApplyUpdatesAndRestart`, which exits the app, applies the update, and relaunches —
  nothing in the click handler after that call ever runs. Shown via `MainWindow.CheckForUpdatesAsync`,
  throttled to roughly once a day via `AppSettings.LastUpdateCheckUtc`; per the agreed update UX (see the
  project memory this came from), this is the *only* place an update is ever downloaded or applied — the
  check itself is silent, but installing always needs this explicit prompt first.
- `SettingsView.axaml` / `.axaml.cs` — the settings screen from README roadmap step 7 ("a settings screen
  to control all of it"), a `UserControl` (not a `Window` — it used to be `SettingsWindow`, opened modally;
  see `MainWindow` above for why that changed) hosted as `MainWindow`'s `SettingsBody`. Content is grouped
  into `FASettingsExpander`s (Appearance, Auto-catch & background, Downloads, Scheduling), each holding
  `FASettingsExpanderItem` rows with the control in `.Footer` — FluentAvaloniaUI's own settings-page idiom,
  modeled on Windows' own Settings app, rather than the hand-rolled `DockPanel` label+control rows this
  used before. Covers: Theme; an accent-color picker (five round swatch buttons, `Classes="AccentSwatch"`
  — see `BRANDING.md`/`App.ApplyAccent` for the preset values and how a click repaints the app live; the
  clicked swatch's "Selected" class is toggled by `SetSelectedAccentSwatch`, called both on click and once
  at construction from the persisted `AppSettings.AccentColor`); "Watch clipboard"/"Keep running in tray"
  (now `ToggleSwitch` rather than `CheckBox`, to match the expander-row idiom — `IsCheckedChanged` behaves
  identically either way, both derive from `ToggleButton`); and the concurrency/speed-limit/scheduling
  controls from this same step. Every control persists its own change immediately via `SettingsService`
  (read-modify-write) — there's no "Save" button; the nav bar's back arrow (see `MainWindow` above) is the
  only way out, and doesn't need to trigger anything since every change already committed on the spot.
  Nothing here needs to push a live update anywhere: `DownloadQueueService`'s processing loop and
  `ClipboardWatcherService` both re-read settings fresh at the point they need them, so a change here takes
  effect on their very next check (within one processing-loop iteration or clipboard poll). Speed limits
  use `NumericUpDown` with 0 or an empty field both meaning "unlimited" (`ToNullableLimit`); scheduling
  uses `TimePicker` (its `SelectedTime` is a `TimeSpan?`, converted to/from `AppSettings`' `TimeOnly` fields
  by hand since there's no built-in conversion). The Downloads group's first row, "Download folder", is a
  read-only `TextBox` (typing an arbitrary path in wouldn't be validated) plus Browse/Reset buttons — Browse
  opens Avalonia's `IStorageProvider.OpenFolderPickerAsync` (the cross-platform stand-in for a
  FolderBrowserDialog, same "Avalonia doesn't provide one directly" reasoning as `MessageBoxWindow`) via
  `TopLevel.GetTopLevel(this)`, Reset clears `AppSettings.DownloadFolder` back to `null`. The box is seeded
  from `DownloadQueueService.ResolveDownloadFolder`, not the raw setting, so it always shows the actual
  folder downloads will land in — the platform default when unset, not a blank field.
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
  defaults if the file is missing or corrupt. Also owns `GetDefaultDownloadFolder`, the platform-Downloads-
  folder guess `Services.DownloadQueueService.ResolveDownloadFolder` falls back to whenever
  `AppSettings.DownloadFolder` is unset: `~/Downloads` (via `Environment.SpecialFolder.UserProfile`, since
  neither Windows nor macOS exposes "Downloads" as its own `SpecialFolder` the way they do Desktop/Documents)
  on Windows/macOS, but on Linux the user's actual XDG_DOWNLOAD_DIR (env var, else parsed out of
  `~/.config/user-dirs.dirs` by `ParseXdgDownloadDir`, else the same `~/Downloads` guess) — a relocated or
  localized Downloads folder isn't a given there the way it is on the other two platforms.
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
  - **Download folder**: `BuildDestinationPath` (called once per item, from `ProcessItemAsync`, and cached
    onto `item.FilePath` from then on) takes the destination folder as a parameter rather than assuming one
    — it used to hardcode `AppContext.BaseDirectory` (the app's own install/build directory, not a real
    destination for anyone's actual downloads), which is what made this configurable in the first place.
    `ResolveDownloadFolder` is what supplies that parameter: `AppSettings.DownloadFolder` when set, else
    `SettingsService.GetDefaultDownloadFolder()`'s platform-Downloads-folder guess (see that method's doc
    comment). Both are read fresh from `SettingsService.Load()` inside `ProcessItemAsync`, same as the
    speed-limit settings right below it.
- `ClipboardWatcherService.cs` — the clipboard-monitoring half of the "auto-catch mechanism" from README
  roadmap step 5 (the browser-extension half is not built — see the README roadmap note on why clipboard
  watching came first). Polls the clipboard on a timer (Avalonia's clipboard API has no change event, and
  there's no OS-agnostic native one either) via a caller-supplied `Func<Task<string?>>`, and raises
  `UrlDetected` when the text changes to something matching a conservative YouTube-URL regex. It never
  downloads anything itself — see `Views.MainWindow.OnClipboardUrlDetected` above for why detection and
  action are kept separate. Whether it's active is a caller-supplied `Func<bool> isEnabled`, checked fresh
  on every poll — not a settable property — so `Views.MainWindow` can hand it
  `() => SettingsService.Load().ClipboardWatchEnabled` once and never need to push a live update when the
  setting changes elsewhere (`SettingsView`, in particular). Known limitation, called out in its doc
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
- `UpdateService.cs` — Yoink's update/distribution mechanism, wrapping the third-party
  [Velopack](https://velopack.io/) library (`Velopack`/`Velopack.Sources` NuGet package; see the
  `update-distribution-strategy` project memory for the full reasoning behind choosing it and the
  per-platform plan). Reads/applies updates from this repo's GitHub Releases via
  `Velopack.Sources.GithubSource` — no separate download server. `IsInstalled` is false for a plain
  `dotnet run`/self-built copy (there's no Velopack install context to check against), and
  `CheckForUpdatesAsync` short-circuits to `null` in that case rather than attempting a network call —
  **verified this is actually safe**: a bare `new UpdateManager(...)` throws `InvalidOperationException`
  ("No VelopackLocator has been set") unless `VelopackApp.Build().Run()` has already run once, which is
  exactly why that call is the literal first line of `Program.Main` (see below), before Avalonia even
  starts — every other line in this codebase, including this service's own constructor, depends on that
  ordering. Best-effort throughout (see `CheckForUpdatesAsync`'s doc comment) — like `NotificationService`,
  a failed check is never worth surfacing as an error.

### Models (`Yoink/Models/`)

- `AppSettings.cs` — `AppSettings` + `ThemePreference`. Theme mapping itself
  (`ThemePreference` → Avalonia's `ThemeVariant`) stays in `App.ToThemeVariant` (see below), not here.
  Every property here is surfaced through `Views.SettingsView` and re-read fresh by whatever needs it
  (`DownloadQueueService`'s loop, `ClipboardWatcherService`'s poll, `App.axaml.cs`'s Closing handler) rather
  than pushed to it live — see the class doc comment. Besides `Theme`: `AccentColor` (default `Blue` —
  the one user-configurable color slot from `BRANDING.md`'s five presets, applied via `App.ApplyAccent`
  and, unlike the rest of this class, pushed live the moment it changes rather than read lazily, the same
  way `Theme` already is — this now includes the window/taskbar/tray icon itself, not just in-app colors,
  see `App.ApplyAccent`'s doc comment), `ClipboardWatchEnabled` (default
  `true`) and `MinimizeToTrayOnClose` (default `false` — see its own doc comment for why this one defaults
  off while clipboard watching defaults on); `DownloadFolder` (default `null`, meaning "use the platform's
  Downloads folder" — see `SettingsService.GetDefaultDownloadFolder`/`DownloadQueueService.ResolveDownloadFolder`
  above for the resolution, and `Views.SettingsView`'s Browse/Reset buttons for how it's set); and, from
  roadmap step 7,
  `MaxConcurrentDownloads`/`PerDownloadSpeedLimitKBps`/`GlobalSpeedLimitKBps`/`SchedulingEnabled`/
  `ScheduleStart`/`ScheduleEnd` — see each property's doc comment, and `DownloadQueueService`'s notes above,
  for exactly how they combine. `LastUpdateCheckUtc` throttles `UpdateService`/`Views.MainWindow`'s update
  check to roughly once a day rather than on every launch.
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

- `Program.cs` — entry point. `VelopackApp.Build().Run()` is the **literal first line** of `Main`, before
  `BuildAvaloniaApp()` is even touched — see `UpdateService`'s notes above for exactly why that ordering is
  load-bearing, not just tidiness. After that, builds and starts the Avalonia app
  (`AppBuilder.Configure<App>()...StartWithClassicDesktopLifetime`).
- `App.axaml` / `App.axaml.cs` — Avalonia `Application` bootstrap; sets FluentAvaloniaUI's `FluentAvaloniaTheme` (not vanilla Avalonia's `FluentTheme` — see the `ui-navigation-shell` project memory for why this package was added and the navigation shell it enables in `MainWindow`), creates `MainWindow` as the desktop lifetime's main window, and (`SetUpTrayIcon`) sets up the tray icon side of README roadmap step 6's "background operation": a `TrayIcon` with Show/Quit `NativeMenuItem`s, and a `MainWindow.Closing` handler that only ever intercepts a user-initiated close (`WindowCloseReason.WindowClosing` specifically — an app- or OS-driven shutdown is deliberately let through unmodified, or `desktop.TryShutdown()` from the tray menu's "Quit" would never actually terminate). What that intercepted close *does* depends on `AppSettings.MinimizeToTrayOnClose`: hide the window if it's on, or call `desktop.TryShutdown()` itself if it's off (the desktop lifetime is switched to `ShutdownMode.OnExplicitShutdown` up front specifically so this handler is always the one deciding, rather than an implicit shutdown racing it). Defaulting that setting to off — rather than clipboard watching's default-on — is deliberate: an unsupported tray (plain GNOME without an extension, for instance) would otherwise strand the window hidden with no visible way back. `OnFrameworkInitializationCompleted` also calls the static `App.ApplyAccent(AccentColor)` (once at startup from `AppSettings.AccentColor`, and again from `Views.SettingsView`'s swatch buttons) — it overwrites `SystemAccentColor`/its Light1-3/Dark1-3 siblings plus this app's own `AccentBrush`/`AccentSoftBrush`/`OnAccentBrush` resources in place, so every control bound to them via `DynamicResource` repaints without anyone pushing a notification. It also loads the matching `Assets/app-icons/app-icon-{accent}.png` into the static `App.CurrentIcon` and pushes it onto every open `Window` (via `desktop.Windows`) plus the tray icon (`_trayIcon`, set once in `SetUpTrayIcon`) — a freshly-constructed window just reads `App.CurrentIcon` itself in its own constructor rather than repeating the accent lookup. It also subscribes to `ActualThemeVariantChanged` to re-run itself, since `AccentSoftBrush` differs by light/dark, not just by preset — see its own doc comment for the light-tint-vs-dark-alpha-wash reasoning and where the five presets' hex values come from (`BRANDING.md`).
- `Assets/app-icons/app-icon-{blue,orange,purple,green,red}.png` — the app/window/taskbar/tray icon, one per accent preset, applied live by `App.ApplyAccent` (see above) rather than one fixed icon like the old `tray-icon.png` (deleted — this fully replaces it, including as the Velopack package icon on every platform, see the release workflow note below). Generated from `Assets/brand/badges/yoink-badge-*.svg`'s exact geometry via a one-off SkiaSharp render — see `BRANDING.md`'s "The mark" section for the recipe if the mark ever changes and these need regenerating. Included via `<AvaloniaResource Include="Assets/**" />` in `Yoink.csproj`, same as every other asset. `Assets/brand/` holds the separate design-tokens/mark/wordmark/badge SVGs these PNGs (and the header's native `Path.YoinkMark`) were sourced from — see `BRANDING.md` for the full breakdown of what's wired into the running app versus staged for reference.
- Theme: `App.axaml` sets `RequestedThemeVariant` at startup from the saved preference (`ThemePreference.System/Light/Dark` in `AppSettings`). `System` maps to Avalonia's `ThemeVariant.Default`, which follows the OS light/dark setting live. `Views.SettingsView`'s Theme combo box flips `Application.Current.RequestedThemeVariant` immediately and persists the choice via `SettingsService`. `App.ToThemeVariant` is the single place that maps preference → `ThemeVariant`; reuse it rather than re-deriving the mapping.
- **`BRANDING.md`** (repo root) — the design tokens (colors, type, spacing/radius) implemented as Avalonia resources/style classes in `App.axaml`. Read it before adding new UI: reuse the existing style classes (`Card`, `AppTitle`, `Subtitle`, `SectionTitle`, `Caption`, `Primary` on buttons) instead of one-off styling, so new screens stay visually consistent with the rest of the app.
- `.github/workflows/release.yml` — cuts a Velopack release on every `v*` tag push (`git tag v0.1.0 && git
  push origin v0.1.0`). `vpk pack`'s `--icon` always points at `Assets/app-icons/app-icon-blue.png`
  specifically (not whichever accent a given user happens to have picked in-app) — the package/installer
  icon is this app's one fixed public identity, unrelated to `App.ApplyAccent`'s live per-window icon
  swapping (see `Assets/app-icons/...` above). Uploading packaged builds straight to this repo's GitHub Releases (no separate
  hosting — see the `update-distribution-strategy` project memory for why, including how download counts
  and traffic are visible there for free without building any telemetry). All three platform jobs are
  defined, but per the agreed rollout order (Ubuntu first) the `release-windows`/`release-macos` jobs carry
  `if: false` with a TODO explaining what's needed before flipping them on (real-hardware verification, and
  proper `.ico`/`.icns` icons — only the Linux leg has been checked against the PNG in `Assets/`). Ordinary
  pushes to `master` don't trigger this at all, only an explicit tag does.

### Key dependency: yt-dlp (external, on PATH)

All YouTube interaction (metadata, playlist expansion, format listing, downloading, audio+video muxing)
goes through the `yt-dlp` command-line tool, invoked via `System.Diagnostics.Process` in `YtDlpClient` — not
a NuGet package. `yt-dlp` (and `ffmpeg`, for muxing) must be installed separately and discoverable on PATH;
see README.md's "Using it" section. This replaced `YoutubeExplode`, which reimplemented YouTube's
extraction logic in C# and, like the `YoutubeExtractor`/`YoutubeExtractorCore` packages before it, was prone
to breaking whenever YouTube changed something server-side (by the time it was replaced, it could no longer
find muxed streams for most videos at all). `yt-dlp` is maintained specifically to track those changes, which
per the README roadmap is far less maintenance than reimplementing the same cat-and-mouse game here.

### Key dependency: Velopack (NuGet package + `vpk` CLI tool)

Update checking/downloading/applying (`Services/UpdateService.cs`) and packaging
(`.github/workflows/release.yml`) both go through [Velopack](https://velopack.io/) (MIT-licensed) — the
`Velopack` NuGet package in-app, and its `vpk` CLI tool (installed as a `dotnet tool` in CI, not referenced
by the project itself) for building release packages. See the `update-distribution-strategy` project memory
for the full comparison against alternatives (a real APT repo/PPA, Snap, etc.) and why Velopack won for
Windows/macOS while Linux stays AppImage-only for now. Two things about it are load-bearing enough to
repeat here even though they're also covered where the relevant code lives:
- `VelopackApp.Build().Run()` must be the literal first line of `Program.Main`, before anything else
  (`UpdateService`'s notes above explain the verified failure mode if this is ever moved or removed).
- Its exact C# API (verified in this session via reflection against the real installed 1.2.0 package rather
  than trusted from docs alone, since a couple of details either weren't in the docs or turned out
  subtly different — e.g. `ApplyUpdatesAndRestart` takes a `VelopackAsset`, not the `UpdateInfo` itself)
  is worth re-verifying the same way before making further changes here, rather than assuming docs/memory
  are exactly right — Velopack is under active development.

### Key dependency: FluentAvaloniaUI (NuGet package)

[FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia) (MIT-licensed) supplies the app's navigation
shell — `MainWindow`'s `FANavigationView` and `SettingsView`'s `FASettingsExpander`/`FASettingsExpanderItem`
groups — plus `styling:FluentAvaloniaTheme` in `App.axaml`, which replaced vanilla Avalonia's `<FluentTheme
/>` app-wide (it's a superset covering the same base controls, so nothing outside `MainWindow`/`SettingsView`
needed to change). See the `ui-navigation-shell` project memory for why this was added (a modal
`SettingsWindow` read as disconnected from the rest of the app, however it was styled) and for a validated
throwaway demo confirming the approach before it touched real code. One detail worth repeating here since
it's easy to get wrong from memory or older docs/tutorials: **this package's 3.x line prefixes every one of
its own custom control/event-args types with `FA`** — `FANavigationView`, `FANavigationViewItem`,
`FASettingsExpander`, `FASettingsExpanderItem`, `FASymbolIconSource`, `FANavigationViewSelectionChangedEventArgs`,
`FANavigationViewBackRequestedEventArgs`, etc. (property/event *names* on those types are unprefixed and
unchanged from what older 2.x-era docs show — it's only the type names that changed). Confirmed by
inspecting the actual installed 3.1.0 DLL in this session rather than trusted from docs, the same way
Velopack's API is verified above — re-check the same way against whatever version is actually installed
before assuming a type name from a tutorial or an older FluentAvalonia app is still correct.

A second gotcha, same lesson: `FANavigationView.PaneHeader`/`PaneTitle` **compile fine and simply never
render anything** when `PaneDisplayMode="Top"` — only `PaneCustomContent` actually shows up on the left in
that mode. This shipped once (the icon+"Yoink" silently missing from the nav bar) before being caught by
rendering `MainWindow` headless to an actual bitmap and looking at it, rather than trusting that XAML which
compiles must also render — see the `headless-visual-verification` project memory for the technique, worth
reusing for any future Avalonia layout question in this repo rather than assuming from properties existing.
