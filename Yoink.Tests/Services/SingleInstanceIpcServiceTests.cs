using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Mostly the pure JSON-shape (de)serialization <see cref="SingleInstanceIpcService"/> splits out for
/// this reason, plus one real end-to-end round trip (below) against the actual OS-level named pipe
/// (a genuine Unix domain socket file under /tmp on Linux, confirmed for real in the session that
/// added this) — same "real behavior, not a mock" standard as <c>DownloadEngineTests</c>' own local
/// HTTP server. This doesn't cover the two-*process* handoff <c>Program.cs</c> actually does (a
/// second real launch finding the pipe and forwarding a URL to a separately-running Yoink) — that was
/// verified manually, once, in the session that added this feature.
/// </summary>
public class SingleInstanceIpcServiceTests
{
    [Fact]
    public async Task StartServer_ThenTrySendAsync_DeliversUrlToServerCallback()
    {
        var pipeName = "Yoink-Ipc-Test-" + Guid.NewGuid();
        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        SingleInstanceIpcService.StartServer(
            url =>
            {
                received.Add(url);
                return Task.CompletedTask;
            },
            cts.Token,
            pipeName);

        var ok = await SingleInstanceIpcService.TrySendAsync("https://youtu.be/dQw4w9WgXcQ", timeoutMs: 5000, pipeName);

        Assert.True(ok);
        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ"], received);

        cts.Cancel();
    }

    [Fact]
    public async Task TrySendAsync_ReturnsFalse_WhenNoServerIsListening()
    {
        // A pipe name unique to this test that nothing has ever bound — connecting should simply
        // time out, the exact signal callers rely on to decide "Yoink isn't running, cold-start it."
        var pipeName = "Yoink-Ipc-Test-" + Guid.NewGuid();

        var ok = await SingleInstanceIpcService.TrySendAsync("https://example.com/should-not-connect", timeoutMs: 300, pipeName);

        Assert.False(ok);
    }

    [Fact]
    public void SerializeRequest_ThenTryDeserializeRequest_RoundTrips()
    {
        var request = new IpcRequest("https://youtu.be/dQw4w9WgXcQ");

        var json = SingleInstanceIpcService.SerializeRequest(request);
        var roundTripped = SingleInstanceIpcService.TryDeserializeRequest(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(request.Url, roundTripped.Url);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "no url")]
    public void SerializeResponse_ThenTryDeserializeResponse_RoundTrips(bool ok, string? error)
    {
        var response = new IpcResponse(ok, error);

        var json = SingleInstanceIpcService.SerializeResponse(response);
        var roundTripped = SingleInstanceIpcService.TryDeserializeResponse(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(response.Ok, roundTripped.Ok);
        Assert.Equal(response.Error, roundTripped.Error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    public void TryDeserializeRequest_ReturnsNullOrEmptyUrl_ForGarbageOrMissingField(string json)
    {
        var result = SingleInstanceIpcService.TryDeserializeRequest(json);

        Assert.True(result is null || string.IsNullOrEmpty(result.Url));
    }

    [Fact]
    public void PipeName_IncludesCurrentUserName()
    {
        Assert.Contains(System.Environment.UserName, SingleInstanceIpcService.PipeName);
    }
}
