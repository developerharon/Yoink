using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace Yoink.Services;

/// <summary>
/// One progress update from <see cref="TorrentEngine.DownloadAsync"/> — bytes-based like
/// <see cref="YtDlpDownloadProgress"/>/<see cref="DownloadEngineProgress"/> so <c>DownloadQueueItem</c>'s
/// existing <c>SizeText</c>/<c>ShowSize</c> presentation works unmodified, plus the two numbers unique
/// to a peer-to-peer download. <paramref name="TotalBytes"/> is null until metadata (the torrent's own
/// file list/sizes) has actually resolved — immediately for a local/already-downloaded .torrent file,
/// only once a magnet link's metadata exchange with a peer completes for a magnet source (see
/// <paramref name="PhaseText"/>, set during that wait). <paramref name="ResolvedName"/> mirrors that
/// same "not known until metadata resolves" timing — null until then, the torrent's real name from
/// then on — so a caller that only had a magnet link's provisional <c>dn=</c> name to go on (see
/// <see cref="TorrentEngine.TryGetMagnetDisplayName"/>) can swap in the authoritative one once it's
/// available.
/// </summary>
public readonly record struct TorrentDownloadProgress(
    double Fraction,
    long BytesDownloaded,
    long? TotalBytes,
    int? SeederCount,
    int? LeecherCount,
    string? PhaseText,
    string? ResolvedName);

