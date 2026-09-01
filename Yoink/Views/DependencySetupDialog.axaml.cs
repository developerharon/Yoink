using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// Shown once, the first time <see cref="DependencyProvisioningService.NeedsProvisioningAsync"/>
/// finds yt-dlp and/or ffmpeg missing from both PATH and Yoink's own managed folder — see
/// <see cref="MainWindow"/>'s startup flow. Downloads whichever is missing and reports the resolved
/// paths back to the caller; <see cref="ShowAsync"/> returns null if the user closes the dialog
/// after a failed attempt rather than retrying.
/// </summary>
public partial class DependencySetupDialog : Window
{
    private DependencyProvisioningService? _dependencies;
    private DependencyPaths? _result;

    public DependencySetupDialog()
    {
        InitializeComponent();
        Icon = App.CurrentIcon;
        Opened += (_, _) => _ = RunProvisioningAsync();
    }

    public static async Task<DependencyPaths?> ShowAsync(Window owner, DependencyProvisioningService dependencies)
    {
        var dialog = new DependencySetupDialog { _dependencies = dependencies };
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private async Task RunProvisioningAsync()
    {
        ErrorPanel.IsVisible = false;
        ProgressBar.IsVisible = true;
        TxtStatus.Text = "Checking…";

        try
        {
            var progress = new Progress<string>(text => Dispatcher.UIThread.Post(() => TxtStatus.Text = text));
            _result = await _dependencies!.EnsureProvisionedAsync(progress);
            Close();
        }
        catch (Exception ex)
        {
            ProgressBar.IsVisible = false;
            TxtStatus.Text = "Couldn't finish setting up.";
            TxtError.Text = ex.Message;
            ErrorPanel.IsVisible = true;
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void BtnRetry_Click(object? sender, RoutedEventArgs e) => _ = RunProvisioningAsync();
}
