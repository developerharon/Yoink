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
        Assert.Null(settings.PerDownloadSpeedLimitKBps);
        Assert.Null(settings.GlobalSpeedLimitKBps);
        Assert.False(settings.SchedulingEnabled);
        Assert.Equal(new TimeOnly(22, 0), settings.ScheduleStart);
        Assert.Equal(new TimeOnly(6, 0), settings.ScheduleEnd);
        Assert.Null(settings.LastUpdateCheckUtc);
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
