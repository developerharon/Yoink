using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yoink.Services;

namespace Yoink.Views;

/// <summary>
/// Linux's own update prompt — shown when <see cref="GitHubReleaseUpdateChecker.CheckForUpdateAsync"/>
/// finds a newer release. A small, focused sibling of <see cref="UpdatePromptDialog"/> rather than a
/// branch inside it: that dialog's "Install Update" click handler assumes Velopack's own
/// download-then-<c>ApplyUpdatesAndRestart</c> flow, which doesn't apply to a `.deb` install at all
/// (installing one needs root, which this app can't and shouldn't try to do for you) — so this one's
/// only action is opening the browser to the release page, no download/progress UI needed.
/// </summary>
public partial class LinuxUpdatePromptDialog : Window
{
    private string? _releaseUrl;

    public LinuxUpdatePromptDialog()
    {
        InitializeComponent();
        Icon = App.CurrentIcon;
    }

    public static Task ShowAsync(Window owner, GitHubReleaseInfo release)
    {
        var dialog = new LinuxUpdatePromptDialog { _releaseUrl = release.HtmlUrl };

        dialog.TxtVersion.Text = $"Version {release.Version} is available.";
        dialog.TxtNotes.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "No release notes provided."
            : release.ReleaseNotes;

        return dialog.ShowDialog(owner);
    }

    private void BtnLater_Click(object? sender, RoutedEventArgs e) => Close();

    private void BtnOpenReleasePage_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_releaseUrl!) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — no browser configured, or some other environment quirk. Nothing more
            // useful to do here than leave the dialog open so the user can copy the URL themselves
            // from the release notes above, or just try again.
        }

        Close();
    }
}
