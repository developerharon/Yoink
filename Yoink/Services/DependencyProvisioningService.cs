using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

/// <summary>Where yt-dlp/ffmpeg were found (or provisioned), and whether Yoink owns that copy.</summary>
/// <param name="YtDlpPath">Executable name/path to hand to <see cref="ProcessStartInfo.FileName"/>.</param>
/// <param name="FfmpegDirectory">
/// Directory to pass as yt-dlp's own <c>--ffmpeg-location</c>, or null when ffmpeg is already on
/// PATH and yt-dlp can find it itself without being told.
/// </param>
/// <param name="YtDlpIsManaged">True when <see cref="YtDlpPath"/> is a Yoink-managed copy rather than a system one on PATH.</param>
/// <param name="FfmpegIsManaged">True when ffmpeg is a Yoink-managed copy rather than a system one on PATH.</param>
public sealed record DependencyPaths(
    string YtDlpPath,
    string? FfmpegDirectory,
    bool YtDlpIsManaged,
    bool FfmpegIsManaged);

/// <summary>
/// Provisions yt-dlp and ffmpeg for a packaged Yoink install, so a plain "download the AppImage/
/// Setup.exe and run it" user never has to separately install and keep either one up to date
/// themselves — see the CLAUDE.md/README notes this class's introduction added for the full
/// reasoning against the alternatives (require a system install; bundle a fixed copy at build
/// time). yt-dlp specifically needs frequent updates just to keep working at all (YouTube changes
/// something server-side every few weeks), which a copy baked into a given Yoink release would go
/// stale against almost immediately — auto-downloading the latest build, and re-checking
/// periodically, is what actually keeps it working.
///
/// A system copy already on PATH is always preferred and never touched — this class only downloads
/// into (and later updates) its own managed folder
/// (<c>%AppData%/Yoink/bin</c>) when nothing already provides the tool, so it never fights a
/// distro's own package manager or a developer's deliberate manual install (this repo's own dev
/// setup, for instance — see CLAUDE.md).
///
/// Windows/macOS provisioning is implemented the same way as Linux's (conditioned on
/// <see cref="OperatingSystem"/> checks) but, like the rest of this app's Windows/macOS-specific
/// code (see CLAUDE.md's "merged-titlebar-navbar" notes), hasn't been exercised on real hardware —
/// this sandbox has neither.
/// </summary>
public sealed class DependencyProvisioningService
{
    // GitHub's own "latest" redirect for each platform's standalone yt-dlp build — no GitHub API
    // call needed (and no risk of hitting its low unauthenticated rate limit), since these URLs
    // never change and simply 302 to whatever the current release actually is.
    private const string YtDlpWindowsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpMacUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";
    private const string YtDlpLinuxUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";
    private const string YtDlpLinuxArm64Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64";

    // BtbN/FFmpeg-Builds' "latest" tag is a rolling tag that always points at the newest build
    // under these same two asset names — same "no API call" reasoning as yt-dlp above, though
    // since the filename never changes there's no version number to read out of the URL the way
    // there is for yt-dlp (see GetLatestFfmpegTagAsync, which reads Last-Modified instead).
    private const string FfmpegWindowsUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string FfmpegLinuxUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz";

    // BtbN doesn't build for macOS; evermeet.cx is the closest equivalent (a single always-current
    // build, versioned, distributed as a plain zip of one binary) and is what this file's own
    // research confirmed actually serves a .zip rather than the 7z its landing page defaults to.
    private const string FfmpegMacInfoUrl = "https://evermeet.cx/ffmpeg/info/ffmpeg/release";

    private static readonly Regex YtDlpVersionFromUrlRegex = new(@"/releases/download/([^/]+)/", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly DownloadEngine _downloadEngine;

    public DependencyProvisioningService() : this(new HttpClient())
    {
    }

    public DependencyProvisioningService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _downloadEngine = new DownloadEngine(httpClient);
    }

