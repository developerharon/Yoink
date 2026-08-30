using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

/// <summary>
/// Desktop notifications — the other half of README roadmap step 6 (background operation &amp;
/// notifications), alongside the tray icon set up in <c>App.axaml.cs</c>. Fires a toast when a
/// download completes or fails, so the outcome is visible even with no Yoink window open (the app
/// keeps running in the tray rather than exiting when the window closes).
///
/// Linux only for now, via <c>notify-send</c> (part of libnotify-bin, commonly preinstalled on
/// Ubuntu desktop) — matches the README roadmap's own scoping of this step to "Ubuntu... plus
/// libnotify-based notifications." Windows/macOS notifications are a known, documented gap:
/// Windows toast notifications need either an app identity/packaging this project doesn't have yet
/// or a third-party package, so that's left for the packaging step (roadmap step 8) rather than
/// half-building it now. Best-effort throughout — a missing notify-send, or a session with no
/// notification daemon running, just means no toast appears, never a crash or an error dialog for
/// something this incidental.
/// </summary>
public static class NotificationService
{
    public static async Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "notify-send",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("--app-name=Yoink");
            process.StartInfo.ArgumentList.Add(title);
            process.StartInfo.ArgumentList.Add(message);

            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — see class doc comment.
        }
    }
}
