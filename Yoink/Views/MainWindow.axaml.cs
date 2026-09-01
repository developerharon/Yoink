using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Views;

public partial class MainWindow : Window
{
    private readonly YtDlpClient _ytDlp = new();
    private readonly DownloadQueueService _queue;
    private readonly UpdateService _updates = new();
    private readonly DependencyProvisioningService _dependencies = new();
    private readonly ObservableCollection<DownloadQueueItem> _items = new();

    // Id -> _items index, kept in lockstep with _items itself (see OnQueueItemChanged/
    // LoadQueueAsync, the only two places that mutate either). This queue doubles as download
    // history and is deliberately never pruned (see DownloadQueueService's doc comment), so a
    // heavy user's list only grows — without this, every single progress-percent tick for every
    // active download would rescan the entire history linearly just to find which row to update,
    // which is the one part of this hot path that's actually frequent (unlike inserting a new row,
    // which only happens once per download).
    private readonly Dictionary<long, int> _itemIndexById = new();

    // Created once the window is attached to a screen (see MainWindow_Opened) — the clipboard
    // isn't guaranteed to be available any earlier than that.
    private ClipboardWatcherService? _clipboardWatcher;

    /// <summary>
    /// The parameterless constructor App.axaml.cs actually uses — kept as a distinct, genuinely
    /// zero-argument overload rather than a default parameter value on the one below, because
    /// Avalonia's XAML runtime loader (<c>avares://</c> resource resolution, used by the previewer/
    /// hot reload) specifically requires a public constructor with no parameters at all to consider
    /// this XAML reachable; a defaultable parameter doesn't satisfy that (confirmed by the
    /// <c>AVLN3001</c> build warning that appeared the one time this was tried).
    /// </summary>
    public MainWindow() : this(null)
    {
    }

    /// <summary>
    /// <paramref name="databasePath"/> mirrors <see cref="DownloadQueueService"/>'s own constructor
    /// override — null (what the parameterless constructor above always passes) means the real
    /// %AppData%-pointed <c>queue.db</c>. Previously there was no override at all (see this class's
    /// "Known gap" note in CLAUDE.md); added specifically so window-level interaction tests like
    /// <c>MainWindowNavigationTests</c> can construct a real <see cref="MainWindow"/> against a temp
    /// database instead of the actual user's queue.
    /// </summary>
    public MainWindow(string? databasePath)
    {
        InitializeComponent();
        Icon = App.CurrentIcon;

        _queue = new DownloadQueueService(_ytDlp, databasePath);
        _queue.ItemChanged += OnQueueItemChanged;
        _queue.ItemRemoved += OnQueueItemRemoved;

        LstQueue.ItemsSource = _items;
        UpdateEmptyQueueVisibility();

        // Now that the nav bar doubles as the window's own title bar (WindowDecorations="None" in
        // MainWindow.axaml — see that file's comment on why, over the cooperative
        // ExtendClientAreaToDecorationsHint approach this used at first), CaptionButtons is this
        // app's own min/max/close row rather than anything OS-drawn, and there's no
        // WindowDecorationMargin to react to any more (it only ever reports space reserved by real
        // OS/window-manager chrome, and there isn't any here). NavView gets a fixed margin instead,
        // sized to CaptionButtons' own footprint (3 * 46px, kept in sync with the CaptionButton style
        // in App.axaml by eye — there's nothing to bind it to, since Panel doesn't measure siblings
        // against each other) so the icon/wordmark/Settings tab stay clear of it, on whichever side
        // matches the platform: right on Windows/Linux, left on macOS, where the traffic lights go.
        // Margining the whole NavView (not just padding it) is deliberate — see MainWindow.axaml's
        // PaneCustomContent comment for why Padding alone doesn't touch the Top-mode pane row at all.
        const double captionButtonsWidth = 46 * 3;
        if (OperatingSystem.IsMacOS())
        {
            CaptionButtons.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            CaptionButtons.Children.Clear();
            CaptionButtons.Children.Add(BtnClose);
            CaptionButtons.Children.Add(BtnMinimize);
            CaptionButtons.Children.Add(BtnMaximizeRestore);
            NavView.Margin = new Thickness(captionButtonsWidth, 0, 0, 0);
        }
        else
        {
            NavView.Margin = new Thickness(0, 0, captionButtonsWidth, 0);
        }

        UpdateMaximizeRestoreIcon();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateMaximizeRestoreIcon();
        };

