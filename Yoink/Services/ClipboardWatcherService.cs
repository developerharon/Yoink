using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

/// <summary>
/// The clipboard-monitoring half of the "auto-catch" mechanism (README roadmap step 5). Polls the
/// clipboard on a timer — Avalonia's clipboard API has no "changed" event, and there's no
/// OS-agnostic native one to hook either, so polling is the standard approach here — and raises
/// <see cref="UrlDetected"/> when the clipboard's text changes to something that looks like a
/// YouTube URL.
///
/// Deliberately does not download anything itself: it only detects and reports. The caller (
/// <c>Views.MainWindow</c>) decides what to do with a detected URL — prompting before queuing
/// anything is the point, so copying a link for an unrelated reason doesn't silently start a
/// download.
///
/// On Linux under Wayland, clipboard access outside your own app's focus can be restricted by the
/// compositor/portal, so detection may be less reliable there than on X11 or Windows while the app
/// is in the background — a known platform limitation, not something this class works around.
/// </summary>
public sealed class ClipboardWatcherService : IDisposable
{
    // Deliberately conservative: only youtube.com/youtu.be watch, shorts, and playlist links.
    // Missing an exotic URL shape is far less annoying than prompting on unrelated copied text.
    private static readonly Regex YouTubeUrlPattern = new(
        @"^https?://(www\.|m\.|music\.)?(youtube\.com/(watch\?v=|playlist\?list=|shorts/)|youtu\.be/)\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<Task<string?>> _readClipboardText;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly Task _pollLoop;
    private string? _lastSeenText;

    public ClipboardWatcherService(Func<Task<string?>> readClipboardText, TimeSpan? pollInterval = null)
    {
        _readClipboardText = readClipboardText;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _pollLoop = Task.Run(() => PollLoopAsync(_stoppingCts.Token));
    }

    /// <summary>Off by default in a fresh instance; the caller sets this from the persisted setting.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Raised (on a background thread — marshal to the UI thread yourself) when the clipboard's
    /// text changes to something matching <see cref="YouTubeUrlPattern"/>.
    /// </summary>
    public event Action<string>? UrlDetected;

    private async Task PollLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    var text = await _readClipboardText().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text) && text != _lastSeenText)
                    {
                        _lastSeenText = text;

                        var trimmed = text.Trim();
                        if (YouTubeUrlPattern.IsMatch(trimmed))
                            UrlDetected?.Invoke(trimmed);
                    }
                }
            }
            catch
            {
                // Best-effort — clipboard access can transiently fail (focus changes, an
                // unfocused Wayland session, another app holding it) and isn't worth surfacing.
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stoppingCts.Cancel();

        try
        {
            _pollLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown.
        }

        _stoppingCts.Dispose();
    }
}
