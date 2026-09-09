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
}