        Opened += MainWindow_Opened;

        _ = LoadQueueAsync();
        _ = EnsureDependenciesAsync();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Dragging the icon/wordmark area moves the window — belt-and-braces alongside
    /// <c>chrome:WindowDecorationProperties.ElementRole="TitleBar"</c> in the XAML (see that
    /// element's comment). Only the primary button starts a drag, matching how a real title bar
    /// ignores right/middle clicks there.
    /// </summary>
    private void TitleBarDragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>
    /// The 4 edge + 4 corner strips in MainWindow.axaml stand in for the OS's own edge-drag resize,
    /// lost along with every other native decoration once <c>WindowDecorations="None"</c> in the
    /// XAML. Each strip's <c>Tag</c> names the matching <see cref="WindowEdge"/> member.
    /// </summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } || !Enum.TryParse<WindowEdge>(tag, out var edge))
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(edge, e);
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximizeRestore_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Close(), not a bare Hide()/TryShutdown() — this needs to raise the same Closing event a native
    // close button would, since App.SetUpTrayIcon's Closing handler (checking
    // WindowCloseReason.WindowClosing, which Close() does pass) is what actually decides
    // hide-to-tray vs. real shutdown.
    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Keeps <c>BtnMaximizeRestore</c>'s glyph/tooltip matching the window's actual state — including
    /// when that state changes some way other than clicking the button itself (double-clicking the
    /// drag handle, an OS-level snap gesture, ...), via the <see cref="AvaloniaObject.PropertyChanged"/>
    /// subscription in the constructor.
    /// </summary>
    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z");

    // Two overlapping outlines (the standard restore glyph) — a partial "back" square peeking out
    // top-right of a full "front" one, both sharing the (2,2) corner.
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M 2,0 H 9 V 7 M 0,2 H 7 V 9 H 0 Z");

    private void UpdateMaximizeRestoreIcon()
    {
        var maximized = WindowState == WindowState.Maximized;
        IcoMaximizeRestore.Data = maximized ? RestoreGlyph : MaximizeGlyph;
        BtnMaximizeRestore.SetValue(ToolTip.TipProperty, maximized ? "Restore" : "Maximize");
    }

    /// <summary>
    /// Silent, throttled to roughly once a day via <see cref="AppSettings.LastUpdateCheckUtc"/>.
    /// Only ever prompts (<see cref="UpdatePromptDialog"/>) — never downloads or installs anything
    /// without that explicit click, per the agreed update UX. A no-op for a `dotnet run`/self-built
    /// copy, since <see cref="UpdateService.IsInstalled"/> is false there.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        await CheckForDependencyUpdatesAsync();

        var settings = SettingsService.Load();
        if (settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        SettingsService.Save(settings);

        var updateInfo = await _updates.CheckForUpdatesAsync();
        if (updateInfo is not null)
            await UpdatePromptDialog.ShowAsync(this, _updates, updateInfo);
    }

