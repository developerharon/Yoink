using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Just the pure/isolable pieces of <see cref="NativeMessagingHost"/> — Chrome's own 4-byte
/// length-prefix stdio framing, and pulling the "url" field out of a decoded message. Everything
/// else on that class (<c>Run</c>, <c>EnsureManifestRegistered</c>) talks to real stdin/stdout, spawns
/// a process, or writes to the real filesystem, so it isn't exercised here — same reasoning as
/// <see cref="DependencyProvisioningServiceTests"/> only covering that class's own pure parsing.
/// </summary>
public class NativeMessagingHostTests
{
    [Theory]
    [InlineData("""{"url":"https://youtu.be/dQw4w9WgXcQ"}""")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    public void EncodeMessage_ThenTryReadMessage_RoundTrips(string json)
    {
        var encoded = NativeMessagingHost.EncodeMessage(json);
        using var stream = new MemoryStream(encoded);

        var decoded = NativeMessagingHost.TryReadMessage(stream);

        Assert.Equal(json, decoded);
    }

    [Fact]
    public void TryReadMessage_ReturnsNull_OnEmptyStream()
    {
        using var stream = new MemoryStream();

        Assert.Null(NativeMessagingHost.TryReadMessage(stream));
    }

    [Fact]
    public void TryReadMessage_ReturnsNull_WhenLengthPrefixExceedsAvailableBytes()
    {
        // A length prefix claiming far more bytes than actually follow — a truncated/corrupt stream.
        var buffer = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }; // huge length, zero body bytes
        using var stream = new MemoryStream(buffer);

        Assert.Null(NativeMessagingHost.TryReadMessage(stream));
    }

    [Fact]
    public void TryReadMessage_ReturnsNull_ForNegativeLengthPrefix()
    {
        var buffer = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // -1 as a little-endian Int32
        using var stream = new MemoryStream(buffer);

        Assert.Null(NativeMessagingHost.TryReadMessage(stream));
    }

    [Fact]
    public void EncodeMessage_WritesFourByteLittleEndianLengthPrefix()
    {
        var json = "abc";
        var encoded = NativeMessagingHost.EncodeMessage(json);

        Assert.Equal(4 + Encoding.UTF8.GetByteCount(json), encoded.Length);
        Assert.Equal(3, encoded[0]);
        Assert.Equal(0, encoded[1]);
        Assert.Equal(0, encoded[2]);
        Assert.Equal(0, encoded[3]);
    }

    [Fact]
    public void ParseUrlFromMessage_ExtractsUrlField()
    {
        var url = NativeMessagingHost.ParseUrlFromMessage("""{"url":"https://example.com/file.pdf"}""");

        Assert.Equal("https://example.com/file.pdf", url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"url": 5}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseUrlFromMessage_ReturnsNull_WhenNoStringUrlField(string json)
    {
        Assert.Null(NativeMessagingHost.ParseUrlFromMessage(json));
    }

    /// <summary>
    /// Redirects <see cref="NativeMessagingHost.HomeDirectory"/> to an isolated temp directory —
    /// never the real developer's actual ~/.config/google-chrome — same convention as
    /// <c>SettingsServiceTests</c> redirecting <see cref="SettingsService.SettingsPath"/>. This is
    /// exactly the gap that let an earlier version of this feature silently write a stale manifest
    /// into a real ~/.config/google-chrome while a headless MainWindow test ran (see
    /// MainWindowNavigationTests' own redirect for the other half of that fix).
    /// </summary>
    [Fact]
    public void EnsureManifestRegistered_WritesValidManifestToBothBrowserConfigDirs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"yoink-native-messaging-test-{Guid.NewGuid():N}");
        var originalHomeDirectory = NativeMessagingHost.HomeDirectory;
        NativeMessagingHost.HomeDirectory = tempDir;

        try
        {
            NativeMessagingHost.EnsureManifestRegistered();

            foreach (var browserDir in new[] { "google-chrome", "chromium" })
            {
                var manifestPath = Path.Combine(tempDir, ".config", browserDir, "NativeMessagingHosts", $"{NativeMessagingHost.HostName}.json");
                Assert.True(File.Exists(manifestPath));

                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                Assert.Equal(NativeMessagingHost.HostName, doc.RootElement.GetProperty("name").GetString());
                Assert.Equal("stdio", doc.RootElement.GetProperty("type").GetString());
                Assert.Equal(Environment.ProcessPath, doc.RootElement.GetProperty("path").GetString());
                Assert.StartsWith("chrome-extension://", doc.RootElement.GetProperty("allowed_origins")[0].GetString());
            }
        }
        finally
        {
            NativeMessagingHost.HomeDirectory = originalHomeDirectory;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
