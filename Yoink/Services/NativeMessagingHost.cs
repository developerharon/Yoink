using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yoink.Services;

/// <summary>
/// The response handed back to Chrome, which <c>chrome-extension/background.js</c> reads as plain
/// JS object fields (<c>response.status</c>/<c>response.message</c>) — lowercase to match that side
/// of the bridge, confirmed by an actual round trip against this class's own <see cref="Run"/>
/// rather than assumed, hence the explicit <see cref="JsonPropertyNameAttribute"/>s overriding this
/// project's usual PascalCase-property convention (same reasoning as <see cref="NativeHostManifest"/>
/// below, whose field names Chrome itself dictates instead).
/// </summary>
internal sealed record NativeHostResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>Source-generated context for <see cref="NativeHostResponse"/> — see <c>IpcMessageJsonContext</c>'s doc comment for why (trim-safety, no reflection).</summary>
[JsonSerializable(typeof(NativeHostResponse))]
internal sealed partial class NativeHostResponseJsonContext : JsonSerializerContext;

/// <summary>
/// The JSON shape Chrome itself requires for a native-messaging host manifest — field names are
/// fixed by Chrome, hence the explicit snake_case <see cref="JsonPropertyNameAttribute"/>s rather
/// than this project's usual PascalCase-property convention.
/// </summary>
internal sealed record NativeHostManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

[JsonSerializable(typeof(NativeHostManifest))]
internal sealed partial class NativeHostManifestJsonContext : JsonSerializerContext;

/// <summary>
/// Chrome's native-messaging host mode — the same Yoink executable, launched by Chrome itself
/// (<c>Program.cs</c> branches into <see cref="Run"/> when started with <c>--native-messaging-host</c>,
/// which is what the JSON manifest Chrome reads points its <c>path</c> at) rather than a second
/// binary. Chrome speaks one request per process invocation over stdin/stdout using its own framing:
/// a 4-byte length prefix (native/little-endian byte order on every realistic deployment target) then
/// that many bytes of UTF-8 JSON, the same framing expected right back on stdout for the response —
/// verify this against a real Chrome round trip before trusting it further, per this repo's own
/// "confirmed, not assumed" standard for anything a third party's docs describe.
///
/// On receiving a URL, this either forwards it to an already-running Yoink over
/// <see cref="SingleInstanceIpcService"/> (fast-fail — a long-running instance's pipe server has
/// been up for a while by the time a context-menu click fires, so a quick timeout here reliably
/// means "not running" rather than "slow to answer"), or cold-starts a fresh Yoink process with the
/// URL as a plain <c>--url=</c> argument when nothing answers.
/// </summary>
internal static class NativeMessagingHost
{
    /// <summary>
    /// Chrome native-messaging host names must be lowercase letters/digits/underscores/dots — this
    /// is both the manifest file's own base name and its <c>name</c> field, and what
    /// <c>chrome-extension/background.js</c> passes to <c>chrome.runtime.sendNativeMessage</c>.
    /// </summary>
    internal const string HostName = "com.developerharon.yoink";

    /// <summary>
    /// The extension ID Chrome deterministically derives from the public key pinned in
    /// <c>chrome-extension/manifest.json</c>'s own <c>"key"</c> field (SHA-256 of the DER-encoded
    /// public key, first 16 bytes, each nibble mapped to a-p) — computed once when that key was
    /// generated and hardcoded here since it can never change without also changing that key.
    /// Re-verify both together (load the actual unpacked extension in chrome://extensions and
    /// confirm its displayed ID matches) before changing either — see the plan's own "verify for
    /// real" note.
    /// </summary>
    private const string AllowedExtensionId = "fploffppjmjodgebmdmebocepnkoiefg";

    // Comfortably above any real URL, comfortably below Chrome's own documented 1MB per-message cap
    // — just a sanity bound against a corrupt length prefix, not a meaningful limit in practice.
    private const int MaxMessageBytes = 1024 * 1024;