    /// <summary>
    /// Keeps a Yoink-managed yt-dlp/ffmpeg copy fresh — rides the same call (and the same
    /// once-a-day throttle shape, via its own <see cref="AppSettings.LastDependencyCheckUtc"/>) as
    /// the app's own update check just above, per the "keep both in sync" design: dependency
    /// freshness and app-update freshness share one heartbeat rather than each polling on its own
    /// schedule. Never touches a copy that's on PATH instead — see
    /// <see cref="DependencyProvisioningService"/>'s own doc comment. Silent and best-effort, like
    /// <see cref="UpdateService"/>'s own check: a failed refresh just means the existing managed
    /// copy keeps working until the next check succeeds.
    /// </summary>
    private async Task CheckForDependencyUpdatesAsync()
    {
        var settings = SettingsService.Load();
        if (settings.LastDependencyCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        settings.LastDependencyCheckUtc = DateTimeOffset.UtcNow;
        SettingsService.Save(settings);

        try
        {
            if (await _dependencies.CheckForManagedUpdatesAsync())
            {
                var paths = await _dependencies.EnsureProvisionedAsync(null);
                _ytDlp.UseResolvedPaths(paths.YtDlpPath, paths.FfmpegDirectory);
            }
        }
        catch
        {
            // Best-effort — see doc comment above.
        }
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _clipboardWatcher = new ClipboardWatcherService(
            ReadClipboardTextAsync,
            () => SettingsService.Load().ClipboardWatchEnabled);
        _clipboardWatcher.UrlDetected += OnClipboardUrlDetected;
    }

    /// <summary>
    /// Avalonia 12's clipboard is data-transfer-shaped rather than a plain get/set-text API:
    /// <c>TryGetDataAsync</c> hands back an <see cref="IAsyncDataTransfer"/> snapshot, from which
    /// <c>TryGetTextAsync</c> (an extension method) pulls the text, if any.
    /// </summary>
    private async Task<string?> ReadClipboardTextAsync()
    {
        if (Clipboard is not { } clipboard)
            return null;

        var data = await clipboard.TryGetDataAsync();
        return data is null ? null : await data.TryGetTextAsync();
    }

    /// <summary>
    /// Called from a background thread by <see cref="ClipboardWatcherService"/> — hands off to the
    /// same add-download dialog the "+ Add download" button uses, pre-filled and awaiting
    /// confirmation, rather than queuing anything on its own.
    /// </summary>
    private void OnClipboardUrlDetected(string url)
    {
        Dispatcher.UIThread.Post(() => _ = AddDownloadDialog.ShowAsync(this, _queue, _ytDlp, url));
    }

    private async Task LoadQueueAsync()
    {
        var all = await _queue.GetAllAsync();
        foreach (var item in all.OrderByDescending(i => i.CreatedAt))
        {
            _items.Add(item);
            _itemIndexById[item.Id] = _items.Count - 1;
        }

        UpdateEmptyQueueVisibility();
    }

    /// <summary>
    /// First-run (or first-launch-since-losing-them) provisioning: if yt-dlp/ffmpeg aren't found
    /// anywhere (checked via <see cref="DependencyProvisioningService.NeedsProvisioningAsync"/>,
    /// which does no network I/O), shows <see cref="DependencySetupDialog"/> to download whichever
    /// is missing into Yoink's own managed folder, then points <see cref="_ytDlp"/> at whatever was
    /// actually resolved (PATH, or the managed copy). A no-op past the first launch, once both are
    /// already available — <c>EnsureProvisionedAsync</c> still runs to resolve the paths, but that's
    /// just fast local checks by then, not a download.
    /// </summary>
    private async Task EnsureDependenciesAsync()
    {
        DependencyPaths paths;

        if (await _dependencies.NeedsProvisioningAsync())
        {
            if (await DependencySetupDialog.ShowAsync(this, _dependencies) is not { } provisioned)
            {
                await MessageBoxWindow.ShowAsync(
                    this,
                    "yt-dlp/ffmpeg couldn't be set up automatically, so downloads will fail until they're " +
                    "installed and on PATH. See the README for manual setup instructions.",
                    "Setup incomplete");
                return;
            }

            paths = provisioned;
        }
        else
        {
            paths = await _dependencies.EnsureProvisionedAsync(null);
        }

        _ytDlp.UseResolvedPaths(paths.YtDlpPath, paths.FfmpegDirectory);
    }

    /// <summary>
    /// The nav bar has exactly one navigable destination — its own built-in Settings entry
    /// (<c>FANavigationView.IsSettingsVisible</c>); Downloads is the home/dashboard, reached only via
    /// <see cref="BtnBack_Click"/>, never a menu item of its own. <c>e.IsSettingsSelected</c> is how
    /// FluentAvalonia reports that entry being picked. Settings is a page in this same window now
    /// (<see cref="SettingsView"/>, formerly the standalone <c>SettingsWindow</c>), not a modal —
    /// swapping which <c>Border</c>/<see cref="SettingsView"/> is visible is enough, since neither
    /// one needs anything handed back: every consumer (<see cref="_clipboardWatcher"/>,
    /// App.axaml.cs's Closing handler, DownloadQueueService's loop) re-reads settings fresh at the
    /// point it needs them regardless of which page is showing. <c>BtnBack</c> itself only shows
    /// while on Settings — Downloads has nowhere to go "back" from, so it stays hidden there.
    /// </summary>
    private void NavView_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        var showSettings = e.IsSettingsSelected;
        DownloadsBody.IsVisible = !showSettings;
        SettingsBody.IsVisible = showSettings;
        BtnBack.IsVisible = showSettings;
    }