    /// <summary>
    /// Internal (not private) and mutable purely so Yoink.Tests can redirect it to a temp directory,
    /// the same pattern <see cref="SettingsService.SettingsPath"/> already uses.
    /// </summary>
    internal static string ManagedBinDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Yoink",
        "bin");

    private static string YtDlpExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
    private static string FfmpegExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static string ManagedYtDlpPath => Path.Combine(ManagedBinDirectory, YtDlpExecutableName);
    private static string ManagedFfmpegPath => Path.Combine(ManagedBinDirectory, FfmpegExecutableName);

    private static string YtDlpDownloadUrl =>
        OperatingSystem.IsWindows() ? YtDlpWindowsUrl :
        OperatingSystem.IsMacOS() ? YtDlpMacUrl :
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? YtDlpLinuxArm64Url : YtDlpLinuxUrl;

    /// <summary>
    /// Whether provisioning actually needs to hit the network: true only when a dependency is
    /// neither on PATH nor already sitting in the managed folder from a previous run. Callers use
    /// this to decide whether to show a "setting up..." progress UI before calling
    /// <see cref="EnsureProvisionedAsync"/>, rather than showing one on every startup.
    /// </summary>
    public async Task<bool> NeedsProvisioningAsync(CancellationToken cancellationToken = default)
    {
        var ytDlpReady = await IsOnPathAsync(YtDlpExecutableName, "--version", cancellationToken).ConfigureAwait(false)
            || File.Exists(ManagedYtDlpPath);
        var ffmpegReady = await IsOnPathAsync(FfmpegExecutableName, "-version", cancellationToken).ConfigureAwait(false)
            || File.Exists(ManagedFfmpegPath);
        return !ytDlpReady || !ffmpegReady;
    }

    /// <summary>
    /// Resolves where yt-dlp/ffmpeg should be run from, downloading either one into the managed
    /// folder first if it isn't already available anywhere. Safe to call on every startup — when
    /// both are already on PATH or already downloaded, this does no network I/O at all.
    /// </summary>
    public async Task<DependencyPaths> EnsureProvisionedAsync(
        IProgress<string>? status,
        CancellationToken cancellationToken = default)
    {
        string ytDlpPath;
        bool ytDlpManaged;
        if (await IsOnPathAsync(YtDlpExecutableName, "--version", cancellationToken).ConfigureAwait(false))
        {
            ytDlpPath = YtDlpExecutableName;
            ytDlpManaged = false;
        }
        else
        {
            if (!File.Exists(ManagedYtDlpPath))
            {
                status?.Report("Downloading yt-dlp…");
                await DownloadYtDlpAsync(cancellationToken).ConfigureAwait(false);
                var version = ParseYtDlpVersionFromRedirect(await GetYtDlpRedirectLocationAsync(cancellationToken).ConfigureAwait(false));
                if (version is not null)
                {
                    var settings = SettingsService.Load();
                    settings.InstalledYtDlpVersion = version;
                    SettingsService.Save(settings);
                }
            }

            ytDlpPath = ManagedYtDlpPath;
            ytDlpManaged = true;
        }

        string? ffmpegDirectory;
        bool ffmpegManaged;
        if (await IsOnPathAsync(FfmpegExecutableName, "-version", cancellationToken).ConfigureAwait(false))
        {
            ffmpegDirectory = null;
            ffmpegManaged = false;
        }
        else
        {
            if (!File.Exists(ManagedFfmpegPath))
            {
                status?.Report("Downloading ffmpeg…");
                await DownloadFfmpegAsync(cancellationToken).ConfigureAwait(false);
                var tag = await GetLatestFfmpegTagAsync(cancellationToken).ConfigureAwait(false);
                if (tag is not null)
                {
                    var settings = SettingsService.Load();
                    settings.InstalledFfmpegBuildTag = tag;
                    SettingsService.Save(settings);
                }
            }

            ffmpegDirectory = ManagedBinDirectory;
            ffmpegManaged = true;
        }

        return new DependencyPaths(ytDlpPath, ffmpegDirectory, ytDlpManaged, ffmpegManaged);
    }

    /// <summary>
    /// Re-checks the *managed* copies only (a PATH-provided one is left to whatever installed it —
    /// see this class's own doc comment) against their upstream latest build, re-downloading
    /// whichever one has moved on. Meant to be called on the same throttled daily cadence as
    /// <c>UpdateService</c>'s own app-update check (<c>Views.MainWindow.CheckForUpdatesAsync</c>),
    /// so yt-dlp/ffmpeg freshness rides along with the app's existing "check once a day" heartbeat
    /// rather than becoming a second, separately-timed polling loop.
    /// </summary>
    public async Task<bool> CheckForManagedUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Load();
        var updatedAny = false;

        if (File.Exists(ManagedYtDlpPath))
        {
            var latestVersion = ParseYtDlpVersionFromRedirect(await GetYtDlpRedirectLocationAsync(cancellationToken).ConfigureAwait(false));
            if (latestVersion is not null && latestVersion != settings.InstalledYtDlpVersion)
            {
                await DownloadYtDlpAsync(cancellationToken).ConfigureAwait(false);
                settings.InstalledYtDlpVersion = latestVersion;
                updatedAny = true;
            }
        }

        if (File.Exists(ManagedFfmpegPath))
        {
            var latestTag = await GetLatestFfmpegTagAsync(cancellationToken).ConfigureAwait(false);
            if (latestTag is not null && latestTag != settings.InstalledFfmpegBuildTag)
            {
                await DownloadFfmpegAsync(cancellationToken).ConfigureAwait(false);
                settings.InstalledFfmpegBuildTag = latestTag;
                updatedAny = true;
            }
        }

        if (updatedAny)
            SettingsService.Save(settings);

        return updatedAny;
    }

    private async Task DownloadYtDlpAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManagedBinDirectory);
        await _downloadEngine.DownloadAsync(new Uri(YtDlpDownloadUrl), ManagedYtDlpPath, progress: null, cancellationToken)
            .ConfigureAwait(false);
        MakeExecutable(ManagedYtDlpPath);
    }

    private async Task DownloadFfmpegAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManagedBinDirectory);

        var tempDir = Path.Combine(Path.GetTempPath(), "Yoink-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string extractedFfmpeg;

            if (OperatingSystem.IsWindows())
            {
                var archivePath = Path.Combine(tempDir, "ffmpeg.zip");
                await _downloadEngine.DownloadAsync(new Uri(FfmpegWindowsUrl), archivePath, null, cancellationToken).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(archivePath, tempDir);
                extractedFfmpeg = FindExtractedFile(tempDir, "ffmpeg.exe");
            }
            else if (OperatingSystem.IsMacOS())
            {
                var zipUrl = await ResolveMacFfmpegZipUrlAsync(cancellationToken).ConfigureAwait(false);
                var archivePath = Path.Combine(tempDir, "ffmpeg.zip");
                await _downloadEngine.DownloadAsync(new Uri(zipUrl), archivePath, null, cancellationToken).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(archivePath, tempDir);
                extractedFfmpeg = FindExtractedFile(tempDir, "ffmpeg");
            }
            else
            {
                // BtbN ships Linux builds as .tar.xz, which .NET has no built-in decompressor for
                // (System.IO.Compression covers GZip/Deflate/Zip, not LZMA/XZ). Shelling out to the
                // system `tar` binary sidesteps reimplementing XZ decompression: `tar` (and the xz
                // support GNU tar auto-detects) is present on essentially every real Linux install,
                // including minimal ones, since apt/dpkg/rpm themselves depend on it.
                var archivePath = Path.Combine(tempDir, "ffmpeg.tar.xz");
                await _downloadEngine.DownloadAsync(new Uri(FfmpegLinuxUrl), archivePath, null, cancellationToken).ConfigureAwait(false);
                await ExtractTarAsync(archivePath, tempDir, cancellationToken).ConfigureAwait(false);
                extractedFfmpeg = FindExtractedFile(tempDir, "ffmpeg");
            }

            File.Copy(extractedFfmpeg, ManagedFfmpegPath, overwrite: true);
            MakeExecutable(ManagedFfmpegPath);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a temp directory — leaving stray files under the OS temp
                // folder isn't worth failing provisioning over.
            }
        }
    }

    private static string FindExtractedFile(string rootDirectory, string fileName)
    {
        var found = Directory.GetFiles(rootDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        if (found is null)
            throw new InvalidOperationException($"Downloaded ffmpeg archive didn't contain a '{fileName}' file.");

        return found;
    }

    private async Task<string> ResolveMacFfmpegZipUrlAsync(CancellationToken cancellationToken)
    {
        var json = await _httpClient.GetStringAsync(new Uri(FfmpegMacInfoUrl), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("download", out var download) &&
            download.TryGetProperty("zip", out var zip) &&
            zip.TryGetProperty("url", out var url) &&
            url.GetString() is { Length: > 0 } urlString)
        {
            return urlString;
        }

        throw new InvalidOperationException("Could not resolve the current ffmpeg download URL for macOS.");
    }

    private static async Task ExtractTarAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("tar")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destinationDirectory);

        using var process = new Process { StartInfo = startInfo };

        Process started;
        try
        {
            process.Start();
            started = process;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Could not run 'tar' to extract the downloaded ffmpeg archive. Install ffmpeg manually instead.", ex);
        }

        var stderr = await started.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await started.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (started.ExitCode != 0)
            throw new InvalidOperationException($"tar failed extracting ffmpeg: {stderr.Trim()}");
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private async Task<string?> GetYtDlpRedirectLocationAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var probeClient = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Head, YtDlpDownloadUrl);
        using var response = await probeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Headers.Location?.ToString();
    }

    /// <summary>
    /// Internal (not private) so Yoink.Tests can feed it sample redirect targets directly, without a
    /// real network call. GitHub's release-download redirect looks like
    /// ".../releases/download/2026.08.19/yt-dlp.exe" — the path segment between "download/" and the
    /// filename is yt-dlp's own version string (it releases under a plain date-stamped tag, not a
    /// "v"-prefixed semver one).
    /// </summary>
    internal static string? ParseYtDlpVersionFromRedirect(string? redirectLocation)
    {
        if (redirectLocation is null)
            return null;

        var match = YtDlpVersionFromUrlRegex.Match(redirectLocation);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<string?> GetLatestFfmpegTagAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            var json = await _httpClient.GetStringAsync(new Uri(FfmpegMacInfoUrl), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }

        // BtbN's asset filename never changes (it's a rolling "latest" tag), so there's no version
        // string to read out of a URL the way there is for yt-dlp above — Last-Modified on the
        // asset itself stands in for one instead.
        var url = OperatingSystem.IsWindows() ? FfmpegWindowsUrl : FfmpegLinuxUrl;
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Content.Headers.LastModified?.ToString() ?? response.Headers.ETag?.Tag;
    }

    /// <summary>
    /// Quick "is this on PATH and does it actually run" probe — shared shape with
    /// <see cref="YtDlpClient.IsAvailableAsync"/>, kept as its own copy here since that method only
    /// ever checks yt-dlp itself and has no reason to know about ffmpeg.
    /// </summary>
    private static async Task<bool> IsOnPathAsync(string executableName, string versionArgument, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executableName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(versionArgument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
