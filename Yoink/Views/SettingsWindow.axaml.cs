using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// The settings screen from README roadmap step 7 ("a settings screen to control all of it") —
/// consolidates every persisted preference in one place, including the theme/clipboard/tray
/// toggles that used to live directly in <see cref="MainWindow"/>'s header. Every control persists
/// its own change immediately on the spot (read-modify-write via <see cref="SettingsService"/>,
/// same as the header toggles did before) rather than waiting for a "Save" button — there isn't
/// one, just "Close". Nothing here needs to push a live update anywhere: <see cref="MainWindow"/>'s
/// <c>ClipboardWatcherService</c> and <c>DownloadQueueService</c> both re-read settings fresh at the
/// point they need them, so a change here takes effect on their very next check.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var settings = SettingsService.Load();

        CboTheme.SelectedIndex = (int)settings.Theme;
        ChkClipboardWatch.IsChecked = settings.ClipboardWatchEnabled;
        ChkMinimizeToTray.IsChecked = settings.MinimizeToTrayOnClose;

        NudMaxConcurrent.Value = settings.MaxConcurrentDownloads;
        NudPerDownloadLimit.Value = settings.PerDownloadSpeedLimitKBps;
        NudGlobalLimit.Value = settings.GlobalSpeedLimitKBps;

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

    private void ChkClipboardWatch_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UpdateSettings(s => s.ClipboardWatchEnabled = ChkClipboardWatch.IsChecked == true);

    private void ChkMinimizeToTray_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UpdateSettings(s => s.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true);

    private void NudMaxConcurrent_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.MaxConcurrentDownloads = (int)(NudMaxConcurrent.Value ?? 1));

    private void NudPerDownloadLimit_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.PerDownloadSpeedLimitKBps = ToNullableLimit(NudPerDownloadLimit.Value));

    private void NudGlobalLimit_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        UpdateSettings(s => s.GlobalSpeedLimitKBps = ToNullableLimit(NudGlobalLimit.Value));

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

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

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