    /// <summary>
    /// Returns to Downloads — there's nowhere deeper to go back from yet, so this doesn't need an
    /// actual navigation stack. This is <c>BtnBack</c>'s own <c>Click</c> handler
    /// (<see cref="MainWindow.axaml"/>'s <c>PaneCustomContent</c>) rather than
    /// <c>FANavigationView.BackRequested</c>/<c>IsBackButtonVisible</c> — verified via a throwaway
    /// headless harness against this real window that FluentAvaloniaUI 3.1.0's built-in back button
    /// never actually releases the layout space it reserves in Top mode once shown once: toggling
    /// <c>IsBackButtonVisible</c>/<c>IsBackEnabled</c> back off left the icon+wordmark permanently
    /// shifted right by the button's width, even after returning to Downloads. <c>BtnBack</c> is a
    /// plain owned <c>Button</c>, <c>IsVisible</c>-toggled the same way <c>DownloadsBody</c>/
    /// <c>SettingsBody</c> already are, so it doesn't touch that broken reservation at all.
    ///
    /// <c>ClearSettingsSelection</c> below (there's no "Downloads" item to select instead — see
    /// <see cref="NavView_SelectionChanged"/>) un-highlights the built-in Settings entry so it can be
    /// opened again. This used to be a single <c>NavView.SelectedItem = null</c>, deferred a tick via
    /// <c>Dispatcher.UIThread.Post</c> and wrapped in a targeted catch for a
    /// <see cref="NullReferenceException"/> once seen deep inside FluentAvaloniaUI's own
    /// <c>ChangeSelection</c>/<c>RaiseItemInvoked</c> — back when this ran inside
    /// <c>FANavigationView</c>'s own built-in back button's <c>OnBackButtonClicked</c> call frame,
    /// before this app owned its back button. Real usage after that showed the gear icon staying
    /// highlighted post-Back, and unable to be clicked open again — reproduced enough to fix, though
    /// not down to the exact FluentAvaloniaUI internal race (a headless click-simulation harness
    /// against this real window, per the headless-visual-verification project memory, never once
    /// caught the underlying exception itself; whatever triggers it needs a live compositor's actual
    /// animation-frame timing — FluentAvaloniaUI's own <c>AnimateSelectionChanged</c> can defer part
    /// of its own indicator work to a *later* dispatcher tick when a selection indicator isn't
    /// realized yet, by its own source comment, which a fast real click could race). See
    /// <see cref="ClearSettingsSelection"/>'s own doc comment for the two changes this made.
    /// </summary>
    private void BtnBack_Click(object? sender, RoutedEventArgs e)
    {
        DownloadsBody.IsVisible = true;
        SettingsBody.IsVisible = false;
        BtnBack.IsVisible = false;

        ClearSettingsSelection();
    }

