using System;
using System.IO;
using Yoink.Models;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Redirects the static <see cref="SettingsService.SettingsPath"/> to a temp file for the duration of
/// each test — never the real user's actual ~/.config/Yoink/settings.json — and restores it
/// afterward. Safe only because the whole assembly runs sequentially (see AssemblyInfo.cs); two tests
/// swapping this shared static path at once would corrupt each other.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _originalPath = SettingsService.SettingsPath;
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"yoink-tests-{Guid.NewGuid():N}", "settings.json");

    public SettingsServiceTests()
    {
        SettingsService.SettingsPath = _tempPath;
    }

    public void Dispose()
    {
        SettingsService.SettingsPath = _originalPath;

        var directory = Path.GetDirectoryName(_tempPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var settings = SettingsService.Load();

        Assert.Equal(new AppSettings().AccentColor, settings.AccentColor);
        Assert.Equal(new AppSettings().Theme, settings.Theme);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var original = new AppSettings
        {
            Theme = ThemePreference.Dark,
            AccentColor = AccentColor.Purple,
            ClipboardWatchEnabled = false,
            MinimizeToTrayOnClose = true,
            MaxConcurrentDownloads = 4,
            PerDownloadSpeedLimitKBps = 512,
            GlobalSpeedLimitKBps = 2048,
            SchedulingEnabled = true,
            ScheduleStart = new TimeOnly(23, 30),
            ScheduleEnd = new TimeOnly(7, 15),
            LastUpdateCheckUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
        };

        SettingsService.Save(original);
        var loaded = SettingsService.Load();

        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.AccentColor, loaded.AccentColor);
        Assert.Equal(original.ClipboardWatchEnabled, loaded.ClipboardWatchEnabled);
        Assert.Equal(original.MinimizeToTrayOnClose, loaded.MinimizeToTrayOnClose);
        Assert.Equal(original.MaxConcurrentDownloads, loaded.MaxConcurrentDownloads);
        Assert.Equal(original.PerDownloadSpeedLimitKBps, loaded.PerDownloadSpeedLimitKBps);
        Assert.Equal(original.GlobalSpeedLimitKBps, loaded.GlobalSpeedLimitKBps);
        Assert.Equal(original.SchedulingEnabled, loaded.SchedulingEnabled);
        Assert.Equal(original.ScheduleStart, loaded.ScheduleStart);
        Assert.Equal(original.ScheduleEnd, loaded.ScheduleEnd);
        Assert.Equal(original.LastUpdateCheckUtc, loaded.LastUpdateCheckUtc);
    }

    [Fact]
    public void Load_FallsBackToDefaults_WhenFileIsCorruptJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tempPath)!);
        File.WriteAllText(_tempPath, "{ not valid json at all");

        var settings = SettingsService.Load();

        Assert.Equal(new AppSettings().AccentColor, settings.AccentColor);
    }

    [Fact]
    public void Save_CreatesTheConfigDirectory_WhenItDoesNotExistYet()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_tempPath)));

        SettingsService.Save(new AppSettings());

        Assert.True(File.Exists(_tempPath));
    }
}
