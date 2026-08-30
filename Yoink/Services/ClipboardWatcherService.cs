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
    private readonly Func<bool> _isEnabled;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly Task _pollLoop;
    private string? _lastSeenText;

    /// <param name="readClipboardText">Reads the current clipboard text (or null if there isn't any).</param>
    /// <param name="isEnabled">
    /// Checked at the start of every poll — the caller's own source of truth for whether watching
    /// should currently be active (typically <c>AppSettings.ClipboardWatchEnabled</c>, re-read fresh
    /// each time rather than pushed in) — so a setting change takes effect within one poll interval
    /// without this class needing a settable property or knowing anything about settings itself.
    /// </param>
    public ClipboardWatcherService(Func<Task<string?>> readClipboardText, Func<bool> isEnabled, TimeSpan? pollInterval = null)
    {
        _readClipboardText = readClipboardText;
        _isEnabled = isEnabled;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _pollLoop = Task.Run(() => PollLoopAsync(_stoppingCts.Token));
    }

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
                if (_isEnabled())
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
