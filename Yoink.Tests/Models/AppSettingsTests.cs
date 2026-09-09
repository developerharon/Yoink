using System;
using Yoink.Models;

namespace Yoink.Tests.Models;

/// <summary>
/// Pins down the defaults a brand-new install ships with — see each property's own doc comment in
/// AppSettings.cs for *why* each one defaults the way it does; this just makes sure nobody flips one
/// by accident.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchWhatANewInstallShouldShipWith()
    {
        var settings = new AppSettings();

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(AccentColor.Blue, settings.AccentColor);
        Assert.True(settings.ClipboardWatchEnabled);
        Assert.False(settings.MinimizeToTrayOnClose);
        Assert.Equal(1, settings.MaxConcurrentDownloads);
        Assert.Equal(4, settings.MaxConnectionsPerDownload);
        Assert.Null(settings.PerDownloadSpeedLimitKBps);
        Assert.Null(settings.GlobalSpeedLimitKBps);
        Assert.False(settings.SchedulingEnabled);
        Assert.Equal(new TimeOnly(22, 0), settings.ScheduleStart);
        Assert.Equal(new TimeOnly(6, 0), settings.ScheduleEnd);
        Assert.Null(settings.LastUpdateCheckUtc);
        Assert.Equal(AppSettings.DefaultExtraTorrentTrackers, settings.ExtraTorrentTrackers);
    }

    /// <summary>
    /// TorrentEngine.AddExtraTrackersAsync parses each configured entry with a bare <c>new Uri(...)</c>
    /// and silently swallows (best-effort, per entry) anything that doesn't parse — so a malformed
    /// default here wouldn't throw or fail loudly anywhere, just quietly never actually get announced
    /// to. This is the regression guard: every shipped default must genuinely be a valid absolute URI.
    /// </summary>
    [Fact]
    public void DefaultExtraTorrentTrackers_AreAllValidAbsoluteUris()
    {
        foreach (var tracker in AppSettings.DefaultExtraTorrentTrackers)
            Assert.True(Uri.TryCreate(tracker, UriKind.Absolute, out _), $"'{tracker}' is not a valid absolute URI");
    }

    [Fact]
    public void DefaultExtraTorrentTrackers_IsNotEmpty()
    {
        // The whole point is giving a fresh install several independent chances to resolve torrent
        // metadata quickly rather than relying solely on a cold DHT walk — an empty default would
        // silently defeat that for every new user, not just one who explicitly cleared the list.
        Assert.NotEmpty(AppSettings.DefaultExtraTorrentTrackers);
    }

    [Fact]
    public void AccentColor_HasExactlyTheFivePresetsFromBranding()
    {
        var values = Enum.GetValues<AccentColor>();

        Assert.Equal(
            new[] { AccentColor.Blue, AccentColor.Orange, AccentColor.Purple, AccentColor.Green, AccentColor.Red },
            values);
    }
}
