using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

internal sealed record GitHubReleaseApiResponse(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("body")] string? Body);

/// <summary>Source-generated context for <see cref="GitHubReleaseApiResponse"/> — same reflection-free, trim-safe reasoning as <c>AppSettingsJsonContext</c>.</summary>
[JsonSerializable(typeof(GitHubReleaseApiResponse))]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext;

/// <summary>A newer release than the one currently running, as reported by GitHub — everything <see cref="Views.LinuxUpdatePromptDialog"/> needs to show and act on.</summary>
public sealed record GitHubReleaseInfo(Version Version, string HtmlUrl, string? ReleaseNotes);

/// <summary>
/// Linux's own update check, replacing <see cref="UpdateService"/>'s Velopack-based one — a `.deb`
/// install has no Velopack context at all (<see cref="UpdateService.IsInstalled"/> is always false
/// for one), so this hits this repo's own GitHub Releases API directly instead, comparing the
/// latest tag against the currently-running assembly's own version. Best-effort throughout, exactly
/// like <see cref="UpdateService.CheckForUpdatesAsync"/> — a failed check (no network, GitHub
/// unreachable, rate-limited) is never worth surfacing as an error, and this never downloads or
/// applies anything itself: the only action available once a newer release is found is opening the
/// browser to it (see <see cref="Views.LinuxUpdatePromptDialog"/>) — installing a `.deb` needs root,
/// so there's no in-app apply step the way Velopack's Windows/macOS flow has.
/// </summary>
public sealed class GitHubReleaseUpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/developerharon/Yoink/releases/latest";

    public async Task<GitHubReleaseInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            // GitHub's API rejects requests with no User-Agent header at all.
            request.Headers.UserAgent.ParseAdd("Yoink");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.GitHubReleaseApiResponse);

            var latest = ParseVersion(release?.TagName);
            var current = Assembly.GetEntryAssembly()?.GetName().Version;
            if (latest is null || current is null || latest <= current)
                return null;

            return new GitHubReleaseInfo(
                latest,
                release!.HtmlUrl ?? "https://github.com/developerharon/Yoink/releases/latest",
                release.Body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// This repo's own tags are plain <c>vX.Y.Z</c> (see release.yml), so a bare
    /// <see cref="Version"/> comparison is enough — no need for a full semver library. Strips a
    /// leading <c>v</c>/<c>V</c> and any <c>-prerelease</c> suffix (<see cref="Version"/> can't
    /// represent one) before parsing. Internal so Yoink.Tests can exercise this directly, the same
    /// pattern as <c>DependencyProvisioningService.ParseYtDlpVersionFromRedirect</c>.
    /// </summary>
    internal static Version? ParseVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var trimmed = tagName.TrimStart('v', 'V').Split('-')[0];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