/// <summary>
/// The peer-to-peer download engine — wraps <a href="https://github.com/alanmcgovern/monotorrent">MonoTorrent</a>'s
/// <see cref="ClientEngine"/>/<see cref="TorrentManager"/>, the same "delegate to a maintained library
/// rather than reimplement the protocol" reasoning <c>YtDlpClient</c>'s own doc comment gives for yt-dlp
/// — reimplementing bencode parsing, DHT, PEX, and the peer wire protocol ourselves would be a huge,
/// fragile undertaking for zero benefit over an actively-maintained MIT-licensed library. Exact API
/// (property/type names, enum values, and — notably — that <see cref="TorrentManager.Progress"/> is a
/// 0-100 percentage, not a 0-1 fraction) verified in this session via reflection against the actual
/// installed 3.0.2 package rather than trusted from docs, the same "confirmed, not assumed" standard
/// this codebase already holds Velopack/FluentAvaloniaUI to.
///
/// <b>Source formats</b>: <see cref="DownloadAsync"/>'s <c>source</c> parameter accepts a
/// <c>magnet:</c> link, a direct http(s) URL to a <c>.torrent</c> file (downloaded and parsed via
/// <see cref="LoadTorrentAsync"/> before adding it to the engine), or a local filesystem path to a
/// <c>.torrent</c> file — <c>Services.DownloadUrlKind</c>/<c>Views.AddDownloadDialog</c> decide which
/// a given <c>DownloadQueueItem.Url</c> actually is.
///
/// <b>One shared <see cref="ClientEngine"/></b>, owned by this class (mirrors <c>DownloadEngine</c>'s
/// own shared-<see cref="HttpClient"/> reasoning) — a fresh engine per download would each bind its own
/// listen port and DHT node, which is both wasteful and unnecessary; <see cref="ClientEngine"/> is
/// designed to host many concurrent <see cref="TorrentManager"/>s. DHT and fast-resume are both on by
/// <see cref="EngineSettingsBuilder"/>'s own defaults (confirmed by reflecting the actual default
/// values, not assumed) — this constructor only overrides <see cref="EngineSettingsBuilder.CacheDirectory"/>,
/// pointed at Yoink's own managed folder rather than a relative "cache" directory next to the
/// executable (that library default, also confirmed by reflection).
///
/// <b>"End everything" — no seeding after a download finishes, per the project's own explicit product
/// decision</b>: every call through <see cref="DownloadAsync"/>, on success, cancellation, or failure
/// alike, ends in the same <c>finally</c> block calling <see cref="TorrentManager.StopAsync()"/> (closes
/// every peer connection and sends trackers a "stopped" announce) followed by
/// <see cref="ClientEngine.RemoveAsync(TorrentManager, RemoveMode)"/> (detaches the manager from the
/// engine entirely — Yoink never continues seeding a torrent once its own download loop has moved on,
/// unlike a dedicated torrent client that would keep seeding for the swarm's benefit). There is a small,
/// unavoidable window — at most one polling tick, see <see cref="PollInterval"/> — between MonoTorrent
/// itself marking a torrent <see cref="TorrentState.Seeding"/> internally and this loop noticing
/// <see cref="TorrentManager.Complete"/> and reacting; genuinely instantaneous cutoff isn't something
/// MonoTorrent's public API exposes a hook for, short of polling faster than is useful for UI purposes.
/// <see cref="RemoveMode.CacheDataOnly"/> vs. <see cref="RemoveMode.KeepAllData"/> below is *not* about
/// this policy — it's the orthogonal choice of whether this attempt's fast-resume/metadata cache is
/// still worth keeping (a paused/canceled download's next attempt resumes from it) or not (nothing left
/// to resume once actually complete).
///
/// <b>Metadata resolution is bounded</b> (<see cref="MetadataResolutionTimeout"/>) — found necessary
/// as a real, user-reported bug: a magnet link with no trackers relies entirely on DHT for peer
/// discovery, and <see cref="TorrentManager.WaitForMetadataAsync(CancellationToken)"/> on its own has
/// nothing bounding it beyond the caller's own cancellation token. Reproduced directly in this
/// session — a real trackerless magnet link stayed at zero DHT nodes and zero peers for 100+ seconds
/// straight against the real network, exactly the "stuck at Fetching torrent metadata… for minutes"
/// symptom reported — and the fix verified the same way: the same magnet now throws a clear
/// <see cref="TimeoutException"/> at exactly the configured timeout (confirmed at 2 minutes, the
/// production default) instead of hanging, with the <c>finally</c> cleanup above completing cleanly
/// right after. See <see cref="MetadataResolutionTimeout"/>'s own doc comment for what's actually
/// likely going on when this fires — DHT bootstrap itself isn't broken (MonoTorrent ships real,
/// standard bootstrap routers by default, confirmed by reading its source), but it needs outbound UDP
/// to work, which some networks (corporate firewalls, some VPNs) restrict or block entirely.
/// </summary>
public sealed class TorrentEngine : IDisposable
{
    /// <summary>
    /// How often the download loop re-checks progress/peer counts and reports them via
    /// <see cref="DownloadAsync"/>'s <c>progress</c> callback. Not tuned to any measurement — just a
    /// reasonable balance between a live-feeling queue row and not spamming <c>DownloadQueueService</c>'s
    /// <c>ItemChanged</c> event (and the UI thread dispatch each of those triggers) needlessly often.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long <see cref="DownloadAsync"/> waits for a magnet link's metadata to resolve before
    /// giving up — found necessary as a real bug, not preemptively: a magnet link with no trackers
    /// relies entirely on DHT for peer discovery, and <see cref="TorrentManager.WaitForMetadataAsync(CancellationToken)"/>
    /// had nothing bounding it beyond the caller's own cancellation token (only canceled by an
    /// explicit pause/cancel/app-shutdown), so a torrent that can't find any peers — DHT bootstrap
    /// itself failing or just being slow, common on a network that restricts the UDP traffic DHT
    /// needs (some corporate networks/VPNs) — sat at "Fetching torrent metadata…" forever with no
    /// feedback and no way to know it was ever going to resolve. Reproduced directly in this session:
    /// a real trackerless magnet link stayed at zero DHT nodes and zero peers for 100+ seconds
    /// straight against the real network. Two minutes is deliberately generous — DHT bootstrap plus
    /// an initial peer lookup normally resolves in well under a minute when it's going to work at
    /// all — while still bounding the wait to something a user isn't left staring at indefinitely.
    /// </summary>
    private static readonly TimeSpan MetadataResolutionTimeout = TimeSpan.FromMinutes(2);

    private readonly ClientEngine _engine;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public TorrentEngine(string cacheDirectory) : this(cacheDirectory, new HttpClient())
    {
        _ownsHttpClient = true;
    }