    /// <summary>
    /// Two changes from the version this replaced (see <see cref="BtnBack_Click"/>'s doc comment for
    /// the bug that prompted them):
    ///
    /// 1. Runs synchronously instead of deferred via <c>Dispatcher.UIThread.Post</c>. The deferral
    ///    predated this app's own <c>BtnBack</c> — it protected against reentering FluentAvaloniaUI's
    ///    own back-button click-handler call frame, which no longer exists now that this button is
    ///    ours. Deferring instead left an extra async gap in which FluentAvaloniaUI's own pending
    ///    "retry the selection indicator next tick" continuation (see this method's own source
    ///    comment on why it exists) could land *after* ours and re-assert a stale selected/highlighted
    ///    visual. Running this the moment Back is clicked, with no gap of our own, removes that window
    ///    entirely.
    /// 2. Forces <c>NavView.SettingsItem.IsSelected = false</c> directly, unconditionally — the same
    ///    low-level flag FluentAvaloniaUI's own <c>ChangeSelectStatusForItem</c> sets internally to
    ///    drive the highlight — as a backstop that doesn't depend on <c>ChangeSelection</c>'s own
    ///    animation pipeline running to completion.
    /// </summary>
    private void ClearSettingsSelection()
    {
        try
        {
            NavView.SelectedItem = null;
        }
        catch (NullReferenceException)
        {
            // See BtnBack_Click's doc comment — the IsSelected force-set below still runs regardless,
            // so the highlight is cleared even when this throws.
        }

        if (NavView.SettingsItem is { } settingsItem)
            settingsItem.IsSelected = false;
    }

    private async void BtnAddDownload_Click(object? sender, RoutedEventArgs e)
    {
        await AddDownloadDialog.ShowAsync(this, _queue, _ytDlp);
    }

