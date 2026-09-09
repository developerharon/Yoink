using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Yoink.Services;

internal sealed record IpcRequest(string Url);

internal sealed record IpcResponse(bool Ok, string? Error);

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the two tiny IPC message shapes below —
/// same reflection-free, trim-safe reasoning as <c>AppSettingsJsonContext</c> (see that class's own
/// doc comment); a bare <c>JsonSerializer.Deserialize&lt;T&gt;</c> here would be exactly the kind of
/// call this project's <c>PublishTrimmed=true</c> publish has already been burned by once.
/// </summary>
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
internal sealed partial class IpcMessageJsonContext : JsonSerializerContext;

/// <summary>
/// A local, per-user IPC channel that lets a second Yoink launch (or the Chrome native-messaging
/// host, see <see cref="NativeMessagingHost"/>) hand a URL to the already-running primary instance
/// instead of starting a second independent window/tray-icon/queue.db writer. Built on
/// <see cref="NamedPipeServerStream"/>/<see cref="NamedPipeClientStream"/> — cross-platform in .NET,
/// backed by a Unix domain socket on Linux/macOS — carrying one line-delimited JSON request/response
/// per connection, a deliberately simpler protocol than Chrome's own length-prefixed framing (that
/// framing is only forced on the native-messaging side of the bridge, by Chrome itself).
/// </summary>
internal static class SingleInstanceIpcService
{
    /// <summary>
    /// Salted with the current user name (not a bare fixed string) so a shared multi-user machine
    /// can't have one user's Yoink launch collide with another's pipe of the same name.
    /// </summary>
    internal static string PipeName => "Yoink-Ipc-" + Environment.UserName;

    /// <summary>
    /// Starts the server loop as a background task and returns immediately. One
    /// <see cref="NamedPipeServerStream"/> is created per incoming connection (rather than one
    /// long-lived duplex stream) so a single malformed/abruptly-closed client can never wedge every
    /// future connection. Runs until <paramref name="cancellationToken"/> is canceled.
    /// <paramref name="pipeName"/> mirrors <see cref="Services.DownloadQueueService"/>'s own
    /// constructor overrides — null (every real caller) means <see cref="PipeName"/>; Yoink.Tests
    /// passes a distinct name per test so tests that each start their own real server can't collide.
    /// </summary>
    public static void StartServer(Func<string, Task> onUrlReceived, CancellationToken cancellationToken, string? pipeName = null)
    {
        _ = Task.Run(() => ServerLoopAsync(onUrlReceived, pipeName ?? PipeName, cancellationToken), cancellationToken);
    }

    private static async Task ServerLoopAsync(Func<string, Task> onUrlReceived, string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, onUrlReceived).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A dropped/malformed connection on one attempt shouldn't take down the loop —
                // the next iteration just opens a fresh server stream and waits again.
            }
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream server, Func<string, Task> onUrlReceived)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        var request = line is null ? null : TryDeserializeRequest(line);

        if (request is not { Url.Length: > 0 })
        {
            await writer.WriteLineAsync(SerializeResponse(new IpcResponse(false, "no url"))).ConfigureAwait(false);
            return;
        }

        try
        {
            await onUrlReceived(request.Url).ConfigureAwait(false);
            await writer.WriteLineAsync(SerializeResponse(new IpcResponse(true, null))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(SerializeResponse(new IpcResponse(false, ex.Message))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects to the primary instance's pipe and hands it a URL, returning whether it was
    /// accepted. False (never throws) whenever nothing is listening at all — that's the normal,
    /// expected "Yoink isn't running" signal callers use to decide whether to cold-start it instead.
    /// <paramref name="pipeName"/> — see <see cref="StartServer"/>'s own doc comment.
    /// </summary>
    public static async Task<bool> TrySendAsync(string url, int timeoutMs, string? pipeName = null)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectCts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(SerializeRequest(new IpcRequest(url))).ConfigureAwait(false);

            using var readCts = new CancellationTokenSource(timeoutMs);
            var responseLine = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            var response = responseLine is null ? null : TryDeserializeResponse(responseLine);
            return response?.Ok ?? false;
        }
        catch
        {
            return false;
        }
    }

    // The four methods below are pure JSON-shape (de)serialization with no I/O — split out
    // specifically so Yoink.Tests can round-trip them directly without standing up a real pipe.
    internal static string SerializeRequest(IpcRequest request) =>
        JsonSerializer.Serialize(request, IpcMessageJsonContext.Default.IpcRequest);

    internal static IpcRequest? TryDeserializeRequest(string json)
    {
        try { return JsonSerializer.Deserialize(json, IpcMessageJsonContext.Default.IpcRequest); }
        catch (JsonException) { return null; }
    }

    internal static string SerializeResponse(IpcResponse response) =>
        JsonSerializer.Serialize(response, IpcMessageJsonContext.Default.IpcResponse);

    internal static IpcResponse? TryDeserializeResponse(string json)
    {
        try { return JsonSerializer.Deserialize(json, IpcMessageJsonContext.Default.IpcResponse); }
        catch (JsonException) { return null; }
    }
}
