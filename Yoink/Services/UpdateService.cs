using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Yoink.Services;

/// <summary>
/// The update-checking/applying half of Yoink's distribution story, via
/// <a href="https://velopack.io/">Velopack</a> (MIT-licensed, cross-platform installer/updater —
/// see the project memory this decision came from for the full reasoning). Reads releases straight
/// from this repo's GitHub Releases, so there's no separate download server or hosting cost.
///
/// Deliberately thin: this class only checks/downloads/applies. Program.cs's
/// <c>VelopackApp.Build().Run()</c> call is the other required half — it must run before anything
/// else at startup so Velopack can recognize when it's been launched for an internal
/// install/update/uninstall hook rather than a normal run.
///
/// Per the agreed update UX, this never applies anything silently — <c>Views.MainWindow</c> always
/// prompts (via <c>Views.UpdatePromptDialog</c>) before downloading or installing, the same pattern
/// already used for the yt-dlp-missing check.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/developerharon/YourPlaylistDownloader";

    private readonly UpdateManager _manager;

    public UpdateService(string? repoUrl = null)
    {
        _manager = new UpdateManager(new GithubSource(repoUrl ?? RepoUrl, accessToken: null!, prerelease: false));
    }

    /// <summary>
    /// False when running from a plain `dotnet run`/self-built copy rather than a Velopack-installed
    /// one — <see cref="CheckForUpdatesAsync"/> always returns null in that case rather than trying
    /// (and failing) to check a release feed for a build that isn't managed by Velopack at all.
    /// </summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Null when not <see cref="IsInstalled"/> — a dev/self-built copy has no meaningful installed version.</summary>
    public SemanticVersion? CurrentVersion => _manager.CurrentVersion;

    /// <summary>
    /// Returns null if there's no update, if this isn't a Velopack-installed build
    /// (<see cref="IsInstalled"/>), or if the check itself failed for any reason (no network, GitHub
    /// unreachable, etc.) — best-effort, like the rest of the app's optional background checks; a
    /// failed update check is never worth surfacing as an error.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            return null;

        try
        {
            return await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task DownloadUpdatesAsync(UpdateInfo update, Action<int>? progress = null, CancellationToken cancellationToken = default) =>
        _manager.DownloadUpdatesAsync(update, progress ?? (_ => { }), cancellationToken);

    /// <summary>Exits the app immediately, applies the update, and relaunches. Nothing after this call runs.</summary>
    public void ApplyUpdatesAndRestart(UpdateInfo update) =>
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease, restartArgs: null);
}