    private async void BtnPause_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.PauseAsync);
    private async void BtnResume_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.ResumeAsync);
    private async void BtnCancel_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.CancelAsync);
    private async void BtnRetry_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.RetryAsync);

    private static Task RunItemActionAsync(object? sender, Func<long, CancellationToken, Task> action) =>
        sender is Control { DataContext: DownloadQueueItem item } ? action(item.Id, default) : Task.CompletedTask;

    /// <summary>Removes a row from the list only — the downloaded file, if any, is left alone.</summary>
    private async void BtnRemoveFromList_Click(object? sender, RoutedEventArgs e) =>
        await RunItemActionAsync(sender, (id, ct) => _queue.DeleteAsync(id, deleteFile: false, ct));

    /// <summary>
    /// The rarer, genuinely destructive option in the Delete flyout — confirms first (unlike every
    /// other row action, none of which are hard to reverse the way an actual file deletion is) via
    /// <see cref="MessageBoxWindow.ShowConfirmAsync"/>.
    /// </summary>
    private async void BtnDeleteWithFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DownloadQueueItem item })
            return;

        var confirmed = await MessageBoxWindow.ShowConfirmAsync(
            this,
            $"Delete \"{item.DisplayTitle}\" from disk and remove it from this list? This can't be undone.",
            "Delete download",
            "Delete");

        if (confirmed)
            await _queue.DeleteAsync(item.Id, deleteFile: true);
    }

    /// <summary>
    /// Opens the OS file manager at the downloaded file, so a completed queue entry is actually
    /// useful once it's scrolled out of view.
    /// </summary>
    private static void BtnShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DownloadQueueItem { FilePath: { } filePath } } || string.IsNullOrWhiteSpace(filePath))
            return;

        var directory = Path.GetDirectoryName(filePath);
        if (directory is null)
            return;

        try
        {
            // ArgumentList, not a hand-quoted Arguments string: filePath comes from the
            // downloaded video's title (BuildDestinationPath in DownloadQueueService), which is
            // remote-controlled — anyone can upload a YouTube video with an attacker-chosen
            // title. A title containing a literal '"' would have broken out of the quoting below
            // and let extra arguments reach explorer.exe/open/xdg-open. ArgumentList sidesteps
            // that entirely, the same way every process launch elsewhere in this codebase already
            // does (YtDlpClient, NotificationService).
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add($"/select,{filePath}");
                Process.Start(psi);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = true };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(filePath);
                Process.Start(psi);
            }
            else
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = true };
                psi.ArgumentList.Add(directory);
                Process.Start(psi);
            }
        }
        catch
        {
            // Best-effort — not worth a dialog if the platform lacks a file manager to hand off to.
        }
    }

    /// <summary>
    /// Applies a live change from the queue to the in-memory list the UI is bound to. Every
    /// <see cref="DownloadQueueService.ItemChanged"/> payload is a complete snapshot of that item
    /// (see the comment in <see cref="DownloadQueueService"/>'s UpdateStatusAsync), so replacing
    /// the whole entry is enough — <see cref="DownloadQueueItem"/> doesn't need to implement
    /// INotifyPropertyChanged for the row to refresh.
    ///
    /// Looks the existing row up via <see cref="_itemIndexById"/> (O(1)) rather than scanning
    /// <see cref="_items"/> (O(n)) — this fires on every progress-percent tick of every active
    /// download, so it's the one hot path in this class, on a list that only ever grows (the queue
    /// doubles as never-pruned download history).
    ///
    /// This keeps firing — and still notifies on completion (below) — even while the window is
    /// hidden in the tray: closing the window only hides it (see App.axaml.cs), it doesn't
    /// unsubscribe this handler or stop the queue's background loop.
    /// </summary>
    private void OnQueueItemChanged(DownloadQueueItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var isNew = !_itemIndexById.TryGetValue(item.Id, out var index);
            var previousStatus = isNew ? (DownloadQueueStatus?)null : _items[index].Status;

            if (isNew)
            {
                // New rows always land at the front (newest-first) — every already-tracked index
                // shifts by one to match. Rare relative to the plain replace below (once per
                // enqueue vs. once per progress tick), so an O(n) shift here is the right trade.
                if (_itemIndexById.Count > 0)
                {
                    foreach (var id in _itemIndexById.Keys.ToArray())
                        _itemIndexById[id]++;
                }

                _items.Insert(0, item);
                _itemIndexById[item.Id] = 0;
            }
            else
            {
                _items[index] = item;
            }

            if (previousStatus != item.Status && item.Status is DownloadQueueStatus.Completed or DownloadQueueStatus.Failed)
                _ = NotifyDownloadFinishedAsync(item);

            UpdateEmptyQueueVisibility();
        });
    }

    /// <summary>
    /// Mirror of <see cref="OnQueueItemChanged"/> for <see cref="DownloadQueueService.ItemRemoved"/>:
    /// drops the row from <see cref="_items"/>/<see cref="_itemIndexById"/> instead of replacing it.
    /// Every tracked index past the removed one shifts down by one to stay in sync — the same
    /// bookkeeping <see cref="OnQueueItemChanged"/>'s own insert-at-front path already does, just in
    /// the other direction.
    /// </summary>
    private void OnQueueItemRemoved(long id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_itemIndexById.TryGetValue(id, out var index))
                return;

            _items.RemoveAt(index);
            _itemIndexById.Remove(id);

            foreach (var key in _itemIndexById.Keys.ToArray())
            {
                if (_itemIndexById[key] > index)
                    _itemIndexById[key]--;
            }

            UpdateEmptyQueueVisibility();
        });
    }

    private static Task NotifyDownloadFinishedAsync(DownloadQueueItem item)
    {
        var completed = item.Status == DownloadQueueStatus.Completed;
        var title = completed ? "Download complete" : "Download failed";
        var message = completed ? item.DisplayTitle : $"{item.DisplayTitle} — {item.ErrorMessage}";
        return NotificationService.NotifyAsync(title, message);
    }

    protected override void OnClosed(EventArgs e)
    {
        _queue.ItemChanged -= OnQueueItemChanged;
        _queue.ItemRemoved -= OnQueueItemRemoved;
        _queue.Dispose();

        if (_clipboardWatcher is not null)
        {
            _clipboardWatcher.UrlDetected -= OnClipboardUrlDetected;
            _clipboardWatcher.Dispose();
        }

        base.OnClosed(e);
    }

    private void UpdateEmptyQueueVisibility()
    {
        TxtEmptyQueue.IsVisible = _items.Count == 0;
    }
}
