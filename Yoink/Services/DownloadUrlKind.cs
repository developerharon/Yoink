using System;
using System.Linq;

namespace Yoink.Services;

/// <summary>
/// "What kind of URL is this" logic shared by <c>Views.AddDownloadDialog</c> (deciding whether to
/// resolve a pasted URL via yt-dlp or <see cref="DownloadEngine"/>) and
/// <see cref="ClipboardWatcherService"/> (deciding whether a copied link is worth prompting about at
/// all) — kept in one place rather than duplicated so the two stay in sync.
/// </summary>
public static class DownloadUrlKind
{
    // Deliberately conservative, same reasoning as ClipboardWatcherService's own doc comment: a
    // missed exotic YouTube domain is far less annoying than misrouting an unrelated URL.
    private static readonly string[] YouTubeHosts =
        ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"];

    // A representative, not exhaustive, set of common downloadable-file extensions — good enough to
    // separate "clearly a file to grab" from "just a webpage link" for clipboard auto-catch without
    // needing a live Content-Type check on every clipboard change.
    private static readonly string[] DownloadableFileExtensions =
    [
        ".pdf", ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar",
        ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm", ".appimage",
        ".iso", ".apk",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".mkv", ".avi", ".mov",
        ".png", ".jpg", ".jpeg", ".gif"
    ];

    /// <summary>The hostname check AddDownloadDialog's kind detection is built on — youtube.com/youtu.be (and its usual subdomains) route to yt-dlp, everything else is a plain direct-link file.</summary>
    public static bool IsYouTubeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        YouTubeHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>magnet:</c> link — checked ahead of <see cref="IsYouTubeUrl"/> in
    /// <c>Views.AddDownloadDialog</c>'s kind detection, since a magnet URI has no host
    /// <see cref="Uri.TryCreate(string,UriKind,out Uri)"/> would even parse the same way an http(s)
    /// URL does.
    /// </summary>
    public static bool IsMagnetLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>A direct http(s) link to a <c>.torrent</c> file — <see cref="Services.TorrentEngine"/> downloads and parses it itself rather than saving the raw .torrent bytes as a generic file.</summary>
    public static bool IsTorrentFileUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>Either shape of a torrent source <c>Views.AddDownloadDialog</c> can resolve from a pasted URL — a local <c>.torrent</c> file is a separate, explicit "Browse" action instead, not URL detection.</summary>
    public static bool IsTorrentSource(string url) => IsMagnetLink(url) || IsTorrentFileUrl(url);

    /// <summary>
    /// True when an http(s) URL's path ends in a well-known downloadable-file extension. Used by
    /// <see cref="ClipboardWatcherService"/> to decide a copied link is worth prompting about — not
    /// by <c>Views.AddDownloadDialog</c>'s own kind detection, which is a plain "YouTube or not"
    /// check via <see cref="IsYouTubeUrl"/> (see that view's own notes for why: hostname-checking a
    /// URL the user explicitly chose to paste doesn't need this extra guard against false positives
    /// the way silently watching every clipboard change does).
    /// </summary>
    public static bool LooksLikeDownloadableFile(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return DownloadableFileExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Any of the three shapes this app knows how to turn into a queue item at all — a YouTube link,
    /// a torrent source, or a plain downloadable file. Used by <c>Program.cs</c> to recognize a bare
    /// positional command-line argument as a URL worth forwarding (rather than, say, some unrelated
    /// flag), which is what lets the .deb's <c>.desktop</c> file's <c>Exec=... %u</c> and the Chrome
    /// native-messaging host's own <c>Process.Start</c> fallback both hand Yoink a URL the same way.
    /// </summary>
    public static bool IsRecognizedDownloadUrl(string url) =>
        IsYouTubeUrl(url) || IsTorrentSource(url) || LooksLikeDownloadableFile(url);
}