    public TorrentEngine(string cacheDirectory, HttpClient httpClient)
    {
        Directory.CreateDirectory(cacheDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory
        }.ToSettings();

        _engine = new ClientEngine(settings);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Reads a magnet link's own <c>dn=</c> (display name) parameter, if it has one — available
    /// immediately from the URI text alone, no network round trip needed, unlike the torrent's real
    /// file list/size (only known once metadata actually resolves — see
    /// <see cref="TorrentDownloadProgress.PhaseText"/>). <c>Services.DownloadQueueService</c> uses this
    /// as a provisional title/destination-folder name for a magnet-sourced item the moment it's
    /// enqueued, rather than leaving it blank until the download actually starts. Most real-world
    /// magnet links do carry <c>dn=</c>; a caller should fall back to something else (a short infohash,
    /// say) for the rare one that doesn't.
    /// </summary>
    public static string? TryGetMagnetDisplayName(string magnetUri) =>
        MagnetLink.TryParse(magnetUri, out var magnet) ? magnet.Name : null;

    /// <summary>
    /// Static, and takes its own short-lived <see cref="HttpClient"/> when none is supplied — parsing
    /// a <c>.torrent</c> file's contents needs no <see cref="ClientEngine"/> at all (no listen port,
    /// no DHT node, nothing this class' one shared engine actually provides), so
    /// <c>Views.AddDownloadDialog</c>'s resolve-then-confirm step calls this directly rather than
    /// standing up a whole second <see cref="TorrentEngine"/> just to resolve a name/size — the same
    /// "just for this probe" reasoning <c>DownloadEngine.ProbeAsync</c> already has, just via a plain
    /// static method instead of a disposable one-off instance since there's no connection pooling
    /// worth keeping around for a single small download. <see cref="DownloadAsync"/> below calls this
    /// same method (passing its own instance <see cref="_httpClient"/> along) for the identical
    /// resolve step it needs before it can add the torrent to <see cref="_engine"/>.
    /// </summary>
    public static async Task<Torrent> LoadTorrentAsync(string source, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var ownedClient = httpClient is null ? new HttpClient() : null;
            try
            {
                var bytes = await (httpClient ?? ownedClient!).GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
                return Torrent.Load(bytes);
            }
            finally
            {
                ownedClient?.Dispose();
            }
        }

        return await Torrent.LoadAsync(source).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one torrent's full content into <paramref name="saveDirectory"/> — a directory this
    /// caller already owns (reserved uniquely by <c>DownloadQueueService.ReserveUniqueDestinationPathAsync</c>
    /// the same way a video/file destination path is), not a filename: MonoTorrent lays out a
    /// single-file torrent's one file directly inside it and a multi-file torrent's whole tree inside
    /// it (via <see cref="TorrentSettingsBuilder.CreateContainingDirectory"/> set <c>false</c> below,
    /// since the caller's directory already <i>is</i> the per-download container — leaving the library
    /// default of <c>true</c> would nest an extra same-named subfolder inside it for a multi-file
    /// torrent).
    ///
    /// Throws <see cref="OperationCanceledException"/> promptly on <paramref name="cancellationToken"/>,
    /// same contract as <c>YtDlpClient.DownloadAsync</c>/<c>DownloadEngine.DownloadAsync</c> — a
    /// paused/canceled item's cancellation flows through this exactly the same way theirs do. Throws
    /// <see cref="TimeoutException"/> instead if metadata never resolves within
    /// <paramref name="metadataResolutionTimeout"/> — see <see cref="MetadataResolutionTimeout"/>'s
    /// own doc comment for why that's needed at all.
    /// </summary>
    /// <param name="metadataResolutionTimeout">
    /// Defaults to <see cref="MetadataResolutionTimeout"/> when null — overridable only so
    /// Yoink.Tests can exercise the timeout path deterministically and quickly, the same reason
    /// <c>YtDlpClient.DownloadAsync</c>'s own <c>stallTimeout</c> parameter exists.
    /// </param>
    public async Task DownloadAsync(
        string source,
        string saveDirectory,
        int? rateLimitKBps,
        IProgress<TorrentDownloadProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? metadataResolutionTimeout = null)
    {
        Directory.CreateDirectory(saveDirectory);

        var torrentSettings = new TorrentSettingsBuilder
        {
            CreateContainingDirectory = false,
            MaximumDownloadRate = rateLimitKBps is > 0 ? rateLimitKBps.Value * 1024 : 0
        }.ToSettings();

        var manager = DownloadUrlKind.IsMagnetLink(source)
            ? await _engine.AddAsync(MagnetLink.Parse(source), saveDirectory, torrentSettings).ConfigureAwait(false)
            : await _engine.AddAsync(await LoadTorrentAsync(source, _httpClient, cancellationToken).ConfigureAwait(false), saveDirectory, torrentSettings).ConfigureAwait(false);

        var completedNaturally = false;
        try
        {
            await manager.StartAsync().ConfigureAwait(false);

            if (!manager.HasMetadata)
            {
                progress?.Report(new TorrentDownloadProgress(0, 0, null, null, null, "Fetching torrent metadata…", null));
                await WaitForMetadataWithTimeoutAsync(
                    manager, metadataResolutionTimeout ?? MetadataResolutionTimeout, cancellationToken).ConfigureAwait(false);
            }

            while (!manager.Complete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var phase = manager.State switch
                {
                    TorrentState.Hashing or TorrentState.HashingPaused => "Checking existing files…",
                    TorrentState.Starting or TorrentState.FetchingHashes or TorrentState.Metadata => "Starting…",
                    _ => null
                };

                var totalBytes = manager.Torrent?.Size;
                var downloadedBytes = totalBytes is > 0 ? (long)(totalBytes.Value * manager.Progress / 100.0) : 0;

                progress?.Report(new TorrentDownloadProgress(
                    manager.Progress / 100.0, downloadedBytes, totalBytes, manager.Peers.Seeds, manager.Peers.Leechs, phase, manager.Torrent?.Name));

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            var finalSize = manager.Torrent!.Size;
            progress?.Report(new TorrentDownloadProgress(1.0, finalSize, finalSize, manager.Peers.Seeds, manager.Peers.Leechs, null, manager.Torrent.Name));
            completedNaturally = true;
        }
        finally
        {
            // Best-effort, deliberately swallowed: this is teardown after the outcome (success,
            // cancellation, or a real failure) is already decided above, and a hiccup here
            // shouldn't turn an otherwise-successful, fully-written-to-disk download into one that
            // reports as Failed — same reasoning as this codebase's other best-effort cleanup paths
            // (YtDlpClient's process kill, DownloadQueueService.DeleteAsync's file deletion).
            try
            {
                await manager.StopAsync().ConfigureAwait(false);
                await _engine.RemoveAsync(manager, completedNaturally ? RemoveMode.CacheDataOnly : RemoveMode.KeepAllData).ConfigureAwait(false);
            }
            catch
            {
                // See above.
            }
        }
    }

    /// <summary>
    /// Bounds <see cref="TorrentManager.WaitForMetadataAsync(CancellationToken)"/> to
    /// <see cref="MetadataResolutionTimeout"/> — see that constant's own doc comment for why. Careful
    /// to distinguish the timeout from a genuine outer cancellation (pause/cancel/app shutdown):
    /// checking <paramref name="cancellationToken"/> itself, not the linked token that actually fired,
    /// is what tells the two apart, so a real pause/cancel still surfaces as
    /// <see cref="OperationCanceledException"/> (handled the same way every other download's
    /// cancellation already is) rather than being mislabeled a metadata timeout.
    /// </summary>
    private static async Task WaitForMetadataWithTimeoutAsync(TorrentManager manager, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await manager.WaitForMetadataAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Couldn't find this torrent's metadata after {timeout.TotalSeconds:0} seconds — no peers " +
                "responded. This is common for a magnet link with no trackers on a network that restricts DHT (the UDP " +
                "traffic peer discovery needs, blocked by some corporate networks/VPNs) — try a magnet link or .torrent " +
                "file that includes trackers, or check your network/firewall settings.");
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
