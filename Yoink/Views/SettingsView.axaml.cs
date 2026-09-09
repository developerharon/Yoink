using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// The settings screen from README roadmap step 7 ("a settings screen to control all of it") —
/// consolidates every persisted preference in one place. Hosted as a page inside
/// <see cref="MainWindow"/>'s <c>FANavigationView</c> shell rather than a separate modal
/// <c>Window</c> (that's what this class used to be, back when it was <c>SettingsWindow</c>) — see
/// <see cref="MainWindow"/>'s doc comments for how navigating to/from it works. Every control
/// persists its own change immediately on the spot (read-modify-write via
/// <see cref="SettingsService"/>, same as before) rather than waiting for a "Save" button — there
/// isn't one; the back arrow in the nav bar is the only way out, and it doesn't need to trigger
/// anything since every change already committed on the spot. Nothing here needs to push a live
/// update anywhere: <see cref="MainWindow"/>'s <c>ClipboardWatcherService</c> and
/// <c>DownloadQueueService</c> both re-read settings fresh at the point they need them, so a change
/// here takes effect on their very next check.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// Guards <see cref="TxtExtraTrackers_TextChanged"/> against firing while the constructor or
    /// <see cref="BtnResetExtraTrackers_Click"/> is itself the one setting <c>TxtExtraTrackers.Text</c>
    /// — without this, setting the text to seed/reset it would immediately re-trigger the handler and
    /// save the exact same value right back. Harmless either way, just pointless churn; simpler to
    /// suppress than to make the handler itself figure out whether anything actually changed.
    /// </summary>
    private bool _suppressExtraTrackersTextChanged;

    public SettingsView()
    {
        InitializeComponent();

        var settings = SettingsService.Load();

        CboTheme.SelectedIndex = (int)settings.Theme;
        SetSelectedAccentSwatch(settings.AccentColor);
        ChkClipboardWatch.IsChecked = settings.ClipboardWatchEnabled;
        ChkMinimizeToTray.IsChecked = settings.MinimizeToTrayOnClose;

        TxtDownloadFolder.Text = DownloadQueueService.ResolveDownloadFolder(settings);

        NudMaxConcurrent.Value = settings.MaxConcurrentDownloads;
        NudMaxConnectionsPerDownload.Value = settings.MaxConnectionsPerDownload;
        NudPerDownloadLimit.Value = settings.PerDownloadSpeedLimitKBps;
        NudGlobalLimit.Value = settings.GlobalSpeedLimitKBps;

        // Suppressed while setting the initial text (see the field's own doc comment) — otherwise
        // this assignment alone would fire TxtExtraTrackers_TextChanged and immediately re-save the
        // exact same list right back, harmless but pointless on every single SettingsView construction.
        _suppressExtraTrackersTextChanged = true;
        TxtExtraTrackers.Text = string.Join(Environment.NewLine, settings.ExtraTorrentTrackers);
        _suppressExtraTrackersTextChanged = false;

        ChkSchedulingEnabled.IsChecked = settings.SchedulingEnabled;
        TpScheduleStart.SelectedTime = settings.ScheduleStart.ToTimeSpan();
        TpScheduleEnd.SelectedTime = settings.ScheduleEnd.ToTimeSpan();
        UpdateScheduleControlsEnabled(settings.SchedulingEnabled);
    }

    private void CboTheme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboTheme.SelectedIndex < 0)
            return;

        var preference = (ThemePreference)CboTheme.SelectedIndex;
        Application.Current!.RequestedThemeVariant = App.ToThemeVariant(preference);
        UpdateSettings(s => s.Theme = preference);
    }

    private void BtnAccentSwatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AccentColor>(tag, out var accent))
            return;

        App.ApplyAccent(accent);
        SetSelectedAccentSwatch(accent);
        UpdateSettings(s => s.AccentColor = accent);
    }

    /// <summary>Toggles the "Selected" style class (the ring in App.axaml's Button.AccentSwatch.Selected)
    /// onto whichever swatch matches <paramref name="accent"/> and off every other one.</summary>
    private void SetSelectedAccentSwatch(AccentColor accent)
    {
        foreach (var button in new[] { BtnAccentBlue, BtnAccentOrange, BtnAccentPurple, BtnAccentGreen, BtnAccentRed })
            button.Classes.Set("Selected", (string)button.Tag! == accent.ToString());
    }

    private void ChkClipboardWatch_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UpdateSettings(s => s.ClipboardWatchEnabled = ChkClipboardWatch.IsChecked == true);

    private void ChkMinimizeToTray_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UpdateSettings(s => s.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true);

    /// <summary>
    /// Opens the OS folder picker via Avalonia's <see cref="IStorageProvider"/> (the cross-platform
    /// replacement for a WinForms-style FolderBrowserDialog — Avalonia doesn't provide one directly,
    /// same reasoning as <c>MessageBoxWindow</c> standing in for <c>MessageBox</c>) so the chosen
    /// folder persists immediately like every other control on this page.
    /// </summary>
    private async void BtnBrowseDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
            return;

        var startLocation = await TryGetStartFolderAsync(storageProvider, TxtDownloadFolder.Text);

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a download folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation
        });

        var folder = result.Count > 0 ? result[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(folder))
            return;

        TxtDownloadFolder.Text = folder;
        UpdateSettings(s => s.DownloadFolder = folder);
    }

    private void BtnResetDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        TxtDownloadFolder.Text = SettingsService.GetDefaultDownloadFolder();
        UpdateSettings(s => s.DownloadFolder = null);
    }

    /// <summary>Best-effort: an unset/no-longer-existing path just opens the picker at its own
    /// platform-chosen default rather than failing the whole click.</summary>
    private static async Task<IStorageFolder?> TryGetStartFolderAsync(IStorageProvider storageProvider, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return await storageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private void NudMaxConcurrent_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.MaxConcurrentDownloads = (int)(NudMaxConcurrent.Value ?? 1));

    private void NudMaxConnectionsPerDownload_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.MaxConnectionsPerDownload = (int)(NudMaxConnectionsPerDownload.Value ?? 4));

    private void NudPerDownloadLimit_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.PerDownloadSpeedLimitKBps = ToNullableLimit(NudPerDownloadLimit.Value));

    private void NudGlobalLimit_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.GlobalSpeedLimitKBps = ToNullableLimit(NudGlobalLimit.Value));

    /// <summary>
    /// One tracker URL per line, same convention the text itself is seeded with (`string.Join` on
    /// <see cref="Environment.NewLine"/>) — blank lines and surrounding whitespace on each are
    /// dropped rather than saved verbatim, so an accidental extra blank line (or trailing whitespace
    /// from a paste) doesn't get handed to <c>TorrentEngine</c>'s own best-effort-per-tracker
    /// <c>Uri</c> parsing as a bogus entry. An empty box saves an empty list, which is exactly how
    /// this feature is meant to be turned off entirely — see <c>AppSettings.ExtraTorrentTrackers</c>'s
    /// own doc comment.
    /// </summary>
    private void TxtExtraTrackers_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressExtraTrackersTextChanged)
            return;

        var trackers = (TxtExtraTrackers.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        UpdateSettings(s => s.ExtraTorrentTrackers = trackers);
    }

    private void BtnResetExtraTrackers_Click(object? sender, RoutedEventArgs e)
    {
        _suppressExtraTrackersTextChanged = true;
        TxtExtraTrackers.Text = string.Join(Environment.NewLine, AppSettings.DefaultExtraTorrentTrackers);
        _suppressExtraTrackersTextChanged = false;

        UpdateSettings(s => s.ExtraTorrentTrackers = AppSettings.DefaultExtraTorrentTrackers.ToList());
    }

    private void ChkSchedulingEnabled_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = ChkSchedulingEnabled.IsChecked == true;
        UpdateScheduleControlsEnabled(enabled);
        UpdateSettings(s => s.SchedulingEnabled = enabled);
    }

    private void TpScheduleStart_SelectedTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) =>
        UpdateSettings(s => s.ScheduleStart = TimeOnly.FromTimeSpan(TpScheduleStart.SelectedTime ?? TimeSpan.Zero));

    private void TpScheduleEnd_SelectedTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) =>
        UpdateSettings(s => s.ScheduleEnd = TimeOnly.FromTimeSpan(TpScheduleEnd.SelectedTime ?? TimeSpan.Zero));

    private void UpdateScheduleControlsEnabled(bool enabled)
    {
        TpScheduleStart.IsEnabled = enabled;
        TpScheduleEnd.IsEnabled = enabled;
    }

    /// <summary>0 and "cleared" (null) both mean unlimited — see AppSettings' doc comments.</summary>
    private static int? ToNullableLimit(decimal? value) => value is > 0 ? (int)value.Value : null;

    private static void UpdateSettings(Action<AppSettings> apply)
    {
        var settings = SettingsService.Load();
        apply(settings);
        SettingsService.Save(settings);
    }
}
