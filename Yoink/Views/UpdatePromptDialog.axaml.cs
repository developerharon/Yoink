using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Velopack;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// Shown when <see cref="UpdateService.CheckForUpdatesAsync"/> finds a newer release. Per the
/// agreed update UX, this is the only place an update actually gets downloaded/applied — the check
/// itself is silent, but installing always needs an explicit click here first.
/// </summary>
public partial class UpdatePromptDialog : Window
{
    private UpdateService? _updates;
    private UpdateInfo? _updateInfo;

    public UpdatePromptDialog()
    {
        InitializeComponent();
        Icon = App.CurrentIcon;
    }

    public static Task ShowAsync(Window owner, UpdateService updates, UpdateInfo updateInfo)
    {
        var dialog = new UpdatePromptDialog { _updates = updates, _updateInfo = updateInfo };

        dialog.TxtVersion.Text =
            $"Version {updateInfo.TargetFullRelease.Version} is available (you have {updates.CurrentVersion}).";
        dialog.TxtNotes.Text = string.IsNullOrWhiteSpace(updateInfo.TargetFullRelease.NotesMarkdown)
            ? "No release notes provided."
            : updateInfo.TargetFullRelease.NotesMarkdown;

        return dialog.ShowDialog(owner);
    }

    private void BtnLater_Click(object? sender, RoutedEventArgs e) => Close();

    private async void BtnInstall_Click(object? sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnLater.IsEnabled = false;
        ProgressBar.IsVisible = true;

        try
        {
            await _updates!.DownloadUpdatesAsync(
                _updateInfo!,
                progress => Dispatcher.UIThread.Post(() => ProgressBar.Value = progress));

            // Exits the app, applies the update, and relaunches — nothing after this runs.
            _updates.ApplyUpdatesAndRestart(_updateInfo!);
        }
        catch (Exception ex)
        {
            await MessageBoxWindow.ShowAsync(this, $"Couldn't install the update: {ex.Message}", "Update failed");
            BtnInstall.IsEnabled = true;
            BtnLater.IsEnabled = true;
            ProgressBar.IsVisible = false;
        }
    }
}
