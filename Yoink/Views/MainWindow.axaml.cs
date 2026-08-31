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

    public MainWindow()
    {
        InitializeComponent();
        Icon = App.CurrentIcon;

        _queue = new DownloadQueueService(_ytDlp);
        _queue.ItemChanged += OnQueueItemChanged;

        LstQueue.ItemsSource = _items;
        UpdateEmptyQueueVisibility();

        Opened += MainWindow_Opened;

        _ = LoadQueueAsync();
        _ = WarnIfYtDlpMissingAsync();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Silent, throttled to roughly once a day via <see cref="AppSettings.LastUpdateCheckUtc"/>.
    /// Only ever prompts (<see cref="UpdatePromptDialog"/>) — never downloads or installs anything
    /// without that explicit click, per the agreed update UX. A no-op for a `dotnet run`/self-built
    /// copy, since <see cref="UpdateService.IsInstalled"/> is false there.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var settings = SettingsService.Load();
        if (settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        SettingsService.Save(settings);

        var updateInfo = await _updates.CheckForUpdatesAsync();
        if (updateInfo is not null)
            await UpdatePromptDialog.ShowAsync(this, _updates, updateInfo);
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
        Dispatcher.UIThread.Post(() => _ = AddDownloadDialog.ShowAsync(this, _queue, url));
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

    private async Task WarnIfYtDlpMissingAsync()
    {
        if (!await _ytDlp.IsAvailableAsync())
        {
            await MessageBoxWindow.ShowAsync(
                this,
                "yt-dlp wasn't found on PATH, so downloads will fail until it's installed. See the README for setup instructions.",
                "yt-dlp not found");
        }
    }

    /// <summary>
    /// The nav bar has exactly one navigable destination — its own built-in Settings entry
    /// (<c>FANavigationView.IsSettingsVisible</c>); Downloads is the home/dashboard, reached only via
    /// the back button, never a menu item of its own. <c>e.IsSettingsSelected</c> is how
    /// FluentAvalonia reports that entry being picked. Settings is a page in this same window now
    /// (<see cref="SettingsView"/>, formerly the standalone <c>SettingsWindow</c>), not a modal —
    /// swapping which <c>Border</c>/<see cref="SettingsView"/> is visible is enough, since neither
    /// one needs anything handed back: every consumer (<see cref="_clipboardWatcher"/>,
    /// App.axaml.cs's Closing handler, DownloadQueueService's loop) re-reads settings fresh at the
    /// point it needs them regardless of which page is showing. The back button itself only appears
    /// while on Settings — Downloads has nowhere to go "back" from, so it stays hidden there.
    /// </summary>
    private void NavView_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        var showSettings = e.IsSettingsSelected;
        DownloadsBody.IsVisible = !showSettings;
        SettingsBody.IsVisible = showSettings;
        NavView.IsBackButtonVisible = showSettings;
        NavView.IsBackEnabled = showSettings;
    }

    /// <summary>
    /// The nav bar's back arrow only ever needs to return to Downloads — there's nowhere deeper to
    /// go back from yet, so this doesn't need an actual navigation stack. Everything a user actually
    /// sees is handled by the four lines below; <c>NavView.SelectedItem = null</c> after them (there's
    /// no "Downloads" item to select instead — see <see cref="NavView_SelectionChanged"/>) is purely
    /// cosmetic bookkeeping to un-highlight the built-in Settings entry.
    ///
    /// That cleanup crashed in production with a <see cref="NullReferenceException"/> deep inside
    /// FluentAvaloniaUI 3.1.0's own <c>ChangeSelection</c>/<c>RaiseItemInvoked</c>, called
    /// synchronously from <c>set_SelectedItem</c> while still nested inside the control's own
    /// <c>OnBackButtonClicked</c> call frame (see the stack trace this was diagnosed from). Isolated
    /// testing (a throwaway headless Avalonia harness against both a synthetic <c>FANavigationView</c>
    /// and this real <c>MainWindow</c>) could NOT reproduce it via a plain property assignment outside
    /// that click's call stack, which points at reentrancy against work <c>OnBackButtonClicked</c> is
    /// still doing when our handler runs — but that couldn't be proven with certainty without
    /// literally clicking the rendered button, which this sandbox has no way to simulate. So: deferred
    /// to the next UI-thread tick (lets <c>OnBackButtonClicked</c> finish first, which should avoid the
    /// reentrancy if that's really the cause) AND wrapped in a targeted catch as a deliberate
    /// belt-and-braces fallback — worst case if the deferral doesn't fully fix it, the gear stays
    /// visually highlighted after going back, which is a real but minor cosmetic gap, not a crash.
    /// </summary>
    private void NavView_BackRequested(object? sender, FANavigationViewBackRequestedEventArgs e)
    {
        DownloadsBody.IsVisible = true;
        SettingsBody.IsVisible = false;
        NavView.IsBackButtonVisible = false;
        NavView.IsBackEnabled = false;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                NavView.SelectedItem = null;
            }
            catch (NullReferenceException)
            {
                // See doc comment above — cosmetic only, the actual page swap above already happened.
            }
        });
    }

    private async void BtnAddDownload_Click(object? sender, RoutedEventArgs e)
    {
        await AddDownloadDialog.ShowAsync(this, _queue);
    }

    private async void BtnPause_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.PauseAsync);
    private async void BtnResume_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.ResumeAsync);
    private async void BtnCancel_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.CancelAsync);
    private async void BtnRetry_Click(object? sender, RoutedEventArgs e) => await RunItemActionAsync(sender, _queue.RetryAsync);

    private static Task RunItemActionAsync(object? sender, Func<long, CancellationToken, Task> action) =>
        sender is Control { DataContext: DownloadQueueItem item } ? action(item.Id, default) : Task.CompletedTask;

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