    internal static byte[] EncodeMessage(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    /// <summary>Null on a clean EOF (Chrome closed the pipe without sending anything) or a corrupt/oversized length prefix.</summary>
    internal static string? TryReadMessage(Stream input)
    {
        Span<byte> lengthBuffer = stackalloc byte[4];
        try
        {
            input.ReadExactly(lengthBuffer);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > MaxMessageBytes)
            return null;

        var body = new byte[length];
        try
        {
            input.ReadExactly(body);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Pulls the "url" string field out of a <c>{"url": "..."}</c> message. <see cref="JsonDocument"/> parses without reflection, so this stays trim-safe with no source-generated context needed for a read-only, single-field extraction.</summary>
    internal static string? ParseUrlFromMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Run()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        NativeHostResponse response;
        try
        {
            var message = TryReadMessage(stdin);
            var url = message is null ? null : ParseUrlFromMessage(message);

            if (string.IsNullOrEmpty(url))
            {
                response = new NativeHostResponse("error", "no url");
            }
            else if (SingleInstanceIpcService.TrySendAsync(url, timeoutMs: 500).GetAwaiter().GetResult())
            {
                response = new NativeHostResponse("forwarded");
            }
            else
            {
                var exePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not determine Yoink's own executable path.");

                // RedirectStandard{Input,Output,Error} — deliberately not read from, just set — so
                // the cold-started full app gets its own fresh, disconnected pipes instead of
                // inheriting this process's actual stdin/stdout. Chrome talks to *this* process over
                // exactly that stdio (the same framing Run() itself reads/writes above); without
                // this, the long-lived GUI app would hold that pipe's write end open indefinitely
                // even after this host process exits and writes its own response below, the same
                // "lingering child keeps the pipe open" hang YtDlpClient.DownloadAsync's own doc
                // comment describes — reproduced for real in this session (a python3 reader on the
                // other end of that same pipe blocked forever) before this fix.
                Process.Start(new ProcessStartInfo(exePath, $"--url={url}")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                response = new NativeHostResponse("launched");
            }
        }
        catch (Exception ex)
        {
            response = new NativeHostResponse("error", ex.Message);
        }

        var json = JsonSerializer.Serialize(response, NativeHostResponseJsonContext.Default.NativeHostResponse);
        var encoded = EncodeMessage(json);
        stdout.Write(encoded);
        stdout.Flush();
    }

    /// <summary>
    /// Internal (not private) and mutable, purely so <c>Yoink.Tests</c>' real, headless
    /// <c>MainWindow</c> constructions (see <c>MainWindowNavigationTests</c>) can redirect this to an
    /// isolated temp directory for the test's duration instead of ever writing into a real
    /// developer's actual <c>~/.config/google-chrome</c>/<c>~/.config/chromium</c> — same convention
    /// as <see cref="SettingsService.SettingsPath"/>. Nothing in the app itself ever reassigns this
    /// outside of tests.
    /// </summary>
    internal static string HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Writes the native-messaging host manifest to every browser config directory Chrome/Chromium
    /// scans on Linux, pointed at this exact running executable via <see cref="Environment.ProcessPath"/>
    /// so it self-corrects across reinstalls/updates without hardcoding an install path. Called from
    /// <c>Views.MainWindow</c>, gated by <see cref="Models.AppSettings.NativeMessagingHostRegistered"/>
    /// so it only runs once — safe to call again regardless (idempotent, just rewrites the same file).
    /// Windows/macOS have their own manifest locations (a registry value, and a different directory,
    /// respectively) that aren't wired up yet — Linux only, matching this whole feature's Ubuntu-first
    /// scope for now.
    /// </summary>
    public static void EnsureManifestRegistered()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
            return;

        var manifest = new NativeHostManifest(
            HostName,
            "Yoink native messaging host",
            exePath,
            "stdio",
            [$"chrome-extension://{AllowedExtensionId}/"]);

        var json = JsonSerializer.Serialize(manifest, NativeHostManifestJsonContext.Default.NativeHostManifest);

        string[] targetDirs =
        [
            System.IO.Path.Combine(HomeDirectory, ".config", "google-chrome", "NativeMessagingHosts"),
            System.IO.Path.Combine(HomeDirectory, ".config", "chromium", "NativeMessagingHosts"),
        ];

        foreach (var dir in targetDirs)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, $"{HostName}.json"), json);
        }
    }
}
